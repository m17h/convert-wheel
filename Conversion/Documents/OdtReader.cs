using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Reads OpenDocument text (.odt): formatting, headings, lists, tables, images and page setup.</summary>
internal sealed class OdtReader
{
    internal static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    internal static readonly XNamespace St = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    internal static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    internal static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    internal static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    internal static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    internal static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    internal static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

    readonly ZipArchive _zip;
    readonly Dictionary<string, XElement> _styles = [];
    readonly Dictionary<string, XElement> _listStyles = [];
    readonly Dictionary<string, string> _fontFaces = [];
    XElement? _defaultParagraphStyle;
    DParagraph? _previous;
    string? _previousStyle;

    sealed class RunProps
    {
        public bool Bold, Italic, Underline, Strike, Super, Sub;
        public double? Size;
        public Color? Color, Highlight;
        public string? Font;

        public RunProps Clone() => (RunProps)MemberwiseClone();

        public TextStyle ToStyle() => new()
        {
            Bold = Bold, Italic = Italic, Underline = Underline, Strike = Strike,
            Superscript = Super, Subscript = Sub, Size = Size, Color = Color, Highlight = Highlight, Font = Font,
        };
    }

    public static DDocument Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return new OdtReader(zip).Build(Path.GetFileNameWithoutExtension(path));
    }

    OdtReader(ZipArchive zip) => _zip = zip;

    DDocument Build(string title)
    {
        var doc = new DDocument { Title = title };
        var styles = Load("styles.xml");
        var content = Load("content.xml") ?? throw new ConversionException("This OpenDocument file has no content.");
        foreach (var root in new[] { styles, content })
        {
            if (root == null) continue;
            foreach (var face in root.Descendants(St + "font-face"))
                if ((string?)face.Attribute(St + "name") is { } name)
                    _fontFaces[name] = ((string?)face.Attribute(Svg + "font-family") ?? name).Trim('\'', '"');
            foreach (var s in root.Descendants(St + "style"))
                if ((string?)s.Attribute(St + "name") is { } name) _styles[name] = s;
            foreach (var s in root.Descendants(Text + "list-style"))
                if ((string?)s.Attribute(St + "name") is { } name) _listStyles[name] = s;
            _defaultParagraphStyle ??= root.Descendants(St + "default-style")
                .FirstOrDefault(d => (string?)d.Attribute(St + "family") == "paragraph");
        }

        var baseRun = new RunProps { Size = 12 };
        ApplyText(baseRun, _defaultParagraphStyle);
        ApplyTextChain(baseRun, "Standard");
        doc.Font = baseRun.Font ?? "Liberation Serif";
        doc.FontSize = baseRun.Size ?? 12;

        if (styles != null) ReadPageLayout(styles, doc);

        var body = content.Element(Office + "body")?.Element(Office + "text");
        if (body != null) ReadBlocks(body.Elements(), doc.Blocks, 0, null);
        doc.NormalizeDefaults();
        return doc;
    }

    void ReadPageLayout(XElement styles, DDocument doc)
    {
        var master = styles.Descendants(St + "master-page").FirstOrDefault(m => (string?)m.Attribute(St + "name") == "Standard")
                     ?? styles.Descendants(St + "master-page").FirstOrDefault();
        var layoutName = (string?)master?.Attribute(St + "page-layout-name");
        var layout = styles.Descendants(St + "page-layout").FirstOrDefault(l => (string?)l.Attribute(St + "name") == layoutName)
                     ?? styles.Descendants(St + "page-layout").FirstOrDefault();
        var props = layout?.Element(St + "page-layout-properties");
        if (props == null) return;
        if (Len(props, Fo + "page-width") is double w && Len(props, Fo + "page-height") is double h && w > 72 && h > 72)
        {
            doc.PageWidth = w;
            doc.PageHeight = h;
        }
        doc.Margins = new Thickness(Len(props, Fo + "margin-left") ?? 56.7, Len(props, Fo + "margin-top") ?? 56.7,
            Len(props, Fo + "margin-right") ?? 56.7, Len(props, Fo + "margin-bottom") ?? 56.7);
    }

    // ------------------------------------------------------------------ blocks

    sealed class ListContext(string? styleName, int level)
    {
        public string? StyleName { get; } = styleName;
        public int Level { get; } = level;
        public int Counter;
    }

    void ReadBlocks(IEnumerable<XElement> elements, List<DBlock> output, int depth, ListContext? list)
    {
        foreach (var el in elements)
        {
            if (el.Name == Text + "p" || el.Name == Text + "h")
            {
                output.Add(ReadParagraph(el, list, el.Name == Text + "h"));
                list = null; // only the first paragraph of a list item carries the marker
            }
            else if (el.Name == Text + "list") ReadNestedList(el, output, depth, list?.StyleName);
            else if (el.Name == Table + "table") output.Add(ReadTable(el));
            else if (el.Name == Text + "section" || el.Name == Text + "index-body" || el.Name == Text + "table-of-content" ||
                     el.Name == Text + "illustration-index" || el.Name == Text + "alphabetical-index" || el.Name == Text + "bibliography")
                ReadBlocks(el.Elements(), output, depth, null);
        }
    }

    void ReadListItem(XElement item, List<DBlock> output, int level, ListContext? ctx)
    {
        bool first = true;
        foreach (var el in item.Elements())
        {
            if (el.Name == Text + "list") ReadNestedList(el, output, level + 1, ctx?.StyleName);
            else
            {
                ReadBlocks([el], output, level, first ? ctx : null);
                if (el.Name == Text + "p" || el.Name == Text + "h") first = false;
            }
        }
    }

    void ReadNestedList(XElement list, List<DBlock> output, int level, string? inheritedStyle)
    {
        var ctx = new ListContext((string?)list.Attribute(Text + "style-name") ?? inheritedStyle, level);
        foreach (var item in list.Elements().Where(e => e.Name == Text + "list-item" || e.Name == Text + "list-header"))
        {
            if (item.Name == Text + "list-item") ctx.Counter++;
            ReadListItem(item, output, level, item.Name == Text + "list-item" ? ctx : null);
        }
    }

    DParagraph ReadParagraph(XElement el, ListContext? list, bool heading)
    {
        var styleName = (string?)el.Attribute(Text + "style-name");
        var para = new DParagraph();
        var chain = StyleChain(styleName).ToList();
        ApplyParagraph(para, _defaultParagraphStyle);
        foreach (var s in chain) ApplyParagraph(para, s);

        if (heading) para.Heading = Math.Clamp((int?)el.Attribute(Text + "outline-level") ?? 1, 1, 6);
        else if (chain.Select(s => (string?)s.Attribute(St + "name")).FirstOrDefault(n => n != null && n.StartsWith("Heading_20_")) is { } hn &&
                 int.TryParse(hn["Heading_20_".Length..], out var lvl))
            para.Heading = Math.Clamp(lvl, 1, 6);

        if (list != null)
        {
            para.ListLevel = list.Level;
            var (kind, label, indent) = ListMarker(list);
            para.List = kind;
            para.ListLabel = label;
            if (para.IndentLeft == 0) para.IndentLeft = indent;
            if (para.IndentFirst == 0) para.IndentFirst = -18;
        }

        var run = new RunProps { Size = 12 };
        ApplyText(run, _defaultParagraphStyle);
        foreach (var s in chain) ApplyText(run, s);
        para.MarkSize = run.Size;

        // contextual-spacing: no gap between consecutive paragraphs of the same style (list items).
        var contextual = chain.Select(s => (string?)s.Element(St + "paragraph-properties")?.Attribute(St + "contextual-spacing"))
            .LastOrDefault(v => v != null) == "true";
        if (contextual && _previous != null && _previousStyle == styleName)
        {
            para.SpaceBefore = 0;
            _previous.SpaceAfter = 0;
        }
        _previous = para;
        _previousStyle = styleName;

        var builder = new InlineBuilder(para);
        ReadInlines(el.Nodes(), run, null, builder);
        builder.Finish();
        return para;
    }

    (ListKind kind, string label, double indent) ListMarker(ListContext list)
    {
        double indent = 36 + list.Level * 18;
        if (list.StyleName == null || !_listStyles.TryGetValue(list.StyleName, out var style))
            return (ListKind.Bullet, "•", indent);
        var levelStyle = style.Elements().FirstOrDefault(e => (int?)e.Attribute(Text + "level") == list.Level + 1);
        if (levelStyle?.Element(St + "list-level-properties")?.Element(St + "list-level-label-alignment") is { } align &&
            Len(align, Fo + "margin-left") is double margin)
            indent = margin;
        if (levelStyle?.Name == Text + "list-level-style-number")
        {
            var format = (string?)levelStyle.Attribute(St + "num-format") ?? "1";
            var label = (string?)levelStyle.Attribute(St + "num-prefix") + ListLabels.Format(list.Counter, format) +
                        (string?)levelStyle.Attribute(St + "num-suffix");
            return (ListKind.Number, label, indent);
        }
        var bullet = (string?)levelStyle?.Attribute(Text + "bullet-char") ?? "•";
        return (ListKind.Bullet, ListLabels.NormalizeBullet(bullet), indent);
    }

    /// <summary>Collapses whitespace the way ODF requires while building runs.</summary>
    sealed class InlineBuilder(DParagraph para)
    {
        bool _lastWasSpace = true;

        public void Text(string raw, TextStyle style, string? link)
        {
            var sb = new StringBuilder();
            foreach (var ch in raw)
            {
                if (ch is ' ' or '\t' or '\r' or '\n')
                {
                    if (!_lastWasSpace) sb.Append(' ');
                    _lastWasSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    _lastWasSpace = false;
                }
            }
            Literal(sb.ToString(), style, link, resetSpace: false);
        }

        public void Literal(string text, TextStyle style, string? link, bool resetSpace = true)
        {
            if (text.Length == 0) return;
            if (resetSpace) _lastWasSpace = text.EndsWith(' ');
            if (para.Inlines.LastOrDefault() is DText last && last.Style == style && last.Link == link) last.Text += text;
            else para.Inlines.Add(new DText(text, style, link));
        }

        public void Add(DInline inline)
        {
            para.Inlines.Add(inline);
            _lastWasSpace = true;
        }

        public void Finish()
        {
            if (para.Inlines.LastOrDefault() is DText last) last.Text = last.Text.TrimEnd(' ');
        }
    }

    void ReadInlines(IEnumerable<XNode> nodes, RunProps run, string? link, InlineBuilder builder)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                builder.Text(text.Value, run.ToStyle(), link);
                continue;
            }
            if (node is not XElement el) continue;
            var n = el.Name;
            if (n == Text + "span")
            {
                var inner = run.Clone();
                foreach (var s in StyleChain((string?)el.Attribute(Text + "style-name"))) ApplyText(inner, s);
                ReadInlines(el.Nodes(), inner, link, builder);
            }
            else if (n == Text + "a") ReadInlines(el.Nodes(), run, (string?)el.Attribute(XLink + "href") ?? link, builder);
            else if (n == Text + "s") builder.Literal(new string(' ', (int?)el.Attribute(Text + "c") ?? 1), run.ToStyle(), link);
            else if (n == Text + "tab") builder.Literal("\t", run.ToStyle(), link);
            else if (n == Text + "line-break") builder.Add(new DLineBreak());
            else if (n == Draw + "frame") ReadFrame(el, builder);
            else if (n == Draw + "a") ReadInlines(el.Nodes(), run, link, builder);
            else if (n == Text + "note" || n == Text + "bookmark-start" || n == Text + "annotation" || n == Office + "annotation" ||
                     n == Text + "soft-page-break" || n == Text + "bookmark" || n == Text + "bookmark-end" || n == Text + "reference-mark")
                continue;
            else ReadInlines(el.Nodes(), run, link, builder); // fields, meta, ruby: keep their visible text
        }
    }

    void ReadFrame(XElement frame, InlineBuilder builder)
    {
        double? w = Len(frame, Svg + "width"), h = Len(frame, Svg + "height");
        var image = frame.Element(Draw + "image");
        var href = (string?)image?.Attribute(XLink + "href");
        if (href != null && !href.Contains("://"))
        {
            var entry = _zip.GetEntry(href.TrimStart('.', '/'));
            var ext = Path.GetExtension(href).TrimStart('.').ToLowerInvariant();
            ext = ext switch { "jpg" or "jpeg" => "jpeg", "tif" => "tiff", _ => ext };
            if (entry != null && ext is "png" or "jpeg" or "gif" or "bmp" or "tiff")
            {
                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var data = ms.ToArray();
                var (pw, ph) = DocxReader.ImageSize(data);
                builder.Add(new DImage(data, ext, w ?? pw, h ?? ph));
                return;
            }
        }
        // Text frames: inline their paragraphs' text.
        if (frame.Element(Draw + "text-box") is { } box)
            foreach (var p in box.Descendants().Where(d => d.Name == Text + "p" || d.Name == Text + "h"))
                builder.Literal(string.Concat(p.DescendantNodes().OfType<XText>().Select(t => t.Value)) + " ", TextStyle.Plain, null);
    }

    // ------------------------------------------------------------------ tables

    DTable ReadTable(XElement el)
    {
        var table = new DTable();
        var widths = new List<double>();
        foreach (var col in el.Descendants(Table + "table-column"))
        {
            int repeat = Math.Min((int?)col.Attribute(Table + "number-columns-repeated") ?? 1, 256);
            var style = Style((string?)col.Attribute(Table + "style-name"));
            var width = Len(style?.Element(St + "table-column-properties"), St + "column-width") ?? 0;
            for (int i = 0; i < repeat; i++) widths.Add(width);
        }
        if (widths.Count > 0 && widths.All(w => w > 0)) table.ColumnWidths = widths;

        foreach (var tr in el.Descendants(Table + "table-row").Where(r => r.Ancestors(Table + "table").First() == el))
        {
            var row = new DRow { Header = tr.Parent?.Name == Table + "table-header-rows" };
            foreach (var tc in tr.Elements())
            {
                if (tc.Name == Table + "covered-table-cell") continue;
                if (tc.Name != Table + "table-cell") continue;
                var cellStyle = Style((string?)tc.Attribute(Table + "style-name"))?.Element(St + "table-cell-properties");
                var border = (string?)cellStyle?.Attribute(Fo + "border") ?? (string?)cellStyle?.Attribute(Fo + "border-bottom");
                if (border != null && border != "none") table.Borders = true;
                var cell = new DCell
                {
                    ColSpan = (int?)tc.Attribute(Table + "number-columns-spanned") ?? 1,
                    Shading = ColorAttr(cellStyle, Fo + "background-color"),
                };
                ReadBlocks(tc.Elements(), cell.Blocks, 0, null);
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0) table.Rows.Add(row);
        }
        return table;
    }

    // ------------------------------------------------------------------ styles

    XElement? Style(string? name) => name != null && _styles.TryGetValue(name, out var s) ? s : null;

    IEnumerable<XElement> StyleChain(string? name)
    {
        var chain = new List<XElement>();
        var seen = new HashSet<string>();
        while (name != null && seen.Add(name) && _styles.TryGetValue(name, out var style))
        {
            chain.Insert(0, style);
            name = (string?)style.Attribute(St + "parent-style-name");
        }
        return chain;
    }

    void ApplyTextChain(RunProps run, string name)
    {
        foreach (var s in StyleChain(name)) ApplyText(run, s);
    }

    void ApplyText(RunProps r, XElement? style)
    {
        var tp = style?.Element(St + "text-properties");
        if (tp == null) return;
        if ((string?)tp.Attribute(Fo + "font-weight") is { } weight) r.Bold = weight is "bold" or "600" or "700" or "800" or "900";
        if ((string?)tp.Attribute(Fo + "font-style") is { } fs) r.Italic = fs is "italic" or "oblique";
        if ((string?)tp.Attribute(St + "text-underline-style") is { } us) r.Underline = us != "none";
        if ((string?)tp.Attribute(St + "text-line-through-style") is { } ls) r.Strike = ls != "none";
        if ((string?)tp.Attribute(Fo + "font-size") is { } size)
        {
            if (size.EndsWith('%') && double.TryParse(size.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                r.Size = (r.Size ?? 12) * pct / 100;
            else if (Units.ParseLength(size) is double pt) r.Size = pt;
        }
        if (ColorAttr(tp, Fo + "color") is { } color) r.Color = color;
        if (ColorAttr(tp, Fo + "background-color") is { } bg) r.Highlight = bg;
        if ((string?)tp.Attribute(St + "font-name") is { } fontName)
            r.Font = _fontFaces.TryGetValue(fontName, out var family) ? family : fontName;
        if ((string?)tp.Attribute(St + "text-position") is { } pos)
        {
            r.Super = pos.StartsWith("super") || (pos.Length > 0 && char.IsDigit(pos[0]) && !pos.StartsWith("0"));
            r.Sub = pos.StartsWith("sub") || pos.StartsWith('-');
        }
    }

    static void ApplyParagraph(DParagraph p, XElement? style)
    {
        var pp = style?.Element(St + "paragraph-properties");
        if (pp == null) return;
        if ((string?)pp.Attribute(Fo + "text-align") is { } align)
            p.Align = align switch
            {
                "center" => TextAlignment.Center,
                "end" or "right" => TextAlignment.Right,
                "justify" => TextAlignment.Justify,
                _ => TextAlignment.Left,
            };
        if (Len(pp, Fo + "margin-top") is double top) p.SpaceBefore = top;
        if (Len(pp, Fo + "margin-bottom") is double bottom) p.SpaceAfter = bottom;
        if (Len(pp, Fo + "margin-left") is double left) p.IndentLeft = left;
        if (Len(pp, Fo + "text-indent") is double indent) p.IndentFirst = indent;
        if ((string?)pp.Attribute(Fo + "line-height") is { } lh && lh.EndsWith('%') &&
            double.TryParse(lh.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            p.LineSpacing = pct / 100;
        if ((string?)pp.Attribute(Fo + "break-before") == "page") p.PageBreakBefore = true;
    }

    static Color? ColorAttr(XElement? el, XName name)
    {
        var value = (string?)el?.Attribute(name);
        return value is null or "transparent" ? null : Units.ParseHex(value);
    }

    static double? Len(XElement? el, XName name) => Units.ParseLength((string?)el?.Attribute(name));

    XElement? Load(string path)
    {
        var entry = _zip.GetEntry(path);
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace).Root;
    }
}
