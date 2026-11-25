using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ConvertWheel.Conversion.Media;

/// <summary>
/// Song details (title, artist, album…) carried across a conversion. Windows' property system reads
/// and writes MP3, M4A, WMA and FLAC tags; OGG and Opus tags go into the encoder's comment header.
/// </summary>
internal sealed class MediaTags
{
    public string? Title, Artist, Album, AlbumArtist, Genre, Comment;
    public int? Track, Year;
    /// <summary>Cover art: a JPEG, PNG, GIF, BMP or WebP file.</summary>
    public byte[]? Cover;

    const int MaxCover = 16 << 20;

    public bool IsEmpty => Cover == null && Title == null && Artist == null && Album == null && AlbumArtist == null && Genre == null && Comment == null && Track == null && Year == null;

    public IEnumerable<(string key, string value)> AsVorbisComments()
    {
        if (Title != null) yield return ("TITLE", Title);
        if (Artist != null) yield return ("ARTIST", Artist);
        if (Album != null) yield return ("ALBUM", Album);
        if (AlbumArtist != null) yield return ("ALBUMARTIST", AlbumArtist);
        if (Genre != null) yield return ("GENRE", Genre);
        if (Comment != null) yield return ("COMMENT", Comment);
        if (Track != null) yield return ("TRACKNUMBER", Track.Value.ToString());
        if (Year != null) yield return ("DATE", Year.Value.ToString());
        if (Cover != null) yield return ("METADATA_BLOCK_PICTURE", System.Convert.ToBase64String(FlacPicture(Cover)));
    }

    public static MediaTags FromVorbisComments(IEnumerable<KeyValuePair<string, string>> comments)
    {
        var tags = new MediaTags();
        foreach (var (key, value) in comments)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            switch (key.ToUpperInvariant())
            {
                case "TITLE": tags.Title ??= value; break;
                case "ARTIST": tags.Artist ??= value; break;
                case "ALBUM": tags.Album ??= value; break;
                case "ALBUMARTIST" or "ALBUM ARTIST": tags.AlbumArtist ??= value; break;
                case "GENRE": tags.Genre ??= value; break;
                case "COMMENT" or "DESCRIPTION": tags.Comment ??= value; break;
                case "TRACKNUMBER": tags.Track ??= LeadingInt(value); break;
                case "DATE" or "YEAR": tags.Year ??= LeadingInt(value); break;
                case "METADATA_BLOCK_PICTURE": tags.Cover = FromFlacPicture(value) ?? tags.Cover; break;
                case "COVERART": tags.Cover ??= FromBase64(value) is { } art && MimeType(art) != null ? art : null; break;
            }
        }
        return tags;
    }

    static int? LeadingInt(string s)
    {
        var digits = new string(s.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? n : null;
    }

    // ------------------------------------------------------------------ cover art

    static string? MimeType(ReadOnlySpan<byte> picture) =>
        picture.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
        : picture.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) ? "image/png"
        : picture.StartsWith("GIF8"u8) ? "image/gif"
        : picture.StartsWith("BM"u8) ? "image/bmp"
        : picture.Length > 12 && picture[8..].StartsWith("WEBP"u8) ? "image/webp" : null;

    static byte[]? FromBase64(string text)
    {
        if (text.Length > MaxCover * 4L / 3 + 1000) return null;
        try { return System.Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { return null; }
    }

    /// <summary>FLAC's picture block, which Ogg files carry too (in base64): front cover, type, size unknown, data.</summary>
    static byte[] FlacPicture(byte[] picture)
    {
        var mime = Encoding.ASCII.GetBytes(MimeType(picture) ?? "image/jpeg");
        var block = new byte[32 + mime.Length + picture.Length];
        BinaryPrimitives.WriteInt32BigEndian(block, 3);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(4), mime.Length);
        mime.CopyTo(block, 8);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(28 + mime.Length), picture.Length);
        picture.CopyTo(block, 32 + mime.Length);
        return block;
    }

    static byte[]? FromFlacPicture(string base64)
    {
        if (FromBase64(base64) is not { Length: > 32 } block) return null;
        long at = 4;
        for (int i = 0; i < 2; i++)
        {
            if (at + 4 > block.Length) return null;
            at += 4 + BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan((int)at));
        }
        at += 16;
        if (at + 4 > block.Length) return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan((int)at));
        if (length > block.Length - at - 4) return null;
        var picture = block[(int)(at + 4)..(int)(at + 4 + length)];
        return MimeType(picture) != null ? picture : null;
    }

    /// <summary>Puts the cover into a finished FLAC file, which Windows' property system doesn't do.</summary>
    public void WriteFlacPicture(string path)
    {
        if (Cover == null) return;
        var block = FlacPicture(Cover);
        if (block.Length > 0xFFFFFF) return; // the most a FLAC block holds
        var temp = path + ".cw-tmp";
        try
        {
            using (var from = File.OpenRead(path))
            {
                // After STREAMINFO, which has to come first (4 + 4 + 34 bytes).
                var start = new byte[42];
                from.ReadExactly(start);
                if (!start.AsSpan(0, 4).SequenceEqual("fLaC"u8) || (start[4] & 0x7F) != 0 || start[7] != 34) return;
                bool only = (start[4] & 0x80) != 0;
                start[4] &= 0x7F;
                using var to = File.Create(temp);
                to.Write(start);
                to.Write([(byte)(only ? 0x86 : 0x06), (byte)(block.Length >> 16), (byte)(block.Length >> 8), (byte)block.Length]);
                to.Write(block);
                from.CopyTo(to);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't add the cover to {path}: {ex.Message}");
            try { File.Delete(temp); } catch { }
        }
    }

    // ------------------------------------------------------------------ Windows property system

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey(Guid fmtid, int pid)
    {
        public Guid Fmtid = fmtid;
        public int Pid = pid;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr value);
        void SetValue(ref PropertyKey key, IntPtr value);
        void Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid riid, out IPropertyStore store);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int PropVariantToStringAlloc(IntPtr value, out IntPtr text);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int InitPropVariantFromStringVector(string[] values, int count, IntPtr variant);
    [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr variant);

    [DllImport("shlwapi.dll")] static extern IStream? SHCreateMemStream(byte[] data, int length);

    static readonly Guid SummaryInfo = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    static readonly Guid MusicInfo = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");
    static PropertyKey KeyTitle => new(SummaryInfo, 2);
    static PropertyKey KeyComment => new(SummaryInfo, 6);
    static PropertyKey KeyCover => new(SummaryInfo, 27); // System.ThumbnailStream
    static PropertyKey KeyArtist => new(MusicInfo, 2);
    static PropertyKey KeyAlbum => new(MusicInfo, 4);
    static PropertyKey KeyYear => new(MusicInfo, 5);
    static PropertyKey KeyTrack => new(MusicInfo, 7);
    static PropertyKey KeyGenre => new(MusicInfo, 11);
    static PropertyKey KeyAlbumArtist => new(MusicInfo, 13);

    public static MediaTags ReadWithWindows(string path)
    {
        var tags = new MediaTags();
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store);
            try
            {
                tags.Title = Text(store, KeyTitle);
                tags.Artist = Text(store, KeyArtist);
                tags.Album = Text(store, KeyAlbum);
                tags.AlbumArtist = Text(store, KeyAlbumArtist);
                tags.Genre = Text(store, KeyGenre);
                tags.Comment = Text(store, KeyComment);
                tags.Track = LeadingInt(Text(store, KeyTrack) ?? "");
                tags.Year = LeadingInt(Text(store, KeyYear) ?? "");
                tags.Cover = Picture(store);
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { }
        return tags;
    }

    static string? Text(IPropertyStore store, PropertyKey key)
    {
        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            store.GetValue(ref key, variant);
            if (Marshal.ReadInt16(variant) == 0 || PropVariantToStringAlloc(variant, out var text) < 0) return null;
            var value = Marshal.PtrToStringUni(text);
            Marshal.FreeCoTaskMem(text);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (COMException) { return null; }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
        }
    }

    static byte[]? Picture(IPropertyStore store)
    {
        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            var key = KeyCover;
            store.GetValue(ref key, variant);
            if (Marshal.ReadInt16(variant) != 66 /* VT_STREAM */ || Marshal.GetObjectForIUnknown(Marshal.ReadIntPtr(variant, 8)) is not IStream stream) return null;
            try
            {
                stream.Stat(out var stat, 1);
                if (stat.cbSize is <= 0 or > MaxCover) return null;
                var data = new byte[stat.cbSize];
                stream.Read(data, data.Length, IntPtr.Zero);
                // For MP3s Windows sometimes leaves the end of the picture's description in front of it.
                for (int skip = 0; skip < Math.Min(64, data.Length); skip++)
                    if (MimeType(data.AsSpan(skip)) != null) return data[skip..];
                return null;
            }
            finally { Marshal.ReleaseComObject(stream); }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotImplementedException) { return null; }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
        }
    }

    static void SetPicture(IPropertyStore store, byte[]? picture)
    {
        if (picture == null || SHCreateMemStream(picture, picture.Length) is not { } stream) return;
        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            Marshal.WriteInt16(variant, 66); // VT_STREAM
            Marshal.WriteIntPtr(variant, 8, Marshal.GetComInterfaceForObject<IStream, IStream>(stream));
            var key = KeyCover;
            try { store.SetValue(ref key, variant); } catch (COMException) { }
        }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
            Marshal.ReleaseComObject(stream);
        }
    }

    /// <summary>Writes tags into a finished MP3/M4A/WMA/FLAC file. Best effort: a file without tags is still fine.</summary>
    public void WriteWithWindows(string path)
    {
        if (IsEmpty) return;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2 /* GPS_READWRITE */, ref iid, out var store);
            try
            {
                Set(store, KeyTitle, Title, vector: false);
                Set(store, KeyArtist, Artist, vector: true);
                Set(store, KeyAlbum, Album, vector: false);
                Set(store, KeyAlbumArtist, AlbumArtist, vector: false);
                Set(store, KeyGenre, Genre, vector: true);
                Set(store, KeyComment, Comment, vector: false);
                SetNumber(store, KeyTrack, Track);
                SetNumber(store, KeyYear, Year);
                SetPicture(store, Cover);
                store.Commit();
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Write($"Couldn't write tags to {path}: {ex.Message}");
        }
    }

    static void Set(IPropertyStore store, PropertyKey key, string? value, bool vector)
    {
        if (value == null) return;
        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            if (vector)
            {
                var parts = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (InitPropVariantFromStringVector(parts, parts.Length, variant) < 0) return;
            }
            else
            {
                Marshal.WriteInt16(variant, 31); // VT_LPWSTR
                Marshal.WriteIntPtr(variant, 8, Marshal.StringToCoTaskMemUni(value));
            }
            try { store.SetValue(ref key, variant); } catch (COMException) { }
        }
        finally
        {
            PropVariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
        }
    }

    static void SetNumber(IPropertyStore store, PropertyKey key, int? value)
    {
        if (value == null) return;
        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(variant, i, 0);
            Marshal.WriteInt16(variant, 19); // VT_UI4
            Marshal.WriteInt32(variant, 8, value.Value);
            try { store.SetValue(ref key, variant); } catch (COMException) { }
        }
        finally { Marshal.FreeCoTaskMem(variant); }
    }
}
