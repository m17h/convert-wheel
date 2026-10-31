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
    int _footnoteNumber, _endnoteNumber;
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

    XElement? _stylesRoot;
    DDocument? _document;
    string _currentMaster = "Standard";

    /// <summary>A page style's settings (size, margins, columns), for sections that switch to it.</summary>
    PageSetup? SetupOf(string masterName)
    {
        var master = _stylesRoot!.Descendants(St + "master-page").FirstOrDefault(m => (string?)m.Attribute(St + "name") == masterName);
        var layoutName = (string?)master?.Attribute(St + "page-layout-name");
        var layout = _stylesRoot.Descendants(St + "page-layout").FirstOrDefault(l => (string?)l.Attribute(St + "name") == layoutName);
        var props = layout?.Element(St + "page-layout-properties");
        if (props == null) return null;
        double w = Len(props, Fo + "page-width") ?? 595, h = Len(props, Fo + "page-height") ?? 842;
        // The body starts below the header (and ends above the footer), like the first page style.
        double top = Len(props, Fo + "margin-top") ?? 56.7, bottom = Len(props, Fo + "margin-bottom") ?? 56.7;
        var headerProps = layout!.Element(St + "header-style")?.Element(St + "header-footer-properties");
        var footerProps = layout.Element(St + "footer-style")?.Element(St + "header-footer-properties");
        if (master!.Element(St + "header") != null) top += (Len(headerProps, Fo + "min-height") ?? 14) + (Len(headerProps, Fo + "margin-bottom") ?? 0);
        if (master.Element(St + "footer") != null) bottom += (Len(footerProps, Fo + "min-height") ?? 14) + (Len(footerProps, Fo + "margin-top") ?? 0);
        var columns = props.Element(St + "columns");
        return new PageSetup(w, h, new Thickness(Len(props, Fo + "margin-left") ?? 56.7, top, Len(props, Fo + "margin-right") ?? 56.7, bottom),
            Math.Max(1, (int?)columns?.Attribute(Fo + "column-count") ?? 1), Len(columns, Fo + "column-gap") ?? 36);
    }

    void ReadPageLayout(XElement styles, DDocument doc)
    {
        _stylesRoot = styles;
        _document = doc;
        var master = styles.Descendants(St + "master-page").FirstOrDefault(m => (string?)m.Attribute(St + "name") == "Standard")
                     ?? styles.Descendants(St + "master-page").FirstOrDefault();
        var layoutName = (string?)master?.Attribute(St + "page-layout-name");
        var layout = styles.Descendants(St + "page-layout").FirstOrDefault(l => (string?)l.Attribute(St + "name") == layoutName)
                     ?? styles.Descendants(St + "page-layout").FirstOrDefault();
        var props = layout?.Element(St + "page-layout-properties");
        if (props == null) return;
        if (props.Element(St + "columns") is { } cols)
        {
            doc.Columns = Math.Max(1, (int?)cols.Attribute(Fo + "column-count") ?? 1);
            doc.ColumnGap = Len(cols, Fo + "column-gap") ?? 36;
        }
        if (Len(props, Fo + "page-width") is double w && Len(props, Fo + "page-height") is double h && w > 72 && h > 72)
        {
            doc.PageWidth = w;
            doc.PageHeight = h;
        }
        double top = Len(props, Fo + "margin-top") ?? 56.7, bottom = Len(props, Fo + "margin-bottom") ?? 56.7;

        // ODF page margins run to the header/footer; the body starts after their height and spacing.
        DHeaderFooter? Part(string name)
        {
            var element = master?.Element(St + name);
            if (element == null || (string?)element.Attribute(St + "display") == "false") return null;
            var part = new DHeaderFooter();
            ReadBlocks(element.Elements(), part.Blocks, 0, null);
            return part;
        }
        doc.Header = Part("header");
        doc.Footer = Part("footer");
        doc.EvenHeader = Part("header-left");
        doc.EvenFooter = Part("footer-left");
        var firstHeader = Part("header-first");
        var firstFooter = Part("footer-first");
        if (firstHeader != null || firstFooter != null)
        {
            doc.DifferentFirstPage = true;
            doc.FirstHeader = firstHeader ?? doc.Header;
            doc.FirstFooter = firstFooter ?? doc.Footer;
        }

        var headerProps = layout?.Element(St + "header-style")?.Element(St + "header-footer-properties");
        var footerProps = layout?.Element(St + "footer-style")?.Element(St + "header-footer-properties");
        if (doc.Header != null)
        {
            doc.HeaderDistance = top;
            top += (Len(headerProps, Fo + "min-height") ?? 14) + (Len(headerProps, Fo + "margin-bottom") ?? 0);
        }
        if (doc.Footer != null)
        {
            doc.FooterDistance = bottom;
            bottom += (Len(footerProps, Fo + "min-height") ?? 14) + (Len(footerProps, Fo + "margin-top") ?? 0);
        }
        doc.Margins = new Thickness(Len(props, Fo + "margin-left") ?? 56.7, top, Len(props, Fo + "margin-right") ?? 56.7, bottom);
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
        // A paragraph whose style names another page style starts a new section with that page's settings.
        if (chain.Select(s => (string?)s.Attribute(St + "master-page-name")).FirstOrDefault(n => !string.IsNullOrEmpty(n)) is { } masterName
            && masterName != _currentMaster && _stylesRoot != null && SetupOf(masterName) is { } setup)
        {
            _currentMaster = masterName;
            if (_document is { Blocks.Count: 0 } first)
                (first.PageWidth, first.PageHeight, first.Margins, first.Columns, first.ColumnGap) = (setup.Width, setup.Height, setup.Margins, setup.Columns, setup.ColumnGap);
            else para.Section = setup;
        }
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
            else if (n == Text + "page-number") builder.Add(new DField(FieldKind.Page, run.ToStyle()));
            else if (n == Text + "page-count") builder.Add(new DField(FieldKind.NumPages, run.ToStyle()));
            else if (n == Text + "note")
            {
                bool endnote = (string?)el.Attribute(Text + "note-class") == "endnote";
                var note = new DNote(endnote ? ++_endnoteNumber : ++_footnoteNumber, endnote, run.ToStyle());
                ReadBlocks(el.Element(Text + "note-body")?.Elements() ?? [], note.Blocks, 0, null);
                builder.Add(note);
            }
            else if (n == Draw + "a") ReadInlines(el.Nodes(), run, link, builder);
            else if (n == Text + "note" || n == Text + "bookmark-start" || n == Text + "annotation" || n == Office + "annotation" ||
                     n == Text + "soft-page-break" || n == Text + "bookmark" || n == Text + "bookmark-end" || n == Text + "reference-mark")
                continue;
            else ReadInlines(el.Nodes(), run, link, builder); // fields, meta, ruby: keep their visible text
        }
    }

    /// <summary>Pictures and text boxes: inline when anchored "as character", otherwise positioned floats.</summary>
    void ReadFrame(XElement frame, InlineBuilder builder)
    {
        double? w = Len(frame, Svg + "width"), h = Len(frame, Svg + "height");
        var image = LoadFrameImage(frame);
        var anchor = (string?)frame.Attribute(Text + "anchor-type") ?? "paragraph";
        var box = frame.Element(Draw + "text-box");

        if (anchor == "as-char")
        {
            if (image != null) builder.Add(new DImage(image.Value.data, image.Value.ext, w ?? image.Value.w, h ?? image.Value.h));
            else if (box != null)
                foreach (var p in box.Descendants().Where(d => d.Name == Text + "p" || d.Name == Text + "h"))
                    builder.Literal(string.Concat(p.DescendantNodes().OfType<XText>().Select(t => t.Value)) + " ", TextStyle.Plain, null);
            return;
        }

        var f = new DFloat { Width = w ?? image?.w ?? 144, Height = h ?? image?.h ?? 72, X = Len(frame, Svg + "x") ?? 0, Y = Len(frame, Svg + "y") ?? 0 };
        if (image != null) f.Image = new DImage(image.Value.data, image.Value.ext, f.Width, f.Height);
        if (box != null) ReadBlocks(box.Elements(), f.Blocks, 0, null);
        if (f.Image == null && f.Blocks.Count == 0) return;

        var g = Style((string?)frame.Attribute(Draw + "style-name"))?.Element(St + "graphic-properties");
        string? Prop(XName name) => (string?)g?.Attribute(name);
        static FloatFrom From(string? rel, FloatFrom fallback) => rel switch
        {
            "page" or "page-start-margin" or "page-end-margin" => FloatFrom.Page,
            "page-content" or "page-content-top" or "page-content-bottom" => FloatFrom.Margin,
            "paragraph" or "paragraph-content" or "char" or "line" or "baseline" or "text" => FloatFrom.Paragraph,
            _ => fallback,
        };
        f.HorizontalFrom = anchor == "page" ? FloatFrom.Page : From(Prop(St + "horizontal-rel"), FloatFrom.Margin);
        f.VerticalFrom = anchor == "page" ? FloatFrom.Page : From(Prop(St + "vertical-rel"), FloatFrom.Paragraph);
        f.HorizontalAlign = Prop(St + "horizontal-pos") switch { "left" or "inside" => FloatAlign.Start, "center" => FloatAlign.Center, "right" or "outside" => FloatAlign.End, _ => FloatAlign.None };
        f.VerticalAlign = Prop(St + "vertical-pos") switch { "top" => FloatAlign.Start, "middle" => FloatAlign.Center, "bottom" => FloatAlign.End, _ => FloatAlign.None };
        f.Wrap = Prop(St + "wrap") switch
        {
            "run-through" => Prop(St + "run-through") == "background" ? FloatWrap.Behind : FloatWrap.InFront,
            "none" => FloatWrap.TopAndBottom,
            _ => FloatWrap.Square,
        };
        if (box != null)
        {
            if (Prop(Draw + "fill") != "none") f.Fill = Units.ParseHex(Prop(Draw + "fill-color") ?? Prop(Fo + "background-color"));
            if (Prop(Draw + "stroke") is not (null or "none")) f.Border = Units.ParseHex(Prop(Svg + "stroke-color")) ?? Colors.Black;
            else if (Prop(Fo + "border") is { } border && border != "none") f.Border = Units.ParseHex(border.Split(' ').LastOrDefault()) ?? Colors.Black;
        }
        builder.Add(f);
    }

    (byte[] data, string ext, double w, double h)? LoadFrameImage(XElement frame)
    {
        // Charts are drawn from their own data; their saved preview is usually a format nothing else reads.
        if ((string?)frame.Element(Draw + "object")?.Attribute(XLink + "href") is { } objectHref && Charts.ReadOdf(_zip, objectHref) is { } chart)
        {
            double cw = Len(frame, Svg + "width") ?? 360, ch = Len(frame, Svg + "height") ?? 216;
            var picture = Charts.ToImage(chart, cw, ch);
            return (picture.Data, "png", cw, ch);
        }
        var href = frame.Elements(Draw + "image").Select(i => (string?)i.Attribute(XLink + "href"))
            .OrderBy(h => h != null && h.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault();
        if (href == null || href.Contains("://")) return null;
        var entry = _zip.GetEntry(href.TrimStart('.', '/'));
        if (entry == null) return null;
        var ext = Path.GetExtension(href).TrimStart('.').ToLowerInvariant();
        ext = ext switch { "jpg" or "jpeg" => "jpeg", "tif" => "tiff", _ => ext };
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();
        if (ext is not ("png" or "jpeg" or "gif" or "bmp" or "tiff"))
        {
            // Metafiles and SVG become PNG so every output format can show them.
            if (MagickImages.Decode(data) is not { } bitmap) return null;
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            png.Save(output);
            (data, ext) = (output.ToArray(), "png");
        }
        var (pw, ph) = DocxReader.ImageSize(data);
        return (data, ext, pw, ph);
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
