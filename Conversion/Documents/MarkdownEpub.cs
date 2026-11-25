using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Markdown (GitHub flavour). Pictures go in a "name_images" folder next to the file.</summary>
internal sealed partial class MarkdownWriter(CancellationToken ct)
{
    public static void Write(DDocument doc, string path, CancellationToken ct = default)
    {
        var writer = new MarkdownWriter(ct);
        writer.Blocks(doc.Blocks);
        var notes = doc.Notes().OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count > 0) writer.Blank();
        foreach (var note in notes)
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ", DDocument.Paragraphs(FlowBridge.WithoutMark(note)).Select(p => writer.Inlines(p).Replace("\n", " ")).Where(s => s.Length > 0));
            writer._out.Append($"[^{NoteKey(note)}]: {text}\n");
        }

        path = Path.GetFullPath(path);
        string text2 = writer._out.ToString().TrimEnd() + "\n", folder = "";
        if (writer._pictures.Count > 0)
        {
            folder = SavePictures(writer._pictures, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), ct);
            text2 = text2.Replace(FolderMark, Uri.EscapeDataString(Path.GetFileName(folder)).Replace("(", "%28").Replace(")", "%29"));
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(path, text2, new UTF8Encoding(false));
        }
        catch
        {
            if (folder.Length > 0) Remove(folder);
            throw;
        }
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>Stands for the pictures' folder in the text until its name is known. (Control characters never get into the text itself.)</summary>
    const string FolderMark = "\u0001";

    readonly List<(string name, byte[] data)> _pictures = [];

    string Picture(DImage img)
    {
        if (DocumentPictures.ForWeb(img) is not { } picture) return Escape(img.Alt ?? "");
        var name = $"image{_pictures.Count + 1}.{picture.ext}";
        _pictures.Add((name, picture.data));
        return $"![{Escape(img.Alt ?? "").Replace("\r", "").Replace('\n', ' ')}]({FolderMark}/{name})";
    }

    /// <summary>
    /// Saves the pictures into a folder of their own: never one that was already there, and only under its real
    /// name once every picture is in it.
    /// </summary>
    static string SavePictures(List<(string name, byte[] data)> pictures, string parent, string stem, CancellationToken ct)
    {
        var staging = Path.Combine(parent, $".cw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, data) in pictures)
            {
                ct.ThrowIfCancellationRequested();
                File.WriteAllBytes(Path.Combine(staging, name), data);
            }
            for (int i = 1, busy = 0; ; )
            {
                var folder = Path.Combine(parent, i == 1 ? $"{stem}_images" : $"{stem}_images-{i}");
                try
                {
                    // Fails if anything has that name, so two conversions can't share a folder.
                    Directory.Move(staging, folder);
                    return folder;
                }
                catch (IOException) when (Directory.Exists(folder) || File.Exists(folder)) { i++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ++busy < 20)
                {
                    Thread.Sleep(50); // a virus scanner is still looking at the pictures
                }
            }
        }
        catch
        {
            Remove(staging);
            throw;
        }
    }

    /// <summary>Deletes a folder this conversion made.</summary>
    static void Remove(string folder)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // ------------------------------------------------------------------ blocks

    readonly StringBuilder _out = new();
    bool _inTable;

    static string NoteKey(DNote n) => n.Endnote ? "e" + n.Number : n.Number.ToString();

    /// <summary>Ends the line and leaves one empty line, unless there already is one.</summary>
    void Blank()
    {
        if (_out.Length == 0) return;
        if (_out[^1] != '\n') _out.Append('\n');
        if (_out.Length < 2 || _out[^2] != '\n') _out.Append('\n');
    }

    void Blocks(List<DBlock> blocks)
    {
        // The lists a paragraph is inside: their level, kind, how far their text is indented, and the number reached.
        var open = new Stack<(int level, ListKind kind, int indent, int number)>();
        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            if (block is DParagraph { List: not ListKind.None, Heading: 0 } item)
            {
                ListItem(item, open);
                continue;
            }
            if (open.Count > 0) Blank();
            open.Clear();
            if (block is DParagraph code && IsCode(code))
            {
                Blank();
                var lines = new StringBuilder();
                foreach (var inline in code.Inlines)
                    lines.Append(inline switch { DText t => SafeXml.Text(t.Text), DLineBreak => "\n", _ => "" });
                var codeText = lines.ToString().TrimEnd('\n');
                var fence = new string('`', Math.Max(3, LongestTicks(codeText) + 1));
                _out.Append(fence).Append('\n').Append(codeText).Append('\n').Append(fence).Append("\n\n");
            }
            else if (block is DParagraph p)
            {
                Blank();
                var label = p.ListLabel is { Length: > 0 } l ? Escape(l) + " " : "";
                var text = label + Inlines(p);
                if (p.Heading > 0) { if (text.Trim().Length > 0) _out.Append(new string('#', Math.Clamp(p.Heading, 1, 6))).Append(' ').Append(text.Replace("  \n", " ").Replace("\n", " ").Trim()).Append("\n\n"); }
                else if (text.Trim().Length > 0) _out.Append(Unindented(text)).Append("\n\n");
                Boxes(p);
            }
            else if (block is DTable t && t.Rows.Count > 0) Table(t);
        }
        if (open.Count > 0) Blank();
    }

    /// <summary>A line that starts with spaces would turn into code.</summary>
    static string Unindented(string text)
    {
        int spaces = text.Length - text.TrimStart(' ').Length;
        return spaces == 0 ? text : string.Concat(Enumerable.Repeat("&nbsp;", Math.Min(spaces, 200))) + text[spaces..];
    }

    void ListItem(DParagraph p, Stack<(int level, ListKind kind, int indent, int number)> open)
    {
        while (open.Count > 0 && open.Peek().level > p.ListLevel) open.Pop();
        bool same = open.Count > 0 && open.Peek().level == p.ListLevel;
        var marker = p.List == ListKind.Number ? ListMarker.Parse(p.ListLabel, same ? open.Peek().number + 1 : null) : null;
        // Markdown only has "1."; a marker with words or outer numbers in it is written as part of the text.
        bool words = p.List == ListKind.Number && (marker == null ? p.ListLabel is { Length: > 0 } : marker.Prefix.Any(char.IsLetterOrDigit));
        var kind = words ? ListKind.Bullet : p.List;
        int number = marker?.Number ?? 1;
        if (same && (open.Peek().kind != kind || kind == ListKind.Number && number != open.Peek().number + 1))
        {
            // A different list straight after: Markdown would run the two together.
            open.Pop();
            same = false;
            if (_out.Length > 0 && open.Count == 0)
            {
                Blank();
                _out.Append("<!-- -->\n\n");
            }
        }
        else if (!same && open.Count == 0) Blank();
        int indent = same ? -1 : 0;
        string mark = kind == ListKind.Number ? number + "." : "-";
        if (same)
        {
            // Items line up with the first of their list.
            var last = open.Pop();
            indent = last.indent;
        }
        else indent = open.Count > 0 ? open.Peek().indent + 1 + MarkWidth(open.Peek()) : 0;
        open.Push((p.ListLevel, kind, indent, number));
        var lead = new string(' ', Math.Min(indent, 200));
        var text = (words ? Escape(p.ListLabel!) + " " : "") + Inlines(p);
        _out.Append(lead).Append(mark).Append(' ').Append(text.Trim().Replace("\n", "\n" + lead + new string(' ', mark.Length + 1))).Append('\n');
        foreach (var box in p.Inlines.OfType<DFloat>().Where(f => f.Blocks.Count > 0))
            _out.Append(lead).Append(new string(' ', mark.Length + 1)).Append(Flat(box.Blocks, " ")).Append('\n');

        static int MarkWidth((int level, ListKind kind, int indent, int number) list) => list.kind == ListKind.Number ? list.number.ToString().Length + 1 : 1;
    }

    /// <summary>Text boxes: their contents follow the paragraph they're anchored in.</summary>
    void Boxes(DParagraph p)
    {
        foreach (var f in p.Inlines.OfType<DFloat>().Where(f => f.Blocks.Count > 0)) Blocks(f.Blocks);
    }

    void Table(DTable t)
    {
        Blank();
        int cols = Math.Max(1, t.ColumnCount);
        bool first = true;
        _inTable = true;
        foreach (var (_, slots) in t.Slots())
        {
            ct.ThrowIfCancellationRequested();
            _out.Append('|');
            int at = 0;
            foreach (var slot in slots)
            {
                for (; at < slot.Column; at++) _out.Append("  |");
                _out.Append(' ').Append(slot.Covered ? "" : Flat(slot.Cell.Blocks, "<br>")).Append(" |");
                for (at++; at < slot.Column + slot.Span; at++) _out.Append("  |");
            }
            for (; at < cols; at++) _out.Append("  |");
            _out.Append('\n');
            if (first) _out.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
            first = false;
        }
        _inTable = false;
        _out.Append('\n');
    }

    /// <summary>Blocks on a single line, as a table cell needs: a table inside becomes its rows.</summary>
    string Flat(List<DBlock> blocks, string between)
    {
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is DParagraph p)
            {
                var text = Inlines(p).Replace("  \n", "\n").Replace("\n", between).Trim();
                if (p.ListLabel is { Length: > 0 } label) text = Escape(label) + " " + text;
                foreach (var f in p.Inlines.OfType<DFloat>().Where(f => f.Blocks.Count > 0)) text = (text + " " + Flat(f.Blocks, between)).Trim();
                if (text.Length > 0) parts.Add(text);
            }
            else if (block is DTable t)
                parts.AddRange(t.Rows.Select(r => string.Join(" · ", r.Cells.Select(c => Flat(c.Blocks, " ")).Where(s => s.Length > 0))).Where(s => s.Length > 0));
        }
        return string.Join(between, parts);
    }

    static bool IsMono(string? font) => font is "Consolas" or "Courier New" or "Cascadia Code" or "Cascadia Mono" or "Courier";

    /// <summary>A paragraph that's entirely monospaced text is a code block.</summary>
    internal static bool IsCode(DParagraph p) =>
        p.List == ListKind.None && p.ListLabel == null && p.Heading == 0 && p.Inlines.Any(i => i is DLineBreak) && p.Inlines.Any(i => i is DText) &&
        p.Inlines.All(i => i is DLineBreak or DAnchor || i is DText t && IsMono(t.Style.Font));

    static int LongestTicks(string text) => text.Contains('`') ? Ticks().Matches(text).Max(m => m.Length) : 0;

    // ------------------------------------------------------------------ inlines

    string Inlines(DParagraph p)
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
                    if (IsMono(s.Font))
                    {
                        var codeText = SafeXml.Text(t.Text).Trim().Replace("\r", "").Replace('\n', ' ');
                        var fence = new string('`', LongestTicks(codeText) + 1);
                        var pad = codeText.StartsWith('`') || codeText.EndsWith('`') ? " " : "";
                        text = fence + pad + (_inTable ? codeText.Replace("|", "\\|") : codeText) + pad + fence;
                    }
                    if (s.Strike) text = $"~~{text}~~";
                    if (s.Italic) text = $"*{text}*";
                    if (s.Bold) text = $"**{text}**";
                    if (s.Superscript) text = $"<sup>{text}</sup>";
                    if (s.Subscript) text = $"<sub>{text}</sub>";
                    if (Links.ForWeb(t.Link) is { } link && !link.StartsWith('#')) text = $"[{text}]({Address(link)})";
                    if (lead.Length == 0 && sb.Length >= 2 &&
                        (text.StartsWith("**") && sb[^2] == '*' && sb[^1] == '*' && !text.StartsWith("***") || text.StartsWith("~~") && sb[^2] == '~' && sb[^1] == '~'))
                    {
                        sb.Length -= 2;
                        text = text[2..];
                    }
                    sb.Append(lead).Append(text).Append(trail);
                    break;
                case DLineBreak: sb.Append("  \n"); break;
                case DImage img: sb.Append(Picture(img)); break;
                case DFloat { Image: { } fi }: sb.Append(Picture(fi)); break;
                case DNote n: sb.Append($"[^{NoteKey(n)}]"); break;
            }
        }
        return sb.Replace("\t", "    ").ToString();
    }

    static string Address(string link)
    {
        var sb = new StringBuilder();
        foreach (var c in link) sb.Append(c switch { ' ' => "%20", '(' => "%28", ')' => "%29", '<' => "%3C", '>' => "%3E", '\\' => "%5C", _ => c.ToString() });
        return sb.ToString();
    }

    static string Escape(string text)
    {
        // Only "&name;" / "&#123;" would read as an entity; a plain "&" stays readable.
        text = MarkdownSpecial().Replace(EntityLike().Replace(Plain(text), "&amp;"), @"\$1");
        // Text that would read as a heading, quote or list at the start of a line.
        return LineStart().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value + "\\" + m.Groups[2].Value : "\\" + m.Value);
    }

    /// <summary>Without control characters, which Markdown has no use for.</summary>
    static string Plain(string text)
    {
        text = SafeXml.Text(text);
        return text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    [GeneratedRegex(@"&(?=#?[A-Za-z0-9]+;)")]
    private static partial Regex EntityLike();

    [GeneratedRegex(@"([\\`*_~\[\]<>|])")]
    private static partial Regex MarkdownSpecial();

    [GeneratedRegex(@"^(?:#|>|[-+] |(\d+)([.)] )|-{3,}\s*$|={3,}\s*$)", RegexOptions.Multiline)]
    private static partial Regex LineStart();

    [GeneratedRegex("`+")]
    private static partial Regex Ticks();
}

/// <summary>EPUB 3 e-books: one XHTML chapter per top-level heading or page break.</summary>
internal static class EpubWriter
{
    static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    public static void Write(DDocument doc, string path, CancellationToken ct = default)
    {
        var images = new List<(string name, byte[] data, string mime)>();
        string? Image(DImage img)
        {
            // Books may only hold PNG, JPEG and GIF.
            if (DocumentPictures.ForWeb(img) is not { } picture) return null;
            var name = $"images/image{images.Count + 1}.{picture.ext}";
            images.Add((name, picture.data, HtmlWriter.Mime(picture.ext)));
            return name;
        }

        // Chapters start at each level-1 heading or page break.
        var chapters = new List<List<DBlock>> { new() };
        foreach (var block in doc.Blocks)
        {
            ct.ThrowIfCancellationRequested();
            bool start = block is DParagraph { Heading: 1 } or DParagraph { PageBreakBefore: true } || block.Section != null;
            if (start && chapters[^1].Any(b => b is not DParagraph { Inlines.Count: 0 })) chapters.Add([]);
            chapters[^1].Add(block);
        }
        // Links inside the document, which may lead to another chapter.
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < chapters.Count; i++)
            foreach (var anchor in DDocument.Paragraphs(chapters[i], boxes: true).SelectMany(p => p.Inlines).OfType<DAnchor>()) places.TryAdd(anchor.Name, i + 1);
        int current = 0;
        string? Link(string target) => !places.TryGetValue(target[1..], out int chapter) ? null
            : (chapter == current ? "" : $"chapter{chapter}.xhtml") + "#" + HtmlWriter.AnchorId(target[1..]);
        string ChapterTitle(List<DBlock> blocks, int i) =>
            blocks.OfType<DParagraph>().FirstOrDefault(b => b.Heading > 0)?.PlainText().Trim() is { Length: > 0 } h ? (h.Length > 200 ? h[..200] + "…" : h).Replace('\n', ' ') : $"Chapter {i + 1}";

        var title = SafeXml.Text(doc.Title ?? "").Trim() is { Length: > 0 } t ? t : "Document";
        var lang = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName is { Length: 2 } two and not "iv" ? two : "en";
        string Page(string pageTitle, string body) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n" +
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\" lang=\"{lang}\">\n" +
            $"<head><meta charset=\"utf-8\" /><title>{HtmlWriter.Enc(pageTitle)}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\" /></head>\n<body>\n{body}</body>\n</html>\n";

        var writer = new HtmlWriter(true, Image, ct, Link);
        var files = new List<(string name, string content)>();
        for (int i = 0; i < chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            current = i + 1;
            var body = writer.Blocks(chapters[i]);
            var chapterDoc = new DDocument();
            chapterDoc.Blocks.AddRange(chapters[i]);
            body += writer.Notes(chapterDoc.Notes());
            if (body.Length == 0) body = "<p>&#160;</p>\n";
            files.Add(($"chapter{i + 1}.xhtml", Page(ChapterTitle(chapters[i], i), body)));
        }

        var nav = new StringBuilder("<nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>\n");
        for (int i = 0; i < chapters.Count; i++)
            nav.Append($"<li><a href=\"chapter{i + 1}.xhtml\">{HtmlWriter.Enc(ChapterTitle(chapters[i], i))}</a></li>\n");
        nav.Append("</ol></nav>\n");

        var css = $"body {{ font-family: serif; line-height: 1.5; }}\n{HtmlWriter.Css}";
        var identifier = "urn:uuid:" + Guid.NewGuid();

        var package = new XElement(Opf + "package", new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "bookid"),
            new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc),
                new XElement(Dc + "identifier", new XAttribute("id", "bookid"), identifier),
                new XElement(Dc + "title", title),
                new XElement(Dc + "language", lang),
                new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))),
            new XElement(Opf + "manifest",
                new XElement(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
                new XElement(Opf + "item", new XAttribute("id", "ncx"), new XAttribute("href", "toc.ncx"), new XAttribute("media-type", "application/x-dtbncx+xml")),
                new XElement(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "style.css"), new XAttribute("media-type", "text/css")),
                files.Select((f, i) => new XElement(Opf + "item", new XAttribute("id", $"c{i + 1}"), new XAttribute("href", f.name), new XAttribute("media-type", "application/xhtml+xml"))),
                images.Select((img, i) => new XElement(Opf + "item", new XAttribute("id", $"img{i + 1}"), new XAttribute("href", img.name), new XAttribute("media-type", img.mime)))),
            new XElement(Opf + "spine", new XAttribute("toc", "ncx"), files.Select((_, i) => new XElement(Opf + "itemref", new XAttribute("idref", $"c{i + 1}")))));

        // The contents again as older (EPUB 2) readers look for them.
        var ncx = new XElement(Ncx + "ncx", new XAttribute("version", "2005-1"),
            new XElement(Ncx + "head", new XElement(Ncx + "meta", new XAttribute("name", "dtb:uid"), new XAttribute("content", identifier)),
                new XElement(Ncx + "meta", new XAttribute("name", "dtb:depth"), new XAttribute("content", "1"))),
            new XElement(Ncx + "docTitle", new XElement(Ncx + "text", title)),
            new XElement(Ncx + "navMap", chapters.Select((c, i) => new XElement(Ncx + "navPoint", new XAttribute("id", $"n{i + 1}"), new XAttribute("playOrder", i + 1),
                new XElement(Ncx + "navLabel", new XElement(Ncx + "text", SafeXml.Text(ChapterTitle(c, i)))), new XElement(Ncx + "content", new XAttribute("src", $"chapter{i + 1}.xhtml"))))));

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        // The mimetype entry must come first and be stored uncompressed.
        using (var w = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
            w.Write("application/epub+zip");
        void Put(string name, string content)
        {
            ct.ThrowIfCancellationRequested();
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Put("META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
            "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>\n");
        Put("OEBPS/content.opf", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + package);
        Put("OEBPS/nav.xhtml", Page("Contents", nav.ToString()));
        Put("OEBPS/toc.ncx", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + ncx);
        Put("OEBPS/style.css", css);
        foreach (var (name, content) in files) Put("OEBPS/" + name, content);
        foreach (var (name, data, _) in images)
        {
            ct.ThrowIfCancellationRequested();
            using var s = zip.CreateEntry("OEBPS/" + name, CompressionLevel.Optimal).Open();
            s.Write(data);
        }
    }
}
