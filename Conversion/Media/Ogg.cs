using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using NVorbis;
using OggVorbisEncoder;
using Encoding = System.Text.Encoding;

namespace ConvertWheel.Conversion.Media;

/// <summary>Ogg, the container of Vorbis and Opus: pages with a checksum, holding packets in 255-byte segments.</summary>
internal static class Ogg
{
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint r = (uint)n << 24;
        for (int i = 0; i < 8; i++) r = (r & 0x80000000) != 0 ? r << 1 ^ 0x04C11DB7 : r << 1;
        return r;
    }).ToArray();

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = crc << 8 ^ CrcTable[(crc >> 24 ^ b) & 0xFF];
        return crc;
    }

    public sealed record Page(int Serial, bool First, bool Last, long Granule, List<byte[]> Packets);

    /// <summary>The pages of a file's first stream, with the packets that end on each.</summary>
    public sealed class Reader(Stream stream)
    {
        readonly MemoryStream _partial = new();
        int? _serial;
        bool _ended;

        public Page? Next()
        {
            while (ReadPage() is { } page)
            {
                if (_ended && page.First) throw new NotSupportedException("This file is several recordings joined together, which needs FFmpeg.");
                _serial ??= page.Serial;
                if (page.Serial != _serial) continue; // another stream alongside
                if (page.Last) _ended = true;
                return page;
            }
            return null;
        }

        Page? ReadPage()
        {
            var header = new byte[27];
            int n = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (n == 0) return null;
            if (n < header.Length) throw CutShort();
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8)) throw new InvalidDataException("This Ogg file is damaged.");
            var segments = new byte[header[26]];
            if (stream.ReadAtLeast(segments, segments.Length, throwOnEndOfStream: false) < segments.Length) throw CutShort();
            var body = new byte[segments.Sum(s => s)];
            if (stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length) throw CutShort();

            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22));
            header.AsSpan(22, 4).Clear();
            if (Crc(Crc(Crc(0, header), segments), body) != stored) throw new InvalidDataException("This Ogg file is damaged.");

            int serial = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14));
            var packets = new List<byte[]>();
            if (_serial is null || serial == _serial)
            {
                if ((header[5] & 1) == 0) _partial.SetLength(0); // not continued: drop a packet that never finished
                int at = 0;
                foreach (var length in segments)
                {
                    _partial.Write(body, at, length);
                    at += length;
                    if (length == 255) continue;
                    packets.Add(_partial.ToArray());
                    _partial.SetLength(0);
                }
            }
            return new Page(serial, (header[5] & 2) != 0, (header[5] & 4) != 0, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6)), packets);
        }

        static InvalidDataException CutShort() => new("This file is cut short.");
    }

    /// <summary>Checks a file is whole (no cut-off page, not several recordings in a row); returns its last granule position.</summary>
    public static long Check(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var header = new byte[27];
        int? serial = null;
        bool ended = false;
        long granule = -1;
        while (file.Position < file.Length)
        {
            long start = file.Position;
            if (file.ReadAtLeast(header, header.Length, false) < header.Length) throw CutShort();
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8)) throw new InvalidDataException("This Ogg file is damaged.");
            var segments = new byte[header[26]];
            if (file.ReadAtLeast(segments, segments.Length, false) < segments.Length) throw CutShort();
            long end = file.Position + segments.Sum(s => s);
            if (end > file.Length) throw CutShort();
            int pageSerial = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14));
            if (ended && (header[5] & 2) != 0) throw new NotSupportedException("This file is several recordings joined together, which needs FFmpeg.");
            serial ??= pageSerial;
            if (pageSerial == serial)
            {
                if ((header[5] & 4) != 0) ended = true;
                if (BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6)) is var g && g >= 0) granule = g;
            }
            file.Position = end;

            InvalidDataException CutShort() => new($"This file is cut short: only the first {start / 1024} KB of audio is whole.");
        }
        return granule;
    }

    /// <summary>Puts packets on pages. The last packet's page carries the end-of-stream flag.</summary>
    public sealed class Writer(Stream stream)
    {
        const int PageBody = 8192;
        readonly int _serial = Random.Shared.Next();
        readonly List<byte> _segments = [];
        readonly MemoryStream _body = new();
        long _granule = -1;
        int _sequence;
        bool _continued;

        /// <param name="granule">The stream position (in samples) when this packet has been decoded.</param>
        public void Packet(ReadOnlySpan<byte> packet, long granule, bool flush = false, bool last = false)
        {
            // On a page of its own, the last packet's position can only mean where to stop, not where to start.
            if (_body.Length > 0 && (last || _body.Length + packet.Length > PageBody)) Flush(false);
            int at = 0;
            while (true)
            {
                if (_segments.Count == 255) Flush(false, midPacket: at > 0);
                int length = Math.Min(255, packet.Length - at);
                _segments.Add((byte)length);
                _body.Write(packet.Slice(at, length));
                at += length;
                if (length < 255) break;
            }
            _granule = granule;
            if (flush || last) Flush(last);
        }

        void Flush(bool last, bool midPacket = false)
        {
            if (_segments.Count == 0) return;
            var header = new byte[27 + _segments.Count];
            "OggS"u8.CopyTo(header);
            header[5] = (byte)((_continued ? 1 : 0) | (_sequence == 0 ? 2 : 0) | (last ? 4 : 0));
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), _granule);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), _serial);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), _sequence++);
            header[26] = (byte)_segments.Count;
            _segments.CopyTo(header, 27);
            var body = _body.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), Crc(Crc(0, header), body));
            stream.Write(header);
            stream.Write(body);
            _segments.Clear();
            _body.SetLength(0);
            _continued = midPacket;
            _granule = -1; // a page on which no packet ends has no position
        }
    }

    /// <summary>The "KEY=value" comments (tags) of a Vorbis or Opus file: its second packet.</summary>
    public static List<KeyValuePair<string, string>> ReadComments(string path)
    {
        var comments = new List<KeyValuePair<string, string>>();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var reader = new Reader(file);
        int seen = 0;
        byte[]? packet = null;
        while (packet == null && reader.Next() is { } page)
            foreach (var p in page.Packets)
                if (++seen == 2) packet = p;
        if (packet == null) return comments;
        int at = packet.AsSpan().StartsWith("OpusTags"u8) ? 8 : packet.AsSpan().StartsWith("\u0003vorbis"u8) ? 7 : -1;
        if (at < 0) return comments;

        string? Text()
        {
            if (at + 4 > packet.Length) return null;
            int length = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(at));
            if (length < 0 || length > packet.Length - at - 4) return null;
            at += 4 + length;
            return Encoding.UTF8.GetString(packet, at - length, length);
        }
        if (Text() == null || at + 4 > packet.Length) return comments;
        int count = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(at));
        at += 4;
        for (int i = 0; i < count && Text() is { } comment; i++)
            if (comment.IndexOf('=') is > 0 and var equals) comments.Add(new(comment[..equals], comment[(equals + 1)..]));
        return comments;
    }

    /// <summary>The comment header Vorbis and Opus share (UTF-8 "KEY=value" lines), after the given magic.</summary>
    public static byte[] Comments(ReadOnlySpan<byte> magic, string vendor, MediaTags tags, bool framingBit)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Text(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        w.Write(magic);
        Text(vendor);
        var comments = tags.AsVorbisComments().Select(c => $"{c.key}={c.value}").ToList();
        w.Write(comments.Count);
        foreach (var comment in comments) Text(comment);
        if (framingBit) w.Write((byte)1);
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>Ogg Vorbis through NVorbis (managed, so it works even without Windows' web media extensions).</summary>
internal sealed class VorbisSource : IAudioSource
{
    // Vorbis puts the centre second and the bass last; Windows' order is L R C LFE back side.
    static readonly Dictionary<int, (int[] order, int mask)> Layouts = new()
    {
        [1] = ([0], 0x4),
        [2] = ([0, 1], 0x3),
        [3] = ([0, 2, 1], 0x7),
        [4] = ([0, 1, 2, 3], 0x33),
        [5] = ([0, 2, 1, 3, 4], 0x37),
        [6] = ([0, 2, 1, 5, 3, 4], 0x3F),
        [7] = ([0, 2, 1, 6, 5, 3, 4], 0x70F),
        [8] = ([0, 2, 1, 7, 5, 6, 3, 4], 0x63F),
    };

    readonly VorbisReader _reader;
    readonly int[]? _order;

    public VorbisSource(string path)
    {
        Ogg.Check(path);
        _reader = new VorbisReader(path);
        if (Layouts.TryGetValue(_reader.Channels, out var layout))
        {
            ChannelMask = layout.mask;
            if (_reader.Channels > 2) _order = layout.order;
        }
    }

    public AudioFormat Format => new(_reader.SampleRate, _reader.Channels);
    public int SourceBits => 16;
    public int ChannelMask { get; }
    public long? TotalFrames => _reader.TotalSamples > 0 ? _reader.TotalSamples : null;
    public VorbisReader Reader => _reader;

    public int Read(float[] buffer, int count)
    {
        int channels = _reader.Channels;
        int n = _reader.ReadSamples(buffer, 0, count - count % channels);
        if (_order != null)
        {
            Span<float> frame = stackalloc float[channels];
            for (int i = 0; i + channels <= n; i += channels)
            {
                buffer.AsSpan(i, channels).CopyTo(frame);
                for (int c = 0; c < channels; c++) buffer[i + c] = frame[_order[c]];
            }
        }
        return n;
    }

    public void Dispose() => _reader.Dispose();
}

/// <summary>Ogg Opus through Concentus.</summary>
internal sealed class OpusSource : IAudioSource
{
    const int MaxPacket = 5760; // 120 ms, the longest an Opus packet can be
    readonly FileStream _file;
    readonly Ogg.Reader _ogg;
    readonly IOpusDecoder _decoder;
    readonly float[] _packet;
    readonly Queue<float[]> _decoded = new();
    readonly int _preSkip;
    float[] _pending = [];
    int _offset;
    long _position, _start = -1; // samples decoded so far, and the granule position they count from
    long _skip;

    public AudioFormat Format { get; }
    public int SourceBits => 16;
    public int ChannelMask => Format.Channels == 1 ? 0x4 : 0x3;
    public long? TotalFrames { get; }

    static OpusSource() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

    public OpusSource(string path)
    {
        long end = Ogg.Check(path);
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            _ogg = new Ogg.Reader(_file);
            var head = _ogg.Next()?.Packets.FirstOrDefault();
            if (head == null || head.Length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8)) throw new NotSupportedException("This isn't an Opus file.");
            int channels = head[9];
            if (channels is not (1 or 2) || head[18] != 0) throw new NotSupportedException("This Opus channel layout needs FFmpeg.");
            _preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
            _skip = _preSkip;
            Format = new AudioFormat(48000, channels);
            _decoder = OpusCodecFactory.CreateDecoder(48000, channels, null);
            _decoder.Gain = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16));
            _packet = new float[MaxPacket * channels];

            while (_ogg.Next() is { } page)
            {
                if (page.Packets.Count > 0) break; // the tags, which Ogg.ReadComments reads
            }
            if (end > _preSkip) TotalFrames = end - _preSkip;
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    /// <summary>Decodes a page. The first samples are the codec warming up, and the last page says where the sound really ends.</summary>
    bool Refill()
    {
        while (_decoded.Count == 0)
        {
            if (_ogg.Next() is not { } page) return false;
            int channels = Format.Channels;
            long pageStart = _position;
            var decoded = new List<float[]>();
            foreach (var packet in page.Packets)
            {
                if (packet.Length == 0) continue;
                int frames = _decoder.Decode(packet, _packet, MaxPacket, false);
                if (frames <= 0) throw new InvalidDataException("This Opus audio is damaged.");
                decoded.Add(_packet[..(frames * channels)]);
                _position += frames;
            }
            if (decoded.Count == 0) continue;
            if (_start < 0 && page.Granule >= 0) _start = Math.Max(0, page.Granule - _position); // recordings of a broadcast don't start at 0
            long end = page.Last && page.Granule >= 0 ? Math.Min(_position, page.Granule - Math.Max(0, _start)) : long.MaxValue;

            long at = pageStart;
            foreach (var samples in decoded)
            {
                long frames = samples.Length / channels;
                long from = Math.Min(frames, _skip);
                _skip -= from;
                long to = Math.Min(frames, end - at);
                at += frames;
                if (to > from) _decoded.Enqueue(samples[(int)(from * channels)..(int)(to * channels)]);
            }
        }
        _pending = _decoded.Dequeue();
        _offset = 0;
        return true;
    }

    public int Read(float[] buffer, int count)
    {
        count -= count % Format.Channels;
        int written = 0;
        while (written < count)
        {
            if (_offset >= _pending.Length && !Refill()) break;
            int n = Math.Min(count - written, _pending.Length - _offset);
            Array.Copy(_pending, _offset, buffer, written, n);
            _offset += n;
            written += n;
        }
        return written;
    }

    public void Dispose() => _file.Dispose();
}

internal sealed class VorbisSink : IAudioSink
{
    readonly FileStream _file;
    readonly Ogg.Writer _ogg;
    readonly ProcessingState _state;
    readonly int _channels;
    OggPacket? _held; // the last packet so far: the final one needs the end-of-stream flag
    long _granule;
    int _lastGranule;

    public VorbisSink(string path, AudioFormat format, MediaTags tags)
    {
        _file = File.Create(path);
        _channels = format.Channels;
        // 0.5 (~160 kbps stereo) is the highest quality this encoder handles correctly; above it levels go wrong.
        var info = VorbisInfo.InitVariableBitRate(format.Channels, format.SampleRate, 0.5f);
        _ogg = new Ogg.Writer(_file);
        _ogg.Packet(HeaderPacketBuilder.BuildInfoPacket(info).PacketData, 0, flush: true);
        _ogg.Packet(Ogg.Comments("\u0003vorbis"u8, "Convert Wheel", tags, framingBit: true), 0);
        _ogg.Packet(HeaderPacketBuilder.BuildBooksPacket(info).PacketData, 0, flush: true);
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
            Drain();
        }
    }

    void Drain()
    {
        while (_state.PacketOut(out var packet))
        {
            WriteHeld(last: false);
            _held = packet;
        }
    }

    void WriteHeld(bool last)
    {
        if (_held == null) return;
        // The library counts samples in 32 bits, which wraps after half a day.
        _granule += _held.GranulePosition - _lastGranule;
        _lastGranule = _held.GranulePosition;
        _ogg.Packet(_held.PacketData, _granule, last: last);
        _held = null;
    }

    public void Finish()
    {
        _state.WriteEndOfStream();
        Drain();
        WriteHeld(last: true);
    }

    public void Dispose() => _file.Dispose();
}

internal sealed class OpusSink : IAudioSink
{
    const int Frame = 960; // 20 ms at 48 kHz
    readonly FileStream _file;
    readonly Ogg.Writer _ogg;
    readonly IOpusEncoder _encoder;
    readonly int _channels, _preSkip;
    readonly float[] _input;
    readonly byte[] _output = new byte[4000];
    int _filled;
    long _frames, _encoded; // per channel: samples given, and samples in encoded packets
    byte[]? _held; // the last packet so far: the final one needs the end-of-stream flag

    static OpusSink() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

    public OpusSink(string path, AudioFormat format, int sourceRate, MediaTags tags)
    {
        _channels = format.Channels;
        _input = new float[Frame * _channels];
        _encoder = OpusCodecFactory.CreateEncoder(48000, _channels, OpusApplication.OPUS_APPLICATION_AUDIO, null);
        _encoder.Bitrate = _channels == 1 ? 96_000 : 160_000;
        _encoder.Complexity = 10;
        _preSkip = _encoder.Lookahead;
        _file = File.Create(path);
        _ogg = new Ogg.Writer(_file);

        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)_channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)_preSkip);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(12), sourceRate);
        _ogg.Packet(head, 0, flush: true);
        _ogg.Packet(Ogg.Comments("OpusTags"u8, "Convert Wheel", tags, framingBit: false), 0, flush: true);
    }

    public void Write(float[] samples, int count)
    {
        _frames += count / _channels;
        for (int at = 0; at < count;)
        {
            int n = Math.Min(count - at, _input.Length - _filled);
            Array.Copy(samples, at, _input, _filled, n);
            _filled += n;
            at += n;
            if (_filled == _input.Length) Encode();
        }
    }

    void Encode()
    {
        int length = _encoder.Encode(_input.AsSpan(), Frame, _output.AsSpan(), _output.Length);
        if (_held != null) _ogg.Packet(_held, _encoded, flush: _encoded % 48000 == 0);
        _held = _output[..length];
        _encoded += Frame;
        _filled = 0;
    }

    public void Finish()
    {
        // The decoder starts with pre-skip samples of nothing, so the sound ends that much later.
        long end = _preSkip + _frames;
        while (_encoded < end || _filled > 0)
        {
            Array.Clear(_input, _filled, _input.Length - _filled);
            Encode();
        }
        if (_held != null) _ogg.Packet(_held, end, last: true);
    }

    public void Dispose() => _file.Dispose();
}
