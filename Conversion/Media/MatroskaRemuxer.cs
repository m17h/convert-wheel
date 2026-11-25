using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ConvertWheel.Conversion.Media;

/// <summary>
/// Writes Matroska (.mkv) files. H.264 video with AAC/MP3 audio is copied in losslessly; anything
/// else is first encoded to H.264/AAC by Windows and then wrapped.
/// </summary>
internal static class MatroskaRemuxer
{
    static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    static readonly Guid UserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");

    public static void Convert(string input, string output, IProgress<double?>? progress, CancellationToken ct)
    {
        MediaCheck.EnsureComplete(input);
        if (TryWrite(input, output, progress, ct)) return;

        var temp = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}.mp4");
        try
        {
            using (File.Create(temp)) { }
            VideoEngine.TranscodeTo(input, temp, "mp4", new Progress<double?>(p => progress?.Report(p * 0.95)), ct);
            if (!TryWrite(temp, output, null, ct)) throw new ConversionException("Couldn't create the MKV file.");
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    sealed class Track
    {
        public int Number, ReaderStream;
        public bool Video;
        public string CodecId = "";
        public byte[]? CodecPrivate;
        public int Width, Height, DisplayWidth, DisplayHeight, Channels, SampleRate, Rotation;
        public bool AdtsHeader;
        public double? Start; // when it should begin, if the file says
        public long Shift; // 100 ns to add to Windows' times
        public string Language = "und";
        public string? Name;
        public (int matrix, int range, int transfer, int primaries) Colour;
    }

    static bool TryWrite(string input, string output, IProgress<double?>? progress, CancellationToken ct)
    {
        MF.EnsureStarted();
        IMFSourceReader? reader = null;
        try
        {
            var timing = Mp4Info.Read(input);
            if (timing?.Trimmed == true) throw new NotSupportedException("This video has an edit list, which needs FFmpeg.");
            MF.MFCreateSourceReaderFromURL(input, null, out reader);
            reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
            var tracks = new List<Track>();
            bool encode = false, otherAudio = false;
            int videos = 0, audios = 0;
            for (int i = 0; ; i++)
            {
                if (reader.GetNativeMediaType(i, 0, out var type) < 0) break;
                try
                {
                    var major = type.GetGuid(MF.MT_MAJOR_TYPE);
                    var codec = type.GetGuid(MF.MT_SUBTYPE);
                    if (major == MF.MediaTypeVideo && videos++ == 0)
                    {
                        // FFmpeg copies these into MKV as they are, which beats encoding them again.
                        if (codec == MF.VideoHEVC || codec == MF.VideoAV1 || codec == MF.VideoVP8 || codec == MF.VideoVP9 || codec == MF.VideoMPEG2 || codec == MF.VideoMP4V)
                            throw new NotSupportedException("This video can be copied, but not by Windows.");
                        if (codec != MF.VideoH264) { encode = true; continue; }
                        var (w, h) = type.GetSize(MF.MT_FRAME_SIZE) ?? (0, 0);
                        var (pn, pd) = type.GetSize(MF.MT_PIXEL_ASPECT_RATIO) ?? (1, 1);
                        bool square = pn <= 0 || pd <= 0 || pn == pd;
                        tracks.Add(new Track
                        {
                            ReaderStream = i, Video = true, CodecId = "V_MPEG4/ISO/AVC", Width = w, Height = h,
                            // Like other muxers: keep the height, stretch the width.
                            DisplayWidth = square ? 0 : (int)Math.Round(w * (double)pn / pd), DisplayHeight = square ? 0 : h,
                            CodecPrivate = AvcConfig(type.GetBlob(SequenceHeader)),
                            Rotation = FrameRotation.Normalize(type.GetInt(MF.MT_VIDEO_ROTATION)),
                            Start = timing?.FirstVideo?.Start,
                            Colour = Colour(type),
                        });
                    }
                    else if (major == MF.MediaTypeAudio)
                    {
                        var details = timing?.Audio.ElementAtOrDefault(audios++);
                        int channels = type.GetInt(MF.MT_AUDIO_NUM_CHANNELS) ?? 2, rate = type.GetInt(MF.MT_AUDIO_SAMPLES_PER_SECOND) ?? 48000;
                        var track = new Track
                        {
                            ReaderStream = i, Channels = channels, SampleRate = rate,
                            Start = details?.Start, Language = details?.Language ?? "und", Name = details?.Name,
                        };
                        if (codec == MF.AudioAAC)
                        {
                            var user = type.GetBlob(UserData);
                            track.CodecId = "A_AAC";
                            track.CodecPrivate = user is { Length: > 12 } ? user[12..] : AacConfig(rate, channels);
                            track.AdtsHeader = (type.GetInt(MF.MT_AAC_PAYLOAD_TYPE) ?? 0) == 1;
                        }
                        else if (codec == MF.AudioMP3) track.CodecId = "A_MPEG/L3";
                        else { otherAudio = true; continue; }
                        tracks.Add(track);
                    }
                }
                finally { MF.Release(type); }
            }
            if (audios == 0 && MediaCheck.HasAudio(input) == true) throw new NotSupportedException("Windows can't read this video's audio track.");
            if (timing != null && timing.Audio.Count > audios) throw new NotSupportedException("Windows can't read all of this video's audio tracks.");
            if (!encode && otherAudio) throw new NotSupportedException("This video's audio can be copied, but not by Windows.");
            if (encode || !tracks.Any(t => t.Video)) return false;

            foreach (var t in tracks)
            {
                reader.SetStreamSelection(t.ReaderStream, true);
                reader.GetNativeMediaType(t.ReaderStream, 0, out var native);
                int hr = reader.SetCurrentMediaType(t.ReaderStream, IntPtr.Zero, native);
                MF.Release(native);
                if (hr < 0) return false;
            }
            tracks.Sort((a, b) => b.Video.CompareTo(a.Video));
            for (int i = 0; i < tracks.Count; i++) tracks[i].Number = i + 1;

            long duration = MF.Duration(reader) ?? 0, end = 0;
            // Windows' times ignore when tracks should start, so work out what to add to them. Matroska can't count
            // below 0: if a track starts before that (encoder warm-up), everything moves along.
            foreach (var t in tracks)
                if (t.Start is double starts && FirstTime(input, t.ReaderStream) is long reported) t.Shift = (long)Math.Round(starts * 1e7) - reported;
            long ahead = tracks.Max(t => t.Start is double starts && starts < 0 ? (long)Math.Round(-starts * 1e7) : 0);
            foreach (var t in tracks) t.Shift += ahead;
            using var mkv = new MatroskaWriter(output, tracks);
            var ended = new HashSet<int>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                reader.ReadSample(MF.SourceReaderAnyStream, 0, out int index, out int flags, out long ts, out var sample);
                try
                {
                    if ((flags & MF.ReaderError) != 0) return false;
                    if (sample != null)
                    {
                        var track = tracks.FirstOrDefault(t => t.ReaderStream == index);
                        if (track != null)
                        {
                            var data = MF.Bytes(sample);
                            bool key = !track.Video || (sample.GetInt(CleanPoint) ?? 0) != 0;
                            if (track.Video) data = ToLengthPrefixed(data, track);
                            else if (track.AdtsHeader && data.Length > 7 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0)
                                data = data[((data[1] & 1) == 0 ? 9 : 7)..];
                            long length = sample.GetSampleDuration(out long l) >= 0 ? l : 0;
                            mkv.Add(track, ts + track.Shift, length, data, key);
                            end = Math.Max(end, ts + length);
                        }
                        if (duration > 0) progress?.Report(Math.Min(0.99, (double)ts / duration));
                    }
                }
                finally { MF.Release(sample); }
                if ((flags & MF.ReaderEndOfStream) != 0)
                {
                    ended.Add(index);
                    if (tracks.All(t => ended.Contains(t.ReaderStream))) break;
                    reader.SetStreamSelection(index, false);
                }
            }
            if (duration > 0 && duration - end > Math.Max(10_000_000, duration / 50))
                throw new InvalidOperationException($"Only {MediaCheck.Clock(end)} of {MediaCheck.Clock(duration)} could be read.");
            mkv.Finish();
            return true;
        }
        catch (COMException ex)
        {
            Log.Write($"MKV copy not possible: {ex.Message}");
            return false;
        }
        finally { MF.Release(reader); }
    }

    /// <summary>The earliest time Windows gives a stream's first samples (pictures aren't stored in the order they're shown).</summary>
    static long? FirstTime(string input, int stream)
    {
        IMFSourceReader? reader = null;
        try
        {
            MF.MFCreateSourceReaderFromURL(input, null, out reader);
            reader.SetStreamSelection(MF.SourceReaderAllStreams, false);
            reader.SetStreamSelection(stream, true);
            long? first = null;
            for (int i = 0; i < 32; i++)
            {
                reader.ReadSample(stream, 0, out _, out int flags, out long time, out var sample);
                if (sample != null) first = Math.Min(first ?? time, time);
                MF.Release(sample);
                if ((flags & (MF.ReaderError | MF.ReaderEndOfStream)) != 0) break;
            }
            return first;
        }
        catch (COMException) { return null; }
        finally { MF.Release(reader); }
    }

    // ------------------------------------------------------------------ codec details

    /// <summary>Windows' colour attributes as the standard code points Matroska uses (0 where unknown).</summary>
    static (int matrix, int range, int transfer, int primaries) Colour(IMFMediaType type) => (
        type.GetInt(MF.MT_YUV_MATRIX) switch { 1 => 1, 2 => 6, 3 => 7, 4 or 5 => 9, _ => 0 },
        type.GetInt(MF.MT_VIDEO_NOMINAL_RANGE) switch { 1 => 2, 2 => 1, _ => 0 },
        type.GetInt(MF.MT_TRANSFER_FUNCTION) switch { 1 => 8, 4 => 4, 5 or 11 => 1, 6 => 7, 7 => 13, 8 => 5, 9 => 9, 10 => 10, 12 or 13 => 14, 15 => 16, 16 => 18, _ => 0 },
        type.GetInt(MF.MT_VIDEO_PRIMARIES) switch { 2 => 1, 3 => 4, 4 => 5, 5 or 8 => 6, 6 => 7, 9 => 9, 10 => 10, 11 => 11, _ => 0 });

    static List<byte[]> SplitAnnexB(byte[] data)
    {
        var nals = new List<byte[]>();
        int start = -1;
        for (int i = 0; i + 3 <= data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                {
                    int end = i;
                    while (end > start && data[end - 1] == 0) end--;
                    nals.Add(data[start..end]);
                }
                start = i + 3;
                i += 2;
            }
        }
        if (start >= 0 && start < data.Length) nals.Add(data[start..]);
        return nals;
    }

    static bool LooksAnnexB(byte[] d) =>
        d.Length > 4 && ((d[0] == 0 && d[1] == 0 && d[2] == 1) || (d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 1));

    /// <summary>Matroska stores H.264 as length-prefixed NAL units; Windows hands them out with start codes.</summary>
    static byte[] ToLengthPrefixed(byte[] data, Track track)
    {
        if (!LooksAnnexB(data)) return data;
        var nals = SplitAnnexB(data);
        if (track.CodecPrivate == null)
        {
            var sps = nals.FirstOrDefault(n => n.Length > 3 && (n[0] & 0x1F) == 7);
            var pps = nals.FirstOrDefault(n => n.Length > 0 && (n[0] & 0x1F) == 8);
            if (sps != null && pps != null) track.CodecPrivate = AvcConfig(sps, pps);
        }
        using var ms = new MemoryStream(data.Length + nals.Count * 4);
        Span<byte> len = stackalloc byte[4];
        foreach (var nal in nals)
        {
            int type = nal.Length > 0 ? nal[0] & 0x1F : 0;
            if (type == 9) continue; // access unit delimiters aren't used in Matroska
            BinaryPrimitives.WriteInt32BigEndian(len, nal.Length);
            ms.Write(len);
            ms.Write(nal);
        }
        return ms.ToArray();
    }

    static byte[]? AvcConfig(byte[]? sequenceHeader)
    {
        if (sequenceHeader == null || !LooksAnnexB(sequenceHeader)) return null;
        var nals = SplitAnnexB(sequenceHeader);
        var sps = nals.FirstOrDefault(n => n.Length > 3 && (n[0] & 0x1F) == 7);
        var pps = nals.FirstOrDefault(n => n.Length > 0 && (n[0] & 0x1F) == 8);
        return sps != null && pps != null ? AvcConfig(sps, pps) : null;
    }

    /// <summary>AVCDecoderConfigurationRecord (ISO 14496-15).</summary>
    static byte[] AvcConfig(byte[] sps, byte[] pps)
    {
        var c = new List<byte> { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1, (byte)(sps.Length >> 8), (byte)sps.Length };
        c.AddRange(sps);
        c.Add(1);
        c.Add((byte)(pps.Length >> 8));
        c.Add((byte)pps.Length);
        c.AddRange(pps);
        return [.. c];
    }

    /// <summary>AudioSpecificConfig for AAC-LC.</summary>
    static byte[] AacConfig(int rate, int channels)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int index = Math.Max(0, Array.IndexOf(rates, rate));
        int config = 2 << 11 | index << 7 | channels << 3;
        return [(byte)(config >> 8), (byte)config];
    }

    // ------------------------------------------------------------------ EBML writing

    sealed class MatroskaWriter : IDisposable
    {
        const long TimestampScale = 1_000_000; // block timestamps in milliseconds
        readonly FileStream _file;
        readonly List<Track> _tracks;
        readonly long _segmentSizePos, _segmentDataStart, _seekHeadPos, _infoPos, _durationPos;
        long _tracksPos, _clusterStart = -1, _clusterTime;
        double _maxEndMs;
        readonly List<(long timeMs, long clusterPos)> _cues = [];
        bool _tracksWritten;

        public MatroskaWriter(string path, List<Track> tracks)
        {
            _file = File.Create(path);
            _tracks = tracks;
            Write(Element(0x1A45DFA3,
                UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
                Str(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2)));

            WriteId(0x18538067);
            _segmentSizePos = _file.Position;
            _file.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // unknown size, patched at the end
            _segmentDataStart = _file.Position;

            // SeekHead with fixed-width positions, patched once everything is written.
            _seekHeadPos = _file.Position;
            Write(SeekHead(0, 0, 0));

            _infoPos = _file.Position;
            var info = Element(0x1549A966,
                UInt(0x2AD7B1, TimestampScale),
                Str(0x4D80, "Convert Wheel"), Str(0x5741, "Convert Wheel"),
                Float(0x4489, 0));
            Write(info);
            _durationPos = _file.Position - 8; // the float is the last 8 bytes of Info
        }

        static byte[] SeekHead(long info, long tracks, long cues) => Element(0x114D9B74,
            Element(0x4DBB, Bin(0x53AB, [0x15, 0x49, 0xA9, 0x66]), FixedUInt(0x53AC, info)),
            Element(0x4DBB, Bin(0x53AB, [0x16, 0x54, 0xAE, 0x6B]), FixedUInt(0x53AC, tracks)),
            Element(0x4DBB, Bin(0x53AB, [0x1C, 0x53, 0xBB, 0x6B]), FixedUInt(0x53AC, cues)));

        void WriteTracks()
        {
            _tracksPos = _file.Position;
            var entries = _tracks.Select(t =>
            {
                var parts = new List<byte[]>
                {
                    UInt(0xD7, (ulong)t.Number), UInt(0x73C5, (ulong)Random.Shared.NextInt64(1, long.MaxValue)),
                    UInt(0x83, t.Video ? 1UL : 2UL), Str(0x86, t.CodecId), UInt(0x9C, 0), Str(0x22B59C, t.Language),
                };
                if (t.Name != null) parts.Add(Str(0x536E, t.Name));
                if (!t.Video && _tracks.First(x => !x.Video) != t) parts.Add(UInt(0x88, 0)); // only the first is the default
                if (t.CodecPrivate != null) parts.Add(Bin(0x63A2, t.CodecPrivate));
                if (t.Video)
                {
                    var video = new List<byte[]> { UInt(0xB0, (ulong)t.Width), UInt(0xBA, (ulong)t.Height) };
                    if (t.DisplayWidth > 0) video.AddRange([UInt(0x54B0, (ulong)t.DisplayWidth), UInt(0x54BA, (ulong)t.DisplayHeight)]);
                    var (matrix, range, transfer, primaries) = t.Colour;
                    if (matrix + range + transfer + primaries > 0)
                        video.Add(Element(0x55B0, [.. new (uint id, int value)[] { (0x55B1, matrix), (0x55B9, range), (0x55BA, transfer), (0x55BB, primaries) }
                            .Where(c => c.value > 0).Select(c => UInt(c.id, (ulong)c.value))]));
                    // Phone rotation: Matroska expresses it as the projection's roll angle (counter-clockwise).
                    if (t.Rotation != 0) video.Add(Element(0x7670, UInt(0x7671, 0), Float(0x7675, -t.Rotation)));
                    parts.Add(Element(0xE0, [.. video]));
                }
                else parts.Add(Element(0xE1, Float(0xB5, t.SampleRate), UInt(0x9F, (ulong)t.Channels)));
                return Element(0xAE, [.. parts]);
            }).ToArray();
            Write(Element(0x1654AE6B, entries));
            _tracksWritten = true;
        }

        public void Add(Track track, long time100ns, long duration100ns, byte[] data, bool keyframe)
        {
            if (!_tracksWritten) WriteTracks(); // after the first sample, so H.264 config found in-band is included
            long ms = Math.Max(0, (time100ns + 5000) / 10_000);
            bool videoKey = track.Video && keyframe;
            if (_clusterStart < 0 || ms - _clusterTime > 30_000 || ms - _clusterTime < -30_000 || (videoKey && ms - _clusterTime >= 1000))
                StartCluster(ms, videoKey);

            var block = new byte[4 + data.Length];
            block[0] = (byte)(0x80 | track.Number);
            BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(1), (short)(ms - _clusterTime));
            block[3] = (byte)(keyframe ? 0x80 : 0x00);
            data.CopyTo(block, 4);
            WriteId(0xA3);
            WriteSize(block.Length);
            _file.Write(block);

            _maxEndMs = Math.Max(_maxEndMs, (time100ns + duration100ns) / 10_000.0);
        }

        void StartCluster(long ms, bool cue)
        {
            FinishCluster();
            _clusterStart = _file.Position;
            _clusterTime = Math.Max(0, ms);
            WriteId(0x1F43B675);
            _file.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]); // size patched in FinishCluster
            Write(UInt(0xE7, (ulong)_clusterTime));
            if (cue || _cues.Count == 0) _cues.Add((_clusterTime, _clusterStart - _segmentDataStart));
        }

        void FinishCluster()
        {
            if (_clusterStart < 0) return;
            long end = _file.Position;
            _file.Position = _clusterStart + 4;
            _file.Write(SizeBytes8(end - _clusterStart - 12));
            _file.Position = end;
        }

        public void Dispose() => _file.Dispose();

        /// <summary>Writes the index and fills in the sizes left open.</summary>
        public void Finish()
        {
            if (!_tracksWritten) WriteTracks();
            FinishCluster();

            long cuesPos = _file.Position;
            var videoTrack = _tracks.FirstOrDefault(t => t.Video)?.Number ?? 1;
            Write(Element(0x1C53BB6B, _cues.Select(c => Element(0xBB,
                UInt(0xB3, (ulong)c.timeMs),
                Element(0xB7, UInt(0xF7, (ulong)videoTrack), UInt(0xF1, (ulong)c.clusterPos)))).ToArray()));

            long end = _file.Position;
            _file.Position = _segmentSizePos;
            _file.Write(SizeBytes8(end - _segmentDataStart));
            _file.Position = _seekHeadPos;
            _file.Write(SeekHead(_infoPos - _segmentDataStart, _tracksPos - _segmentDataStart, cuesPos - _segmentDataStart));
            _file.Position = _durationPos;
            var duration = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(duration, _maxEndMs);
            _file.Write(duration);
        }

        void Write(byte[] bytes) => _file.Write(bytes);
        void WriteId(uint id) => _file.Write(IdBytes(id));
        void WriteSize(long size) => _file.Write(SizeBytes(size));

        // EBML primitives
        static byte[] IdBytes(uint id) => id switch
        {
            > 0xFFFFFF => [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id],
            > 0xFFFF => [(byte)(id >> 16), (byte)(id >> 8), (byte)id],
            > 0xFF => [(byte)(id >> 8), (byte)id],
            _ => [(byte)id],
        };

        static byte[] SizeBytes(long size)
        {
            for (int len = 1; len <= 8; len++)
                if (size < (1L << (7 * len)) - 1)
                {
                    var b = new byte[len];
                    for (int i = 0; i < len; i++) b[len - 1 - i] = (byte)(size >> (8 * i));
                    b[0] |= (byte)(0x80 >> (len - 1));
                    return b;
                }
            throw new InvalidOperationException("Element too large.");
        }

        static byte[] SizeBytes8(long size)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, size);
            b[0] = 0x01;
            return b;
        }

        static byte[] Element(uint id, params byte[][] children)
        {
            int length = children.Sum(c => c.Length);
            using var ms = new MemoryStream();
            ms.Write(IdBytes(id));
            ms.Write(SizeBytes(length));
            foreach (var c in children) ms.Write(c);
            return ms.ToArray();
        }

        static byte[] Bin(uint id, byte[] data) => Element(id, data);
        static byte[] Str(uint id, string s) => Element(id, Encoding.UTF8.GetBytes(s));

        static byte[] UInt(uint id, ulong value)
        {
            int len = 1;
            while (len < 8 && value >> (8 * len) != 0) len++;
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[len - 1 - i] = (byte)(value >> (8 * i));
            return Element(id, b);
        }

        static byte[] FixedUInt(uint id, long value)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, value);
            return Element(id, b);
        }

        static byte[] Float(uint id, double value)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(b, value);
            return Element(id, b);
        }
    }
}
