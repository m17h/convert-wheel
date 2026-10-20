using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Documents;

internal static class OdfPackage
{
    static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    public static void Save(string path, string mimeType, XElement content, XElement styles, IEnumerable<(string name, byte[] data, string type)> files)
    {
        var fileList = files.ToList();
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        // The mimetype entry must come first and be stored uncompressed.
        using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
            writer.Write(mimeType);

        var manifest = new XElement(Manifest + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", Manifest), new XAttribute(Manifest + "version", "1.3"),
            Entry("/", mimeType), Entry("content.xml", "text/xml"), Entry("styles.xml", "text/xml"));
        foreach (var f in fileList) manifest.Add(Entry(f.name, f.type));

        Put(zip, "META-INF/manifest.xml", manifest);
        Put(zip, "content.xml", content);
        Put(zip, "styles.xml", styles);
        foreach (var (name, data, _) in fileList)
        {
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(data);
        }

        XElement Entry(string fullPath, string type) =>
            new(Manifest + "file-entry", new XAttribute(Manifest + "full-path", fullPath), new XAttribute(Manifest + "media-type", type));
    }

    static void Put(ZipArchive zip, string name, XElement root)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(stream, SaveOptions.DisableFormatting);
    }

    public static XAttribute[] Namespaces() =>
    [
        new(XNamespace.Xmlns + "office", Office), new(XNamespace.Xmlns + "style", St), new(XNamespace.Xmlns + "text", Text),
        new(XNamespace.Xmlns + "table", Table), new(XNamespace.Xmlns + "draw", Draw), new(XNamespace.Xmlns + "fo", Fo),
        new(XNamespace.Xmlns + "xlink", XLink), new(XNamespace.Xmlns + "svg", Svg), new(Office + "version", "1.3"),
    ];

    public static string Len(double points) => points.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
}

/// <summary>Writes OpenDocument text (.odt).</summary>
internal sealed class OdtWriter
{
    readonly DDocument _doc;
    /// <summary>Automatic styles live in the file that uses them: content.xml for the body, styles.xml for headers/footers.</summary>
    sealed class StyleSet(string prefix)
    {
        public readonly string Prefix = prefix;
        public readonly List<XElement> Elements = [];
        public readonly Dictionary<string, string> Text = [], Para = [];
    }
    readonly StyleSet _contentStyles = new(""), _masterStyles = new("M");
    StyleSet _set;
    readonly List<(string name, byte[] data, string type)> _pictures = [];
    readonly HashSet<string> _fonts = [];
    int _tables, _frames;

    OdtWriter(DDocument doc)
    {
        _doc = doc;
        _set = _contentStyles;
    }

    public static void Write(DDocument doc, string path) => new OdtWriter(doc).Save(path);

    void Save(string path)
    {
        _fonts.Add(_doc.Font);
        var body = new XElement(Office + "text");
        Blocks(_doc.Blocks, body, _doc.ContentWidth);

        var content = new XElement(Office + "document-content", OdfPackage.Namespaces(),
            FontDecls(),
            new XElement(Office + "automatic-styles", _contentStyles.Elements),
            new XElement(Office + "body", body));
        OdfPackage.Save(path, "application/vnd.oasis.opendocument.text", content, Styles(), _pictures);
    }

    void Blocks(List<DBlock> blocks, XElement parent, double available)
    {
        foreach (var block in blocks)
        {
            if (block is DParagraph p) parent.Add(Paragraph(p, available));
            else if (block is DTable t) parent.Add(TableElement(t, available));
        }
    }

    XElement Paragraph(DParagraph p, double available)
    {
        var el = p.Heading > 0
            ? new XElement(Text + "h", new XAttribute(Text + "outline-level", p.Heading))
            : new XElement(Text + "p");
        el.Add(new XAttribute(Text + "style-name", ParagraphStyle(p)));

        if (p.ListLabel != null) el.Add(p.ListLabel, new XElement(Text + "tab"));
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var nodes = TextNodes(t.Text);
                    XElement? container = null;
                    if (TextStyleName(t.Style, p.Heading > 0) is { } styleName) container = new XElement(Text + "span", new XAttribute(Text + "style-name", styleName), nodes);
                    if (t.Link != null)
                        el.Add(new XElement(Text + "a", new XAttribute(XLink + "type", "simple"), new XAttribute(XLink + "href", t.Link), (object?)container ?? nodes));
                    else el.Add((object?)container ?? nodes);
                    break;
                case DLineBreak:
                    el.Add(new XElement(Text + "line-break"));
                    break;
                case DImage img:
                    el.Add(Frame(img, available));
                    break;
                case DField f:
                    var field = f.Kind == FieldKind.Page
                        ? new XElement(Text + "page-number", new XAttribute(Text + "select-page", "current"), "1")
                        : new XElement(Text + "page-count", "1");
                    el.Add(TextStyleName(f.Style, false) is { } fieldStyle ? new XElement(Text + "span", new XAttribute(Text + "style-name", fieldStyle), field) : field);
                    break;
                case DNote n:
                    el.Add(Note(n));
                    break;
                case DFloat f:
                    el.Add(FloatFrame(f));
                    break;
            }
        }
        return el;
    }

    XElement Note(DNote note)
    {
        var body = new XElement(Text + "note-body");
        Blocks(FlowBridge.WithoutMark(note), body, _doc.ContentWidth);
        if (!body.HasElements) body.Add(new XElement(Text + "p"));
        return new XElement(Text + "note",
            new XAttribute(Text + "id", $"{(note.Endnote ? "edn" : "ftn")}{note.Number}"),
            new XAttribute(Text + "note-class", note.Endnote ? "endnote" : "footnote"),
            new XElement(Text + "note-citation", FlowBridge.NoteMark(note)),
            body);
    }

    /// <summary>A positioned picture or text box, anchored to its paragraph.</summary>
    XElement FloatFrame(DFloat f)
    {
        _frames++;
        var styleName = $"{_set.Prefix}fr{_frames}";
        var props = new XElement(St + "graphic-properties",
            new XAttribute(St + "wrap", f.Wrap switch { FloatWrap.TopAndBottom => "none", FloatWrap.Behind or FloatWrap.InFront => "run-through", _ => "parallel" }),
            new XAttribute(St + "horizontal-pos", f.HorizontalAlign switch { FloatAlign.Start => "left", FloatAlign.Center => "center", FloatAlign.End => "right", _ => "from-left" }),
            new XAttribute(St + "horizontal-rel", f.HorizontalFrom switch { FloatFrom.Page => "page", FloatFrom.Margin => "page-content", _ => "paragraph" }),
            new XAttribute(St + "vertical-pos", f.VerticalAlign switch { FloatAlign.Start => "top", FloatAlign.Center => "middle", FloatAlign.End => "bottom", _ => "from-top" }),
            new XAttribute(St + "vertical-rel", f.VerticalFrom switch { FloatFrom.Page => "page", FloatFrom.Margin => "page-content", _ => "paragraph" }));
        if (f.Wrap is FloatWrap.Behind or FloatWrap.InFront)
            props.Add(new XAttribute(St + "run-through", f.Wrap == FloatWrap.Behind ? "background" : "foreground"));
        if (f.Image == null)
        {
            props.Add(new XAttribute(Fo + "padding", "3.6pt"));
            props.Add(f.Fill is { } fill ? new XAttribute(Fo + "background-color", "#" + Units.Hex(fill)) : new XAttribute(Fo + "background-color", "transparent"));
            props.Add(new XAttribute(Fo + "border", f.Border is { } b ? $"0.75pt solid #{Units.Hex(b)}" : "none"));
        }
        _set.Elements.Add(new XElement(St + "style", new XAttribute(St + "name", styleName), new XAttribute(St + "family", "graphic"), props));

        var frame = new XElement(Draw + "frame",
            new XAttribute(Draw + "style-name", styleName), new XAttribute(Draw + "name", $"Frame{_frames}"),
            new XAttribute(Text + "anchor-type", "paragraph"),
            new XAttribute(Svg + "width", OdfPackage.Len(f.Width)), new XAttribute(Svg + "height", OdfPackage.Len(f.Height)));
        if (f.HorizontalAlign == FloatAlign.None) frame.Add(new XAttribute(Svg + "x", OdfPackage.Len(f.X)));
        if (f.VerticalAlign == FloatAlign.None) frame.Add(new XAttribute(Svg + "y", OdfPackage.Len(f.Y)));
        if (f.Image != null)
        {
            var ext = f.Image.Ext == "jpeg" ? "jpg" : f.Image.Ext;
            var name = $"Pictures/image{_pictures.Count + 1}.{ext}";
            _pictures.Add((name, f.Image.Data, f.Image.Ext == "jpeg" ? "image/jpeg" : $"image/{f.Image.Ext}"));
            frame.Add(new XElement(Draw + "image", new XAttribute(XLink + "href", name), new XAttribute(XLink + "type", "simple"),
                new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad")));
        }
        else
        {
            var box = new XElement(Draw + "text-box");
            Blocks(f.Blocks, box, Math.Max(20, f.Width - 7.2));
            if (!box.HasElements) box.Add(new XElement(Text + "p"));
            frame.Add(box);
        }
        return frame;
    }

    /// <summary>Header/footer content for the master page; its styles go to styles.xml.</summary>
    XElement? MasterPart(string name, DHeaderFooter? part)
    {
        if (part == null) return null;
        var saved = _set;
        _set = _masterStyles;
        var element = new XElement(St + name);
        Blocks(part.Blocks, element, _doc.ContentWidth);
        if (!element.HasElements) element.Add(new XElement(Text + "p"));
        _set = saved;
        return element;
    }

    /// <summary>ODF collapses whitespace, so runs of spaces and tabs need explicit elements.</summary>
    static List<object> TextNodes(string text)
    {
        var nodes = new List<object>();
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\t')
            {
                Flush();
                nodes.Add(new XElement(Text + "tab"));
            }
            else if (c == ' ' && (i == 0 || text[i - 1] == ' '))
            {
                int count = 0;
                while (i < text.Length && text[i] == ' ') { count++; i++; }
                i--;
                Flush();
                nodes.Add(count == 1 ? new XElement(Text + "s") : new XElement(Text + "s", new XAttribute(Text + "c", count)));
            }
            else if (c >= 0x20 || c == '\n') sb.Append(c);
        }
        Flush();
        return nodes;

        void Flush()
        {
            if (sb.Length == 0) return;
            nodes.Add(new XText(sb.ToString()));
            sb.Clear();
        }
    }

    string ParagraphStyle(DParagraph p)
    {
        var props = new XElement(St + "paragraph-properties",
            new XAttribute(Fo + "margin-top", OdfPackage.Len(p.SpaceBefore)),
            new XAttribute(Fo + "margin-bottom", OdfPackage.Len(p.SpaceAfter)),
            new XAttribute(Fo + "margin-left", OdfPackage.Len(p.IndentLeft)),
            new XAttribute(Fo + "text-indent", OdfPackage.Len(p.IndentFirst)),
            new XAttribute(Fo + "text-align", p.Align switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "end",
                TextAlignment.Justify => "justify",
                _ => "start",
            }));
        if (Math.Abs(p.LineSpacing - 1) > 0.01)
            props.Add(new XAttribute(Fo + "line-height", (p.LineSpacing * 100).ToString("0", CultureInfo.InvariantCulture) + "%"));
        if (p.PageBreakBefore) props.Add(new XAttribute(Fo + "break-before", "page"));

        var parent = p.Heading > 0 ? $"Heading_20_{p.Heading}" : "Standard";
        var key = parent + props;
        if (p.Inlines.Count == 0 && p.MarkSize is double mark) key += mark;
        if (_set.Para.TryGetValue(key, out var name)) return name;

        name = $"{_set.Prefix}P{_set.Para.Count + 1}";
        var style = new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "paragraph"),
            new XAttribute(St + "parent-style-name", parent), props);
        if (p.Inlines.Count == 0 && p.MarkSize is double size)
            style.Add(new XElement(St + "text-properties", new XAttribute(Fo + "font-size", OdfPackage.Len(size))));
        _set.Elements.Add(style);
        _set.Para[key] = name;
        return name;
    }

    string? TextStyleName(TextStyle s, bool heading)
    {
        var props = new XElement(St + "text-properties");
        if (s.Bold) props.Add(new XAttribute(Fo + "font-weight", "bold"));
        else if (heading && s.Size != null) props.Add(new XAttribute(Fo + "font-weight", "normal"));
        if (s.Italic) props.Add(new XAttribute(Fo + "font-style", "italic"));
        if (s.Underline) props.Add(new XAttribute(St + "text-underline-style", "solid"), new XAttribute(St + "text-underline-width", "auto"), new XAttribute(St + "text-underline-color", "font-color"));
        if (s.Strike) props.Add(new XAttribute(St + "text-line-through-style", "solid"));
        if (s.Size is double size) props.Add(new XAttribute(Fo + "font-size", OdfPackage.Len(size)));
        if (s.Color is { } c) props.Add(new XAttribute(Fo + "color", "#" + Units.Hex(c)));
        if (s.Highlight is { } h) props.Add(new XAttribute(Fo + "background-color", "#" + Units.Hex(h)));
        if (s.Superscript) props.Add(new XAttribute(St + "text-position", "super 58%"));
        if (s.Subscript) props.Add(new XAttribute(St + "text-position", "sub 58%"));
        if (s.Font != null)
        {
            _fonts.Add(s.Font);
            props.Add(new XAttribute(St + "font-name", s.Font));
        }
        if (!props.HasAttributes) return null;

        var key = props.ToString();
        if (_set.Text.TryGetValue(key, out var name)) return name;
        name = $"{_set.Prefix}T{_set.Text.Count + 1}";
        _set.Elements.Add(new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "text"), props));
        _set.Text[key] = name;
        return name;
    }

    XElement Frame(DImage img, double available)
    {
        double scale = Math.Min(1, available / Math.Max(1, img.Width));
        var ext = img.Ext == "jpeg" ? "jpg" : img.Ext;
        var name = $"Pictures/image{_pictures.Count + 1}.{ext}";
        _pictures.Add((name, img.Data, img.Ext == "jpeg" ? "image/jpeg" : $"image/{img.Ext}"));
        _frames++;
        return new XElement(Draw + "frame",
            new XAttribute(Draw + "name", $"Image{_frames}"), new XAttribute(Text + "anchor-type", "as-char"),
            new XAttribute(Svg + "width", OdfPackage.Len(img.Width * scale)), new XAttribute(Svg + "height", OdfPackage.Len(img.Height * scale)),
            new XElement(Draw + "image", new XAttribute(XLink + "href", name), new XAttribute(XLink + "type", "simple"),
                new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad")));
    }

    XElement TableElement(DTable t, double available)
    {
        int index = ++_tables;
        int columns = Math.Max(1, t.ColumnCount);
        var widths = t.ColumnWidths != null && t.ColumnWidths.Count >= columns
            ? t.ColumnWidths.Take(columns).ToList()
            : Enumerable.Repeat(available / columns, columns).ToList();
        double total = widths.Sum();
        if (total > available) widths = widths.Select(w => w * available / total).ToList();

        var tableName = $"{_set.Prefix}Table{index}";
        _set.Elements.Add(new XElement(St + "style", new XAttribute(St + "name", tableName), new XAttribute(St + "family", "table"),
            new XElement(St + "table-properties", new XAttribute(St + "width", OdfPackage.Len(widths.Sum())), new XAttribute(Table + "align", "left"))));
        var cellStyle = $"{tableName}.Cell";
        var cellProps = new XElement(St + "table-cell-properties", new XAttribute(Fo + "padding", "2.7pt"));
        if (t.Borders) cellProps.Add(new XAttribute(Fo + "border", "0.5pt solid #000000"));
        _set.Elements.Add(new XElement(St + "style", new XAttribute(St + "name", cellStyle), new XAttribute(St + "family", "table-cell"), cellProps));

        var table = new XElement(Table + "table", new XAttribute(Table + "name", tableName), new XAttribute(Table + "style-name", tableName));
        for (int i = 0; i < columns; i++)
        {
            var colStyle = $"{tableName}.C{i + 1}";
            _set.Elements.Add(new XElement(St + "style", new XAttribute(St + "name", colStyle), new XAttribute(St + "family", "table-column"),
                new XElement(St + "table-column-properties", new XAttribute(St + "column-width", OdfPackage.Len(widths[i])))));
            table.Add(new XElement(Table + "table-column", new XAttribute(Table + "style-name", colStyle)));
        }

        var shaded = new Dictionary<string, string>();
        foreach (var row in t.Rows)
        {
            var tr = new XElement(Table + "table-row");
            int col = 0;
            foreach (var cell in row.Cells)
            {
                int span = Math.Max(1, cell.ColSpan);
                var style = cellStyle;
                if (cell.Shading is { } shade)
                {
                    var hex = Units.Hex(shade);
                    if (!shaded.TryGetValue(hex, out style!))
                    {
                        style = $"{tableName}.S{shaded.Count + 1}";
                        var props = new XElement(cellProps);
                        props.Add(new XAttribute(Fo + "background-color", "#" + hex));
                        _set.Elements.Add(new XElement(St + "style", new XAttribute(St + "name", style), new XAttribute(St + "family", "table-cell"), props));
                        shaded[hex] = style;
                    }
                }
                var tc = new XElement(Table + "table-cell", new XAttribute(Table + "style-name", style), new XAttribute(Office + "value-type", "string"));
                if (span > 1) tc.Add(new XAttribute(Table + "number-columns-spanned", span));
                double width = widths.Skip(col).Take(span).Sum();
                Blocks(cell.Blocks, tc, Math.Max(20, width - 5.4));
                if (!tc.HasElements) tc.Add(new XElement(Text + "p"));
                tr.Add(tc);
                for (int s = 1; s < span; s++) tr.Add(new XElement(Table + "covered-table-cell"));
                col += span;
            }
            for (; col < columns; col++) tr.Add(new XElement(Table + "table-cell", new XElement(Text + "p")));
            table.Add(row.Header && row == t.Rows[0] ? new XElement(Table + "table-header-rows", tr) : tr);
        }
        return table;
    }

    XElement FontDecls() => new(Office + "font-face-decls",
        _fonts.Select(f => new XElement(St + "font-face", new XAttribute(St + "name", f), new XAttribute(Svg + "font-family", f))));

    XElement Styles()
    {
        double[] sizes = [0, 20, 16, 14, 13, 12, 11];
        var styles = new XElement(Office + "styles",
            new XElement(St + "default-style", new XAttribute(St + "family", "paragraph"),
                new XElement(St + "paragraph-properties", new XAttribute(St + "tab-stop-distance", "36pt")),
                new XElement(St + "text-properties", new XAttribute(St + "font-name", _doc.Font), new XAttribute(Fo + "font-size", OdfPackage.Len(_doc.FontSize)))),
            new XElement(St + "style", new XAttribute(St + "name", "Standard"), new XAttribute(St + "family", "paragraph"), new XAttribute(St + "class", "text")));
        for (int level = 1; level <= 6; level++)
            styles.Add(new XElement(St + "style", new XAttribute(St + "name", $"Heading_20_{level}"), new XAttribute(St + "display-name", $"Heading {level}"),
                new XAttribute(St + "family", "paragraph"), new XAttribute(St + "parent-style-name", "Standard"),
                new XAttribute(St + "default-outline-level", level), new XAttribute(St + "class", "text"),
                new XElement(St + "paragraph-properties", new XAttribute(Fo + "keep-with-next", "always")),
                new XElement(St + "text-properties", new XAttribute(Fo + "font-weight", "bold"), new XAttribute(Fo + "font-size", OdfPackage.Len(sizes[level])))));

        // Headers and footers are written first so their fonts and styles are known below.
        var master = new XElement(St + "master-page", new XAttribute(St + "name", "Standard"), new XAttribute(St + "page-layout-name", "pm1"),
            MasterPart("header", _doc.Header), MasterPart("header-left", _doc.EvenHeader),
            _doc.DifferentFirstPage ? MasterPart("header-first", _doc.FirstHeader ?? new DHeaderFooter()) : null,
            MasterPart("footer", _doc.Footer), MasterPart("footer-left", _doc.EvenFooter),
            _doc.DifferentFirstPage ? MasterPart("footer-first", _doc.FirstFooter ?? new DHeaderFooter()) : null);

        // ODF margins run to the header/footer; the gap down to the body becomes their height plus spacing.
        bool hasHeader = _doc.Header != null || _doc.FirstHeader != null, hasFooter = _doc.Footer != null || _doc.FirstFooter != null;
        double top = hasHeader ? Math.Min(_doc.HeaderDistance, _doc.Margins.Top) : _doc.Margins.Top;
        double bottom = hasFooter ? Math.Min(_doc.FooterDistance, _doc.Margins.Bottom) : _doc.Margins.Bottom;
        XElement HeaderFooterStyle(string name, double gap, string spacingSide) => new(St + name,
            new XElement(St + "header-footer-properties", new XAttribute(St + "dynamic-spacing", "false"),
                new XAttribute(Fo + "min-height", OdfPackage.Len(Math.Max(0, gap - Math.Min(7.2, gap / 3)))),
                new XAttribute(Fo + spacingSide, OdfPackage.Len(Math.Min(7.2, gap / 3)))));

        var layout = new XElement(St + "page-layout", new XAttribute(St + "name", "pm1"),
            new XElement(St + "page-layout-properties",
                new XAttribute(Fo + "page-width", OdfPackage.Len(_doc.PageWidth)), new XAttribute(Fo + "page-height", OdfPackage.Len(_doc.PageHeight)),
                new XAttribute(St + "print-orientation", _doc.PageWidth > _doc.PageHeight ? "landscape" : "portrait"),
                new XAttribute(Fo + "margin-top", OdfPackage.Len(top)), new XAttribute(Fo + "margin-bottom", OdfPackage.Len(bottom)),
                new XAttribute(Fo + "margin-left", OdfPackage.Len(_doc.Margins.Left)), new XAttribute(Fo + "margin-right", OdfPackage.Len(_doc.Margins.Right))),
            hasHeader ? HeaderFooterStyle("header-style", _doc.Margins.Top - top, "margin-bottom") : null,
            hasFooter ? HeaderFooterStyle("footer-style", _doc.Margins.Bottom - bottom, "margin-top") : null);

        var automatic = new XElement(Office + "automatic-styles", layout, _masterStyles.Elements);
        return new XElement(Office + "document-styles", OdfPackage.Namespaces(),
            FontDecls(),
            styles,
            automatic,
            new XElement(Office + "master-styles", master));
    }
}
