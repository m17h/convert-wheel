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

    readonly DDocument _doc;
    readonly List<XElement> _rels = [];
    readonly List<(string name, byte[] data)> _media = [];
    readonly List<XElement> _nums = [];
    int _nextRel = 10, _nextDrawing = 1;
    int? _currentNumId;
    ListKind _currentKind;

    DocxWriter(DDocument doc) => _doc = doc;

    public static void Write(DDocument doc, string path) => new DocxWriter(doc).Save(path);

    void Save(string path)
    {
        var body = new XElement(W + "body");
        Blocks(_doc.Blocks, body, _doc.ContentWidth, topLevel: true);
        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", Units.ToTwips(_doc.PageWidth)), new XAttribute(W + "h", Units.ToTwips(_doc.PageHeight)),
                _doc.PageWidth > _doc.PageHeight ? new XAttribute(W + "orient", "landscape") : null),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", Units.ToTwips(_doc.Margins.Top)), new XAttribute(W + "right", Units.ToTwips(_doc.Margins.Right)),
                new XAttribute(W + "bottom", Units.ToTwips(_doc.Margins.Bottom)), new XAttribute(W + "left", Units.ToTwips(_doc.Margins.Left)),
                new XAttribute(W + "header", 708), new XAttribute(W + "footer", 708), new XAttribute(W + "gutter", 0))));

        var document = new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R),
            new XAttribute(XNamespace.Xmlns + "wp", WP), new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "pic", PIC),
            body);

        _rels.Add(Rel("rId1", "styles", "styles.xml"));
        _rels.Add(Rel("rId2", "numbering", "numbering.xml"));
        _rels.Add(Rel("rId3", "settings", "settings.xml"));

        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        Put(zip, "[Content_Types].xml", ContentTypes());
        Put(zip, "_rels/.rels", new XElement(PR + "Relationships",
            new XElement(PR + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", RelBase + "officeDocument"), new XAttribute("Target", "word/document.xml"))));
        Put(zip, "word/document.xml", document);
        Put(zip, "word/_rels/document.xml.rels", new XElement(PR + "Relationships", _rels));
        Put(zip, "word/styles.xml", Styles());
        Put(zip, "word/numbering.xml", Numbering());
        Put(zip, "word/settings.xml", new XElement(W + "settings", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "defaultTabStop", new XAttribute(W + "val", 720))));
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
            if (blocks[i] is DParagraph p) parent.Add(Paragraph(p, available));
            else if (blocks[i] is DTable t)
            {
                _currentNumId = null;
                parent.Add(Table(t, available));
                // Word merges back-to-back tables, and a cell must end with a paragraph.
                bool last = i == blocks.Count - 1;
                if ((!topLevel && last) || (!last && blocks[i + 1] is DTable)) parent.Add(new XElement(W + "p"));
            }
        }
        if (!topLevel && !parent.Elements(W + "p").Any()) parent.Add(new XElement(W + "p"));
    }

    XElement Paragraph(DParagraph p, double available)
    {
        var pPr = new XElement(W + "pPr");
        if (p.Heading > 0) pPr.Add(new XElement(W + "pStyle", new XAttribute(W + "val", $"Heading{p.Heading}")));
        if (p.PageBreakBefore) pPr.Add(new XElement(W + "pageBreakBefore"));

        if (p.List != ListKind.None)
        {
            if (_currentNumId == null || _currentKind != p.List || (p.ListLevel == 0 && StartsNewList(p)))
            {
                _currentNumId = _nums.Count + 1;
                _currentKind = p.List;
                var num = new XElement(W + "num", new XAttribute(W + "numId", _currentNumId),
                    new XElement(W + "abstractNumId", new XAttribute(W + "val", p.List == ListKind.Bullet ? 0 : 1)));
                if (p.List == ListKind.Number && LeadingNumber(p.ListLabel) is int start && start != 1)
                    num.Add(new XElement(W + "lvlOverride", new XAttribute(W + "ilvl", 0),
                        new XElement(W + "startOverride", new XAttribute(W + "val", start))));
                _nums.Add(num);
            }
            pPr.Add(new XElement(W + "numPr",
                new XElement(W + "ilvl", new XAttribute(W + "val", Math.Clamp(p.ListLevel, 0, 8))),
                new XElement(W + "numId", new XAttribute(W + "val", _currentNumId))));
        }
        else
        {
            _currentNumId = null;
        }

        var spacing = new XElement(W + "spacing",
            new XAttribute(W + "before", Units.ToTwips(p.SpaceBefore)), new XAttribute(W + "after", Units.ToTwips(p.SpaceAfter)),
            new XAttribute(W + "line", (int)Math.Round(p.LineSpacing * 240)), new XAttribute(W + "lineRule", "auto"));
        pPr.Add(spacing);

        if (p.List == ListKind.None && (p.IndentLeft != 0 || p.IndentFirst != 0))
        {
            var ind = new XElement(W + "ind", new XAttribute(W + "left", Units.ToTwips(p.IndentLeft)));
            if (p.IndentFirst > 0) ind.Add(new XAttribute(W + "firstLine", Units.ToTwips(p.IndentFirst)));
            else if (p.IndentFirst < 0) ind.Add(new XAttribute(W + "hanging", Units.ToTwips(-p.IndentFirst)));
            pPr.Add(ind);
        }
        if (p.Align != TextAlignment.Left)
            pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", p.Align switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "right",
                _ => "both",
            })));
        if (p.Inlines.Count == 0 && p.MarkSize is double mark && Math.Abs(mark - _doc.FontSize) > 0.1)
            pPr.Add(new XElement(W + "rPr", new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(mark * 2)))));

        var para = new XElement(W + "p", pPr);
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t:
                    var runs = Runs(t, p.Heading > 0);
                    if (t.Link != null && Uri.IsWellFormedUriString(t.Link, UriKind.Absolute))
                    {
                        var id = NewRel("hyperlink", t.Link, external: true);
                        para.Add(new XElement(W + "hyperlink", new XAttribute(R + "id", id), runs));
                    }
                    else para.Add(runs);
                    break;
                case DLineBreak:
                    para.Add(new XElement(W + "r", new XElement(W + "br")));
                    break;
                case DImage img:
                    para.Add(Image(img, available));
                    break;
            }
        }
        return para;
    }

    bool StartsNewList(DParagraph p) => p.List == ListKind.Number && LeadingNumber(p.ListLabel) == 1;

    static int? LeadingNumber(string? label)
    {
        if (label == null) return null;
        var digits = new string(label.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    IEnumerable<XElement> Runs(DText t, bool heading)
    {
        var rPr = RunProps(t.Style, t.Link != null, heading);
        var parts = t.Text.Split('\t');
        var run = new XElement(W + "r");
        if (rPr.HasElements) run.Add(rPr);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Add(new XElement(W + "tab"));
            if (parts[i].Length > 0) run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(parts[i])));
        }
        yield return run;
    }

    /// <param name="heading">Source-styled heading text stays unbolded if it was, rather than taking the heading style's bold.</param>
    static XElement RunProps(TextStyle s, bool link, bool heading)
    {
        var rPr = new XElement(W + "rPr");
        if (link) rPr.Add(new XElement(W + "rStyle", new XAttribute(W + "val", "Hyperlink")));
        if (s.Font != null)
            rPr.Add(new XElement(W + "rFonts", new XAttribute(W + "ascii", s.Font), new XAttribute(W + "hAnsi", s.Font), new XAttribute(W + "cs", s.Font)));
        if (s.Bold) rPr.Add(new XElement(W + "b"));
        else if (heading && s.Size != null) rPr.Add(new XElement(W + "b", new XAttribute(W + "val", 0)));
        if (s.Italic) rPr.Add(new XElement(W + "i"));
        if (s.Strike) rPr.Add(new XElement(W + "strike"));
        if (s.Color is { } c) rPr.Add(new XElement(W + "color", new XAttribute(W + "val", Units.Hex(c))));
        if (s.Size is double size) rPr.Add(new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(size * 2))));
        if (s.Highlight is { } h)
            rPr.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", Units.Hex(h))));
        if (s.Underline) rPr.Add(new XElement(W + "u", new XAttribute(W + "val", "single")));
        if (s.Superscript || s.Subscript) rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", s.Superscript ? "superscript" : "subscript")));
        return rPr;
    }

    /// <summary>Removes characters that are illegal in XML.</summary>
    static string Clean(string s) => s.Any(c => c < 0x20 && c is not ('\t' or '\n' or '\r'))
        ? new string(s.Where(c => c >= 0x20 || c is '\t' or '\n' or '\r').ToArray())
        : s;

    XElement Image(DImage img, double available)
    {
        double scale = Math.Min(1, available / Math.Max(1, img.Width));
        long cx = Units.ToEmu(img.Width * scale), cy = Units.ToEmu(img.Height * scale);
        var ext = img.Ext == "jpeg" ? "jpg" : img.Ext;
        var name = $"image{_media.Count + 1}.{ext}";
        _media.Add((name, img.Data));
        var rid = NewRel("image", "media/" + name, external: false);
        int id = _nextDrawing++;

        return new XElement(W + "r", new XElement(W + "drawing",
            new XElement(WP + "inline", new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(WP + "docPr", new XAttribute("id", id), new XAttribute("name", $"Picture {id}")),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData", new XAttribute("uri", PIC.NamespaceName),
                        new XElement(PIC + "pic",
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
                                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))))));
    }

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

        foreach (var row in t.Rows)
        {
            var tr = new XElement(W + "tr");
            if (row.Header) tr.Add(new XElement(W + "trPr", new XElement(W + "tblHeader")));
            int col = 0;
            foreach (var cell in row.Cells)
            {
                int span = Math.Max(1, cell.ColSpan);
                double width = widths.Skip(col).Take(span).Sum();
                col += span;
                var tcPr = new XElement(W + "tcPr", new XElement(W + "tcW", new XAttribute(W + "w", Units.ToTwips(width)), new XAttribute(W + "type", "dxa")));
                if (span > 1) tcPr.Add(new XElement(W + "gridSpan", new XAttribute(W + "val", span)));
                if (cell.Shading is { } shade)
                    tcPr.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", Units.Hex(shade))));
                var tc = new XElement(W + "tc", tcPr);
                Blocks(cell.Blocks, tc, Math.Max(20, width - 10.8), topLevel: false);
                tr.Add(tc);
            }
            table.Add(tr);
        }
        return table;
    }

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
        foreach (var ext in _media.Select(m => Path.GetExtension(m.name).TrimStart('.')).Distinct())
            types.Add(Default(ext, ext switch { "jpg" => "image/jpeg", "tiff" => "image/tiff", _ => $"image/{ext}" }));
        return types;

        static XElement Default(string ext, string type) => new(CT + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", type));
        static XElement Override(string part, string type) => new(CT + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type));
    }

    XElement Styles()
    {
        var font = _doc.Font;
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

    XElement Numbering()
    {
        string[] bullets = ["•", "◦", "▪"];
        string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
        var numbering = new XElement(W + "numbering", new XAttribute(XNamespace.Xmlns + "w", W),
            Abstract(0, level => ("bullet", bullets[level % 3])),
            Abstract(1, level => (formats[level % 3], $"%{level + 1}.")));
        numbering.Add(_nums);
        return numbering;

        static XElement Abstract(int id, Func<int, (string format, string text)> level) =>
            new(W + "abstractNum", new XAttribute(W + "abstractNumId", id),
                new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")),
                Enumerable.Range(0, 9).Select(i =>
                {
                    var (format, text) = level(i);
                    return new XElement(W + "lvl", new XAttribute(W + "ilvl", i),
                        new XElement(W + "start", new XAttribute(W + "val", 1)),
                        new XElement(W + "numFmt", new XAttribute(W + "val", format)),
                        new XElement(W + "lvlText", new XAttribute(W + "val", text)),
                        new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
                        new XElement(W + "pPr", new XElement(W + "ind", new XAttribute(W + "left", 720 * (i + 1)), new XAttribute(W + "hanging", 360))));
                }));
    }

    static void Put(ZipArchive zip, string name, XElement root)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(stream);
    }
}
