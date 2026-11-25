using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ConvertWheel.Conversion.Media;

// Audio flows as interleaved 32-bit float samples: a source decodes, optional processors change the
// channel count or sample rate, and a sink encodes.

internal readonly record struct AudioFormat(int SampleRate, int Channels);

internal interface IAudioSource : IDisposable
{
    AudioFormat Format { get; }
    /// <summary>Bit depth of the original audio, so lossless outputs can keep 24-bit sources at 24 bits.</summary>
    int SourceBits { get; }
    /// <summary>Which speaker each channel is for, as WAVE speaker bits in channel order; 0 when unknown.</summary>
    int ChannelMask { get; }
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
    readonly bool _keepTiming;
    readonly int _pcmBits;
    readonly long? _duration; // 100 ns, of audio-only files
    readonly long _tolerance; // how much less than that is still normal
    float[] _pending = [];
    int _pendingOffset;
    bool _ended;
    long _framesRead, _firstTime;
    byte[]? _first; // read ahead while opening
    double _shift;
    bool _started;

    public AudioFormat Format { get; }
    public int SourceBits { get; }
    public int ChannelMask { get; }
    public long? TotalFrames { get; }
    public Guid NativeSubtype { get; }

    /// <summary>When the first sound starts, in 100-ns units as Windows reports it.</summary>
    public long FirstTime => _firstTime;

    /// <summary>With timing kept: seconds to add to the times Windows reports, so 0 is where the output starts.</summary>
    public double Shift { set => _shift = value; }

    /// <param name="keepTiming">Fills a late start or a gap with silence, so the sound stays in step with the picture.</param>
    public MfAudioSource(string path, bool keepTiming = false)
    {
        _keepTiming = keepTiming;
        MF.EnsureStarted();
        MediaCheck.EnsureComplete(path);
        MF.MFCreateSourceReaderFromURL(path, null, out _reader);
        try
        {
            try
            {
                _reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
                _reader.SetStreamSelection(MF.SourceReaderFirstAudio, true);
            }
            catch (COMException)
            {
                throw NoAudio(path);
            }
            if (_reader.GetNativeMediaType(MF.SourceReaderFirstAudio, 0, out var native) < 0)
                throw NoAudio(path);
            try
            {
                NativeSubtype = native.GetGuid(MF.MT_SUBTYPE) ?? Guid.Empty;
                // Float and 32-bit audio counts as 24-bit, the most our lossless outputs hold.
                bool lossless = NativeSubtype == MF.AudioPCM || NativeSubtype == MF.AudioFLAC || NativeSubtype == MF.AudioALAC || NativeSubtype == MF.AudioMp4ALAC || NativeSubtype == MF.AudioWMALossless;
                int bits = native.GetInt(MF.MT_AUDIO_BITS_PER_SAMPLE) ?? 0;
                SourceBits = NativeSubtype == MF.AudioFloat || lossless && bits > 16 ? 24 : 16;
                // Uncompressed samples are taken as they are (Windows' own conversion isn't exact for 32-bit ones).
                if (NativeSubtype == MF.AudioPCM && bits is 8 or 16 or 24 or 32 && native.GetInt(MF.MT_AUDIO_BLOCK_ALIGNMENT) == native.GetInt(MF.MT_AUDIO_NUM_CHANNELS) * bits / 8)
                    _pcmBits = _reader.SetCurrentMediaType(MF.SourceReaderFirstAudio, IntPtr.Zero, native) >= 0 ? bits : 0;
            }
            finally { MF.Release(native); }

            int hr = 0;
            if (_pcmBits == 0)
            {
                var want = MF.NewType(MF.MediaTypeAudio, MF.AudioFloat);
                try { hr = _reader.SetCurrentMediaType(MF.SourceReaderFirstAudio, IntPtr.Zero, want); }
                finally { MF.Release(want); }
            }
            if (hr < 0) throw new NotSupportedException("Windows can't decode this audio.");
            // Some decoders (AC-3) only say what they really produce once they've decoded something.
            while (_first == null && !_ended) (_first, _firstTime) = NextSample();
            Marshal.ThrowExceptionForHR(_reader.GetCurrentMediaType(MF.SourceReaderFirstAudio, out var current));
            try
            {
                Format = new AudioFormat(current.GetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND) ?? 44100, current.GetInt(MF.MT_AUDIO_NUM_CHANNELS) ?? 2);
                ChannelMask = current.GetInt(MF.MT_AUDIO_CHANNEL_MASK) ?? 0;
            }
            finally { MF.Release(current); }
            if (MF.Duration(_reader) is long d)
            {
                TotalFrames = (long)((decimal)d * Format.SampleRate / 10_000_000);
                // MP3, raw AAC and ADPCM durations are guesses, and in a video the audio may simply be shorter.
                bool certain = NativeSubtype == MF.AudioPCM || NativeSubtype == MF.AudioFloat || NativeSubtype == MF.AudioFLAC || NativeSubtype == MF.AudioAAC
                    || NativeSubtype == MF.AudioALAC || NativeSubtype == MF.AudioMp4ALAC || NativeSubtype == MF.AudioWMAV8 || NativeSubtype == MF.AudioWMAV9;
                if (certain && Path.GetExtension(path).ToLowerInvariant() is ".flac" or ".wav" or ".m4a" or ".m4b" or ".wma") _duration = d;
                // FLAC and WAV count samples; the others' encoders add and drop a little.
                _tolerance = NativeSubtype == MF.AudioFLAC || NativeSubtype == MF.AudioPCM ? 500_000 : Math.Max(3_000_000, d / 50);
            }
        }
        catch
        {
            MF.Release(_reader);
            throw;
        }
    }

    static Exception NoAudio(string path) => MediaCheck.HasAudio(path) == false || Path.GetExtension(path).ToLowerInvariant() is ".wmv" or ".asf"
        ? new ConversionException("This video has no audio track")
        : new NotSupportedException("Windows found no audio track in this file.");

    public int Read(float[] buffer, int count)
    {
        count -= count % Format.Channels;
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

    /// <summary>The next decoded piece, or null when there's none this time (see _ended).</summary>
    (byte[]? data, long time) NextSample()
    {
        _reader.ReadSample(MF.SourceReaderFirstAudio, 0, out _, out var flags, out long time, out var sample);
        try
        {
            if ((flags & MF.ReaderError) != 0) throw new InvalidOperationException("Windows couldn't decode this audio.");
            if ((flags & MF.ReaderTypeChanged) != 0 && Format.Channels > 0) CheckFormat();
            if ((flags & MF.ReaderEndOfStream) != 0) _ended = true;
            return (sample == null ? null : MF.Bytes(sample), time);
        }
        finally { MF.Release(sample); }
    }

    void CheckFormat()
    {
        Marshal.ThrowExceptionForHR(_reader.GetCurrentMediaType(MF.SourceReaderFirstAudio, out var current));
        try
        {
            if (current.GetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND) != Format.SampleRate || current.GetInt(MF.MT_AUDIO_NUM_CHANNELS) != Format.Channels)
                throw new NotSupportedException("This audio changes format partway, which needs FFmpeg.");
        }
        finally { MF.Release(current); }
    }

    bool Refill()
    {
        long time = _firstTime;
        var bytes = _first;
        _first = null;
        if (bytes == null) (bytes, time) = NextSample();
        int count = bytes == null ? 0 : bytes.Length / (_pcmBits == 0 ? 4 : _pcmBits / 8);
        if (bytes != null)
        {
            long lead = 0, drop = 0;
            if (_keepTiming)
            {
                long gap = (long)((decimal)time * Format.SampleRate / 10_000_000 + (decimal)_shift * Format.SampleRate) - _framesRead;
                if (gap > (_started ? Format.SampleRate / 25 : 0)) lead = Math.Min(gap, Format.SampleRate * 3600L) * Format.Channels; // gaps over 40 ms aren't just rounding
                else if (gap < 0 && !_started) drop = Math.Min(-gap * Format.Channels, count); // encoder warm-up before the start
                _started = drop < count;
            }
            _pending = new float[lead + count - drop];
            _pendingOffset = 0;
            var samples = _pending.AsSpan((int)lead);
            for (int i = 0, from = (int)drop; i < samples.Length; i++, from++)
                samples[i] = _pcmBits switch
                {
                    0 => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(from * 4)),
                    8 => (bytes[from] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(from * 2)) / 32768f,
                    24 => (bytes[from * 3] << 8 | bytes[from * 3 + 1] << 16 | bytes[from * 3 + 2] << 24) / 2147483648f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(from * 4)) / 2147483648f,
                };
            _framesRead += _pending.Length / Format.Channels;
        }
        if (_ended)
        {
            // Damaged files can just stop; a shorter result without a word would be worse than trying FFmpeg.
            long read = (long)((decimal)_framesRead * 10_000_000 / Format.SampleRate);
            if (_duration is long expected && expected - read > _tolerance)
                throw new InvalidOperationException($"Only {MediaCheck.Clock(read)} of {MediaCheck.Clock(expected)} could be read, so this file is cut short or damaged.");
        }
        return bytes != null || !_ended;
    }

    public void Dispose() => MF.Release(_reader);
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
    public int ChannelMask => 0;
    public long? TotalFrames { get; }

    public AiffSource(string path)
    {
        var file = File.ReadAllBytes(path);
        if (file.Length < 12 || file[0] != 'F' || file[1] != 'O' || file[2] != 'R' || file[3] != 'M')
            throw new NotSupportedException("Not an AIFF file.");
        bool aifc = file[8] == 'A' && file[9] == 'I' && file[10] == 'F' && file[11] == 'C';
        if (!aifc && !file.AsSpan(8, 4).SequenceEqual("AIFF"u8)) throw new NotSupportedException("Not an AIFF file.");
        int channels = 0, bits = 16;
        double rate = 44100;
        string compression = "NONE";
        int dataOffset = -1, dataLength = 0;
        for (int pos = 12; pos + 8 <= file.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(file, pos, 4);
            int size = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos + 4));
            int body = pos + 8;
            if (size < 0 || size > file.Length - body) throw new NotSupportedException("This AIFF file is cut short or damaged.");
            if (id == "COMM")
            {
                if (size < (aifc ? 22 : 18)) throw new NotSupportedException("This AIFF file is damaged.");
                channels = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(body));
                bits = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(body + 6));
                rate = ReadExtended(file.AsSpan(body + 8, 10));
                if (aifc && size >= 22) compression = System.Text.Encoding.ASCII.GetString(file, body + 18, 4);
            }
            else if (id == "SSND")
            {
                if (size < 8) throw new NotSupportedException("This AIFF file is damaged.");
                int offset = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(body));
                if (offset < 0 || offset > size - 8) throw new NotSupportedException("This AIFF file is damaged.");
                dataOffset = body + 8 + offset;
                dataLength = Math.Min(size - 8 - offset, file.Length - dataOffset);
            }
            pos = body + size + (size & 1);
        }
        if (channels <= 0 || dataOffset < 0 || bits is < 1 or > 32 || !double.IsFinite(rate) || rate < 1 || rate > int.MaxValue)
            throw new NotSupportedException("This AIFF file is damaged.");
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
        samples -= samples % Format.Channels;
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
        return v / MathF.ScaleB(1, 8 * s.Length - 1);
    }

    public void Dispose() { }
}

// ---------------------------------------------------------------------- processors

/// <summary>Converts to a target channel count: mono to stereo, or surround downmixed to stereo.</summary>
internal sealed class ChannelMixer : IAudioSource
{
    readonly IAudioSource _source;
    readonly int _channels;
    readonly float[] _gains; // [input * output channels + output]
    float[] _scratch = [];

    public AudioFormat Format => _source.Format with { Channels = _channels };
    public int SourceBits => _source.SourceBits;
    public int ChannelMask => Speakers.Default(_channels);
    public long? TotalFrames => _source.TotalFrames;

    public ChannelMixer(IAudioSource source, int channels)
    {
        _source = source;
        _channels = channels;
        int inputs = source.Format.Channels;
        _gains = new float[inputs * channels];
        var speakers = Speakers.Of(source.ChannelMask, inputs);
        for (int i = 0; i < inputs; i++)
        {
            if (channels == 1) _gains[i] = 1f / inputs;
            else if (inputs == 1) for (int c = 0; c < channels; c++) _gains[c] = 1;
            else
            {
                // Centre and surrounds fold in at -3 dB, a single back speaker at -6 dB to each side; the subwoofer is left out.
                const float K = 0.7071f;
                (_gains[i * channels], _gains[i * channels + 1]) = speakers[i] switch
                {
                    Speakers.FrontLeft or Speakers.FrontLeftOfCentre => (1f, 0f),
                    Speakers.FrontRight or Speakers.FrontRightOfCentre => (0f, 1f),
                    Speakers.FrontCentre => (K, K),
                    Speakers.BackLeft or Speakers.SideLeft => (K, 0f),
                    Speakers.BackRight or Speakers.SideRight => (0f, K),
                    Speakers.BackCentre => (0.5f, 0.5f),
                    Speakers.LowFrequency => (0f, 0f),
                    _ => (0.5f, 0.5f), // ceiling speakers and the like
                };
            }
        }
        if (channels == 1 || inputs == 1) return;
        // Turn everything down just enough that it can't clip.
        float loudest = 1;
        for (int c = 0; c < 2; c++) loudest = Math.Max(loudest, Enumerable.Range(0, inputs).Sum(i => _gains[i * channels + c]));
        for (int i = 0; i < _gains.Length; i++) _gains[i] /= loudest;
    }

    public int Read(float[] buffer, int count)
    {
        int inCh = _source.Format.Channels, frames = count / _channels;
        if (_scratch.Length < frames * inCh) _scratch = new float[frames * inCh];
        int read = _source.Read(_scratch, frames * inCh) / inCh;
        Array.Clear(buffer, 0, read * _channels);
        for (int f = 0; f < read; f++)
            for (int i = 0; i < inCh; i++)
            {
                float v = _scratch[f * inCh + i];
                for (int c = 0; c < _channels; c++) buffer[f * _channels + c] += v * _gains[i * _channels + c];
            }
        return read * _channels;
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>WAVE speaker positions. Channels are stored in the order of these bits.</summary>
internal static class Speakers
{
    public const int FrontLeft = 0x1, FrontRight = 0x2, FrontCentre = 0x4, LowFrequency = 0x8, BackLeft = 0x10, BackRight = 0x20,
        FrontLeftOfCentre = 0x40, FrontRightOfCentre = 0x80, BackCentre = 0x100, SideLeft = 0x200, SideRight = 0x400;

    /// <summary>The usual layout for a number of channels.</summary>
    public static int Default(int channels) => channels switch
    {
        1 => 0x4, 2 => 0x3, 3 => 0x7, 4 => 0x33, 5 => 0x37, 6 => 0x3F, 7 => 0x13F, 8 => 0x63F, _ => 0,
    };

    /// <summary>The speaker for each channel.</summary>
    public static int[] Of(int mask, int channels)
    {
        if (System.Numerics.BitOperations.PopCount((uint)mask) != channels) mask = Default(channels);
        var result = new int[channels];
        for (int i = 0, bit = 1; i < channels && bit != 0; bit <<= 1)
            if ((mask & bit) != 0) result[i++] = bit;
        return result;
    }
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
    public int ChannelMask => _source.ChannelMask;
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
            // The same scale audio is decoded with, so 16- and 24-bit samples come back exactly as they were.
            int value = bits == 16
                ? (int)Math.Clamp(MathF.Round(samples[i] * 32768f), -32768f, 32767f)
                : (int)Math.Clamp(MathF.Round(samples[i] * 8388608f), -8388608f, 8388607f);
            var o = output.Slice(i * bytes, bytes);
            for (int b = 0; b < bytes; b++) o[bigEndian ? bytes - 1 - b : b] = (byte)(value >> (8 * b));
        }
    }
}

/// <summary>Encodes through a Windows encoder (MP3, AAC, WMA, FLAC) into a Windows container.</summary>
internal sealed class MfAudioSink : IAudioSink
{
    readonly IMFSinkWriter _writer;
    readonly int _stream, _bits, _tail;
    readonly AudioFormat _format;
    readonly List<float> _held = [];
    long _frames;
    byte[] _bytes = [];

    /// <param name="mask">Speakers of multichannel audio.</param>
    /// <param name="tail">Frames of silence the encoder needs at the end to let go of the last sounds.</param>
    public MfAudioSink(string path, Guid container, IMFMediaType outputType, AudioFormat format, int bits, int mask = 0, int tail = 0)
    {
        MF.EnsureStarted();
        _format = format;
        _bits = bits;
        _tail = tail;
        try
        {
            MF.MFCreateAttributes(out var attrs, 3);
            try
            {
                attrs.SetGUID(MF.TRANSCODE_CONTAINERTYPE, container);
                attrs.SetInt(MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                attrs.SetInt(MF.SINK_WRITER_DISABLE_THROTTLING, 1);
                MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer);
            }
            finally { MF.Release(attrs); }
            _writer.AddStream(outputType, out _stream);
            var input = PcmType(format, bits);
            if (mask != 0) input.SetInt(MF.MT_AUDIO_CHANNEL_MASK, mask);
            try { _writer.SetInputMediaType(_stream, input, null); }
            finally { MF.Release(input); }
            _writer.BeginWriting();
        }
        catch
        {
            MF.Release(_writer);
            throw;
        }
        finally { MF.Release(outputType); }
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

    // Windows' MP3 encoder drops the end of the audio when the last piece it gets is small, so the final
    // pieces are held back and go in together.
    const int Piece = 8192;

    public void Write(float[] samples, int count)
    {
        CollectionsMarshal.SetCount(_held, _held.Count + count);
        samples.AsSpan(0, count).CopyTo(CollectionsMarshal.AsSpan(_held)[^count..]);
        int piece = Piece * _format.Channels;
        while (_held.Count >= 2 * piece)
        {
            WritePiece(CollectionsMarshal.AsSpan(_held)[..piece]);
            _held.RemoveRange(0, piece);
        }
    }

    void WritePiece(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;
        int count = samples.Length, size = count * _bits / 8;
        if (_bytes.Length < size) _bytes = new byte[size];
        Pcm.ToInt(samples, _bytes, _bits);
        long frames = count / _format.Channels;
        long time = _frames * 10_000_000 / _format.SampleRate;
        long duration = (_frames + frames) * 10_000_000 / _format.SampleRate - time;
        var sample = MF.SampleFrom(_bytes.AsSpan(0, size), time, duration);
        try { _writer.WriteSample(_stream, sample); }
        finally { MF.Release(sample); }
        _frames += frames;
    }

    public void Finish()
    {
        int silence = _tail * _format.Channels;
        CollectionsMarshal.SetCount(_held, _held.Count + silence);
        CollectionsMarshal.AsSpan(_held)[^silence..].Clear();
        WritePiece(CollectionsMarshal.AsSpan(_held));
        _held.Clear();
        _writer.DoFinalize();
    }

    public void Dispose() => MF.Release(_writer);
}

internal sealed class WavSink : IAudioSink
{
    readonly FileStream _file;
    readonly AudioFormat _format;
    readonly int _bits, _mask, _header;
    long _dataBytes;
    byte[] _bytes = [];

    public WavSink(string path, AudioFormat format, int bits, int mask)
    {
        _file = File.Create(path);
        _format = format;
        _bits = bits;
        // More than two channels need the extended header, which says which speaker each is for.
        _mask = format.Channels > 2 ? mask : 0;
        _header = _mask != 0 ? 68 : 44;
        _file.Write(new byte[_header]); // written at the end, once sizes are known
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
        if (_dataBytes > uint.MaxValue - _header - (_dataBytes & 1)) throw new ConversionException("This audio is too large for a WAV file.");
        if ((_dataBytes & 1) != 0) _file.WriteByte(0);
        int block = _format.Channels * _bits / 8;
        var h = new byte[_header];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(_header - 8 + _dataBytes + (_dataBytes & 1)));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), _header - 28);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), (short)(_mask != 0 ? -2 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), (short)_format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), _format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), _format.SampleRate * block);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), (short)block);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), (short)_bits);
        if (_mask != 0)
        {
            BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(36), 22);
            BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(38), (short)_bits);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), _mask);
            MF.AudioPCM.TryWriteBytes(h.AsSpan(44));
        }
        "data"u8.CopyTo(h.AsSpan(_header - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(_header - 4), (uint)_dataBytes);
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
        if (_dataBytes > uint.MaxValue - 46L - (_dataBytes & 1)) throw new ConversionException("This audio is too large for an AIFF file.");
        if ((_dataBytes & 1) != 0) _file.WriteByte(0);
        var h = new byte[54];
        "FORM"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), (uint)(46 + _dataBytes + (_dataBytes & 1)));
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
