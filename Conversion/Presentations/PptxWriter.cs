using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Writes a deck as a PowerPoint (.pptx) file: one blank master and layout, and every shape, picture,
/// table and text run with its formatting spelled out on the slide.
/// </summary>
internal sealed class PptxWriter
{
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    const string Ml = "application/vnd.openxmlformats-officedocument.presentationml.";

    readonly ZipArchive _zip;
    readonly Deck _deck;
    readonly CancellationToken _ct;
    readonly Dictionary<DImage, string> _media = [];
    readonly HashSet<string> _mediaTypes = [];
    List<XElement> _rels = [];
    SlideNumbering _numbering = new();
    int _shapeId;

    PptxWriter(ZipArchive zip, Deck deck, CancellationToken ct) => (_zip, _deck, _ct) = (zip, deck, ct);

    public static void Write(Deck deck, string path, CancellationToken ct = default)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        new PptxWriter(zip, deck, ct).WriteAll();
    }

    void WriteAll()
    {
        int count = _deck.Slides.Count;
        for (int i = 0; i < count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            WriteSlide(i);
        }

        var presRels = new List<XElement> { Rel("rId1", "slideMaster", "slideMasters/slideMaster1.xml") };
        for (int i = 0; i < count; i++) presRels.Add(Rel($"rId{i + 2}", "slide", $"slides/slide{i + 1}.xml"));
        presRels.Add(Rel($"rId{count + 2}", "theme", "theme/theme1.xml"));
        presRels.Add(Rel($"rId{count + 3}", "presProps", "presProps.xml"));
        presRels.Add(Rel($"rId{count + 4}", "tableStyles", "tableStyles.xml"));
        bool notes = _deck.Slides.Any(s => s.Notes.Count > 0);
        if (notes) presRels.Add(Rel("rIdNotesMaster", "notesMaster", "notesMasters/notesMaster1.xml"));
        Save("ppt/_rels/presentation.xml.rels", Rels(presRels));

        Save("ppt/presentation.xml", new XElement(P + "presentation", Ns(), new XAttribute("saveSubsetFonts", 1),
            new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", 2147483648), new XAttribute(R + "id", "rId1"))),
            notes ? new XElement(P + "notesMasterIdLst", new XElement(P + "notesMasterId", new XAttribute(R + "id", "rIdNotesMaster"))) : null,
            new XElement(P + "sldIdLst", Enumerable.Range(0, count).Select(i =>
                new XElement(P + "sldId", new XAttribute("id", 256 + i), new XAttribute(R + "id", $"rId{i + 2}")))),
            // PowerPoint's limits: 1 to 56 inches.
            new XElement(P + "sldSz", new XAttribute("cx", Math.Clamp(Emu(_deck.Width), 914400, 51206400)), new XAttribute("cy", Math.Clamp(Emu(_deck.Height), 914400, 51206400))),
            new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000))));

        WriteMasterAndLayout();
        Save("ppt/theme/theme1.xml", Theme());
        if (notes) WriteNotesMaster();
        Save("ppt/presProps.xml", new XElement(P + "presentationPr", Ns()));
        Save("ppt/tableStyles.xml", new XElement(A + "tblStyleLst", new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute("def", "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}")));

        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        Save("docProps/core.xml", new XElement(cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", cp), new XAttribute(XNamespace.Xmlns + "dc", dc),
            new XElement(dc + "title", _deck.Title ?? "")));
        XNamespace ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        Save("docProps/app.xml", new XElement(ep + "Properties", new XElement(ep + "Application", "Convert Wheel"),
            new XElement(ep + "Slides", count)));

        Save("_rels/.rels", Rels([
            Rel("rId1", "officeDocument", "ppt/presentation.xml"),
            new XElement(PR + "Relationship", new XAttribute("Id", "rId2"), new XAttribute("Target", "docProps/core.xml"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties")),
            Rel("rId3", "extended-properties", "docProps/app.xml"),
        ]));
        Save("[Content_Types].xml", ContentTypes(count));
    }

    // ------------------------------------------------------------------ slides

    void WriteSlide(int index)
    {
        var slide = _deck.Slides[index];
        _rels = [Rel("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml")];
        _shapeId = 1;

        var tree = new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", _shapeId++), new XAttribute("name", "")),
                new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", Xfrm(new Rect(), 0, false, false, group: true)));
        foreach (var shape in slide.Shapes)
        {
            _ct.ThrowIfCancellationRequested();
            try { tree.Add(Shape(shape)); }
            catch (Exception ex) when (SlideLimits.CanSkip(ex))
            {
                Log.Write($"Skipped writing a shape: {ex.Message}");
            }
        }

        var cSld = new XElement(P + "cSld");
        if (slide.Background is { } bg && Fill(bg) is { } bgFill)
            cSld.Add(new XElement(P + "bg", new XElement(P + "bgPr", bgFill, new XElement(A + "effectLst"))));
        cSld.Add(tree);
        Save($"ppt/slides/slide{index + 1}.xml", new XElement(P + "sld", Ns(), slide.Hidden ? new XAttribute("show", 0) : null, cSld,
            new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
        if (slide.Notes.Count > 0)
        {
            _rels.Add(Rel($"rId{_rels.Count + 1}", "notesSlide", $"../notesSlides/notesSlide{index + 1}.xml"));
            WriteNotes(slide, index);
        }
        Save($"ppt/slides/_rels/slide{index + 1}.xml.rels", Rels(_rels));
    }

    XElement Shape(SlideShape shape)
    {
        int id = _shapeId++;
        if (shape.Picture != null) return Picture(shape, id);
        if (shape.Text.Count == 1 && shape.Text[0] is DTable table) return Table(shape, table, id);

        var name = shape.Role == "title" ? $"Title {id}" : shape.HasText ? $"TextBox {id}" : $"Shape {id}";
        var spPr = new XElement(P + "spPr", Xfrm(shape.Bounds, shape.Rotation, shape.FlipH, shape.FlipV), Geometry(shape));
        spPr.Add(shape.Fill != null && !IsConnector(shape) ? Fill(shape.Fill) ?? new XElement(A + "noFill") : new XElement(A + "noFill"));
        spPr.Add(Line(shape));

        var bodyPr = new XElement(A + "bodyPr",
            new XAttribute("wrap", shape.Wrap ? "square" : "none"),
            new XAttribute("lIns", Emu(shape.Insets.Left)), new XAttribute("tIns", Emu(shape.Insets.Top)),
            new XAttribute("rIns", Emu(shape.Insets.Right)), new XAttribute("bIns", Emu(shape.Insets.Bottom)),
            new XAttribute("rtlCol", 0),
            new XAttribute("anchor", shape.Anchor switch { TextAnchor.Middle => "ctr", TextAnchor.Bottom => "b", _ => "t" }));
        if (shape.TextRotation == 180) bodyPr.Add(new XAttribute("rot", 10800000));
        else if (shape.TextRotation != 0) bodyPr.Add(new XAttribute("vert", shape.TextRotation == 90 ? "vert" : "vert270"));
        bodyPr.Add(shape.ShrinkToFit ? new XElement(A + "normAutofit") : new XElement(A + "noAutofit"));

        _numbering = new SlideNumbering();
        var txBody = new XElement(P + "txBody", bodyPr, new XElement(A + "lstStyle"));
        foreach (var p in shape.Text.OfType<DParagraph>()) txBody.Add(Paragraph(p));
        if (!txBody.Elements(A + "p").Any()) txBody.Add(new XElement(A + "p", new XElement(A + "endParaRPr", new XAttribute("lang", "en-US"))));

        return new XElement(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr", shape.HasText && shape.Fill == null && shape.Line == null ? new XAttribute("txBox", 1) : null),
                new XElement(P + "nvPr")),
            spPr, txBody);
    }

    XElement Picture(SlideShape shape, int id)
    {
        var rid = Media(shape.Picture!);
        var blipFill = new XElement(P + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", rid),
            shape.PictureOpacity < 0.999 ? new XElement(A + "alphaModFix", new XAttribute("amt", Pct(shape.PictureOpacity))) : null));
        if (shape.Crop != default) blipFill.Add(SourceRect(shape.Crop));
        blipFill.Add(new XElement(A + "stretch", new XElement(A + "fillRect")));
        return new XElement(P + "pic",
            new XElement(P + "nvPicPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Picture {id}")),
                new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))),
                new XElement(P + "nvPr")),
            blipFill,
            new XElement(P + "spPr", Xfrm(shape.Bounds, shape.Rotation, shape.FlipH, shape.FlipV), Geometry(shape), Line(shape)));
    }

    static XElement SourceRect(Thickness crop) => new(A + "srcRect",
        new XAttribute("l", Pct(crop.Left)), new XAttribute("t", Pct(crop.Top)), new XAttribute("r", Pct(crop.Right)), new XAttribute("b", Pct(crop.Bottom)));

    XElement Table(SlideShape shape, DTable table, int id)
    {
        var grid = new TableGrid(table);
        var widths = grid.Widths(shape.Bounds.Width);
        double known = table.Rows.Sum(r => r.Height ?? 0);
        int unknown = table.Rows.Count(r => r.Height is not > 0);
        double share = unknown > 0 ? Math.Max(0, shape.Bounds.Height - known) / unknown : 0;

        var tbl = new XElement(A + "tbl",
            new XElement(A + "tblPr", new XAttribute("firstRow", table.Rows.FirstOrDefault()?.Header == true ? 1 : 0)),
            new XElement(A + "tblGrid", widths.Select(cw => new XElement(A + "gridCol", new XAttribute("w", Emu(cw))))));
        var plain = table.Borders ? new CellEdge(1, table.BorderColor ?? Color.FromRgb(0x80, 0x80, 0x80)) : new CellEdge(1, null);
        for (int r = 0; r < grid.Rows; r++)
        {
            var row = table.Rows[r];
            var tr = new XElement(A + "tr", new XAttribute("h", Emu(row.Height is > 0 and var h ? h : share)));
            for (int c = 0; c < grid.Columns; c++)
            {
                var place = grid.At(r, c);
                // Merged cells are all written; the ones a merge covers say so, and every corner says how far it reaches.
                bool top = place == null || place.Row == r, left = place == null || place.Column == c;
                var txBody = new XElement(A + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"));
                if (place != null && top && left)
                {
                    _numbering = new SlideNumbering();
                    foreach (var p in place.Cell.Blocks.OfType<DParagraph>()) txBody.Add(Paragraph(p, row.Header));
                }
                if (!txBody.Elements(A + "p").Any()) txBody.Add(new XElement(A + "p"));
                var look = place?.Cell.Look;
                var tcPr = new XElement(A + "tcPr");
                if ((look?.Padding ?? table.CellPadding) is { } pad)
                    tcPr.Add(new XAttribute("marL", Emu(pad.Left)), new XAttribute("marR", Emu(pad.Right)), new XAttribute("marT", Emu(pad.Top)), new XAttribute("marB", Emu(pad.Bottom)));
                if (look is { Anchor: not TextAnchor.Top }) tcPr.Add(new XAttribute("anchor", look.Anchor == TextAnchor.Middle ? "ctr" : "b"));
                foreach (var (name, side) in new[] { ("lnL", TableGrid.Side.Left), ("lnR", TableGrid.Side.Right), ("lnT", TableGrid.Side.Top), ("lnB", TableGrid.Side.Bottom) })
                {
                    if (grid.Edge(r, c, side, plain) is not { } line) continue;
                    tcPr.Add(new XElement(A + name, new XAttribute("w", Emu(line.Color != null && line.Width > 0 ? line.Width : 1)), line.Color is { } color ? Solid(color) : new XElement(A + "noFill")));
                }
                tcPr.Add(place?.Cell.Shading is { } shade ? Solid(shade) : new XElement(A + "noFill"));
                tr.Add(new XElement(A + "tc",
                    place is { RowSpan: > 1 } && top ? new XAttribute("rowSpan", place.RowSpan) : null,
                    place is { ColumnSpan: > 1 } && left ? new XAttribute("gridSpan", place.ColumnSpan) : null,
                    left ? null : new XAttribute("hMerge", 1),
                    top ? null : new XAttribute("vMerge", 1),
                    txBody, tcPr));
            }
            tbl.Add(tr);
        }

        return new XElement(P + "graphicFrame",
            new XElement(P + "nvGraphicFramePr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Table {id}")),
                new XElement(P + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noGrp", 1))),
                new XElement(P + "nvPr")),
            new XElement(P + "xfrm",
                new XElement(A + "off", new XAttribute("x", Emu(shape.Bounds.X)), new XAttribute("y", Emu(shape.Bounds.Y))),
                new XElement(A + "ext", new XAttribute("cx", Emu(shape.Bounds.Width)), new XAttribute("cy", Emu(shape.Bounds.Height)))),
            new XElement(A + "graphic", new XElement(A + "graphicData",
                new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/table"), tbl)));
    }

    // ------------------------------------------------------------------ text

    XElement Paragraph(DParagraph p, bool bold = false)
    {
        var pPr = new XElement(A + "pPr");
        if (p.Align != TextAlignment.Left)
            pPr.Add(new XAttribute("algn", p.Align switch { TextAlignment.Center => "ctr", TextAlignment.Right => "r", _ => "just" }));
        if (p.IndentLeft != 0) pPr.Add(new XAttribute("marL", Math.Clamp(Emu(p.IndentLeft), 0, 51206400)));
        if (p.IndentFirst != 0) pPr.Add(new XAttribute("indent", Math.Clamp(Emu(p.IndentFirst), -51206400, 51206400)));
        if (p.ListLevel > 0) pPr.Add(new XAttribute("lvl", Math.Min(8, p.ListLevel)));
        if (p.RightToLeft) pPr.Add(new XAttribute("rtl", 1));
        var (scheme, around, _, start) = _numbering.Next(p);
        if (Math.Abs(p.LineSpacing - 1) > 0.01)
            pPr.Add(new XElement(A + "lnSpc", new XElement(A + "spcPct", new XAttribute("val", Math.Clamp((int)Math.Round(p.LineSpacing * 100000), 0, 13200000)))));
        pPr.Add(new XElement(A + "spcBef", new XElement(A + "spcPts", new XAttribute("val", (int)(p.SpaceBefore * 100)))));
        pPr.Add(new XElement(A + "spcAft", new XElement(A + "spcPts", new XAttribute("val", (int)(p.SpaceAfter * 100)))));
        if (p.List == ListKind.None || string.IsNullOrEmpty(p.ListLabel))
        {
            pPr.Add(new XElement(A + "buNone"));
        }
        else
        {
            if (p.ListColor is { } bc) pPr.Add(new XElement(A + "buClr", Rgb(bc)));
            if (p.ListSize is { } bs) pPr.Add(new XElement(A + "buSzPct", new XAttribute("val", Math.Clamp((int)(bs * 100000), 25000, 400000))));
            if (p.List == ListKind.Number)
            {
                var type = scheme switch { "a" => "alphaLc", "A" => "alphaUc", "i" => "romanLc", "I" => "romanUc", _ => "arabic" }
                    + around switch { "(x)" => "ParenBoth", "x)" => "ParenR", "x" when scheme == "1" => "Plain", _ => "Period" };
                pPr.Add(new XElement(A + "buFont", new XAttribute("typeface", "+mj-lt")));
                pPr.Add(new XElement(A + "buAutoNum", new XAttribute("type", type), start > 1 ? new XAttribute("startAt", start) : null));
            }
            else
            {
                pPr.Add(new XElement(A + "buFont", new XAttribute("typeface", p.ListFont ?? "Arial")));
                pPr.Add(new XElement(A + "buChar", new XAttribute("char", p.ListLabel)));
            }
        }

        var para = new XElement(A + "p", pPr);
        TextStyle? last = null;
        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t when t.Text.Length > 0:
                    // Tabs and line feeds inside text become their DrawingML forms.
                    var pieces = t.Text.Replace("\r", "").Split('\n');
                    for (int i = 0; i < pieces.Length; i++)
                    {
                        if (i > 0) para.Add(new XElement(A + "br", RunProps(t.Style, null, bold, "rPr")));
                        if (pieces[i].Length > 0)
                            para.Add(new XElement(A + "r", RunProps(t.Style, t.Link, bold, "rPr"), new XElement(A + "t", Clean(pieces[i]))));
                    }
                    last = t.Style;
                    break;
                case DLineBreak:
                    para.Add(new XElement(A + "br", RunProps(last ?? new TextStyle { Size = p.MarkSize }, null, bold, "rPr")));
                    break;
                case DField f:
                    para.Add(new XElement(A + "fld", new XAttribute("id", "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}"),
                        new XAttribute("type", "slidenum"), RunProps(f.Style, null, bold, "rPr"), new XElement(A + "t", "‹#›")));
                    break;
            }
        }
        para.Add(RunProps(last ?? new TextStyle { Size = p.MarkSize ?? 18 }, null, bold, "endParaRPr"));
        return para;
    }

    XElement RunProps(TextStyle s, string? link, bool bold, string name)
    {
        var rPr = new XElement(A + name, new XAttribute("lang", "en-US"));
        if (s.Size is { } size) rPr.Add(new XAttribute("sz", Math.Clamp((int)Math.Round(size * 100), 100, 400000)));
        if (s.Bold || bold) rPr.Add(new XAttribute("b", 1));
        if (s.Italic) rPr.Add(new XAttribute("i", 1));
        if (s.Underline) rPr.Add(new XAttribute("u", "sng"));
        if (s.Strike) rPr.Add(new XAttribute("strike", "sngStrike"));
        if (s.Superscript) rPr.Add(new XAttribute("baseline", 30000));
        else if (s.Subscript) rPr.Add(new XAttribute("baseline", -25000));
        rPr.Add(new XAttribute("dirty", 0));
        if (s.Color is { } c) rPr.Add(Solid(c));
        if (s.Highlight is { } h) rPr.Add(new XElement(A + "highlight", Rgb(h)));
        if (s.Font is { Length: > 0 } font) rPr.Add(new XElement(A + "latin", new XAttribute("typeface", font)));
        if (name == "rPr" && SlideLimits.Link(link) is { } address)
        {
            var rid = $"rId{_rels.Count + 1}";
            _rels.Add(new XElement(PR + "Relationship", new XAttribute("Id", rid), new XAttribute("Type", RelBase + "hyperlink"),
                new XAttribute("Target", address), new XAttribute("TargetMode", "External")));
            rPr.Add(new XElement(A + "hlinkClick", new XAttribute(R + "id", rid)));
        }
        return rPr;
    }

    /// <summary>Characters XML can't hold (control codes) are dropped.</summary>
    static string Clean(string text) => SafeXml.Text(text);

    // ------------------------------------------------------------------ shapes, fills and lines

    static XElement Xfrm(Rect r, double rotation, bool flipH, bool flipV, bool group = false)
    {
        var xfrm = new XElement(A + "xfrm");
        if (Math.Abs(rotation) > 0.01) xfrm.Add(new XAttribute("rot", (long)Math.Round((rotation % 360 + 360) % 360 * 60000)));
        if (flipH) xfrm.Add(new XAttribute("flipH", 1));
        if (flipV) xfrm.Add(new XAttribute("flipV", 1));
        xfrm.Add(new XElement(A + "off", new XAttribute("x", Emu(r.X)), new XAttribute("y", Emu(r.Y))),
            new XElement(A + "ext", new XAttribute("cx", Emu(r.Width)), new XAttribute("cy", Emu(r.Height))));
        if (group)
            xfrm.Add(new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)),
                new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)));
        return xfrm;
    }

    static bool IsConnector(SlideShape s) => s.Geometry is ShapeGeometry.Line or ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve;

    static XElement Geometry(SlideShape shape)
    {
        if (shape.Geometry == ShapeGeometry.Custom && shape.Path != null) return CustomGeometry(shape);
        var prst = shape.Geometry switch
        {
            ShapeGeometry.RoundRect => "roundRect",
            ShapeGeometry.Ellipse => "ellipse",
            ShapeGeometry.Triangle => "triangle",
            ShapeGeometry.RightTriangle => "rtTriangle",
            ShapeGeometry.Diamond => "diamond",
            ShapeGeometry.Parallelogram => "parallelogram",
            ShapeGeometry.Trapezoid => "trapezoid",
            ShapeGeometry.Pentagon => "regularPentagon",
            ShapeGeometry.HomePlate => "homePlate",
            ShapeGeometry.Hexagon => "hexagon",
            ShapeGeometry.Octagon => "octagon",
            ShapeGeometry.Chevron => "chevron",
            ShapeGeometry.RightArrow => "rightArrow",
            ShapeGeometry.LeftArrow => "leftArrow",
            ShapeGeometry.UpArrow => "upArrow",
            ShapeGeometry.DownArrow => "downArrow",
            ShapeGeometry.Star5 => "star5",
            ShapeGeometry.Plus => "plus",
            ShapeGeometry.Line => "line",
            ShapeGeometry.Elbow => "bentConnector3",
            ShapeGeometry.Corner => "bentConnector2",
            ShapeGeometry.Curve => "curvedConnector3",
            _ => "rect",
        };
        var av = new XElement(A + "avLst");
        if (shape.Adjust is { } adj && shape.Geometry is ShapeGeometry.RoundRect or ShapeGeometry.Elbow or ShapeGeometry.Curve or ShapeGeometry.Triangle)
            av.Add(new XElement(A + "gd", new XAttribute("name", shape.Geometry == ShapeGeometry.RoundRect || shape.Geometry == ShapeGeometry.Triangle ? "adj" : "adj1"),
                new XAttribute("fmla", $"val {(int)Math.Round(adj * 100000)}")));
        return new XElement(A + "prstGeom", new XAttribute("prst", prst), av);
    }

    /// <summary>A freeform outline as straight segments in the shape's own coordinates (EMU).</summary>
    static XElement CustomGeometry(SlideShape shape)
    {
        XElement Pt(Point p) => new(A + "pt", new XAttribute("x", Emu(p.X)), new XAttribute("y", Emu(p.Y)));
        var paths = new List<XElement>();
        // Parts that aren't filled (open lines) go in a path of their own.
        foreach (var group in SlideRenderer.Flatten(shape.Path!).GroupBy(run => run.filled))
        {
            var path = new XElement(A + "path", new XAttribute("w", Emu(shape.Bounds.Width)), new XAttribute("h", Emu(shape.Bounds.Height)), group.Key ? null : new XAttribute("fill", "none"));
            foreach (var (points, closed, _) in group)
            {
                path.Add(new XElement(A + "moveTo", Pt(points[0])), points.Skip(1).Select(p => new XElement(A + "lnTo", Pt(p))), closed ? new XElement(A + "close") : null);
            }
            paths.Add(path);
        }
        return new XElement(A + "custGeom",
            new XElement(A + "avLst"), new XElement(A + "gdLst"), new XElement(A + "ahLst"), new XElement(A + "cxnLst"),
            new XElement(A + "rect", new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", "r"), new XAttribute("b", "b")),
            new XElement(A + "pathLst", paths));
    }

    XElement? Fill(SlideFill fill)
    {
        if (fill.Image != null)
        {
            var blipFill = new XElement(A + "blipFill", new XAttribute("rotWithShape", 1), new XElement(A + "blip", new XAttribute(R + "embed", Media(fill.Image)),
                fill.Opacity < 0.999 ? new XElement(A + "alphaModFix", new XAttribute("amt", (int)(fill.Opacity * 100000))) : null));
            if (fill.Tile == null && fill.Crop != default) blipFill.Add(SourceRect(fill.Crop));
            blipFill.Add(fill.Tile is { } tile
                ? new XElement(A + "tile", new XAttribute("tx", 0), new XAttribute("ty", 0), new XAttribute("sx", (int)(tile * 100000)),
                    new XAttribute("sy", (int)(tile * 100000)), new XAttribute("flip", "none"), new XAttribute("algn", "tl"))
                : new XElement(A + "stretch", new XElement(A + "fillRect")));
            return blipFill;
        }
        if (fill.Gradient is { Count: > 0 } stops)
        {
            var grad = new XElement(A + "gradFill", new XAttribute("rotWithShape", 1),
                new XElement(A + "gsLst", stops.Select(s => new XElement(A + "gs", new XAttribute("pos", (int)Math.Round(s.Position * 100000)), Rgb(s.Color)))));
            grad.Add(fill.RadialGradient
                ? new XElement(A + "path", new XAttribute("path", "circle"), new XElement(A + "fillToRect",
                    new XAttribute("l", 50000), new XAttribute("t", 50000), new XAttribute("r", 50000), new XAttribute("b", 50000)))
                : new XElement(A + "lin", new XAttribute("ang", (long)Math.Round((fill.GradientAngle % 360 + 360) % 360 * 60000)), new XAttribute("scaled", 0)));
            grad.Add(new XElement(A + "tileRect")); // without it PowerPoint stretches the gradient oddly
            return grad;
        }
        return fill.Color is { } c ? Solid(c) : null;
    }

    static XElement Line(SlideShape shape)
    {
        if (shape.Line is not { A: > 0 } color) return new XElement(A + "ln", new XElement(A + "noFill"));
        var ln = new XElement(A + "ln", new XAttribute("w", Emu(shape.LineWidth)), Solid(color));
        if (shape.Dashed) ln.Add(new XElement(A + "prstDash", new XAttribute("val", "dash")));
        if (shape.HeadEnd != ArrowHead.None) ln.Add(new XElement(A + "headEnd", new XAttribute("type", LineEnd(shape.HeadEnd))));
        if (shape.TailEnd != ArrowHead.None) ln.Add(new XElement(A + "tailEnd", new XAttribute("type", LineEnd(shape.TailEnd))));
        return ln;
    }

    static string LineEnd(ArrowHead end) => end switch
    {
        ArrowHead.Stealth => "stealth", ArrowHead.Open => "arrow", ArrowHead.Diamond => "diamond", ArrowHead.Oval => "oval", _ => "triangle",
    };

    static XElement Solid(Color c) => new(A + "solidFill", Rgb(c));

    static XElement Rgb(Color c)
    {
        var el = new XElement(A + "srgbClr", new XAttribute("val", Units.Hex(c)));
        if (c.A < 255) el.Add(new XElement(A + "alpha", new XAttribute("val", c.A * 100000 / 255)));
        return el;
    }

    /// <summary>Adds a picture to the package once; formats PowerPoint can't show become PNG.</summary>
    string Media(DImage image)
    {
        if (!_media.TryGetValue(image, out var name))
        {
            var ext = image.Ext.ToLowerInvariant() switch { "jpeg" => "jpg", "tif" => "tiff", var e => e };
            var data = image.Data;
            if (ext is not ("png" or "jpg" or "gif" or "bmp" or "tiff" or "emf" or "wmf"))
            {
                data = ToPng(data) ?? data;
                ext = "png";
            }
            name = $"image{_media.Count + 1}.{ext}";
            _media[image] = name;
            _mediaTypes.Add(ext);
            var entry = _zip.CreateEntry($"ppt/media/{name}", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(data);
        }
        var rid = $"rId{_rels.Count + 1}";
        _rels.Add(Rel(rid, "image", $"../media/{name}"));
        return rid;
    }

    internal static byte[]? ToPng(byte[] data)
    {
        if (FlowBridge.Decode(data) is not { } bitmap) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ speaker notes

    static XElement Placeholder(int id, string name, string type, int idx, Rect bounds, XElement? body) => new(P + "sp",
        new XElement(P + "nvSpPr",
            new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
            new XElement(P + "cNvSpPr", new XElement(A + "spLocks", new XAttribute("noGrp", 1))),
            new XElement(P + "nvPr", new XElement(P + "ph", new XAttribute("type", type), idx > 0 ? new XAttribute("idx", idx) : null))),
        new XElement(P + "spPr", Xfrm(bounds, 0, false, false)),
        body);

    /// <summary>A notes page per slide: the slide picture above, the notes text below (PowerPoint's own layout).</summary>
    void WriteNotes(Slide slide, int index)
    {
        var body = new XElement(P + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
            slide.Notes.Select(n => new XElement(A + "p",
                n.Split('\n').SelectMany((line, i) => new[]
                {
                    i > 0 ? new XElement(A + "br", new XElement(A + "rPr", new XAttribute("lang", "en-US"))) : null,
                    line.Length > 0 ? new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("dirty", 0)), new XElement(A + "t", Clean(line))) : null,
                }.OfType<XElement>()))));
        var tree = new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", Xfrm(new Rect(), 0, false, false, group: true)),
            new XElement(P + "sp",
                new XElement(P + "nvSpPr",
                    new XElement(P + "cNvPr", new XAttribute("id", 2), new XAttribute("name", "Slide Image")),
                    new XElement(P + "cNvSpPr", new XElement(A + "spLocks", new XAttribute("noGrp", 1), new XAttribute("noRot", 1), new XAttribute("noChangeAspect", 1))),
                    new XElement(P + "nvPr", new XElement(P + "ph", new XAttribute("type", "sldImg")))),
                new XElement(P + "spPr")),
            new XElement(P + "sp",
                new XElement(P + "nvSpPr",
                    new XElement(P + "cNvPr", new XAttribute("id", 3), new XAttribute("name", "Notes")),
                    new XElement(P + "cNvSpPr", new XElement(A + "spLocks", new XAttribute("noGrp", 1))),
                    new XElement(P + "nvPr", new XElement(P + "ph", new XAttribute("type", "body"), new XAttribute("idx", 1)))),
                new XElement(P + "spPr"),
                body));
        Save($"ppt/notesSlides/notesSlide{index + 1}.xml", new XElement(P + "notes", Ns(), new XElement(P + "cSld", tree),
            new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
        Save($"ppt/notesSlides/_rels/notesSlide{index + 1}.xml.rels", Rels([
            Rel("rId1", "notesMaster", "../notesMasters/notesMaster1.xml"),
            Rel("rId2", "slide", $"../slides/slide{index + 1}.xml"),
        ]));
    }

    void WriteNotesMaster()
    {
        const double H = 720; // a portrait notes page (7.5 × 10 in)
        double slideH = Math.Min(270, 432 * _deck.Height / Math.Max(1, _deck.Width));
        var tree = new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", Xfrm(new Rect(), 0, false, false, group: true)),
            Placeholder(2, "Slide Image", "sldImg", 2, new Rect(54, 54, 432, slideH), null),
            Placeholder(3, "Notes", "body", 1, new Rect(54, 72 + slideH, 432, H - 126 - slideH),
                new XElement(P + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"), new XElement(A + "p"))));
        XElement Level(int n) => new(A + $"lvl{n}pPr", new XAttribute("marL", (n - 1) * 457200), new XElement(A + "defRPr", new XAttribute("sz", 1200),
            new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "tx1"))), new XElement(A + "latin", new XAttribute("typeface", "+mn-lt"))));
        Save("ppt/notesMasters/notesMaster1.xml", new XElement(P + "notesMaster", Ns(),
            new XElement(P + "cSld",
                new XElement(P + "bg", new XElement(P + "bgRef", new XAttribute("idx", 1001), new XElement(A + "schemeClr", new XAttribute("val", "bg1")))),
                tree),
            new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
                new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"), new XAttribute("accent3", "accent3"),
                new XAttribute("accent4", "accent4"), new XAttribute("accent5", "accent5"), new XAttribute("accent6", "accent6"),
                new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink")),
            new XElement(P + "notesStyle", Enumerable.Range(1, 3).Select(Level))));
        Save("ppt/notesMasters/_rels/notesMaster1.xml.rels", Rels([Rel("rId1", "theme", "../theme/theme2.xml")]));
        Save("ppt/theme/theme2.xml", Theme());
    }

    // ------------------------------------------------------------------ fixed parts

    void WriteMasterAndLayout()
    {
        var emptyTree = () => new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")),
                new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", Xfrm(new Rect(), 0, false, false, group: true)));
        XElement Level(int size) => new(A + "lvl1pPr", new XElement(A + "defRPr", new XAttribute("sz", size),
            new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "tx1"))),
            new XElement(A + "latin", new XAttribute("typeface", "+mn-lt"))));

        Save("ppt/slideMasters/slideMaster1.xml", new XElement(P + "sldMaster", Ns(),
            new XElement(P + "cSld",
                new XElement(P + "bg", new XElement(P + "bgRef", new XAttribute("idx", 1001), new XElement(A + "schemeClr", new XAttribute("val", "bg1")))),
                emptyTree()),
            new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
                new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"), new XAttribute("accent3", "accent3"),
                new XAttribute("accent4", "accent4"), new XAttribute("accent5", "accent5"), new XAttribute("accent6", "accent6"),
                new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink")),
            new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId", new XAttribute("id", 2147483649), new XAttribute(R + "id", "rId1"))),
            new XElement(P + "txStyles",
                new XElement(P + "titleStyle", Level(4400)), new XElement(P + "bodyStyle", Level(2800)), new XElement(P + "otherStyle", Level(1800)))));
        Save("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels([
            Rel("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"),
            Rel("rId2", "theme", "../theme/theme1.xml"),
        ]));
        Save("ppt/slideLayouts/slideLayout1.xml", new XElement(P + "sldLayout", Ns(), new XAttribute("type", "blank"), new XAttribute("preserve", 1),
            new XElement(P + "cSld", new XAttribute("name", "Blank"), emptyTree()),
            new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
        Save("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels([Rel("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")]));
    }

    XElement Theme()
    {
        XElement Rgb(string name, string hex) => new(A + name, new XElement(A + "srgbClr", new XAttribute("val", hex)));
        XElement Font(string name, string face) => new(A + name,
            new XElement(A + "latin", new XAttribute("typeface", face)), new XElement(A + "ea", new XAttribute("typeface", "")), new XElement(A + "cs", new XAttribute("typeface", "")));
        XElement Ph() => new(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")));
        return new XElement(A + "theme", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute("name", "Office Theme"),
            new XElement(A + "themeElements",
                new XElement(A + "clrScheme", new XAttribute("name", "Office"),
                    new XElement(A + "dk1", new XElement(A + "sysClr", new XAttribute("val", "windowText"), new XAttribute("lastClr", "000000"))),
                    new XElement(A + "lt1", new XElement(A + "sysClr", new XAttribute("val", "window"), new XAttribute("lastClr", "FFFFFF"))),
                    Rgb("dk2", "44546A"), Rgb("lt2", "E7E6E6"), Rgb("accent1", "4472C4"), Rgb("accent2", "ED7D31"), Rgb("accent3", "A5A5A5"),
                    Rgb("accent4", "FFC000"), Rgb("accent5", "5B9BD5"), Rgb("accent6", "70AD47"), Rgb("hlink", "0563C1"), Rgb("folHlink", "954F72")),
                new XElement(A + "fontScheme", new XAttribute("name", "Office"), Font("majorFont", _deck.Font), Font("minorFont", _deck.Font)),
                new XElement(A + "fmtScheme", new XAttribute("name", "Office"),
                    new XElement(A + "fillStyleLst", Ph(), Ph(), Ph()),
                    new XElement(A + "lnStyleLst", Enumerable.Range(0, 3).Select(i => new XElement(A + "ln", new XAttribute("w", 6350 * (i + 1)), Ph()))),
                    new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
                    new XElement(A + "bgFillStyleLst", Ph(), Ph(), Ph()))));
    }

    XElement ContentTypes(int slides)
    {
        var types = new XElement(CT + "Types",
            new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
        foreach (var ext in _mediaTypes)
            types.Add(new XElement(CT + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", ext switch
            {
                "jpg" => "image/jpeg", "emf" => "image/x-emf", "wmf" => "image/x-wmf", _ => "image/" + ext,
            })));
        void Override(string part, string type) => types.Add(new XElement(CT + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type)));
        Override("/ppt/presentation.xml", Ml + "presentation.main+xml");
        for (int i = 1; i <= slides; i++) Override($"/ppt/slides/slide{i}.xml", Ml + "slide+xml");
        Override("/ppt/slideMasters/slideMaster1.xml", Ml + "slideMaster+xml");
        Override("/ppt/slideLayouts/slideLayout1.xml", Ml + "slideLayout+xml");
        Override("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml");
        if (_deck.Slides.Any(s => s.Notes.Count > 0))
        {
            Override("/ppt/notesMasters/notesMaster1.xml", Ml + "notesMaster+xml");
            Override("/ppt/theme/theme2.xml", "application/vnd.openxmlformats-officedocument.theme+xml");
            for (int i = 0; i < slides; i++)
                if (_deck.Slides[i].Notes.Count > 0) Override($"/ppt/notesSlides/notesSlide{i + 1}.xml", Ml + "notesSlide+xml");
        }
        Override("/ppt/presProps.xml", Ml + "presProps+xml");
        Override("/ppt/tableStyles.xml", Ml + "tableStyles+xml");
        Override("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        return types;
    }

    // ------------------------------------------------------------------ helpers

    static object[] Ns() =>
    [
        new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XAttribute(XNamespace.Xmlns + "p", P),
    ];

    static XElement Rel(string id, string type, string target) =>
        new(PR + "Relationship", new XAttribute("Id", id), new XAttribute("Type", RelBase + type), new XAttribute("Target", target));

    static XElement Rels(IEnumerable<XElement> rels) => new(PR + "Relationships", rels);

    void Save(string path, XElement root)
    {
        SafeXml.Clean(root);
        var entry = _zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
    }

    static long Emu(double points) => (long)Math.Round(points * 12700);
    static int Pct(double fraction) => (int)Math.Round(fraction * 100000);
}
