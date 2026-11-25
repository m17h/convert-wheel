using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>What a document file really is, whatever its name says.</summary>
internal enum DocumentShape { Unknown, Empty, Locked, Word, WordOld, OpenDocument, Epub, Rtf, Html, WebArchive, Text, Other }

/// <summary>
/// The way into every document reader: reads a file as what it is when its name is wrong (Word is happy to open
/// a web page called ".doc"), and turns whatever a damaged file trips over into a message people understand.
/// </summary>
internal static class DocumentFiles
{
    // The same words the app uses elsewhere for these.
    const string Password = "This file is password-protected.", Damaged = "This file is damaged, or it isn't really the type its name says.";

    /// <param name="read">Reads the file as what its name says.</param>
    public static DDocument Open(string path, DocumentShape expected, Func<DDocument> read)
    {
        try
        {
            var shape = Detect(path);
            if (shape == DocumentShape.Locked) throw new ConversionException(Password);
            if (shape == expected || shape == DocumentShape.Unknown) return read();
            switch (shape)
            {
                case DocumentShape.Empty when expected is not (DocumentShape.Html or DocumentShape.Unknown): throw new ConversionException("This file is empty.");
                case DocumentShape.Word: return DocxReader.ReadDocx(path);
                case DocumentShape.WordOld: return WordBinaryReader.ReadDoc(path);
                case DocumentShape.OpenDocument: return OdtReader.ReadOdt(path);
                case DocumentShape.Epub: return HtmlReader.ReadBook(path);
                case DocumentShape.Rtf: return RichText.ReadRtf(path);
                // (Markdown can start with anything.)
                case DocumentShape.WebArchive when expected is not DocumentShape.Unknown: return HtmlReader.ReadWebArchive(path);
                case DocumentShape.Html when expected is not DocumentShape.Unknown: return HtmlReader.ReadPage(path);
                case DocumentShape.Text when expected is DocumentShape.WordOld or DocumentShape.Rtf or DocumentShape.Word: return PlainText.Read(path);
                case DocumentShape.Other: throw new ConversionException(Damaged);
                default: return read();
            }
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ConversionException("This file is nested too deeply to be read.");
        }
        catch (Exception ex) when (ex is not (ConversionException or OperationCanceledException or UnauthorizedAccessException or OutOfMemoryException)
                                   && (ex is not IOException || ex is EndOfStreamException or FileFormatException))
        {
            Log.Write($"Couldn't read {path}: {ex}");
            throw new ConversionException(Damaged);
        }
    }

    static DocumentShape Detect(string path)
    {
        byte[] head;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            head = new byte[4096];
            Array.Resize(ref head, file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false));
            if (head.Length == 0) return DocumentShape.Empty;
            if (head.Length > 4 && head[0] == 'P' && head[1] == 'K') return Package(file);
        }
        if (head.Length >= 8 && head.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return Compound(path);
        if (head.All(b => b == 0)) return DocumentShape.Other; // a file whose contents were never written

        bool binary = head.Contains((byte)0) && !TextDecoding.LooksLikeUtf16(head);
        var text = TextDecoding.Decode(head).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith(@"{\rtf", StringComparison.Ordinal)) return DocumentShape.Rtf;
        if (binary) return DocumentShape.Unknown;
        if (text.StartsWith('<'))
        {
            if (text.Contains("urn:oasis:names:tc:opendocument", StringComparison.Ordinal)) return DocumentShape.OpenDocument;
            if (Regex.IsMatch(text, @"<!doctype\s+html|<([a-z]+:)?html[\s>]|<(head|body)[\s>]", RegexOptions.IgnoreCase)) return DocumentShape.Html;
            return DocumentShape.Unknown;
        }
        if (text.StartsWith("%PDF-", StringComparison.Ordinal)) return DocumentShape.Other;
        if (Regex.IsMatch(text, @"^(MIME-Version|From|Subject|Date|Content-Type):", RegexOptions.IgnoreCase) && text.Contains("multipart/related", StringComparison.OrdinalIgnoreCase))
            return DocumentShape.WebArchive;
        return DocumentShape.Text;
    }

    static DocumentShape Package(Stream file)
    {
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        string Part(string name)
        {
            if (zip.GetEntry(name) is not { } entry) return "";
            using var reader = new StreamReader(PackageResources.Open(entry), Encoding.UTF8);
            var text = new char[1 << 18];
            return new string(text, 0, reader.ReadBlock(text));
        }
        if (Part("META-INF/manifest.xml").Contains(":encryption-data", StringComparison.Ordinal)) return DocumentShape.Locked;
        if (zip.GetEntry("META-INF/container.xml") != null) return DocumentShape.Epub;
        var mime = Part("mimetype").Trim();
        if (mime.StartsWith("application/epub", StringComparison.Ordinal)) return DocumentShape.Epub;
        if (mime.StartsWith("application/vnd.oasis.opendocument.text", StringComparison.Ordinal)) return DocumentShape.OpenDocument;
        if (Part("[Content_Types].xml").Contains("wordprocessingml", StringComparison.Ordinal) || zip.GetEntry("word/document.xml") != null) return DocumentShape.Word;
        return zip.GetEntry("content.xml") != null ? DocumentShape.OpenDocument : DocumentShape.Unknown;
    }

    static DocumentShape Compound(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // .docx with a password is an old-style container around the encrypted file.
        if (bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("EncryptedPackage")) >= 0) return DocumentShape.Locked;
        try { return new WordBinaryReader.CompoundFile(bytes).Stream("WordDocument") != null ? DocumentShape.WordOld : DocumentShape.Unknown; }
        catch (Exception ex) when (ex is ConversionException or ArgumentException or IndexOutOfRangeException) { return DocumentShape.Unknown; }
    }
}

/// <summary>Limits that keep a hostile or damaged document from taking the app down with it.</summary>
internal static class DocumentLimits
{
    /// <summary>Tables inside tables: beyond this the text is kept and the grid isn't (page layout runs out of stack).</summary>
    public const int TableDepth = 16;
    public const int Nesting = 200;
    public const int Columns = 256, Rows = 200_000, Cells = 2_000_000;
    /// <summary>The longest side of a page, in points (what PDF allows).</summary>
    public const double Length = 14400;

    /// <summary>Whether there's room to go one level deeper.</summary>
    public static bool CanGoDeeper(int depth, int limit = Nesting) => depth < limit && RuntimeHelpers.TryEnsureSufficientExecutionStack();

    public static double Clamp(double value, double limit = Length) => double.IsFinite(value) ? Math.Clamp(value, -limit, limit) : 0;

    /// <summary>The last step of every reader: settings no page could be laid out with are put right, and links inside the document made sure of.</summary>
    public static void Finish(DDocument doc)
    {
        (doc.PageWidth, doc.PageHeight, doc.Margins, doc.Columns, doc.ColumnGap) =
            Fit(new PageSetup(doc.PageWidth, doc.PageHeight, doc.Margins, doc.Columns, doc.ColumnGap));
        doc.HeaderDistance = Math.Clamp(Clamp(doc.HeaderDistance), 0, doc.PageHeight / 2);
        doc.FooterDistance = Math.Clamp(Clamp(doc.FooterDistance), 0, doc.PageHeight / 2);
        if (!(doc.FontSize >= 1 && doc.FontSize <= 1638)) doc.FontSize = 11;
        if (string.IsNullOrWhiteSpace(doc.Font)) doc.Font = "Calibri";
        foreach (var block in doc.Blocks)
            if (block.Section is { } section) block.Section = Fit(section);

        foreach (var p in doc.EveryParagraph())
        {
            p.IndentLeft = Math.Clamp(Clamp(p.IndentLeft), 0, doc.PageWidth * 0.75);
            p.IndentFirst = Math.Clamp(Clamp(p.IndentFirst), -doc.PageWidth * 0.75, doc.PageWidth * 0.75);
            p.SpaceBefore = Math.Clamp(Clamp(p.SpaceBefore), 0, 1584);
            p.SpaceAfter = Math.Clamp(Clamp(p.SpaceAfter), 0, 1584);
            p.LineSpacing = double.IsFinite(p.LineSpacing) && p.LineSpacing > 0.05 ? Math.Min(p.LineSpacing, 20) : 1;
            if (p.MarkSize is { } mark && !(mark >= 1 && mark <= 1638)) p.MarkSize = null;
            foreach (var inline in p.Inlines)
                switch (inline)
                {
                    case DText { Style.Size: { } size } t when !(size >= 1 && size <= 1638): t.Style = t.Style with { Size = null }; break;
                    case DField { Style.Size: { } size } f when !(size >= 1 && size <= 1638): f.Style = f.Style with { Size = null }; break;
                    case DImage img: (img.Width, img.Height) = (Side(img.Width), Side(img.Height)); break;
                    case DFloat f:
                        (f.Width, f.Height, f.X, f.Y) = (Side(f.Width), Side(f.Height), Clamp(f.X), Clamp(f.Y));
                        if (f.Image != null) (f.Image.Width, f.Image.Height) = (Side(f.Image.Width), Side(f.Image.Height));
                        break;
                }
        }
        Anchors(doc);

        static double Side(double v) => double.IsFinite(v) && v >= 1 ? Math.Min(v, 4000) : 1;
        static PageSetup Fit(PageSetup s)
        {
            double w = Math.Clamp(Clamp(s.Width), 72, Length), h = Math.Clamp(Clamp(s.Height), 72, Length);
            double Margin(double m, double side) => Math.Clamp(Clamp(m), 0, side / 2 - 18);
            var margins = new Thickness(Margin(s.Margins.Left, w), Margin(s.Margins.Top, h), Margin(s.Margins.Right, w), Margin(s.Margins.Bottom, h));
            double gap = Math.Clamp(Clamp(s.ColumnGap), 0, w / 2);
            double content = w - margins.Left - margins.Right;
            int columns = Math.Clamp(s.Columns, 1, 20);
            while (columns > 1 && (content - gap * (columns - 1)) / columns < 18) columns--;
            return new PageSetup(w, h, margins, columns, gap);
        }
    }

    /// <summary>
    /// A table with hundreds of columns is nearly always one whose cells claim to span more than there are:
    /// columns no cell starts or ends in are left out.
    /// </summary>
    public static void TrimColumns(DTable table)
    {
        if (table.ColumnCount <= 100) return;
        var rows = table.Slots().ToList();
        var edges = rows.SelectMany(r => r.slots).SelectMany(s => new[] { s.Column, s.Column + s.Span }).Append(0).Distinct().Order().ToList();
        if (edges.Count - 1 == table.ColumnCount) return;
        var rank = edges.Select((edge, i) => (edge, i)).ToDictionary(e => e.edge, e => e.i);
        foreach (var slot in rows.SelectMany(r => r.slots).Where(s => !s.Covered))
            slot.Cell.ColSpan = Math.Max(1, rank[slot.Column + slot.Span] - rank[slot.Column]);
        table.ColumnWidths = null;
    }

    /// <summary>Keeps the places something links to, and the links that lead somewhere.</summary>
    static void Anchors(DDocument doc)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var paragraphs = doc.EveryParagraph().ToList();
        foreach (var p in paragraphs)
            foreach (var inline in p.Inlines)
            {
                if (inline is not DText { Link: not null } t) continue;
                if (t.Link is ['#', .. var name]) wanted.Add(name);
                else t.Link = Links.ForDocument(t.Link);
            }
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paragraphs)
            p.Inlines.RemoveAll(i => i is DAnchor a && (!wanted.Contains(a.Name) || !found.Add(a.Name)));
        foreach (var p in paragraphs)
            foreach (var inline in p.Inlines)
                if (inline is DText { Link: ['#', .. var place] } t && !found.Contains(place)) t.Link = null;
    }
}

internal static partial class Links
{
    /// <summary>
    /// A link that's safe to put in a web page or e-book: to a website, mail address, phone number or place in
    /// the document, or a relative one. Scripts, files on this PC and network shares aren't.
    /// </summary>
    public static string? ForWeb(string? link)
    {
        if (Clean(link) is not { } clean) return null;
        var scheme = Scheme().Match(clean);
        if (scheme.Success) return scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto" or "tel" ? clean : null;
        // "//server" and "\\server" mean a network share when the page is opened from disk.
        return clean.Length > 1 && clean[0] is '/' or '\\' && clean[1] is '/' or '\\' ? null : clean;
    }

    /// <summary>A link for Word, OpenDocument or RTF: anything but scripts.</summary>
    public static string? ForDocument(string? link)
    {
        if (Clean(link) is not { } clean) return null;
        var scheme = Scheme().Match(clean);
        return scheme.Success && scheme.Groups[1].Value.ToLowerInvariant() is "javascript" or "vbscript" or "data" or "livescript" or "mhtml" or "view-source" ? null : clean;
    }

    /// <summary>Browsers ignore control characters and surrounding spaces in an address, so a check must too.</summary>
    static string? Clean(string? link)
    {
        if (link == null) return null;
        var clean = new string(link.Where(c => c is not ('\t' or '\r' or '\n') && !char.IsControl(c)).ToArray()).Trim();
        return clean.Length is 0 or > 4096 ? null : SafeXml.Text(clean);
    }

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*)\s*:")]
    private static partial Regex Scheme();
}

/// <summary>Pictures from documents: only from inside them, only of kinds every output can show, and only so big.</summary>
internal static partial class DocumentPictures
{
    const long FileLimit = 128L * 1024 * 1024;
    const long PixelLimit = 120_000_000;

    /// <summary>
    /// A picture a web page or Markdown file refers to, if it's in the same folder or one inside it. Anything else
    /// (the internet, network shares, other folders, links that lead out) is left alone.
    /// </summary>
    public static byte[]? FromFolder(string folder, string source)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return FromDataUri(source);
        try
        {
            var relative = Uri.UnescapeDataString(source.Split('?', '#')[0]).Replace('/', '\\').Trim();
            if (relative.Length == 0 || relative.StartsWith('\\') || relative.Contains(':') || relative.Any(char.IsControl)) return null;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + "\\";
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            // A shortcut on disk (symbolic link or junction) could still lead out.
            for (var at = path; at.Length > root.Length; at = Path.GetDirectoryName(at)!)
                if ((File.Exists(at) ? (FileSystemInfo)new FileInfo(at) : new DirectoryInfo(at)).LinkTarget != null) return null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is 0 or > FileLimit || (file.Attributes & FileAttributes.Device) != 0) return null;
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    public static byte[]? FromDataUri(string source)
    {
        int comma = source.IndexOf(',');
        if (comma < 0 || source.Length > FileLimit) return null;
        try
        {
            var body = source[(comma + 1)..];
            if (!source.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase)) return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(body));
            if (body.Contains('%')) body = Uri.UnescapeDataString(body);
            body = Spaces().Replace(body, "").TrimEnd('=');
            return Convert.FromBase64String(body + new string('=', (4 - body.Length % 4) % 4));
        }
        catch (FormatException) { return null; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// A picture as something every output can hold (by its content, not its name). Vector art and newer formats
    /// become PNG; anything unrecognised is left out rather than handed to a decoder that might read other files.
    /// </summary>
    public static (byte[] data, string ext)? Embeddable(byte[] data)
    {
        if (data.Length is < 8 || data.Length > FileLimit) return null;
        if (HtmlReader.ImageExt(data) is { } ext)
        {
            if (ext == "jpeg" && data[^1] == 0 && data.Length < 64) return null;
            var (w, h) = Pixels(data);
            if ((long)w * h > PixelLimit) return Shrunk(data, w, h);
            return (data, ext);
        }
        if (Kind(data) is not { } kind) return null;
        if (kind == "svg")
        {
            if (SafeSvg(data) is not { } svg) return null;
            data = svg;
        }
        return ToPng(data) is { } png ? (png, "png") : null;
    }

    /// <summary>Picture formats that need converting before a document can hold them.</summary>
    static string? Kind(byte[] d)
    {
        if (d.Length > 44 && BitConverter.ToInt32(d) == 1 && BitConverter.ToInt32(d, 40) == 0x464D4520)
        {
            // Metafiles have no pixels, but a damaged size can ask for a picture of billions.
            long w = Math.Abs((long)BitConverter.ToInt32(d, 16) - BitConverter.ToInt32(d, 8)), h = Math.Abs((long)BitConverter.ToInt32(d, 20) - BitConverter.ToInt32(d, 12));
            return w < 20000 && h < 20000 && w * h < PixelLimit ? "emf" : null;
        }
        if (BitConverter.ToUInt32(d) == 0x9AC6CDD7)
        {
            if (d.Length < 22) return null;
            double inch = Math.Max(1, (int)BitConverter.ToUInt16(d, 14));
            double w = Math.Abs(BitConverter.ToInt16(d, 10) - BitConverter.ToInt16(d, 6)) / inch, h = Math.Abs(BitConverter.ToInt16(d, 12) - BitConverter.ToInt16(d, 8)) / inch;
            return w < 200 && h < 200 ? "wmf" : null;
        }
        if (BitConverter.ToUInt16(d) is 1 or 2 && BitConverter.ToUInt16(d, 2) == 9) return "wmf";
        if (d.Length > 12 && d.AsSpan(0, 4).SequenceEqual("RIFF"u8) && d.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        if (d.Length > 12 && d.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return "heif";
        var head = Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, 2048)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase) || head.Contains(":svg", StringComparison.Ordinal) && head.StartsWith('<') ? "svg" : null;
    }

    /// <summary>A drawing without anything that points outside itself.</summary>
    static byte[]? SafeSvg(byte[] data)
    {
        try
        {
            if (data.Length > 16 * 1024 * 1024) return null;
            using var input = new MemoryStream(data);
            var svg = PackageResources.LoadXml(input);
            foreach (var e in svg.Descendants().Where(e => e.Name.LocalName is "script" or "foreignObject" or "style" or "handler" or "iframe" or "feImage").ToList())
                if (e.Parent != null && (e.Name.LocalName != "style" || Regex.IsMatch(e.Value, @"url\s*\(\s*['""]?\s*[^#'""\s]|@import|\\", RegexOptions.IgnoreCase))) e.Remove();
            foreach (var a in svg.Descendants().Attributes().Where(a => !a.IsNamespaceDeclaration).ToList())
            {
                bool points = a.Name.LocalName is "href" or "src";
                if (points && !a.Value.TrimStart().StartsWith('#') && !Regex.IsMatch(a.Value, @"^\s*data:image/(png|jpe?g|gif);", RegexOptions.IgnoreCase)) a.Remove();
                else if (!points && Regex.IsMatch(a.Value, @"url\s*\(\s*['""]?\s*[^#'""\s]", RegexOptions.IgnoreCase)) a.Remove();
                else if (a.Name.LocalName == "base") a.Remove();
            }
            using var output = new MemoryStream();
            svg.Save(output);
            return output.ToArray();
        }
        catch (XmlException) { return null; }
    }

    /// <summary>The picture as PNG, or null if it can't be decoded.</summary>
    public static byte[]? ToPng(byte[] data)
    {
        try
        {
            var bitmap = Kind(data) is "svg" or "emf" or "wmf" ? MagickImages.Decode(data) : FlowBridge.Decode(data);
            if (bitmap == null) return null;
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            png.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException or OutOfMemoryException or IOException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Skipped a picture that can't be decoded: {ex.Message}");
            return null;
        }
    }

    /// <summary>The picture as a web page or e-book can show it: PNG, JPEG or GIF.</summary>
    public static (byte[] data, string ext)? ForWeb(DImage image)
    {
        var ext = HtmlReader.ImageExt(image.Data);
        if (ext is "png" or "jpeg" or "gif") return (image.Data, ext == "jpeg" ? "jpg" : ext);
        return ToPng(image.Data) is { } png ? (png, "png") : null;
    }

    static (int w, int h) Pixels(byte[] data)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (0, 0); }
    }

    /// <summary>A picture too big to hold in memory, read at a smaller size.</summary>
    static (byte[], string)? Shrunk(byte[] data, int w, int h)
    {
        try
        {
            double scale = Math.Sqrt(PixelLimit / 4.0 / ((double)w * h));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = Math.Max(1, (int)(w * scale));
            image.DecodePixelHeight = Math.Max(1, (int)(h * scale));
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            png.Save(output);
            return (output.ToArray(), "png");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"Skipped a huge picture: {ex.Message}");
            return null;
        }
    }
}
