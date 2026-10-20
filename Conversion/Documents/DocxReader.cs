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

    readonly ZipArchive _zip;
    // Relationships (images, links) are per part: the body, each header/footer and the notes all have their own.
    string _docDir;
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
    }
    readonly Dictionary<string, Style> _styles = [];
    readonly Dictionary<string, NumberingInstance> _numbering = [];
    readonly Dictionary<string, int[]> _counters = [];
    readonly XElement? _defaultRPr, _defaultPPr;
    readonly string? _defaultParaStyle;
    string? _majorFont, _minorFont;
    bool _pendingPageBreak;
    DParagraph? _previous;
    string? _previousStyle;

    sealed record Style(string Id, string? Name, string? BasedOn, XElement? PPr, XElement? RPr, XElement? TblPr, string Type);
    sealed record Level(int Start, string Format, string Text, XElement? PPr, XElement? RPr);
    sealed record NumberingInstance(string Key, Dictionary<int, Level> Levels);

    sealed class RunProps
    {
        public bool Bold, Italic, Underline, Strike, Caps, Hidden;
        public string? Vert, Font;
        public double? Size;
        public Color? Color, Highlight;

        public RunProps Clone() => (RunProps)MemberwiseClone();

        public TextStyle ToStyle() => new()
        {
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            Strike = Strike,
            Superscript = Vert == "superscript",
            Subscript = Vert == "subscript",
            Size = Size,
            Color = Color,
            Highlight = Highlight,
            Font = Font,
        };
    }

    sealed class ParaProps
    {
        public TextAlignment Align = TextAlignment.Left;
        public double Before, After, Left, First, Line = 1;
        public bool PageBreakBefore, ContextualSpacing;
        public string? NumId;
        public int Ilvl;
        public int? OutlineLevel;
    }

    public static DDocument Read(string path)
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
        if (officeDoc != null) mainPath = ((string)officeDoc.Attribute("Target")!).TrimStart('/');
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
            foreach (var an in numbering.Elements(W + "abstractNum"))
            {
                var levels = new Dictionary<int, Level>();
                foreach (var lvl in an.Elements(W + "lvl"))
                {
                    int ilvl = Int(Attr(lvl, "ilvl")) ?? 0;
                    levels[ilvl] = new Level(Int(Val(lvl.Element(W + "start"))) ?? 1, Val(lvl.Element(W + "numFmt")) ?? "decimal",
                        Val(lvl.Element(W + "lvlText")) ?? "", lvl.Element(W + "pPr"), lvl.Element(W + "rPr"));
                }
                if (Attr(an, "abstractNumId") is { } aid) abstracts[aid] = levels;
            }
            foreach (var num in numbering.Elements(W + "num"))
            {
                var numId = Attr(num, "numId");
                var aid = Val(num.Element(W + "abstractNumId"));
                if (numId == null || aid == null || !abstracts.TryGetValue(aid, out var baseLevels)) continue;
                var levels = new Dictionary<int, Level>(baseLevels);
                foreach (var ov in num.Elements(W + "lvlOverride"))
                {
                    int ilvl = Int(Attr(ov, "ilvl")) ?? 0;
                    if (Int(Val(ov.Element(W + "startOverride"))) is int start && levels.TryGetValue(ilvl, out var l))
                        levels[ilvl] = l with { Start = start };
                }
                _numbering[numId] = new NumberingInstance(numId, levels);
            }
        }
    }

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

        ReadBlocks(body.Elements(), doc.Blocks, null);

        if (body.Element(W + "sectPr") is { } sect)
        {
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
        foreach (var el in elements)
        {
            if (el.Name == W + "p") ReadParagraph(el, output, tableStyle);
            else if (el.Name == W + "tbl") output.Add(ReadTable(el));
            else if (el.Name == W + "sdt") ReadBlocks(el.Element(W + "sdtContent")?.Elements() ?? [], output, tableStyle);
            else if (el.Name == W + "customXml" || el.Name == W + "ins" || el.Name == W + "smartTag") ReadBlocks(el.Elements(), output, tableStyle);
            else if (el.Name == MC + "AlternateContent") ReadBlocks(Alternate(el), output, tableStyle);
        }
    }

    void ReadParagraph(XElement p, List<DBlock> output, Style? tableStyle)
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
        Level? level = null;
        if (numId != null && numId != "0" && _numbering.TryGetValue(numId, out var instance))
        {
            instance.Levels.TryGetValue(ilvl, out level);
            if (level != null) ApplyPara(props, level.PPr);
        }
        ApplyPara(props, pPr);

        var runBase = new RunProps { Size = 10 };
        ApplyRun(runBase, _defaultRPr);
        if (tableStyle != null) foreach (var s in Chain(tableStyle.Id)) ApplyRun(runBase, s.RPr);
        foreach (var s in styles) ApplyRun(runBase, s.RPr);

        var para = NewParagraph(props, styles);
        if (level != null && numId != null)
        {
            para.ListLevel = ilvl;
            para.List = level.Format == "bullet" ? ListKind.Bullet : ListKind.Number;
            para.ListLabel = Label(numId, ilvl, level);
        }

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
        output.Add(current);
        ReadInlines(p.Elements().Where(e => e.Name != W + "pPr"), runBase, null, ref current, output, pending, props, styles);
        // A page break at the end of a paragraph leaves nothing after it; Word doesn't show that as an
        // empty line at the top of the next page, so carry the break to the next paragraph instead.
        if (current != para && current.Inlines.Count == 0 && current.PageBreakBefore)
        {
            output.Remove(current);
            _pendingPageBreak = true;
            current = para;
        }
        output.AddRange(pending);

        if (pPr?.Element(W + "sectPr") is { } sect && Attr(sect.Element(W + "type"), "val") != "continuous")
            _pendingPageBreak = true;

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

        var name = styles.LastOrDefault()?.Name?.ToLowerInvariant();
        if (props.OutlineLevel is int outline and < 9) para.Heading = Math.Min(6, outline + 1);
        else if (name != null && name.StartsWith("heading ") && int.TryParse(name[8..], out var h)) para.Heading = Math.Clamp(h, 1, 6);
        else if (name == "title") para.Heading = 1;
        return para;
    }

    string Label(string numId, int ilvl, Level level)
    {
        if (!_counters.TryGetValue(numId, out var counts))
        {
            counts = new int[9];
            for (int i = 0; i < 9; i++) counts[i] = int.MinValue;
            _counters[numId] = counts;
        }
        var levels = _numbering[numId].Levels;
        if (counts[ilvl] == int.MinValue) counts[ilvl] = level.Start - 1;
        counts[ilvl]++;
        for (int i = ilvl + 1; i < 9; i++) counts[i] = int.MinValue;

        if (level.Format == "bullet") return ListLabels.NormalizeBullet(level.Text);
        var text = level.Text;
        for (int i = 0; i <= ilvl; i++)
        {
            var lvl = levels.TryGetValue(i, out var l) ? l : level;
            int value = counts[i] == int.MinValue ? lvl.Start : counts[i];
            text = text.Replace($"%{i + 1}", ListLabels.Format(value, lvl.Format));
        }
        return text;
    }

    // ------------------------------------------------------------------ inlines

    void ReadInlines(IEnumerable<XElement> elements, RunProps runBase, string? link, ref DParagraph current,
        List<DBlock> output, List<DBlock> pending, ParaProps props, List<Style> styles)
    {
        foreach (var el in elements)
        {
            var name = el.Name;
            if (name == W + "r") ReadRun(el, runBase, link, ref current, output, pending, props, styles);
            else if (name == W + "hyperlink")
            {
                string? target = null;
                if (Attr(el, R + "id") is { } rid && _rels.TryGetValue(rid, out var rel)) target = rel.target;
                ReadInlines(el.Elements(), runBase, target ?? link, ref current, output, pending, props, styles);
            }
            else if (name == W + "fldSimple" && FieldKindOf(Attr(el, "instr")) is { } kind)
                current.Inlines.Add(new DField(kind, RunStyleOf(el.Element(W + "r"), runBase)));
            else if (name == W + "ins" || name == W + "smartTag" || name == W + "customXml" || name == W + "fldSimple" ||
                     name == W + "moveTo" || name == W + "bdo" || name == W + "dir")
                ReadInlines(el.Elements(), runBase, link, ref current, output, pending, props, styles);
            else if (name == W + "sdt")
                ReadInlines(el.Element(W + "sdtContent")?.Elements() ?? [], runBase, link, ref current, output, pending, props, styles);
            else if (name == MC + "AlternateContent")
                ReadInlines(Alternate(el), runBase, link, ref current, output, pending, props, styles);
            else if (name == M + "oMathPara" || name == M + "oMath")
                current.Inlines.Add(new DText(string.Concat(el.Descendants(M + "t").Select(t => t.Value)), runBase.ToStyle()));
        }
    }

    void ReadRun(XElement r, RunProps runBase, string? link, ref DParagraph current,
        List<DBlock> output, List<DBlock> pending, ParaProps props, List<Style> styles)
    {
        var rp = runBase.Clone();
        var rPr = r.Element(W + "rPr");
        if (Val(rPr?.Element(W + "rStyle")) is { } rStyle) foreach (var s in Chain(rStyle)) ApplyRun(rp, s.RPr);
        ApplyRun(rp, rPr);
        if (rp.Hidden) return;
        var style = rp.ToStyle();

        foreach (var el in r.Elements())
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
            if (_fields.Any(f => f.ReplacesResult)) continue;

            if (n == W + "footnoteReference" || n == W + "endnoteReference")
            {
                bool endnote = n == W + "endnoteReference";
                var notes = endnote ? _endnotes : _footnotes;
                if (Attr(el, "id") is { } id && notes.TryGetValue(id, out var body))
                {
                    var note = new DNote(endnote ? ++_endnoteNumber : ++_footnoteNumber, endnote, style);
                    var saved = _currentNote;
                    _currentNote = note;
                    WithPart((endnote ? _endnotesPath : _footnotesPath)!, () => ReadBlocks(body.Elements(), note.Blocks, null));
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

            if (n == W + "t") AddText(current, rp.Caps ? el.Value.ToUpperInvariant() : el.Value, style, link);
            else if (n == W + "tab" || n == W + "ptab") AddText(current, "\t", style, link);
            else if (n == W + "noBreakHyphen") AddText(current, "-", style, link);
            else if (n == W + "sym")
            {
                var code = Attr(el, "char");
                if (code != null && int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var ch))
                    AddText(current, ListLabels.NormalizeBullet(((char)ch).ToString()), style, link);
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
            else if (n == W + "drawing") ReadDrawing(el, current, pending, runBase);
            else if (n == W + "pict" || n == W + "object") ReadVml(el, current);
            else if (n == MC + "AlternateContent")
            {
                var choice = Alternate(el).ToList();
                foreach (var d in choice.Where(c => c.Name == W + "drawing")) ReadDrawing(d, current, pending, runBase);
                foreach (var d in choice.Where(c => c.Name == W + "pict")) ReadVml(d, current);
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

            DImage? image = null;
            var blip = container.Descendants(A + "blip").FirstOrDefault();
            if (blip != null && Attr(blip, R + "embed") is { } rid && LoadImage(rid) is { } data)
                image = new DImage(data.data, data.ext, width, height);
            var boxes = container.Descendants(W + "txbxContent").Where(b => !b.Ancestors(W + "txbxContent").Any()).ToList();

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
        f.X = (Long(ph?.Element(WP + "posOffset")?.Value) ?? 0) / 12700.0;
        f.Y = (Long(pv?.Element(WP + "posOffset")?.Value) ?? 0) / 12700.0;
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

    void ReadVml(XElement pict, DParagraph current)
    {
        foreach (var data in pict.Descendants(V + "imagedata"))
        {
            if (Attr(data, R + "id") is not { } rid || LoadImage(rid) is not { } image) continue;
            var style = (string?)data.Parent?.Attribute("style") ?? "";
            double? w = null, h = null;
            foreach (var part in style.Split(';'))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2) continue;
                if (kv[0].Trim() == "width") w = Units.ParseLength(kv[1]);
                if (kv[0].Trim() == "height") h = Units.ParseLength(kv[1]);
            }
            var (pw, ph) = ImageSize(image.data);
            current.Inlines.Add(new DImage(image.data, image.ext, w ?? pw, h ?? ph));
        }
    }

    (byte[] data, string ext)? LoadImage(string rid)
    {
        if (!_rels.TryGetValue(rid, out var rel) || rel.external) return null;
        var path = Resolve(rel.target);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        ext = ext switch { "jpg" or "jpeg" => "jpeg", "tif" => "tiff", _ => ext };
        if (ext is not ("png" or "jpeg" or "gif" or "bmp" or "tiff")) return null; // EMF/WMF/SVG aren't supported
        var entry = _zip.GetEntry(path);
        if (entry == null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return (ms.ToArray(), ext);
    }

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

        foreach (var tr in tbl.Elements(W + "tr"))
        {
            var row = new DRow { Header = tr.Element(W + "trPr")?.Element(W + "tblHeader") != null };
            foreach (var tc in tr.Elements().SelectMany(e => e.Name == W + "sdt" ? e.Element(W + "sdtContent")?.Elements() ?? [] : [e])
                         .Where(e => e.Name == W + "tc"))
            {
                var tcPr = tc.Element(W + "tcPr");
                var cell = new DCell
                {
                    ColSpan = Int(Val(tcPr?.Element(W + "gridSpan"))) ?? 1,
                    Shading = Fill(tcPr?.Element(W + "shd")),
                };
                bool continuesMerge = tcPr?.Element(W + "vMerge") is { } vm && Attr(vm, "val") is null or "continue";
                if (!continuesMerge) ReadBlocks(tc.Elements(), cell.Blocks, style);
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0) table.Rows.Add(row);
        }
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
                    if (font != null) r.Font = font;
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

    static IEnumerable<XElement> Alternate(XElement alternate) =>
        (alternate.Element(MC + "Choice") ?? alternate.Element(MC + "Fallback"))?.Elements() ?? [];

    XDocument? Load(string path)
    {
        var entry = _zip.GetEntry(path);
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    static string RelsPath(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? $"_rels/{part}.rels" : $"{part[..slash]}/_rels/{part[(slash + 1)..]}.rels";
    }

    string Resolve(string target)
    {
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
    static double? Twips(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v / 20 : null;
}
