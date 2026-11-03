using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Reads PowerPoint (.pptx) slides: theme colours and fonts, placeholders inheriting from their layout
/// and master, text styles, shapes, pictures, tables, groups and SmartArt.
/// </summary>
internal sealed class PptxReader
{
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    static readonly XNamespace Dsp = "http://schemas.microsoft.com/office/drawing/2008/diagram";
    const double Emu = 12700;

    sealed record Rel(string Target, string Type, bool External);
    sealed record Part(string Path, XElement Root, Dictionary<string, Rel> Rels);

    sealed class Theme
    {
        public Dictionary<string, Color> Colors { get; } = [];
        public string Major { get; set; } = "Calibri Light";
        public string Minor { get; set; } = "Calibri";
        public XElement? Formats { get; set; }
        /// <summary>The theme part itself: pictures in its fill styles are linked from it.</summary>
        public Part? Part { get; set; }
    }

    /// <summary>Everything a shape on one slide needs to resolve inherited looks.</summary>
    sealed class Context
    {
        public required Part Part { get; set; }
        public Part? Layout { get; init; }
        public Part? Master { get; init; }
        public required Theme Theme { get; init; }
        public required Dictionary<string, string> ColorMap { get; init; }
        public int SlideNumber { get; init; }
        public SlideFill? GroupFill { get; set; }
    }

    /// <summary>Maps a group's child coordinates onto the slide.</summary>
    sealed record GroupMap(double OffX, double OffY, double ScaleX, double ScaleY, double ChildX, double ChildY, GroupMap? Parent)
    {
        public Rect Map(Rect r)
        {
            var mapped = new Rect(OffX + (r.X - ChildX) * ScaleX, OffY + (r.Y - ChildY) * ScaleY, r.Width * ScaleX, r.Height * ScaleY);
            return Parent?.Map(mapped) ?? mapped;
        }
    }

    sealed class RunProps
    {
        public double? Size;
        public bool? Bold, Italic, Underline, Strike, Caps;
        public int Baseline;
        public Color? Color;
        public string? Font;
        public RunProps Clone() => (RunProps)MemberwiseClone();
    }

    sealed class ParaProps
    {
        public string? Align;
        public double? MarginLeft, Indent;
        public double? LinePct, LinePts, BeforePts, BeforePct, AfterPts, AfterPct;
        /// <summary>-1 unset, 0 none, 1 character, 2 automatic numbering.</summary>
        public int Bullet = -1;
        public string? BulletChar, BulletFont, NumberType;
        public int NumberStart = 1;
        public Color? BulletColor;
        public double? BulletSize;
        public RunProps Run = new();

        public ParaProps Clone()
        {
            var p = (ParaProps)MemberwiseClone();
            p.Run = Run.Clone();
            return p;
        }
    }

    readonly ZipArchive _zip;
    readonly CancellationToken _ct;
    readonly Dictionary<string, Part?> _parts = [];
    readonly Dictionary<string, Theme> _themes = [];
    XElement? _defaultTextStyle;

    PptxReader(ZipArchive zip, CancellationToken ct) => (_zip, _ct) = (zip, ct);

    public static Deck Read(string path, CancellationToken ct = default)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return new PptxReader(zip, ct).Build(Path.GetFileNameWithoutExtension(path));
        }
        catch (InvalidDataException)
        {
            throw new ConversionException("This presentation is damaged or password-protected.");
        }
    }

    Deck Build(string title)
    {
        var pres = LoadPart("ppt/presentation.xml") ?? throw new ConversionException("This isn't a PowerPoint presentation.");
        var deck = new Deck { Title = title };
        var size = pres.Root.Element(P + "sldSz");
        deck.Width = (Long(size, "cx") ?? 9144000) / Emu;
        deck.Height = (Long(size, "cy") ?? 6858000) / Emu;
        _defaultTextStyle = pres.Root.Element(P + "defaultTextStyle");

        int number = 0;
        foreach (var id in pres.Root.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            _ct.ThrowIfCancellationRequested();
            number++;
            if (Attr(id, R + "id") is not { } rid || !pres.Rels.TryGetValue(rid, out var rel)) continue;
            var slidePart = LoadPart(rel.Target);
            if (slidePart == null || Attr(slidePart.Root, "show") is "0" or "false") continue; // hidden slides aren't exported
            deck.Slides.Add(ReadSlide(slidePart, number, deck));
        }
        if (deck.Slides.Count == 0) throw new ConversionException("This presentation has no slides.");
        return deck;
    }

    Slide ReadSlide(Part slidePart, int number, Deck deck)
    {
        var layout = Related(slidePart, "/slideLayout");
        var master = layout != null ? Related(layout, "/slideMaster") : null;
        var theme = master != null ? ThemeOf(master) : new Theme();
        deck.Font = theme.Minor;

        // Colour mapping: the master's, overridden by the layout's and then the slide's.
        var map = new Dictionary<string, string>
        {
            ["bg1"] = "lt1", ["tx1"] = "dk1", ["bg2"] = "lt2", ["tx2"] = "dk2",
        };
        foreach (var a in master?.Root.Element(P + "clrMap")?.Attributes() ?? []) map[a.Name.LocalName] = a.Value;
        foreach (var part in new[] { layout, slidePart })
            foreach (var a in part?.Root.Element(P + "clrMapOvr")?.Element(A + "overrideClrMapping")?.Attributes() ?? [])
                map[a.Name.LocalName] = a.Value;

        var ctx = new Context { Part = slidePart, Layout = layout, Master = master, Theme = theme, ColorMap = map, SlideNumber = number };
        var slide = new Slide { Background = Background(ctx) };

        // Decorations from the master and layout (logos, bars) sit behind the slide's own shapes.
        bool showMaster = Attr(slidePart.Root, "showMasterSp") is not ("0" or "false");
        if (showMaster && master != null && Attr(layout?.Root, "showMasterSp") is not ("0" or "false"))
            ReadTree(Tree(master), Inherit(ctx, master), null, slide.Shapes, placeholders: false);
        if (showMaster && layout != null)
            ReadTree(Tree(layout), Inherit(ctx, layout), null, slide.Shapes, placeholders: false);
        ReadTree(Tree(slidePart), ctx, null, slide.Shapes, placeholders: true);

        // Speaker notes: the body placeholder of the slide's notes page.
        if (Related(slidePart, "/notesSlide") is { } notes)
            foreach (var sp in Tree(notes)?.Descendants(P + "sp") ?? [])
                if (Attr(PlaceholderOf(sp), "type") == "body")
                    foreach (var para in sp.Descendants(A + "p"))
                        slide.Notes.Add(string.Concat(para.Elements().Select(e => e.Name.LocalName switch { "r" or "fld" => (string?)e.Element(A + "t") ?? "", "br" => "\n", _ => "" })));
        while (slide.Notes.Count > 0 && slide.Notes[^1].Length == 0) slide.Notes.RemoveAt(slide.Notes.Count - 1);
        return slide;
    }

    static Context Inherit(Context ctx, Part part) => new()
    {
        Part = part, Layout = ctx.Layout, Master = ctx.Master, Theme = ctx.Theme, ColorMap = ctx.ColorMap, SlideNumber = ctx.SlideNumber,
    };

    static Context ThemeContext(Context ctx) => ctx.Theme.Part is { } part ? Inherit(ctx, part) : ctx;

    static XElement? Tree(Part part) => part.Root.Element(P + "cSld")?.Element(P + "spTree");

    // ------------------------------------------------------------------ shapes

    void ReadTree(XElement? tree, Context ctx, GroupMap? group, List<SlideShape> into, bool placeholders)
    {
        if (tree == null) return;
        foreach (var el in tree.Elements())
        {
            try
            {
                switch (el.Name.LocalName)
                {
                    case "sp" or "cxnSp":
                        if (ReadShape(el, ctx, group, placeholders) is { } shape) into.Add(shape);
                        break;
                    case "pic":
                        if (ReadPicture(el, ctx, group) is { } pic) into.Add(pic);
                        break;
                    case "grpSp":
                        ReadGroup(el, ctx, group, into, placeholders);
                        break;
                    case "graphicFrame":
                        ReadFrame(el, ctx, group, into);
                        break;
                    case "AlternateContent":
                        // The fallback is what older PowerPoints draw (usually a picture); the choice may need newer features.
                        var pick = el.Element(Mc + "Fallback") is { HasElements: true } fb ? fb : el.Element(Mc + "Choice");
                        if (pick != null) ReadTree(pick, ctx, group, into, placeholders);
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidOperationException)
            {
                Log.Write($"Skipped a shape on slide {ctx.SlideNumber}: {ex.Message}");
            }
        }
    }

    void ReadGroup(XElement grp, Context ctx, GroupMap? parent, List<SlideShape> into, bool placeholders)
    {
        var spPr = Child(grp, "grpSpPr");
        var xfrm = spPr?.Element(A + "xfrm");
        if (Attr(Child(Child(grp, "nvGrpSpPr"), "cNvPr"), "hidden") is "1" or "true") return;
        GroupMap? map = parent;
        if (xfrm != null)
        {
            double ox = Coord(xfrm.Element(A + "off"), "x"), oy = Coord(xfrm.Element(A + "off"), "y");
            double w = Coord(xfrm.Element(A + "ext"), "cx"), h = Coord(xfrm.Element(A + "ext"), "cy");
            double cx = Coord(xfrm.Element(A + "chOff"), "x"), cy = Coord(xfrm.Element(A + "chOff"), "y");
            double cw = Coord(xfrm.Element(A + "chExt"), "cx"), ch = Coord(xfrm.Element(A + "chExt"), "cy");
            map = new GroupMap(ox, oy, cw > 0 ? w / cw : 1, ch > 0 ? h / ch : 1, cx, cy, parent);
        }
        var saved = ctx.GroupFill;
        var (has, fill) = FillOf(spPr, ctx);
        if (has) ctx.GroupFill = fill;
        int start = into.Count;
        ReadTree(grp, ctx, map, into, placeholders);
        ctx.GroupFill = saved;

        // A rotated or flipped group turns its children about the group's centre.
        double rot = (Long(xfrm, "rot") ?? 0) / 60000.0;
        if (Math.Abs(rot) > 0.01 && map != null && xfrm != null)
        {
            var groupRect = (parent?.Map(Bounds(xfrm)) ?? Bounds(xfrm));
            var center = new Point(groupRect.X + groupRect.Width / 2, groupRect.Y + groupRect.Height / 2);
            var turn = new RotateTransform(rot, center.X, center.Y);
            for (int i = start; i < into.Count; i++)
            {
                var s = into[i];
                var c = turn.Transform(new Point(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2));
                s.Bounds = new Rect(c.X - s.Bounds.Width / 2, c.Y - s.Bounds.Height / 2, s.Bounds.Width, s.Bounds.Height);
                s.Rotation += rot;
            }
        }
    }

    SlideShape? ReadShape(XElement sp, Context ctx, GroupMap? group, bool placeholders)
    {
        var nv = Child(sp, "nvSpPr") ?? Child(sp, "nvCxnSpPr");
        if (Attr(Child(nv, "cNvPr"), "hidden") is "1" or "true") return null;
        var ph = Child(Child(nv, "nvPr"), "ph");
        if (ph != null && !placeholders) return null;

        var (layoutSp, masterSp) = ph != null ? FindPlaceholder(ph, ctx) : (null, null);
        var spPr = Child(sp, "spPr");
        var inherited = new[] { Child(sp, "spPr"), Child(layoutSp, "spPr"), Child(masterSp, "spPr") };
        var xfrm = inherited.Select(s => s?.Element(A + "xfrm")).FirstOrDefault(x => x?.Element(A + "off") != null);
        if (xfrm == null) return null;

        var shape = new SlideShape
        {
            Bounds = Map(Bounds(xfrm), group),
            Rotation = (Long(xfrm, "rot") ?? 0) / 60000.0,
            FlipH = Attr(xfrm, "flipH") is "1" or "true",
            FlipV = Attr(xfrm, "flipV") is "1" or "true",
            Role = ph != null ? NormalType(Attr(ph, "type")) : null,
        };

        var geometrySource = inherited.FirstOrDefault(s => s?.Element(A + "prstGeom") != null || s?.Element(A + "custGeom") != null);
        Geometry(geometrySource, shape);
        if (sp.Name.LocalName == "cxnSp" && shape.Geometry is not (ShapeGeometry.Custom or ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve)) shape.Geometry = ShapeGeometry.Line;

        var style = Child(sp, "style");
        // Fill: the shape's own, its placeholder's, or the theme style it references.
        var fill = inherited.Select(s => FillOf(s, ctx)).FirstOrDefault(f => f.has);
        shape.Fill = fill.has ? fill.fill : StyleFill(style, ctx);
        ReadLine(inherited.Select(s => s?.Element(A + "ln")).FirstOrDefault(l => l != null), style, ctx, shape);

        var txBody = Child(sp, "txBody");
        var txXfrm = sp.Element(Dsp + "txXfrm");
        if (txXfrm != null)
        {
            // SmartArt places text in its own rectangle within the shape.
            var t = Map(Bounds(txXfrm), group);
            var b = shape.Bounds;
            shape.Insets = new Thickness(Math.Max(0, t.X - b.X), Math.Max(0, t.Y - b.Y), Math.Max(0, b.Right - t.Right), Math.Max(0, b.Bottom - t.Bottom));
        }
        if (txBody != null) ReadText(txBody, ph, layoutSp, masterSp, style, ctx, shape, txXfrm == null);
        return shape;
    }

    SlideShape? ReadPicture(XElement pic, Context ctx, GroupMap? group)
    {
        var nv = Child(pic, "nvPicPr");
        if (Attr(Child(nv, "cNvPr"), "hidden") is "1" or "true") return null;
        var ph = Child(Child(nv, "nvPr"), "ph");
        var (layoutSp, masterSp) = ph != null ? FindPlaceholder(ph, ctx) : (null, null);
        var xfrm = new[] { Child(pic, "spPr"), Child(layoutSp, "spPr"), Child(masterSp, "spPr") }
            .Select(s => s?.Element(A + "xfrm")).FirstOrDefault(x => x?.Element(A + "off") != null);
        var blipFill = Child(pic, "blipFill");
        if (xfrm == null || blipFill == null) return null;

        var image = Blip(blipFill.Element(A + "blip"), ctx);
        if (image == null) return null;
        var shape = new SlideShape
        {
            Bounds = Map(Bounds(xfrm), group),
            Rotation = (Long(xfrm, "rot") ?? 0) / 60000.0,
            FlipH = Attr(xfrm, "flipH") is "1" or "true",
            FlipV = Attr(xfrm, "flipV") is "1" or "true",
            Picture = image,
            Crop = Crop(blipFill.Element(A + "srcRect")),
        };
        Geometry(Child(pic, "spPr"), shape);
        if (shape.Geometry is ShapeGeometry.Line or ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve) shape.Geometry = ShapeGeometry.Rect;
        ReadLine(Child(pic, "spPr")?.Element(A + "ln"), null, ctx, shape);
        return shape;
    }

    void ReadFrame(XElement frame, Context ctx, GroupMap? group, List<SlideShape> into)
    {
        var xfrm = Child(frame, "xfrm");
        var data = frame.Element(A + "graphic")?.Element(A + "graphicData");
        if (xfrm == null || data == null) return;
        var bounds = Map(Bounds(xfrm), group);

        if (data.Element(A + "tbl") is { } tbl)
        {
            var shape = new SlideShape { Bounds = bounds, Insets = new Thickness(0) };
            shape.Text.Add(ReadTable(tbl, ctx));
            into.Add(shape);
            return;
        }

        // Charts: drawn from the data PowerPoint keeps with them (there is no saved picture).
        if (((string?)data.Attribute("uri"))?.EndsWith("/chart") == true
            && Attr(data.Elements().FirstOrDefault(e => e.Name.LocalName == "chart"), R + "id") is { } chartId
            && ctx.Part.Rels.TryGetValue(chartId, out var chartRel) && LoadPart(chartRel.Target) is { } chartPart
            && Charts.ReadDrawingML(chartPart.Root, name => Scheme(name, ctx, null), ctx.Theme.Minor) is { } chart)
        {
            into.Add(new SlideShape { Bounds = bounds, Picture = Charts.ToImage(chart, bounds.Width, bounds.Height) });
            return;
        }

        // SmartArt: PowerPoint saves a ready-made drawing of the diagram next to its data.
        if (((string?)data.Attribute("uri"))?.EndsWith("/diagram") == true && DiagramDrawing(data, ctx.Part) is { } drawing)
        {
            var saved = ctx.Part;
            ctx.Part = drawing;
            ReadTree(drawing.Root.Element(Dsp + "spTree"), ctx, new GroupMap(bounds.X, bounds.Y, 1, 1, 0, 0, null), into, placeholders: true);
            ctx.Part = saved;
            return;
        }

        // Charts and embedded objects: their cached preview picture, when there is one.
        var preview = data.Descendants(P + "pic").FirstOrDefault();
        if (preview != null && ReadPicture(preview, ctx, group) is { } pic) into.Add(pic);
    }

    Part? DiagramDrawing(XElement data, Part slide)
    {
        var ids = data.Elements().FirstOrDefault(e => e.Name.LocalName == "relIds");
        if (Attr(ids, R + "dm") is { } dm && slide.Rels.TryGetValue(dm, out var dmRel) && LoadPart(dmRel.Target) is { } model)
        {
            var ext = model.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt");
            if (Attr(ext, "relId") is { } relId && slide.Rels.TryGetValue(relId, out var rel)) return LoadPart(rel.Target);
        }
        var drawings = slide.Rels.Values.Where(r => r.Type.EndsWith("/diagramDrawing")).ToList();
        return drawings.Count == 1 ? LoadPart(drawings[0].Target) : null;
    }

    // ------------------------------------------------------------------ placeholders

    static string NormalType(string? type) => type switch
    {
        "ctrTitle" or "title" => "title",
        "dt" or "ftr" or "sldNum" or "hdr" => type,
        _ => "body",
    };

    /// <summary>The layout placeholder this one inherits from (by index, then type), and that one's master placeholder.</summary>
    static (XElement? layout, XElement? master) FindPlaceholder(XElement ph, Context ctx)
    {
        var type = Attr(ph, "type");
        var idx = Attr(ph, "idx");
        XElement? layoutSp = null;
        if (ctx.Layout != null && ctx.Part != ctx.Layout)
        {
            var candidates = Placeholders(ctx.Layout).ToList();
            layoutSp = (idx != null ? candidates.FirstOrDefault(c => Attr(c.ph, "idx") == idx).sp : null)
                       ?? candidates.FirstOrDefault(c => Attr(c.ph, "type") == type && type != null).sp
                       ?? candidates.FirstOrDefault(c => NormalType(Attr(c.ph, "type")) == NormalType(type) && (idx == null || NormalType(type) != "body")).sp;
        }
        XElement? masterSp = null;
        if (ctx.Master != null && ctx.Part != ctx.Master)
        {
            var layoutType = layoutSp != null ? Attr(PlaceholderOf(layoutSp), "type") : type;
            var normal = NormalType(layoutType ?? type);
            masterSp = Placeholders(ctx.Master).FirstOrDefault(c => NormalType(Attr(c.ph, "type")) == normal).sp;
        }
        return (layoutSp, masterSp);
    }

    static IEnumerable<(XElement sp, XElement ph)> Placeholders(Part part) =>
        (Tree(part)?.Descendants() ?? []).Where(e => e.Name.LocalName is "sp" or "pic")
            .Select(sp => (sp, ph: PlaceholderOf(sp)!)).Where(x => x.ph != null);

    static XElement? PlaceholderOf(XElement sp) =>
        Child(Child(Child(sp, "nvSpPr") ?? Child(sp, "nvPicPr"), "nvPr"), "ph");

    // ------------------------------------------------------------------ text

    void ReadText(XElement txBody, XElement? ph, XElement? layoutSp, XElement? masterSp, XElement? style, Context ctx, SlideShape shape, bool useInsets)
    {
        var bodies = new[] { Child(masterSp, "txBody"), Child(layoutSp, "txBody"), txBody };
        var autoFit = txBody.Element(A + "bodyPr")?.Element(A + "normAutofit");
        foreach (var bodyPr in bodies.Select(b => b?.Element(A + "bodyPr")).Where(b => b != null))
        {
            if (useInsets)
            {
                var ins = shape.Insets;
                shape.Insets = new Thickness(
                    Long(bodyPr, "lIns") is { } l ? l / Emu : ins.Left, Long(bodyPr, "tIns") is { } t ? t / Emu : ins.Top,
                    Long(bodyPr, "rIns") is { } r ? r / Emu : ins.Right, Long(bodyPr, "bIns") is { } b ? b / Emu : ins.Bottom);
            }
            if (Attr(bodyPr, "anchor") is { } anchor) shape.Anchor = anchor switch { "ctr" => TextAnchor.Middle, "b" => TextAnchor.Bottom, _ => TextAnchor.Top };
            if (Attr(bodyPr, "wrap") is { } wrap) shape.Wrap = wrap != "none";
            if (Attr(bodyPr, "vert") is { } vert) shape.TextRotation = vert switch { "vert" or "eaVert" => 90, "vert270" => 270, _ => 0 };
            if (bodyPr!.Element(A + "normAutofit") != null) shape.ShrinkToFit = true;
        }
        double fontScale = (Long(autoFit, "fontScale") ?? 100000) / 100000.0;
        double lineReduction = (Long(autoFit, "lnSpcReduction") ?? 0) / 100000.0;

        // Where the base look comes from: the master's title, body or "other" text styles.
        var kind = ph == null ? "other" : NormalType(Attr(ph, "type")) switch { "title" => "title", "body" => "body", _ => "other" };
        var txStyles = ctx.Master?.Root.Element(P + "txStyles");
        var baseStyle = kind switch
        {
            "title" => txStyles?.Element(P + "titleStyle"),
            "body" => txStyles?.Element(P + "bodyStyle"),
            _ => txStyles?.Element(P + "otherStyle"),
        };
        var lists = new List<XElement?>();
        if (kind == "other") lists.Add(_defaultTextStyle);
        lists.Add(baseStyle);
        lists.AddRange(bodies.Select(b => b?.Element(A + "lstStyle")));

        // Shapes styled by the theme (e.g. a filled rectangle) get their default text colour from it.
        var fontRef = style?.Element(A + "fontRef");
        var styleColor = ColorOf(fontRef, ctx);
        var styleFont = Attr(fontRef, "idx") switch { "major" => ctx.Theme.Major, "minor" => ctx.Theme.Minor, _ => null };

        var counters = new Dictionary<int, (string type, int n)>();
        foreach (var para in txBody.Elements(A + "p"))
        {
            var pPr = para.Element(A + "pPr");
            int level = Math.Clamp((int)(Long(pPr, "lvl") ?? 0), 0, 8);
            var props = new ParaProps();
            for (int i = 0; i < lists.Count; i++)
            {
                // The theme style's text colour beats the master's defaults but not the shape's own list styles.
                if (i == lists.Count - bodies.Length)
                {
                    if (styleColor != null) props.Run.Color = styleColor;
                    if (styleFont != null) props.Run.Font = styleFont;
                }
                ApplyPara(props, lists[i]?.Element(A + "defPPr"), ctx);
                ApplyPara(props, lists[i]?.Element(A + $"lvl{level + 1}pPr"), ctx);
            }
            ApplyPara(props, pPr, ctx);
            shape.Text.Add(BuildParagraph(para, props, level, counters, fontScale, lineReduction, ctx));
        }
    }

    DParagraph BuildParagraph(XElement para, ParaProps props, int level, Dictionary<int, (string type, int n)> counters,
        double fontScale, double lineReduction, Context ctx)
    {
        double baseSize = (props.Run.Size ?? 18) * fontScale;
        var p = new DParagraph
        {
            Align = props.Align switch { "ctr" => TextAlignment.Center, "r" => TextAlignment.Right, "just" or "dist" => TextAlignment.Justify, _ => TextAlignment.Left },
            IndentLeft = props.MarginLeft ?? 0,
            IndentFirst = props.Indent ?? 0,
            ListLevel = level,
            SpaceBefore = props.BeforePts ?? (props.BeforePct ?? 0) * baseSize * 1.2,
            SpaceAfter = props.AfterPts ?? (props.AfterPct ?? 0) * baseSize * 1.2,
        };
        if (props.LinePct is { } pct) p.LineSpacing = Math.Max(0.5, pct * (1 - lineReduction));
        else if (props.LinePts is { } pts) p.LineSpacing = Math.Max(0.5, pts / (baseSize * 1.2));
        else if (lineReduction > 0) p.LineSpacing = 1 - lineReduction;

        foreach (var el in para.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "r" or "fld":
                    var text = (string?)el.Element(A + "t") ?? "";
                    if (el.Name.LocalName == "fld" && Attr(el, "type") == "slidenum") text = ctx.SlideNumber.ToString(CultureInfo.InvariantCulture);
                    if (text.Length == 0) break;
                    var run = props.Run.Clone();
                    var rPr = el.Element(A + "rPr");
                    ApplyRun(run, rPr, ctx);
                    string? link = null;
                    if (rPr?.Element(A + "hlinkClick") is { } click)
                    {
                        // Jumps to other slides look like links too, though only web links stay clickable.
                        if (Attr(click, R + "id") is { } rid && ctx.Part.Rels.TryGetValue(rid, out var rel) && rel.External) link = rel.Target;
                        if (rPr?.Element(A + "solidFill") == null) run.Color = Scheme("hlink", ctx, null);
                        run.Underline ??= true;
                    }
                    if (run.Caps == true) text = text.ToUpperInvariant();
                    p.Inlines.Add(new DText(text, Style(run, fontScale), link));
                    break;
                case "br":
                    p.Inlines.Add(new DLineBreak());
                    break;
            }
        }

        var end = props.Run.Clone();
        ApplyRun(end, para.Element(A + "endParaRPr"), ctx);
        p.MarkSize = (end.Size ?? 18) * fontScale;

        foreach (var deeper in counters.Keys.Where(k => k > level).ToList()) counters.Remove(deeper);
        bool hasText = p.Inlines.OfType<DText>().Any(t => t.Text.Trim().Length > 0);
        if (props.Bullet == 2 && hasText)
        {
            var type = props.NumberType ?? "arabicPeriod";
            int n = counters.TryGetValue(level, out var c) && c.type == type ? c.n + 1 : props.NumberStart;
            counters[level] = (type, n);
            p.List = ListKind.Number;
            p.ListLabel = NumberLabel(type, n);
        }
        else
        {
            if (hasText) counters.Remove(level);
            if (props.Bullet == 1 && hasText && !string.IsNullOrEmpty(props.BulletChar))
            {
                p.List = ListKind.Bullet;
                (p.ListLabel, p.ListFont) = BulletGlyph(props.BulletChar, props.BulletFont);
            }
        }
        if (p.List != ListKind.None)
        {
            p.ListColor = props.BulletColor;
            p.ListSize = props.BulletSize;
            if (p.IndentFirst >= 0) p.IndentFirst = -Math.Max(12, baseSize * 0.9); // no hanging room set: give the marker some
        }
        return p;
    }

    static TextStyle Style(RunProps r, double scale) => new()
    {
        Bold = r.Bold == true, Italic = r.Italic == true, Underline = r.Underline == true, Strike = r.Strike == true,
        Superscript = r.Baseline > 0, Subscript = r.Baseline < 0,
        Size = (r.Size ?? 18) * scale, Color = r.Color ?? Colors.Black, Font = r.Font,
    };

    static string NumberLabel(string type, int n)
    {
        bool upper = type.Contains("Uc");
        var core = type.StartsWith("roman") ? ListLabels.Format(n, upper ? "upperRoman" : "lowerRoman")
            : type.StartsWith("alpha") ? ListLabels.Format(n, upper ? "upperLetter" : "lowerLetter")
            : n.ToString(CultureInfo.InvariantCulture);
        return type.EndsWith("ParenBoth") ? $"({core})" : type.EndsWith("ParenR") ? core + ")" : type.EndsWith("Plain") ? core : core + ".";
    }

    /// <summary>Symbol-font bullets as the Unicode characters they look like.</summary>
    static (string label, string? font) BulletGlyph(string ch, string? font)
    {
        if (font is "Wingdings" or "Wingdings 2" or "Wingdings 3" or "Symbol")
        {
            var mapped = (font, ch) switch
            {
                ("Wingdings", "§") => "▪", ("Wingdings", "Ø") => "➢", ("Wingdings", "ü") => "✓", ("Wingdings", "q") => "❑",
                ("Wingdings", "v") => "❖", ("Wingdings", "n") => "■", ("Wingdings", "l") => "●", ("Wingdings", "ú") => "▪",
                ("Wingdings", "à") => "→", ("Wingdings", "Ü") => "➢", ("Wingdings", "w") => "⬥", ("Symbol", "·") => "•",
                _ => null,
            };
            return mapped != null ? (mapped, "Segoe UI Symbol") : ("•", null);
        }
        return (ch, font is null or "Arial" or "Calibri" ? null : font);
    }

    void ApplyPara(ParaProps p, XElement? el, Context ctx)
    {
        if (el == null) return;
        if (Attr(el, "algn") is { } algn) p.Align = algn;
        if (Long(el, "marL") is { } marL) p.MarginLeft = marL / Emu;
        if (Long(el, "indent") is { } indent) p.Indent = indent / Emu;
        if (el.Element(A + "lnSpc") is { } ln)
        {
            if (Long(ln.Element(A + "spcPct"), "val") is { } pct) { p.LinePct = pct / 100000.0; p.LinePts = null; }
            else if (Long(ln.Element(A + "spcPts"), "val") is { } pts) { p.LinePts = pts / 100.0; p.LinePct = null; }
        }
        if (el.Element(A + "spcBef") is { } bef)
        {
            p.BeforePts = Long(bef.Element(A + "spcPts"), "val") / 100.0;
            p.BeforePct = Long(bef.Element(A + "spcPct"), "val") / 100000.0;
        }
        if (el.Element(A + "spcAft") is { } aft)
        {
            p.AfterPts = Long(aft.Element(A + "spcPts"), "val") / 100.0;
            p.AfterPct = Long(aft.Element(A + "spcPct"), "val") / 100000.0;
        }
        if (el.Element(A + "buNone") != null) p.Bullet = 0;
        if (el.Element(A + "buChar") is { } bu) { p.Bullet = 1; p.BulletChar = Attr(bu, "char"); }
        if (el.Element(A + "buAutoNum") is { } num)
        {
            p.Bullet = 2;
            p.NumberType = Attr(num, "type");
            p.NumberStart = (int)(Long(num, "startAt") ?? 1);
        }
        if (el.Element(A + "buFont") is { } buFont) p.BulletFont = FontName(Attr(buFont, "typeface"), ctx);
        if (el.Element(A + "buFontTx") != null) p.BulletFont = null;
        if (el.Element(A + "buClr") is { } buClr) p.BulletColor = ColorOf(buClr, ctx);
        if (el.Element(A + "buClrTx") != null) p.BulletColor = null;
        if (Long(el.Element(A + "buSzPct"), "val") is { } sz) p.BulletSize = sz / 100000.0;
        ApplyRun(p.Run, el.Element(A + "defRPr"), ctx);
    }

    void ApplyRun(RunProps r, XElement? el, Context ctx)
    {
        if (el == null) return;
        if (Long(el, "sz") is { } sz) r.Size = sz / 100.0;
        if (Attr(el, "b") is { } b) r.Bold = b is "1" or "true";
        if (Attr(el, "i") is { } i) r.Italic = i is "1" or "true";
        if (Attr(el, "u") is { } u) r.Underline = u != "none";
        if (Attr(el, "strike") is { } s) r.Strike = s != "noStrike";
        if (Attr(el, "cap") is { } cap) r.Caps = cap == "all";
        if (Long(el, "baseline") is { } baseline) r.Baseline = Math.Sign(baseline);
        if (el.Element(A + "solidFill") is { } fill && ColorOf(fill, ctx) is { } color) r.Color = color;
        else if (el.Element(A + "gradFill") is { } grad && ColorOf(grad.Element(A + "gsLst")?.Element(A + "gs"), ctx) is { } first) r.Color = first;
        if (FontName(Attr(el.Element(A + "latin"), "typeface"), ctx) is { } font) r.Font = font;
    }

    static string? FontName(string? typeface, Context ctx) => typeface switch
    {
        null or "" => null,
        _ when typeface.StartsWith("+mj") => ctx.Theme.Major,
        _ when typeface.StartsWith("+mn") => ctx.Theme.Minor,
        _ => typeface,
    };

    // ------------------------------------------------------------------ tables

    DTable ReadTable(XElement tbl, Context ctx)
    {
        var table = new DTable { Borders = false, CellPadding = new Thickness(7.2, 3.6, 7.2, 3.6) };
        table.ColumnWidths = tbl.Element(A + "tblGrid")?.Elements(A + "gridCol").Select(c => (Long(c, "w") ?? 0) / Emu).ToList();
        var tblPr = tbl.Element(A + "tblPr");
        bool On(string name) => Attr(tblPr, name) is "1" or "true";
        bool firstRow = On("firstRow"), lastRow = On("lastRow"), firstCol = On("firstCol"), lastCol = On("lastCol");
        bool bandRow = On("bandRow"), bandCol = On("bandCol");

        // The look comes from a table style saved in the deck (tableStyles.xml), e.g. "Medium Style 2 - Accent 1".
        var styleId = (string?)tblPr?.Element(A + "tableStyleId");
        var style = TableStyle(styleId);
        var gridColor = ColorOf(style?.Element(A + "wholeTbl")?.Element(A + "tcStyle")?.Element(A + "tcBdr")?.Element(A + "insideH")?.Element(A + "ln")?.Element(A + "solidFill"), ctx);
        if (gridColor != null)
        {
            table.Borders = true;
            table.BorderColor = gridColor;
        }

        var rows = tbl.Elements(A + "tr").ToList();
        int columns = table.ColumnWidths?.Count ?? 0;
        for (int r = 0; r < rows.Count; r++)
        {
            var tr = rows[r];
            var row = new DRow();
            int c = 0;
            foreach (var tc in tr.Elements(A + "tc"))
            {
                int col = c++;
                if (Attr(tc, "hMerge") is "1" or "true") continue;
                var cell = new DCell { ColSpan = (int)(Long(tc, "gridSpan") ?? 1) };

                // Style parts in the order PowerPoint layers them; later parts win.
                var parts = new List<string> { "wholeTbl" };
                int dataRow = r - (firstRow ? 1 : 0), dataCol = col - (firstCol ? 1 : 0);
                if (bandRow && dataRow >= 0) parts.Add(dataRow % 2 == 0 ? "band1H" : "band2H");
                if (bandCol && dataCol >= 0) parts.Add(dataCol % 2 == 0 ? "band1V" : "band2V");
                if (lastCol && col == columns - 1) parts.Add("lastCol");
                if (firstCol && col == 0) parts.Add("firstCol");
                if (lastRow && r == rows.Count - 1) parts.Add("lastRow");
                if (firstRow && r == 0) parts.Add("firstRow");

                var run = new RunProps { Font = ctx.Theme.Minor, Color = Scheme("tx1", ctx, null), Size = 18 };
                foreach (var part in parts.Select(p => style?.Element(A + p)).Where(p => p != null))
                {
                    var tcStyle = part!.Element(A + "tcStyle");
                    if (tcStyle?.Element(A + "fill") is { } partFill && FillOf(partFill, ctx) is { has: true } f) cell.Shading = f.fill?.Average;
                    else if (tcStyle?.Element(A + "fillRef") is { } fillRef && StyleFill(new XElement("s", fillRef), ctx) is { } sf) cell.Shading = sf.Average;
                    if (part.Element(A + "tcTxStyle") is { } tx)
                    {
                        if (Attr(tx, "b") is { } b) run.Bold = b == "on";
                        if (Attr(tx, "i") is { } i) run.Italic = i == "on";
                        if (ColorOf(tx, ctx) is { } color) run.Color = color;
                    }
                }
                if (style == null && styleId != null && firstRow && r == 0)
                {
                    // A built-in style the deck didn't save: the usual accent header row.
                    cell.Shading = Scheme("accent1", ctx, null);
                    run.Color = Colors.White;
                    run.Bold = true;
                }

                var tcPr = tc.Element(A + "tcPr");
                if (FillOf(tcPr, ctx) is { has: true } own) cell.Shading = own.fill?.Average;
                if (Attr(tc, "vMerge") is not ("1" or "true") && tc.Element(A + "txBody") is { } body)
                {
                    var counters = new Dictionary<int, (string, int)>();
                    foreach (var para in body.Elements(A + "p"))
                    {
                        var pp = new ParaProps();
                        ApplyPara(pp, _defaultTextStyle?.Element(A + "lvl1pPr"), ctx);
                        pp.Run = Merge(pp.Run, run);
                        ApplyPara(pp, para.Element(A + "pPr"), ctx);
                        cell.Blocks.Add(BuildParagraph(para, pp, 0, counters, 1, 0, ctx));
                    }
                }
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new DParagraph());
                row.Cells.Add(cell);
            }

            // Rows keep at least their saved height (extra space falls below the text, as in PowerPoint).
            if ((Long(tr, "h") ?? 0) / Emu is var minHeight and > 0 && row.Cells.FirstOrDefault()?.Blocks.OfType<DParagraph>().ToList() is { Count: > 0 } paras)
            {
                double natural = 7.2 + paras.Sum(p => p.Inlines.OfType<DText>().Select(t => t.Style.Size ?? 18).DefaultIfEmpty(p.MarkSize ?? 18).Max() * 1.2 + p.SpaceBefore + p.SpaceAfter);
                if (minHeight > natural + 1) paras[^1].SpaceAfter += minHeight - natural;
            }
            table.Rows.Add(row);
        }
        return table;
    }

    /// <summary>Text defaults from a table style under the paragraph's inherited ones.</summary>
    static RunProps Merge(RunProps under, RunProps over)
    {
        var r = under.Clone();
        r.Font = over.Font ?? r.Font;
        r.Color = over.Color ?? r.Color;
        r.Bold = over.Bold ?? r.Bold;
        r.Italic = over.Italic ?? r.Italic;
        return r;
    }

    XElement? TableStyle(string? id)
    {
        if (id == null) return null;
        var styles = LoadPart("ppt/tableStyles.xml");
        return styles?.Root.Elements(A + "tblStyle").FirstOrDefault(s => Attr(s, "styleId") == id);
    }

    // ------------------------------------------------------------------ geometry, fills and lines

    static void Geometry(XElement? spPr, SlideShape shape)
    {
        if (spPr?.Element(A + "custGeom") is { } cust)
        {
            shape.Geometry = ShapeGeometry.Custom;
            shape.Path = CustomPath(cust, shape.Bounds.Width, shape.Bounds.Height);
            if (shape.Path == null) shape.Geometry = ShapeGeometry.Rect;
            return;
        }
        var prst = spPr?.Element(A + "prstGeom");
        var name = Attr(prst, "prst") ?? "rect";
        shape.Geometry = Preset(name);
        if (name == "flowChartTerminator") shape.Adjust = 0.5;
        var adj = prst?.Element(A + "avLst")?.Elements(A + "gd").FirstOrDefault(g => Attr(g, "name") is "adj" or "adj1");
        if (adj != null && Attr(adj, "fmla") is { } fmla && fmla.StartsWith("val ") && double.TryParse(fmla[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            shape.Adjust = v / 100000.0;
    }

    /// <summary>PowerPoint's preset shape names (also used by LibreOffice as "ooxml-…").</summary>
    internal static ShapeGeometry Preset(string name) => name switch
    {
        "roundRect" or "round1Rect" or "round2SameRect" or "round2DiagRect" or "snipRoundRect" or "flowChartAlternateProcess" or "flowChartTerminator" => ShapeGeometry.RoundRect,
        "ellipse" or "flowChartConnector" or "donut" or "cloud" or "pie" or "chord" => ShapeGeometry.Ellipse,
        "triangle" or "flowChartExtract" => ShapeGeometry.Triangle,
        "rtTriangle" => ShapeGeometry.RightTriangle,
        "diamond" or "flowChartDecision" => ShapeGeometry.Diamond,
        "parallelogram" or "flowChartInputOutput" => ShapeGeometry.Parallelogram,
        "trapezoid" or "flowChartManualOperation" => ShapeGeometry.Trapezoid,
        "homePlate" or "pentagon" => ShapeGeometry.HomePlate,
        "hexagon" => ShapeGeometry.Hexagon,
        "octagon" => ShapeGeometry.Octagon,
        "chevron" => ShapeGeometry.Chevron,
        "rightArrow" or "notchedRightArrow" or "stripedRightArrow" => ShapeGeometry.RightArrow,
        "leftArrow" => ShapeGeometry.LeftArrow,
        "upArrow" => ShapeGeometry.UpArrow,
        "downArrow" => ShapeGeometry.DownArrow,
        "star5" or "star4" or "star6" or "star7" or "star8" or "star10" or "star12" => ShapeGeometry.Star5,
        "plus" or "mathPlus" => ShapeGeometry.Plus,
        "line" or "straightConnector1" => ShapeGeometry.Line,
        "bentConnector3" or "bentConnector4" or "bentConnector5" => ShapeGeometry.Elbow,
        "bentConnector2" => ShapeGeometry.Corner,
        "curvedConnector2" or "curvedConnector3" or "curvedConnector4" or "curvedConnector5" => ShapeGeometry.Curve,
        "regularPentagon" => ShapeGeometry.Pentagon,
        _ => ShapeGeometry.Rect,
    };

    /// <summary>A freeform outline, scaled from its own coordinate space to the shape's size in points.</summary>
    static Geometry? CustomPath(XElement custGeom, double width, double height)
    {
        var geometry = new PathGeometry();
        foreach (var path in custGeom.Element(A + "pathLst")?.Elements(A + "path") ?? [])
        {
            double pw = Long(path, "w") ?? 0, ph = Long(path, "h") ?? 0;
            double sx = pw > 0 ? width / pw : 1 / Emu, sy = ph > 0 ? height / ph : 1 / Emu;
            PathFigure? figure = null;
            var current = new Point();
            Point Pt(XElement? pt)
            {
                if (Long(pt, "x") is not { } x || Long(pt, "y") is not { } y) throw new FormatException("Guide-based path");
                return new Point(x * sx, y * sy);
            }
            try
            {
                foreach (var cmd in path.Elements())
                {
                    switch (cmd.Name.LocalName)
                    {
                        case "moveTo":
                            figure = new PathFigure { StartPoint = current = Pt(cmd.Element(A + "pt")), IsFilled = Attr(path, "fill") != "none" };
                            geometry.Figures.Add(figure);
                            break;
                        case "lnTo" when figure != null:
                            figure.Segments.Add(new LineSegment(current = Pt(cmd.Element(A + "pt")), true));
                            break;
                        case "cubicBezTo" when figure != null:
                            var c = cmd.Elements(A + "pt").Select(Pt).ToList();
                            if (c.Count == 3) figure.Segments.Add(new BezierSegment(c[0], c[1], current = c[2], true));
                            break;
                        case "quadBezTo" when figure != null:
                            var q = cmd.Elements(A + "pt").Select(Pt).ToList();
                            if (q.Count == 2) figure.Segments.Add(new QuadraticBezierSegment(q[0], current = q[1], true));
                            break;
                        case "arcTo" when figure != null:
                            double wr = (Long(cmd, "wR") ?? 0) * sx, hr = (Long(cmd, "hR") ?? 0) * sy;
                            double st = (Long(cmd, "stAng") ?? 0) / 60000.0 * Math.PI / 180, sw = (Long(cmd, "swAng") ?? 0) / 60000.0 * Math.PI / 180;
                            var center = new Point(current.X - wr * Math.Cos(st), current.Y - hr * Math.Sin(st));
                            var endPoint = new Point(center.X + wr * Math.Cos(st + sw), center.Y + hr * Math.Sin(st + sw));
                            figure.Segments.Add(new ArcSegment(endPoint, new Size(wr, hr), 0, Math.Abs(sw) > Math.PI,
                                sw > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
                            current = endPoint;
                            break;
                        case "close" when figure != null:
                            figure.IsClosed = true;
                            break;
                    }
                }
            }
            catch (FormatException) { return null; }
        }
        return geometry.Figures.Count > 0 ? geometry : null;
    }

    /// <returns>has: whether a fill (including "no fill") was specified at all.</returns>
    (bool has, SlideFill? fill) FillOf(XElement? holder, Context ctx, Color? placeholderColor = null)
    {
        if (holder == null) return (false, null);
        foreach (var el in holder.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "noFill":
                    return (true, null);
                case "solidFill":
                    return (true, ColorOf(el, ctx, placeholderColor) is { } c ? SlideFill.Solid(c) : null);
                case "gradFill":
                    var stops = el.Element(A + "gsLst")?.Elements(A + "gs")
                        .Select(gs => (pos: (Long(gs, "pos") ?? 0) / 100000.0, color: ColorOf(gs, ctx, placeholderColor)))
                        .Where(s => s.color != null).Select(s => (s.pos, s.color!.Value)).OrderBy(s => s.pos).ToList();
                    if (stops is not { Count: > 0 }) return (true, null);
                    return (true, new SlideFill
                    {
                        Gradient = stops,
                        GradientAngle = (Long(el.Element(A + "lin"), "ang") ?? 5400000) / 60000.0,
                        RadialGradient = el.Element(A + "path") != null,
                    });
                case "blipFill":
                    if (Blip(el.Element(A + "blip"), ctx, placeholderColor) is not { } image) return (true, null);
                    var tile = el.Element(A + "tile");
                    return (true, new SlideFill
                    {
                        Image = image,
                        Tile = tile != null ? (Long(tile, "sx") ?? 100000) / 100000.0 : null,
                        Opacity = (Long(el.Element(A + "blip")?.Element(A + "alphaModFix"), "amt") ?? 100000) / 100000.0,
                    });
                case "grpFill":
                    return (true, ctx.GroupFill);
                case "pattFill":
                    return (true, ColorOf(el.Element(A + "fgClr"), ctx, placeholderColor) is { } fg ? SlideFill.Solid(fg) : null);
            }
        }
        return (false, null);
    }

    /// <summary>A shape's fill from the theme's style list ("fillRef"), tinted with the colour it carries.</summary>
    SlideFill? StyleFill(XElement? style, Context ctx)
    {
        var fillRef = style?.Element(A + "fillRef");
        int idx = (int)(Long(fillRef, "idx") ?? 0);
        if (idx <= 0) return null;
        var color = ColorOf(fillRef, ctx);
        var list = ctx.Theme.Formats?.Element(A + "fillStyleLst")?.Elements().ToList();
        if (list != null && idx <= list.Count)
        {
            var wrapper = new XElement("w", list[idx - 1]);
            var (has, fill) = FillOf(wrapper, ThemeContext(ctx), color);
            if (has) return fill;
        }
        return color is { } c ? SlideFill.Solid(c) : null;
    }

    void ReadLine(XElement? ln, XElement? style, Context ctx, SlideShape shape)
    {
        Color? color = null;
        bool specified = false;
        if (ln != null)
        {
            if (ln.Element(A + "noFill") != null) specified = true;
            else if (ln.Element(A + "solidFill") is { } solid) { color = ColorOf(solid, ctx); specified = true; }
            else if (ln.Element(A + "gradFill") is { } grad) { color = ColorOf(grad.Descendants(A + "gs").FirstOrDefault(), ctx); specified = true; }
            if (Long(ln, "w") is { } w) shape.LineWidth = w / Emu;
            shape.Dashed = Attr(ln.Element(A + "prstDash"), "val") is { } dash && dash != "solid";
            shape.HeadEnd = Attr(ln.Element(A + "headEnd"), "type") is { } h && h != "none" ? ArrowHead.Triangle : ArrowHead.None;
            shape.TailEnd = Attr(ln.Element(A + "tailEnd"), "type") is { } t && t != "none" ? ArrowHead.Triangle : ArrowHead.None;
        }
        if (!specified)
        {
            var lnRef = style?.Element(A + "lnRef");
            int idx = (int)(Long(lnRef, "idx") ?? 0);
            if (idx > 0)
            {
                color = ColorOf(lnRef, ctx);
                var themeLine = ctx.Theme.Formats?.Element(A + "lnStyleLst")?.Elements(A + "ln").ElementAtOrDefault(idx - 1);
                if (ln?.Attribute("w") == null && Long(themeLine, "w") is { } w) shape.LineWidth = w / Emu;
            }
        }
        shape.Line = color;
    }

    SlideFill? Background(Context ctx)
    {
        foreach (var part in new[] { ctx.Part, ctx.Layout, ctx.Master })
        {
            var bg = part?.Root.Element(P + "cSld")?.Element(P + "bg");
            if (bg == null) continue;
            var partCtx = Inherit(ctx, part!);
            if (bg.Element(P + "bgPr") is { } bgPr)
            {
                var (has, fill) = FillOf(bgPr, partCtx);
                if (has) return fill ?? SlideFill.Solid(Colors.White);
            }
            if (bg.Element(P + "bgRef") is { } bgRef)
            {
                var color = ColorOf(bgRef, partCtx);
                int idx = (int)(Long(bgRef, "idx") ?? 0);
                var list = ctx.Theme.Formats?.Element(A + "bgFillStyleLst")?.Elements().ToList();
                if (idx > 1000 && list != null && idx - 1001 < list.Count)
                {
                    var (has, fill) = FillOf(new XElement("w", list[idx - 1001]), ThemeContext(partCtx), color);
                    if (has && fill != null) return fill;
                }
                if (color is { } c) return SlideFill.Solid(c);
            }
        }
        return SlideFill.Solid(Scheme("bg1", ctx, null) ?? Colors.White);
    }

    DImage? Blip(XElement? blip, Context ctx, Color? placeholderColor = null)
    {
        if (Attr(blip, R + "embed") is not { } rid || !ctx.Part.Rels.TryGetValue(rid, out var rel) || rel.External) return null;
        var entry = _zip.GetEntry(rel.Target);
        if (entry == null) return null;
        using var stream = PackageResources.Open(entry, media: true);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();
        var ext = Path.GetExtension(rel.Target).TrimStart('.').ToLowerInvariant();
        if (Recolor(data, blip!, ctx, placeholderColor) is { } recolored) (data, ext) = (recolored, "png");
        var (w, h) = DocxReader.ImageSize(data);
        return new DImage(data, ext, w, h);
    }

    /// <summary>Picture effects PowerPoint applies when drawing: duotone (common on theme backgrounds), greyscale, brightness and contrast.</summary>
    byte[]? Recolor(byte[] data, XElement blip, Context ctx, Color? placeholderColor)
    {
        var duotone = blip.Element(A + "duotone")?.Elements().Select(e => ColorOf(new XElement("w", e), ctx, placeholderColor)).ToList();
        bool gray = blip.Element(A + "grayscl") != null;
        var lum = blip.Element(A + "lum");
        double bright = (Long(lum, "bright") ?? 0) / 100000.0, contrast = (Long(lum, "contrast") ?? 0) / 100000.0;
        bool twoTone = duotone is { Count: 2 } && duotone[0] != null && duotone[1] != null;
        if (!twoTone && !gray && bright == 0 && contrast == 0) return null;
        if (FlowBridge.Decode(data) is not { } source) return null;

        var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var px = new byte[width * height * 4];
        bitmap.CopyPixels(px, width * 4, 0);
        Color dark = twoTone ? duotone![0]!.Value : Colors.Black, light = twoTone ? duotone![1]!.Value : Colors.White;
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            if (bright != 0 || contrast != 0)
            {
                double Adjust(double v) => Math.Clamp((v - 0.5) * (1 + contrast) + 0.5 + bright, 0, 1);
                (r, g, b) = (Adjust(r), Adjust(g), Adjust(b));
            }
            if (twoTone || gray)
            {
                double l = 0.299 * r + 0.587 * g + 0.114 * b;
                r = (dark.R + (light.R - dark.R) * l) / 255.0;
                g = (dark.G + (light.G - dark.G) * l) / 255.0;
                b = (dark.B + (light.B - dark.B) * l) / 255.0;
            }
            px[i] = (byte)(b * 255); px[i + 1] = (byte)(g * 255); px[i + 2] = (byte)(r * 255);
        }
        var result = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, px, width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(result));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    static Thickness Crop(XElement? srcRect) => srcRect == null ? default : new Thickness(
        (Long(srcRect, "l") ?? 0) / 100000.0, (Long(srcRect, "t") ?? 0) / 100000.0,
        (Long(srcRect, "r") ?? 0) / 100000.0, (Long(srcRect, "b") ?? 0) / 100000.0);

    // ------------------------------------------------------------------ colours

    Color? ColorOf(XElement? holder, Context ctx, Color? placeholderColor = null)
    {
        if (holder == null) return null;
        foreach (var el in holder.Elements())
        {
            Color? color = el.Name.LocalName switch
            {
                "srgbClr" => Units.ParseHex(Attr(el, "val")),
                "sysClr" => Units.ParseHex(Attr(el, "lastClr")) ?? (Attr(el, "val") == "windowText" ? Colors.Black : Colors.White),
                "schemeClr" => Scheme(Attr(el, "val"), ctx, placeholderColor),
                "prstClr" => Attr(el, "val") is { } name && ColorConverter.ConvertFromString(name) is Color named ? named : null,
                "scrgbClr" => Color.FromRgb(Linear(Long(el, "r")), Linear(Long(el, "g")), Linear(Long(el, "b"))),
                "hslClr" => FromHsl((Long(el, "hue") ?? 0) / 60000.0, (Long(el, "sat") ?? 0) / 100000.0, (Long(el, "lum") ?? 0) / 100000.0),
                _ => null,
            };
            if (color is { } c) return Modify(c, el);
        }
        return null;

        static byte Linear(long? v) => (byte)Math.Round(255 * Math.Pow(Math.Clamp((v ?? 0) / 100000.0, 0, 1), 1 / 2.2));
    }

    static Color? Scheme(string? name, Context ctx, Color? placeholderColor)
    {
        if (name == null) return null;
        if (name == "phClr") return placeholderColor ?? ctx.Theme.Colors.GetValueOrDefault("accent1");
        if (ctx.ColorMap.TryGetValue(name, out var mapped)) name = mapped;
        return ctx.Theme.Colors.TryGetValue(name, out var c) ? c : null;
    }

    /// <summary>Applies DrawingML colour transforms (tints, shades, luminance, alpha).</summary>
    static Color Modify(Color c, XElement el)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, a = c.A / 255.0;
        foreach (var m in el.Elements())
        {
            double v = (Long(m, "val") ?? 0) / 100000.0;
            switch (m.Name.LocalName)
            {
                // Tints and shades work in linear light, which is why a 75% tint of black is a mid grey.
                case "tint": r = Gamma(1 - (1 - Linear(r)) * v); g = Gamma(1 - (1 - Linear(g)) * v); b = Gamma(1 - (1 - Linear(b)) * v); break;
                case "shade": r = Gamma(Linear(r) * v); g = Gamma(Linear(g) * v); b = Gamma(Linear(b) * v); break;
                case "alpha": a = v; break;
                case "alphaMod": a *= v; break;
                case "alphaOff": a += v; break;
                case "lumMod" or "lumOff" or "satMod" or "satOff" or "hueMod" or "hueOff":
                    var (h, s, l) = ToHsl(r, g, b);
                    switch (m.Name.LocalName)
                    {
                        case "lumMod": l *= v; break;
                        case "lumOff": l += v; break;
                        case "satMod": s *= v; break;
                        case "satOff": s += v; break;
                        case "hueMod": h *= v; break;
                        case "hueOff": h += v * 100000 / 60000; break;
                    }
                    h = (h % 360 + 360) % 360;
                    var back = FromHsl(h, Math.Clamp(s, 0, 1), Math.Clamp(l, 0, 1));
                    (r, g, b) = (back.R / 255.0, back.G / 255.0, back.B / 255.0);
                    break;
            }
        }
        static byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        return Color.FromArgb(B(a), B(r), B(g), B(b));
    }

    static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    static double Gamma(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    static (double h, double s, double l) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        double d = max - min, s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    // ------------------------------------------------------------------ package

    Theme ThemeOf(Part master)
    {
        var part = Related(master, "/theme");
        if (part == null) return new Theme();
        if (_themes.TryGetValue(part.Path, out var cached)) return cached;
        var theme = new Theme();
        var elements = part.Root.Element(A + "themeElements");
        foreach (var c in elements?.Element(A + "clrScheme")?.Elements() ?? [])
        {
            var value = c.Element(A + "srgbClr") is { } srgb ? Units.ParseHex(Attr(srgb, "val"))
                : c.Element(A + "sysClr") is { } sys ? Units.ParseHex(Attr(sys, "lastClr")) : null;
            if (value is { } v) theme.Colors[c.Name.LocalName] = v;
        }
        var fonts = elements?.Element(A + "fontScheme");
        if (Attr(fonts?.Element(A + "majorFont")?.Element(A + "latin"), "typeface") is { Length: > 0 } major) theme.Major = major;
        if (Attr(fonts?.Element(A + "minorFont")?.Element(A + "latin"), "typeface") is { Length: > 0 } minor) theme.Minor = minor;
        theme.Formats = elements?.Element(A + "fmtScheme");
        theme.Part = part;
        _themes[part.Path] = theme;
        return theme;
    }

    Part? Related(Part part, string typeSuffix)
    {
        var rel = part.Rels.Values.FirstOrDefault(r => r.Type.EndsWith(typeSuffix) && !r.External);
        return rel != null ? LoadPart(rel.Target) : null;
    }

    Part? LoadPart(string path)
    {
        if (_parts.TryGetValue(path, out var cached)) return cached;
        Part? part = null;
        if (_zip.GetEntry(path) is { } entry)
        {
            using var stream = PackageResources.Open(entry);
            var root = PackageResources.LoadXml(stream).Root!;
            part = new Part(path, root, LoadRels(path));
        }
        _parts[path] = part;
        return part;
    }

    Dictionary<string, Rel> LoadRels(string partPath)
    {
        var rels = new Dictionary<string, Rel>();
        var dir = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? "";
        var relsPath = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + Path.GetFileName(partPath) + ".rels";
        if (_zip.GetEntry(relsPath) is not { } entry) return rels;
        using var stream = PackageResources.Open(entry);
        foreach (var rel in PackageResources.LoadXml(stream).Root!.Elements(PR + "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            if (id == null || target == null) continue;
            bool external = (string?)rel.Attribute("TargetMode") == "External";
            rels[id] = new Rel(external ? target : Resolve(dir, target), (string?)rel.Attribute("Type") ?? "", external);
        }
        return rels;
    }

    static string Resolve(string dir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var piece in Uri.UnescapeDataString(target).Split('/'))
        {
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (piece is not ("." or "")) parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    // ------------------------------------------------------------------ helpers

    static Rect Bounds(XElement xfrm)
    {
        var off = xfrm.Element(A + "off");
        var ext = xfrm.Element(A + "ext");
        return new Rect(Coord(off, "x"), Coord(off, "y"), Math.Max(0, Coord(ext, "cx")), Math.Max(0, Coord(ext, "cy")));
    }

    static Rect Map(Rect r, GroupMap? group) => group?.Map(r) ?? r;

    static double Coord(XElement? el, string name) => (Long(el, name) ?? 0) / Emu;

    /// <summary>A structural child in either the slide (p:) or the SmartArt drawing (dsp:) namespace.</summary>
    static XElement? Child(XElement? el, string localName) => el?.Element(P + localName) ?? el?.Element(Dsp + localName);

    static string? Attr(XElement? el, XName name) => (string?)el?.Attribute(name);

    static long? Long(XElement? el, XName name) =>
        long.TryParse((string?)el?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
