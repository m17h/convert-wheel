namespace ConvertWheel.Conversion;

public enum FormatKind { Audio, Video, Image, Document, Spreadsheet, Presentation, Archive, Subtitle, Data }

public sealed record FormatInfo(string Ext, string Label, string Hint, FormatKind Kind);

/// <summary>What the wheel offers for one file: a short smart list, plus everything reachable.</summary>
public sealed record ConversionPlan(
    string SourcePath,
    string SourceExt,
    FormatKind SourceKind,
    IReadOnlyList<FormatInfo> Suggested,
    IReadOnlyList<FormatInfo> All)
{
    public bool IsOfficeDocument => SourceKind is FormatKind.Document or FormatKind.Spreadsheet or FormatKind.Presentation;
}

public static class Formats
{
    public const int MaxSuggestions = 7;

    static readonly FormatInfo[] TargetList =
    [
        new("mp3", "MP3", "Universal", FormatKind.Audio),
        new("wav", "WAV", "Uncompressed", FormatKind.Audio),
        new("flac", "FLAC", "Lossless", FormatKind.Audio),
        new("ogg", "OGG", "Vorbis", FormatKind.Audio),
        new("m4a", "M4A", "AAC audio", FormatKind.Audio),
        new("opus", "OPUS", "Efficient", FormatKind.Audio),
        new("aac", "AAC", "Raw AAC", FormatKind.Audio),
        new("wma", "WMA", "Windows audio", FormatKind.Audio),
        new("aiff", "AIFF", "Apple PCM", FormatKind.Audio),
        new("ac3", "AC3", "Dolby Digital", FormatKind.Audio),
        new("m4r", "M4R", "iPhone ringtone", FormatKind.Audio),

        new("mp4", "MP4", "H.264 video", FormatKind.Video),
        new("mkv", "MKV", "Matroska", FormatKind.Video),
        new("webm", "WEBM", "VP9 video", FormatKind.Video),
        new("mov", "MOV", "QuickTime", FormatKind.Video),
        new("avi", "AVI", "Xvid video", FormatKind.Video),
        new("wmv", "WMV", "Windows video", FormatKind.Video),
        new("mpg", "MPG", "MPEG-2 / DVD", FormatKind.Video),

        new("png", "PNG", "Lossless", FormatKind.Image),
        new("jpg", "JPG", "Photo", FormatKind.Image),
        new("webp", "WEBP", "Web image", FormatKind.Image),
        new("gif", "GIF", "Animated", FormatKind.Image),
        new("avif", "AVIF", "Modern", FormatKind.Image),
        new("jxl", "JXL", "JPEG XL", FormatKind.Image),
        new("bmp", "BMP", "Bitmap", FormatKind.Image),
        new("tiff", "TIFF", "Print", FormatKind.Image),
        new("ico", "ICO", "Icon", FormatKind.Image),
        new("tga", "TGA", "Targa", FormatKind.Image),

        new("pdf", "PDF", "Document", FormatKind.Document),
        new("docx", "DOCX", "Word", FormatKind.Document),
        new("odt", "ODT", "OpenDocument", FormatKind.Document),
        new("rtf", "RTF", "Rich text", FormatKind.Document),
        new("txt", "TXT", "Plain text", FormatKind.Document),
        new("html", "HTML", "Web page", FormatKind.Document),
        new("md", "MD", "Markdown", FormatKind.Document),
        new("epub", "EPUB", "E-book", FormatKind.Document),

        new("xlsx", "XLSX", "Excel", FormatKind.Spreadsheet),
        new("ods", "ODS", "OpenDocument", FormatKind.Spreadsheet),
        new("csv", "CSV", "Values", FormatKind.Spreadsheet),
        new("tsv", "TSV", "Tab separated", FormatKind.Spreadsheet),

        new("pptx", "PPTX", "PowerPoint", FormatKind.Presentation),
        new("odp", "ODP", "OpenDocument", FormatKind.Presentation),

        new("zip", "ZIP", "Archive", FormatKind.Archive),
        new("tar", "TAR", "Tape archive", FormatKind.Archive),
        new("tgz", "TAR.GZ", "Compressed tar", FormatKind.Archive),

        new("srt", "SRT", "SubRip", FormatKind.Subtitle),
        new("vtt", "VTT", "WebVTT", FormatKind.Subtitle),
        new("ass", "ASS", "Styled subs", FormatKind.Subtitle),

        new("json", "JSON", "Data", FormatKind.Data),
        new("yaml", "YAML", "Config", FormatKind.Data),
        new("xml", "XML", "Markup", FormatKind.Data),
    ];

    static readonly Dictionary<string, FormatInfo> Targets = TargetList.ToDictionary(t => t.Ext);

    static readonly Dictionary<string, FormatKind> Inputs = Build(
        (FormatKind.Audio, "mp3 wav flac ogg oga opus m4a m4b m4r aac wma aiff aif aifc ape wv ac3 eac3 dts amr awb mka caf au snd mp2 mpa weba tta mpc spx dsf dff w64 voc ra"),
        (FormatKind.Video, "mp4 m4v mkv webm mov qt avi wmv flv f4v mpg mpeg m2v m2ts mts ts 3gp 3g2 ogv vob asf divx rm rmvb mxf dv"),
        (FormatKind.Image, "png jpg jpeg jpe jfif webp gif bmp dib tif tiff ico cur heic heif avif tga jxl jxr wdp hdp psd psb dds svg svgz " +
                           "dng cr2 cr3 crw nef nrw arw srf sr2 orf rw2 raf pef srw x3f exr hdr jp2 j2k jpf qoi pcx xcf emf wmf"),
        (FormatKind.Document, "doc docx docm dotx odt fodt rtf txt text log md markdown html htm xhtml epub pdf"),
        (FormatKind.Spreadsheet, "xls xlsx xlsm ods csv tsv"),
        (FormatKind.Presentation, "ppt pps pptx pptm ppsx potx odp"),
        (FormatKind.Archive, "zip 7z rar tar gz tgz bz2 tbz2 tbz xz txz"),
        (FormatKind.Subtitle, "srt vtt ass ssa sbv"),
        (FormatKind.Data, "json yaml yml xml"));

    /// <summary>Hand-tuned "what people usually want" lists. Anything missing falls back to the kind default.</summary>
    static readonly Dictionary<string, string[]> SmartByExt = new()
    {
        ["mp3"] = ["wav", "flac", "ogg", "m4a", "opus", "m4r"],
        ["wav"] = ["mp3", "flac", "ogg", "m4a", "opus", "aiff"],
        ["flac"] = ["mp3", "wav", "ogg", "m4a", "opus", "aiff"],
        ["ogg"] = ["mp3", "wav", "flac", "m4a", "opus", "aac"],
        ["opus"] = ["mp3", "wav", "ogg", "flac", "m4a"],
        ["m4a"] = ["mp3", "wav", "flac", "ogg", "opus", "m4r"],
        ["aac"] = ["mp3", "m4a", "wav", "flac", "ogg"],
        ["wma"] = ["mp3", "wav", "flac", "m4a", "ogg"],
        ["aiff"] = ["wav", "mp3", "flac", "m4a", "ogg"],

        ["mp4"] = ["mp3", "gif", "webm", "mkv", "mov", "wav", "avi"],
        ["mkv"] = ["mp4", "webm", "mov", "mp3", "gif", "avi"],
        ["mov"] = ["mp4", "gif", "webm", "mkv", "mp3", "avi"],
        ["webm"] = ["mp4", "gif", "mp3", "mkv", "mov", "ogg"],
        ["avi"] = ["mp4", "mkv", "webm", "mov", "mp3", "gif"],
        ["flv"] = ["mp4", "mkv", "webm", "mp3", "gif"],

        ["png"] = ["jpg", "webp", "ico", "pdf", "avif", "gif", "bmp"],
        ["jpg"] = ["png", "webp", "pdf", "avif", "ico", "bmp"],
        ["webp"] = ["png", "jpg", "gif", "avif", "pdf", "ico"],
        ["webp-animated"] = ["gif", "mp4", "webm", "png", "jpg"],
        ["gif"] = ["mp4", "webp", "webm", "png", "jpg"],
        ["bmp"] = ["png", "jpg", "webp", "ico", "pdf"],
        ["tiff"] = ["pdf", "png", "jpg", "webp"],
        ["ico"] = ["png", "jpg", "bmp", "webp"],
        ["heic"] = ["jpg", "png", "webp", "pdf", "avif"],
        ["avif"] = ["jpg", "png", "webp", "gif"],
        ["svg"] = ["png", "pdf", "jpg", "webp", "ico"],
        ["psd"] = ["png", "jpg", "pdf", "webp", "tiff"],

        ["docx"] = ["pdf", "odt", "rtf", "txt", "html", "md", "epub"],
        ["doc"] = ["docx", "pdf", "odt", "rtf", "txt"],
        ["odt"] = ["docx", "pdf", "rtf", "txt", "html"],
        ["rtf"] = ["docx", "pdf", "odt", "txt"],
        ["txt"] = ["pdf", "docx", "odt", "rtf", "html"],
        ["md"] = ["pdf", "html", "docx", "epub", "txt"],
        ["html"] = ["pdf", "docx", "md", "epub", "txt"],
        ["epub"] = ["pdf", "docx", "txt", "html", "md"],
        ["pdf"] = ["docx", "png", "jpg", "txt", "html", "epub"],

        ["xlsx"] = ["pdf", "csv", "ods", "html", "tsv"],
        ["xls"] = ["xlsx", "pdf", "csv", "ods"],
        ["csv"] = ["xlsx", "ods", "pdf", "json", "html"],
        ["tsv"] = ["xlsx", "csv", "ods", "pdf"],
        ["ods"] = ["xlsx", "pdf", "csv", "html"],
        ["pptx"] = ["pdf", "png", "odp", "jpg"],
        ["ppt"] = ["pptx", "pdf", "png", "odp"],
        ["odp"] = ["pptx", "pdf", "png"],
    };

    static readonly Dictionary<FormatKind, string[]> SmartByKind = new()
    {
        [FormatKind.Audio] = ["mp3", "wav", "flac", "ogg", "m4a", "opus"],
        [FormatKind.Video] = ["mp4", "mp3", "gif", "webm", "mkv", "mov"],
        [FormatKind.Image] = ["png", "jpg", "webp", "pdf", "avif", "ico"],
        [FormatKind.Document] = ["pdf", "docx", "odt", "rtf", "txt"],
        [FormatKind.Spreadsheet] = ["xlsx", "pdf", "csv", "ods"],
        [FormatKind.Presentation] = ["pdf", "pptx", "png", "odp"],
        [FormatKind.Archive] = ["zip", "tgz", "tar"],
        [FormatKind.Subtitle] = ["srt", "vtt", "ass", "txt"],
        [FormatKind.Data] = ["json", "yaml", "xml", "csv", "xlsx"],
    };

    static readonly string[] AudioTargets = ["mp3", "wav", "flac", "ogg", "m4a", "opus", "aac", "wma", "aiff", "ac3", "m4r"];

    static readonly Dictionary<FormatKind, string[]> AllByKind = new()
    {
        [FormatKind.Audio] = AudioTargets,
        [FormatKind.Video] = ["mp4", "mkv", "webm", "mov", "avi", "wmv", "mpg", "gif", "webp", .. AudioTargets],
        [FormatKind.Image] = ["png", "jpg", "webp", "avif", "jxl", "gif", "bmp", "tiff", "ico", "tga", "pdf", "txt", "docx"],
        [FormatKind.Document] = ["pdf", "docx", "odt", "rtf", "txt", "html", "md", "epub"],
        [FormatKind.Spreadsheet] = ["xlsx", "ods", "csv", "tsv", "json", "pdf", "html"],
        [FormatKind.Presentation] = ["pdf", "pptx", "odp", "png", "jpg"],
        [FormatKind.Archive] = ["zip", "tar", "tgz"],
        [FormatKind.Subtitle] = ["srt", "vtt", "ass", "txt"],
        [FormatKind.Data] = ["json", "yaml", "xml", "csv", "xlsx"],
    };

    /// <summary>Inputs whose reachable formats differ from the rest of their kind.</summary>
    static readonly Dictionary<string, string[]> AllByExt = new()
    {
        ["gif"] = ["mp4", "webm", "mov", "mkv", "png", "jpg", "webp", "avif", "jxl", "bmp", "tiff", "ico", "tga", "pdf"],
        ["pdf"] = ["docx", "odt", "rtf", "txt", "html", "md", "epub", "png", "jpg", "webp", "tiff"],
        // Animated WebP (detected from the file header) can become a video or GIF.
        ["webp-animated"] = ["gif", "mp4", "webm", "mov", "mkv", "png", "jpg", "avif", "jxl", "bmp", "tiff", "ico", "tga", "pdf"],
    };

    /// <summary>Extensions that are the same format under another name.</summary>
    public static string Canonical(string ext) => ext switch
    {
        "jpeg" or "jpe" or "jfif" => "jpg",
        "tif" => "tiff",
        "aif" or "aifc" => "aiff",
        "htm" or "xhtml" => "html",
        "oga" => "ogg",
        "heif" => "heic",
        "mpeg" => "mpg",
        "markdown" => "md",
        "yml" => "yaml",
        "text" => "txt",
        "docm" or "dotx" => "docx",
        "xlsm" => "xlsx",
        "pptm" or "ppsx" or "potx" => "pptx",
        "pps" => "ppt",
        "tbz" or "tbz2" => "tbz2",
        "txz" => "txz",
        _ => ext,
    };

    public static FormatKind? KindOf(string path) =>
        Inputs.TryGetValue(Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), out var kind) ? kind : null;

    public static ConversionPlan? Plan(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!Inputs.TryGetValue(ext, out var kind)) return null;
        var canonical = Canonical(ext);

        // A .gz holding a .tar is a tar.gz; compare by what's inside rather than the last extension.
        if (kind == FormatKind.Archive && path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) canonical = "tgz";
        var lookup = canonical == "webp" && MagickImages.IsAnimatedWebP(path) ? "webp-animated" : canonical;

        var all = (AllByExt.TryGetValue(lookup, out var special) ? special : AllByKind[kind]).ToList();
        all.Remove(canonical);
        if (canonical == "log") all.Remove("txt");

        var smart = SmartByExt.TryGetValue(lookup, out var s) ? s : SmartByKind[kind];
        var suggested = smart.Where(all.Contains).Take(MaxSuggestions).ToList();

        return new ConversionPlan(path, canonical, kind, suggested.Select(e => Targets[e]).ToList(), all.Select(e => Targets[e]).ToList());
    }

    static Dictionary<string, FormatKind> Build(params (FormatKind kind, string exts)[] groups)
    {
        var map = new Dictionary<string, FormatKind>();
        foreach (var (kind, exts) in groups)
            foreach (var ext in exts.Split(' '))
                map[ext] = kind;
        return map;
    }
}
