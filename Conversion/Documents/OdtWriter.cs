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
        SafeXml.Clean(root);
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
    readonly CancellationToken _ct;
    /// <summary>Automatic styles live in the file that uses them: content.xml for the body, styles.xml for headers/footers.</summary>
    sealed class StyleSet(string prefix)
    {
        public readonly string Prefix = prefix;
        public readonly List<XElement> Elements = [];
        public readonly Dictionary<string, string> Text = [], Para = [];
        public readonly ListPlanner Lists = new();
    }
    readonly StyleSet _contentStyles = new(""), _masterStyles = new("M");
    StyleSet _set;
    readonly List<(string name, byte[] data, string type)> _pictures = [];
    readonly HashSet<string> _fonts = [];
    int _tables, _frames, _notes;
    /// <summary>Page settings of the sections after the first, in order (each gets its own page style).</summary>
    readonly List<PageSetup> _sections = [];

    OdtWriter(DDocument doc, CancellationToken ct)
    {
        _doc = doc;
        _ct = ct;
        _set = _contentStyles;
    }

    public static void Write(DDocument doc, string path, CancellationToken ct = default) => new OdtWriter(doc, ct).Save(path);

    void Save(string path)
    {
        _fonts.Add(_doc.Font);
        var body = new XElement(Office + "text");
        Blocks(_doc.Blocks, body, _doc.ContentWidth);
        ListStyles(_contentStyles);

        var content = new XElement(Office + "document-content", OdfPackage.Namespaces(),
            FontDecls(),
            new XElement(Office + "automatic-styles", _contentStyles.Elements),
            new XElement(Office + "body", body));
        OdfPackage.Save(path, "application/vnd.oasis.opendocument.text", content, Styles(), _pictures);
    }

    void Blocks(List<DBlock> blocks, XElement parent, double available)
    {
        // The list being written: one element per level that's open.
        ListPlanner.ListDefinition? list = null;
        var open = new List<(XElement list, int number)>();
        foreach (var block in blocks)
        {
            _ct.ThrowIfCancellationRequested();
            if (block is DParagraph { List: not ListKind.None, Heading: 0, Section: null } item && _set.Lists.Next(item) is var (planned, level) && Countable(planned.Levels[level]!)
                // Outer levels' numbers can only be shown by items inside theirs.
                && planned.Levels[level]!.Text.Count(c => c == '%') is var shown && (shown < 2 || planned == list && open.Count >= level && open.Take(level).TakeLast(shown - 1).All(o => o.number != int.MinValue)))
            {
                if (planned != list) open.Clear();
                list = planned;
                ListItem(item, planned, level, open, parent, available);
                continue;
            }
            list = null;
            // A section on its own page settings opens with a paragraph that switches to its page style.
            if (block.Section is { } setup && block is not DParagraph && parent.Name == Office + "text")
                parent.Add(new XElement(Text + "p", new XAttribute(Text + "style-name", ParagraphStyle(new DParagraph { Section = setup }))));
            if (block is DParagraph p) parent.Add(Paragraph(p, available, p.ListLabel));
            else if (block is DTable t) parent.Add(TableElement(t, available));
        }
    }

    /// <summary>Markers OpenDocument can make itself; others are written as text.</summary>
    static bool Countable(ListPlanner.LevelLook look) => look.Kind == ListKind.Bullet ? look.Text.Length == 1 : look.Format is not ("none" or "decimalZero");

    void ListItem(DParagraph p, ListPlanner.ListDefinition planned, int level, List<(XElement list, int number)> open, XElement parent, double available)
    {
        while (open.Count > level + 1) open.RemoveAt(open.Count - 1);
        while (open.Count < level + 1)
        {
            var nested = new XElement(Text + "list");
            if (open.Count == 0)
            {
                nested.Add(new XAttribute(Text + "style-name", ListStyleName(_set, planned)));
                parent.Add(nested);
            }
            else
            {
                // Inside the item above; where there is none, in an unnumbered one.
                var outer = open[^1].list;
                if (outer.LastNode is not XElement holder) outer.Add(holder = new XElement(Text + "list-header"));
                holder.Add(nested);
            }
            open.Add((nested, int.MinValue));
        }
        var item = new XElement(Text + "list-item");
        if (p.List == ListKind.Number && ListMarker.Parse(p.ListLabel, open[level].number + 1) is { Number: >= 0 } marker)
        {
            if (marker.Number != open[level].number + 1) item.Add(new XAttribute(Text + "start-value", marker.Number));
            open[level] = (open[level].list, marker.Number);
        }
        item.Add(Paragraph(p, available, null));
        open[level].list.Add(item);
    }

    static string ListStyleName(StyleSet set, ListPlanner.ListDefinition list) => $"{set.Prefix}L{list.Id}";

    static void ListStyles(StyleSet set)
    {
        foreach (var list in set.Lists.Definitions)
        {
            var style = new XElement(Text + "list-style", new XAttribute(St + "name", ListStyleName(set, list)));
            for (int i = 0; i < 9; i++)
            {
                var look = list.Levels[i];
                XElement level;
                if (look is { Kind: ListKind.Number } && Countable(look))
                {
                    // "(%1.%2)": what's around the numbers, and how many levels' numbers there are.
                    int first = look.Text.IndexOf('%'), last = look.Text.LastIndexOf('%');
                    level = new XElement(Text + "list-level-style-number", new XAttribute(Text + "level", i + 1),
                        new XAttribute(St + "num-prefix", look.Text[..first]), new XAttribute(St + "num-suffix", look.Text[Math.Min(look.Text.Length, last + 2)..]),
                        new XAttribute(St + "num-format", look.Format switch { "lowerLetter" => "a", "upperLetter" => "A", "lowerRoman" => "i", "upperRoman" => "I", _ => "1" }));
                    int shown = look.Text.Count(c => c == '%');
                    if (shown > 1) level.Add(new XAttribute(Text + "display-levels", shown));
                }
                else level = new XElement(Text + "list-level-style-bullet", new XAttribute(Text + "level", i + 1),
                    new XAttribute(Text + "bullet-char", look is { Kind: ListKind.Bullet, Text.Length: 1 } ? look.Text : i % 3 == 0 ? "•" : i % 3 == 1 ? "◦" : "▪"));
                level.Add(new XElement(St + "list-level-properties", new XAttribute(Text + "list-level-position-and-space-mode", "label-alignment"),
                    new XElement(St + "list-level-label-alignment", new XAttribute(Text + "label-followed-by", "listtab"),
                        new XAttribute(Text + "list-tab-stop-position", OdfPackage.Len(36 + i * 18)), new XAttribute(Fo + "text-indent", "-18pt"), new XAttribute(Fo + "margin-left", OdfPackage.Len(36 + i * 18)))));
                style.Add(level);
            }
            set.Elements.Add(style);
        }
    }

    XElement Paragraph(DParagraph p, double available, string? label)
    {
        var el = p.Heading > 0
            ? new XElement(Text + "h", new XAttribute(Text + "outline-level", Math.Clamp(p.Heading, 1, 10)))
            : new XElement(Text + "p");
        el.Add(new XAttribute(Text + "style-name", ParagraphStyle(p)));

        if (label != null) el.Add(label, new XElement(Text + "tab"));
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var nodes = TextNodes(t.Text);
                    XElement? container = null;
                    if (TextStyleName(t.Style, p.Heading > 0) is { } styleName) container = new XElement(Text + "span", new XAttribute(Text + "style-name", styleName), nodes);
                    var target = t.Link is ['#', .. var place] ? "#" + SafeXml.Text(place) : Links.ForDocument(t.Link);
                    if (target != null)
                        el.Add(new XElement(Text + "a", new XAttribute(XLink + "type", "simple"), new XAttribute(XLink + "href", target), (object?)container ?? nodes));
                    else el.Add((object?)container ?? nodes);
                    break;
                case DLineBreak:
                    el.Add(new XElement(Text + "line-break"));
                    break;
                case DAnchor a:
                    el.Add(new XElement(Text + "bookmark", new XAttribute(Text + "name", SafeXml.Text(a.Name))));
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
            new XAttribute(Text + "id", $"{(note.Endnote ? "edn" : "ftn")}{++_notes}"),
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
            frame.Add(PictureElements(f.Image));
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
    internal static List<object> TextNodes(string text)
    {
        text = SafeXml.Text(text);
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
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                Flush();
                nodes.Add(new XElement(Text + "line-break"));
            }
            else if (c >= 0x20) sb.Append(c);
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
                TextAlignment.Justify => "justify",
                _ => (p.Align == TextAlignment.Right) == p.RightToLeft ? "start" : "end",
            }));
        if (p.RightToLeft) props.Add(new XAttribute(St + "writing-mode", "rl-tb"));
        if (Math.Abs(p.LineSpacing - 1) > 0.01)
            props.Add(new XAttribute(Fo + "line-height", (p.LineSpacing * 100).ToString("0", CultureInfo.InvariantCulture) + "%"));
        if (p.PageBreakBefore) props.Add(new XAttribute(Fo + "break-before", "page"));

        var parent = p.Heading > 0 ? $"Heading_20_{Math.Clamp(p.Heading, 1, 6)}" : "Standard";
        var key = parent + props;
        string? master = null;
        if (p.Section is { } section)
        {
            _sections.Add(section);
            master = $"Section{_sections.Count}";
            key += master;
        }
        if (p.Inlines.Count == 0 && p.MarkSize is double mark) key += mark;
        if (_set.Para.TryGetValue(key, out var name)) return name;

        name = $"{_set.Prefix}P{_set.Para.Count + 1}";
        var style = new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", "paragraph"),
            new XAttribute(St + "parent-style-name", parent), props);
        if (master != null) style.Add(new XAttribute(St + "master-page-name", master));
        if (p.Inlines.Count == 0 && p.MarkSize is double size)
            style.Add(new XElement(St + "text-properties", new XAttribute(Fo + "font-size", OdfPackage.Len(size))));
        _set.Elements.Add(style);
        _set.Para[key] = name;
        return name;
    }

    string? TextStyleName(TextStyle s, bool heading)
    {
        var props = new XElement(St + "text-properties");
        // OpenDocument keeps the look of Asian and right-to-left text apart from Western text's.
        void Every(string name, string value) => props.Add(new XAttribute(Fo + name, value), new XAttribute(St + name + "-asian", value), new XAttribute(St + name + "-complex", value));
        if (s.Bold) Every("font-weight", "bold");
        else if (heading && s.Size != null) Every("font-weight", "normal");
        if (s.Italic) Every("font-style", "italic");
        if (s.Underline) props.Add(new XAttribute(St + "text-underline-style", "solid"), new XAttribute(St + "text-underline-width", "auto"), new XAttribute(St + "text-underline-color", "font-color"));
        if (s.Strike) props.Add(new XAttribute(St + "text-line-through-style", "solid"));
        if (s.Size is double size) Every("font-size", OdfPackage.Len(size));
        if (s.Color is { } c) props.Add(new XAttribute(Fo + "color", "#" + Units.Hex(c)));
        if (s.Highlight is { } h) props.Add(new XAttribute(Fo + "background-color", "#" + Units.Hex(h)));
        if (s.Superscript) props.Add(new XAttribute(St + "text-position", "super 58%"));
        if (s.Subscript && !s.Superscript) props.Add(new XAttribute(St + "text-position", "sub 58%"));
        if (s.Font is { Length: > 0 })
        {
            _fonts.Add(s.Font);
            props.Add(new XAttribute(St + "font-name", SafeXml.Text(s.Font)), new XAttribute(St + "font-name-complex", SafeXml.Text(s.Font)));
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
        _frames++;
        return new XElement(Draw + "frame",
            new XAttribute(Draw + "name", $"Image{_frames}"), new XAttribute(Text + "anchor-type", "as-char"),
            new XAttribute(Svg + "width", OdfPackage.Len(img.Width * scale)), new XAttribute(Svg + "height", OdfPackage.Len(img.Height * scale)),
            PictureElements(img));
    }

    readonly Dictionary<byte[], string> _pictureNames = new(ReferenceEqualityComparer.Instance);

    /// <summary>The reference to a picture, which is stored once however often it's shown, and its description.</summary>
    IEnumerable<XElement> PictureElements(DImage img)
    {
        if (!_pictureNames.TryGetValue(img.Data, out var name))
        {
            // The type is in the bytes; the name may be out of date.
            var ext = HtmlReader.ImageExt(img.Data) ?? img.Ext;
            if (ext == "jpeg") ext = "jpg";
            _pictureNames[img.Data] = name = $"Pictures/image{_pictures.Count + 1}.{ext}";
            _pictures.Add((name, img.Data, ext switch { "jpg" => "image/jpeg", "wmf" or "emf" => $"image/x-{ext}", _ => $"image/{ext}" }));
        }
        yield return new XElement(Draw + "image", new XAttribute(XLink + "href", name), new XAttribute(XLink + "type", "simple"),
            new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad"));
        if (img.Alt is { Length: > 0 } alt) yield return new XElement(Svg + "desc", alt);
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
        int rowsLeft = t.Rows.Count;
        // Header rows are a group of their own at the top.
        XElement? headerRows = null;
        bool inHeader = true;
        foreach (var (row, slots) in t.Slots())
        {
            _ct.ThrowIfCancellationRequested();
            var tr = new XElement(Table + "table-row");
            int col = 0;
            foreach (var (cell, column, span, covered) in slots)
            {
                for (; col < column; col++) tr.Add(new XElement(Table + "table-cell", new XElement(Text + "p")));
                if (covered)
                {
                    for (int s = 0; s < span; s++) tr.Add(new XElement(Table + "covered-table-cell"));
                    col += span;
                    continue;
                }
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
                if (Math.Min(cell.RowSpan, rowsLeft) is > 1 and var down) tc.Add(new XAttribute(Table + "number-rows-spanned", down));
                double width = widths.Skip(col).Take(span).Sum();
                Blocks(cell.Blocks, tc, Math.Max(20, width - 5.4));
                if (!tc.HasElements) tc.Add(new XElement(Text + "p"));
                tr.Add(tc);
                for (int s = 1; s < span; s++) tr.Add(new XElement(Table + "covered-table-cell"));
                col += span;
            }
            for (; col < columns; col++) tr.Add(new XElement(Table + "table-cell", new XElement(Text + "p")));
            rowsLeft--;
            inHeader &= row.Header;
            if (!inHeader) table.Add(tr);
            else
            {
                if (headerRows == null) table.Add(headerRows = new XElement(Table + "table-header-rows"));
                headerRows.Add(tr);
            }
        }
        return table;
    }

    XElement FontDecls() => new(Office + "font-face-decls",
        _fonts.Select(f => SafeXml.Text(f)).Distinct().Select(f => new XElement(St + "font-face", new XAttribute(St + "name", f), new XAttribute(Svg + "font-family", f.Contains(' ') && !f.Contains('\'') ? $"'{f}'" : f))));

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
                new XElement(St + "text-properties", new XAttribute(Fo + "font-weight", "bold"), new XAttribute(Fo + "font-size", OdfPackage.Len(sizes[level])),
                    new XAttribute(St + "font-weight-complex", "bold"), new XAttribute(St + "font-size-complex", OdfPackage.Len(sizes[level])))));

        // Headers and footers are written first so their fonts and styles are known below.
        var master = new XElement(St + "master-page", new XAttribute(St + "name", "Standard"), new XAttribute(St + "page-layout-name", "pm1"),
            MasterPart("header", _doc.Header), MasterPart("header-left", _doc.EvenHeader),
            _doc.DifferentFirstPage ? MasterPart("header-first", _doc.FirstHeader ?? new DHeaderFooter()) : null,
            MasterPart("footer", _doc.Footer), MasterPart("footer-left", _doc.EvenFooter),
            _doc.DifferentFirstPage ? MasterPart("footer-first", _doc.FirstFooter ?? new DHeaderFooter()) : null);

        // ODF margins run to the header/footer; the gap down to the body becomes their height plus spacing.
        bool hasHeader = _doc.Header != null || _doc.FirstHeader != null, hasFooter = _doc.Footer != null || _doc.FirstFooter != null;
        XElement HeaderFooterStyle(string name, double gap, string spacingSide) => new(St + name,
            new XElement(St + "header-footer-properties", new XAttribute(St + "dynamic-spacing", "false"),
                new XAttribute(Fo + "min-height", OdfPackage.Len(Math.Max(0, gap - Math.Min(7.2, gap / 3)))),
                new XAttribute(Fo + spacingSide, OdfPackage.Len(Math.Min(7.2, gap / 3)))));

        XElement Layout(string name, PageSetup setup)
        {
            double layoutTop = hasHeader ? Math.Min(_doc.HeaderDistance, setup.Margins.Top) : setup.Margins.Top;
            double layoutBottom = hasFooter ? Math.Min(_doc.FooterDistance, setup.Margins.Bottom) : setup.Margins.Bottom;
            var properties = new XElement(St + "page-layout-properties",
                new XAttribute(Fo + "page-width", OdfPackage.Len(setup.Width)), new XAttribute(Fo + "page-height", OdfPackage.Len(setup.Height)),
                new XAttribute(St + "print-orientation", setup.Width > setup.Height ? "landscape" : "portrait"),
                new XAttribute(Fo + "margin-top", OdfPackage.Len(layoutTop)), new XAttribute(Fo + "margin-bottom", OdfPackage.Len(layoutBottom)),
                new XAttribute(Fo + "margin-left", OdfPackage.Len(setup.Margins.Left)), new XAttribute(Fo + "margin-right", OdfPackage.Len(setup.Margins.Right)));
            if (setup.Columns > 1)
                properties.Add(new XElement(St + "columns", new XAttribute(Fo + "column-count", setup.Columns), new XAttribute(Fo + "column-gap", OdfPackage.Len(setup.ColumnGap))));
            return new XElement(St + "page-layout", new XAttribute(St + "name", name), properties,
                hasHeader ? HeaderFooterStyle("header-style", setup.Margins.Top - layoutTop, "margin-bottom") : null,
                hasFooter ? HeaderFooterStyle("footer-style", setup.Margins.Bottom - layoutBottom, "margin-top") : null);
        }
        var layouts = new List<XElement> { Layout("pm1", new PageSetup(_doc.PageWidth, _doc.PageHeight, _doc.Margins, _doc.Columns, _doc.ColumnGap)) };
        var masters = new List<XElement> { master };
        // Later sections: their own page layout, with the same headers and footers.
        for (int k = 0; k < _sections.Count; k++)
        {
            layouts.Add(Layout($"pm{k + 2}", _sections[k]));
            var copy = new XElement(master);
            copy.SetAttributeValue(St + "name", $"Section{k + 1}");
            copy.SetAttributeValue(St + "page-layout-name", $"pm{k + 2}");
            masters.Add(copy);
        }

        ListStyles(_masterStyles);
        var automatic = new XElement(Office + "automatic-styles", layouts, _masterStyles.Elements);
        return new XElement(Office + "document-styles", OdfPackage.Namespaces(),
            FontDecls(),
            styles,
            automatic,
            new XElement(Office + "master-styles", masters));
    }
}
