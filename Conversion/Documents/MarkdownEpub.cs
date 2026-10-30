using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Markdown (GitHub flavour). Pictures go in a "name_images" folder next to the file.</summary>
internal static partial class MarkdownWriter
{
    public static void Write(DDocument doc, string path)
    {
        var imagesDir = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_images");
        int imageCount = 0;
        string Image(DImage img)
        {
            Directory.CreateDirectory(imagesDir);
            var name = $"image{++imageCount}.{(img.Ext == "jpeg" ? "jpg" : img.Ext)}";
            File.WriteAllBytes(Path.Combine(imagesDir, name), img.Data);
            return $"![]({Uri.EscapeDataString(Path.GetFileName(imagesDir))}/{name})";
        }

        var sb = new StringBuilder();
        Blocks(doc.Blocks, sb, Image);
        var notes = doc.Notes().OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count > 0) sb.Append('\n');
        foreach (var note in notes)
        {
            var text = string.Join(" ", FlowBridge.WithoutMark(note).OfType<DParagraph>().Select(p => Inlines(p, Image)).Where(s => s.Length > 0));
            sb.Append($"[^{NoteKey(note)}]: {text}\n");
        }
        File.WriteAllText(path, Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").TrimEnd() + "\n", new UTF8Encoding(false));
    }

    static string NoteKey(DNote n) => n.Endnote ? "e" + n.Number : n.Number.ToString();

    static void Blocks(List<DBlock> blocks, StringBuilder sb, Func<DImage, string> image)
    {
        var numbers = new Dictionary<int, int>();
        var lastTopList = ListKind.None;
        foreach (var block in blocks)
        {
            // A top-level list changing between bullets and numbers starts a new Markdown list.
            if (block is DParagraph { List: not ListKind.None, ListLevel: 0 } lp && lastTopList != ListKind.None && lp.List != lastTopList)
            {
                sb.Append('\n');
                numbers.Clear();
            }
            lastTopList = block is DParagraph { List: not ListKind.None } cur
                ? cur.ListLevel == 0 ? cur.List : lastTopList
                : ListKind.None;
            if (block is DParagraph code && IsCode(code))
            {
                numbers.Clear();
                if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append('\n');
                var lines = new StringBuilder();
                foreach (var inline in code.Inlines)
                    lines.Append(inline switch { DText t => t.Text, DLineBreak => "\n", _ => "" });
                sb.Append("```\n").Append(lines.ToString().TrimEnd('\n')).Append("\n```\n\n");
            }
            else if (block is DParagraph p)
            {
                var text = Inlines(p, image);
                if (p.List != ListKind.None)
                {
                    var indent = new string(' ', p.ListLevel * (p.List == ListKind.Number ? 3 : 2));
                    // Keep the source's numbering (a list may start at 3); Markdown only needs the first number right.
                    int number = numbers.TryGetValue(p.ListLevel, out var n) ? n + 1
                        : int.TryParse(new string((p.ListLabel ?? "").TakeWhile(char.IsDigit).ToArray()), out var start) ? start : 1;
                    var marker = p.List == ListKind.Number ? $"{numbers[p.ListLevel] = number}." : "-";
                    foreach (var deeper in numbers.Keys.Where(k => k > p.ListLevel).ToList()) numbers.Remove(deeper);
                    sb.Append(indent).Append(marker).Append(' ').Append(text.Replace("\n", "\n" + indent + "   ")).Append('\n');
                    continue;
                }
                numbers.Clear();
                if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.Append('\n');
                if (p.Heading > 0) sb.Append(new string('#', p.Heading)).Append(' ').Append(text.Replace("\n", " ")).Append("\n\n");
                else if (text.Length > 0) sb.Append(text).Append("\n\n");
            }
            else if (block is DTable t && t.Rows.Count > 0)
            {
                numbers.Clear();
                int cols = Math.Max(1, t.ColumnCount);
                string Row(DRow r)
                {
                    var cells = new List<string>();
                    foreach (var c in r.Cells)
                    {
                        cells.Add(string.Join("<br>", c.Blocks.OfType<DParagraph>().Select(cp => Inlines(cp, image).Replace("\n", "<br>").Replace("|", "\\|"))));
                        for (int s = 1; s < c.ColSpan; s++) cells.Add("");
                    }
                    while (cells.Count < cols) cells.Add("");
                    return "| " + string.Join(" | ", cells) + " |\n";
                }
                sb.Append('\n').Append(Row(t.Rows[0]));
                sb.Append("|").Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
                foreach (var r in t.Rows.Skip(1)) sb.Append(Row(r));
                sb.Append('\n');
            }
        }
    }

    static bool IsMono(string? font) => font is "Consolas" or "Courier New" or "Cascadia Code" or "Cascadia Mono" or "Courier";

    /// <summary>A paragraph that's entirely monospaced text is a code block.</summary>
    static bool IsCode(DParagraph p) =>
        p.List == ListKind.None && p.Heading == 0 && p.Inlines.Count > 0 &&
        p.Inlines.All(i => i is DLineBreak || i is DText t && IsMono(t.Style.Font)) && p.Inlines.Any(i => i is DLineBreak);

    static string Inlines(DParagraph p, Func<DImage, string> image)
    {
        var sb = new StringBuilder();
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var s = t.Style;
                    var text = Escape(t.Text);
                    if (text.Trim().Length == 0) { sb.Append(text); break; }
                    // Markers must hug the text, so move surrounding spaces outside them.
                    var lead = text[..^text.TrimStart().Length];
                    var trail = text[text.TrimEnd().Length..];
                    text = text.Trim();
                    if (IsMono(s.Font)) text = "`" + t.Text.Trim() + "`";
                    if (s.Strike) text = $"~~{text}~~";
                    if (s.Italic) text = $"*{text}*";
                    if (s.Bold) text = $"**{text}**";
                    if (s.Superscript) text = $"<sup>{text}</sup>";
                    if (s.Subscript) text = $"<sub>{text}</sub>";
                    if (t.Link != null) text = $"[{text}]({t.Link.Replace(" ", "%20").Replace(")", "%29")})";
                    sb.Append(lead).Append(text).Append(trail);
                    break;
                case DLineBreak: sb.Append("  \n"); break;
                case DImage img: sb.Append(image(img)); break;
                case DFloat { Image: { } fi }: sb.Append(image(fi)); break;
                case DFloat f: sb.Append(string.Join(" ", f.Blocks.OfType<DParagraph>().Select(fp => Inlines(fp, image)))); break;
                case DNote n: sb.Append($"[^{NoteKey(n)}]"); break;
            }
        }
        return Regex.Replace(sb.ToString().Replace("\t", "    "), @"\*\*\*\*|~~~~", "");
    }

    static string Escape(string text)
    {
        text = MarkdownSpecial().Replace(text, @"\$1");
        // Text that would read as a heading, quote or list at the start of a line.
        return LineStart().Replace(text, @"\$1");
    }

    [GeneratedRegex(@"([\\`*_\[\]<>|])")]
    private static partial Regex MarkdownSpecial();

    [GeneratedRegex(@"^(#|>|[-+] |\d+\. )", RegexOptions.Multiline)]
    private static partial Regex LineStart();
}

/// <summary>EPUB 3 e-books: one XHTML chapter per top-level heading or page break.</summary>
internal static class EpubWriter
{
    static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public static void Write(DDocument doc, string path)
    {
        var images = new List<(string name, byte[] data, string mime)>();
        string Image(DImage img)
        {
            var name = $"images/image{images.Count + 1}.{(img.Ext == "jpeg" ? "jpg" : img.Ext)}";
            images.Add((name, img.Data, HtmlWriter.Mime(img)));
            return name;
        }
        var writer = new HtmlWriter(true, Image);

        // Chapters start at each level-1 heading or page break.
        var chapters = new List<List<DBlock>> { new() };
        foreach (var block in doc.Blocks)
        {
            bool start = block is DParagraph { Heading: 1 } or DParagraph { PageBreakBefore: true };
            if (start && chapters[^1].Count > 0) chapters.Add([]);
            if (block is DParagraph { PageBreakBefore: true } p) p.PageBreakBefore = false;
            chapters[^1].Add(block);
        }
        string ChapterTitle(List<DBlock> blocks, int i) =>
            blocks.OfType<DParagraph>().FirstOrDefault(b => b.Heading > 0)?.PlainText().Trim() is { Length: > 0 } h ? h : $"Chapter {i + 1}";

        var title = doc.Title ?? "Document";
        var lang = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
        string Page(string pageTitle, string body) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n" +
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\" lang=\"{lang}\">\n" +
            $"<head><meta charset=\"utf-8\" /><title>{HtmlWriter.Enc(pageTitle)}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\" /></head>\n<body>\n{body}</body>\n</html>\n";

        var files = new List<(string name, string content)>();
        for (int i = 0; i < chapters.Count; i++)
        {
            var body = writer.Blocks(chapters[i]);
            if (i == chapters.Count - 1) body += writer.Notes(doc.Notes()).Replace("<section", "<section epub:type=\"footnotes\"");
            files.Add(($"chapter{i + 1}.xhtml", Page(ChapterTitle(chapters[i], i), body)));
        }

        var nav = new StringBuilder("<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>\n");
        for (int i = 0; i < chapters.Count; i++)
            nav.Append($"<li><a href=\"chapter{i + 1}.xhtml\">{HtmlWriter.Enc(ChapterTitle(chapters[i], i))}</a></li>\n");
        nav.Append("</ol></nav>\n");

        var css = $"body {{ font-family: serif; line-height: 1.5; }}\n{HtmlWriter.Css}";

        var package = new XElement(Opf + "package", new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "bookid"),
            new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc),
                new XElement(Dc + "identifier", new XAttribute("id", "bookid"), "urn:uuid:" + Guid.NewGuid()),
                new XElement(Dc + "title", title),
                new XElement(Dc + "language", lang),
                new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))),
            new XElement(Opf + "manifest",
                new XElement(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
                new XElement(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "style.css"), new XAttribute("media-type", "text/css")),
                files.Select((f, i) => new XElement(Opf + "item", new XAttribute("id", $"c{i + 1}"), new XAttribute("href", f.name), new XAttribute("media-type", "application/xhtml+xml"))),
                images.Select((img, i) => new XElement(Opf + "item", new XAttribute("id", $"img{i + 1}"), new XAttribute("href", img.name), new XAttribute("media-type", img.mime)))),
            new XElement(Opf + "spine", files.Select((_, i) => new XElement(Opf + "itemref", new XAttribute("idref", $"c{i + 1}")))));

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        // The mimetype entry must come first and be stored uncompressed.
        using (var w = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
            w.Write("application/epub+zip");
        void Put(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Put("META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
            "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>\n");
        Put("OEBPS/content.opf", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + package);
        Put("OEBPS/nav.xhtml", Page("Contents", nav.ToString()));
        Put("OEBPS/style.css", css);
        foreach (var (name, content) in files) Put("OEBPS/" + name, content);
        foreach (var (name, data, _) in images)
        {
            using var s = zip.CreateEntry("OEBPS/" + name, CompressionLevel.Optimal).Open();
            s.Write(data);
        }
    }
}
