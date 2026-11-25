using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

/// <summary>Writes a Word .docx with real headings, lists, tables, hyperlinks and images.</summary>
internal sealed class DocxWriter
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    readonly DDocument _doc;
    readonly CancellationToken _ct;
    readonly List<XElement> _mainRels = [];
    // Relationships of the part currently being written (body, a header/footer, or the notes).
    List<XElement> _rels;
    readonly List<XElement> _footnoteRels = [], _endnoteRels = [];
    readonly List<XElement> _footnotes = [], _endnotes = [];
    readonly List<(string name, string type, XElement root, List<XElement> rels)> _parts = [];
    readonly List<(string name, byte[] data)> _media = [];
    readonly ListPlanner _lists = new();
    readonly Dictionary<string, int> _bookmarks = new(StringComparer.Ordinal);
    int _nextRel = 10, _nextDrawing = 1, _nextFootnote = 1, _nextEndnote = 1;

    DocxWriter(DDocument doc, CancellationToken ct)
    {
        _doc = doc;
        _ct = ct;
        _rels = _mainRels;
    }

    public static void Write(DDocument doc, string path, CancellationToken ct = default) => new DocxWriter(doc, ct).Save(path);

    PageSetup _section = null!;
    List<XElement>? _headerReferences;

    /// <summary>A section's settings: header/footer references (shared by all sections), page size, margins and columns.</summary>
    XElement SectPr(PageSetup setup)
    {
        if (_headerReferences == null)
        {
            _headerReferences = [];
            foreach (var (kind, type, part) in new[]
            {
                ("header", "default", _doc.Header), ("header", "first", _doc.FirstHeader), ("header", "even", _doc.EvenHeader),
                ("footer", "default", _doc.Footer), ("footer", "first", _doc.FirstFooter), ("footer", "even", _doc.EvenFooter),
            })
            {
                if (part == null || (type == "first" && !_doc.DifferentFirstPage)) continue;
                _headerReferences.Add(new XElement(W + (kind + "Reference"), new XAttribute(W + "type", type), new XAttribute(R + "id", HeaderFooterPart(kind, part))));
            }
        }
        var sectPr = new XElement(W + "sectPr", _headerReferences.Select(r => new XElement(r)));
        sectPr.Add(
            new XElement(W + "pgSz", new XAttribute(W + "w", Units.ToTwips(setup.Width)), new XAttribute(W + "h", Units.ToTwips(setup.Height)),
                setup.Width > setup.Height ? new XAttribute(W + "orient", "landscape") : null),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", Units.ToTwips(setup.Margins.Top)), new XAttribute(W + "right", Units.ToTwips(setup.Margins.Right)),
                new XAttribute(W + "bottom", Units.ToTwips(setup.Margins.Bottom)), new XAttribute(W + "left", Units.ToTwips(setup.Margins.Left)),
                new XAttribute(W + "header", Units.ToTwips(_doc.HeaderDistance)), new XAttribute(W + "footer", Units.ToTwips(_doc.FooterDistance)),
                new XAttribute(W + "gutter", 0)));
        if (setup.Columns > 1)
            sectPr.Add(new XElement(W + "cols", new XAttribute(W + "num", setup.Columns), new XAttribute(W + "space", Units.ToTwips(setup.ColumnGap))));
        if (_doc.DifferentFirstPage) sectPr.Add(new XElement(W + "titlePg"));
        return sectPr;
    }

    void Save(string path)
    {
        _section = new PageSetup(_doc.PageWidth, _doc.PageHeight, _doc.Margins, _doc.Columns, _doc.ColumnGap);
        var body = new XElement(W + "body");
        Blocks(_doc.Blocks, body, _doc.ContentWidth, topLevel: true);

        body.Add(SectPr(_section));

        var document = new XElement(W + "document", Namespaces(), body);

        _mainRels.Add(Rel("rId1", "styles", "styles.xml"));
        _mainRels.Add(Rel("rId2", "numbering", "numbering.xml"));
        _mainRels.Add(Rel("rId3", "settings", "settings.xml"));
        if (_footnotes.Count > 0) _mainRels.Add(Rel("rId4", "footnotes", "footnotes.xml"));
        if (_endnotes.Count > 0) _mainRels.Add(Rel("rId5", "endnotes", "endnotes.xml"));

        var settings = new XElement(W + "settings", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "defaultTabStop", new XAttribute(W + "val", 720)));
        if (_doc.EvenHeader != null || _doc.EvenFooter != null) settings.Add(new XElement(W + "evenAndOddHeaders"));
        // Without this Word opens the file in its "compatibility mode".
        settings.Add(new XElement(W + "compat", new XElement(W + "compatSetting", new XAttribute(W + "name", "compatibilityMode"),
            new XAttribute(W + "uri", "http://schemas.microsoft.com/office/word"), new XAttribute(W + "val", 15))));

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        Put(zip, "[Content_Types].xml", ContentTypes());
        Put(zip, "_rels/.rels", new XElement(PR + "Relationships",
            new XElement(PR + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", RelBase + "officeDocument"), new XAttribute("Target", "word/document.xml"))));
        Put(zip, "word/document.xml", document);
        Put(zip, "word/_rels/document.xml.rels", new XElement(PR + "Relationships", _mainRels));
        Put(zip, "word/styles.xml", Styles());
        Put(zip, "word/numbering.xml", Numbering());
        Put(zip, "word/settings.xml", settings);
        foreach (var (name, _, root, rels) in _parts)
        {
            Put(zip, "word/" + name, root);
            if (rels.Count > 0) Put(zip, $"word/_rels/{name}.rels", new XElement(PR + "Relationships", rels));
        }
        if (_footnotes.Count > 0)
        {
            Put(zip, "word/footnotes.xml", NotesPart("footnote", _footnotes));
            if (_footnoteRels.Count > 0) Put(zip, "word/_rels/footnotes.xml.rels", new XElement(PR + "Relationships", _footnoteRels));
        }
        if (_endnotes.Count > 0)
        {
            Put(zip, "word/endnotes.xml", NotesPart("endnote", _endnotes));
            if (_endnoteRels.Count > 0) Put(zip, "word/_rels/endnotes.xml.rels", new XElement(PR + "Relationships", _endnoteRels));
        }
        foreach (var (name, data) in _media)
        {
            using var stream = zip.CreateEntry("word/media/" + name, CompressionLevel.Optimal).Open();
            stream.Write(data);
        }
    }

    // ------------------------------------------------------------------ blocks

    void Blocks(List<DBlock> blocks, XElement parent, double available, bool topLevel)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            if (topLevel && i > 0 && blocks[i].Section is { } next)
            {
                // A section ends: Word keeps its settings in the last paragraph of the section.
                if (parent.LastNode is XElement last && last.Name == W + "p" && last.Element(W + "pPr") is { } ending && ending.Element(W + "sectPr") == null) ending.Add(SectPr(_section));
                else parent.Add(new XElement(W + "p", new XElement(W + "pPr", SectPr(_section))));
                _section = next;
                available = next.ContentWidth;
            }
            if (blocks[i] is DParagraph p) parent.Add(Paragraph(p, available));
            else if (blocks[i] is DTable t)
            {
                parent.Add(Table(t, available));
                // Word merges back-to-back tables, and a cell must end with a paragraph.
                bool ends = i == blocks.Count - 1;
                if ((!topLevel && ends) || (!ends && blocks[i + 1] is DTable)) parent.Add(new XElement(W + "p"));
            }
        }
        if (!topLevel && !parent.Elements(W + "p").Any()) parent.Add(new XElement(W + "p"));
    }

    XElement Paragraph(DParagraph p, double available)
    {
        var pPr = new XElement(W + "pPr");
        if (p.Heading > 0) pPr.Add(new XElement(W + "pStyle", new XAttribute(W + "val", $"Heading{p.Heading}")));
        if (p.PageBreakBefore) pPr.Add(new XElement(W + "pageBreakBefore"));

        var numbered = p.List != ListKind.None ? _lists.Next(p) : null;
        if (numbered is var (list, level))
            pPr.Add(new XElement(W + "numPr",
                new XElement(W + "ilvl", new XAttribute(W + "val", level)),
                new XElement(W + "numId", new XAttribute(W + "val", list.Id))));
        if (p.RightToLeft) pPr.Add(new XElement(W + "bidi"));

        var spacing = new XElement(W + "spacing",
            new XAttribute(W + "before", Units.ToTwips(p.SpaceBefore)), new XAttribute(W + "after", Units.ToTwips(p.SpaceAfter)),
            new XAttribute(W + "line", (int)Math.Round(p.LineSpacing * 240)), new XAttribute(W + "lineRule", "auto"));
        pPr.Add(spacing);

        if (numbered == null ? p.IndentLeft != 0 || p.IndentFirst != 0 : p.IndentLeft > 0 && p.IndentFirst < 0)
        {
            var ind = new XElement(W + "ind", new XAttribute(W + "left", Units.ToTwips(p.IndentLeft)));
            if (p.IndentFirst > 0) ind.Add(new XAttribute(W + "firstLine", Units.ToTwips(p.IndentFirst)));
            else if (p.IndentFirst < 0) ind.Add(new XAttribute(W + "hanging", Units.ToTwips(-p.IndentFirst)));
            pPr.Add(ind);
        }
        // Word's "left" is the starting side, which is the right in right-to-left text.
        if (p.Align != (p.RightToLeft ? TextAlignment.Right : TextAlignment.Left))
            pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", p.Align switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Justify => "both",
                _ => "right",
            })));
        if (p.Inlines.Count == 0 && p.MarkSize is double mark && Math.Abs(mark - _doc.FontSize) > 0.1)
            pPr.Add(new XElement(W + "rPr", new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(mark * 2)))));

        var para = new XElement(W + "p", pPr);
        // A marker that isn't a list Word can number.
        if (p.ListLabel is { Length: > 0 } label && numbered == null)
            para.Add(new XElement(W + "r", new XElement(W + "t", Clean(label)), new XElement(W + "tab")));
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var target = LinkTarget(t.Link);
                    var runs = Runs(t, p.Heading > 0, target != null, p.RightToLeft);
                    if (target is ['#', .. var place]) para.Add(new XElement(W + "hyperlink", new XAttribute(W + "anchor", Bookmark(place)), new XAttribute(W + "history", 1), runs));
                    else if (target != null)
                    {
                        if (!_links.TryGetValue((_rels, target), out var id)) _links[(_rels, target)] = id = NewRel("hyperlink", target, external: true);
                        para.Add(new XElement(W + "hyperlink", new XAttribute(R + "id", id), runs));
                    }
                    else para.Add(runs);
                    break;
                case DAnchor a:
                    if (_placed.Add(a.Name))
                    {
                        int bookmarkId = _placed.Count;
                        para.Add(new XElement(W + "bookmarkStart", new XAttribute(W + "id", bookmarkId), new XAttribute(W + "name", Bookmark(a.Name))),
                            new XElement(W + "bookmarkEnd", new XAttribute(W + "id", bookmarkId)));
                    }
                    break;
                case DLineBreak:
                    para.Add(new XElement(W + "r", new XElement(W + "br")));
                    break;
                case DImage img:
                    para.Add(Image(img, available));
                    break;
                case DField f:
                    var fieldRun = new XElement(W + "r");
                    if (RunProps(f.Style, false, false) is { HasElements: true } fieldProps) fieldRun.Add(fieldProps);
                    fieldRun.Add(new XElement(W + "t", "1"));
                    para.Add(new XElement(W + "fldSimple", new XAttribute(W + "instr", f.Kind == FieldKind.Page ? " PAGE " : " NUMPAGES "), fieldRun));
                    break;
                case DNote n:
                    para.Add(NoteReference(n));
                    break;
                case DFloat f:
                    para.Add(Anchor(f));
                    break;
            }
        }
        return para;
    }

    readonly Dictionary<(List<XElement> part, string target), string> _links = [];
    readonly HashSet<string> _placed = new(StringComparer.Ordinal);

    /// <summary>A link Word can follow: a place in the document, an address, or a relative one.</summary>
    static string? LinkTarget(string? link)
    {
        if (link is ['#', _, ..]) return link;
        if (Links.ForDocument(link) is not { } safe || safe.StartsWith('#')) return null;
        if (Uri.IsWellFormedUriString(safe, UriKind.RelativeOrAbsolute)) return safe;
        return Uri.TryCreate(safe, UriKind.Absolute, out var uri) && Uri.IsWellFormedUriString(uri.AbsoluteUri, UriKind.Absolute) ? uri.AbsoluteUri : null;
    }

    /// <summary>A bookmark's name as Word allows it: letters, digits and underscores, 40 at most, each only once.</summary>
    string Bookmark(string name)
    {
        if (!_bookmarks.TryGetValue(name, out int number)) _bookmarks[name] = number = _bookmarks.Count + 1;
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) && !char.IsSurrogate(c) ? c : '_').ToArray());
        if (clean == name && name.Length <= 40 && char.IsLetter(name[0]) || clean == name && name.Length <= 40 && name[0] == '_') return name;
        return $"b{number}_{(clean.Length > 30 ? clean[..30] : clean)}";
    }

    // ------------------------------------------------------------------ notes, headers, floats

    XElement NoteReference(DNote note)
    {
        bool endnote = note.Endnote;
        int id = endnote ? _nextEndnote++ : _nextFootnote++;
        var tag = endnote ? "endnote" : "footnote";

        // Note content lives in its own part (with its own relationships for links and pictures).
        var savedRels = _rels;
        _rels = endnote ? _endnoteRels : _footnoteRels;
        var element = new XElement(W + tag, new XAttribute(W + "id", id));
        var blocks = FlowBridge.WithoutMark(note);
        Blocks(blocks, element, _doc.ContentWidth, topLevel: false);
        if (element.FirstNode is not XElement first || first.Name != W + "p") element.AddFirst(first = new XElement(W + "p"));
        var pPr = first.Element(W + "pPr");
        var markRun = new XElement(W + "r",
            new XElement(W + "rPr", new XElement(W + "rStyle", new XAttribute(W + "val", endnote ? "EndnoteReference" : "FootnoteReference"))),
            new XElement(W + (endnote ? "endnoteRef" : "footnoteRef")));
        var space = new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), " "));
        if (pPr != null) pPr.AddAfterSelf(markRun, space);
        else first.AddFirst(markRun, space);
        _rels = savedRels;
        (endnote ? _endnotes : _footnotes).Add(element);

        return new XElement(W + "r",
            new XElement(W + "rPr", new XElement(W + "rStyle", new XAttribute(W + "val", endnote ? "EndnoteReference" : "FootnoteReference"))),
            new XElement(W + (endnote ? "endnoteReference" : "footnoteReference"), new XAttribute(W + "id", id)));
    }


    static XElement NotesPart(string tag, List<XElement> notes)
    {
        // Word expects the separator lines as notes -1 and 0.
        var root = new XElement(W + (tag + "s"), Namespaces(),
            new XElement(W + tag, new XAttribute(W + "type", "separator"), new XAttribute(W + "id", -1),
                new XElement(W + "p", new XElement(W + "r", new XElement(W + "separator")))),
            new XElement(W + tag, new XAttribute(W + "type", "continuationSeparator"), new XAttribute(W + "id", 0),
                new XElement(W + "p", new XElement(W + "r", new XElement(W + "continuationSeparator")))));
        root.Add(notes);
        return root;
    }

    /// <summary>Writes a header or footer part and returns its relationship id.</summary>
    string HeaderFooterPart(string kind, DHeaderFooter part)
    {
        var name = $"{kind}{_parts.Count(p => p.type == kind) + 1}.xml";
        var savedRels = _rels;
        _rels = [];
        var root = new XElement(W + (kind == "header" ? "hdr" : "ftr"), Namespaces());
        Blocks(part.Blocks, root, _doc.ContentWidth, topLevel: false);
        _parts.Add((name, kind, root, _rels));
        _rels = savedRels;
        return NewRel(kind, name, external: false);
    }

    static XAttribute[] Namespaces() =>
    [
        new(XNamespace.Xmlns + "w", W), new(XNamespace.Xmlns + "r", R), new(XNamespace.Xmlns + "wp", WP),
        new(XNamespace.Xmlns + "a", A), new(XNamespace.Xmlns + "pic", PIC), new(XNamespace.Xmlns + "wps", WPS),
        new(XNamespace.Xmlns + "mc", MC),
    ];

    /// <summary>A positioned picture or text box (Word's "anchor" drawing).</summary>
    XElement Anchor(DFloat f)
    {
        int id = _nextDrawing++;
        long cx = Units.ToEmu(f.Width), cy = Units.ToEmu(f.Height);
        XElement Position(string axis, FloatFrom from, FloatAlign align, double offset)
        {
            string rel = from switch
            {
                FloatFrom.Page => "page",
                FloatFrom.Margin => "margin",
                _ => axis == "H" ? "column" : "paragraph",
            };
            object value = align == FloatAlign.None
                ? new XElement(WP + "posOffset", Units.ToEmu(offset))
                : new XElement(WP + "align", (axis, align) switch
                {
                    ("H", FloatAlign.Start) => "left", ("H", FloatAlign.Center) => "center", ("H", _) => "right",
                    (_, FloatAlign.Start) => "top", (_, FloatAlign.Center) => "center", _ => "bottom",
                });
            return new XElement(WP + ("position" + axis), new XAttribute("relativeFrom", rel), value);
        }

        XElement graphic;
        if (f.Image != null)
        {
            var rid = AddMedia(f.Image, out var name);
            graphic = new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", PIC.NamespaceName),
                Picture(id, name, rid, cx, cy)));
        }
        else
        {
            var box = new XElement(W + "txbxContent");
            Blocks(f.Blocks, box, Math.Max(20, f.Width - 14.4), topLevel: false);
            var spPr = new XElement(WPS + "spPr",
                new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                    new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")),
                f.Fill is { } fill ? new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", Units.Hex(fill)))) : new XElement(A + "noFill"),
                new XElement(A + "ln", new XAttribute("w", 9525), f.Border is { } border
                    ? new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", Units.Hex(border))))
                    : new XElement(A + "noFill")));
            graphic = new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", WPS.NamespaceName),
                new XElement(WPS + "wsp", new XElement(WPS + "cNvSpPr", new XAttribute("txBox", 1)), spPr,
                    new XElement(WPS + "txbx", box),
                    new XElement(WPS + "bodyPr", new XAttribute("wrap", "square"), new XAttribute("lIns", 91440), new XAttribute("tIns", 45720),
                        new XAttribute("rIns", 91440), new XAttribute("bIns", 45720), new XElement(A + "noAutofit")))));
        }

        var anchor = new XElement(WP + "anchor",
            new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 114300), new XAttribute("distR", 114300),
            new XAttribute("simplePos", 0), new XAttribute("relativeHeight", 251658240 + id), new XAttribute("behindDoc", f.Wrap == FloatWrap.Behind ? 1 : 0),
            new XAttribute("locked", 0), new XAttribute("layoutInCell", 1), new XAttribute("allowOverlap", 1),
            new XElement(WP + "simplePos", new XAttribute("x", 0), new XAttribute("y", 0)),
            Position("H", f.HorizontalFrom, f.HorizontalAlign, f.X),
            Position("V", f.VerticalFrom, f.VerticalAlign, f.Y),
            new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
            new XElement(WP + "effectExtent", new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", 0), new XAttribute("b", 0)),
            f.Wrap switch
            {
                FloatWrap.TopAndBottom => new XElement(WP + "wrapTopAndBottom"),
                FloatWrap.Behind or FloatWrap.InFront => new XElement(WP + "wrapNone"),
                _ => new XElement(WP + "wrapSquare", new XAttribute("wrapText", "bothSides")),
            },
            new XElement(WP + "docPr", new XAttribute("id", id), new XAttribute("name", f.Image != null ? $"Picture {id}" : $"Text Box {id}"), Description(f.Image)),
            new XElement(WP + "cNvGraphicFramePr"),
            graphic);

        var drawing = new XElement(W + "drawing", anchor);
        // Text boxes use the Word 2010 shape schema, which older readers expect wrapped like this.
        return f.Image != null
            ? new XElement(W + "r", drawing)
            : new XElement(W + "r", new XElement(MC + "AlternateContent", new XElement(MC + "Choice", new XAttribute("Requires", "wps"), drawing)));
    }

    static IEnumerable<XElement> Runs(DText t, bool heading, bool link, bool rtl)
    {
        // Word needs to be told which text runs right to left (it sizes and orders it separately).
        var pieces = rtl || t.Text.Any(c => Bidi.IsRightToLeft(c)) ? ByDirection(t.Text, rtl) : [(t.Text, false)];
        foreach (var (text, backwards) in pieces)
            foreach (var run in Run(text, t.Style, heading, link, backwards)) yield return run;
    }

    /// <summary>Text split where its direction changes; spaces and punctuation go with what's before them.</summary>
    static IEnumerable<(string text, bool rtl)> ByDirection(string text, bool rtl)
    {
        int start = 0;
        bool current = rtl, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            bool strong = char.IsLetter(text[i]) || char.IsDigit(text[i]);
            if (!strong) continue;
            bool here = Bidi.IsRightToLeft(text[i]);
            if (any && here != current)
            {
                yield return (text[start..i], current);
                start = i;
            }
            (current, any) = (here, true);
        }
        if (start < text.Length) yield return (text[start..], current);
    }

    static IEnumerable<XElement> Run(string text, TextStyle style, bool heading, bool link, bool rtl)
    {
        var rPr = RunProps(style, link, heading, rtl);
        var parts = text.Split('\t');
        var run = new XElement(W + "r");
        if (rPr.HasElements) run.Add(rPr);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Add(new XElement(W + "tab"));
            var lines = parts[i].Split('\n');
            for (int k = 0; k < lines.Length; k++)
            {
                if (k > 0) run.Add(new XElement(W + "br"));
                if (lines[k].Length > 0) run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(lines[k])));
            }
        }
        yield return run;
    }

    /// <param name="heading">Source-styled heading text stays unbolded if it was, rather than taking the heading style's bold.</param>
    static XElement RunProps(TextStyle s, bool link, bool heading, bool rtl = false)
    {
        var rPr = new XElement(W + "rPr");
        if (link) rPr.Add(new XElement(W + "rStyle", new XAttribute(W + "val", "Hyperlink")));
        if (s.Font is { Length: > 0 })
        {
            var font = FontName(s.Font);
            rPr.Add(new XElement(W + "rFonts", new XAttribute(W + "ascii", font), new XAttribute(W + "hAnsi", font), new XAttribute(W + "cs", font)));
        }
        if (s.Bold) rPr.Add(new XElement(W + "b"), rtl ? new XElement(W + "bCs") : null);
        else if (heading && s.Size != null) rPr.Add(new XElement(W + "b", new XAttribute(W + "val", 0)));
        if (s.Italic) rPr.Add(new XElement(W + "i"), rtl ? new XElement(W + "iCs") : null);
        if (s.Strike) rPr.Add(new XElement(W + "strike"));
        if (s.Color is { } c) rPr.Add(new XElement(W + "color", new XAttribute(W + "val", Units.Hex(c))));
        if (s.Size is double size) rPr.Add(new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(size * 2))), rtl ? new XElement(W + "szCs", new XAttribute(W + "val", (int)Math.Round(size * 2))) : null);
        if (s.Underline) rPr.Add(new XElement(W + "u", new XAttribute(W + "val", "single")));
        if (s.Highlight is { } h)
            rPr.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", Units.Hex(h))));
        if (s.Superscript || s.Subscript) rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", s.Superscript ? "superscript" : "subscript")));
        if (rtl) rPr.Add(new XElement(W + "rtl"));
        return rPr;
    }

    /// <summary>Word gives up on font names longer than 31 characters.</summary>
    static string FontName(string name) => name.Length > 31 ? name[..31] : name;

    /// <summary>Removes characters that are illegal in XML.</summary>
    static string Clean(string s) => SafeXml.Text(s);

    XElement Image(DImage img, double available)
    {
        double scale = Math.Min(1, available / Math.Max(1, img.Width));
        long cx = Units.ToEmu(img.Width * scale), cy = Units.ToEmu(img.Height * scale);
        var rid = AddMedia(img, out var name);
        int id = _nextDrawing++;

        return new XElement(W + "r", new XElement(W + "drawing",
            new XElement(WP + "inline", new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(WP + "docPr", new XAttribute("id", id), new XAttribute("name", $"Picture {id}"), Description(img)),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData", new XAttribute("uri", PIC.NamespaceName), Picture(id, name, rid, cx, cy))))));
    }

    static XAttribute? Description(DImage? img) => img?.Alt is { Length: > 0 } alt ? new XAttribute("descr", alt) : null;

    readonly Dictionary<byte[], string> _mediaNames = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(List<XElement> part, string name), string> _mediaRels = [];

    /// <summary>Stores the picture in word/media and links it from the part being written.</summary>
    string AddMedia(DImage img, out string name)
    {
        // The type is in the bytes; the name may be out of date.
        var ext = HtmlReader.ImageExt(img.Data) ?? img.Ext;
        if (ext == "jpeg") ext = "jpg";
        if (!_mediaNames.TryGetValue(img.Data, out name!))
        {
            _mediaNames[img.Data] = name = $"image{_media.Count + 1}.{ext}";
            _media.Add((name, img.Data));
        }
        if (!_mediaRels.TryGetValue((_rels, name), out var id)) _mediaRels[(_rels, name)] = id = NewRel("image", "media/" + name, external: false);
        return id;
    }

    static XElement Picture(int id, string name, string rid, long cx, long cy) =>
        new(PIC + "pic",
            new XElement(PIC + "nvPicPr",
                new XElement(PIC + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(PIC + "cNvPicPr")),
            new XElement(PIC + "blipFill",
                new XElement(A + "blip", new XAttribute(R + "embed", rid)),
                new XElement(A + "stretch", new XElement(A + "fillRect"))),
            new XElement(PIC + "spPr",
                new XElement(A + "xfrm",
                    new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                    new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));

    XElement Table(DTable t, double available)
    {
        int columns = Math.Max(1, t.ColumnCount);
        var widths = t.ColumnWidths != null && t.ColumnWidths.Count >= columns
            ? t.ColumnWidths.Take(columns).ToList()
            : Enumerable.Repeat(available / columns, columns).ToList();
        double total = widths.Sum();
        if (total > available) widths = widths.Select(w => w * available / total).ToList();

        var tblPr = new XElement(W + "tblPr");
        if (t.Borders) tblPr.Add(new XElement(W + "tblStyle", new XAttribute(W + "val", "TableGrid")));
        tblPr.Add(new XElement(W + "tblW", new XAttribute(W + "w", 0), new XAttribute(W + "type", "auto")));
        tblPr.Add(new XElement(W + "tblCellMar",
            new XElement(W + "left", new XAttribute(W + "w", 108), new XAttribute(W + "type", "dxa")),
            new XElement(W + "right", new XAttribute(W + "w", 108), new XAttribute(W + "type", "dxa"))));
        tblPr.Add(new XElement(W + "tblLook", new XAttribute(W + "val", "04A0")));
        var table = new XElement(W + "tbl", tblPr,
            new XElement(W + "tblGrid", widths.Select(w => new XElement(W + "gridCol", new XAttribute(W + "w", Units.ToTwips(w))))));

        int rowsLeft = t.Rows.Count;
        foreach (var (row, slots) in t.Slots())
        {
            _ct.ThrowIfCancellationRequested();
            rowsLeft--;
            var tr = new XElement(W + "tr");
            if (row.Header) tr.Add(new XElement(W + "trPr", new XElement(W + "tblHeader")));
            int col = 0;
            foreach (var (cell, column, span, covered) in slots)
            {
                for (; col < column; col++) tr.Add(EmptyCell(widths[col]));
                double width = widths.Skip(col).Take(span).Sum();
                col += span;
                var tcPr = new XElement(W + "tcPr", new XElement(W + "tcW", new XAttribute(W + "w", Units.ToTwips(width)), new XAttribute(W + "type", "dxa")));
                if (span > 1) tcPr.Add(new XElement(W + "gridSpan", new XAttribute(W + "val", span)));
                if (covered || cell.RowSpan > 1 && rowsLeft > 0) tcPr.Add(new XElement(W + "vMerge", covered ? null : new XAttribute(W + "val", "restart")));
                if (cell.Shading is { } shade)
                    tcPr.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", Units.Hex(shade))));
                var tc = new XElement(W + "tc", tcPr);
                if (covered) tc.Add(new XElement(W + "p"));
                else Blocks(cell.Blocks, tc, Math.Max(20, width - 10.8), topLevel: false);
                tr.Add(tc);
            }
            // Word calls a row with no cells damage.
            if (slots.Count == 0) tr.Add(EmptyCell(widths.Sum(), columns));
            table.Add(tr);
        }
        return table;
    }

    static XElement EmptyCell(double width, int span = 1) => new(W + "tc",
        new XElement(W + "tcPr", new XElement(W + "tcW", new XAttribute(W + "w", Units.ToTwips(width)), new XAttribute(W + "type", "dxa")),
            span > 1 ? new XElement(W + "gridSpan", new XAttribute(W + "val", span)) : null),
        new XElement(W + "p"));

    // ------------------------------------------------------------------ package parts

    string NewRel(string type, string target, bool external)
    {
        var id = $"rId{_nextRel++}";
        var rel = Rel(id, type, target);
        if (external) rel.Add(new XAttribute("TargetMode", "External"));
        _rels.Add(rel);
        return id;
    }

    static XElement Rel(string id, string type, string target) =>
        new(PR + "Relationship", new XAttribute("Id", id), new XAttribute("Type", RelBase + type), new XAttribute("Target", target));

    XElement ContentTypes()
    {
        var types = new XElement(CT + "Types",
            Default("rels", "application/vnd.openxmlformats-package.relationships+xml"),
            Default("xml", "application/xml"),
            Override("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
            Override("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"),
            Override("/word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"),
            Override("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"));
        const string Ml = "application/vnd.openxmlformats-officedocument.wordprocessingml.";
        foreach (var part in _parts) types.Add(Override("/word/" + part.name, Ml + part.type + "+xml"));
        if (_footnotes.Count > 0) types.Add(Override("/word/footnotes.xml", Ml + "footnotes+xml"));
        if (_endnotes.Count > 0) types.Add(Override("/word/endnotes.xml", Ml + "endnotes+xml"));
        foreach (var ext in _media.Select(m => Path.GetExtension(m.name).TrimStart('.')).Distinct())
            types.Add(Default(ext, ext switch { "jpg" => "image/jpeg", "tiff" => "image/tiff", "emf" or "wmf" => $"image/x-{ext}", _ => $"image/{ext}" }));
        return types;

        static XElement Default(string ext, string type) => new(CT + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", type));
        static XElement Override(string part, string type) => new(CT + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type));
    }

    XElement Styles()
    {
        var font = FontName(_doc.Font);
        var styles = new XElement(W + "styles", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "docDefaults",
                new XElement(W + "rPrDefault", new XElement(W + "rPr",
                    new XElement(W + "rFonts", new XAttribute(W + "ascii", font), new XAttribute(W + "hAnsi", font), new XAttribute(W + "eastAsia", font), new XAttribute(W + "cs", font)),
                    new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(_doc.FontSize * 2))),
                    new XElement(W + "szCs", new XAttribute(W + "val", (int)Math.Round(_doc.FontSize * 2))),
                    new XElement(W + "lang", new XAttribute(W + "val", CultureInfo.CurrentCulture.Name is { Length: > 0 } n ? n : "en-US")))),
                new XElement(W + "pPrDefault", new XElement(W + "pPr",
                    new XElement(W + "spacing", new XAttribute(W + "after", 0), new XAttribute(W + "line", 240), new XAttribute(W + "lineRule", "auto"))))),
            new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "default", 1), new XAttribute(W + "styleId", "Normal"),
                new XElement(W + "name", new XAttribute(W + "val", "Normal")), new XElement(W + "qFormat")),
            CharacterStyle("FootnoteReference", "footnote reference", new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript"))),
            CharacterStyle("EndnoteReference", "endnote reference", new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript"))),
            new XElement(W + "style", new XAttribute(W + "type", "character"), new XAttribute(W + "styleId", "Hyperlink"),
                new XElement(W + "name", new XAttribute(W + "val", "Hyperlink")),
                new XElement(W + "rPr", new XElement(W + "color", new XAttribute(W + "val", "0563C1")), new XElement(W + "u", new XAttribute(W + "val", "single")))),
            new XElement(W + "style", new XAttribute(W + "type", "table"), new XAttribute(W + "styleId", "TableGrid"),
                new XElement(W + "name", new XAttribute(W + "val", "Table Grid")),
                new XElement(W + "tblPr", new XElement(W + "tblBorders",
                    new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(side =>
                        new XElement(W + side, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 4), new XAttribute(W + "space", 0), new XAttribute(W + "color", "auto")))))));

        double[] sizes = [0, 20, 16, 14, 13, 12, 11];
        for (int level = 1; level <= 6; level++)
            styles.Add(new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", $"Heading{level}"),
                new XElement(W + "name", new XAttribute(W + "val", $"heading {level}")),
                new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
                new XElement(W + "next", new XAttribute(W + "val", "Normal")),
                new XElement(W + "qFormat"),
                new XElement(W + "pPr", new XElement(W + "keepNext"), new XElement(W + "outlineLvl", new XAttribute(W + "val", level - 1))),
                new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", (int)(sizes[level] * 2))))));
        return styles;
    }

    static XElement CharacterStyle(string id, string name, params XElement[] runProps) =>
        new(W + "style", new XAttribute(W + "type", "character"), new XAttribute(W + "styleId", id),
            new XElement(W + "name", new XAttribute(W + "val", name)), new XElement(W + "rPr", runProps));

    /// <summary>
    /// Each list has a definition of its own: Word carries numbers on from one list to the next when they share one.
    /// </summary>
    XElement Numbering()
    {
        string[] bullets = ["•", "◦", "▪"];
        var numbering = new XElement(W + "numbering", new XAttribute(XNamespace.Xmlns + "w", W));
        foreach (var list in _lists.Definitions)
            numbering.Add(new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", list.Id),
                new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")),
                Enumerable.Range(0, 9).Select(i =>
                {
                    var look = list.Levels[i] ?? new ListPlanner.LevelLook(ListKind.Bullet, "bullet", bullets[i % 3], 1);
                    return new XElement(W + "lvl", new XAttribute(W + "ilvl", i),
                        new XElement(W + "start", new XAttribute(W + "val", Math.Max(0, look.Start))),
                        new XElement(W + "numFmt", new XAttribute(W + "val", look.Format)),
                        new XElement(W + "lvlText", new XAttribute(W + "val", look.Text)),
                        new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
                        new XElement(W + "pPr", new XElement(W + "ind", new XAttribute(W + "left", 720 * (i + 1)), new XAttribute(W + "hanging", 360))));
                })));
        foreach (var list in _lists.Definitions)
            numbering.Add(new XElement(W + "num", new XAttribute(W + "numId", list.Id), new XElement(W + "abstractNumId", new XAttribute(W + "val", list.Id))));
        return numbering;
    }

    static void Put(ZipArchive zip, string name, XElement root)
    {
        SafeXml.Clean(root);
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(stream);
    }
}
