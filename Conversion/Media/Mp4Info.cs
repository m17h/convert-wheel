using System.Buffers.Binary;
using System.Text;

namespace ConvertWheel.Conversion.Media;

/// <summary>
/// The timing of an MP4/MOV's tracks. Files with B-frames, encoder delay or a late track say so in an "edit
/// list", which Windows' reader ignores: the sound then slips against the picture.
/// </summary>
internal sealed class Mp4Info
{
    /// <param name="Start">When the track's first sound or picture should play, in seconds (below 0 is encoder warm-up), if the file says.</param>
    /// <param name="Delayed">Something other than "starts at 0" is asked for.</param>
    /// <param name="Trimmed">The edit list cuts pieces out, which only FFmpeg plays right.</param>
    public sealed record Track(bool Video, bool Audio, double? Start, bool Delayed, bool Trimmed, string Language, string? Name);

    public List<Track> Tracks { get; } = [];

    public Track? FirstVideo => Tracks.Find(t => t.Video);
    public List<Track> Audio => Tracks.FindAll(t => t.Audio);
    public bool Trimmed => Tracks.Exists(t => (t.Video || t.Audio) && t.Trimmed);

    /// <returns>null when the file isn't an MP4/MOV (or its index can't be read).</returns>
    public static Mp4Info? Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[8];
            for (long pos = 0; pos + 8 <= f.Length;)
            {
                f.Position = pos;
                f.ReadExactly(head);
                long size = BinaryPrimitives.ReadUInt32BigEndian(head), header = 8;
                if (size == 1)
                {
                    var large = new byte[8];
                    f.ReadExactly(large);
                    size = BinaryPrimitives.ReadInt64BigEndian(large);
                    header = 16;
                }
                else if (size == 0) size = f.Length - pos;
                if (size < header || size > f.Length - pos) return null;
                var type = Encoding.Latin1.GetString(head, 4, 4);
                if (pos == 0 && type is not ("ftyp" or "moov" or "mdat" or "wide" or "free" or "skip" or "pnot")) return null;
                if (type == "moov")
                {
                    if (size > 512 << 20) return null;
                    var moov = new byte[size - header];
                    f.Position = pos + header;
                    f.ReadExactly(moov);
                    return Parse(moov);
                }
                pos += size;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or OverflowException) { }
        return null;
    }

    static IEnumerable<(string type, int start, int end)> Boxes(byte[] data, int start, int end)
    {
        while (start + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));
            int header = 8;
            if (size == 1 && start + 16 <= end)
            {
                size = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(start + 8));
                header = 16;
            }
            else if (size == 0) size = end - start;
            if (size < header || size > end - start) yield break;
            yield return (Encoding.Latin1.GetString(data, start + 4, 4), start + header, start + (int)size);
            start += (int)size;
        }
    }

    static Mp4Info Parse(byte[] d)
    {
        var info = new Mp4Info();
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
        long I64(int at) => BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(at));
        double movieScale = 1000;
        // Files made of fragments keep their times with the fragments.
        bool fragmented = Boxes(d, 0, d.Length).Any(b => b.type == "mvex");
        foreach (var (type, start, end) in Boxes(d, 0, d.Length))
        {
            if (type == "mvhd") movieScale = Math.Max(1, U32(start + (d[start] == 1 ? 20 : 12)));
            if (type != "trak") continue;
            double scale = 1;
            long mediaDuration = 0;
            string handler = "", language = "und";
            string? name = null;
            double first = 0; // when the first picture shows, before edits
            var edits = new List<(long duration, long mediaTime)>();
            foreach (var (t, s, e) in Boxes(d, start, end))
            {
                if (t == "edts")
                    foreach (var (et, es, ee) in Boxes(d, s, e))
                    {
                        if (et != "elst") continue;
                        bool wide = d[es] == 1;
                        int entry = wide ? 20 : 12;
                        long count = Math.Min(U32(es + 4), (ee - es - 8) / entry);
                        for (int i = 0; i < count && i < 1000; i++)
                        {
                            int at = es + 8 + i * entry;
                            edits.Add(wide ? (I64(at), I64(at + 8)) : (U32(at), (int)U32(at + 4)));
                        }
                    }
                else if (t == "mdia")
                    foreach (var (mt, ms, me) in Boxes(d, s, e))
                    {
                        if (mt == "mdhd")
                        {
                            bool wide = d[ms] == 1;
                            scale = Math.Max(1, U32(ms + (wide ? 20 : 12)));
                            mediaDuration = wide ? I64(ms + 24) : U32(ms + 16);
                            int packed = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(ms + (wide ? 32 : 20)));
                            if (packed >= 0x400 && packed != 0x7FFF)
                                language = new string([(char)((packed >> 10 & 31) + 0x60), (char)((packed >> 5 & 31) + 0x60), (char)((packed & 31) + 0x60)]);
                        }
                        else if (mt == "minf")
                        {
                            foreach (var (nt, ns, ne) in Boxes(d, ms, me))
                                if (nt == "stbl") first = FirstShown(ns, ne);
                        }
                        else if (mt == "hdlr" && me - ms >= 12) handler = Encoding.Latin1.GetString(d, ms + 8, 4);
                    }
                else if (t == "udta")
                    foreach (var (ut, us, ue) in Boxes(d, s, e))
                        if (ut == "name" && ue > us) name = Encoding.UTF8.GetString(d, us, ue - us).Trim('\0', ' ');
            }

            first /= scale;
            double delay = 0, skip = 0;
            bool trimmed = false;
            int next = 0;
            while (next < edits.Count && edits[next].mediaTime < 0) delay += edits[next++].duration / movieScale;
            if (next < edits.Count)
            {
                var (duration, mediaTime) = edits[next];
                skip = mediaTime / scale;
                trimmed = edits.Skip(next + 1).Any(x => x.mediaTime >= 0)
                          || skip > (handler == "vide" ? 1 : 0.2)
                          || duration > 0 && duration / movieScale < mediaDuration / scale - skip - 0.5;
            }
            info.Tracks.Add(new Track(handler == "vide", handler == "soun", fragmented ? null : first + delay - skip, !fragmented && (delay > 0 || Math.Abs(first - skip) > 0.005 || first > 0.005), trimmed, language, string.IsNullOrEmpty(name) ? null : name));
        }
        return info;

        // The earliest of the first pictures: with B-frames they're stored in another order than they're shown.
        double FirstShown(int start, int end)
        {
            var steps = new List<long>();
            var shifts = new List<long>();
            foreach (var (t, s, e) in Boxes(d, start, end))
            {
                if (t is not ("stts" or "ctts")) continue;
                long count = Math.Min(U32(s + 4), (e - s - 8) / 8);
                var list = t == "stts" ? steps : shifts;
                for (int i = 0; i < count && list.Count < 32; i++)
                    for (long n = Math.Min(32, U32(s + 8 + i * 8)); n > 0 && list.Count < 32; n--) list.Add((int)U32(s + 12 + i * 8));
            }
            if (shifts.Count == 0) return 0;
            long time = 0, earliest = long.MaxValue;
            for (int i = 0; i < shifts.Count; i++)
            {
                earliest = Math.Min(earliest, time + shifts[i]);
                if (i < steps.Count) time += steps[i];
            }
            return earliest;
        }
    }
}
