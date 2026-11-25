using System.Buffers.Binary;

namespace ConvertWheel.Conversion.Media;

/// <summary>
/// Quick looks at a media file's own structure. Windows plays a file that was cut short (a broken download,
/// a full disk) as if it simply ended early, and can't always see every track, so ask the container.
/// </summary>
internal static class MediaCheck
{
    /// <summary>Throws when the container says there should be more file than there is.</summary>
    public static void EnsureComplete(string path)
    {
        long expected;
        long length;
        try
        {
            using var f = File.OpenRead(path);
            length = f.Length;
            expected = ExpectedLength(f);
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException) { return; }
        if (expected > length)
            throw new InvalidOperationException($"This file is cut short: {length / 1024:N0} of {expected / 1024:N0} KB are there ({100.0 * length / expected:0}%).");
    }

    static long ExpectedLength(FileStream f)
    {
        var h = new byte[16];
        if (f.ReadAtLeast(h, 16, false) < 16) return 0;
        bool Has(int at, ReadOnlySpan<byte> bytes) => h.AsSpan(at, bytes.Length).SequenceEqual(bytes);

        if (Has(0, "RIFF"u8) && (Has(8, "WAVE"u8) || Has(8, "AVI "u8)))
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            // 0 and the largest values mean "unknown": recorded live, or bigger than RIFF can say.
            return size is 0 or >= 0xFFFFFF00 ? 0 : size;
        }
        if (Has(4, "ftyp"u8) || Has(4, "moov"u8) || Has(4, "mdat"u8) || Has(4, "wide"u8) || Has(4, "free"u8) || Has(4, "skip"u8))
        {
            long pos = 0;
            while (Box(f, pos) is { } box)
            {
                if (box.size == 0) return 0; // "to the end of the file"
                if (pos + box.size > f.Length) return box.type is "mdat" or "moov" ? pos + box.size : 0;
                pos += box.size;
            }
            return 0;
        }
        if (Has(0, [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11]))
        {
            // ASF (WMA/WMV): the header object, then the data object with its size.
            long data = BinaryPrimitives.ReadInt64LittleEndian(Bytes(f, 16, 8));
            if (data < 24 || data > f.Length - 24) return 0;
            var d = Bytes(f, data, 24);
            if (!d.AsSpan(0, 4).SequenceEqual(new byte[] { 0x36, 0x26, 0xB2, 0x75 })) return 0;
            long size = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(16));
            return size <= 50 ? 0 : data + size;
        }
        if (Has(0, "ID3"u8) || h[0] == 0xFF && (h[1] & 0xE6) == 0xE2)
        {
            // MP3: encoders put the audio's size in an (otherwise silent) first frame, marked Xing or Info.
            long at = Has(0, "ID3"u8) ? 10 + (h[6] << 21 | h[7] << 14 | h[8] << 7 | h[9]) : 0;
            if (at + 200 > f.Length) return 0;
            var frame = Bytes(f, at, 200);
            if (frame[0] != 0xFF || (frame[1] & 0xE6) != 0xE2) return 0;
            bool mpeg1 = (frame[1] & 0x18) == 0x18, mono = (frame[3] & 0xC0) == 0xC0;
            int tag = 4 + (mpeg1 ? mono ? 17 : 32 : mono ? 9 : 17);
            if (!frame.AsSpan(tag, 4).SequenceEqual("Xing"u8) && !frame.AsSpan(tag, 4).SequenceEqual("Info"u8)) return 0;
            int flags = frame[tag + 7];
            if ((flags & 2) == 0) return 0;
            long size = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(tag + 8 + ((flags & 1) != 0 ? 4 : 0)));
            // Some editors forget to update it, so only believe a clear difference.
            return at + size > f.Length + f.Length / 20 ? at + size : 0;
        }
        if (Has(0, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            // Matroska/WebM: the segment's size, unless it was written live.
            long pos = 0;
            while (pos < Math.Min(f.Length, 1 << 20) && Element(f, pos) is { } e)
            {
                if (e.id == 0x18538067) return e.size < 0 ? 0 : e.data + e.size;
                if (e.size < 0) return 0;
                pos = e.data + e.size;
            }
            return 0;
        }
        return 0;
    }

    public static bool IsMatroska(string path)
    {
        using var f = File.OpenRead(path);
        var h = new byte[4];
        return f.ReadAtLeast(h, 4, false) == 4 && h.AsSpan().SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
    }

    /// <summary>Whether a file has an audio track; null when its structure doesn't make that plain.</summary>
    public static bool? HasAudio(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var h = new byte[16];
            if (f.ReadAtLeast(h, 16, false) < 16) return null;
            bool Has(int at, ReadOnlySpan<byte> bytes) => h.AsSpan(at, bytes.Length).SequenceEqual(bytes);
            if (Has(0, "RIFF"u8) && Has(8, "AVI "u8))
            {
                var head = new byte[(int)Math.Min(f.Length, 1 << 20)];
                f.Position = 0;
                f.ReadExactly(head);
                return head.AsSpan().IndexOf("auds"u8) >= 0;
            }
            if (Has(4, "ftyp"u8) || Has(4, "moov"u8) || Has(4, "wide"u8) || Has(4, "mdat"u8))
            {
                for (long pos = 0; Box(f, pos) is { size: > 0 } box; pos += box.size)
                    if (box.type == "moov")
                    {
                        if (box.size > 256 << 20) return null;
                        var moov = Bytes(f, pos, (int)Math.Min(box.size, f.Length - pos));
                        // Each track's handler box: "hdlr", version, a type (QuickTime: "mhlr"), then "soun" or "vide".
                        for (int at = 0; at + 12 < moov.Length; at++)
                        {
                            int i = moov.AsSpan(at).IndexOf("hdlr"u8);
                            if (i < 0 || at + i + 16 > moov.Length) break;
                            at += i;
                            if (moov.AsSpan(at + 12, 4).SequenceEqual("soun"u8)) return true;
                        }
                        return false;
                    }
                return null;
            }
            if (Has(0, [0x1A, 0x45, 0xDF, 0xA3]))
            {
                for (long pos = 0; Element(f, pos) is { } e;)
                {
                    if (e.id == 0x18538067) { pos = e.data; continue; } // look inside the segment
                    if (e.id == 0x1F43B675 || e.size < 0) return null; // the frames begin
                    if (e.id == 0x1654AE6B)
                    {
                        for (long track = e.data; track < e.data + e.size && Element(f, track) is { size: >= 0 } t; track = t.data + t.size)
                            for (long field = t.data; field < t.data + t.size && Element(f, field) is { size: >= 0 } x; field = x.data + x.size)
                                if (x.id == 0x83 && x.size == 1 && Bytes(f, x.data, 1)[0] == 2) return true;
                        return false;
                    }
                    pos = e.data + e.size;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { }
        return null;
    }

    /// <summary>A time in 100-ns units as h:mm:ss.</summary>
    public static string Clock(long time) => TimeSpan.FromTicks(time).ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

    static byte[] Bytes(FileStream f, long at, int count)
    {
        var b = new byte[count];
        f.Position = at;
        f.ReadExactly(b);
        return b;
    }

    static (string type, long size)? Box(FileStream f, long pos)
    {
        if (pos < 0 || pos + 8 > f.Length) return null;
        var b = Bytes(f, pos, 8);
        long size = BinaryPrimitives.ReadUInt32BigEndian(b);
        if (size == 1)
        {
            if (pos + 16 > f.Length) return null;
            size = BinaryPrimitives.ReadInt64BigEndian(Bytes(f, pos + 8, 8));
        }
        return size is 0 or >= 8 && size < long.MaxValue - pos ? (System.Text.Encoding.Latin1.GetString(b, 4, 4), size) : null;
    }

    /// <summary>An EBML element's ID, where its contents start, and their size (-1 when unknown).</summary>
    static (uint id, long data, long size)? Element(FileStream f, long pos)
    {
        if (pos < 0 || pos + 2 > f.Length) return null;
        var b = new byte[12];
        f.Position = pos;
        int n = f.ReadAtLeast(b, b.Length, false);
        int idLength = System.Numerics.BitOperations.LeadingZeroCount((uint)b[0]) - 24 + 1;
        if (idLength > 4 || idLength >= n) return null;
        uint id = 0;
        for (int i = 0; i < idLength; i++) id = id << 8 | b[i];
        int sizeLength = System.Numerics.BitOperations.LeadingZeroCount((uint)b[idLength]) - 24 + 1;
        if (sizeLength > 8 || idLength + sizeLength > n) return null;
        long size = b[idLength] & (0xFF >> sizeLength);
        bool unknown = size == 0xFF >> sizeLength;
        for (int i = 1; i < sizeLength; i++)
        {
            size = size << 8 | b[idLength + i];
            unknown &= b[idLength + i] == 0xFF;
        }
        return (id, pos + idLength + sizeLength, unknown ? -1 : size);
    }
}
