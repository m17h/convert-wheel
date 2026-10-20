namespace ConvertWheel.Conversion;

public enum FormatKind { Audio, Video, Image, Document, Spreadsheet, Presentation }

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

        new("mp4", "MP4", "H.264 video", FormatKind.Video),
        new("mkv", "MKV", "Matroska", FormatKind.Video),
        new("webm", "WEBM", "VP9 video", FormatKind.Video),
        new("mov", "MOV", "QuickTime", FormatKind.Video),
        new("avi", "AVI", "Xvid video", FormatKind.Video),
        new("wmv", "WMV", "Windows video", FormatKind.Video),

        new("png", "PNG", "Lossless", FormatKind.Image),
        new("jpg", "JPG", "Photo", FormatKind.Image),
        new("webp", "WEBP", "Web image", FormatKind.Image),
        new("gif", "GIF", "Animated", FormatKind.Image),
        new("avif", "AVIF", "Modern", FormatKind.Image),
        new("bmp", "BMP", "Bitmap", FormatKind.Image),
        new("tiff", "TIFF", "Print", FormatKind.Image),
        new("ico", "ICO", "Icon", FormatKind.Image),

        new("pdf", "PDF", "Document", FormatKind.Document),
        new("docx", "DOCX", "Word", FormatKind.Document),
        new("odt", "ODT", "OpenDocument", FormatKind.Document),
        new("rtf", "RTF", "Rich text", FormatKind.Document),
        new("txt", "TXT", "Plain text", FormatKind.Document),
        new("html", "HTML", "Web page", FormatKind.Document),
        new("xlsx", "XLSX", "Excel", FormatKind.Spreadsheet),
        new("ods", "ODS", "OpenDocument", FormatKind.Spreadsheet),
        new("csv", "CSV", "Values", FormatKind.Spreadsheet),
        new("pptx", "PPTX", "PowerPoint", FormatKind.Presentation),
        new("odp", "ODP", "OpenDocument", FormatKind.Presentation),
    ];

    static readonly Dictionary<string, FormatInfo> Targets = TargetList.ToDictionary(t => t.Ext);

    static readonly Dictionary<string, FormatKind> Inputs = Build(
        (FormatKind.Audio, "mp3 wav flac ogg oga opus m4a m4b aac wma aiff aif aifc ape wv ac3 dts amr mka caf au mp2 weba"),
        (FormatKind.Video, "mp4 m4v mkv webm mov avi wmv flv mpg mpeg m2ts mts ts 3gp 3g2 ogv vob f4v asf divx"),
        (FormatKind.Image, "png jpg jpeg jpe jfif webp gif bmp dib tif tiff ico heic heif avif tga jxl jxr wdp psd"),
        (FormatKind.Document, "doc docx odt rtf txt wpd"),
        (FormatKind.Spreadsheet, "xls xlsx ods csv"),
        (FormatKind.Presentation, "ppt pptx odp"));

    /// <summary>Hand-tuned "what people usually want" lists. Anything missing falls back to the kind default.</summary>
    static readonly Dictionary<string, string[]> SmartByExt = new()
    {
        ["mp3"] = ["wav", "flac", "ogg", "m4a", "opus", "aac"],
        ["wav"] = ["mp3", "flac", "ogg", "m4a", "opus", "aiff"],
        ["flac"] = ["mp3", "wav", "ogg", "m4a", "opus", "aiff"],
        ["ogg"] = ["mp3", "wav", "flac", "m4a", "opus", "aac"],
        ["opus"] = ["mp3", "wav", "ogg", "flac", "m4a"],
        ["m4a"] = ["mp3", "wav", "flac", "ogg", "opus"],
        ["aac"] = ["mp3", "m4a", "wav", "flac", "ogg"],
        ["wma"] = ["mp3", "wav", "flac", "m4a", "ogg"],
        ["aiff"] = ["wav", "mp3", "flac", "m4a", "ogg"],

        ["mp4"] = ["mp3", "gif", "webm", "mkv", "mov", "wav", "avi"],
        ["mkv"] = ["mp4", "webm", "mov", "mp3", "gif", "avi"],
        ["mov"] = ["mp4", "gif", "webm", "mkv", "mp3", "avi"],
        ["webm"] = ["mp4", "gif", "mp3", "mkv", "mov", "ogg"],
        ["avi"] = ["mp4", "mkv", "webm", "mov", "mp3", "gif"],

        ["png"] = ["jpg", "webp", "ico", "avif", "bmp", "gif", "tiff"],
        ["jpg"] = ["png", "webp", "avif", "ico", "bmp", "tiff"],
        ["webp"] = ["png", "jpg", "gif", "avif", "bmp", "ico"],
        ["gif"] = ["mp4", "webm", "png", "webp", "jpg"],
        ["bmp"] = ["png", "jpg", "webp", "ico", "tiff"],
        ["tiff"] = ["png", "jpg", "webp", "bmp"],
        ["ico"] = ["png", "jpg", "bmp", "webp"],
        ["heic"] = ["jpg", "png", "webp", "avif"],
        ["avif"] = ["jpg", "png", "webp", "gif"],

        ["docx"] = ["pdf", "odt", "rtf", "txt", "html"],
        ["doc"] = ["docx", "pdf", "odt", "rtf", "txt"],
        ["odt"] = ["docx", "pdf", "rtf", "txt", "html"],
        ["rtf"] = ["docx", "pdf", "odt", "txt"],
        ["txt"] = ["pdf", "docx", "odt", "rtf", "html"],
        ["xlsx"] = ["pdf", "csv", "ods", "html"],
        ["xls"] = ["xlsx", "pdf", "csv", "ods"],
        ["csv"] = ["xlsx", "ods", "pdf", "html"],
        ["ods"] = ["xlsx", "pdf", "csv", "html"],
        ["pptx"] = ["pdf", "odp"],
        ["ppt"] = ["pptx", "pdf", "odp"],
        ["odp"] = ["pptx", "pdf"],
    };

    static readonly Dictionary<FormatKind, string[]> SmartByKind = new()
    {
        [FormatKind.Audio] = ["mp3", "wav", "flac", "ogg", "m4a", "opus"],
        [FormatKind.Video] = ["mp4", "mp3", "gif", "webm", "mkv", "mov"],
        [FormatKind.Image] = ["png", "jpg", "webp", "avif", "ico", "bmp"],
        [FormatKind.Document] = ["pdf", "docx", "odt", "rtf", "txt"],
        [FormatKind.Spreadsheet] = ["xlsx", "pdf", "csv", "ods"],
        [FormatKind.Presentation] = ["pdf", "pptx", "odp"],
    };

    static readonly Dictionary<FormatKind, string[]> AllByKind = new()
    {
        [FormatKind.Audio] = ["mp3", "wav", "flac", "ogg", "m4a", "opus", "aac", "wma", "aiff"],
        [FormatKind.Video] = ["mp4", "mkv", "webm", "mov", "avi", "wmv", "gif", "mp3", "wav", "flac", "ogg", "m4a", "opus", "aac", "wma", "aiff"],
        [FormatKind.Image] = ["png", "jpg", "webp", "gif", "avif", "bmp", "tiff", "ico"],
        [FormatKind.Document] = ["pdf", "docx", "odt", "rtf", "txt", "html"],
        [FormatKind.Spreadsheet] = ["xlsx", "ods", "csv", "pdf", "html"],
        [FormatKind.Presentation] = ["pptx", "odp", "pdf"],
    };

    /// <summary>Extensions that are the same format under another name.</summary>
    public static string Canonical(string ext) => ext switch
    {
        "jpeg" or "jpe" or "jfif" => "jpg",
        "tif" => "tiff",
        "aif" or "aifc" => "aiff",
        "htm" => "html",
        "oga" => "ogg",
        "heif" => "heic",
        "mpeg" => "mpg",
        _ => ext,
    };

    public static ConversionPlan? Plan(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!Inputs.TryGetValue(ext, out var kind)) return null;
        var canonical = Canonical(ext);

        var all = AllByKind[kind].ToList();
        if (canonical == "gif") all.InsertRange(0, ["mp4", "webm", "mov"]);
        all.Remove(canonical);

        var smart = SmartByExt.TryGetValue(canonical, out var s) ? s : SmartByKind[kind];
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
