using System.Runtime.InteropServices;

namespace ConvertWheel.Conversion.Media;

/// <summary>
/// Song details (title, artist, album…) carried across a conversion. Windows' property system reads
/// and writes MP3, M4A, WMA and FLAC tags; OGG and Opus tags go into the encoder's comment header.
/// </summary>
internal sealed class MediaTags
{
    public string? Title, Artist, Album, AlbumArtist, Genre, Comment;
    public int? Track, Year;

    public bool IsEmpty => Title == null && Artist == null && Album == null && AlbumArtist == null && Genre == null && Comment == null && Track == null && Year == null;

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
            }
        }
        return tags;
    }

    static int? LeadingInt(string s)
    {
        var digits = new string(s.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? n : null;
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

    static readonly Guid SummaryInfo = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    static readonly Guid MusicInfo = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");
    static PropertyKey KeyTitle => new(SummaryInfo, 2);
    static PropertyKey KeyComment => new(SummaryInfo, 6);
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
