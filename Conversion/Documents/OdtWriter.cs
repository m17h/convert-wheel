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
    readonly List<XElement> _autoStyles = [];
    readonly Dictionary<string, string> _textStyles = [];
    readonly Dictionary<string, string> _paraStyles = [];
    readonly List<(string name, byte[] data, string type)> _pictures = [];
    readonly HashSet<string> _fonts = [];
    int _tables, _frames;

    OdtWriter(DDocument doc) => _doc = doc;

    public static void Write(DDocument doc, string path) => new OdtWriter(doc).Save(path);

    void Save(string path)
    {
        _fonts.Add(_doc.Font);
        var body = new XElement(Office + "text");
        Blocks(_doc.Blocks, body, _doc.ContentWidth);

        var content = new XElement(Office + "document-content", OdfPackage.Namespaces(),
            FontDecls(),
            new XElement(Office + "automatic-styles", _autoStyles),
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
            }
        }
        return el;
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
        if (_paraStyles.TryGetValue(key, out var name)) return name;

        name = $"P{_paraStyles.Count + 1}";
        var style = new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "paragraph"),
            new XAttribute(St + "parent-style-name", parent), props);
        if (p.Inlines.Count == 0 && p.MarkSize is double size)
            style.Add(new XElement(St + "text-properties", new XAttribute(Fo + "font-size", OdfPackage.Len(size))));
        _autoStyles.Add(style);
        _paraStyles[key] = name;
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
        if (_textStyles.TryGetValue(key, out var name)) return name;
        name = $"T{_textStyles.Count + 1}";
        _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "text"), props));
        _textStyles[key] = name;
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

        var tableName = $"Table{index}";
        _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", tableName), new XAttribute(St + "family", "table"),
            new XElement(St + "table-properties", new XAttribute(St + "width", OdfPackage.Len(widths.Sum())), new XAttribute(Table + "align", "left"))));
        var cellStyle = $"{tableName}.Cell";
        var cellProps = new XElement(St + "table-cell-properties", new XAttribute(Fo + "padding", "2.7pt"));
        if (t.Borders) cellProps.Add(new XAttribute(Fo + "border", "0.5pt solid #000000"));
        _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", cellStyle), new XAttribute(St + "family", "table-cell"), cellProps));

        var table = new XElement(Table + "table", new XAttribute(Table + "name", tableName), new XAttribute(Table + "style-name", tableName));
        for (int i = 0; i < columns; i++)
        {
            var colStyle = $"{tableName}.C{i + 1}";
            _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", colStyle), new XAttribute(St + "family", "table-column"),
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
                        _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", style), new XAttribute(St + "family", "table-cell"), props));
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

        var layout = new XElement(St + "page-layout", new XAttribute(St + "name", "pm1"),
            new XElement(St + "page-layout-properties",
                new XAttribute(Fo + "page-width", OdfPackage.Len(_doc.PageWidth)), new XAttribute(Fo + "page-height", OdfPackage.Len(_doc.PageHeight)),
                new XAttribute(St + "print-orientation", _doc.PageWidth > _doc.PageHeight ? "landscape" : "portrait"),
                new XAttribute(Fo + "margin-top", OdfPackage.Len(_doc.Margins.Top)), new XAttribute(Fo + "margin-bottom", OdfPackage.Len(_doc.Margins.Bottom)),
                new XAttribute(Fo + "margin-left", OdfPackage.Len(_doc.Margins.Left)), new XAttribute(Fo + "margin-right", OdfPackage.Len(_doc.Margins.Right))));

        return new XElement(Office + "document-styles", OdfPackage.Namespaces(),
            FontDecls(),
            styles,
            new XElement(Office + "automatic-styles", layout),
            new XElement(Office + "master-styles",
                new XElement(St + "master-page", new XAttribute(St + "name", "Standard"), new XAttribute(St + "page-layout-name", "pm1"))));
    }
}
