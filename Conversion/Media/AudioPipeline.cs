using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NVorbis;
using OggVorbisEncoder;

namespace ConvertWheel.Conversion.Media;

// Audio flows as interleaved 32-bit float samples: a source decodes, optional processors change the
// channel count or sample rate, and a sink encodes.

internal readonly record struct AudioFormat(int SampleRate, int Channels);

internal interface IAudioSource : IDisposable
{
    AudioFormat Format { get; }
    /// <summary>Bit depth of the original audio, so lossless outputs can keep 24-bit sources at 24 bits.</summary>
    int SourceBits { get; }
    long? TotalFrames { get; }
    /// <summary>Fills <paramref name="buffer"/> with whole frames; returns samples written, 0 at the end.</summary>
    int Read(float[] buffer, int count);
}

internal interface IAudioSink : IDisposable
{
    void Write(float[] samples, int count);
    void Finish();
}

// ---------------------------------------------------------------------- sources

/// <summary>Decodes anything Windows can play (MP3, AAC, WMA, FLAC, ALAC, WAV, and the audio of videos).</summary>
internal sealed class MfAudioSource : IAudioSource
{
    readonly IMFSourceReader _reader;
    float[] _pending = [];
    int _pendingOffset;
    bool _ended;

    public AudioFormat Format { get; }
    public int SourceBits { get; }
    public long? TotalFrames { get; }
    public Guid NativeSubtype { get; }

    public MfAudioSource(string path)
    {
        MF.EnsureStarted();
        MF.MFCreateSourceReaderFromURL(path, null, out _reader);
        try
        {
            _reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
            _reader.SetStreamSelection(MF.SourceReaderFirstAudio, true);
        }
        catch (COMException)
        {
            throw new NotSupportedException("This file has no audio track.");
        }
        if (_reader.GetNativeMediaType(MF.SourceReaderFirstAudio, 0, out var native) < 0)
            throw new NotSupportedException("This file has no audio track.");
        NativeSubtype = native.GetGuid(MF.MT_SUBTYPE) ?? Guid.Empty;
        SourceBits = native.GetInt(MF.MT_AUDIO_BITS_PER_SAMPLE) is int b and >= 16 and <= 32 && (NativeSubtype == MF.AudioPCM || NativeSubtype == MF.AudioFLAC) ? b : 16;
        MF.Release(native);

        var want = MF.NewType(MF.MediaTypeAudio, MF.AudioFloat);
        int hr = _reader.SetCurrentMediaType(MF.SourceReaderFirstAudio, IntPtr.Zero, want);
        MF.Release(want);
        if (hr < 0) throw new NotSupportedException("Windows can't decode this audio.");
        _reader.GetCurrentMediaType(MF.SourceReaderFirstAudio, out var current);
        Format = new AudioFormat(current.GetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND) ?? 44100, current.GetInt(MF.MT_AUDIO_NUM_CHANNELS) ?? 2);
        MF.Release(current);
        if (MF.Duration(_reader) is long d) TotalFrames = d * Format.SampleRate / 10_000_000;
    }

    public int Read(float[] buffer, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pendingOffset >= _pending.Length)
            {
                if (_ended || !Refill()) break;
                continue;
            }
            int n = Math.Min(count - written, _pending.Length - _pendingOffset);
            Array.Copy(_pending, _pendingOffset, buffer, written, n);
            _pendingOffset += n;
            written += n;
        }
        return written - written % Format.Channels;
    }

    bool Refill()
    {
        _reader.ReadSample(MF.SourceReaderFirstAudio, 0, out _, out var flags, out _, out var sample);
        if ((flags & MF.ReaderError) != 0) throw new InvalidOperationException("Windows couldn't decode this audio.");
        if ((flags & MF.ReaderEndOfStream) != 0) _ended = true;
        if (sample == null) return !_ended;
        var bytes = MF.Bytes(sample);
        MF.Release(sample);
        _pending = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        _pendingOffset = 0;
        return true;
    }

    public void Dispose() => MF.Release(_reader);
}

/// <summary>Ogg Vorbis through NVorbis (managed, so it works even without Windows' web media extensions).</summary>
internal sealed class VorbisSource(string path) : IAudioSource
{
    readonly VorbisReader _reader = new(path);
    public AudioFormat Format => new(_reader.SampleRate, _reader.Channels);
    public int SourceBits => 16;
    public long? TotalFrames => _reader.TotalSamples > 0 ? _reader.TotalSamples : null;
    public VorbisReader Reader => _reader;
    public int Read(float[] buffer, int count) => _reader.ReadSamples(buffer, 0, count - count % _reader.Channels);
    public void Dispose() => _reader.Dispose();
}

/// <summary>Ogg Opus through Concentus.</summary>
internal sealed class OpusSource : IAudioSource
{
    readonly FileStream _file;
    readonly OpusOggReadStream _reader;
    short[] _pending = [];
    int _offset;

    public AudioFormat Format { get; }
    public int SourceBits => 16;
    public long? TotalFrames { get; }
    public OpusTags? Tags => _reader.Tags;

    public OpusSource(string path, int channels)
    {
        _file = File.OpenRead(path);
        Format = new AudioFormat(48000, Math.Clamp(channels, 1, 2));
        _reader = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(48000, Format.Channels, null), _file);
        if (_reader.TotalTime > TimeSpan.Zero) TotalFrames = (long)(_reader.TotalTime.TotalSeconds * 48000);
    }

    public int Read(float[] buffer, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_offset >= _pending.Length)
            {
                if (!_reader.HasNextPacket) break;
                _pending = _reader.DecodeNextPacket() ?? [];
                _offset = 0;
                continue;
            }
            int n = Math.Min(count - written, _pending.Length - _offset);
            for (int i = 0; i < n; i++) buffer[written + i] = _pending[_offset + i] / 32768f;
            _offset += n;
            written += n;
        }
        return written - written % Format.Channels;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Uncompressed AIFF/AIFC, which Windows can't read on its own.</summary>
internal sealed class AiffSource : IAudioSource
{
    readonly byte[] _data;
    readonly int _bytesPerSample;
    readonly bool _littleEndian, _float;
    int _position;

    public AudioFormat Format { get; }
    public int SourceBits { get; }
    public long? TotalFrames { get; }

    public AiffSource(string path)
    {
        var file = File.ReadAllBytes(path);
        if (file.Length < 12 || file[0] != 'F' || file[1] != 'O' || file[2] != 'R' || file[3] != 'M')
            throw new NotSupportedException("Not an AIFF file.");
        bool aifc = file[8] == 'A' && file[9] == 'I' && file[10] == 'F' && file[11] == 'C';
        int channels = 0, bits = 16;
        double rate = 44100;
        string compression = "NONE";
        int dataOffset = -1, dataLength = 0;
        for (int pos = 12; pos + 8 <= file.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(file, pos, 4);
            int size = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "COMM")
            {
                channels = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(body));
                bits = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(body + 6));
                rate = ReadExtended(file.AsSpan(body + 8, 10));
                if (aifc && size >= 22) compression = System.Text.Encoding.ASCII.GetString(file, body + 18, 4);
            }
            else if (id == "SSND")
            {
                int offset = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(body));
                dataOffset = body + 8 + offset;
                dataLength = Math.Min(size - 8 - offset, file.Length - dataOffset);
            }
            pos = body + size + (size & 1);
        }
        if (channels <= 0 || dataOffset < 0) throw new NotSupportedException("This AIFF file is damaged.");
        _littleEndian = compression is "sowt";
        _float = compression is "fl32" or "FL32";
        if (compression is not ("NONE" or "sowt" or "fl32" or "FL32" or "twos")) throw new NotSupportedException("Compressed AIFF isn't supported.");
        _bytesPerSample = _float ? 4 : (bits + 7) / 8;
        _data = file.AsSpan(dataOffset, dataLength).ToArray();
        SourceBits = _float ? 24 : Math.Min(bits, 24);
        Format = new AudioFormat((int)Math.Round(rate), channels);
        TotalFrames = _data.Length / (_bytesPerSample * channels);
    }

    static double ReadExtended(ReadOnlySpan<byte> b)
    {
        int exponent = ((b[0] & 0x7F) << 8 | b[1]) - 16383;
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(b[2..]);
        double value = mantissa * Math.Pow(2, exponent - 63);
        return (b[0] & 0x80) != 0 ? -value : value;
    }

    public int Read(float[] buffer, int count)
    {
        int samples = Math.Min(count - count % Format.Channels, (_data.Length - _position) / _bytesPerSample);
        for (int i = 0; i < samples; i++, _position += _bytesPerSample)
        {
            var s = _data.AsSpan(_position, _bytesPerSample);
            buffer[i] = _float ? (_littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s)) : Pcm(s);
        }
        return samples;
    }

    float Pcm(ReadOnlySpan<byte> s)
    {
        int v = 0;
        for (int i = 0; i < s.Length; i++) v = v << 8 | s[_littleEndian ? s.Length - 1 - i : i];
        int shift = 32 - 8 * s.Length;
        v = v << shift >> shift; // sign-extend
        return s.Length == 1 ? v / 128f : v / (float)(1 << (8 * s.Length - 1));
    }

    public void Dispose() { }
}

// ---------------------------------------------------------------------- processors

/// <summary>Converts to a target channel count: mono to stereo, or surround downmixed to stereo.</summary>
internal sealed class ChannelMixer(IAudioSource source, int channels) : IAudioSource
{
    float[] _scratch = [];
    public AudioFormat Format => source.Format with { Channels = channels };
    public int SourceBits => source.SourceBits;
    public long? TotalFrames => source.TotalFrames;

    public int Read(float[] buffer, int count)
    {
        int inCh = source.Format.Channels, frames = count / channels;
        if (_scratch.Length < frames * inCh) _scratch = new float[frames * inCh];
        int read = source.Read(_scratch, frames * inCh) / inCh;
        for (int f = 0; f < read; f++)
        {
            var frame = _scratch.AsSpan(f * inCh, inCh);
            if (channels == 1)
            {
                float sum = 0;
                foreach (var v in frame) sum += v;
                buffer[f] = sum / inCh;
            }
            else if (inCh == 1)
            {
                for (int c = 0; c < channels; c++) buffer[f * channels + c] = frame[0];
            }
            else
            {
                // WAVE channel order: L R C LFE BL BR SL SR. Centre and surrounds fold in at -3 dB.
                const float k = 0.7071f;
                float l = frame[0], r = frame[1];
                if (inCh > 2) { l += k * frame[2]; r += k * frame[2]; }
                if (inCh > 4) { l += k * frame[4]; r += k * frame[Math.Min(5, inCh - 1)]; }
                if (inCh > 6) { l += k * frame[6]; r += k * frame[Math.Min(7, inCh - 1)]; }
                float norm = inCh switch { <= 2 => 1, <= 4 => 1 + k, <= 6 => 1 + 2 * k, _ => 1 + 3 * k };
                buffer[f * channels] = l / norm;
                buffer[f * channels + 1] = r / norm;
                for (int c = 2; c < channels; c++) buffer[f * channels + c] = 0;
            }
        }
        return read * channels;
    }

    public void Dispose() => source.Dispose();
}

/// <summary>Band-limited (windowed-sinc, polyphase) sample-rate conversion.</summary>
internal sealed class Resampler : IAudioSource
{
    const int HalfTaps = 24, Phases = 512;
    readonly IAudioSource _source;
    readonly int _channels;
    readonly double _step;
    readonly float[] _table; // [phase][tap], with one extra phase for interpolation
    readonly List<float> _history = []; // interleaved input frames starting at _historyStart
    long _historyStart;
    double _position; // in input frames
    bool _sourceEnded;
    readonly float[] _readBuffer;

    public AudioFormat Format { get; }
    public int SourceBits => _source.SourceBits;
    public long? TotalFrames { get; }

    public Resampler(IAudioSource source, int rate)
    {
        _source = source;
        _channels = source.Format.Channels;
        Format = source.Format with { SampleRate = rate };
        _step = (double)source.Format.SampleRate / rate;
        if (source.TotalFrames is long total) TotalFrames = (long)(total / _step);
        _readBuffer = new float[4096 * _channels];

        double cutoff = Math.Min(1.0, 1.0 / _step) * 0.96;
        _table = new float[(Phases + 1) * 2 * HalfTaps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            for (int j = 0; j < 2 * HalfTaps; j++)
            {
                double d = frac + HalfTaps - 1 - j; // distance from output position to tap
                double x = Math.PI * cutoff * d;
                double sinc = Math.Abs(d) < 1e-9 ? 1 : Math.Sin(x) / x;
                double w = d / HalfTaps;
                double window = Math.Abs(w) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2 * Math.PI * w); // Blackman
                _table[p * 2 * HalfTaps + j] = (float)(cutoff * sinc * window);
            }
        }
    }

    bool EnsureBuffered(long lastFrame)
    {
        while (_historyStart + _history.Count / _channels <= lastFrame && !_sourceEnded)
        {
            int n = _source.Read(_readBuffer, _readBuffer.Length);
            if (n == 0) _sourceEnded = true;
            for (int i = 0; i < n; i++) _history.Add(_readBuffer[i]);
        }
        return _historyStart + _history.Count / _channels > (long)Math.Floor(_position);
    }

    public int Read(float[] buffer, int count)
    {
        int frames = count / _channels, produced = 0;
        while (produced < frames)
        {
            long i0 = (long)Math.Floor(_position);
            if (!EnsureBuffered(i0 + HalfTaps)) break;
            double frac = _position - i0;
            double phase = frac * Phases;
            int p = (int)phase;
            float t = (float)(phase - p);
            int rowA = p * 2 * HalfTaps, rowB = (p + 1) * 2 * HalfTaps;
            long available = _historyStart + _history.Count / _channels;

            for (int c = 0; c < _channels; c++)
            {
                float sum = 0;
                for (int j = 0; j < 2 * HalfTaps; j++)
                {
                    long k = i0 - HalfTaps + 1 + j;
                    if (k < _historyStart || k >= available) continue; // silence outside the signal
                    float coef = _table[rowA + j] + t * (_table[rowB + j] - _table[rowA + j]);
                    sum += coef * _history[(int)((k - _historyStart) * _channels + c)];
                }
                buffer[produced * _channels + c] = sum;
            }
            produced++;
            _position += _step;

            // Drop history no longer needed.
            long keepFrom = (long)Math.Floor(_position) - HalfTaps;
            if (keepFrom - _historyStart > 8192)
            {
                int drop = (int)(keepFrom - _historyStart);
                _history.RemoveRange(0, drop * _channels);
                _historyStart += drop;
            }
        }
        return produced * _channels;
    }

    public void Dispose() => _source.Dispose();
}

// ---------------------------------------------------------------------- sinks

internal static class Pcm
{
    public static void ToInt(ReadOnlySpan<float> samples, Span<byte> output, int bits, bool bigEndian = false)
    {
        int bytes = bits / 8;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = Math.Clamp(samples[i], -1f, 1f);
            int value = bits == 16 ? (int)MathF.Round(v * 32767f) : (int)MathF.Round(v * 8388607f);
            var o = output.Slice(i * bytes, bytes);
            for (int b = 0; b < bytes; b++) o[bigEndian ? bytes - 1 - b : b] = (byte)(value >> (8 * b));
        }
    }
}

/// <summary>Encodes through a Windows encoder (MP3, AAC, WMA, FLAC) into a Windows container.</summary>
internal sealed class MfAudioSink : IAudioSink
{
    readonly IMFSinkWriter _writer;
    readonly int _stream, _bits;
    readonly AudioFormat _format;
    long _frames;
    byte[] _bytes = [];

    public MfAudioSink(string path, Guid container, IMFMediaType outputType, AudioFormat format, int bits)
    {
        MF.EnsureStarted();
        _format = format;
        _bits = bits;
        MF.MFCreateAttributes(out var attrs, 3);
        attrs.SetGUID(MF.TRANSCODE_CONTAINERTYPE, container);
        attrs.SetInt(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        attrs.SetInt(MF.SINK_WRITER_DISABLE_THROTTLING, 1);
        MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer);
        MF.Release(attrs);
        _writer.AddStream(outputType, out _stream);
        var input = PcmType(format, bits);
        _writer.SetInputMediaType(_stream, input, null);
        MF.Release(input);
        _writer.BeginWriting();
    }

    public static IMFMediaType PcmType(AudioFormat format, int bits)
    {
        var t = MF.NewType(MF.MediaTypeAudio, MF.AudioPCM);
        t.SetInt(MF.MT_AUDIO_NUM_CHANNELS, format.Channels);
        t.SetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND, format.SampleRate);
        t.SetInt(MF.MT_AUDIO_BITS_PER_SAMPLE, bits);
        t.SetInt(MF.MT_AUDIO_BLOCK_ALIGNMENT, format.Channels * bits / 8);
        t.SetInt(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, format.SampleRate * format.Channels * bits / 8);
        t.SetInt(MF.MT_ALL_SAMPLES_INDEPENDENT, 1);
        return t;
    }

    public void Write(float[] samples, int count)
    {
        int size = count * _bits / 8;
        if (_bytes.Length < size) _bytes = new byte[size];
        Pcm.ToInt(samples.AsSpan(0, count), _bytes, _bits);
        long frames = count / _format.Channels;
        long time = _frames * 10_000_000 / _format.SampleRate;
        long duration = (_frames + frames) * 10_000_000 / _format.SampleRate - time;
        var sample = MF.SampleFrom(_bytes.AsSpan(0, size), time, duration);
        _writer.WriteSample(_stream, sample);
        MF.Release(sample);
        _frames += frames;
    }

    public void Finish() => _writer.DoFinalize();
    public void Dispose() => MF.Release(_writer);
}

internal sealed class WavSink : IAudioSink
{
    readonly FileStream _file;
    readonly AudioFormat _format;
    readonly int _bits;
    long _dataBytes;
    byte[] _bytes = [];

    public WavSink(string path, AudioFormat format, int bits)
    {
        _file = File.Create(path);
        _format = format;
        _bits = bits;
        _file.Write(new byte[44]); // header written at the end, once sizes are known
    }

    public void Write(float[] samples, int count)
    {
        int size = count * _bits / 8;
        if (_bytes.Length < size) _bytes = new byte[size];
        Pcm.ToInt(samples.AsSpan(0, count), _bytes, _bits);
        _file.Write(_bytes, 0, size);
        _dataBytes += size;
    }

    public void Finish()
    {
        int block = _format.Channels * _bits / 8;
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, 36 + _dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), (short)_format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), _format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), _format.SampleRate * block);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), (short)block);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), (short)_bits);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, _dataBytes));
        _file.Position = 0;
        _file.Write(h);
    }

    public void Dispose() => _file.Dispose();
}

internal sealed class AiffSink : IAudioSink
{
    readonly FileStream _file;
    readonly AudioFormat _format;
    readonly int _bits;
    long _dataBytes;
    byte[] _bytes = [];

    public AiffSink(string path, AudioFormat format, int bits)
    {
        _file = File.Create(path);
        _format = format;
        _bits = bits;
        _file.Write(new byte[54]);
    }

    public void Write(float[] samples, int count)
    {
        int size = count * _bits / 8;
        if (_bytes.Length < size) _bytes = new byte[size];
        Pcm.ToInt(samples.AsSpan(0, count), _bytes, _bits, bigEndian: true);
        _file.Write(_bytes, 0, size);
        _dataBytes += size;
    }

    public void Finish()
    {
        var h = new byte[54];
        "FORM"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)(46 + _dataBytes));
        "AIFFCOMM"u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(16), 18);
        BinaryPrimitives.WriteInt16BigEndian(h.AsSpan(20), (short)_format.Channels);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(22), (uint)(_dataBytes / (_format.Channels * _bits / 8)));
        BinaryPrimitives.WriteInt16BigEndian(h.AsSpan(26), (short)_bits);
        WriteExtended(h.AsSpan(28, 10), _format.SampleRate);
        "SSND"u8.CopyTo(h.AsSpan(38));
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(42), (uint)(8 + _dataBytes));
        _file.Position = 0;
        _file.Write(h);
    }

    static void WriteExtended(Span<byte> b, double value)
    {
        int exponent = (int)Math.Floor(Math.Log2(value));
        ulong mantissa = (ulong)(value / Math.Pow(2, exponent - 63));
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(exponent + 16383));
        BinaryPrimitives.WriteUInt64BigEndian(b[2..], mantissa);
    }

    public void Dispose() => _file.Dispose();
}

internal sealed class VorbisSink : IAudioSink
{
    readonly FileStream _file;
    readonly OggStream _ogg;
    readonly ProcessingState _state;
    readonly int _channels;

    public VorbisSink(string path, AudioFormat format, MediaTags tags)
    {
        _file = File.Create(path);
        _channels = format.Channels;
        // 0.5 (~160 kbps stereo) is the highest quality this encoder handles correctly; above it levels go wrong.
        var info = VorbisInfo.InitVariableBitRate(format.Channels, format.SampleRate, 0.5f);
        _ogg = new OggStream(Random.Shared.Next());
        var comments = new Comments();
        foreach (var (key, value) in tags.AsVorbisComments()) comments.AddTag(key, value);
        _ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        _ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        _ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        Flush(force: true);
        _state = ProcessingState.Create(info);

        // The encoder drops the first half long-block of audio, so give it that much silence to drop.
        int lead = info.CodecSetup.BlockSizes.Max() / 2;
        var silence = new float[_channels][];
        for (int c = 0; c < _channels; c++) silence[c] = new float[lead];
        _state.WriteData(silence, lead, 0);
    }

    public void Write(float[] samples, int count)
    {
        const int Chunk = 512; // the size the encoder library is designed and tested around
        int frames = count / _channels;
        for (int start = 0; start < frames; start += Chunk)
        {
            int n = Math.Min(Chunk, frames - start);
            // Fresh arrays per chunk: the encoder may hold on to what it was given.
            var planar = new float[_channels][];
            for (int c = 0; c < _channels; c++) planar[c] = new float[n];
            for (int f = 0; f < n; f++)
                for (int c = 0; c < _channels; c++)
                    planar[c][f] = samples[(start + f) * _channels + c];
            _state.WriteData(planar, n, 0);
            Drain(false);
        }
    }

    void Drain(bool force)
    {
        while (!_ogg.Finished && _state.PacketOut(out var packet))
        {
            _ogg.PacketIn(packet);
            Flush(force);
        }
    }

    void Flush(bool force)
    {
        while (_ogg.PageOut(out var page, force))
        {
            _file.Write(page.Header);
            _file.Write(page.Body);
        }
    }

    public void Finish()
    {
        _state.WriteEndOfStream();
        Drain(true);
        Flush(true);
    }

    public void Dispose() => _file.Dispose();
}

internal sealed class OpusSink : IAudioSink
{
    readonly OpusOggWriteStream _writer;

    public OpusSink(string path, AudioFormat format, MediaTags tags)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, format.Channels, OpusApplication.OPUS_APPLICATION_AUDIO, null);
        encoder.Bitrate = format.Channels == 1 ? 96_000 : 160_000;
        encoder.Complexity = 10;
        var opusTags = new OpusTags();
        foreach (var (key, value) in tags.AsVorbisComments()) opusTags.Fields[key] = value;
        _writer = new OpusOggWriteStream(encoder, File.Create(path), opusTags, format.SampleRate, 8, leaveOpen: false);
    }

    public void Write(float[] samples, int count) => _writer.WriteSamples(samples, 0, count);
    public void Finish() => _writer.Finish();
    public void Dispose() { }
}
