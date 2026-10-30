using System.Net;
using System.Text;
using System.Windows;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Writes the document model as HTML. The same code produces the XHTML chapters of an EPUB, where
/// void tags must self-close and pictures are separate files rather than embedded data.
/// </summary>
internal sealed class HtmlWriter(bool xhtml, Func<DImage, string> imageSource)
{
    readonly string _end = xhtml ? " />" : ">";

    public static string DataUri(DImage img) => $"data:{Mime(img)};base64,{Convert.ToBase64String(img.Data)}";
    public static string Mime(DImage img) => img.Ext == "jpeg" ? "image/jpeg" : $"image/{img.Ext}";

    public const string Css =
        "p { margin: 0 0 0.6em; }\ntable { border-collapse: collapse; margin: 0 0 1em; }\n" +
        "td, th { padding: 4px 8px; vertical-align: top; text-align: left; }\ntable.grid td, table.grid th { border: 1px solid #999; }\n" +
        "img { max-width: 100%; height: auto; }\n" +
        "header, footer { color: #555; font-size: 0.9em; }\nheader { border-bottom: 1px solid #ddd; margin-bottom: 1.5em; }\nfooter { border-top: 1px solid #ddd; margin-top: 2em; }\n" +
        ".notes { font-size: 0.9em; border-top: 1px solid #ccc; margin-top: 2em; padding-top: 0.5em; }\n.box { padding: 4px 8px; margin: 0 0 0.5em; }\n";

    public static void Write(DDocument doc, string path)
    {
        var writer = new HtmlWriter(false, DataUri);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append($"<title>{Enc(doc.Title ?? "Document")}</title>\n<style>\n");
        sb.Append($"body {{ font-family: '{doc.Font}', Calibri, Arial, sans-serif; font-size: {Num(doc.FontSize)}pt; line-height: 1.4; max-width: 50em; margin: 2em auto; padding: 0 1em; color: #111; }}\n");
        sb.Append(Css).Append("</style>\n</head>\n<body>\n");

        var (header, footer) = doc.HeaderFooterFor(1);
        if (header is { IsEmpty: false }) sb.Append("<header>\n").Append(writer.Blocks(header.Blocks)).Append("</header>\n");
        sb.Append(writer.Blocks(doc.Blocks));
        sb.Append(writer.Notes(doc.Notes()));
        if (footer is { IsEmpty: false }) sb.Append("<footer>\n").Append(writer.Blocks(footer.Blocks)).Append("</footer>\n");
        sb.Append("</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public string Notes(IEnumerable<DNote> allNotes)
    {
        var notes = allNotes.OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count == 0) return "";
        var sb = new StringBuilder("<section class=\"notes\">\n");
        foreach (var note in notes)
        {
            sb.Append($"<div id=\"{NoteId(note)}\"><a href=\"#ref-{NoteId(note)}\">{Enc(FlowBridge.NoteMark(note))}</a> ");
            sb.Append(Blocks(FlowBridge.WithoutMark(note)));
            sb.Append("</div>\n");
        }
        return sb.Append("</section>\n").ToString();
    }

    static string NoteId(DNote note) => (note.Endnote ? "endnote-" : "footnote-") + note.Number;

    public string Blocks(List<DBlock> blocks)
    {
        var sb = new StringBuilder();
        var openLists = new Stack<(ListKind kind, int level)>();
        foreach (var block in blocks)
        {
            if (block is DParagraph { List: not ListKind.None } item)
            {
                while (openLists.Count > 0 && (openLists.Peek().level > item.ListLevel ||
                       (openLists.Peek().level == item.ListLevel && openLists.Peek().kind != item.List)))
                    sb.Append(openLists.Pop().kind == ListKind.Number ? "</ol>\n" : "</ul>\n");
                while (openLists.Count == 0 || openLists.Peek().level < item.ListLevel)
                {
                    int level = openLists.Count == 0 ? item.ListLevel : openLists.Peek().level + 1;
                    sb.Append(item.List == ListKind.Number ? "<ol>\n" : "<ul>\n");
                    openLists.Push((item.List, level));
                }
                sb.Append("<li>");
                Inlines(item, sb);
                sb.Append("</li>\n");
                continue;
            }
            while (openLists.Count > 0) sb.Append(openLists.Pop().kind == ListKind.Number ? "</ol>\n" : "</ul>\n");

            if (block is DParagraph p)
            {
                var tag = p.Heading > 0 ? $"h{p.Heading}" : "p";
                var style = new List<string>();
                if (p.Align != TextAlignment.Left) style.Add($"text-align: {p.Align.ToString().ToLowerInvariant()}");
                if (p.IndentLeft > 0) style.Add($"margin-left: {Num(p.IndentLeft)}pt");
                if (p.PageBreakBefore) style.Add("break-before: page");
                sb.Append('<').Append(tag);
                if (style.Count > 0) sb.Append(" style=\"").Append(string.Join("; ", style)).Append('"');
                sb.Append('>');
                if (p.Inlines.Count == 0) sb.Append("&#160;");
                Inlines(p, sb);
                sb.Append("</").Append(tag).Append(">\n");
            }
            else if (block is DTable t)
            {
                sb.Append(t.Borders ? "<table class=\"grid\">\n" : "<table>\n");
                foreach (var row in t.Rows)
                {
                    sb.Append("<tr>");
                    foreach (var cell in row.Cells)
                    {
                        var tag = row.Header ? "th" : "td";
                        sb.Append('<').Append(tag);
                        if (cell.ColSpan > 1) sb.Append($" colspan=\"{cell.ColSpan}\"");
                        if (cell.Shading is { } shade) sb.Append($" style=\"background: #{Units.Hex(shade)}\"");
                        sb.Append('>');
                        if (cell.Blocks.Count == 1 && cell.Blocks[0] is DParagraph only && only.List == ListKind.None) Inlines(only, sb);
                        else sb.Append(Blocks(cell.Blocks));
                        sb.Append("</").Append(tag).Append('>');
                    }
                    sb.Append("</tr>\n");
                }
                sb.Append("</table>\n");
            }
        }
        while (openLists.Count > 0) sb.Append(openLists.Pop().kind == ListKind.Number ? "</ol>\n" : "</ul>\n");
        return sb.ToString();
    }

    void Inlines(DParagraph p, StringBuilder sb)
    {
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DLineBreak:
                    sb.Append("<br").Append(_end);
                    break;
                case DNote note:
                    sb.Append($"<sup id=\"ref-{NoteId(note)}\"><a href=\"#{NoteId(note)}\">{Enc(FlowBridge.NoteMark(note))}</a></sup>");
                    break;
                case DField:
                    break; // page numbers have no meaning in a web page
                case DFloat f:
                    // Pictures and text boxes float to the side they were aligned to.
                    string side = f.HorizontalAlign == FloatAlign.End || (f.HorizontalAlign == FloatAlign.None && f.X > 72) ? "right" : "left";
                    string floatCss = f.Wrap == FloatWrap.TopAndBottom
                        ? $"display: block; width: {Num(Units.ToDip(f.Width))}px"
                        : $"float: {side}; width: {Num(Units.ToDip(f.Width))}px; margin: 0 {(side == "left" ? "1em" : "0")} 0.5em {(side == "right" ? "1em" : "0")}";
                    if (f.Image != null)
                        sb.Append($"<img src=\"{Enc(imageSource(f.Image))}\" style=\"{floatCss}\" alt=\"\"{_end}");
                    else
                    {
                        if (f.Border is { } border) floatCss += $"; border: 1px solid #{Units.Hex(border)}";
                        if (f.Fill is { } fill) floatCss += $"; background: #{Units.Hex(fill)}";
                        sb.Append($"<span class=\"box\" style=\"{floatCss}; display: inline-block\">");
                        foreach (var boxPara in f.Blocks.OfType<DParagraph>())
                        {
                            Inlines(boxPara, sb);
                            sb.Append("<br").Append(_end);
                        }
                        sb.Append("</span>");
                    }
                    break;
                case DImage img:
                    sb.Append($"<img src=\"{Enc(imageSource(img))}\" width=\"{(int)Units.ToDip(img.Width)}\" height=\"{(int)Units.ToDip(img.Height)}\" alt=\"\"{_end}");
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
                    if (t.Link != null) Wrap("a", $" href=\"{Enc(t.Link)}\"");
                    var css = new List<string>();
                    if (s.Font != null) css.Add($"font-family: '{Enc(s.Font)}'");
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
                    sb.Append(open).Append(Enc(t.Text).Replace("\t", "&#8195;"));
                    while (close.Count > 0) sb.Append(close.Pop());
                    break;
            }
        }
    }

    public static string Enc(string s) => WebUtility.HtmlEncode(s);
    static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
