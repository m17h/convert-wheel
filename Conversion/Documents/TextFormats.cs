using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace ConvertWheel.Conversion.Documents;

internal static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void EnsureRegistered() { }

    /// <summary>Honours a BOM, otherwise UTF-8 if the bytes are valid UTF-8, otherwise the Windows ANSI code page.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }
}

internal static class PlainText
{
    public static DDocument Read(string path)
    {
        var doc = new DDocument { Title = Path.GetFileNameWithoutExtension(path), FontSize = 11 };
        var text = TextDecoding.Decode(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.EndsWith('\n')) text = text[..^1];
        foreach (var line in text.Split('\n'))
        {
            var p = new DParagraph();
            if (line.Length > 0) p.Inlines.Add(new DText(line, TextStyle.Plain));
            doc.Blocks.Add(p);
        }
        return doc;
    }

    public static void Write(DDocument doc, string path) =>
        File.WriteAllText(path, ToText(doc), new UTF8Encoding(false));

    public static string ToText(DDocument doc)
    {
        var sb = new StringBuilder();
        var (header, footer) = doc.HeaderFooterFor(1);
        if (PartText(header) is { Length: > 0 } top) sb.Append(top).Append(Environment.NewLine).Append(Environment.NewLine);
        Blocks(doc.Blocks, sb);

        // Notes are listed at the end, after a short rule.
        var notes = doc.Notes().OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count > 0)
        {
            sb.Append(Environment.NewLine).Append("----").Append(Environment.NewLine);
            foreach (var note in notes)
            {
                var text = string.Join(" ", note.Blocks.OfType<DParagraph>().Select(p => p.PlainText().Trim()).Where(s => s.Length > 0));
                var mark = FlowBridge.NoteMark(note);
                if (text.StartsWith(mark)) text = text[mark.Length..].TrimStart();
                sb.Append('[').Append(mark).Append("] ").Append(text).Append(Environment.NewLine);
            }
        }
        if (PartText(footer) is { Length: > 0 } bottom) sb.Append(Environment.NewLine).Append(bottom).Append(Environment.NewLine);
        return sb.ToString().TrimEnd('\r', '\n') + Environment.NewLine;
    }

    /// <summary>A header/footer as text, skipping page-number fields (there are no pages in a text file).</summary>
    static string PartText(DHeaderFooter? part) => part == null ? "" :
        string.Join(Environment.NewLine, part.Blocks.OfType<DParagraph>().Select(p => p.PlainText().Trim()).Where(s => s.Length > 0 && !IsOnlyPageWords(s)));

    static bool IsOnlyPageWords(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => w.Equals("page", StringComparison.OrdinalIgnoreCase) || w.Equals("of", StringComparison.OrdinalIgnoreCase));

    static void Blocks(IEnumerable<DBlock> blocks, StringBuilder sb)
    {
        foreach (var block in blocks)
        {
            if (block is DParagraph p)
            {
                if (p.ListLabel != null) sb.Append(new string(' ', p.ListLevel * 2)).Append(p.ListLabel).Append(' ');
                sb.Append(p.PlainText().Replace("\n", Environment.NewLine)).Append(Environment.NewLine);
            }
            else if (block is DTable t)
            {
                foreach (var row in t.Rows)
                    sb.Append(string.Join('\t', row.Cells.Select(c => CellText(c)))).Append(Environment.NewLine);
                sb.Append(Environment.NewLine);
            }
        }
    }

    internal static string CellText(DCell cell) =>
        string.Join(" ", cell.Blocks.OfType<DParagraph>().Select(p => p.PlainText().Replace('\n', ' ')).Where(s => s.Length > 0));
}

internal static class HtmlWriter
{
    public static void Write(DDocument doc, string path)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append($"<title>{Enc(doc.Title ?? "Document")}</title>\n<style>\n");
        sb.Append($"body {{ font-family: '{doc.Font}', Calibri, Arial, sans-serif; font-size: {Num(doc.FontSize)}pt; line-height: 1.4; max-width: 50em; margin: 2em auto; padding: 0 1em; color: #111; }}\n");
        sb.Append("p { margin: 0 0 0.6em; }\ntable { border-collapse: collapse; margin: 0 0 1em; }\n");
        sb.Append("td, th { padding: 4px 8px; vertical-align: top; text-align: left; }\ntable.grid td, table.grid th { border: 1px solid #999; }\n");
        sb.Append("img { max-width: 100%; height: auto; }\n");
        sb.Append("header, footer { color: #555; font-size: 0.9em; }\nheader { border-bottom: 1px solid #ddd; margin-bottom: 1.5em; }\nfooter { border-top: 1px solid #ddd; margin-top: 2em; }\n");
        sb.Append(".notes { font-size: 0.9em; border-top: 1px solid #ccc; margin-top: 2em; padding-top: 0.5em; }\n.box { padding: 4px 8px; margin: 0 0 0.5em; }\n");
        sb.Append("</style>\n</head>\n<body>\n");

        var (header, footer) = doc.HeaderFooterFor(1);
        if (header is { IsEmpty: false })
        {
            sb.Append("<header>\n");
            Blocks(header.Blocks, sb);
            sb.Append("</header>\n");
        }
        Blocks(doc.Blocks, sb);

        var notes = doc.Notes().OrderBy(n => n.Endnote).ThenBy(n => n.Number).ToList();
        if (notes.Count > 0)
        {
            sb.Append("<section class=\"notes\">\n");
            foreach (var note in notes)
            {
                var mark = FlowBridge.NoteMark(note);
                sb.Append($"<div id=\"{NoteId(note)}\"><a href=\"#ref-{NoteId(note)}\">{Enc(mark)}</a> ");
                Blocks(FlowBridge.WithoutMark(note), sb);
                sb.Append("</div>\n");
            }
            sb.Append("</section>\n");
        }

        if (footer is { IsEmpty: false })
        {
            sb.Append("<footer>\n");
            Blocks(footer.Blocks, sb);
            sb.Append("</footer>\n");
        }
        sb.Append("</body>\n</html>\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static string NoteId(DNote note) => (note.Endnote ? "endnote-" : "footnote-") + note.Number;

    static void Blocks(List<DBlock> blocks, StringBuilder sb)
    {
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
                if (p.Inlines.Count == 0) sb.Append("&nbsp;");
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
                        else Blocks(cell.Blocks, sb);
                        sb.Append("</").Append(tag).Append('>');
                    }
                    sb.Append("</tr>\n");
                }
                sb.Append("</table>\n");
            }
        }
        while (openLists.Count > 0) sb.Append(openLists.Pop().kind == ListKind.Number ? "</ol>\n" : "</ul>\n");
    }

    static void Inlines(DParagraph p, StringBuilder sb)
    {
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DLineBreak:
                    sb.Append("<br>");
                    break;
                case DNote note:
                    sb.Append($"<sup id=\"ref-{NoteId(note)}\"><a href=\"#{NoteId(note)}\">{Enc(FlowBridge.NoteMark(note))}</a></sup>");
                    break;
                case DFloat f:
                    // Pictures and text boxes float to the side they were aligned to.
                    string side = f.HorizontalAlign == FloatAlign.End || (f.HorizontalAlign == FloatAlign.None && f.X > 72) ? "right" : "left";
                    string floatCss = f.Wrap == FloatWrap.TopAndBottom
                        ? $"display: block; width: {Num(Units.ToDip(f.Width))}px"
                        : $"float: {side}; width: {Num(Units.ToDip(f.Width))}px; margin: 0 {(side == "left" ? "1em" : "0")} 0.5em {(side == "right" ? "1em" : "0")}";
                    if (f.Image != null)
                    {
                        var floatMime = f.Image.Ext == "jpeg" ? "image/jpeg" : $"image/{f.Image.Ext}";
                        sb.Append($"<img src=\"data:{floatMime};base64,{Convert.ToBase64String(f.Image.Data)}\" style=\"{floatCss}\" alt=\"\">");
                    }
                    else
                    {
                        if (f.Border is { } border) floatCss += $"; border: 1px solid #{Units.Hex(border)}";
                        if (f.Fill is { } fill) floatCss += $"; background: #{Units.Hex(fill)}";
                        sb.Append($"<span class=\"box\" style=\"{floatCss}; display: inline-block\">");
                        foreach (var boxPara in f.Blocks.OfType<DParagraph>())
                        {
                            Inlines(boxPara, sb);
                            sb.Append("<br>");
                        }
                        sb.Append("</span>");
                    }
                    break;
                case DImage img:
                    var mime = img.Ext == "jpeg" ? "image/jpeg" : $"image/{img.Ext}";
                    sb.Append($"<img src=\"data:{mime};base64,{Convert.ToBase64String(img.Data)}\" width=\"{(int)Units.ToDip(img.Width)}\" height=\"{(int)Units.ToDip(img.Height)}\" alt=\"\">");
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
                    sb.Append(open).Append(Enc(t.Text).Replace("\t", "&emsp;"));
                    while (close.Count > 0) sb.Append(close.Pop());
                    break;
            }
        }
    }

    static string Enc(string s) => WebUtility.HtmlEncode(s);
    static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
