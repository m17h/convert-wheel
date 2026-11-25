using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Reads Word .docx files: text formatting, styles, lists, tables, images and page setup.</summary>
internal sealed class DocxReader
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    readonly ZipArchive _zip;
    // Relationships (images, links) are per part: the body, each header/footer and the notes all have their own.
    string _docDir;
    readonly Dictionary<string, System.Windows.Media.Color> _themeColors = [];
    Dictionary<string, (string target, bool external)> _rels = [];
    readonly Dictionary<string, (string target, bool external)> _mainRels;
    readonly Dictionary<string, XElement> _footnotes = [], _endnotes = [];
    string? _footnotesPath, _endnotesPath, _settingsPath;
    int _footnoteNumber, _endnoteNumber;
    DNote? _currentNote;
    readonly Stack<FieldState> _fields = new();

    sealed class FieldState
    {
        public readonly System.Text.StringBuilder Code = new();
        public bool Separated, ReplacesResult;
        public string? Link;
    }
    /// <summary>How many tables, text boxes and notes the reader is inside.</summary>
    int _depth, _tableDepth;
    int _cells;
    readonly Dictionary<string, Style> _styles = [];
    readonly Dictionary<string, NumberingInstance> _numbering = [];
    /// <summary>List numbers reached, per list definition (which several numbering instances can share).</summary>
    readonly Dictionary<string, int[]> _counters = [];
    readonly HashSet<(string numId, int level)> _restarted = [];
    readonly XElement? _defaultRPr, _defaultPPr;
    readonly string? _defaultParaStyle;
    string? _majorFont, _minorFont;
    bool _pendingPageBreak;
    /// <summary>The page settings of each section in order (Word stores them at the end of each section).</summary>
    List<XElement> _sections = [];
    int _sectionIndex;
    PageSetup? _pendingSection;
    DParagraph? _previous;
    string? _previousStyle;

    sealed record Style(string Id, string? Name, string? BasedOn, XElement? PPr, XElement? RPr, XElement? TblPr, string Type);
    sealed record Level(int Start, string Format, string Text, XElement? PPr, XElement? RPr, int? Restart = null, bool Legal = false);
    /// <param name="Key">The list definition whose numbers it shares.</param>
    /// <param name="Overrides">Levels it starts again, and at what.</param>
    sealed record NumberingInstance(string Key, Dictionary<int, Level> Levels, Dictionary<int, int> Overrides);

    sealed class RunProps
    {
        public bool Bold, Italic, Underline, Strike, Caps, Hidden;
        // Right-to-left text has its own bold, italic, size and font.
        public bool Rtl, BoldCs, ItalicCs;
        public string? Vert, Font, FontCs;
        public double? Size, SizeCs;
        public Color? Color, Highlight;

        public RunProps Clone() => (RunProps)MemberwiseClone();

        public TextStyle ToStyle() => new()
        {
            Bold = Rtl ? BoldCs : Bold,
            Italic = Rtl ? ItalicCs : Italic,
            Underline = Underline,
            Strike = Strike,
            Superscript = Vert == "superscript",
            Subscript = Vert == "subscript",
            Size = Rtl ? SizeCs ?? Size : Size,
            Color = Color,
            Highlight = Highlight,
            Font = Rtl ? FontCs ?? Font : Font,
        };
    }

    sealed class ParaProps
    {
        public TextAlignment Align = TextAlignment.Left;
        public double Before, After, Left, First, Line = 1;
        public bool PageBreakBefore, ContextualSpacing, Bidi;
        public string? NumId;
        public int Ilvl;
        public int? OutlineLevel;
    }

    public static DDocument Read(string path) => DocumentFiles.Open(path, DocumentShape.Word, () => ReadDocx(path));

    internal static DDocument ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return new DocxReader(zip).Build(Path.GetFileNameWithoutExtension(path));
    }

    DocxReader(ZipArchive zip)
    {
        _zip = zip;
        var mainPath = "word/document.xml";
        var rootRels = Load("_rels/.rels");
        var officeDoc = rootRels?.Root?.Elements(PR + "Relationship")
            .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument") == true);
        if ((string?)officeDoc?.Attribute("Target") is { Length: > 0 } main) mainPath = main.TrimStart('/');
        if (zip.GetEntry(mainPath) == null && zip.GetEntry("word/document.xml") != null) mainPath = "word/document.xml";
        _docDir = mainPath.Contains('/') ? mainPath[..mainPath.LastIndexOf('/')] : "";
        MainPath = mainPath;

        var rels = Load(RelsPath(mainPath));
        string? stylesPath = null, numberingPath = null, themePath = null;
        foreach (var rel in rels?.Root?.Elements(PR + "Relationship") ?? [])
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            var type = (string?)rel.Attribute("Type") ?? "";
            if (id == null || target == null) continue;
            bool external = (string?)rel.Attribute("TargetMode") == "External";
            _rels[id] = (target, external);
            if (type.EndsWith("/styles")) stylesPath = Resolve(target);
            else if (type.EndsWith("/numbering")) numberingPath = Resolve(target);
            else if (type.EndsWith("/theme")) themePath = Resolve(target);
            else if (type.EndsWith("/footnotes")) _footnotesPath = Resolve(target);
            else if (type.EndsWith("/endnotes")) _endnotesPath = Resolve(target);
            else if (type.EndsWith("/settings")) _settingsPath = Resolve(target);
        }
        _mainRels = _rels;

        foreach (var (path, map, tag) in new[] { (_footnotesPath, _footnotes, "footnote"), (_endnotesPath, _endnotes, "endnote") })
        {
            if (path == null || Load(path)?.Root is not { } notes) continue;
            foreach (var note in notes.Elements(W + tag))
                if (Attr(note, "id") is { } id && Attr(note, "type") is null or "normal") map[id] = note;
        }

        if (themePath != null && Load(themePath)?.Root is { } theme)
        {
            _majorFont = (string?)theme.Descendants(A + "majorFont").Elements(A + "latin").FirstOrDefault()?.Attribute("typeface");
            _minorFont = (string?)theme.Descendants(A + "minorFont").Elements(A + "latin").FirstOrDefault()?.Attribute("typeface");
            foreach (var c in theme.Descendants(A + "clrScheme").Elements())
                if (Units.ParseHex((string?)c.Element(A + "srgbClr")?.Attribute("val") ?? (string?)c.Element(A + "sysClr")?.Attribute("lastClr")) is { } color)
                    _themeColors[c.Name.LocalName] = color;
        }

        if (Load(stylesPath ?? "word/styles.xml")?.Root is { } styles)
        {
            var defaults = styles.Element(W + "docDefaults");
            _defaultRPr = defaults?.Element(W + "rPrDefault")?.Element(W + "rPr");
            _defaultPPr = defaults?.Element(W + "pPrDefault")?.Element(W + "pPr");
            foreach (var s in styles.Elements(W + "style"))
            {
                var id = Attr(s, "styleId");
                if (id == null) continue;
                var type = Attr(s, "type") ?? "paragraph";
                _styles[id] = new Style(id, Val(s.Element(W + "name")), Val(s.Element(W + "basedOn")),
                    s.Element(W + "pPr"), s.Element(W + "rPr"), s.Element(W + "tblPr"), type);
                if (type == "paragraph" && Attr(s, "default") is "1" or "true") _defaultParaStyle = id;
            }
        }

        if (Load(numberingPath ?? "word/numbering.xml")?.Root is { } numbering)
        {
            var abstracts = new Dictionary<string, Dictionary<int, Level>>();
            var styleLinks = new Dictionary<string, string>();
            foreach (var an in numbering.Elements(W + "abstractNum"))
            {
                var levels = new Dictionary<int, Level>();
                foreach (var lvl in an.Elements(W + "lvl"))
                    if (Int(Attr(lvl, "ilvl")) is null or (>= 0 and <= 8)) levels[Int(Attr(lvl, "ilvl")) ?? 0] = ReadLevel(lvl);
                if (Attr(an, "abstractNumId") is not { } aid) continue;
                abstracts[aid] = levels;
                if (Val(an.Element(W + "numStyleLink")) is { } link) styleLinks[aid] = link;
            }
            // A definition that only points at a numbering style uses the one that style's numbering points to.
            var instances = numbering.Elements(W + "num").ToList();
            foreach (var (aid, link) in styleLinks)
                if (abstracts[aid].Count == 0 && _styles.TryGetValue(link, out var linked) && Val(linked.PPr?.Element(W + "numPr")?.Element(W + "numId")) is { } linkedNum
                    && instances.FirstOrDefault(n => Attr(n, "numId") == linkedNum) is { } target && Val(target.Element(W + "abstractNumId")) is { } real && abstracts.TryGetValue(real, out var realLevels))
                    abstracts[aid] = realLevels;
            foreach (var num in instances)
            {
                var numId = Attr(num, "numId");
                var aid = Val(num.Element(W + "abstractNumId"));
                if (numId == null || aid == null || !abstracts.TryGetValue(aid, out var baseLevels)) continue;
                var levels = new Dictionary<int, Level>(baseLevels);
                var overrides = new Dictionary<int, int>();
                foreach (var ov in num.Elements(W + "lvlOverride"))
                {
                    int ilvl = Int(Attr(ov, "ilvl")) ?? 0;
                    if (ilvl is < 0 or > 8) continue;
                    if (ov.Element(W + "lvl") is { } replaced) levels[ilvl] = ReadLevel(replaced);
                    if (Int(Val(ov.Element(W + "startOverride"))) is int start) overrides[ilvl] = start;
                }
                _numbering[numId] = new NumberingInstance(aid, levels, overrides);
            }
        }
    }

    static Level ReadLevel(XElement lvl) => new(Int(Val(lvl.Element(W + "start"))) ?? 1, Val(lvl.Element(W + "numFmt")) ?? "decimal",
        Val(lvl.Element(W + "lvlText")) ?? "", lvl.Element(W + "pPr"), lvl.Element(W + "rPr"), Int(Val(lvl.Element(W + "lvlRestart"))), lvl.Element(W + "isLgl") is { } legal && On(legal));

    string MainPath { get; }

    DDocument Build(string title)
    {
        var doc = new DDocument { Title = title };
        var body = Load(MainPath)?.Root?.Element(W + "body") ?? throw new ConversionException("This Word file has no document body.");

        var baseRun = new RunProps { Size = 10 };
        ApplyRun(baseRun, _defaultRPr);
        if (_defaultParaStyle != null) foreach (var s in Chain(_defaultParaStyle)) ApplyRun(baseRun, s.RPr);
        doc.Font = baseRun.Font ?? _minorFont ?? "Calibri";
        doc.FontSize = baseRun.Size ?? 11;

        // (A changed section keeps its old settings inside it; those aren't sections.)
        _sections = body.Descendants(W + "sectPr").Where(s => s.Parent == body || s.Parent?.Name == W + "pPr" && s.Parent.Parent?.Parent == body).ToList();
        ReadBlocks(body.Elements(), doc.Blocks, null);

        // The document's page settings are its first section's; later sections carry their own.
        if (_sections.FirstOrDefault() is { } sect)
        {
            var columns = sect.Element(W + "cols");
            doc.Columns = Math.Max(1, Int(Attr(columns, "num")) ?? 1);
            doc.ColumnGap = Twips(Attr(columns, "space")) ?? 36;
            var size = sect.Element(W + "pgSz");
            var margin = sect.Element(W + "pgMar");
            if (Twips(Attr(size, "w")) is double w && Twips(Attr(size, "h")) is double h && w > 72 && h > 72)
            {
                doc.PageWidth = w;
                doc.PageHeight = h;
            }
            if (margin != null)
            {
                doc.Margins = new Thickness(Twips(Attr(margin, "left")) ?? 72, Twips(Attr(margin, "top")) ?? 72,
                    Twips(Attr(margin, "right")) ?? 72, Twips(Attr(margin, "bottom")) ?? 72);
                doc.HeaderDistance = Twips(Attr(margin, "header")) ?? 36;
                doc.FooterDistance = Twips(Attr(margin, "footer")) ?? 36;
            }
        }
        ReadHeadersAndFooters(body, doc);
        DocumentLimits.Finish(doc);
        doc.NormalizeDefaults();
        return doc;
    }

    /// <summary>
    /// Running headers and footers come from the last section that defines them (sections inherit
    /// them in Word), with "different first page" and, if enabled, separate even-page versions.
    /// </summary>
    void ReadHeadersAndFooters(XElement body, DDocument doc)
    {
        var sections = body.Descendants(W + "sectPr").ToList();
        var final = body.Element(W + "sectPr");
        bool evenAndOdd = _settingsPath != null && Load(_settingsPath)?.Root?.Element(W + "evenAndOddHeaders") is { } eo && On(eo);
        doc.DifferentFirstPage = final?.Element(W + "titlePg") is { } tp && On(tp);

        DHeaderFooter? Part(string element, string type)
        {
            foreach (var sect in Enumerable.Reverse(sections))
            {
                var reference = sect.Elements(W + element).FirstOrDefault(r => (Attr(r, "type") ?? "default") == type);
                if (reference == null || Attr(reference, R + "id") is not { } rid || !_mainRels.TryGetValue(rid, out var rel)) continue;
                var path = Resolve(rel.target);
                var root = Load(path)?.Root;
                if (root == null) return null;
                var part = new DHeaderFooter();
                WithPart(path, () => ReadBlocks(root.Elements(), part.Blocks, null));
                return part;
            }
            return null;
        }

        doc.Header = Part("headerReference", "default");
        doc.Footer = Part("footerReference", "default");
        if (doc.DifferentFirstPage)
        {
            doc.FirstHeader = Part("headerReference", "first") ?? new DHeaderFooter();
            doc.FirstFooter = Part("footerReference", "first") ?? new DHeaderFooter();
        }
        if (evenAndOdd)
        {
            doc.EvenHeader = Part("headerReference", "even");
            doc.EvenFooter = Part("footerReference", "even");
        }
    }

    /// <summary>Reads content from another part (header, footer, notes) with that part's relationships.</summary>
    void WithPart(string path, Action read)
    {
        var (savedRels, savedDir) = (_rels, _docDir);
        _rels = LoadRels(path);
        _docDir = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        try { read(); }
        finally { (_rels, _docDir) = (savedRels, savedDir); }
    }

    Dictionary<string, (string target, bool external)> LoadRels(string partPath)
    {
        var map = new Dictionary<string, (string target, bool external)>();
        foreach (var rel in Load(RelsPath(partPath))?.Root?.Elements(PR + "Relationship") ?? [])
            if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target)
                map[id] = (target, (string?)rel.Attribute("TargetMode") == "External");
        return map;
    }

    // ------------------------------------------------------------------ blocks

    void ReadBlocks(IEnumerable<XElement> elements, List<DBlock> output, Style? tableStyle)
    {
        _depth++;
        bool deepEnough = !DocumentLimits.CanGoDeeper(_depth);
        foreach (var el in Unwrapped(elements))
        {
            if (deepEnough) Flatten(el, output);
            else if (el.Name == W + "p")
            {
                if (_joinNext is { } before && output.LastOrDefault() == before) ReadParagraph(el, output, tableStyle, before);
                else ReadParagraph(el, output, tableStyle);
            }
            else if (el.Name == W + "tbl")
            {
                if (_tableDepth >= DocumentLimits.TableDepth) { Flatten(el, output); continue; }
                var table = ReadTable(el);
                if (table.Rows.Count == 0) continue;
                if (_pendingSection != null) { table.Section = _pendingSection; _pendingSection = null; }
                output.Add(table);
            }
            else if (el.Name == W + "altChunk") ReadChunk(el, output);
            else if (el.Name == W + "bookmarkStart" || el.Name == M + "oMathPara" || el.Name == M + "oMath" || el.Name == W + "r" || el.Name == W + "hyperlink")
                // Not where they belong, but Word shows them all the same.
                if (el.Name != W + "bookmarkStart") ReadParagraph(new XElement(W + "p", el), output, tableStyle);
                else if (Attr(el, "name") is { } mark) _bookmarks.Add(mark);
        }
        _depth--;
    }

    /// <summary>Elements with whatever merely wraps them (content controls, insertions, smart tags) taken off. Deletions go with their wrapper.</summary>
    static IEnumerable<XElement> Unwrapped(IEnumerable<XElement> elements)
    {
        var pending = new Stack<IEnumerator<XElement>>();
        pending.Push(elements.GetEnumerator());
        while (pending.Count > 0)
        {
            if (!pending.Peek().MoveNext()) { pending.Pop().Dispose(); continue; }
            var el = pending.Peek().Current;
            var name = el.Name;
            if (name == W + "sdt") { if (el.Element(W + "sdtContent") is { } content) pending.Push(content.Elements().GetEnumerator()); }
            else if (name == W + "ins" || name == W + "moveTo" || name == W + "smartTag" || name == W + "customXml" || name == W + "sdtContent") pending.Push(el.Elements().GetEnumerator());
            else if (name == MC + "AlternateContent") pending.Push(Alternate(el).GetEnumerator());
            else yield return el;
        }
    }

    /// <summary>Content nested too deeply to lay out: its paragraphs, one after another.</summary>
    void Flatten(XElement el, List<DBlock> output)
    {
        foreach (var p in el.DescendantsAndSelf(W + "p"))
        {
            var text = string.Concat(p.Descendants().Where(t => t.Name == W + "t" && t.Ancestors(W + "p").First() == p).Select(t => t.Value));
            if (text.Length == 0) continue;
            var para = new DParagraph();
            para.Inlines.Add(new DText(text, TextStyle.Plain));
            output.Add(para);
        }
    }

    /// <summary>A whole document of another kind (HTML, RTF, plain text, Word) kept inside this one by the program that made it.</summary>
    void ReadChunk(XElement el, List<DBlock> output)
    {
        if (_depth > 4 || Attr(el, R + "id") is not { } rid || !_rels.TryGetValue(rid, out var rel) || rel.external || _zip.GetEntry(Resolve(rel.target)) is not { } entry) return;
        using var stream = PackageResources.Open(entry);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();
        try
        {
            var head = System.Text.Encoding.Latin1.GetString(data, 0, Math.Min(64, data.Length)).TrimStart('\u00EF', '\u00BB', '\u00BF', ' ', '\r', '\n');
            DDocument? inner = null;
            if (head.StartsWith("PK", StringComparison.Ordinal))
            {
                ms.Position = 0;
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                inner = new DocxReader(zip) { _depth = _depth + 1 }.Build("");
            }
            else if (head.StartsWith(@"{\rtf", StringComparison.Ordinal)) inner = RichText.ReadRtf(data, null);
            else if (head.StartsWith('<')) inner = HtmlReader.FromHtml(HtmlReader.Decode(data), null, src => src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DocumentPictures.FromDataUri(src) : null);
            if (inner != null) output.AddRange(inner.Blocks);
            else output.AddRange(PlainText.Paragraphs(TextDecoding.Decode(data)));
        }
        catch (Exception ex) when (ex is ConversionException or InvalidDataException or System.Xml.XmlException or ArgumentException)
        {
            Log.Write($"Skipped part of a Word document: {ex.Message}");
        }
    }

    readonly List<string> _bookmarks = [];
    /// <summary>The paragraph whose mark was deleted: the next one runs on from it.</summary>
    DParagraph? _joinNext;

    void ReadParagraph(XElement p, List<DBlock> output, Style? tableStyle, DParagraph? joined = null)
    {
        var pPr = p.Element(W + "pPr");
        var styleId = Val(pPr?.Element(W + "pStyle")) ?? _defaultParaStyle;
        var styles = styleId != null ? Chain(styleId).ToList() : [];

        var props = new ParaProps { After = 0, Line = 1 };
        ApplyPara(props, _defaultPPr);
        if (tableStyle != null) foreach (var s in Chain(tableStyle.Id)) ApplyPara(props, s.PPr);
        foreach (var s in styles) ApplyPara(props, s.PPr);

        // Numbering can come from the style or the paragraph; its level indents sit under direct formatting.
        string? numId = props.NumId;
        int ilvl = props.Ilvl;
        if (pPr?.Element(W + "numPr") is { } numPr)
        {
            numId = Val(numPr.Element(W + "numId")) ?? numId;
            ilvl = Int(Val(numPr.Element(W + "ilvl"))) ?? ilvl;
        }
        ilvl = Math.Clamp(ilvl, 0, 8);
        Level? level = null;
        if (joined == null && numId != null && numId != "0" && _numbering.TryGetValue(numId, out var instance))
        {
            instance.Levels.TryGetValue(ilvl, out level);
            if (level != null) ApplyPara(props, level.PPr);
        }
        ApplyPara(props, pPr);

        var runBase = new RunProps { Size = 10 };
        ApplyRun(runBase, _defaultRPr);
        if (tableStyle != null) foreach (var s in Chain(tableStyle.Id)) ApplyRun(runBase, s.RPr);
        foreach (var s in styles) ApplyRun(runBase, s.RPr);

        // A field whose result never came in its own paragraph isn't coming: don't let it swallow the rest.
        while (_fields.Count > 0 && !_fields.Peek().Separated) _fields.Pop();
        var para = joined ?? NewParagraph(props, styles);
        _joinNext = null;
        if (level != null && numId != null)
        {
            para.ListLevel = ilvl;
            para.List = level.Format == "bullet" ? ListKind.Bullet : ListKind.Number;
            para.ListLabel = Label(numId, ilvl, level);
            // A level that draws nothing isn't a list to look at.
            if (para.ListLabel.Trim().Length == 0) (para.List, para.ListLabel) = (ListKind.None, null);
        }
        foreach (var bookmark in _bookmarks) para.Inlines.Add(new DAnchor(bookmark));
        _bookmarks.Clear();

        var markProps = runBase.Clone();
        ApplyRun(markProps, pPr?.Element(W + "rPr"));
        para.MarkSize = markProps.Size;

        // contextualSpacing: no extra space between paragraphs of the same style (list items, etc.)
        if (props.ContextualSpacing && _previous != null && _previousStyle == styleId && output.LastOrDefault() == _previous)
        {
            para.SpaceBefore = 0;
            _previous.SpaceAfter = 0;
        }

        var pending = new List<DBlock>();
        var current = para;
        if (joined == null) output.Add(current);
        ReadInlines(p.Elements().Where(e => e.Name != W + "pPr"), runBase, null, ref current, output, pending, props, styles);
        // A tracked deletion of the paragraph mark: an empty one is gone, and any other runs on into the next.
        bool markDeleted = pPr?.Element(W + "rPr") is { } markRun && (markRun.Element(W + "del") != null || markRun.Element(W + "moveFrom") != null) && pPr.Element(W + "sectPr") == null;
        if (markDeleted && p.ElementsAfterSelf(W + "p").Any())
        {
            if (current.Inlines.Count == 0 && joined == null && current == para)
            {
                output.Remove(current);
                if (current.PageBreakBefore) _pendingPageBreak = true;
                if (current.Section != null) _pendingSection = current.Section;
                output.AddRange(pending);
                return;
            }
            if (pending.Count == 0) _joinNext = current;
        }
        // A page break at the end of a paragraph leaves nothing after it; Word doesn't show that as an
        // empty line at the top of the next page, so carry the break to the next paragraph instead.
        if (current != para && current.Inlines.Count == 0 && current.PageBreakBefore)
        {
            output.Remove(current);
            _pendingPageBreak = true;
            current = para;
        }
        output.AddRange(pending);

        if (pPr?.Element(W + "sectPr") is { } sect && _sections.Contains(sect))
        {
            // The next section's own settings say how it starts: on a new page (page size, orientation and
            // columns may change) or continuing on the same page.
            var next = ++_sectionIndex < _sections.Count ? _sections[_sectionIndex] : null;
            if (next != null && Attr(next.Element(W + "type"), "val") != "continuous") _pendingSection = Setup(next);
            else if (Attr(sect.Element(W + "type"), "val") != "continuous" && next == null) _pendingPageBreak = true;
        }

        _previous = current;
        _previousStyle = styleId;
    }

    DParagraph NewParagraph(ParaProps props, List<Style> styles)
    {
        var para = new DParagraph
        {
            Align = props.Align,
            SpaceBefore = props.Before,
            SpaceAfter = props.After,
            IndentLeft = Math.Max(0, props.Left),
            IndentFirst = props.First,
            LineSpacing = props.Line,
            PageBreakBefore = props.PageBreakBefore || _pendingPageBreak,
        };
        _pendingPageBreak = false;
        if (_pendingSection != null) { para.Section = _pendingSection; _pendingSection = null; }
        if (props.Bidi)
        {
            // "Left" means the starting side, which is the right in right-to-left text.
            para.RightToLeft = true;
            para.Align = props.Align switch { TextAlignment.Left => TextAlignment.Right, TextAlignment.Right => TextAlignment.Left, var a => a };
        }

        var name = styles.LastOrDefault()?.Name?.ToLowerInvariant();
        if (props.OutlineLevel is int outline and < 9) para.Heading = Math.Min(6, outline + 1);
        else if (name != null && name.StartsWith("heading ") && int.TryParse(name[8..], out var h)) para.Heading = Math.Clamp(h, 1, 6);
        else if (name == "title") para.Heading = 1;
        return para;
    }

    string Label(string numId, int ilvl, Level level)
    {
        var instance = _numbering[numId];
        if (!_counters.TryGetValue(instance.Key, out var counts))
        {
            counts = new int[9];
            for (int i = 0; i < 9; i++) counts[i] = int.MinValue;
            _counters[instance.Key] = counts;
        }
        var levels = instance.Levels;
        // An instance that overrides a start begins its numbers again the first time it's used.
        if (instance.Overrides.TryGetValue(ilvl, out int from) && _restarted.Add((numId, ilvl))) counts[ilvl] = from - 1;
        else if (counts[ilvl] == int.MinValue) counts[ilvl] = (instance.Overrides.TryGetValue(ilvl, out from) ? from : level.Start) - 1;
        if (counts[ilvl] < int.MaxValue) counts[ilvl]++;
        // Deeper levels start again, unless they say they only do after some other level.
        for (int i = ilvl + 1; i < 9; i++)
            if (!levels.TryGetValue(i, out var deeper) || deeper.Restart is not { } restart || restart > ilvl) counts[i] = int.MinValue;

        if (level.Format == "bullet")
        {
            var font = Attr(level.RPr?.Element(W + "rFonts"), "ascii") ?? Attr(level.RPr?.Element(W + "rFonts"), "hAnsi");
            return level.Text.Length == 1 && SymbolText(level.Text[0], font) is { } symbol ? symbol : ListLabels.NormalizeBullet(level.Text);
        }
        var text = new System.Text.StringBuilder();
        for (int k = 0; k < level.Text.Length; k++)
        {
            if (level.Text[k] != '%' || k + 1 >= level.Text.Length || level.Text[k + 1] is < '1' or > '9') { text.Append(level.Text[k]); continue; }
            int i = level.Text[++k] - '1';
            if (i > ilvl) continue;
            var lvl = levels.TryGetValue(i, out var l) ? l : level;
            int value = counts[i] == int.MinValue ? lvl.Start : counts[i];
            text.Append(Number(value, level.Legal && i < ilvl ? "decimal" : lvl.Format));
        }
        return text.ToString();
    }

    static string Number(int n, string format) => format switch
    {
        "decimalZero" => n.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
        "ordinal" => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }),
        // Word doubles letters (z, aa, bb) rather than counting on (z, aa, ab).
        "lowerLetter" or "upperLetter" when n is > 26 and <= 780 => ListLabels.Format(1 + (n - 1) % 26, format) is var one ? string.Concat(Enumerable.Repeat(one, 1 + (n - 1) / 26)) : "",
        _ => ListLabels.Format(n, format),
    };

    /// <summary>Characters of the symbol fonts as the Unicode ones they look like.</summary>
    static string? SymbolText(char c, string? font)
    {
        int code = c >= 0xF000 && c <= 0xF0FF ? c - 0xF000 : c;
        if (code is < 0x20 or > 0xFF) return null;
        if (font != null && font.Equals("Symbol", StringComparison.OrdinalIgnoreCase)) return code - 0x20 < SymbolFont.Length && SymbolFont[code - 0x20] is var s and not '\0' ? s.ToString() : null;
        if (font == null || !font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase)) return null;
        if (font.Equals("Wingdings 2", StringComparison.OrdinalIgnoreCase))
            return code switch { 0x50 => "✓", 0x52 => "☑", 0x54 => "☒", 0x4F => "✗", 0xA3 => "☐", 0x97 or 0x98 or 0x95 => "•", _ => null };
        if (font.Equals("Wingdings 3", StringComparison.OrdinalIgnoreCase))
            return code switch { 0x7D => "▶", 0x84 => "◀", 0x70 => "▲", 0x71 => "▼", 0x22 => "→", 0x21 => "←", _ => null };
        return code switch
        {
            0x22 => "✂", 0x28 => "☎", 0x2A => "✉", 0x3A => "💻", 0x46 => "☞", 0x45 => "☜", 0x4A => "☺", 0x4B => "😐", 0x4C => "☹", 0x6C => "●", 0x6E => "■", 0x6F or 0xA8 => "☐",
            0x71 or 0x72 => "❑", 0x73 => "⬧", 0x74 => "⧫", 0x75 => "◆", 0x76 => "❖", 0x77 => "⬥", 0xA1 => "○", 0xA7 => "▪", 0xAB => "★", 0xB7 or 0x9F => "•", 0x9E => "·",
            0xD8 => "➢", 0xDF or 0xE7 => "←", 0xE0 or 0xE8 => "→", 0xE1 => "↑", 0xE2 => "↓", 0xEF => "⇦", 0xF0 => "⇨", 0xFB => "✗", 0xFC => "✓", 0xFD => "☒", 0xFE => "☑", 0x4D => "💣", 0x54 => "❄",
            0x80 => "⓪", >= 0x81 and <= 0x8A => ((char)(0x2460 + code - 0x81)).ToString(), >= 0x8C and <= 0x95 => ((char)(0x2776 + code - 0x8C)).ToString(),
            _ => null,
        };
    }

    // Adobe's Symbol encoding, from 0x20.
    const string SymbolFont =
        " !∀#∃%&∋()∗+,−./0123456789:;<=>?≅ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ[∴]⊥_‾αβχδεφγηιϕκλμνοπθρστυϖωξψζ{|}∼\0" +
        "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0" +
        "€ϒ′≤⁄∞ƒ♣♦♥♠↔←↑→↓°±″≥×∝∂•÷≠≡≈…⏐⎯↵ℵℑℜ℘⊗⊕∅∩∪⊃⊇⊄⊂⊆∈∉∠∇®©™∏√⋅¬∧∨⇔⇐⇑⇒⇓◊〈®©™∑⎛⎜⎝⎡⎢⎣⎧⎨⎩⎪\0〉∫⌠⎮⌡⎞⎟⎠⎤⎥⎦⎫⎬⎭\0";

    // ------------------------------------------------------------------ inlines

    void ReadInlines(IEnumerable<XElement> elements, RunProps runBase, string? link, ref DParagraph current,
        List<DBlock> output, List<DBlock> pending, ParaProps props, List<Style> styles)
    {
        _depth++;
        bool deepEnough = !DocumentLimits.CanGoDeeper(_depth);
        foreach (var el in Unwrapped(elements))
        {
            var name = el.Name;
            if (deepEnough) AddText(current, string.Concat(el.Descendants(W + "t").Select(t => t.Value)), runBase.ToStyle(), link);
            else if (name == W + "r") ReadRun(el, runBase, link, ref current, output, pending, props, styles);
            else if (name == W + "hyperlink")
            {
                string? target = null;
                if (Attr(el, R + "id") is { } rid && _rels.TryGetValue(rid, out var rel)) target = rel.target;
                if (Attr(el, "anchor") is { Length: > 0 } anchor) target = target == null ? "#" + anchor : $"{target}#{anchor}";
                ReadInlines(el.Elements(), runBase, target ?? link, ref current, output, pending, props, styles);
            }
            else if (name == W + "fldSimple" && FieldKindOf(Attr(el, "instr")) is { } kind)
                current.Inlines.Add(new DField(kind, RunStyleOf(el.Element(W + "r"), runBase)));
            else if (name == W + "fldSimple")
                ReadInlines(el.Elements(), runBase, FieldLink(Attr(el, "instr")) ?? link, ref current, output, pending, props, styles);
            else if (name == W + "bdo" || name == W + "dir")
                ReadInlines(el.Elements(), runBase, link, ref current, output, pending, props, styles);
            else if (name == W + "bookmarkStart")
            {
                if (Attr(el, "name") is { Length: > 0 } bookmark) current.Inlines.Add(new DAnchor(bookmark));
            }
            else if (name == M + "oMathPara" || name == M + "oMath")
                AddText(current, Formula(el, 0), runBase.ToStyle(), link);
        }
        _depth--;
    }

    /// <summary>Where a HYPERLINK field leads: its address, or a "#bookmark" in this document.</summary>
    static string? FieldLink(string? code)
    {
        if (code == null || !code.TrimStart().StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
        string url = "", name = "";
        var words = System.Text.RegularExpressions.Regex.Matches(code, "\"([^\"]*)\"|\\S+").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value).ToList();
        for (int i = 1; i < words.Count; i++)
        {
            switch (words[i].ToLowerInvariant())
            {
                case "\\l": if (++i < words.Count) name = words[i]; break;
                case "\\o" or "\\t": i++; break; // a tip or a frame
                case ['\\', _]: break;
                default: if (url.Length == 0) url = words[i].Replace(@"\\", @"\"); break;
            }
        }
        if (url.Length > 0) return name.Length > 0 ? $"{url}#{name}" : url;
        return name.Length > 0 ? "#" + name : null;
    }

    /// <summary>An equation as a line of text: (a+b)/c for fractions, x^2 for powers, √(x) for roots.</summary>
    static string Formula(XElement el, int depth)
    {
        if (!DocumentLimits.CanGoDeeper(depth, 80)) return string.Concat(el.Descendants(M + "t").Select(t => t.Value));
        string Part(string name) => el.Element(M + name) is { } part ? Formula(part, depth + 1) : "";
        static string Group(string s) => s.Length > 1 && !s.All(char.IsLetterOrDigit) ? $"({s})" : s;
        string Set(string properties, string name, string fallback) => Val(el.Element(M + properties)?.Element(M + name)) ?? (string?)el.Element(M + properties)?.Element(M + name)?.Attribute(M + "val") ?? fallback;
        string Inner() => string.Concat(el.Elements().Select(c => Formula(c, depth + 1)));
        switch (el.Name.LocalName)
        {
            case "t": return el.Value;
            case "f": return Set("fPr", "type", "bar") == "noBar" ? $"({Part("num")} {Part("den")})" : $"{Group(Part("num"))}/{Group(Part("den"))}";
            case "sSup": return $"{Part("e")}^{Group(Part("sup"))}";
            case "sSub": return $"{Part("e")}_{Group(Part("sub"))}";
            case "sSubSup": return $"{Part("e")}_{Group(Part("sub"))}^{Group(Part("sup"))}";
            case "sPre": return $"_{Group(Part("sub"))}^{Group(Part("sup"))}{Part("e")}";
            case "rad": return $"{Part("deg")}√{(Part("e") is { Length: > 1 } e ? $"({e})" : Part("e"))}";
            case "nary":
                var sign = Set("naryPr", "chr", "∫");
                return sign + (Part("sub") is { Length: > 0 } sub ? "_" + Group(sub) : "") + (Part("sup") is { Length: > 0 } sup ? "^" + Group(sup) : "") + " " + Part("e");
            case "d":
                return Set("dPr", "begChr", "(") + string.Join(Set("dPr", "sepChr", "|"), el.Elements(M + "e").Select(c => Formula(c, depth + 1))) + Set("dPr", "endChr", ")");
            case "m": return "[" + string.Join("; ", el.Elements(M + "mr").Select(r => string.Join(", ", r.Elements(M + "e").Select(c => Formula(c, depth + 1))))) + "]";
            case "eqArr": return string.Join("; ", el.Elements(M + "e").Select(c => Formula(c, depth + 1)));
            case "func": return $"{Part("fName")} {Part("e")}";
            case "limLow": return $"{Part("e")}_{Group(Part("lim"))}";
            case "limUpp": return $"{Part("e")}^{Group(Part("lim"))}";
            case "acc": return Part("e") + Set("accPr", "chr", "\u0302");
            case "del" or "moveFrom": return "";
        }
        return el.Name.LocalName.EndsWith("Pr", StringComparison.Ordinal) ? "" : Inner();
    }

    void ReadRun(XElement r, RunProps runBase, string? link, ref DParagraph current,
        List<DBlock> output, List<DBlock> pending, ParaProps props, List<Style> styles)
    {
        var rp = runBase.Clone();
        var rPr = r.Element(W + "rPr");
        if (Val(rPr?.Element(W + "rStyle")) is { } rStyle) foreach (var s in Chain(rStyle)) ApplyRun(rp, s.RPr);
        ApplyRun(rp, rPr);
        var style = rp.ToStyle();
        // A field's begin, code and end still count in hidden text.
        foreach (var el in rp.Hidden ? r.Elements().Where(e => e.Name == W + "fldChar" || e.Name == W + "instrText") : r.Elements())
        {
            var n = el.Name;
            // Complex fields: begin … code … separate … displayed result … end. Page-number fields
            // become live DFields and their stale result text is dropped.
            if (n == W + "fldChar")
            {
                switch (Attr(el, "fldCharType"))
                {
                    case "begin": _fields.Push(new FieldState()); break;
                    case "separate" when _fields.Count > 0:
                        var field = _fields.Peek();
                        field.Separated = true;
                        if (FieldKindOf(field.Code.ToString()) is { } kind)
                        {
                            current.Inlines.Add(new DField(kind, style));
                            field.ReplacesResult = true;
                        }
                        else field.Link = FieldLink(field.Code.ToString());
                        break;
                    case "end" when _fields.Count > 0:
                        var ended = _fields.Pop();
                        if (!ended.Separated && FieldKindOf(ended.Code.ToString()) is { } k) current.Inlines.Add(new DField(k, style));
                        break;
                }
                continue;
            }
            if (n == W + "instrText")
            {
                if (_fields.Count > 0 && !_fields.Peek().Separated) _fields.Peek().Code.Append(el.Value);
                continue;
            }
            if (_fields.Count > 0)
            {
                if (_fields.Any(f => f.ReplacesResult)) continue;
                // Anything before the result is part of the field's code (a nested field's result, mostly).
                if (!_fields.Peek().Separated && n != W + "drawing" && n != W + "pict" && n != MC + "AlternateContent") { if (n == W + "t") _fields.Peek().Code.Append(el.Value); continue; }
            }
            var runLink = link ?? (_fields.Count > 0 ? _fields.FirstOrDefault(f => f.Link != null)?.Link : null);

            if (n == W + "footnoteReference" || n == W + "endnoteReference")
            {
                bool endnote = n == W + "endnoteReference";
                var notes = endnote ? _endnotes : _footnotes;
                if (_currentNote == null && Attr(el, "id") is { } id && notes.TryGetValue(id, out var body) && DocumentLimits.CanGoDeeper(_depth))
                {
                    var note = new DNote(endnote ? ++_endnoteNumber : ++_footnoteNumber, endnote, style);
                    var saved = _currentNote;
                    _currentNote = note;
                    // The note is a story of its own: the field and paragraph around it don't reach into it.
                    var (fields, previous, previousStyle, join) = (_fields.ToArray(), _previous, _previousStyle, _joinNext);
                    _fields.Clear();
                    WithPart((endnote ? _endnotesPath : _footnotesPath)!, () => ReadBlocks(body.Elements(), note.Blocks, null));
                    _fields.Clear();
                    foreach (var f in fields.Reverse()) _fields.Push(f);
                    (_previous, _previousStyle, _joinNext) = (previous, previousStyle, join);
                    _currentNote = saved;
                    current.Inlines.Add(note);
                }
                continue;
            }
            if (n == W + "footnoteRef" || n == W + "endnoteRef")
            {
                // The note's own number at the start of its text.
                if (_currentNote != null) AddText(current, _currentNote.Endnote ? ListLabels.Format(_currentNote.Number, "lowerRoman") : _currentNote.Number.ToString(), style with { Superscript = true }, null);
                continue;
            }

            if (n == W + "t")
            {
                var text = rp.Caps ? el.Value.ToUpperInvariant() : el.Value;
                if (rp.Font is "Symbol" or "Wingdings" or "Wingdings 2" or "Wingdings 3" && string.Concat(text.Select(c => SymbolText(c, rp.Font) ?? c.ToString())) is var symbols && symbols != text)
                    AddText(current, symbols, style with { Font = null }, runLink);
                else AddText(current, text, style, runLink);
            }
            else if (n == W + "tab" || n == W + "ptab") AddText(current, "\t", style, runLink);
            else if (n == W + "noBreakHyphen") AddText(current, "\u2011", style, runLink);
            else if (n == W + "pgNum") current.Inlines.Add(new DField(FieldKind.Page, style));
            else if (n == W + "ruby")
            {
                // The reading follows the characters it belongs to, in brackets.
                if (DocumentLimits.CanGoDeeper(_depth))
                    ReadInlines(el.Element(W + "rubyBase")?.Elements() ?? [], runBase, runLink, ref current, output, pending, props, styles);
                var reading = string.Concat(el.Element(W + "rt")?.Descendants(W + "t").Select(t => t.Value) ?? []);
                if (reading.Trim().Length > 0) AddText(current, $"({reading.Trim()})", style, runLink);
            }
            else if (n == W + "sym")
            {
                var code = Attr(el, "char");
                if (code != null && int.TryParse(code, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var ch) && ch is > 0 and <= 0xFFFF)
                    AddText(current, SymbolText((char)ch, Attr(el, "font")) ?? ListLabels.NormalizeBullet(((char)ch).ToString()), style, runLink);
            }
            else if (n == W + "br" || n == W + "cr")
            {
                if (Attr(el, "type") == "page")
                {
                    // Split the paragraph: the rest continues on a new page.
                    var next = NewParagraph(props, styles);
                    next.SpaceBefore = 0;
                    next.PageBreakBefore = true;
                    output.Add(next);
                    current = next;
                }
                else current.Inlines.Add(new DLineBreak());
            }
            else if (n == W + "drawing" || n == W + "pict" || n == W + "object" || n == MC + "AlternateContent")
            {
                if (!DocumentLimits.CanGoDeeper(_depth)) { Flatten(el, pending); continue; }
                // What's in a text box is its own story.
                var (previous, previousStyle, join) = (_previous, _previousStyle, _joinNext);
                var fields = _fields.ToArray();
                _fields.Clear();
                _depth++;
                foreach (var d in n == MC + "AlternateContent" ? Unwrapped([el]) : [el])
                {
                    if (d.Name == W + "drawing") ReadDrawing(d, current, pending, runBase);
                    else if (d.Name == W + "pict" || d.Name == W + "object") ReadVml(d, current, pending);
                }
                _depth--;
                _fields.Clear();
                foreach (var f in fields.Reverse()) _fields.Push(f);
                (_previous, _previousStyle, _joinNext) = (previous, previousStyle, join);
            }
        }
    }

    static FieldKind? FieldKindOf(string? code)
    {
        var word = code?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
        return word switch { "PAGE" => FieldKind.Page, "NUMPAGES" or "SECTIONPAGES" => FieldKind.NumPages, _ => null };
    }

    TextStyle RunStyleOf(XElement? r, RunProps runBase)
    {
        var rp = runBase.Clone();
        var rPr = r?.Element(W + "rPr");
        if (Val(rPr?.Element(W + "rStyle")) is { } rStyle) foreach (var s in Chain(rStyle)) ApplyRun(rp, s.RPr);
        ApplyRun(rp, rPr);
        return rp.ToStyle();
    }

    static void AddText(DParagraph p, string text, TextStyle style, string? link)
    {
        if (text.Length == 0) return;
        if (p.Inlines.LastOrDefault() is DText last && last.Style == style && last.Link == link)
            last.Text += text;
        else
            p.Inlines.Add(new DText(text, style, link));
    }

    void ReadDrawing(XElement drawing, DParagraph current, List<DBlock> pending, RunProps runBase)
    {
        foreach (var container in drawing.Elements().Where(e => e.Name == WP + "inline" || e.Name == WP + "anchor"))
        {
            var extent = container.Element(WP + "extent");
            double width = (Long(Attr(extent, "cx")) ?? 0) / 12700.0, height = (Long(Attr(extent, "cy")) ?? 0) / 12700.0;

            if (DocumentLimits.Clamp(width, 4000) != width || DocumentLimits.Clamp(height, 4000) != height) (width, height) = (Math.Clamp(width, 0, 4000), Math.Clamp(height, 0, 4000));

            DImage? image = null;
            var alt = (string?)container.Element(WP + "docPr")?.Attribute("descr");
            // Text boxes belonging to this drawing, not to one inside them.
            var boxes = container.Descendants(W + "txbxContent").Where(b => b.Ancestors().TakeWhile(a => a != container).All(a => a.Name != W + "txbxContent")).ToList();
            var pictures = container.Descendants(A + "blip").Where(b => b.Ancestors().TakeWhile(a => a != container).All(a => a.Name != W + "txbxContent")).ToList();
            if (pictures.Count + boxes.Count > 1 && Group(container, width, height) is { Count: > 1 } parts)
            {
                // Several pictures or text boxes grouped together, each in its place.
                foreach (var part in parts)
                    if (container.Name == WP + "anchor") current.Inlines.Add(part);
                    else if (part.Image != null && part.Blocks.Count == 0) current.Inlines.Add(part.Image);
                    else pending.AddRange(part.Blocks);
                continue;
            }
            if (pictures.Select(b => Attr(b, R + "embed")).FirstOrDefault(i => i != null) is { } rid && LoadImage(rid) is { } data)
            {
                if (width < 1 || height < 1) (width, height) = ImageSize(data.data);
                image = new DImage(data.data, data.ext, width, height) { Alt = string.IsNullOrWhiteSpace(alt) ? null : alt };
            }
            else if (width >= 1 && height >= 1 && Chart(container) is { } chart) image = Charts.ToImage(chart, width, height);

            if (container.Name == WP + "anchor")
            {
                // Positioned picture or text box.
                var f = ReadAnchor(container, width, height);
                f.Image = image;
                foreach (var box in boxes) ReadBlocks(box.Elements(), f.Blocks, null);
                if (f.Image != null || f.Blocks.Count > 0) current.Inlines.Add(f);
                continue;
            }
            if (image != null) current.Inlines.Add(image);
            // An inline text box: keep its text as paragraphs after this one.
            foreach (var box in boxes) ReadBlocks(box.Elements(), pending, null);
        }
    }

    /// <summary>The pictures and text boxes of a group, placed where the group puts them.</summary>
    List<DFloat> Group(XElement container, double width, double height)
    {
        var parts = new List<DFloat>();
        bool anchored = container.Name == WP + "anchor";
        static double Emu(XElement? e, string name) => DocumentLimits.Clamp((Long(Attr(e, name)) ?? 0) / 12700.0, 1e6);
        void Add(XElement shape, double x, double y, double sx, double sy)
        {
            var xfrm = shape.Elements().FirstOrDefault(e => e.Name.LocalName is "spPr")?.Element(A + "xfrm");
            var f = anchored ? ReadAnchor(container, 0, 0) : new DFloat();
            f.Width = Emu(xfrm?.Element(A + "ext"), "cx") * sx;
            f.Height = Emu(xfrm?.Element(A + "ext"), "cy") * sy;
            if (f.Width < 1 || f.Height < 1) (f.Width, f.Height) = (Math.Max(1, width), Math.Max(1, height));
            f.X += x + Emu(xfrm?.Element(A + "off"), "x") * sx;
            f.Y += y + Emu(xfrm?.Element(A + "off"), "y") * sy;
            if (shape.Descendants(A + "blip").Select(b => Attr(b, R + "embed")).FirstOrDefault(i => i != null) is { } rid && LoadImage(rid) is { } data)
                f.Image = new DImage(data.data, data.ext, f.Width, f.Height);
            foreach (var box in shape.Elements().Where(e => e.Name.LocalName == "txbx").Elements(W + "txbxContent")) ReadBlocks(box.Elements(), f.Blocks, null);
            if (f.Image != null || f.Blocks.Count > 0) parts.Add(f);
        }
        void Walk(XElement group, double x, double y, double sx, double sy, int depth)
        {
            var xfrm = group.Elements().FirstOrDefault(e => e.Name.LocalName == "grpSpPr")?.Element(A + "xfrm");
            double chX = Emu(xfrm?.Element(A + "chOff"), "x"), chY = Emu(xfrm?.Element(A + "chOff"), "y");
            double chW = Emu(xfrm?.Element(A + "chExt"), "cx"), chH = Emu(xfrm?.Element(A + "chExt"), "cy");
            double w = depth == 0 ? width : Emu(xfrm?.Element(A + "ext"), "cx") * sx, h = depth == 0 ? height : Emu(xfrm?.Element(A + "ext"), "cy") * sy;
            if (depth > 0) (x, y) = (x + Emu(xfrm?.Element(A + "off"), "x") * sx, y + Emu(xfrm?.Element(A + "off"), "y") * sy);
            (sx, sy) = (chW > 0 ? w / chW : 1, chH > 0 ? h / chH : 1);
            (x, y) = (x - chX * sx, y - chY * sy);
            foreach (var child in group.Elements())
            {
                if (child.Name.LocalName == "grpSp" && depth < 10) Walk(child, x, y, sx, sy, depth + 1);
                else if (child.Name.LocalName is "pic" or "wsp") Add(child, x, y, sx, sy);
            }
        }
        foreach (var group in container.Descendants(A + "graphicData").Elements().Where(e => e.Name.LocalName is "wgp" or "wpc").Take(1))
            Walk(group, 0, 0, 1, 1, 0);
        return parts;
    }

    static DFloat ReadAnchor(XElement anchor, double width, double height)
    {
        var f = new DFloat { Width = width, Height = height };
        var ph = anchor.Element(WP + "positionH");
        var pv = anchor.Element(WP + "positionV");
        f.HorizontalFrom = (string?)ph?.Attribute("relativeFrom") switch
        {
            "page" => FloatFrom.Page,
            "character" => FloatFrom.Paragraph,
            _ => FloatFrom.Margin,
        };
        f.VerticalFrom = (string?)pv?.Attribute("relativeFrom") switch
        {
            "page" => FloatFrom.Page,
            "margin" or "topMargin" => FloatFrom.Margin,
            _ => FloatFrom.Paragraph,
        };
        f.X = DocumentLimits.Clamp((Long(ph?.Element(WP + "posOffset")?.Value) ?? 0) / 12700.0);
        f.Y = DocumentLimits.Clamp((Long(pv?.Element(WP + "posOffset")?.Value) ?? 0) / 12700.0);
        f.HorizontalAlign = ph?.Element(WP + "align")?.Value switch
        {
            "left" or "inside" => FloatAlign.Start,
            "center" => FloatAlign.Center,
            "right" or "outside" => FloatAlign.End,
            _ => FloatAlign.None,
        };
        f.VerticalAlign = pv?.Element(WP + "align")?.Value switch
        {
            "top" or "inside" => FloatAlign.Start,
            "center" => FloatAlign.Center,
            "bottom" or "outside" => FloatAlign.End,
            _ => FloatAlign.None,
        };
        bool behind = (string?)anchor.Attribute("behindDoc") is "1" or "true";
        f.Wrap = anchor.Element(WP + "wrapNone") != null ? (behind ? FloatWrap.Behind : FloatWrap.InFront)
               : anchor.Element(WP + "wrapTopAndBottom") != null ? FloatWrap.TopAndBottom
               : FloatWrap.Square;

        // Text box fill and outline.
        var spPr = anchor.Descendants().FirstOrDefault(e => e.Name.LocalName == "spPr" && e.Parent?.Name.LocalName == "wsp");
        if (spPr != null)
        {
            f.Fill = Units.ParseHex((string?)spPr.Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val"));
            var ln = spPr.Element(A + "ln");
            if (ln != null && ln.Element(A + "noFill") == null)
                f.Border = Units.ParseHex((string?)ln.Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val")) ?? Colors.Black;
        }
        return f;
    }

    void ReadVml(XElement pict, DParagraph current, List<DBlock> pending)
    {
        bool Own(XElement e) => e.Ancestors().TakeWhile(a => a != pict).All(a => a.Name != W + "txbxContent");
        foreach (var data in pict.Descendants(V + "imagedata").Where(Own).ToList())
        {
            if (Attr(data, R + "id") is not { } rid || LoadImage(rid) is not { } image) continue;
            var (w, h, _, _, _) = VmlStyle(data.Parent);
            var (pw, ph) = ImageSize(image.data);
            current.Inlines.Add(new DImage(image.data, image.ext, w is > 1 ? w.Value : pw, h is > 1 ? h.Value : ph) { Alt = (string?)data.Parent?.Attribute("alt") });
        }
        foreach (var box in pict.Descendants(V + "textbox").Where(Own).ToList())
        {
            var blocks = new List<DBlock>();
            ReadBlocks(box.Elements(W + "txbxContent").Elements(), blocks, null);
            if (blocks.Count == 0) continue;
            var shape = box.Parent;
            var (w, h, x, y, absolute) = VmlStyle(shape);
            // In line with the text, or inside a group (whose own units its parts are placed in): the text follows the paragraph.
            if (!absolute || w is not > 1 || h is not > 1 || shape?.Parent?.Name == V + "group") { pending.AddRange(blocks); continue; }
            var f = new DFloat { Width = w.Value, Height = h.Value, X = x, Y = y, Wrap = FloatWrap.Square };
            var style = (string?)shape?.Attribute("style") ?? "";
            string? Setting(string name) => System.Text.RegularExpressions.Regex.Match(style, $@"(?:^|;)\s*{name}\s*:\s*([^;]+)").Groups[1].Value is { Length: > 0 } v ? v.Trim() : null;
            if (Setting("mso-position-horizontal-relative") == "page") f.HorizontalFrom = FloatFrom.Page;
            f.VerticalFrom = Setting("mso-position-vertical-relative") switch { "page" => FloatFrom.Page, "margin" => FloatFrom.Margin, _ => FloatFrom.Paragraph };
            f.HorizontalAlign = Setting("mso-position-horizontal") switch { "left" => FloatAlign.Start, "center" => FloatAlign.Center, "right" => FloatAlign.End, _ => FloatAlign.None };
            f.VerticalAlign = Setting("mso-position-vertical") switch { "top" => FloatAlign.Start, "center" => FloatAlign.Center, "bottom" => FloatAlign.End, _ => FloatAlign.None };
            if ((string?)shape?.Attribute("filled") is not ("f" or "false")) f.Fill = VmlColor((string?)shape?.Attribute("fillcolor"));
            if ((string?)shape?.Attribute("stroked") is not ("f" or "false")) f.Border = VmlColor((string?)shape?.Attribute("strokecolor")) ?? Colors.Black;
            f.Blocks.AddRange(blocks);
            current.Inlines.Add(f);
        }
        // WordArt and old text effects.
        foreach (var path in pict.Descendants(V + "textpath").Where(Own))
            if ((string?)path.Attribute("string") is { Length: > 0 and < 2000 } text) AddText(current, text, TextStyle.Plain, null);
    }

    static Color? VmlColor(string? value) => value == null ? null : HtmlReader.CssColor(value.Split(' ')[0].ToLowerInvariant());

    static (double? w, double? h, double x, double y, bool absolute) VmlStyle(XElement? shape)
    {
        double? w = null, h = null;
        double x = 0, y = 0;
        bool absolute = false;
        foreach (var part in ((string?)shape?.Attribute("style") ?? "").Split(';'))
        {
            var kv = part.Split(':', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].Trim())
            {
                case "width": w = Units.ParseLength(kv[1]); break;
                case "height": h = Units.ParseLength(kv[1]); break;
                case "margin-left": x = Units.ParseLength(kv[1]) ?? 0; break;
                case "margin-top": y = Units.ParseLength(kv[1]) ?? 0; break;
                case "position": absolute = kv[1].Trim() == "absolute"; break;
            }
        }
        return (w, h, x, y, absolute);
    }

    /// <summary>A chart drawn from the data Word keeps with it.</summary>
    Chart? Chart(XElement container)
    {
        var data = container.Descendants(A + "graphicData").FirstOrDefault();
        if (((string?)data?.Attribute("uri"))?.EndsWith("/chart") != true) return null;
        var reference = data!.Elements().FirstOrDefault(e => e.Name.LocalName == "chart");
        if (Attr(reference, R + "id") is not { } rid || !_rels.TryGetValue(rid, out var rel) || rel.external) return null;
        if (Load(Resolve(rel.target))?.Root is not { } root) return null;
        System.Windows.Media.Color? Scheme(string name) => _themeColors.TryGetValue(name switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => name }, out var c) ? c : null;
        return Charts.ReadDrawingML(root, Scheme, _minorFont ?? "Calibri");
    }

    /// <summary>A section's page size, margins and columns.</summary>
    static PageSetup Setup(XElement sect)
    {
        var size = sect.Element(W + "pgSz");
        var margin = sect.Element(W + "pgMar");
        var columns = sect.Element(W + "cols");
        double w = Twips(Attr(size, "w")) ?? 612, h = Twips(Attr(size, "h")) ?? 792;
        return new PageSetup(w, h,
            new Thickness(Twips(Attr(margin, "left")) ?? 72, Twips(Attr(margin, "top")) ?? 72, Twips(Attr(margin, "right")) ?? 72, Twips(Attr(margin, "bottom")) ?? 72),
            Math.Max(1, Int(Attr(columns, "num")) ?? 1), Twips(Attr(columns, "space")) ?? 36);
    }

    (byte[] data, string ext)? LoadImage(string rid)
    {
        if (!_rels.TryGetValue(rid, out var rel) || rel.external) return null;
        var path = Resolve(rel.target);
        if (_images.TryGetValue(path, out var known)) return known;
        (byte[], string)? image = null;
        if (_zip.GetEntry(path) is { } entry)
        {
            using var stream = PackageResources.Open(entry, media: true);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            // Metafiles (clip art, pasted charts) and SVG become PNG so every output format can show them.
            image = DocumentPictures.Embeddable(ms.ToArray());
        }
        return _images[path] = image;
    }

    /// <summary>Pictures already read: a logo can appear on every page.</summary>
    readonly Dictionary<string, (byte[] data, string ext)?> _images = [];

    internal static (double w, double h) ImageSize(byte[] data)
    {
        try
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(data),
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
            return (frame.PixelWidth * 72 / Math.Max(1, frame.DpiX), frame.PixelHeight * 72 / Math.Max(1, frame.DpiY));
        }
        catch
        {
            return (144, 144);
        }
    }

    // ------------------------------------------------------------------ tables

    DTable ReadTable(XElement tbl)
    {
        var table = new DTable();
        var tblPr = tbl.Element(W + "tblPr");
        var styleId = Val(tblPr?.Element(W + "tblStyle"));
        Style? style = styleId != null && _styles.TryGetValue(styleId, out var s) ? s : null;

        var borderSources = new List<XElement?> { tblPr?.Element(W + "tblBorders") };
        if (styleId != null) borderSources.AddRange(Chain(styleId).Select(st => st.TblPr?.Element(W + "tblBorders")));
        table.Borders = borderSources.Any(HasVisibleBorder) || tbl.Descendants(W + "tcBorders").Any(HasVisibleBorder);

        var grid = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(g => Twips(Attr(g, "w")) ?? 0).ToList();
        if (grid != null && grid.Count > 0 && grid.All(w => w > 0)) table.ColumnWidths = grid;

        _tableDepth++;
        // Cells merged downwards, by the column they start in.
        var merged = new Dictionary<int, DCell>();
        foreach (var tr in Unwrapped(tbl.Elements()).Where(e => e.Name == W + "tr"))
        {
            var trPr = tr.Element(W + "trPr");
            if (trPr?.Element(W + "del") != null) continue; // a tracked deletion of the whole row
            var row = new DRow { Header = trPr?.Element(W + "tblHeader") is { } header && On(header) };
            int column = 0, covered = 0;
            void Gap(string name)
            {
                if (Int(Val(trPr?.Element(W + name))) is not (> 0 and var gap)) return;
                row.Cells.Add(new DCell { ColSpan = Math.Min(gap, DocumentLimits.Columns) });
                column += Math.Min(gap, DocumentLimits.Columns);
            }
            Gap("gridBefore");
            foreach (var tc in Unwrapped(tr.Elements()).Where(e => e.Name == W + "tc"))
            {
                var tcPr = tc.Element(W + "tcPr");
                if (tcPr?.Element(W + "cellDel") != null) continue;
                if (++_cells > DocumentLimits.Cells || column > DocumentLimits.Columns * 8) throw new ConversionException("This document has a table that's too big.");
                int span = Math.Clamp(Int(Val(tcPr?.Element(W + "gridSpan"))) ?? 1, 1, DocumentLimits.Columns);
                var merge = tcPr?.Element(W + "vMerge");
                if (merge != null && Attr(merge, "val") is null or "continue" && merged.TryGetValue(column, out var above) && above.ColSpan == span && tcPr?.Element(W + "hMerge") == null)
                {
                    above.RowSpan++;
                    covered++;
                    column += span;
                    continue;
                }
                // The old way of merging across: the cells after the first only take up room.
                if (tcPr?.Element(W + "hMerge") is { } across && Attr(across, "val") is null or "continue" && row.Cells.LastOrDefault() is { } first && column > 0 && covered == 0)
                {
                    first.ColSpan += span;
                    column += span;
                    continue;
                }
                var cell = new DCell { ColSpan = span, Shading = Fill(tcPr?.Element(W + "shd")) };
                ReadBlocks(tc.Elements(), cell.Blocks, style);
                _joinNext = null;
                row.Cells.Add(cell);
                if (merge != null && Attr(merge, "val") == "restart") merged[column] = cell;
                else merged.Remove(column);
                column += span;
            }
            Gap("gridAfter");
            // Merges end where a row doesn't carry them on.
            foreach (var start in merged.Keys.Where(k => k >= column).ToList()) merged.Remove(start);
            if (row.Cells.Count > 0 || covered > 0) table.Rows.Add(row);
        }
        _tableDepth--;
        DocumentLimits.TrimColumns(table);
        return table;
    }

    static bool HasVisibleBorder(XElement? borders) =>
        borders != null && borders.Elements().Any(b => Attr(b, "val") is { } v && v != "nil" && v != "none");

    // ------------------------------------------------------------------ properties

    IEnumerable<Style> Chain(string styleId)
    {
        var chain = new List<Style>();
        var seen = new HashSet<string>();
        for (var id = styleId; id != null && seen.Add(id) && _styles.TryGetValue(id, out var style); id = style.BasedOn)
            chain.Insert(0, style);
        return chain;
    }

    void ApplyPara(ParaProps p, XElement? pPr)
    {
        if (pPr == null) return;
        foreach (var el in pPr.Elements())
        {
            var n = el.Name.LocalName;
            switch (n)
            {
                case "jc":
                    p.Align = Attr(el, "val") switch
                    {
                        "center" => TextAlignment.Center,
                        "right" or "end" => TextAlignment.Right,
                        "both" or "distribute" => TextAlignment.Justify,
                        _ => TextAlignment.Left,
                    };
                    break;
                case "bidi": p.Bidi = On(el); break;
                case "spacing":
                    if (Twips(Attr(el, "before")) is double b) p.Before = b;
                    if (Twips(Attr(el, "after")) is double a) p.After = a;
                    if (Attr(el, "beforeAutospacing") is "1" or "true") p.Before = 14;
                    if (Attr(el, "afterAutospacing") is "1" or "true") p.After = 14;
                    if (Int(Attr(el, "line")) is int line && line > 0)
                        p.Line = Attr(el, "lineRule") is null or "auto" ? line / 240.0 : Math.Max(1, line / 20.0 / 13.5);
                    break;
                case "ind":
                    if (Twips(Attr(el, "left") ?? Attr(el, "start")) is double left) p.Left = left;
                    if (Twips(Attr(el, "firstLine")) is double first) p.First = first;
                    if (Twips(Attr(el, "hanging")) is double hanging) p.First = -hanging;
                    break;
                case "numPr":
                    if (Val(el.Element(W + "numId")) is { } numId) p.NumId = numId;
                    if (Int(Val(el.Element(W + "ilvl"))) is int ilvl) p.Ilvl = ilvl;
                    break;
                case "outlineLvl": p.OutlineLevel = Int(Attr(el, "val")); break;
                case "pageBreakBefore": p.PageBreakBefore = On(el); break;
                case "contextualSpacing": p.ContextualSpacing = On(el); break;
            }
        }
    }

    void ApplyRun(RunProps r, XElement? rPr)
    {
        if (rPr == null) return;
        foreach (var el in rPr.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "b": r.Bold = On(el); break;
                case "i": r.Italic = On(el); break;
                case "bCs": r.BoldCs = On(el); break;
                case "iCs": r.ItalicCs = On(el); break;
                case "szCs": if (Int(Attr(el, "val")) is int szCs) r.SizeCs = szCs / 2.0; break;
                case "rtl": r.Rtl = On(el); break;
                case "caps": r.Caps = On(el); break;
                case "vanish": r.Hidden = On(el); break;
                case "strike":
                case "dstrike": r.Strike = On(el); break;
                case "u": r.Underline = Attr(el, "val") is { } u ? u != "none" : true; break;
                case "sz": if (Int(Attr(el, "val")) is int sz) r.Size = sz / 2.0; break;
                case "color": r.Color = Attr(el, "val") is "auto" ? null : Units.ParseHex(Attr(el, "val")); break;
                case "highlight": r.Highlight = HighlightColor(Attr(el, "val")); break;
                case "shd": if (Fill(el) is { } fill) r.Highlight = fill; break;
                case "vertAlign": r.Vert = Attr(el, "val"); break;
                case "rFonts":
                    var font = Attr(el, "ascii") ?? Attr(el, "hAnsi");
                    var theme = Attr(el, "asciiTheme") ?? Attr(el, "hAnsiTheme");
                    if (theme != null) font = theme.StartsWith("major") ? _majorFont ?? font : _minorFont ?? font;
                    if (!string.IsNullOrWhiteSpace(font)) r.Font = font;
                    if (Attr(el, "cs") is { Length: > 0 } cs) r.FontCs = cs;
                    break;
            }
        }
    }

    static Color? Fill(XElement? shd)
    {
        var fill = Attr(shd, "fill");
        return fill is null or "auto" ? null : Units.ParseHex(fill);
    }

    static Color? HighlightColor(string? name) => name switch
    {
        "yellow" => Color.FromRgb(255, 255, 0),
        "green" => Color.FromRgb(0, 255, 0),
        "cyan" => Color.FromRgb(0, 255, 255),
        "magenta" => Color.FromRgb(255, 0, 255),
        "blue" => Color.FromRgb(0, 0, 255),
        "red" => Color.FromRgb(255, 0, 0),
        "darkBlue" => Color.FromRgb(0, 0, 128),
        "darkCyan" => Color.FromRgb(0, 128, 128),
        "darkGreen" => Color.FromRgb(0, 128, 0),
        "darkMagenta" => Color.FromRgb(128, 0, 128),
        "darkRed" => Color.FromRgb(128, 0, 0),
        "darkYellow" => Color.FromRgb(128, 128, 0),
        "darkGray" => Color.FromRgb(128, 128, 128),
        "lightGray" => Color.FromRgb(192, 192, 192),
        "black" => Color.FromRgb(0, 0, 0),
        "white" => Color.FromRgb(255, 255, 255),
        _ => null,
    };

    // ------------------------------------------------------------------ helpers

    /// <summary>The first version of some content that uses only what this reader knows, or else the simple version.</summary>
    static IEnumerable<XElement> Alternate(XElement alternate)
    {
        var choice = alternate.Elements(MC + "Choice").FirstOrDefault(c => ((string?)c.Attribute("Requires") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(prefix => prefix is "wps" or "wpg" or "wpc" or "wp14" or "w14" or "w15" or "w16se" or "a14" or "v" or "wne" || c.GetNamespaceOfPrefix(prefix) is { } ns && (ns == WPS || ns == A || ns == Pic)));
        return (choice ?? alternate.Element(MC + "Fallback") ?? alternate.Element(MC + "Choice"))?.Elements() ?? [];
    }

    XDocument? Load(string path)
    {
        var entry = _zip.GetEntry(path);
        if (entry == null) return null;
        using var stream = PackageResources.Open(entry);
        return PackageResources.LoadXml(stream, LoadOptions.PreserveWhitespace);
    }

    static string RelsPath(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? $"_rels/{part}.rels" : $"{part[..slash]}/_rels/{part[(slash + 1)..]}.rels";
    }

    string Resolve(string target)
    {
        try { target = Uri.UnescapeDataString(target); } catch (UriFormatException) { }
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = (_docDir.Length > 0 ? _docDir + "/" + target : target).Split('/').ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { parts.RemoveAt(i); if (i > 0) parts.RemoveAt(--i); i--; }
            else if (parts[i] == ".") parts.RemoveAt(i--);
        }
        return string.Join('/', parts);
    }

    static string? Attr(XElement? el, string name) => (string?)el?.Attribute(W + name) ?? (string?)el?.Attribute(name);
    static string? Attr(XElement? el, XName name) => (string?)el?.Attribute(name);
    static string? Val(XElement? el) => Attr(el, "val");
    static bool On(XElement el) => Attr(el, "val") is not ("0" or "false" or "off" or "none");
    static int? Int(string? s) => int.TryParse(s, out var v) ? v : null;
    static long? Long(string? s) => long.TryParse(s, out var v) ? v : null;
    static double? Twips(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? DocumentLimits.Clamp(v / 20) : null;
}
