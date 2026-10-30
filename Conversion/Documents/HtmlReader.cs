using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Reads HTML into the document model: headings, paragraphs, lists, tables, images, links and
/// inline styles. Markdown (via Markdig) and EPUB e-books (a zip of XHTML chapters) come in this way too.
/// </summary>
internal sealed partial class HtmlReader
{
    readonly Func<string, byte[]?> _resolve;
    readonly double _maxImageWidth;
    readonly List<DBlock> _output;
    readonly NoteState _notes;
    DParagraph? _current;
    bool _breakBeforeNext;

    sealed record Context(TextStyle Style, string? Link, bool Pre, TextAlignment Align, double Indent, int ListLevel);

    /// <summary>Footnote bodies found in the page (Markdown and EPUB style), keyed by the id links point at.</summary>
    sealed class NoteState
    {
        public readonly Dictionary<string, IElement> Bodies = [];
        public int Count;
    }

    HtmlReader(List<DBlock> output, Func<string, byte[]?> resolve, double maxImageWidth, NoteState notes)
    {
        _output = output;
        _resolve = resolve;
        _maxImageWidth = maxImageWidth;
        _notes = notes;
    }

    // ------------------------------------------------------------------ entry points

    public static DDocument ReadHtml(string path)
    {
        var html = TextDecoding.Decode(File.ReadAllBytes(path));
        var dir = Path.GetDirectoryName(path)!;
        var doc = new DDocument { Title = Path.GetFileNameWithoutExtension(path) };
        Parse(doc, html, src => FromFile(dir, src), pageBreak: false);
        doc.NormalizeDefaults();
        return doc;
    }

    public static DDocument ReadMarkdown(string path)
    {
        var markdown = TextDecoding.Decode(File.ReadAllBytes(path));
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var html = Markdown.ToHtml(markdown, pipeline);
        var dir = Path.GetDirectoryName(path)!;
        var doc = new DDocument { Title = Path.GetFileNameWithoutExtension(path), Font = "Calibri", FontSize = 11 };
        Parse(doc, html, src => FromFile(dir, src), pageBreak: false);
        doc.NormalizeDefaults();
        return doc;
    }

    public static DDocument ReadEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string? Text(string name)
        {
            var entry = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var r = new StreamReader(entry.Open());
            return r.ReadToEnd();
        }
        byte[]? Bytes(string name)
        {
            var entry = zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        var container = XDocument.Parse(Text("META-INF/container.xml") ?? throw new ConversionException("This EPUB has no table of contents."));
        var opfPath = container.Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opf = XDocument.Parse(Text(opfPath) ?? throw new ConversionException("This EPUB is damaged."));
        var opfDir = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";
        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .ToDictionary(e => (string)e.Attribute("id")!, e => (string)e.Attribute("href")!);
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(e => (string?)e.Attribute("idref"))
            .Where(id => id != null && manifest.ContainsKey(id)).Select(id => Combine(opfDir, Uri.UnescapeDataString(manifest[id!]))).ToList();

        var doc = new DDocument { Title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? Path.GetFileNameWithoutExtension(path) };
        bool first = true;
        foreach (var chapter in spine)
        {
            var html = Text(chapter);
            if (html == null) continue;
            var chapterDir = chapter.Contains('/') ? chapter[..chapter.LastIndexOf('/')] : "";
            int before = doc.Blocks.Count;
            Parse(doc, html, src => src.StartsWith("data:") ? DataUri(src) : Bytes(Combine(chapterDir, Uri.UnescapeDataString(src.Split('#')[0]))), pageBreak: !first);
            if (doc.Blocks.Count > before) first = false;
        }
        doc.NormalizeDefaults();
        return doc;
    }

    static string Combine(string dir, string relative)
    {
        var parts = (dir.Length > 0 ? dir + "/" + relative : relative).Split('/').ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { parts.RemoveAt(i); if (i > 0) parts.RemoveAt(--i); i--; }
            else if (parts[i] is "." or "") parts.RemoveAt(i--);
        }
        return string.Join('/', parts);
    }

    static byte[]? FromFile(string dir, string src)
    {
        if (src.StartsWith("data:")) return DataUri(src);
        if (Uri.TryCreate(src, UriKind.Absolute, out var uri) && !uri.IsFile) return null; // never fetch from the internet
        try
        {
            var path = uri is { IsFile: true } ? uri.LocalPath : Path.Combine(dir, Uri.UnescapeDataString(src.Split('?', '#')[0]).Replace('/', '\\'));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static byte[]? DataUri(string src)
    {
        int comma = src.IndexOf(',');
        if (comma < 0 || !src[..comma].Contains(";base64")) return null;
        try { return Convert.FromBase64String(src[(comma + 1)..]); } catch (FormatException) { return null; }
    }

    static void Parse(DDocument doc, string html, Func<string, byte[]?> resolve, bool pageBreak)
    {
        var dom = new HtmlParser().ParseDocument(html);
        var notes = new NoteState { Count = doc.Notes().Count(n => !n.Endnote) };
        CollectNotes(dom, notes);
        var reader = new HtmlReader(doc.Blocks, resolve, doc.ContentWidth, notes) { _breakBeforeNext = pageBreak };
        var body = dom.Body ?? (IElement?)dom.DocumentElement;
        if (body == null) return;
        reader.Children(body, new Context(TextStyle.Plain, null, false, TextAlignment.Left, 0, 0));
        reader.EndParagraph();
    }

    /// <summary>
    /// Lifts footnote bodies out of the page so they become real notes: Markdown's "footnotes" section
    /// (list items fn:1, fn:2…) and EPUB's &lt;aside epub:type="footnote"&gt;.
    /// </summary>
    static void CollectNotes(AngleSharp.Html.Dom.IHtmlDocument dom, NoteState notes)
    {
        static bool Is(IElement e, params string[] types) =>
            (e.GetAttribute("epub:type") ?? e.GetAttribute("role") ?? "").Split(' ').Any(t => types.Contains(t.Replace("doc-", "")));
        foreach (var section in dom.All.Where(e => e.ClassList.Contains("footnotes") || Is(e, "footnotes", "endnotes", "rearnotes")).ToList())
        {
            foreach (var item in section.QuerySelectorAll("[id]").Where(i => i.LocalName is "li" or "aside" or "div" or "p" or "section"))
                notes.Bodies.TryAdd(item.Id!, item);
            section.Remove();
        }
        foreach (var aside in dom.All.Where(e => e.Id != null && Is(e, "footnote", "endnote", "rearnote")).ToList())
        {
            notes.Bodies.TryAdd(aside.Id!, aside);
            aside.Remove();
        }
    }

    void Note(IElement body, Context ctx)
    {
        var note = new DNote(++_notes.Count, false, ctx.Style with { Superscript = false });
        // Leave out the "back to text" links.
        foreach (var back in body.QuerySelectorAll("a").Where(a => a.ClassList.Contains("footnote-back-ref") || (a.GetAttribute("epub:type") ?? "").Contains("backlink") || a.TextContent.Trim() is "↩" or "↩︎").ToList())
            back.Remove();
        var reader = new HtmlReader(note.Blocks, _resolve, _maxImageWidth, _notes);
        reader.Children(body, new Context(TextStyle.Plain, null, false, TextAlignment.Left, 0, 0));
        reader.EndParagraph();
        Paragraph(ctx).Inlines.Add(note);
    }

    // ------------------------------------------------------------------ walking

    static readonly HashSet<string> Skip = ["script", "style", "head", "noscript", "template", "nav", "button", "input", "select", "textarea", "svg", "iframe", "object", "video", "audio"];
    static readonly HashSet<string> Blocks = ["p", "div", "section", "article", "header", "footer", "main", "aside", "figure", "figcaption", "address", "dl", "dt", "dd", "center", "form", "fieldset", "details", "summary"];

    void Children(INode node, Context ctx)
    {
        foreach (var child in node.ChildNodes) Node(child, ctx);
    }

    void Node(INode node, Context ctx)
    {
        if (node is IText text)
        {
            AddText(text.Data, ctx);
            return;
        }
        if (node is not IElement e) return;
        var name = e.LocalName;
        if (Skip.Contains(name) || e.GetAttribute("hidden") != null || (e.GetAttribute("style") ?? "").Replace(" ", "").Contains("display:none")) return;

        ctx = ApplyStyle(e, ctx);
        switch (name)
        {
            case "br":
                Paragraph(ctx).Inlines.Add(new DLineBreak());
                return;
            case "img":
                Image(e, ctx);
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                EndParagraph();
                var heading = Paragraph(ctx);
                heading.Heading = name[1] - '0';
                heading.SpaceBefore = 12;
                heading.SpaceAfter = 6;
                Children(e, ctx);
                EndParagraph();
                return;
            case "ul" or "ol":
                EndParagraph();
                List(e, ctx, name == "ol");
                return;
            case "li":
                EndParagraph();
                Children(e, ctx);
                EndParagraph();
                return;
            case "table":
                EndParagraph();
                Table(e, ctx);
                return;
            case "blockquote":
                EndParagraph();
                Children(e, ctx with { Indent = ctx.Indent + 36, Style = ctx.Style with { Italic = true } });
                EndParagraph();
                return;
            case "pre":
                EndParagraph();
                Children(e, ctx with { Pre = true, Style = ctx.Style with { Font = "Consolas" } });
                EndParagraph();
                return;
            case "hr":
                EndParagraph();
                var rule = Paragraph(ctx);
                rule.Align = TextAlignment.Center;
                rule.Inlines.Add(new DText("⁂", TextStyle.Plain));
                EndParagraph();
                return;
            case "a":
                var href = e.GetAttribute("href");
                if (href is ['#', .. var id] && _notes.Bodies.Remove(id, out var noteBody)) { Note(noteBody, ctx); return; }
                Children(e, href != null && !href.StartsWith('#') ? ctx with { Link = href } : ctx);
                return;
        }

        bool block = Blocks.Contains(name);
        if (block) EndParagraph();
        Children(e, ctx);
        if (block) EndParagraph();
    }

    Context ApplyStyle(IElement e, Context ctx)
    {
        var s = ctx.Style;
        switch (e.LocalName)
        {
            case "b" or "strong" or "th": s = s with { Bold = true }; break;
            case "i" or "em" or "cite" or "dfn" or "var": s = s with { Italic = true }; break;
            case "u" or "ins": s = s with { Underline = true }; break;
            case "s" or "strike" or "del": s = s with { Strike = true }; break;
            case "sup": s = s with { Superscript = true }; break;
            case "sub": s = s with { Subscript = true }; break;
            case "code" or "kbd" or "samp" or "tt": s = s with { Font = "Consolas" }; break;
            case "mark": s = s with { Highlight = Colors.Yellow }; break;
            case "small": s = s with { Size = (s.Size ?? 11) * 0.85 }; break;
            case "big": s = s with { Size = (s.Size ?? 11) * 1.2 }; break;
            case "font":
                if (Units.ParseHex(e.GetAttribute("color")) is { } fc) s = s with { Color = fc };
                if (e.GetAttribute("face") is { } face) s = s with { Font = face.Split(',')[0].Trim('\'', '"', ' ') };
                break;
        }
        var align = ctx.Align;
        if (e.GetAttribute("align") is { } a) align = ParseAlign(a, align);

        if (e.GetAttribute("style") is { } css)
            foreach (var rule in css.Split(';'))
            {
                var kv = rule.Split(':', 2);
                if (kv.Length != 2) continue;
                var value = kv[1].Trim().ToLowerInvariant();
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "font-weight": s = s with { Bold = value is "bold" or "bolder" || (int.TryParse(value, out var w) && w >= 600) }; break;
                    case "font-style": s = s with { Italic = value is "italic" or "oblique" }; break;
                    case "text-decoration" or "text-decoration-line":
                        if (value.Contains("underline")) s = s with { Underline = true };
                        if (value.Contains("line-through")) s = s with { Strike = true };
                        break;
                    case "color": if (CssColor(value) is { } color) s = s with { Color = color }; break;
                    case "background-color" or "background": if (CssColor(value) is { } bg && bg != Colors.White) s = s with { Highlight = bg }; break;
                    case "font-size": if (Units.ParseLength(value) is double size && size is > 3 and < 200) s = s with { Size = size }; break;
                    case "font-family": s = s with { Font = value.Split(',')[0].Trim('\'', '"', ' ') is { Length: > 0 } f ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(f) : s.Font }; break;
                    case "text-align": align = ParseAlign(value, align); break;
                    case "page-break-before" or "break-before": if (value is "always" or "page") { EndParagraph(); _breakBeforeNext = true; } break;
                }
            }
        return ctx with { Style = s, Align = align };
    }

    static TextAlignment ParseAlign(string value, TextAlignment fallback) => value.Trim().ToLowerInvariant() switch
    {
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.Right,
        "justify" => TextAlignment.Justify,
        "left" or "start" => TextAlignment.Left,
        _ => fallback,
    };

    static Color? CssColor(string value)
    {
        value = value.Trim();
        if (value.StartsWith('#') && value.Length == 4) value = "#" + string.Concat(value[1..].Select(c => $"{c}{c}"));
        if (Units.ParseHex(value) is { } hex) return hex;
        var rgb = RgbRegex().Match(value);
        if (rgb.Success) return Color.FromRgb(byte.Parse(rgb.Groups[1].Value), byte.Parse(rgb.Groups[2].Value), byte.Parse(rgb.Groups[3].Value));
        try { return (Color)ColorConverter.ConvertFromString(value); } catch { return null; }
    }

    [GeneratedRegex(@"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})")]
    private static partial Regex RgbRegex();

    // ------------------------------------------------------------------ paragraphs and text

    DParagraph Paragraph(Context ctx)
    {
        if (_current != null) return _current;
        _current = new DParagraph
        {
            Align = ctx.Align,
            IndentLeft = ctx.Indent,
            SpaceAfter = 8,
            PageBreakBefore = _breakBeforeNext,
        };
        _breakBeforeNext = false;
        _output.Add(_current);
        return _current;
    }

    void EndParagraph()
    {
        if (_current == null) return;
        // Trim whitespace left at the ends by collapsing.
        if (_current.Inlines.FirstOrDefault() is DText first) first.Text = first.Text.TrimStart();
        if (_current.Inlines.LastOrDefault() is DText last) last.Text = last.Text.TrimEnd();
        _current.Inlines.RemoveAll(i => i is DText { Text.Length: 0 });
        if (_current.Inlines.Count == 0 && _current.ListLabel == null && _current.Heading == 0)
        {
            _output.Remove(_current);
            if (_current.PageBreakBefore) _breakBeforeNext = true;
        }
        _current = null;
    }

    void AddText(string raw, Context ctx)
    {
        if (ctx.Pre)
        {
            var lines = raw.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) Paragraph(ctx).Inlines.Add(new DLineBreak());
                if (lines[i].Length > 0) Append(Paragraph(ctx), lines[i], ctx);
            }
            return;
        }
        var text = WhitespaceRegex().Replace(raw, " ");
        if (text.Length == 0) return;
        if (text == " " && _current == null) return;
        var p = Paragraph(ctx);
        // Collapse a space after a space (or at the start of the paragraph).
        if (text.StartsWith(' ') && (p.Inlines.Count == 0 || p.Inlines.Last() is DText { Text: var prev } && prev.EndsWith(' ') || p.Inlines.Last() is DLineBreak))
            text = text[1..];
        if (text.Length > 0) Append(p, text, ctx);
    }

    static void Append(DParagraph p, string text, Context ctx)
    {
        if (p.Inlines.LastOrDefault() is DText last && last.Style == ctx.Style && last.Link == ctx.Link) last.Text += text;
        else p.Inlines.Add(new DText(text, ctx.Style, ctx.Link));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    void Image(IElement e, Context ctx)
    {
        var src = e.GetAttribute("src");
        if (src == null || _resolve(src) is not { } data) return;
        var ext = ImageExt(data);
        if (ext == null) return;
        var (w, h) = DocxReader.ImageSize(data);
        // Width/height attributes are CSS pixels.
        if (double.TryParse(e.GetAttribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var aw) && aw > 0)
        {
            double ratio = h / Math.Max(1, w);
            w = aw * 0.75;
            h = double.TryParse(e.GetAttribute("height"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) && ah > 0 ? ah * 0.75 : w * ratio;
        }
        if (w > _maxImageWidth) (w, h) = (_maxImageWidth, h * _maxImageWidth / w);
        Paragraph(ctx).Inlines.Add(new DImage(data, ext, w, h));
    }

    /// <summary>The picture format from its first bytes (SVG and other formats aren't embeddable).</summary>
    internal static string? ImageExt(byte[] d) => d.Length < 8 ? null
        : d[0] == 0x89 && d[1] == 'P' ? "png"
        : d[0] == 0xFF && d[1] == 0xD8 ? "jpeg"
        : d[0] == 'G' && d[1] == 'I' ? "gif"
        : d[0] == 'B' && d[1] == 'M' ? "bmp"
        : (d[0] == 'I' && d[1] == 'I') || (d[0] == 'M' && d[1] == 'M') ? "tiff"
        : null;

    void List(IElement list, Context ctx, bool ordered)
    {
        int number = int.TryParse(list.GetAttribute("start"), out var start) ? start : 1;
        var type = list.GetAttribute("type");
        int level = ctx.ListLevel;
        foreach (var item in list.Children.Where(c => c.LocalName == "li"))
        {
            EndParagraph();
            var label = ordered ? Number(number++, type) : (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
            var p = Paragraph(ctx);
            p.List = ordered ? ListKind.Number : ListKind.Bullet;
            p.ListLevel = level;
            p.ListLabel = label;
            p.IndentLeft = 36 + level * 18;
            p.IndentFirst = -18;
            p.SpaceAfter = 2;
            var inner = ApplyStyle(item, ctx) with { ListLevel = level + 1, Indent = p.IndentLeft };
            foreach (var child in item.ChildNodes)
            {
                if (child is IElement { LocalName: "ul" or "ol" } nested)
                {
                    EndParagraph();
                    List(nested, inner, nested.LocalName == "ol");
                }
                else Node(child, inner);
            }
            EndParagraph();
        }
        if (_output.LastOrDefault() is DParagraph lastItem) lastItem.SpaceAfter = 8;
    }

    static string Number(int n, string? type) => type switch
    {
        "a" => ListLabels.Format(n, "lowerLetter") + ".",
        "A" => ListLabels.Format(n, "upperLetter") + ".",
        "i" => ListLabels.Format(n, "lowerRoman") + ".",
        "I" => ListLabels.Format(n, "upperRoman") + ".",
        _ => n + ".",
    };

    void Table(IElement table, Context ctx)
    {
        var t = new DTable { Borders = true }; // web tables rely on CSS for lines; gridlines keep them readable on paper
        foreach (var tr in table.QuerySelectorAll("tr").Where(r => r.Closest("table") == table))
        {
            var row = new DRow { Header = tr.Children.All(c => c.LocalName == "th") || tr.ParentElement?.LocalName == "thead" };
            foreach (var td in tr.Children.Where(c => c.LocalName is "td" or "th"))
            {
                var cell = new DCell { ColSpan = int.TryParse(td.GetAttribute("colspan"), out var span) ? Math.Max(1, span) : 1 };
                if (td.GetAttribute("bgcolor") is { } bg && CssColor(bg) is { } shade) cell.Shading = shade;
                var inner = new HtmlReader(cell.Blocks, _resolve, _maxImageWidth / Math.Max(1, tr.Children.Length), _notes);
                inner.Children(td, ApplyStyle(td, ctx with { Indent = 0, Align = TextAlignment.Left }));
                inner.EndParagraph();
                foreach (var p in cell.Blocks.OfType<DParagraph>()) p.SpaceAfter = 0;
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0) t.Rows.Add(row);
        }
        if (t.Rows.Count > 0) _output.Add(t);
    }
}
