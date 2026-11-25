using System.Net;
using System.Text;
using System.Windows;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Writes the document model as HTML. The same code produces the XHTML chapters of an EPUB, where
/// void tags must self-close and pictures are separate files rather than embedded data.
/// </summary>
/// <param name="imageSource">Where the picture can be loaded from, or null to leave it out.</param>
/// <param name="link">In an e-book: turns a "#place" into a link to the chapter it's in (null: it isn't in the book).</param>
internal sealed class HtmlWriter(bool xhtml, Func<DImage, string?> imageSource, CancellationToken ct = default, Func<string, string?>? link = null)
{
    readonly string _end = xhtml ? " />" : ">";
    readonly Dictionary<DNote, string> _noteIds = [];
    readonly HashSet<string> _anchorIds = new(StringComparer.Ordinal);

    public static string? DataUri(DImage img) => DocumentPictures.ForWeb(img) is { } picture ? $"data:{Mime(picture.ext)};base64,{Convert.ToBase64String(picture.data)}" : null;
    public static string Mime(string ext) => ext is "jpg" or "jpeg" ? "image/jpeg" : $"image/{ext}";

    public const string Css =
        "p { margin: 0 0 0.6em; }\ntable { border-collapse: collapse; margin: 0 0 1em; }\n" +
        "td, th { padding: 4px 8px; vertical-align: top; text-align: left; }\ntable.grid td, table.grid th { border: 1px solid #999; }\n" +
        "img { max-width: 100%; height: auto; }\nli > p, li > div { margin: 0.2em 0; }\nol.plain { list-style: none; }\nol.plain > li > .marker { display: inline-block; min-width: 1.6em; }\n" +
        "pre { white-space: pre-wrap; }\n" +
        "header, footer { color: #555; font-size: 0.9em; }\nheader { border-bottom: 1px solid #ddd; margin-bottom: 1.5em; }\nfooter { border-top: 1px solid #ddd; margin-top: 2em; }\n" +
        ".notes { font-size: 0.9em; border-top: 1px solid #ccc; margin-top: 2em; padding-top: 0.5em; }\n.box { padding: 4px 8px; margin: 0 0 0.5em; }\n";

    public static void Write(DDocument doc, string path, CancellationToken ct = default)
    {
        var writer = new HtmlWriter(false, DataUri, ct);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>{Enc(doc.Title ?? "Document")}</title>\n<style>\n");
        sb.Append($"body {{ font-family: '{FontName(doc.Font) ?? "Calibri"}', Calibri, Arial, sans-serif; font-size: {Num(doc.FontSize)}pt; line-height: 1.4; max-width: 50em; margin: 2em auto; padding: 0 1em; color: #111; }}\n");
        sb.Append(Css).Append("</style>\n</head>\n<body>\n");

        var (header, footer) = doc.HeaderFooterFor(1);
        if (header is { IsEmpty: false }) sb.Append("<header>\n").Append(writer.Blocks(header.Blocks)).Append("</header>\n");
        writer.Blocks(doc.Blocks, sb);
        sb.Append(writer.Notes(doc.Notes()));
        if (footer is { IsEmpty: false }) sb.Append("<footer>\n").Append(writer.Blocks(footer.Blocks)).Append("</footer>\n");
        sb.Append("</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public string Notes(IEnumerable<DNote> allNotes)
    {
        var notes = allNotes.OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count == 0) return "";
        var sb = new StringBuilder(xhtml ? "<section class=\"notes footnotes\" epub:type=\"footnotes\">\n" : "<section class=\"notes footnotes\">\n");
        foreach (var note in notes)
        {
            sb.Append($"<div id=\"{NoteId(note)}\"><a class=\"footnote-back-ref\" href=\"#ref-{NoteId(note)}\">{Enc(FlowBridge.NoteMark(note))}</a> ");
            Blocks(FlowBridge.WithoutMark(note), sb);
            sb.Append("</div>\n");
        }
        return sb.Append("</section>\n").ToString();
    }

    /// <summary>An id for a note: its number, unless another note (a header's, say) already has that one.</summary>
    string NoteId(DNote note)
    {
        if (_noteIds.TryGetValue(note, out var id)) return id;
        var name = (note.Endnote ? "endnote-" : "footnote-") + note.Number;
        id = name;
        for (int i = 2; !_anchorIds.Add(id); i++) id = $"{name}-{i}";
        return _noteIds[note] = id;
    }

    /// <summary>A place's name as an id: no spaces, and nothing an id in an e-book can't start with.</summary>
    internal static string AnchorId(string name)
    {
        var sb = new StringBuilder("a-");
        foreach (var c in SafeXml.Text(name))
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            else sb.Append('_').Append(((int)c).ToString("x"));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ blocks

    public string Blocks(List<DBlock> blocks)
    {
        var sb = new StringBuilder();
        Blocks(blocks, sb);
        return sb.ToString();
    }

    sealed record OpenList(ListKind Kind, int Level, string Format, bool Plain, double Indent)
    {
        public int Number { get; set; }
        public string End => Kind == ListKind.Number ? "</li>\n</ol>\n" : "</li>\n</ul>\n";
    }

    void Blocks(List<DBlock> blocks, StringBuilder sb)
    {
        var open = new Stack<OpenList>();
        void CloseAll() { while (open.Count > 0) sb.Append(open.Pop().End); }
        for (int i = 0; i < blocks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var block = blocks[i];
            if (block is DParagraph { List: not ListKind.None, Heading: 0 } item)
            {
                ListItem(item, open, sb);
                continue;
            }
            // A further paragraph of a list item stays inside it, so the list carries on after it.
            if (block is DParagraph { List: ListKind.None, Heading: 0, PageBreakBefore: false } more && open.Count > 0 && more.IndentLeft >= open.Peek().Indent && open.Peek().Indent > 0
                && blocks.Skip(i + 1).Take(8).FirstOrDefault(b => b is not DParagraph { List: ListKind.None } x || x.IndentLeft < open.Peek().Indent) is DParagraph { List: not ListKind.None, Heading: 0 })
            {
                Paragraph(more, sb, inList: true);
                continue;
            }
            CloseAll();
            if (block is DParagraph p) Paragraph(p, sb, inList: false);
            else if (block is DTable t) Table(t, sb);
        }
        CloseAll();
    }

    void ListItem(DParagraph item, Stack<OpenList> open, StringBuilder sb)
    {
        while (open.Count > 0 && open.Peek().Level > item.ListLevel) sb.Append(open.Pop().End);
        bool same = open.Count > 0 && open.Peek().Level == item.ListLevel;
        var marker = item.List == ListKind.Number ? ListMarker.Parse(item.ListLabel, same ? open.Peek().Number + 1 : null, same ? open.Peek().Format : null) : null;
        // HTML numbers lists as "1."; any other marker ("(a)", "2.1", "Article 3") is written out.
        bool plain = item.List == ListKind.Number && marker is not { Prefix: "", Suffix: ".", Format: not "decimalZero", Number: > 0 };
        var format = plain ? "" : marker?.Format ?? "";
        if (same && (open.Peek().Kind != item.List || open.Peek().Plain != plain || open.Peek().Format != format))
        {
            sb.Append(open.Pop().End);
            same = false;
        }
        string value = "";
        if (!same)
        {
            if (item.List == ListKind.Bullet) sb.Append("<ul>\n");
            else
            {
                sb.Append("<ol");
                if (plain) sb.Append(" class=\"plain\"");
                if (format is "lowerLetter" or "upperLetter" or "lowerRoman" or "upperRoman")
                    sb.Append($" type=\"{format switch { "lowerLetter" => "a", "upperLetter" => "A", "lowerRoman" => "i", _ => "I" }}\"");
                if (!plain && marker!.Number != 1) sb.Append($" start=\"{marker.Number}\"");
                sb.Append(">\n");
            }
            open.Push(new OpenList(item.List, item.ListLevel, format, plain, item.IndentLeft));
        }
        else
        {
            sb.Append("</li>\n");
            if (!plain && marker != null && marker.Number != open.Peek().Number + 1) value = $" value=\"{marker.Number}\"";
        }
        open.Peek().Number = marker?.Number ?? 0;
        sb.Append("<li").Append(value);
        Style(sb, Direction(item, sb), []);
        sb.Append('>');
        if (plain && item.ListLabel is { Length: > 0 } label) sb.Append("<span class=\"marker\">").Append(Enc(label)).Append("</span> ");
        Inlines(item, sb);
        Boxes(item, sb);
        sb.Append('\n');
    }

    void Paragraph(DParagraph p, StringBuilder sb, bool inList)
    {
        if (IsCode(p))
        {
            sb.Append("<pre>");
            foreach (var inline in p.Inlines) sb.Append(inline switch { DText t => Enc(t.Text), DLineBreak => "\n", _ => "" });
            sb.Append("</pre>\n");
            return;
        }
        var tag = p.Heading > 0 ? $"h{Math.Clamp(p.Heading, 1, 6)}" : "p";
        var style = new List<string>();
        if (p.IndentLeft > 0 && !inList) style.Add($"margin-left: {Num(p.IndentLeft)}pt");
        if (p.PageBreakBefore) style.Add("break-before: page");
        sb.Append('<').Append(tag);
        Style(sb, Direction(p, sb), style);
        sb.Append('>');
        // A numbered heading keeps its number; a list marker that isn't part of a list, its marker.
        if (p.ListLabel is { Length: > 0 } label) sb.Append(Enc(label)).Append("&#160;");
        int before = sb.Length;
        Inlines(p, sb);
        if (sb.Length == before) sb.Append("&#160;");
        sb.Append("</").Append(tag).Append(">\n");
        Boxes(p, sb);
    }

    static bool IsCode(DParagraph p) => p is { List: ListKind.None, Heading: 0, RightToLeft: false, PageBreakBefore: false, IndentLeft: 0 } && MarkdownWriter.IsCode(p) && p.Inlines.All(i => i is not DText { Link: not null });

    /// <summary>Writes the direction attribute, and returns the alignment if it isn't the usual one.</summary>
    static TextAlignment? Direction(DParagraph p, StringBuilder sb)
    {
        if (p.RightToLeft) sb.Append(" dir=\"rtl\"");
        return p.Align == (p.RightToLeft ? TextAlignment.Right : TextAlignment.Left) ? null : p.Align;
    }

    static void Style(StringBuilder sb, TextAlignment? align, List<string> style)
    {
        if (align is { } a) style.Insert(0, $"text-align: {a.ToString().ToLowerInvariant()}");
        if (style.Count > 0) sb.Append(" style=\"").Append(string.Join("; ", style)).Append('"');
    }

    void Table(DTable t, StringBuilder sb)
    {
        sb.Append(t.Borders ? "<table class=\"grid\">\n" : "<table>\n");
        for (int r = 0; r < t.Rows.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var row = t.Rows[r];
            sb.Append("<tr>");
            foreach (var cell in row.Cells)
            {
                var tag = row.Header ? "th" : "td";
                sb.Append('<').Append(tag);
                if (cell.ColSpan > 1) sb.Append($" colspan=\"{cell.ColSpan}\"");
                if (Math.Min(cell.RowSpan, t.Rows.Count - r) is > 1 and var down) sb.Append($" rowspan=\"{down}\"");
                var only = cell.Blocks is [DParagraph { List: ListKind.None, Heading: 0, PageBreakBefore: false, ListLabel: null } single] && !IsCode(single) ? single : null;
                var style = new List<string>();
                if (cell.Shading is { } shade) style.Add($"background: #{Units.Hex(shade)}");
                Style(sb, only != null ? Direction(only, sb) : null, style);
                sb.Append('>');
                if (only != null)
                {
                    Inlines(only, sb);
                    Boxes(only, sb);
                }
                else Blocks(cell.Blocks, sb);
                sb.Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</table>\n");
    }

    /// <summary>Text boxes anchored in a paragraph: they hold paragraphs themselves, so they follow it.</summary>
    void Boxes(DParagraph p, StringBuilder sb)
    {
        foreach (var f in p.Inlines.OfType<DFloat>().Where(f => f.Blocks.Count > 0))
        {
            var css = FloatCss(f);
            if (f.Border is { } border) css += $"; border: 1px solid #{Units.Hex(border)}";
            if (f.Fill is { } fill) css += $"; background: #{Units.Hex(fill)}";
            sb.Append($"<div class=\"box\" style=\"{css}\">\n");
            Blocks(f.Blocks, sb);
            sb.Append("</div>\n");
        }
    }

    /// <summary>Pictures and text boxes float to the side they were aligned to.</summary>
    static string FloatCss(DFloat f)
    {
        string side = f.HorizontalAlign == FloatAlign.End || (f.HorizontalAlign == FloatAlign.None && f.X > 72) ? "right" : "left";
        return f.Wrap == FloatWrap.TopAndBottom
            ? $"display: block; width: {Num(Units.ToDip(f.Width))}px"
            : $"float: {side}; width: {Num(Units.ToDip(f.Width))}px; margin: 0 {(side == "left" ? "1em" : "0")} 0.5em {(side == "right" ? "1em" : "0")}";
    }

    // ------------------------------------------------------------------ inlines

    void Inlines(DParagraph p, StringBuilder sb)
    {
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DLineBreak:
                    sb.Append("<br").Append(_end);
                    break;
                case DAnchor anchor:
                    if (_anchorIds.Add(AnchorId(anchor.Name))) sb.Append($"<a id=\"{AnchorId(anchor.Name)}\"></a>");
                    break;
                case DNote note:
                    sb.Append($"<sup id=\"ref-{NoteId(note)}\"><a {(xhtml ? "epub:type=\"noteref\" " : "")}href=\"#{NoteId(note)}\">{Enc(FlowBridge.NoteMark(note))}</a></sup>");
                    break;
                case DField:
                    break; // page numbers have no meaning in a web page
                case DFloat f:
                    if (f.Image != null && imageSource(f.Image) is { } floating)
                        sb.Append($"<img src=\"{Enc(floating)}\" style=\"{FloatCss(f)}\" alt=\"{Enc(f.Image.Alt ?? "")}\"{_end}");
                    break;
                case DImage img:
                    if (imageSource(img) is { } source)
                        sb.Append($"<img src=\"{Enc(source)}\" width=\"{Math.Max(1, (int)Units.ToDip(img.Width))}\" height=\"{Math.Max(1, (int)Units.ToDip(img.Height))}\" alt=\"{Enc(img.Alt ?? "")}\"{_end}");
                    else if (img.Alt != null) sb.Append(Enc(img.Alt));
                    break;
                case DText t:
                    var s = t.Style;
                    var open = new StringBuilder();
                    var close = new Stack<string>();
                    void Wrap(string tag, string? attrs = null)
                    {
                        open.Append('<').Append(tag).Append(attrs).Append('>');
                        close.Push($"</{tag}>");
                    }
                    if (Href(t.Link) is { } href) Wrap("a", $" href=\"{Enc(href)}\"");
                    var css = new List<string>();
                    if (s.Font != null && FontName(s.Font) is { } font) css.Add($"font-family: '{font}'");
                    if (s.Size != null) css.Add($"font-size: {Num(s.Size.Value)}pt");
                    if (s.Color is { } c) css.Add($"color: #{Units.Hex(c)}");
                    if (s.Highlight is { } h) css.Add($"background: #{Units.Hex(h)}");
                    if (css.Count > 0) Wrap("span", $" style=\"{string.Join("; ", css)}\"");
                    if (s.Bold) Wrap("b");
                    if (s.Italic) Wrap("i");
                    if (s.Underline) Wrap("u");
                    if (s.Strike) Wrap("s");
                    if (s.Superscript) Wrap("sup");
                    if (s.Subscript) Wrap("sub");
                    sb.Append(open).Append(Spaces(Enc(t.Text)).Replace("\t", "&#8195;"));
                    while (close.Count > 0) sb.Append(close.Pop());
                    break;
            }
        }
    }

    /// <summary>Runs of spaces survive, as they do in the document.</summary>
    static string Spaces(string text)
    {
        if (!text.Contains("  ", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length + 32);
        for (int i = 0; i < text.Length; i++)
            if (text[i] == ' ' && i + 1 < text.Length && text[i + 1] == ' ') sb.Append("&#160;");
            else sb.Append(text[i]);
        return sb.ToString();
    }

    string? Href(string? target)
    {
        if (target is ['#', .. var name]) return link != null ? link(target) : "#" + AnchorId(name);
        var safe = Links.ForWeb(target);
        // A book can't refer to files next to it.
        return safe == null || !xhtml || safe.Contains(':') ? safe : null;
    }

    /// <summary>A font's name if it's safe to put in a style sheet: no quotes, brackets or the like.</summary>
    static string? FontName(string? name)
    {
        if (name == null) return null;
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '+').ToArray()).Trim();
        return clean.Length is 0 or > 80 ? null : clean;
    }

    public static string Enc(string s) => WebUtility.HtmlEncode(SafeXml.Text(s));
    static string Num(double v) => (double.IsFinite(v) ? v : 0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
