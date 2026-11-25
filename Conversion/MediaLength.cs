using System.Buffers.Binary;

namespace ConvertWheel.Conversion;

/// <summary>
/// Reads what a sound or video file says about itself, so a conversion that quietly stopped early (a
/// half-downloaded or damaged file) can be told apart from a complete one. Anything that can't be read for
/// certain is "unknown", never "damaged".
/// </summary>
internal static class MediaLength
{
    /// <param name="Seconds">The length the file claims to have, or null.</param>
    /// <param name="CutOff">The file says it should be longer, in bytes, than it is.</param>
    /// <param name="Whole">The file says how big it should be, and it is.</param>
    public sealed record Claims(double? Seconds, bool CutOff, bool Whole = false)
    {
        public static readonly Claims None = new(null, false);
    }

    /// <summary>Short enough to notice: more than a second, and more than 2%.</summary>
    public static bool MuchShorter(double actual, double expected) => expected - actual > Math.Max(1, expected * 0.02);

    /// <summary>0:04, 1:02:03…</summary>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds + 0.05)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static Claims Read(string path)
    {
        try
        {
            using var file = Open(path);
            var head = Bytes(file, 0, 16);
            if (head.Length < 16) return Claims.None;
            if (Is(head, 4, "ftyp")) return Mp4(file);
            if (U32(head, 0) == 0x1A45DFA3) return Matroska(file);
            if (Is(head, 0, "RIFF") || Is(head, 0, "RF64")) return Is(head, 8, "WAVE") ? Wav(file) : Is(head, 8, "AVI ") ? Avi(file) : Claims.None;
            if (Is(head, 0, "FORM") && (Is(head, 8, "AIFF") || Is(head, 8, "AIFC"))) return Aiff(file);
            if (U32(head, 0) == 0x3026B275) return Asf(file);
            if (Is(head, 0, "fLaC")) return Flac(file);
            if (Is(head, 0, "caff")) return Caf(file);
            if (Is(head, 0, "OggS")) return OggPages(file);
            return Mp3(file, guess: false);
        }
        catch (Exception ex) when (IsReadError(ex)) { return Claims.None; }
    }

    /// <summary>How long a file that was just made really is, or null if its type doesn't say.</summary>
    public static double? MadeLength(string path, string ext)
    {
        try
        {
            switch (ext)
            {
                case "ogg" or "opus":
                    using (var file = Open(path)) return Ogg(file);
                case "aac":
                    using (var file = Open(path)) return Adts(file);
                case "mp3":
                    using (var file = Open(path)) return Mp3(file, guess: true).Seconds;
                default:
                    return Read(path).Seconds;
            }
        }
        catch (Exception ex) when (IsReadError(ex)) { return null; }
    }

    static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or IndexOutOfRangeException;

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    // ------------------------------------------------------------------ containers

    static Claims Sized(double? seconds, bool cutOff) => new(seconds, cutOff, !cutOff);

    static Claims Mp4(FileStream file)
    {
        double? seconds = null;
        long at = 0;
        bool media = false;
        for (int n = 0; n < 200_000 && at + 8 <= file.Length; n++)
        {
            if (Box(file, at, file.Length) is not { } box) break;
            if (box.end > file.Length) return new Claims(seconds, box.type is "mdat" or "moov" or "moof" or "free" or "skip" or "wide");
            media |= box.type == "mdat";
            if (box.type == "moov")
            {
                for (long child = box.body; child + 8 <= box.end && Box(file, child, box.end) is { } inner; child = inner.end)
                {
                    if (inner.type != "mvhd") continue;
                    var b = Bytes(file, inner.body, 32);
                    if (b.Length < 32) break;
                    double scale = b[0] == 1 ? U32(b, 20) : U32(b, 12);
                    double length = b[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24)) : U32(b, 16);
                    if (scale > 0 && length > 0 && length != uint.MaxValue && length < 1e15) seconds = length / scale;
                    break;
                }
            }
            at = box.end;
        }
        return new Claims(seconds, false, media && at >= file.Length - 8);
    }

    static (string type, long body, long end)? Box(FileStream file, long at, long limit)
    {
        var h = Bytes(file, at, 16);
        if (h.Length < 8) return null;
        for (int i = 4; i < 8; i++) if (h[i] is < 0x20 or > 0x7E && h[i] != 0xA9) return null;
        var type = System.Text.Encoding.Latin1.GetString(h, 4, 4);
        long size = U32(h, 0);
        if (size == 0) return (type, at + 8, limit);
        if (size != 1) return size < 8 ? null : (type, at + 8, at + size);
        if (h.Length < 16) return null;
        ulong big = BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
        return big is < 16 or > 1L << 50 ? null : (type, at + 16, at + (long)big);
    }

    static Claims Matroska(FileStream file)
    {
        long at = 0;
        var header = Element(file, at);
        if (header is not { } ebml || ebml.end is not { } afterHeader) return Claims.None;
        if (Element(file, afterHeader) is not { id: 0x18538067 } segment) return Claims.None;
        bool cutOff = segment.end > file.Length;
        double? seconds = null;
        at = segment.body;
        for (int n = 0; n < 1000 && Element(file, at) is { } element; n++)
        {
            if (element.id == 0x1F43B675 || element.end is not { } end || end > file.Length) break; // the media itself starts here
            if (element.id == 0x1549A966)
            {
                double scale = 1_000_000, length = 0;
                for (long child = element.body; child < end && Element(file, child) is { end: { } childEnd } inner; child = childEnd)
                {
                    int size = (int)Math.Min(8, childEnd - inner.body);
                    if (inner.id == 0x2AD7B1) scale = Bytes(file, inner.body, size).Aggregate(0UL, (v, b) => v << 8 | b);
                    else if (inner.id == 0x4489 && Bytes(file, inner.body, size) is var b)
                        length = b.Length switch { 4 => BinaryPrimitives.ReadSingleBigEndian(b), 8 => BinaryPrimitives.ReadDoubleBigEndian(b), _ => 0 };
                }
                if (length > 0 && double.IsFinite(length) && scale > 0) seconds = length * scale / 1e9;
            }
            at = end;
        }
        return segment.end != null ? Sized(seconds, cutOff) : new Claims(seconds, false);
    }

    /// <returns>end is null when the size isn't known (a live recording).</returns>
    static (uint id, long body, long? end)? Element(FileStream file, long at)
    {
        var h = Bytes(file, at, 12);
        if (h.Length < 2 || h[0] < 0x10) return null;
        int idLength = System.Numerics.BitOperations.LeadingZeroCount(h[0]) - 23;
        if (h.Length <= idLength || h[idLength] == 0) return null;
        uint id = 0;
        for (int i = 0; i < idLength; i++) id = id << 8 | h[i];
        int sizeLength = System.Numerics.BitOperations.LeadingZeroCount(h[idLength]) - 23;
        if (h.Length < idLength + sizeLength) return null;
        long size = h[idLength] & (0xFF >> sizeLength);
        bool unknown = size == 0xFF >> sizeLength;
        for (int i = 1; i < sizeLength; i++)
        {
            size = size << 8 | h[idLength + i];
            unknown &= h[idLength + i] == 0xFF;
        }
        long body = at + idLength + sizeLength;
        return (id, body, unknown ? null : body + size);
    }

    /// <summary>Programs that stream their output can't go back and fill in the real size.</summary>
    static bool Placeholder(uint size) => size is 0 or >= 0x7FFF0000;

    static Claims Wav(FileStream file)
    {
        double rate = 0;
        long? large = null;
        long at = 12;
        for (int n = 0; n < 1000 && Bytes(file, at, 8) is { Length: 8 } h; n++)
        {
            uint size = L32(h, 4);
            if (Is(h, 0, "ds64")) large = (long)BinaryPrimitives.ReadUInt64LittleEndian(Bytes(file, at + 8, 16).AsSpan(8));
            else if (Is(h, 0, "fmt ")) rate = L32(Bytes(file, at + 8, 12), 8);
            else if (Is(h, 0, "data"))
            {
                long bytes = size == uint.MaxValue && large is { } l ? l : Placeholder(size) ? 0 : size;
                if (bytes <= 0 || rate <= 0) return Claims.None;
                return Sized(bytes / rate, (at + 8 + bytes - file.Length) / rate > 0.05);
            }
            at += 8 + size + (size & 1);
        }
        return Claims.None;
    }

    static Claims Avi(FileStream file)
    {
        double? seconds = null;
        var list = Bytes(file, 12, 64);
        if (list.Length == 64 && Is(list, 0, "LIST") && Is(list, 8, "hdrl") && Is(list, 12, "avih"))
        {
            double perFrame = L32(list, 20), frames = L32(list, 36);
            if (perFrame is > 0 and < 10_000_000 && frames is > 0 and < 100_000_000) seconds = perFrame * frames / 1e6;
        }
        bool whole = false;
        // One RIFF block, then more of them for long videos.
        long at = 0;
        for (int n = 0; n < 1000 && Bytes(file, at, 8) is { Length: 8 } h && Is(h, 0, "RIFF"); n++)
        {
            uint size = L32(h, 4);
            if (Placeholder(size)) break;
            if (at + size > file.Length + 16) return new Claims(seconds, true);
            whole = true;
            at += 8 + size + (size & 1);
        }
        return new Claims(seconds, false, whole);
    }

    static Claims Aiff(FileStream file)
    {
        double? seconds = null;
        bool cutOff = false, sized = false;
        long at = 12;
        for (int n = 0; n < 1000 && Bytes(file, at, 8) is { Length: 8 } h; n++)
        {
            long size = U32(h, 4);
            if (Is(h, 0, "COMM") && Bytes(file, at + 8, 18) is { Length: 18 } c)
            {
                double frames = U32(c, 2);
                // An 80-bit floating point sample rate.
                double rate = BinaryPrimitives.ReadUInt64BigEndian(c.AsSpan(10)) * Math.Pow(2, (U16(c, 8) & 0x7FFF) - 16383 - 63);
                if (frames is > 0 and < uint.MaxValue && rate is > 0 and < 10_000_000) seconds = frames / rate;
            }
            else if (Is(h, 0, "SSND") && !Placeholder((uint)size))
            {
                sized = true;
                cutOff = at + 8 + size > file.Length + 8;
            }
            at += 8 + size + (size & 1);
        }
        return sized ? Sized(seconds, cutOff) : new Claims(seconds, false);
    }

    static Claims Asf(FileStream file)
    {
        double? seconds = null;
        long at = 30;
        for (int n = 0; n < 100 && Bytes(file, at, 24) is { Length: 24 } h; n++)
        {
            long size = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(16));
            uint id = U32(h, 0);
            if (id == 0xA1DCAB8C && Bytes(file, at + 24, 68) is { Length: 68 } p) // file properties
            {
                bool live = (p[64] & 1) != 0;
                double length = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(40)) / 1e7 - BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(56)) / 1e3;
                if (!live && length is > 0 and < 1e7) seconds = length;
            }
            if (id == 0x3626B275) // the media data
                return size is > 24 and < 1L << 50 ? Sized(seconds, at + size > file.Length + 8) : new Claims(seconds, false);
            if (size < 24) break;
            at += size;
        }
        return new Claims(seconds, false);
    }

    static Claims Flac(FileStream file)
    {
        var b = Bytes(file, 4, 38);
        if (b.Length < 38 || (b[0] & 0x7F) != 0) return Claims.None;
        uint rate = U32(b, 14) >> 12;
        long samples = (long)(b[17] & 0xF) << 32 | U32(b, 18);
        return rate > 0 && samples > 0 ? new Claims((double)samples / rate, false) : Claims.None;
    }

    static Claims Caf(FileStream file)
    {
        double perByte = 0;
        long at = 8;
        for (int n = 0; n < 1000 && Bytes(file, at, 12) is { Length: 12 } h; n++)
        {
            long size = BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(4));
            if (Is(h, 0, "desc") && Bytes(file, at + 12, 24) is { Length: 24 } d)
            {
                double rate = BinaryPrimitives.ReadDoubleBigEndian(d);
                if (rate > 0 && U32(d, 16) > 0) perByte = U32(d, 20) / rate / U32(d, 16);
            }
            if (size < 0) break; // "until the end"
            if (Is(h, 0, "data")) return Sized(perByte > 0 && size > 4 ? (size - 4) * perByte : null, at + 12 + size > file.Length + 8);
            at += 12 + size;
        }
        return Claims.None;
    }

    // ------------------------------------------------------------------ bare sound streams

    static readonly int[] BitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    static readonly int[] BitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    static readonly int[] Rates = [44100, 48000, 32000];

    /// <param name="guess">Work it out from the bitrate when there's no length inside (fine for a constant bitrate).</param>
    static Claims Mp3(FileStream file, bool guess)
    {
        long at = 0;
        var tag = Bytes(file, 0, 10);
        if (tag.Length == 10 && Is(tag, 0, "ID3")) at = 10 + ((tag[6] & 0x7F) << 21 | (tag[7] & 0x7F) << 14 | (tag[8] & 0x7F) << 7 | tag[9] & 0x7F);
        var b = Bytes(file, at, 4096);
        int i = 0;
        while (i + 4 <= b.Length && !(b[i] == 0xFF && (b[i + 1] & 0xE6) == 0xE2 && (b[i + 2] & 0xF0) is not 0xF0 and not 0 && (b[i + 2] & 0x0C) != 0x0C)) i++;
        if (i + 200 > b.Length || i > 0 && !guess) return Claims.None; // only trust a file that starts with a frame
        int version = b[i + 1] >> 3 & 3; // 3: MPEG 1, 2: MPEG 2, 0: MPEG 2.5
        if (version == 1) return Claims.None;
        int rate = Rates[b[i + 2] >> 2 & 3] / (version == 3 ? 1 : version == 2 ? 2 : 4);
        int samples = version == 3 ? 1152 : 576;
        bool mono = b[i + 3] >> 6 == 3;
        int info = i + 4 + (version == 3 ? mono ? 17 : 32 : mono ? 9 : 17);
        if (Is(b, info, "Xing") || Is(b, info, "Info"))
        {
            uint flags = U32(b, info + 4);
            double? seconds = (flags & 1) != 0 && U32(b, info + 8) is > 0 and < 100_000_000 and var frames ? frames * (double)samples / rate : null;
            bool cutOff = (flags & 3) == 3 && at + i + U32(b, info + 12) > file.Length + 8192;
            return new Claims(seconds, cutOff);
        }
        int bitrate = (version == 3 ? BitratesV1 : BitratesV2)[b[i + 2] >> 4] * 1000;
        return guess && bitrate > 0 ? new Claims((file.Length - at - i) * 8.0 / bitrate, false) : Claims.None;
    }

    static double? Ogg(FileStream file)
    {
        var head = Bytes(file, 0, 256);
        double rate, skip = 0;
        int opus = head.AsSpan().IndexOf("OpusHead"u8), vorbis = head.AsSpan().IndexOf("\u0001vorbis"u8);
        if (opus >= 0) (rate, skip) = (48000, BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(opus + 10)));
        else if (vorbis >= 0) rate = L32(head, vorbis + 12);
        else return null;
        long start = Math.Max(0, file.Length - 70_000);
        var tail = Bytes(file, start, (int)(file.Length - start));
        for (int at = tail.AsSpan().LastIndexOf("OggS"u8); at >= 0; at = tail.AsSpan(0, at).LastIndexOf("OggS"u8))
        {
            if (at + 14 > tail.Length) continue;
            long position = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(at + 6));
            if (position >= 0 && rate > 0) return Math.Max(0, (position - skip) / rate);
        }
        return null;
    }

    /// <summary>Ogg doesn't say how long it is, but a page that runs past the end of the file was cut off.</summary>
    static Claims OggPages(FileStream file)
    {
        long start = Math.Max(0, file.Length - 70_000); // more than a page
        var tail = Bytes(file, start, (int)(file.Length - start));
        for (int at = tail.AsSpan().IndexOf("OggS"u8); at >= 0 && at + 4 <= tail.Length; )
        {
            int segments = at + 27 <= tail.Length ? tail[at + 26] : -1;
            if (segments < 0 || at + 27 + segments > tail.Length) return new Claims(null, true);
            int size = 27 + segments;
            for (int i = 0; i < segments; i++) size += tail[at + 27 + i];
            if (at + size > tail.Length) return new Claims(null, true);
            if (at + size == tail.Length) return new Claims(null, false, true);
            // "OggS" inside sound data isn't a page: one page is followed by another.
            at = Is(tail, at + size, "OggS") ? at + size : tail.AsSpan(at + 4).IndexOf("OggS"u8) is >= 0 and var next ? at + 4 + next : -1;
        }
        return Claims.None;
    }

    static double? Adts(FileStream file)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        long frames = 0;
        int rate = 0;
        var h = new byte[7];
        for (long at = 0; at + 7 <= file.Length; frames++)
        {
            file.Position = at;
            file.ReadExactly(h);
            if (h[0] != 0xFF || (h[1] & 0xF6) != 0xF0) break;
            if (rate == 0 && (h[2] >> 2 & 0xF) is < 13 and var index) rate = rates[index];
            int size = (h[3] & 3) << 11 | h[4] << 3 | h[5] >> 5;
            if (size < 7) break;
            at += size;
        }
        return rate > 0 ? frames * 1024.0 / rate : null;
    }

    // ------------------------------------------------------------------ bytes

    /// <summary>Up to that many bytes; fewer at the end of the file.</summary>
    static byte[] Bytes(FileStream file, long at, int count)
    {
        if (at < 0 || at >= file.Length) return [];
        var data = new byte[(int)Math.Min(count, file.Length - at)];
        file.Position = at;
        int n = file.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        return n == data.Length ? data : data[..n];
    }

    static bool Is(byte[] data, int at, string text)
    {
        if (at < 0 || at + text.Length > data.Length) return false;
        for (int i = 0; i < text.Length; i++) if (data[at + i] != text[i]) return false;
        return true;
    }

    static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
    static uint L32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    static int U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
}
