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

    readonly ZipArchive? _zip;
    readonly XElement? _flat;
    /// <summary>Styles by family and name: a paragraph style and a text style may share a name.</summary>
    readonly Dictionary<(string family, string name), XElement> _styles = [];
    readonly Dictionary<string, XElement> _listStyles = [];
    XElement? _outlineStyle;
    readonly int[] _outline = new int[10];
    int _depth, _tableDepth, _cells;
    readonly Dictionary<string, string> _fontFaces = [];
    XElement? _defaultParagraphStyle;
    int _footnoteNumber, _endnoteNumber;
    DParagraph? _previous;
    string? _previousStyle;

    sealed class RunProps
    {
        public bool Bold, Italic, Underline, Strike, Super, Sub, Hidden;
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

    public static DDocument Read(string path) => DocumentFiles.Open(path, DocumentShape.OpenDocument, () => ReadOdt(path));

    internal static DDocument ReadOdt(string path)
    {
        // A flat file is one XML document rather than a package, whatever it's called.
        bool flat;
        using (var file = File.OpenRead(path)) flat = file.ReadByte() != 'P' || file.ReadByte() != 'K';
        if (flat)
        {
            using var stream = File.OpenRead(path);
            return new OdtReader(null, PackageResources.LoadXml(stream, LoadOptions.PreserveWhitespace).Root).Build(Path.GetFileNameWithoutExtension(path));
        }
        using var zip = ZipFile.OpenRead(path);
        return new OdtReader(zip).Build(Path.GetFileNameWithoutExtension(path));
    }

    OdtReader(ZipArchive? zip, XElement? flat = null) => (_zip, _flat) = (zip, flat);

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
                    _fontFaces[name] = ((string?)face.Attribute(Svg + "font-family") ?? name).Split(',')[0].Trim().Trim('\'', '"') is { Length: > 0 } family ? family : name;
            foreach (var s in root.Descendants(St + "style"))
                if ((string?)s.Attribute(St + "name") is { } name) _styles[((string?)s.Attribute(St + "family") ?? "", name)] = s;
            _outlineStyle ??= root.Descendants(Text + "outline-style").FirstOrDefault();
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
        // Pictures fixed to a page, after the last paragraph.
        if (_pageFrames.Count > 0)
        {
            var holder = doc.Blocks.OfType<DParagraph>().LastOrDefault();
            if (holder == null) doc.Blocks.Add(holder = new DParagraph());
            holder.Inlines.AddRange(_pageFrames);
        }
        DocumentLimits.Finish(doc);
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
            Math.Max(1, Int(columns, Fo + "column-count") ?? 1), Len(columns, Fo + "column-gap") ?? 36);
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
            doc.Columns = Math.Max(1, Int(cols, Fo + "column-count") ?? 1);
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
            _previous = null;
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

    sealed class ListContext(string? styleName, int level, ListContext? outer)
    {
        public string? StyleName { get; } = styleName;
        public int Level { get; } = level;
        public ListContext? Outer { get; } = outer;
        /// <summary>The number of the current item.</summary>
        public int? Counter;
    }

    /// <summary>Where each list got to, for lists that carry on from another (by id, and by style).</summary>
    readonly Dictionary<string, int> _listNumbers = [];
    /// <summary>Pictures and text boxes fixed to a page, waiting for a paragraph to belong to.</summary>
    readonly List<DFloat> _pageFrames = [];
    readonly List<string> _anchors = [];

    static readonly HashSet<XName> Containers =
    [
        Text + "section", Text + "index-body", Text + "index-title", Text + "table-of-content", Text + "illustration-index", Text + "table-index", Text + "object-index",
        Text + "user-index", Text + "alphabetical-index", Text + "bibliography", Text + "list-header", Office + "text",
    ];

    void ReadBlocks(IEnumerable<XElement> elements, List<DBlock> output, int depth, ListContext? list)
    {
        _depth++;
        bool deepEnough = !DocumentLimits.CanGoDeeper(_depth);
        foreach (var el in elements)
        {
            if (deepEnough) Flatten(el, output);
            else if (el.Name == Text + "p" || el.Name == Text + "h")
            {
                if ((string?)el.Attribute(Text + "display") == "none") continue;
                output.Add(ReadParagraph(el, list, el.Name == Text + "h"));
                list = null; // only the first paragraph of a list item carries the marker
            }
            else if (el.Name == Text + "list") ReadNestedList(el, output, depth, list);
            else if (el.Name == Text + "numbered-paragraph") ReadNumberedParagraph(el, output);
            else if (el.Name == Table + "table")
            {
                if (_tableDepth >= DocumentLimits.TableDepth) { Flatten(el, output); continue; }
                if (ReadTable(el) is { Rows.Count: > 0 } table) output.Add(table);
            }
            else if (Containers.Contains(el.Name))
            {
                if ((string?)el.Attribute(Text + "display") != "none") ReadBlocks(el.Elements(), output, depth, null);
            }
            else if (el.Name.Namespace == Draw || el.Name.LocalName == "scene" || el.Name == Draw + "a")
            {
                // Fixed to the page rather than to any text.
                foreach (var frame in Drawings(el))
                    if (Positioned(frame) is { } f) _pageFrames.Add(f);
            }
        }
        _depth--;
    }

    /// <summary>The separate drawings in an element: groups are taken apart.</summary>
    static IEnumerable<XElement> Drawings(XElement el)
    {
        var pending = new Stack<XElement>([el]);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (e.Name == Draw + "g" || e.Name == Draw + "a" || e.Name.LocalName == "scene") foreach (var child in e.Elements().Reverse()) pending.Push(child);
            else if (e.Name.Namespace == Draw) yield return e;
        }
    }

    /// <summary>Content nested too deeply to lay out: its paragraphs, one after another.</summary>
    static void Flatten(XElement el, List<DBlock> output)
    {
        foreach (var p in el.DescendantsAndSelf().Where(e => e.Name == Text + "p" || e.Name == Text + "h"))
        {
            var text = string.Concat(p.DescendantNodes().OfType<XText>().Where(t => t.Ancestors().First(a => a.Name == Text + "p" || a.Name == Text + "h") == p).Select(t => t.Value)).Trim();
            if (text.Length == 0) continue;
            var para = new DParagraph();
            para.Inlines.Add(new DText(text, TextStyle.Plain));
            output.Add(para);
        }
    }

    void ReadListItem(XElement item, List<DBlock> output, int level, ListContext ctx, bool numbered)
    {
        bool first = true;
        foreach (var el in item.Elements())
        {
            if (el.Name == Text + "list") ReadNestedList(el, output, level + 1, ctx);
            else
            {
                int before = output.Count;
                ReadBlocks([el], output, level, first && numbered ? ctx : null);
                if (el.Name == Text + "p" || el.Name == Text + "h") first = false;
                // A further paragraph of the item lines up with its text.
                if (!(first && numbered) && output.Count > before && output[before] is DParagraph { ListLabel: null, IndentLeft: 0 } more && numbered)
                    more.IndentLeft = ListMarker(ctx).indent;
            }
        }
    }

    void ReadNestedList(XElement list, List<DBlock> output, int level, ListContext? outer)
    {
        _depth++;
        if (!DocumentLimits.CanGoDeeper(_depth)) { Flatten(list, output); _depth--; return; }
        var ctx = new ListContext((string?)list.Attribute(Text + "style-name") ?? outer?.StyleName, level, outer);
        var id = (string?)list.Attribute(XNamespace.Xml + "id") ?? (string?)list.Attribute(Text + "id");
        var styleKey = $"style {ctx.StyleName} {level}";
        // Carrying on from a particular list, or the last one of the same style.
        if ((string?)list.Attribute(Text + "continue-list") is { } continued && _listNumbers.TryGetValue($"id {continued} {level}", out int reached)) ctx.Counter = reached;
        else if ((string?)list.Attribute(Text + "continue-numbering") == "true" && _listNumbers.TryGetValue(styleKey, out reached)) ctx.Counter = reached;
        foreach (var item in list.Elements().Where(e => e.Name == Text + "list-item" || e.Name == Text + "list-header"))
        {
            bool numbered = item.Name == Text + "list-item";
            if (numbered)
            {
                if (Int(item, Text + "start-value") is { } start) ctx.Counter = start;
                else ctx.Counter = ctx.Counter is { } n ? (n < int.MaxValue ? n + 1 : n) : Int(LevelStyle(ctx), Text + "start-value") ?? 1;
            }
            ReadListItem(item, output, level, ctx, numbered);
            if (ctx.Counter is { } now)
            {
                _listNumbers[styleKey] = now;
                if (id != null) _listNumbers[$"id {id} {level}"] = now;
            }
        }
        _depth--;
    }

    /// <summary>A numbered paragraph outside any list: its number carries on from the last with the same list id or style.</summary>
    void ReadNumberedParagraph(XElement el, List<DBlock> output)
    {
        int level = Math.Clamp((Int(el, Text + "level") ?? 1) - 1, 0, 9);
        var style = (string?)el.Attribute(Text + "style-name");
        var key = $"numbered {(string?)el.Attribute(Text + "list-id") ?? style}";
        var ctx = new ListContext(style, level, null);
        ctx.Counter = Int(el, Text + "start-value") ?? (_listNumbers.TryGetValue($"{key} {level}", out int reached) ? reached + 1 : Int(LevelStyle(ctx), Text + "start-value") ?? 1);
        _listNumbers[$"{key} {level}"] = ctx.Counter.Value;
        for (int i = level + 1; i < 10; i++) _listNumbers.Remove($"{key} {i}");
        ReadBlocks(el.Elements().Where(e => e.Name == Text + "p" || e.Name == Text + "h"), output, level, ctx);
    }

    DParagraph ReadParagraph(XElement el, ListContext? list, bool heading)
    {
        var styleName = (string?)el.Attribute(Text + "style-name");
        var para = new DParagraph();
        var chain = StyleChain(styleName, "paragraph").ToList();
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

        if (heading)
        {
            int outline = Math.Clamp(Int(el, Text + "outline-level") ?? 1, 1, 10);
            para.Heading = Math.Min(outline, 6);
            if (list == null && (string?)el.Attribute(Text + "is-list-header") != "true") para.ListLabel = OutlineNumber(el, outline);
            if (para.ListLabel != null) para.List = ListKind.Number;
        }
        else if (chain.Select(s => (string?)s.Attribute(St + "name")).FirstOrDefault(n => n != null && n.StartsWith("Heading_20_")) is { } hn &&
                 int.TryParse(hn["Heading_20_".Length..], out var lvl))
            para.Heading = Math.Clamp(lvl, 1, 6);

        if (list != null)
        {
            para.ListLevel = Math.Min(list.Level, 30);
            var (kind, label, indent) = ListMarker(list);
            para.List = kind;
            para.ListLabel = label;
            if (para.IndentLeft == 0) para.IndentLeft = indent;
            if (para.IndentFirst == 0) para.IndentFirst = -18;
            if (string.IsNullOrWhiteSpace(label)) (para.List, para.ListLabel) = (ListKind.None, null);
        }
        if (chain.Select(s => (string?)s.Element(St + "paragraph-properties")?.Attribute(St + "writing-mode")).LastOrDefault(m => m is not (null or "page")) is "rl-tb" or "rl")
        {
            para.RightToLeft = true;
            // Text that starts on its starting side; "left" and "right" are sides already.
            var align = chain.Select(s => (string?)s.Element(St + "paragraph-properties")?.Attribute(Fo + "text-align")).LastOrDefault(a => a != null);
            if (align is null or "start") para.Align = TextAlignment.Right;
            else if (align == "end") para.Align = TextAlignment.Left;
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
        foreach (var anchor in _anchors) para.Inlines.Add(new DAnchor(anchor));
        _anchors.Clear();
        if (_tableDepth == 0 && list == null)
        {
            foreach (var frame in _pageFrames) para.Inlines.Add(frame);
            _pageFrames.Clear();
        }
        ReadInlines(el.Nodes(), run, null, builder);
        builder.Finish();
        return para;
    }

    XElement? LevelStyle(ListContext list) =>
        list.StyleName != null && _listStyles.TryGetValue(list.StyleName, out var style) ? style.Elements().FirstOrDefault(e => Int(e, Text + "level") == list.Level + 1) : null;

    (ListKind kind, string label, double indent) ListMarker(ListContext list)
    {
        double indent = 36 + Math.Min(list.Level, 12) * 18;
        var levelStyle = LevelStyle(list);
        var properties = levelStyle?.Element(St + "list-level-properties");
        if (Len(properties?.Element(St + "list-level-label-alignment"), Fo + "margin-left") is double margin) indent = margin;
        // Older documents give the space before the marker and the room it takes up.
        else if (properties != null && (Len(properties, Text + "space-before") != null || Len(properties, Text + "min-label-width") != null))
            indent = (Len(properties, Text + "space-before") ?? 0) + (Len(properties, Text + "min-label-width") ?? 0);
        if (levelStyle?.Name == Text + "list-level-style-number")
        {
            // Outer levels' numbers first, if the style shows them ("1.2").
            int shown = Math.Clamp(Int(levelStyle, Text + "display-levels") ?? 1, 1, list.Level + 1);
            var numbers = new List<string>();
            for (var c = list; c != null && numbers.Count < shown; c = c.Outer)
                if (Format(LevelStyle(c), c.Counter ?? 1) is { Length: > 0 } number) numbers.Insert(0, number);
            return (ListKind.Number, (string?)levelStyle.Attribute(St + "num-prefix") + string.Join('.', numbers) + (string?)levelStyle.Attribute(St + "num-suffix"), indent);
        }
        if (levelStyle?.Name == Text + "list-level-style-image") return (ListKind.Bullet, "•", indent);
        var bullet = (string?)levelStyle?.Attribute(Text + "bullet-char") ?? "•";
        return (ListKind.Bullet, ListLabels.NormalizeBullet(bullet), indent);

        static string Format(XElement? style, int n) => ListLabels.Format(n, (string?)style?.Attribute(St + "num-format") ?? "1");
    }

    /// <summary>The number of a chapter heading, if the document numbers them ("2.1").</summary>
    string? OutlineNumber(XElement heading, int level)
    {
        if (_outlineStyle == null) return null;
        XElement? Style(int l) => _outlineStyle.Elements(Text + "outline-level-style").FirstOrDefault(e => Int(e, Text + "level") == l);
        if (Style(level) is not { } style) return null;
        if (Int(heading, Text + "start-value") is { } restart) _outline[level - 1] = restart;
        else _outline[level - 1] = _outline[level - 1] == 0 ? Int(style, Text + "start-value") ?? 1 : Math.Min(_outline[level - 1], int.MaxValue - 1) + 1;
        for (int i = level; i < 10; i++) _outline[i] = 0;
        if (string.IsNullOrEmpty((string?)style.Attribute(St + "num-format"))) return null;
        int shown = Math.Clamp(Int(style, Text + "display-levels") ?? 1, 1, level);
        var numbers = Enumerable.Range(level - shown + 1, shown).Select(l => ListLabels.Format(Math.Max(1, _outline[l - 1]), (string?)Style(l)?.Attribute(St + "num-format") ?? "")).Where(n => n.Length > 0);
        return (string?)style.Attribute(St + "num-prefix") + string.Join('.', numbers) + (string?)style.Attribute(St + "num-suffix");
    }

    /// <summary>Collapses whitespace the way ODF requires while building runs.</summary>
    sealed class InlineBuilder(DParagraph para)
    {
        bool _lastWasSpace = true, _spaceCounts;

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
            if (resetSpace) _lastWasSpace = false; // a space written as an element is never collapsed
            _spaceCounts = resetSpace;
            if (para.Inlines.LastOrDefault() is DText last && last.Style == style && last.Link == link) last.Text += text;
            else para.Inlines.Add(new DText(text, style, link));
        }

        public void Add(DInline inline)
        {
            para.Inlines.Add(inline);
            _spaceCounts = false;
            _lastWasSpace = inline is DLineBreak;
        }

        public void Anchor(string name) => para.Inlines.Add(new DAnchor(name));

        public void Finish()
        {
            if (!_spaceCounts && para.Inlines.LastOrDefault() is DText last) last.Text = last.Text.TrimEnd(' ');
        }
    }

    void ReadInlines(IEnumerable<XNode> nodes, RunProps run, string? link, InlineBuilder builder)
    {
        _depth++;
        if (!DocumentLimits.CanGoDeeper(_depth))
        {
            // Nested absurdly deep: keep the words.
            foreach (var node in nodes)
                builder.Text(node is XText t ? t.Value : node is XElement e ? e.Value : "", run.ToStyle(), link);
            _depth--;
            return;
        }
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
                foreach (var s in StyleChain((string?)el.Attribute(Text + "style-name"), "text")) ApplyText(inner, s);
                if (inner.Hidden) continue;
                ReadInlines(el.Nodes(), inner, link, builder);
            }
            else if (n == Text + "a") ReadInlines(el.Nodes(), run, Target((string?)el.Attribute(XLink + "href")) ?? link, builder);
            else if (n == Text + "s") builder.Literal(new string(' ', Units.OdfSpaceCount((string?)el.Attribute(Text + "c"))), run.ToStyle(), link);
            else if (n == Text + "tab") builder.Literal("\t", run.ToStyle(), link);
            else if (n == Text + "line-break") builder.Add(new DLineBreak());
            else if (n == Draw + "frame" || n.Namespace == Draw && n != Draw + "a" || n.LocalName == "scene")
            {
                foreach (var drawing in Drawings(el)) ReadFrame(drawing, builder);
            }
            else if (n == Text + "page-number") builder.Add(new DField(FieldKind.Page, run.ToStyle()));
            else if (n == Text + "page-count") builder.Add(new DField(FieldKind.NumPages, run.ToStyle()));
            else if (n == Text + "note")
            {
                bool endnote = (string?)el.Attribute(Text + "note-class") == "endnote";
                var note = new DNote(endnote ? ++_endnoteNumber : ++_footnoteNumber, endnote, run.ToStyle());
                var (previous, previousStyle) = (_previous, _previousStyle);
                _previous = null;
                ReadBlocks(el.Element(Text + "note-body")?.Elements() ?? [], note.Blocks, 0, null);
                (_previous, _previousStyle) = (previous, previousStyle);
                builder.Add(note);
            }
            else if (n == Draw + "a") ReadInlines(el.Nodes(), run, link, builder);
            else if (n == Text + "bookmark-start" || n == Text + "bookmark" || n == Text + "reference-mark" || n == Text + "reference-mark-start")
            {
                if ((string?)el.Attribute(Text + "name") is { Length: > 0 } name) builder.Anchor(name);
            }
            else if (n == Text + "ruby")
            {
                // The reading follows the characters it belongs to, in brackets.
                ReadInlines(el.Element(Text + "ruby-base")?.Nodes() ?? [], run, link, builder);
                if (el.Element(Text + "ruby-text")?.Value.Trim() is { Length: > 0 } reading) builder.Text($"({reading})", run.ToStyle(), link);
            }
            else if (n == Text + "annotation" || n == Office + "annotation" || n == Office + "annotation-end" || n == Text + "soft-page-break" || n == Text + "bookmark-end"
                     || n == Text + "reference-mark-end" || n == Office + "event-listeners" || n == Text + "tracked-changes" || n == Text + "hidden-paragraph" || n == Text + "toc-mark" || n == Text + "alphabetical-index-mark")
                continue;
            else if (n == Text + "hidden-text" && (string?)el.Attribute(Text + "is-hidden") == "true") continue;
            else ReadInlines(el.Nodes(), run, link, builder); // fields and the like: keep their visible text
        }
        _depth--;
    }

    /// <summary>Links to a place in the document come as "#name", or "#Heading|outline".</summary>
    static string? Target(string? href) => href == null ? null : href.StartsWith('#') ? (href.LastIndexOf('|') is > 0 and var bar ? href[..bar] : href) : href;

    /// <summary>Pictures and text boxes: inline when anchored "as character", otherwise positioned floats.</summary>
    void ReadFrame(XElement frame, InlineBuilder builder)
    {
        if ((string?)frame.Attribute(Text + "anchor-type") != "as-char")
        {
            if (Positioned(frame) is { } f) builder.Add(f);
            return;
        }
        if (LoadFrameImage(frame) is { } image)
            builder.Add(new DImage(image.data, image.ext, Len(frame, Svg + "width") is > 1 and double w ? w : image.w, Len(frame, Svg + "height") is > 1 and double h ? h : image.h) { Alt = Alt(frame) });
        else if (Box(frame) is { } box)
        {
            // Text in line with other text: a float holding it keeps its paragraphs and tables.
            var f = new DFloat { Width = Len(frame, Svg + "width") is > 1 and double bw ? bw : 144, Height = Len(frame, Svg + "height") ?? Len(box, Fo + "min-height") ?? 36, HorizontalFrom = FloatFrom.Paragraph, Wrap = FloatWrap.TopAndBottom };
            ReadStory(box, f.Blocks);
            if (f.Blocks.Count > 0) builder.Add(f);
        }
        else if (frame.Element(Draw + "object") is { } formula && ObjectText(formula) is { Length: > 0 } text) builder.Text(text, TextStyle.Plain, null);
    }

    static string? Alt(XElement frame) => (frame.Element(Svg + "desc")?.Value ?? frame.Element(Svg + "title")?.Value) is { } alt && alt.Trim().Length > 0 ? alt.Trim() : null;

    /// <summary>What holds a drawing's text: a text box, or the shape itself.</summary>
    static XElement? Box(XElement frame) => frame.Element(Draw + "text-box") ?? (frame.Name != Draw + "frame" && frame.Elements().Any(e => e.Name == Text + "p" || e.Name == Text + "list") ? frame : null);

    void ReadStory(XElement box, List<DBlock> blocks)
    {
        var (previous, previousStyle) = (_previous, _previousStyle);
        _previous = null;
        ReadBlocks(box.Elements().Where(e => e.Name.Namespace != Draw || box.Name == Draw + "text-box"), blocks, 0, null);
        (_previous, _previousStyle) = (previous, previousStyle);
    }

    /// <summary>The text of an embedded formula.</summary>
    string? ObjectText(XElement obj)
    {
        var math = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "math");
        if (math == null && _zip != null && (string?)obj.Attribute(XLink + "href") is { } href && _zip.GetEntry(href.TrimStart('.', '/').TrimEnd('/') + "/content.xml") is { } entry)
        {
            using var stream = PackageResources.Open(entry);
            math = PackageResources.LoadXml(stream).Root is { Name.LocalName: "math" } root ? root : null;
        }
        return math == null ? null : string.Concat(math.DescendantNodes().OfType<XText>().Where(t => t.Parent?.Name.LocalName != "annotation").Select(t => t.Value.Trim()));
    }

    DFloat? Positioned(XElement frame)
    {
        double? w = Len(frame, Svg + "width"), h = Len(frame, Svg + "height");
        var image = LoadFrameImage(frame);
        var anchor = (string?)frame.Attribute(Text + "anchor-type") ?? "paragraph";
        var box = Box(frame);

        var f = new DFloat { Width = w ?? image?.w ?? 144, Height = h ?? image?.h ?? Len(box, Fo + "min-height") ?? 72, X = Len(frame, Svg + "x") ?? 0, Y = Len(frame, Svg + "y") ?? 0 };
        if (image != null) f.Image = new DImage(image.Value.data, image.Value.ext, f.Width, f.Height) { Alt = Alt(frame) };
        if (box != null) ReadStory(box, f.Blocks);
        else if (image == null && frame.Element(Draw + "object") is { } formula && ObjectText(formula) is { Length: > 0 } text)
        {
            var p = new DParagraph();
            p.Inlines.Add(new DText(text, TextStyle.Plain));
            f.Blocks.Add(p);
        }
        if (f.Image == null && f.Blocks.Count == 0) return null;

        var g = Style((string?)frame.Attribute(Draw + "style-name"), "graphic")?.Element(St + "graphic-properties");
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
        return f;
    }

    (byte[] data, string ext, double w, double h)? LoadFrameImage(XElement frame)
    {
        // Charts are drawn from their own data; their saved preview is usually a format nothing else reads.
        if (_zip != null && (string?)frame.Element(Draw + "object")?.Attribute(XLink + "href") is { } objectHref && Charts.ReadOdf(_zip, objectHref) is { } chart)
        {
            double cw = Len(frame, Svg + "width") ?? 360, ch = Len(frame, Svg + "height") ?? 216;
            var picture = Charts.ToImage(chart, cw, ch);
            return (picture.Data, "png", cw, ch);
        }
        if (frame.Elements(Draw + "image").Elements(Office + "binary-data").FirstOrDefault() is { } binary)
        {
            byte[] embedded;
            try { embedded = Convert.FromBase64String(binary.Value); }
            catch (FormatException) { return null; }
            if (DocumentPictures.Embeddable(embedded) is not { } picture) return null;
            var (iw, ih) = DocxReader.ImageSize(picture.data);
            return (picture.data, picture.ext, iw, ih);
        }
        // Only pictures inside the package: never ones the document points to elsewhere.
        foreach (var href in frame.Elements(Draw + "image").Select(i => (string?)i.Attribute(XLink + "href"))
                     .OrderBy(h => h != null && h.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            if (href == null || href.Contains(':') || href.StartsWith("//", StringComparison.Ordinal) || href.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            var name = href.StartsWith("./", StringComparison.Ordinal) ? href[2..] : href.TrimStart('/');
            if (!_images.TryGetValue(name, out var known))
            {
                (byte[] data, string ext)? picture = null;
                if ((_zip?.GetEntry(name) ?? _zip?.GetEntry(Uri.UnescapeDataString(name))) is { } entry)
                {
                    using var stream = PackageResources.Open(entry, media: true);
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    // Metafiles and SVG become PNG so every output format can show them.
                    picture = DocumentPictures.Embeddable(ms.ToArray());
                }
                known = null;
                if (picture is { } read)
                {
                    var (pw, ph) = DocxReader.ImageSize(read.data);
                    known = (read.data, read.ext, pw, ph);
                }
                _images[name] = known;
            }
            if (known != null) return known;
        }
        return null;
    }

    /// <summary>Pictures already read: a logo can appear on every page.</summary>
    readonly Dictionary<string, (byte[] data, string ext, double w, double h)?> _images = [];

    // ------------------------------------------------------------------ tables

    DTable ReadTable(XElement el)
    {
        var table = new DTable();
        _tableDepth++;
        var rows = Rows(el).ToList();
        // Blank cells and rows repeated by the hundred only fill up the sheet: the table ends where its contents do.
        static bool Filler(XElement tc) => !HasContent(tc) && OdfRepetitions.Count(tc, "number-columns-repeated") > 16;
        int width = rows.Select(r => r.row.Elements().Where(c => c.Name.Namespace == Table).Reverse().SkipWhile(Filler)
            .Sum(c => Math.Min(OdfRepetitions.Count(c, "number-columns-repeated"), DocumentLimits.Columns))).DefaultIfEmpty(0).Max() is var most && most > 0 ? (int)Math.Min(most, DocumentLimits.Columns) : 16;
        foreach (var (tr, header) in rows)
        {
            long times = OdfRepetitions.Count(tr, "number-rows-repeated");
            bool blank = !tr.Elements(Table + "table-cell").Any(HasContent);
            times = Math.Min(times, blank && times > 100 ? 1 : DocumentLimits.Rows);
            for (long repeat = 0; repeat < times; repeat++)
            {
                if (table.Rows.Count >= DocumentLimits.Rows) throw new ConversionException("This document has a table that's too big.");
                var row = new DRow { Header = header };
                int column = 0;
                foreach (var tc in tr.Elements())
                {
                    // A covered cell is the place of one that reaches across or down: it isn't a cell of its own.
                    bool covered = tc.Name == Table + "covered-table-cell";
                    if (!covered && tc.Name != Table + "table-cell") continue;
                    long across = OdfRepetitions.Count(tc, "number-columns-repeated");
                    int room = (HasContent(tc) ? DocumentLimits.Columns : width) - column;
                    if (room <= 0) continue;
                    across = Math.Min(across, room);
                    if (covered) { column += (int)across; continue; }
                    for (int k = 0; k < across; k++)
                    {
                        if (++_cells > DocumentLimits.Cells) throw new ConversionException("This document has a table that's too big.");
                        var cellStyle = Style((string?)tc.Attribute(Table + "style-name"), "table-cell")?.Element(St + "table-cell-properties");
                        var border = (string?)cellStyle?.Attribute(Fo + "border") ?? (string?)cellStyle?.Attribute(Fo + "border-bottom") ?? (string?)cellStyle?.Attribute(Fo + "border-left");
                        if (border != null && border != "none") table.Borders = true;
                        var cell = new DCell
                        {
                            ColSpan = Math.Clamp(Int(tc, Table + "number-columns-spanned") ?? 1, 1, DocumentLimits.Columns),
                            RowSpan = Math.Clamp(Int(tc, Table + "number-rows-spanned") ?? 1, 1, DocumentLimits.Rows),
                            Shading = ColorAttr(cellStyle, Fo + "background-color"),
                        };
                        ReadBlocks(tc.Elements(), cell.Blocks, 0, null);
                        row.Cells.Add(cell);
                    }
                    column += (int)across;
                }
                table.Rows.Add(row);
            }
        }
        _tableDepth--;
        DocumentLimits.TrimColumns(table);
        // A cell can't reach past the end of the table.
        for (int r = 0; r < table.Rows.Count; r++)
            foreach (var cell in table.Rows[r].Cells) cell.RowSpan = Math.Min(cell.RowSpan, table.Rows.Count - r);
        var widths = OdfRepetitions.Widths(el, table.ColumnCount,
            col => Len(Style((string?)col.Attribute(Table + "style-name"), "table-column")?.Element(St + "table-column-properties"), St + "column-width") ?? 0);
        if (widths.Count > 0 && widths.All(w => w > 0)) table.ColumnWidths = widths;
        return table;
    }

    static bool HasContent(XElement cell) => cell.Elements().Any(e => e.Name != Text + "p" || e.Nodes().Any());

    /// <summary>A table's own rows, out of whatever groups they're in.</summary>
    static IEnumerable<(XElement row, bool header)> Rows(XElement table)
    {
        var pending = new Stack<(XElement el, bool header)>(table.Elements().Reverse().Select(e => (e, false)));
        while (pending.Count > 0)
        {
            var (el, header) = pending.Pop();
            if (el.Name == Table + "table-row") yield return (el, header);
            else if (el.Name == Table + "table-rows" || el.Name == Table + "table-row-group" || el.Name == Table + "table-header-rows")
                foreach (var child in el.Elements().Reverse()) pending.Push((child, header || el.Name == Table + "table-header-rows"));
        }
    }

    // ------------------------------------------------------------------ styles

    XElement? Style(string? name, string family) => name != null && _styles.TryGetValue((family, name), out var s) ? s : null;

    IEnumerable<XElement> StyleChain(string? name, string family)
    {
        var chain = new List<XElement>();
        var seen = new HashSet<string>();
        while (name != null && seen.Add(name) && _styles.TryGetValue((family, name), out var style))
        {
            chain.Insert(0, style);
            name = (string?)style.Attribute(St + "parent-style-name");
        }
        return chain;
    }

    void ApplyTextChain(RunProps run, string name)
    {
        foreach (var s in StyleChain(name, "paragraph")) ApplyText(run, s);
    }

    void ApplyText(RunProps r, XElement? style)
    {
        var tp = style?.Element(St + "text-properties");
        if (tp == null) return;
        if ((string?)tp.Attribute(Fo + "font-weight") is { } weight) r.Bold = weight is "bold" or "600" or "700" or "800" or "900";
        if ((string?)tp.Attribute(Text + "display") is { } display) r.Hidden = display == "none";
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
        if ((string?)tp.Attribute(St + "font-name") is { Length: > 0 } fontName)
            r.Font = _fontFaces.TryGetValue(fontName, out var family) ? family : fontName;
        else if ((string?)tp.Attribute(Fo + "font-family") is { } families && families.Split(',')[0].Trim().Trim('\'', '"') is { Length: > 0 } named) r.Font = named;
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
        if ((string?)pp.Attribute(Fo + "break-before") is { } before) p.PageBreakBefore = before is "page" or "even-page" or "odd-page";
    }

    static Color? ColorAttr(XElement? el, XName name)
    {
        var value = (string?)el?.Attribute(name);
        return value is null or "transparent" ? null : Units.ParseHex(value);
    }

    static double? Len(XElement? el, XName name) => Units.ParseLength((string?)el?.Attribute(name));

    static int? Int(XElement? el, XName name) => int.TryParse((string?)el?.Attribute(name), System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;

    XElement? Load(string path)
    {
        if (_flat != null) return _flat;
        var entry = _zip?.GetEntry(path);
        if (entry == null) return null;
        using var stream = PackageResources.Open(entry);
        return PackageResources.LoadXml(stream, LoadOptions.PreserveWhitespace).Root;
    }
}
