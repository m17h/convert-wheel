using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Reads OpenDocument presentations (.odp, from LibreOffice, Google Slides or PowerPoint): pages, master
/// decorations, shapes, frames, pictures, tables and styled text with bullets.
/// </summary>
internal sealed partial class OdpReader
{
    static readonly XNamespace Pres = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    readonly ZipArchive _zip;
    readonly CancellationToken _ct;
    readonly Dictionary<string, XElement> _styles = [];
    /// <summary>Styles only the master pages use; the slides may have others by the same name.</summary>
    readonly Dictionary<string, XElement> _masterStyles = [];
    readonly Dictionary<string, XElement> _listStyles = [];
    readonly Dictionary<string, XElement> _gradients = [];
    readonly Dictionary<string, XElement> _fillImages = [];
    readonly Dictionary<string, XElement> _opacities = [];
    readonly Dictionary<string, string> _fontFaces = [];
    readonly Dictionary<string, DImage?> _images = [];
    XElement? _defaultGraphic;
    int _pageNumber, _shapes;
    bool _onMaster;

    sealed class Run
    {
        public double Size = 18;
        public bool Bold, Italic, Underline, Strike, Caps;
        public int Position;
        public Color Color = Colors.Black;
        public string? Font;
        public Run Clone() => (Run)MemberwiseClone();
    }

    OdpReader(ZipArchive zip, CancellationToken ct) => (_zip, _ct) = (zip, ct);

    public static Deck Read(ZipArchive zip, string title, CancellationToken ct = default) => new OdpReader(zip, ct).Build(title);

    Deck Build(string title)
    {
        var content = Load("content.xml") ?? throw new ConversionException("This OpenDocument file has no content.");
        var styles = Load("styles.xml");
        foreach (var root in new[] { styles, content })
        {
            if (root == null) continue;
            foreach (var face in root.Descendants(St + "font-face"))
                if ((string?)face.Attribute(St + "name") is { } name)
                    _fontFaces[name] = ((string?)face.Attribute(Svg + "font-family") ?? name).Trim('\'', '"');
            foreach (var s in root.Descendants(St + "style"))
                if ((string?)s.Attribute(St + "name") is { } name)
                    (root == styles && s.Parent?.Name.LocalName == "automatic-styles" ? _masterStyles : _styles)[name] = s;
            foreach (var s in root.Descendants(Text + "list-style"))
                if ((string?)s.Attribute(St + "name") is { } name) _listStyles[name] = s;
            foreach (var g in root.Descendants(Draw + "gradient"))
                if ((string?)g.Attribute(Draw + "name") is { } name) _gradients[name] = g;
            foreach (var o in root.Descendants(Draw + "opacity"))
                if ((string?)o.Attribute(Draw + "name") is { } name) _opacities[name] = o;
            foreach (var f in root.Descendants(Draw + "fill-image"))
                if ((string?)f.Attribute(Draw + "name") is { } name) _fillImages[name] = f;
            _defaultGraphic ??= root.Descendants(St + "default-style").FirstOrDefault(d => (string?)d.Attribute(St + "family") == "graphic");
        }

        var deck = new Deck { Title = title };
        var defaultText = _defaultGraphic?.Element(St + "text-properties");
        deck.Font = FontOf(defaultText) ?? "Liberation Sans";

        var pages = content.Element(Office + "body")?.Element(Office + "presentation")?.Elements(Draw + "page").ToList()
            ?? throw new ConversionException("This isn't an OpenDocument presentation.");
        var masters = new Dictionary<string, XElement>();
        foreach (var m in styles?.Descendants(St + "master-page") ?? []) masters.TryAdd((string?)m.Attribute(St + "name") ?? "", m);
        // Pages have one size in every app: the one the first slide's master uses.
        masters.TryGetValue((string?)pages.FirstOrDefault()?.Attribute(Draw + "master-page-name") ?? "", out var firstMaster);
        var layoutName = (string?)(firstMaster ?? masters.Values.FirstOrDefault())?.Attribute(St + "page-layout-name");
        var layout = styles?.Descendants(St + "page-layout").FirstOrDefault(l => (string?)l.Attribute(St + "name") == layoutName)?.Element(St + "page-layout-properties");
        if (Len(layout, Fo + "page-width") is double w && Len(layout, Fo + "page-height") is double h && w > 36 && h > 36)
        {
            deck.Width = w;
            deck.Height = h;
        }
        else (deck.Width, deck.Height) = (792, 612);

        foreach (var page in pages)
        {
            _ct.ThrowIfCancellationRequested();
            SlideLimits.CheckSlides(++_pageNumber);
            masters.TryGetValue((string?)page.Attribute(Draw + "master-page-name") ?? "", out var master);
            master ??= masters.Values.FirstOrDefault();
            var background = Background(page);
            _onMaster = true;
            background ??= master != null ? Background(master) : null;
            _onMaster = false;
            var pageProps = Style((string?)page.Attribute(Draw + "style-name"))?.Element(St + "drawing-page-properties");
            var slide = new Slide
            {
                Background = background ?? SlideFill.Solid(Colors.White),
                Hidden = (string?)pageProps?.Attribute(Pres + "visibility") == "hidden",
            };
            bool showMaster = (string?)pageProps?.Attribute(Pres + "background-objects-visible") != "false";
            if (master != null && showMaster)
            {
                _onMaster = true;
                ReadShapes(master.Elements(), slide.Shapes, onMaster: true);
                _onMaster = false;
            }
            ReadShapes(page.Elements(), slide.Shapes, onMaster: false);
            foreach (var frame in page.Element(Pres + "notes")?.Elements(Draw + "frame").Where(f => (string?)f.Attribute(Pres + "class") == "notes") ?? [])
                foreach (var p in frame.Descendants().Where(e => e.Name == Text + "p" || e.Name == Text + "h"))
                    slide.Notes.Add(string.Concat(p.DescendantNodes().Select(n => n switch
                    {
                        XText t => t.Value,
                        XElement { Name.LocalName: "line-break" } => "\n",
                        XElement { Name.LocalName: "tab" } => "\t",
                        XElement { Name.LocalName: "s" } s => new string(' ', Units.OdfSpaceCount((string?)s.Attribute(Text + "c"))),
                        _ => "",
                    })));
            while (slide.Notes.Count > 0 && slide.Notes[^1].Length == 0) slide.Notes.RemoveAt(slide.Notes.Count - 1);
            deck.Slides.Add(slide);
        }
        if (deck.Slides.Count == 0) throw new ConversionException("This presentation has no slides.");
        return deck;
    }

    // ------------------------------------------------------------------ shapes

    void ReadShapes(IEnumerable<XElement> elements, List<SlideShape> into, bool onMaster)
    {
        foreach (var el in elements)
        {
            SlideLimits.CheckShapes(++_shapes);
            try
            {
                // Master pages hold placeholder prompts ("Click to add Title") that never show on slides.
                if (onMaster && ((string?)el.Attribute(Pres + "placeholder") == "true" || el.Attribute(Pres + "class") != null)) continue;
                if ((string?)el.Attribute(Pres + "placeholder") == "true" && !HasText(el)) continue;
                switch (el.Name.LocalName)
                {
                    case "g":
                        int start = into.Count;
                        ReadShapes(el.Elements(), into, onMaster);
                        if (TransformOf(el) is { } move)
                            for (int i = start; i < into.Count; i++) into[i].Transform(move.matrix);
                        break;
                    case "a": // a link around shapes
                        ReadShapes(el.Elements(), into, onMaster);
                        break;
                    case "frame":
                        ReadFrame(el, into);
                        break;
                    case "custom-shape" or "rect" or "ellipse" or "circle" or "polygon" or "polyline" or "path" or "regular-polygon" or "caption":
                        if (ReadShape(el) is { } shape) into.Add(shape);
                        break;
                    case "line" or "connector":
                        if (ReadLine(el) is { } line) into.Add(line);
                        break;
                }
            }
            catch (Exception ex) when (SlideLimits.CanSkip(ex))
            {
                Log.Write($"Skipped a shape on slide {_pageNumber}: {ex.Message}");
            }
        }
    }

    static bool HasText(XElement el) => el.Descendants(Text + "p").Any(p => p.Value.Trim().Length > 0) || el.Descendants(Draw + "image").Any();

    void ReadFrame(XElement frame, List<SlideShape> into)
    {
        var chain = Chain(frame);
        if (frame.Element(Table + "table") is { } table)
        {
            var shape = NewShape(frame);
            shape.Insets = new Thickness(0);
            shape.Text.Add(ReadTable(table, chain));
            into.Add(shape);
            return;
        }

        // Charts: drawn from their own data (the saved preview is often a format nothing else reads).
        if (frame.Element(Draw + "object") is { } obj && (string?)obj.Attribute(XLink + "href") is { } objectHref
            && Charts.ReadOdf(_zip, objectHref) is { } chart)
        {
            var shape = NewShape(frame);
            shape.Picture = Charts.ToImage(chart, shape.Bounds.Width, shape.Bounds.Height);
            into.Add(shape);
            return;
        }

        // Pictures (an SVG may come with a bitmap copy for older readers: prefer the bitmap).
        var images = frame.Elements(Draw + "image").ToList();
        var image = images.FirstOrDefault(i => !((string?)i.Attribute(XLink + "href") ?? "").EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) ?? images.FirstOrDefault();
        if (image != null && Picture((string?)image.Attribute(XLink + "href")) is { } picture)
        {
            var shape = NewShape(frame);
            shape.Picture = picture;
            PictureLook(shape, chain, picture);
            into.Add(shape);
            if (image.Elements(Text + "p").Any(p => p.Value.Trim().Length > 0))
            {
                var caption = NewShape(frame);
                ReadText(image, caption, chain, frame);
                into.Add(caption);
            }
            return;
        }

        if (frame.Element(Draw + "text-box") is { } box)
        {
            var shape = NewShape(frame);
            ApplyGraphic(shape, chain, isFrame: true);
            ReadText(box, shape, chain, frame);
            into.Add(shape);
        }
    }

    /// <summary>A picture's outline, mirroring, cropping and transparency.</summary>
    void PictureLook(SlideShape shape, List<XElement> chain, DImage picture)
    {
        ApplyStroke(shape, chain);
        var mirror = Graphic(chain, St + "mirror") ?? "";
        if (mirror.Contains("horizontal")) shape.FlipH ^= true;
        if (mirror.Contains("vertical")) shape.FlipV ^= true;
        if (Percent(Graphic(chain, Draw + "image-opacity")) is double opacity) shape.PictureOpacity = Math.Clamp(opacity, 0, 1);
        // Cropping is given as lengths of the picture at its own size.
        if (ClipRegex().Match(Graphic(chain, Fo + "clip") ?? "") is { Success: true } clip && picture.Width > 0 && picture.Height > 0)
        {
            double Side(int group, double size) => Math.Clamp((Units.ParseLength(clip.Groups[group].Value) ?? 0) / size, -100, 1);
            shape.Crop = new Thickness(Side(4, picture.Width), Side(1, picture.Height), Side(2, picture.Width), Side(3, picture.Height));
        }
    }

    [GeneratedRegex(@"rect\s*\(\s*([-+0-9.a-z]+)[\s,]+([-+0-9.a-z]+)[\s,]+([-+0-9.a-z]+)[\s,]+([-+0-9.a-z]+)\s*\)")]
    private static partial Regex ClipRegex();

    SlideShape? ReadShape(XElement el)
    {
        var chain = Chain(el);
        var shape = NewShape(el);
        switch (el.Name.LocalName)
        {
            case "ellipse" or "circle":
                shape.Geometry = ShapeGeometry.Ellipse;
                break;
            case "rect":
                if (Len(el, Draw + "corner-radius") is double radius && radius > 0)
                {
                    shape.Geometry = ShapeGeometry.RoundRect;
                    shape.Adjust = radius / Math.Max(0.01, Math.Min(shape.Bounds.Width, shape.Bounds.Height));
                }
                break;
            case "polygon" or "polyline":
                shape.Geometry = ShapeGeometry.Custom;
                shape.Path = Polygon(el, shape.Bounds, closed: el.Name.LocalName == "polygon");
                break;
            case "path":
                shape.Geometry = ShapeGeometry.Custom;
                shape.Path = SvgPath(el, shape.Bounds);
                break;
            case "custom-shape":
                Enhanced(el.Element(Draw + "enhanced-geometry"), shape);
                break;
        }
        if (shape.Geometry == ShapeGeometry.Custom && shape.Path == null) shape.Geometry = ShapeGeometry.Rect;
        ApplyGraphic(shape, chain, isFrame: false);
        if (shape.Geometry == ShapeGeometry.Custom && el.Name.LocalName == "polyline") shape.Fill = null;
        ReadText(el, shape, chain, el);
        return shape;
    }

    SlideShape? ReadLine(XElement el)
    {
        double x1 = Len(el, Svg + "x1") ?? 0, y1 = Len(el, Svg + "y1") ?? 0, x2 = Len(el, Svg + "x2") ?? 0, y2 = Len(el, Svg + "y2") ?? 0;
        var shape = new SlideShape
        {
            Bounds = new Rect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1)),
            FlipH = x2 < x1,
            FlipV = y2 < y1,
            Geometry = el.Name.LocalName == "connector" && (string?)el.Attribute(Draw + "type") is null or "standard" ? ShapeGeometry.Elbow : ShapeGeometry.Line,
        };
        var chain = Chain(el);
        ApplyStroke(shape, chain);
        shape.Line ??= Colors.Black;
        return shape;
    }

    /// <summary>Position, size and turn (ODF transforms the shape's box about its top-left corner).</summary>
    static SlideShape NewShape(XElement el)
    {
        double w = Len(el, Svg + "width") ?? 0, h = Len(el, Svg + "height") ?? 0;
        double x = Len(el, Svg + "x") ?? 0, y = Len(el, Svg + "y") ?? 0;
        var shape = new SlideShape { Bounds = new Rect(x, y, Math.Max(0, w), Math.Max(0, h)) };
        if (TransformOf(el) is not { } move) return shape;
        if (move.placed) shape.Bounds = new Rect(0, 0, shape.Bounds.Width, shape.Bounds.Height);
        else
        {
            // Turned without being moved: it stays where it was, about its centre.
            move.matrix.OffsetX = move.matrix.OffsetY = 0;
            move.matrix.TranslatePrepend(-(x + w / 2), -(y + h / 2));
            move.matrix.Translate(x + w / 2, y + h / 2);
        }
        shape.Transform(move.matrix);
        return shape;
    }

    /// <summary>An element's draw:transform, applied left to right; angles turn counter-clockwise.</summary>
    static (Matrix matrix, bool placed)? TransformOf(XElement el)
    {
        if ((string?)el.Attribute(Draw + "transform") is not { } text) return null;
        var matrix = Matrix.Identity;
        bool placed = false, any = false;
        foreach (Match step in TransformRegex().Matches(text))
        {
            var args = step.Groups[2].Value.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            double Number(int i) => i < args.Length && double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            double Length(int i) => i < args.Length ? Units.ParseLength(args[i]) ?? 0 : 0;
            switch (step.Groups[1].Value)
            {
                case "rotate": matrix.Rotate(-Number(0) * 180 / Math.PI); break;
                case "scale": matrix.Scale(Number(0), args.Length > 1 ? Number(1) : Number(0)); break;
                case "skewX": matrix.Skew(-Number(0) * 180 / Math.PI, 0); break;
                case "skewY": matrix.Skew(0, -Number(0) * 180 / Math.PI); break;
                case "translate": matrix.Translate(Length(0), Length(1)); placed = true; break;
                case "matrix": matrix.Append(new Matrix(Number(0), Number(1), Number(2), Number(3), Length(4), Length(5))); placed = true; break;
            }
            any = true;
        }
        bool usable = new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY }.All(double.IsFinite) && Math.Abs(matrix.Determinant) > 1e-9;
        return any && usable ? (matrix, placed) : null;
    }

    [GeneratedRegex(@"(rotate|scale|skewX|skewY|translate|matrix)\s*\(([^)]*)\)")]
    private static partial Regex TransformRegex();

    void Enhanced(XElement? geometry, SlideShape shape)
    {
        var type = (string?)geometry?.Attribute(Draw + "type") ?? "rectangle";
        shape.Geometry = type switch
        {
            "rectangle" or "rect" or "frame" or "flowchart-process" => ShapeGeometry.Rect,
            "round-rectangle" or "round-quadrat" or "flowchart-alternate-process" or "flowchart-terminator" => ShapeGeometry.RoundRect,
            "ellipse" or "circle" or "flowchart-connector" or "ring" => ShapeGeometry.Ellipse,
            "isosceles-triangle" => ShapeGeometry.Triangle,
            "right-triangle" => ShapeGeometry.RightTriangle,
            "diamond" or "flowchart-decision" => ShapeGeometry.Diamond,
            "parallelogram" or "flowchart-data" => ShapeGeometry.Parallelogram,
            "trapezoid" or "flowchart-manual-operation" => ShapeGeometry.Trapezoid,
            "pentagon" => ShapeGeometry.Pentagon,
            "pentagon-right" => ShapeGeometry.HomePlate,
            "hexagon" => ShapeGeometry.Hexagon,
            "octagon" => ShapeGeometry.Octagon,
            "chevron" => ShapeGeometry.Chevron,
            "right-arrow" => ShapeGeometry.RightArrow,
            "left-arrow" => ShapeGeometry.LeftArrow,
            "up-arrow" => ShapeGeometry.UpArrow,
            "down-arrow" => ShapeGeometry.DownArrow,
            "star5" or "star4" or "star6" or "star8" => ShapeGeometry.Star5,
            "cross" => ShapeGeometry.Plus,
            _ when type.StartsWith("ooxml-") => PptxReader.Preset(type[6..]),
            _ => ShapeGeometry.Custom,
        };
        if (type is "flowchart-terminator") shape.Adjust = 0.5;
        if (shape.Geometry == ShapeGeometry.Custom || type == "non-primitive")
        {
            var path = EnhancedPath((string?)geometry?.Attribute(Draw + "enhanced-path"), (string?)geometry?.Attribute(Svg + "viewBox"), shape.Bounds);
            if (path != null)
            {
                shape.Geometry = ShapeGeometry.Custom;
                shape.Path = path;
            }
            else if (shape.Geometry == ShapeGeometry.Custom) shape.Geometry = ShapeGeometry.Rect;
        }
        if ((string?)geometry?.Attribute(Draw + "mirror-horizontal") == "true") shape.FlipH ^= true;
        if ((string?)geometry?.Attribute(Draw + "mirror-vertical") == "true") shape.FlipV ^= true;
        if (Math.Abs(Angle((string?)geometry?.Attribute(Draw + "text-rotate-angle"), plainDegrees: true) % 360) is > 135 and < 225) shape.TextRotation = 180;
    }

    /// <summary>An enhanced path with plain numbers (paths using formulas fall back to the shape type).</summary>
    static Geometry? EnhancedPath(string? path, string? viewBox, Rect bounds)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('?') || path.Contains('$')) return null;
        var box = (viewBox ?? "0 0 21600 21600").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (box.Length < 4 || box[2] <= 0 || box[3] <= 0) return null;
        double sx = bounds.Width / box[2], sy = bounds.Height / box[3];
        var tokens = path.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var geometry = new PathGeometry();
        PathFigure? figure = null;
        char command = 'M';
        var numbers = new List<double>();
        Point P(int i) => new((numbers[i] - box[0]) * sx, (numbers[i + 1] - box[1]) * sy);
        void Flush()
        {
            switch (command)
            {
                case 'M':
                    for (int i = 0; i + 1 < numbers.Count; i += 2)
                    {
                        if (i == 0) geometry.Figures.Add(figure = new PathFigure { StartPoint = P(0) });
                        else figure!.Segments.Add(new LineSegment(P(i), true));
                    }
                    break;
                case 'L' when figure != null:
                    for (int i = 0; i + 1 < numbers.Count; i += 2) figure.Segments.Add(new LineSegment(P(i), true));
                    break;
                case 'C' when figure != null:
                    for (int i = 0; i + 5 < numbers.Count; i += 6) figure.Segments.Add(new BezierSegment(P(i), P(i + 2), P(i + 4), true));
                    break;
                case 'Q' when figure != null:
                    for (int i = 0; i + 3 < numbers.Count; i += 4) figure.Segments.Add(new QuadraticBezierSegment(P(i), P(i + 2), true));
                    break;
            }
            numbers.Clear();
        }
        foreach (var token in tokens)
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) { numbers.Add(n); continue; }
            if (token.Length != 1) return null;
            Flush();
            command = token[0];
            switch (command)
            {
                case 'Z' when figure != null: figure.IsClosed = true; break;
                case 'F' when figure != null: figure.IsFilled = false; break;
                case 'N' or 'S' or 'Z' or 'F': break;
                case 'M' or 'L' or 'C' or 'Q': break;
                default: return null; // arcs and other commands: use the shape type instead
            }
        }
        Flush();
        return geometry.Figures.Count > 0 ? geometry : null;
    }

    static Geometry? Polygon(XElement el, Rect bounds, bool closed)
    {
        var box = ((string?)el.Attribute(Svg + "viewBox") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var pts = ((string?)el.Attribute(Draw + "points") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(',')).Where(p => p.Length == 2)
            .Select(p => new Point(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture))).ToList();
        if (pts.Count < 2 || box.Length < 4 || box[2] <= 0 || box[3] <= 0) return null;
        double sx = bounds.Width / box[2], sy = bounds.Height / box[3];
        var figure = new PathFigure { StartPoint = new Point((pts[0].X - box[0]) * sx, (pts[0].Y - box[1]) * sy), IsClosed = closed, IsFilled = closed };
        figure.Segments.Add(new PolyLineSegment(pts.Skip(1).Select(p => new Point((p.X - box[0]) * sx, (p.Y - box[1]) * sy)), true));
        return new PathGeometry([figure]);
    }

    static Geometry? SvgPath(XElement el, Rect bounds)
    {
        var box = ((string?)el.Attribute(Svg + "viewBox") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var d = (string?)el.Attribute(Svg + "d");
        if (d == null || box.Length < 4 || box[2] <= 0 || box[3] <= 0) return null;
        var geometry = Geometry.Parse(d).Clone();
        var t = new TransformGroup();
        t.Children.Add(new TranslateTransform(-box[0], -box[1]));
        t.Children.Add(new ScaleTransform(bounds.Width / box[2], bounds.Height / box[3]));
        geometry.Transform = t;
        return geometry;
    }

    // ------------------------------------------------------------------ looks

    /// <summary>A shape's styles from most to least specific: its own, its presentation style, their parents.</summary>
    List<XElement> Chain(XElement el)
    {
        var chain = new List<XElement>();
        void Walk(string? name)
        {
            for (int guard = 0; guard < 20 && Style(name) is { } s; guard++)
            {
                if (chain.Contains(s)) break;
                chain.Add(s);
                name = (string?)s.Attribute(St + "parent-style-name");
            }
        }
        Walk((string?)el.Attribute(Draw + "style-name"));
        Walk((string?)el.Attribute(Pres + "style-name"));
        return chain;
    }

    /// <summary>The first value of a graphic property along the chain, then the document default.</summary>
    string? Graphic(List<XElement> chain, XName attr, bool useDefault = true)
    {
        foreach (var s in chain)
            if ((string?)s.Element(St + "graphic-properties")?.Attribute(attr) is { } v) return v;
        return useDefault ? (string?)_defaultGraphic?.Element(St + "graphic-properties")?.Attribute(attr) : null;
    }

    void ApplyGraphic(SlideShape shape, List<XElement> chain, bool isFrame)
    {
        // Text frames are see-through unless their style says otherwise; drawn shapes take the default fill.
        var fillKind = Graphic(chain, Draw + "fill", useDefault: !isFrame) ?? (isFrame ? "none" : "solid");
        shape.Fill = FillFrom(fillKind, n => Graphic(chain, n, useDefault: !isFrame));
        if (!isFrame) ApplyStroke(shape, chain);
        else if (Graphic(chain, Draw + "stroke", useDefault: false) is { } stroke && stroke != "none") ApplyStroke(shape, chain);

        shape.Anchor = Graphic(chain, Draw + "textarea-vertical-align") switch
        {
            "middle" => TextAnchor.Middle,
            "bottom" => TextAnchor.Bottom,
            "top" => TextAnchor.Top,
            _ => isFrame ? TextAnchor.Top : TextAnchor.Middle,
        };
        double Pad(string side, double fallback) => Units.ParseLength(Graphic(chain, Fo + $"padding-{side}")) ?? Units.ParseLength(Graphic(chain, Fo + "padding")) ?? fallback;
        shape.Insets = new Thickness(Pad("left", 7.2), Pad("top", 3.6), Pad("right", 7.2), Pad("bottom", 3.6));
        shape.Wrap = Graphic(chain, Fo + "wrap-option") != "no-wrap";
        shape.ShrinkToFit = Graphic(chain, Draw + "fit-to-size") is "shrink-to-fit" || Graphic(chain, St + "shrink-to-fit") == "true";
        if (Graphic(chain, St + "writing-mode") is "tb-rl" or "tb") shape.TextRotation = (shape.TextRotation + 90) % 360;
        else if (Graphic(chain, St + "writing-mode") is "bt-lr") shape.TextRotation = 270;
    }

    void ApplyStroke(SlideShape shape, List<XElement> chain)
    {
        var stroke = Graphic(chain, Draw + "stroke") ?? "solid";
        if (stroke == "none") { shape.Line = null; return; }
        var color = Units.ParseHex(Graphic(chain, Svg + "stroke-color")) ?? Color.FromRgb(0x34, 0x65, 0xA4);
        if (Percent(Graphic(chain, Svg + "stroke-opacity")) is double alpha) color.A = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        shape.Line = color;
        shape.LineWidth = Math.Max(0.75, Units.ParseLength(Graphic(chain, Svg + "stroke-width")) ?? 0.75);
        shape.Dashed = stroke == "dash";
        shape.HeadEnd = LineEnd(Graphic(chain, Draw + "marker-start"));
        shape.TailEnd = LineEnd(Graphic(chain, Draw + "marker-end"));
    }

    /// <summary>Arrow heads go by name ("Arrow", "Diamond_20_unfilled", "Circle", "msArrowStealthEnd_20_5"…).</summary>
    static ArrowHead LineEnd(string? marker)
    {
        if (string.IsNullOrEmpty(marker)) return ArrowHead.None;
        bool Has(string word) => marker.Contains(word, StringComparison.OrdinalIgnoreCase);
        return Has("stealth") || Has("concave") ? ArrowHead.Stealth : Has("diamond") ? ArrowHead.Diamond : Has("oval") || Has("circle") ? ArrowHead.Oval
            : Has("open") || Has("line") ? ArrowHead.Open : ArrowHead.Triangle;
    }

    SlideFill? FillFrom(string? kind, Func<XName, string?> get)
    {
        switch (kind)
        {
            case "solid":
                var color = Units.ParseHex(get(Draw + "fill-color")) ?? Color.FromRgb(0x72, 0x9F, 0xCF);
                if (Percent(get(Draw + "opacity")) is double opacity) color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
                return SlideFill.Solid(color);
            case "gradient" when get(Draw + "fill-gradient-name") is { } name && _gradients.TryGetValue(name, out var g):
                var start = Units.ParseHex((string?)g.Attribute(Draw + "start-color")) ?? Colors.White;
                var end = Units.ParseHex((string?)g.Attribute(Draw + "end-color")) ?? Colors.Black;
                var style = (string?)g.Attribute(Draw + "style") ?? "linear";
                bool radial = style is "radial" or "ellipsoid" or "square" or "rectangular";
                // Axial gradients mirror about the middle; radial ones run from the edge (start) to the centre (end).
                var stops = style == "axial" ? new List<(double, Color)> { (0, end), (0.5, start), (1, end) } : radial ? [(0, end), (1, start)] : [(0, start), (1, end)];
                // Gradients with more than two colours list them all as an extension.
                var listed = g.Elements().Where(e => e.Name.LocalName == "gradient-stop")
                    .Select(e => (at: Number((string?)e.Attribute(Svg + "offset")), color: Units.ParseHex((string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "color-value"))))
                    .Where(s => s.at != null && s.color != null).Select(s => (Math.Clamp(s.at!.Value, 0, 1), s.color!.Value)).ToList();
                if (listed.Count > 2 && style == "linear") stops = listed;
                else if (listed.Count > 2 && radial) stops = listed.Select(s => (1 - s.Item1, s.Item2)).Reverse().ToList();
                if (get(Draw + "opacity-name") is { Length: > 0 } fade && _opacities.TryGetValue(fade, out var opacities))
                {
                    double from = Percent((string?)opacities.Attribute(Draw + "start")) ?? 1, to = Percent((string?)opacities.Attribute(Draw + "end")) ?? 1;
                    if (radial) (from, to) = (to, from);
                    stops = stops.Select(s => (s.Item1, Color.FromArgb((byte)Math.Round(Math.Clamp(from + (to - from) * s.Item1, 0, 1) * 255), s.Item2.R, s.Item2.G, s.Item2.B))).ToList();
                }
                else if (Percent(get(Draw + "opacity")) is double see)
                    stops = stops.Select(s => (s.Item1, Color.FromArgb((byte)Math.Round(Math.Clamp(see, 0, 1) * 255), s.Item2.R, s.Item2.G, s.Item2.B))).ToList();
                return new SlideFill
                {
                    Gradient = stops,
                    // ODF's 0° runs top to bottom, counter-clockwise; ours runs left to right, clockwise.
                    GradientAngle = 90 - Angle((string?)g.Attribute(Draw + "angle")),
                    RadialGradient = radial,
                };
            case "bitmap" when get(Draw + "fill-image-name") is { } name && _fillImages.TryGetValue(name, out var f):
                var picture = Picture((string?)f.Attribute(XLink + "href"));
                if (picture == null) return null;
                return new SlideFill
                {
                    Image = picture,
                    Tile = get(St + "repeat") == "repeat" ? 1 : null,
                    Opacity = Percent(get(Draw + "opacity")) is double through ? Math.Clamp(through, 0, 1) : 1,
                };
            case "hatch":
                return Units.ParseHex(get(Draw + "fill-color")) is { } hatchBack ? SlideFill.Solid(hatchBack) : null;
            default:
                return null;
        }
    }

    /// <summary>ODF angles: plain numbers are tenths of a degree; "deg", "rad" and "grad" units are allowed.</summary>
    static double Angle(string? value, bool plainDegrees = false)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (value.EndsWith("deg")) return Num(value[..^3]);
        if (value.EndsWith("grad")) return Num(value[..^4]) * 0.9;
        if (value.EndsWith("rad")) return Num(value[..^3]) * 180 / Math.PI;
        return plainDegrees ? Num(value) : Num(value) / 10;
    }

    SlideFill? Background(XElement page)
    {
        var props = Style((string?)page.Attribute(Draw + "style-name"))?.Element(St + "drawing-page-properties");
        var kind = (string?)props?.Attribute(Draw + "fill");
        if (props == null || kind == null) return null;
        return FillFrom(kind, n => (string?)props.Attribute(n)) ?? (kind == "none" ? SlideFill.Solid(Colors.White) : null);
    }

    // ------------------------------------------------------------------ text

    void ReadText(XElement container, SlideShape shape, List<XElement> chain, XElement owner)
    {
        // Outline placeholders style each list level from "<master>-outline1…9".
        string? outlinePrefix = null;
        foreach (var s in chain)
        {
            var name = (string?)s.Attribute(St + "name") ?? "";
            if (Regex.IsMatch(name, @"-outline\d$")) { outlinePrefix = name[..^1]; break; }
        }
        var baseRun = new Run();
        var defaultText = _defaultGraphic?.Element(St + "text-properties");
        ApplyRun(baseRun, defaultText);
        foreach (var s in Enumerable.Reverse(chain)) ApplyRun(baseRun, s.Element(St + "text-properties"));
        var baseParagraph = chain.Select(s => s.Element(St + "paragraph-properties")).Where(p => p != null).Reverse().ToList();
        var shapeList = chain.Select(s => s.Element(St + "graphic-properties")?.Element(Text + "list-style")).FirstOrDefault(l => l != null);

        var counters = new Dictionary<int, int>();
        foreach (var child in container.Elements())
            Blocks(child, shape.Text, 0, null, baseRun, baseParagraph, shapeList, outlinePrefix, counters, chain);
    }

    void Blocks(XElement el, List<DBlock> into, int level, XElement? listStyle, Run baseRun, List<XElement?> baseParagraph,
        XElement? shapeList, string? outlinePrefix, Dictionary<int, int> counters, List<XElement> chain, bool header = false)
    {
        switch (el.Name.LocalName)
        {
            case "p" or "h":
                into.Add(Paragraph(el, level, listStyle, baseRun, baseParagraph, outlinePrefix, counters, header));
                break;
            case "list":
                var style = (string?)el.Attribute(Text + "style-name") is { } name && _listStyles.TryGetValue(name, out var ls) ? ls : listStyle ?? shapeList;
                if ((string?)el.Attribute(Text + "continue-numbering") != "true" && el.Parent?.Name.LocalName != "list-item") counters.Remove(level + 1);
                foreach (var item in el.Elements())
                {
                    bool isHeader = item.Name.LocalName == "list-header";
                    if (item.Attribute(Text + "start-value") is { } sv && int.TryParse(sv.Value, out var start)) counters[level + 1] = start - 1;
                    foreach (var child in item.Elements())
                        Blocks(child, into, level + 1, style, baseRun, baseParagraph, shapeList, outlinePrefix, counters, chain, isHeader);
                }
                break;
            case "table":
                into.Add(ReadTable(el, chain));
                break;
        }
    }

    DParagraph Paragraph(XElement el, int level, XElement? listStyle, Run baseRun, List<XElement?> baseParagraph,
        string? outlinePrefix, Dictionary<int, int> counters, bool header)
    {
        var run = baseRun.Clone();
        var paragraphProps = new List<XElement?>(baseParagraph);
        if (outlinePrefix != null && level > 1 && Style(outlinePrefix + Math.Min(level, 9)) is { } outline)
        {
            ApplyRun(run, outline.Element(St + "text-properties"));
            paragraphProps.Add(outline.Element(St + "paragraph-properties"));
        }
        foreach (var s in StyleChain((string?)el.Attribute(Text + "style-name")).AsEnumerable().Reverse())
        {
            ApplyRun(run, s.Element(St + "text-properties"));
            paragraphProps.Add(s.Element(St + "paragraph-properties"));
        }

        var p = new DParagraph { MarkSize = run.Size };
        foreach (var props in paragraphProps.Where(x => x != null))
        {
            if ((string?)props!.Attribute(Fo + "text-align") is { } align)
                p.Align = align switch { "center" => TextAlignment.Center, "end" or "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
            if ((string?)props.Attribute(St + "writing-mode") is { } mode && mode != "page") p.RightToLeft = mode.StartsWith("rl");
            if (Len(props, Fo + "margin-left") is double ml) p.IndentLeft = ml;
            if (Len(props, Fo + "text-indent") is double ti) p.IndentFirst = ti;
            if (Len(props, Fo + "margin-top") is double mt) p.SpaceBefore = mt;
            if (Len(props, Fo + "margin-bottom") is double mb) p.SpaceAfter = mb;
            if ((string?)props.Attribute(Fo + "line-height") is { } lh)
                p.LineSpacing = Percent(lh) ?? (Units.ParseLength(lh) is double abs ? abs / (run.Size * 1.2) : p.LineSpacing);
            if ((string?)props.Attribute(St + "line-height-at-least") is { } least && Units.ParseLength(least) is double atLeast)
                p.LineSpacing = Math.Max(p.LineSpacing, atLeast / (run.Size * 1.2));
        }

        // Right-to-left text without a side of its own is on the right. ("start" and "end" mean left and right to LibreOffice, whichever way text runs.)
        if (p.RightToLeft && !paragraphProps.Any(x => x?.Attribute(Fo + "text-align") != null)) p.Align = TextAlignment.Right;

        Inlines(el, p, run, null);

        // Bullets and numbers from the list's style for this level.
        foreach (var deeper in counters.Keys.Where(k => k > level).ToList()) counters.Remove(deeper);
        if (level > 0 && !header && p.Inlines.OfType<DText>().Any(t => t.Text.Trim().Length > 0))
        {
            var levelStyle = listStyle?.Elements().FirstOrDefault(l => (string?)l.Attribute(Text + "level") == level.ToString(CultureInfo.InvariantCulture));
            if (levelStyle != null) ApplyListLevel(p, levelStyle, level, counters, run);
        }
        return p;
    }

    void ApplyListLevel(DParagraph p, XElement levelStyle, int level, Dictionary<int, int> counters, Run run)
    {
        var markerText = levelStyle.Element(St + "text-properties");
        switch (levelStyle.Name.LocalName)
        {
            case "list-level-style-bullet":
                var ch = (string?)levelStyle.Attribute(Text + "bullet-char");
                if (string.IsNullOrEmpty(ch)) return;
                p.List = ListKind.Bullet;
                var font = FontOf(markerText);
                p.ListLabel = font is "StarSymbol" or "OpenSymbol" or "Symbol" or "Wingdings" && ch[0] >= 0xE000 ? "•" : ch;
                p.ListFont = font is "StarSymbol" or "OpenSymbol" ? "Segoe UI Symbol" : font;
                break;
            case "list-level-style-number":
                var format = (string?)levelStyle.Attribute(St + "num-format") ?? "1";
                if (format.Length == 0) return;
                int n = counters.TryGetValue(level, out var c) ? c + 1 : int.TryParse((string?)levelStyle.Attribute(Text + "start-value"), out var s) ? s : 1;
                counters[level] = n;
                p.List = ListKind.Number;
                p.ListLabel = (string?)levelStyle.Attribute(St + "num-prefix") + ListLabels.Format(n, format) + (string?)levelStyle.Attribute(St + "num-suffix");
                break;
            case "list-level-style-image":
                p.List = ListKind.Bullet;
                p.ListLabel = "•";
                break;
            default:
                return;
        }
        if (Units.ParseHex((string?)markerText?.Attribute(Fo + "color")) is { } color) p.ListColor = color;
        if (Percent((string?)markerText?.Attribute(Fo + "font-size")) is double size) p.ListSize = size;

        // Marker position: the older "space before + label width", or the newer label alignment.
        var props = levelStyle.Element(St + "list-level-properties");
        var alignment = props?.Element(St + "list-level-label-alignment");
        if (alignment != null && Len(alignment, Fo + "margin-left") is double margin)
        {
            if (p.IndentLeft == 0) p.IndentLeft = margin;
            if (p.IndentFirst == 0) p.IndentFirst = Len(alignment, Fo + "text-indent") ?? 0;
        }
        else if (props != null)
        {
            double before = Len(props, Text + "space-before") ?? 0, width = Len(props, Text + "min-label-width") ?? run.Size;
            if (p.IndentLeft == 0) p.IndentLeft = before + width;
            if (p.IndentFirst == 0) p.IndentFirst = -width;
        }
        if (p.IndentFirst >= 0) p.IndentFirst = -Math.Max(12, run.Size * 0.9);
    }

    void Inlines(XElement el, DParagraph p, Run run, string? link)
    {
        foreach (var node in el.Nodes())
        {
            if (node is XText text)
            {
                var value = Regex.Replace(text.Value, @"\s+", " ");
                if (value.Length > 0) p.Inlines.Add(new DText(run.Caps ? value.ToUpperInvariant() : value, Style(run, link), link));
                continue;
            }
            if (node is not XElement child) continue;
            switch (child.Name.LocalName)
            {
                case "span":
                    var inner = run.Clone();
                    foreach (var s in StyleChain((string?)child.Attribute(Text + "style-name")).AsEnumerable().Reverse()) ApplyRun(inner, s.Element(St + "text-properties"));
                    Inlines(child, p, inner, link);
                    break;
                case "a":
                    var linked = run.Clone();
                    linked.Underline = true;
                    Inlines(child, p, linked, SlideLimits.Link((string?)child.Attribute(XLink + "href")));
                    break;
                case "s":
                    int count = Units.OdfSpaceCount((string?)child.Attribute(Text + "c"));
                    p.Inlines.Add(new DText(new string(' ', count), Style(run, link)));
                    break;
                case "tab":
                    p.Inlines.Add(new DText("\t", Style(run, link)));
                    break;
                case "line-break":
                    p.Inlines.Add(new DLineBreak());
                    break;
                case "page-number":
                    p.Inlines.Add(new DText(_pageNumber.ToString(CultureInfo.InvariantCulture), Style(run, link)));
                    break;
                case "date-time" or "date" or "time" or "footer" or "header" or "title" or "page-count":
                    if (child.Value.Length > 0) p.Inlines.Add(new DText(child.Value, Style(run, link)));
                    break;
                default:
                    if (child.Name.Namespace == Text) Inlines(child, p, run, link);
                    break;
            }
        }
    }

    static TextStyle Style(Run r, string? link) => new()
    {
        Bold = r.Bold, Italic = r.Italic, Underline = r.Underline, Strike = r.Strike,
        Superscript = r.Position > 0, Subscript = r.Position < 0,
        Size = r.Size, Color = link != null && r.Color == Colors.Black ? Color.FromRgb(0x05, 0x63, 0xC1) : r.Color, Font = r.Font,
    };

    void ApplyRun(Run r, XElement? props)
    {
        if (props == null) return;
        if ((string?)props.Attribute(Fo + "font-size") is { } size)
        {
            if (Percent(size) is double pct) r.Size *= pct;
            else if (Units.ParseLength(size) is double pt) r.Size = pt;
        }
        if ((string?)props.Attribute(Fo + "font-weight") is { } weight) r.Bold = weight is "bold" or "600" or "700" or "800" or "900";
        if ((string?)props.Attribute(Fo + "font-style") is { } style) r.Italic = style is "italic" or "oblique";
        if ((string?)props.Attribute(St + "text-underline-style") is { } u) r.Underline = u != "none";
        if ((string?)props.Attribute(St + "text-line-through-style") is { } s) r.Strike = s != "none";
        if ((string?)props.Attribute(Fo + "text-transform") is { } transform) r.Caps = transform == "uppercase";
        if ((string?)props.Attribute(St + "text-position") is { } position)
            r.Position = position.StartsWith("super") ? 1 : position.StartsWith("sub") ? -1 : position.StartsWith('-') ? -1 : position.StartsWith('0') ? 0 : 1;
        if (Units.ParseHex((string?)props.Attribute(Fo + "color")) is { } color) r.Color = color;
        if (FontOf(props) is { } font) r.Font = font;
    }

    string? FontOf(XElement? props)
    {
        if (props == null) return null;
        if ((string?)props.Attribute(St + "font-name") is { } name) return _fontFaces.TryGetValue(name, out var family) ? family : name;
        return ((string?)props.Attribute(Fo + "font-family"))?.Trim('\'', '"');
    }

    // ------------------------------------------------------------------ tables

    DTable ReadTable(XElement table, List<XElement> chain)
    {
        var result = new DTable { CellPadding = new Thickness(5.4, 3.6, 5.4, 3.6) };
        XElement? Props(XElement? style, string kind) => style?.Elements().FirstOrDefault(e => e.Name.LocalName == kind);

        var columnStyles = new List<XElement?>();
        var widths = new List<double>();
        foreach (var col in table.Descendants(Table + "table-column").Where(c => c.Ancestors(Table + "table").First() == table))
        {
            var style = Style((string?)col.Attribute(Table + "style-name"));
            for (long n = Math.Min(OdfRepetitions.Count(col, "number-columns-repeated"), SlideLimits.MaxColumns - widths.Count); n > 0; n--)
            {
                widths.Add(Len(Props(style, "table-column-properties"), St + "column-width") ?? 0);
                columnStyles.Add(Style((string?)col.Attribute(Table + "default-cell-style-name")));
            }
        }

        // Rows and cells as they're listed; only empty repeats may be cut short.
        var rows = new List<(XElement tr, List<XElement> cells)>();
        long total = 0;
        foreach (var tr in table.Descendants(Table + "table-row").Where(r => r.Ancestors(Table + "table").First() == table))
        {
            _ct.ThrowIfCancellationRequested();
            var cells = new List<XElement>();
            foreach (var tc in tr.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                Repeat(OdfRepetitions.Count(tc, "number-columns-repeated"), SlideLimits.MaxColumns - cells.Count, HasText(tc), () => cells.Add(tc));
            Repeat(OdfRepetitions.Count(tr, "number-rows-repeated"), (SlideLimits.MaxTableCells - total) / Math.Max(1, cells.Count), cells.Any(HasText), () =>
            {
                rows.Add((tr, cells));
                total += Math.Max(1, cells.Count);
            });
        }
        static void Repeat(long count, long room, bool content, Action add)
        {
            if (count > room && content) throw SlideLimits.TooLarge();
            for (long n = Math.Min(count, room); n > 0; n--) add();
        }

        bool anyEdges = false;
        var covered = new HashSet<(int, int)>();
        for (int r = 0; r < rows.Count; r++)
        {
            var (tr, cells) = rows[r];
            var rowProps = Props(Style((string?)tr.Attribute(Table + "style-name")), "table-row-properties");
            var row = new DRow { Height = Len(rowProps, St + "row-height") ?? Len(rowProps, St + "min-row-height") };
            var rowStyle = Style((string?)tr.Attribute(Table + "default-cell-style-name"));
            for (int c = 0; c < cells.Count; c++)
            {
                if (covered.Contains((r, c))) continue;
                var tc = cells[c];
                var cell = new DCell { Look = new CellLook() };
                if (tc.Name != Table + "covered-table-cell")
                {
                    bool Covered(int row, int col) => row < rows.Count && col < rows[row].cells.Count && rows[row].cells[col].Name == Table + "covered-table-cell";
                    long wantedColumns = OdfRepetitions.Count(tc, "number-columns-spanned"), wantedRows = OdfRepetitions.Count(tc, "number-rows-spanned");
                    while (cell.ColSpan < wantedColumns && Covered(r, c + cell.ColSpan)) cell.ColSpan++;
                    while (cell.RowSpan < wantedRows && Covered(r + cell.RowSpan, c)) cell.RowSpan++;
                }
                for (int i = 0; i < cell.RowSpan; i++) for (int k = 0; k < cell.ColSpan; k++) covered.Add((r + i, c + k));

                var style = Style((string?)tc.Attribute(Table + "style-name")) ?? rowStyle ?? (c < columnStyles.Count ? columnStyles[c] : null);
                // LibreOffice keeps a cell's fill and padding with drawing properties, the standard with cell properties.
                XElement? graphic = Props(style, "graphic-properties"), box = Props(style, "table-cell-properties"), paragraph = Props(style, "paragraph-properties");
                var kind = (string?)graphic?.Attribute(Draw + "fill");
                var fill = Units.ParseHex((string?)graphic?.Attribute(Draw + "fill-color")) is { } drawn && kind is null or "solid" ? drawn : Units.ParseHex((string?)box?.Attribute(Fo + "background-color"));
                if (kind != "none") cell.Shading = fill;
                if (cell.Shading is { } shade && Percent((string?)graphic?.Attribute(Draw + "opacity")) is double opacity)
                    cell.Shading = Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), shade.R, shade.G, shade.B);

                string? Setting(XName name) => (string?)box?.Attribute(name) ?? (string?)graphic?.Attribute(name) ?? (string?)paragraph?.Attribute(name);
                CellEdge? Edge(string side) => EdgeOf(Setting(Fo + $"border-{side}") ?? Setting(Fo + "border"));
                (cell.Look.Left, cell.Look.Top, cell.Look.Right, cell.Look.Bottom) = (Edge("left"), Edge("top"), Edge("right"), Edge("bottom"));
                anyEdges |= cell.Look.Left != null || cell.Look.Top != null || cell.Look.Right != null || cell.Look.Bottom != null;
                double Pad(string side, double fallback) => Math.Clamp(Units.ParseLength(Setting(Fo + $"padding-{side}") ?? Setting(Fo + "padding")) ?? fallback, 0, 1000);
                cell.Look.Padding = new Thickness(Pad("left", 5.4), Pad("top", 3.6), Pad("right", 5.4), Pad("bottom", 3.6));
                cell.Look.Anchor = ((string?)graphic?.Attribute(Draw + "textarea-vertical-align") ?? (string?)box?.Attribute(St + "vertical-align")) switch
                {
                    "middle" => TextAnchor.Middle, "bottom" => TextAnchor.Bottom, _ => TextAnchor.Top,
                };

                var baseRun = new Run();
                ApplyRun(baseRun, _defaultGraphic?.Element(St + "text-properties"));
                ApplyRun(baseRun, style?.Element(St + "text-properties"));
                var counters = new Dictionary<int, int>();
                if (tc.Name != Table + "covered-table-cell")
                    foreach (var child in tc.Elements())
                        Blocks(child, cell.Blocks, 0, null, baseRun, [paragraph], null, null, counters, chain);
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new DParagraph());
                row.Cells.Add(cell);
            }
            result.Rows.Add(row);
        }
        // A table that says nothing about its lines gets a plain grid, so it still reads as a table.
        if (!anyEdges) (result.Borders, result.BorderColor) = (true, Color.FromRgb(0x80, 0x80, 0x80));
        if (widths.Any(w => w > 0)) result.ColumnWidths = widths;
        return result;
    }

    /// <summary>A border like "0.75pt solid #808080": a line, explicitly none, or null when nothing is said.</summary>
    static CellEdge? EdgeOf(string? border)
    {
        if (string.IsNullOrWhiteSpace(border)) return null;
        var parts = border.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("none") || parts.Contains("hidden")) return new CellEdge(0, null);
        var color = parts.Select(Units.ParseHex).FirstOrDefault(c => c != null);
        return color == null ? null : new CellEdge(Math.Clamp(parts.Select(Units.ParseLength).FirstOrDefault(w => w > 0) ?? 0.75, 0.25, 72), color);
    }

    // ------------------------------------------------------------------ helpers

    XElement? Style(string? name)
    {
        if (name == null) return null;
        return _onMaster && _masterStyles.TryGetValue(name, out var own) || _styles.TryGetValue(name, out own) ? own : null;
    }

    List<XElement> StyleChain(string? name)
    {
        var chain = new List<XElement>();
        for (int guard = 0; guard < 20 && Style(name) is { } s && !chain.Contains(s); guard++)
        {
            chain.Add(s);
            name = (string?)s.Attribute(St + "parent-style-name");
        }
        return chain;
    }

    DImage? Picture(string? href)
    {
        if (string.IsNullOrEmpty(href) || href.Contains("://")) return null;
        href = href.TrimStart('.', '/');
        if (_images.TryGetValue(href, out var cached)) return cached;
        DImage? image = null;
        if (_zip.GetEntry(href) is { } entry)
        {
            try
            {
                using var stream = PackageResources.Open(entry, media: true);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var data = ms.ToArray();
                var (w, h) = DocxReader.ImageSize(data);
                image = new DImage(data, Path.GetExtension(href).TrimStart('.').ToLowerInvariant(), w, h);
            }
            catch (InvalidDataException ex) { Log.Write($"Left out a damaged picture ({href}): {ex.Message}"); }
        }
        _images[href] = image;
        return image;
    }

    XElement? Load(string name)
    {
        if (_zip.GetEntry(name) is not { } entry) return null;
        using var stream = PackageResources.Open(entry);
        return SlideLimits.Check(PackageResources.LoadXml(stream).Root!);
    }

    static double? Len(XElement? el, XName name) => Units.ParseLength((string?)el?.Attribute(name));

    static double? Number(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    static double? Percent(string? value) =>
        value != null && value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 100 : null;
}
