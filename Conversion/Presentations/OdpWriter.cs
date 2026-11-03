using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;
using static ConvertWheel.Conversion.Documents.OdtReader;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>Writes a deck as an OpenDocument presentation (.odp), with every shape's look as automatic styles.</summary>
internal sealed class OdpWriter
{
    static readonly XNamespace Pres = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    readonly ZipArchive _zip;
    readonly Deck _deck;
    readonly CancellationToken _ct;
    readonly List<XElement> _autoStyles = [];
    readonly List<XElement> _commonStyles = [];
    readonly Dictionary<string, string> _styleCache = [];
    readonly Dictionary<DImage, string> _pictures = [];
    readonly List<string> _files = [];
    int _styleCount;
    /// <summary>One master page per distinct background: PowerPoint reads ODP backgrounds only from masters.</summary>
    readonly Dictionary<string, string> _masterByBackground = [];
    readonly List<XElement> _masters = [];
    readonly List<XElement> _masterStyles = [];

    OdpWriter(ZipArchive zip, Deck deck, CancellationToken ct) => (_zip, _deck, _ct) = (zip, deck, ct);

    public static void Write(Deck deck, string path, CancellationToken ct = default)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        new OdpWriter(zip, deck, ct).WriteAll();
    }

    void WriteAll()
    {
        // The mimetype comes first and uncompressed, so the file is recognised by its first bytes.
        var mime = _zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open()) s.Write(Encoding.ASCII.GetBytes("application/vnd.oasis.opendocument.presentation"));

        _commonStyles.Add(new XElement(Draw + "marker", new XAttribute(Draw + "name", "Arrow"),
            new XAttribute(Svg + "viewBox", "0 0 20 30"), new XAttribute(Svg + "d", "M10 0l-10 30h20z")));

        var presentation = new XElement(Office + "presentation");
        for (int i = 0; i < _deck.Slides.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            presentation.Add(Page(_deck.Slides[i], i));
        }

        Save("content.xml", new XElement(Office + "document-content", Namespaces(), new XAttribute(Office + "version", "1.3"),
            new XElement(Office + "automatic-styles", _autoStyles),
            new XElement(Office + "body", presentation)));

        Save("styles.xml", new XElement(Office + "document-styles", Namespaces(), new XAttribute(Office + "version", "1.3"),
            new XElement(Office + "styles", _commonStyles,
                new XElement(St + "default-style", new XAttribute(St + "family", "graphic"),
                    new XElement(St + "graphic-properties", new XAttribute(Draw + "shadow", "hidden")),
                    new XElement(St + "text-properties", new XAttribute(Fo + "font-family", _deck.Font), new XAttribute(Fo + "font-size", "18pt")))),
            new XElement(Office + "automatic-styles",
                new XElement(St + "page-layout", new XAttribute(St + "name", "PM1"),
                    new XElement(St + "page-layout-properties",
                        new XAttribute(Fo + "margin-top", "0cm"), new XAttribute(Fo + "margin-bottom", "0cm"),
                        new XAttribute(Fo + "margin-left", "0cm"), new XAttribute(Fo + "margin-right", "0cm"),
                        new XAttribute(Fo + "page-width", Len(_deck.Width)), new XAttribute(Fo + "page-height", Len(_deck.Height)),
                        new XAttribute(St + "print-orientation", _deck.Width >= _deck.Height ? "landscape" : "portrait"))),
                _masterStyles),
            new XElement(Office + "master-styles", _masters)));

        Save("meta.xml", new XElement(Office + "document-meta", Namespaces(), new XAttribute(Office + "version", "1.3"),
            new XElement(Office + "meta",
                new XElement(XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:meta:1.0") + "generator", "Convert Wheel"),
                new XElement(XNamespace.Get("http://purl.org/dc/elements/1.1/") + "title", _deck.Title ?? ""))));

        var manifest = new XElement(Manifest + "manifest", new XAttribute(XNamespace.Xmlns + "manifest", Manifest), new XAttribute(Manifest + "version", "1.3"),
            new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", "/"), new XAttribute(Manifest + "version", "1.3"),
                new XAttribute(Manifest + "media-type", "application/vnd.oasis.opendocument.presentation")));
        foreach (var f in new[] { "content.xml", "styles.xml", "meta.xml" }.Concat(_files))
            manifest.Add(new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", f),
                new XAttribute(Manifest + "media-type", f.EndsWith(".xml") ? "text/xml" : f.EndsWith(".png") ? "image/png" : f.EndsWith(".jpg") ? "image/jpeg" : "image/" + Path.GetExtension(f).TrimStart('.'))));
        Save("META-INF/manifest.xml", manifest);
    }

    XElement Page(Slide slide, int index)
    {
        var props = new XElement(St + "drawing-page-properties");
        FillProperties(props, slide.Background ?? SlideFill.Solid(Colors.White));
        props.Add(new XAttribute(Draw + "background-size", "border"), new XAttribute(Pres + "visibility", "visible"),
            new XAttribute(Pres + "background-visible", "true"), new XAttribute(Pres + "background-objects-visible", "true"),
            new XAttribute(Pres + "display-footer", "false"), new XAttribute(Pres + "display-page-number", "false"), new XAttribute(Pres + "display-date-time", "false"));
        var key = props.ToString(SaveOptions.DisableFormatting);
        if (!_masterByBackground.TryGetValue(key, out var master))
        {
            int n = _masters.Count + 1;
            master = n == 1 ? "Default" : $"Master{n}";
            _masterByBackground[key] = master;
            _masterStyles.Add(new XElement(St + "style", new XAttribute(St + "name", $"Mdp{n}"), new XAttribute(St + "family", "drawing-page"), new XElement(props)));
            _masters.Add(new XElement(St + "master-page", new XAttribute(St + "name", master),
                new XAttribute(St + "page-layout-name", "PM1"), new XAttribute(Draw + "style-name", $"Mdp{n}")));
        }
        var page = new XElement(Draw + "page", new XAttribute(Draw + "name", $"page{index + 1}"),
            new XAttribute(Draw + "style-name", Style("drawing-page", props)), new XAttribute(Draw + "master-page-name", master));
        foreach (var shape in slide.Shapes)
        {
            try { page.Add(Shape(shape)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Log.Write($"Skipped writing a shape: {ex.Message}");
            }
        }
        if (slide.Notes.Count > 0)
        {
            // Speaker notes: a portrait notes page with the slide picture above the text.
            double slideHeight = Math.Min(270, 432 * _deck.Height / Math.Max(1, _deck.Width));
            var box = new XElement(Draw + "text-box");
            foreach (var note in slide.Notes)
            {
                var p = new XElement(Text + "p");
                var lines = note.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.Add(new XElement(Text + "line-break"));
                    AddText(p, lines[i]);
                }
                box.Add(p);
            }
            page.Add(new XElement(Pres + "notes",
                new XElement(Draw + "page-thumbnail", new XAttribute(Draw + "page-number", index + 1), new XAttribute(Pres + "class", "page"),
                    new XAttribute(Svg + "x", Len(54)), new XAttribute(Svg + "y", Len(54)), new XAttribute(Svg + "width", Len(432)), new XAttribute(Svg + "height", Len(slideHeight))),
                new XElement(Draw + "frame", new XAttribute(Pres + "class", "notes"),
                    new XAttribute(Svg + "x", Len(54)), new XAttribute(Svg + "y", Len(72 + slideHeight)),
                    new XAttribute(Svg + "width", Len(432)), new XAttribute(Svg + "height", Len(594 - slideHeight)),
                    box)));
        }
        return page;
    }

    // ------------------------------------------------------------------ shapes

    XElement Shape(SlideShape shape)
    {
        if (shape.Picture != null) return Picture(shape);
        if (shape.Text.Count == 1 && shape.Text[0] is DTable table) return TableFrame(shape, table);
        if (shape.Geometry is ShapeGeometry.Line) return Line(shape);

        var graphic = new XElement(St + "graphic-properties");
        if (shape.Fill != null && shape.Geometry is not (ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve)) FillProperties(graphic, shape.Fill);
        else graphic.Add(new XAttribute(Draw + "fill", "none"));
        Stroke(graphic, shape);
        graphic.Add(
            new XAttribute(Draw + "textarea-vertical-align", shape.Anchor switch { TextAnchor.Middle => "middle", TextAnchor.Bottom => "bottom", _ => "top" }),
            new XAttribute(Draw + "auto-grow-height", "false"), new XAttribute(Draw + "auto-grow-width", "false"),
            new XAttribute(Fo + "padding-left", Len(shape.Insets.Left)), new XAttribute(Fo + "padding-top", Len(shape.Insets.Top)),
            new XAttribute(Fo + "padding-right", Len(shape.Insets.Right)), new XAttribute(Fo + "padding-bottom", Len(shape.Insets.Bottom)),
            new XAttribute(Fo + "wrap-option", shape.Wrap ? "wrap" : "no-wrap"),
            new XAttribute(Draw + "shadow", "hidden"));
        if (shape.ShrinkToFit) graphic.Add(new XAttribute(Draw + "fit-to-size", "shrink-to-fit"));
        if (shape.TextRotation != 0) graphic.Add(new XAttribute(St + "writing-mode", "tb-rl"));

        var el = new XElement(Draw + "custom-shape", new XAttribute(Draw + "style-name", Style("graphic", graphic)));
        if (shape.Role == "title") el.Add(new XAttribute(Draw + "name", "Title"));
        Place(el, shape);
        foreach (var block in shape.Text.OfType<DParagraph>()) el.Add(TextBlock(block));
        el.Add(EnhancedGeometry(shape));
        return el;
    }

    /// <summary>The outline as an ODF enhanced path in the shape's own coordinates (hundredths of a point).</summary>
    static XElement EnhancedGeometry(SlideShape shape)
    {
        double w = Math.Max(shape.Bounds.Width, 0.01), h = Math.Max(shape.Bounds.Height, 0.01);
        var type = shape.Geometry switch
        {
            ShapeGeometry.Rect => "rectangle",
            ShapeGeometry.Ellipse => "ellipse",
            ShapeGeometry.RoundRect => "round-rectangle",
            _ => "non-primitive",
        };
        var geometry = new XElement(Draw + "enhanced-geometry",
            new XAttribute(Svg + "viewBox", $"0 0 {Hundredths(w)} {Hundredths(h)}"), new XAttribute(Draw + "type", type));
        var outline = SlideRenderer.Outline(shape, new Rect(0, 0, Units.ToDip(w), Units.ToDip(h)));
        if (outline != null)
        {
            var flat = outline.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);
            var sb = new StringBuilder();
            string P(Point p) => $"{Hundredths(Units.ToPoints(p.X))} {Hundredths(Units.ToPoints(p.Y))}";
            foreach (var figure in flat.Figures)
            {
                sb.Append("M ").Append(P(figure.StartPoint));
                var points = figure.Segments.SelectMany(s => s switch
                {
                    PolyLineSegment poly => poly.Points.AsEnumerable(),
                    LineSegment line => [line.Point],
                    _ => [],
                }).ToList();
                if (points.Count > 0) sb.Append(" L ").Append(string.Join(" ", points.Select(P)));
                if (figure.IsClosed) sb.Append(" Z");
                if (!figure.IsFilled) sb.Append(" F");
                sb.Append(' ');
            }
            sb.Append('N');
            geometry.Add(new XAttribute(Draw + "enhanced-path", sb.ToString()));
        }
        return geometry;
    }

    XElement Line(SlideShape shape)
    {
        var graphic = new XElement(St + "graphic-properties", new XAttribute(Draw + "fill", "none"));
        Stroke(graphic, shape);
        var r = shape.Bounds;
        bool flipH = shape.FlipH, flipV = shape.FlipV;
        return new XElement(Draw + "line", new XAttribute(Draw + "style-name", Style("graphic", graphic)),
            new XAttribute(Svg + "x1", Len(flipH ? r.Right : r.X)), new XAttribute(Svg + "y1", Len(flipV ? r.Bottom : r.Y)),
            new XAttribute(Svg + "x2", Len(flipH ? r.X : r.Right)), new XAttribute(Svg + "y2", Len(flipV ? r.Y : r.Bottom)),
            new XElement(Text + "p"));
    }

    XElement Picture(SlideShape shape)
    {
        var graphic = new XElement(St + "graphic-properties", new XAttribute(Draw + "fill", "none"));
        Stroke(graphic, shape);
        var frame = new XElement(Draw + "frame", new XAttribute(Draw + "style-name", Style("graphic", graphic)));
        Place(frame, shape);
        var href = PictureFile(shape.Picture!, shape.Crop);
        frame.Add(new XElement(Draw + "image", new XAttribute(XLink + "href", href), new XAttribute(XLink + "type", "simple"),
            new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad"), new XElement(Text + "p")));
        return frame;
    }

    XElement TableFrame(SlideShape shape, DTable table)
    {
        var frame = new XElement(Draw + "frame");
        Place(frame, shape);
        int columns = Math.Max(1, table.ColumnCount);
        var widths = table.ColumnWidths is { } w && w.Count >= columns ? w.Take(columns).ToList() : Enumerable.Repeat(shape.Bounds.Width / columns, columns).ToList();
        var t = new XElement(Table + "table");
        foreach (var cw in widths)
            t.Add(new XElement(Table + "table-column", new XAttribute(Table + "style-name",
                Style("table-column", new XElement(St + "table-column-properties", new XAttribute(St + "column-width", Len(cw)))))));
        var border = table.Borders ? $"0.75pt solid {Hex(table.BorderColor ?? Color.FromRgb(0x80, 0x80, 0x80))}" : "none";
        foreach (var row in table.Rows)
        {
            var tr = new XElement(Table + "table-row");
            foreach (var cell in row.Cells)
            {
                var cellProps = new XElement(St + "table-cell-properties", new XAttribute(Fo + "border", border),
                    new XAttribute(Fo + "padding", "0.1cm"));
                var graphic = new XElement(St + "graphic-properties");
                if (cell.Shading is { } shade)
                {
                    cellProps.Add(new XAttribute(Fo + "background-color", Hex(shade)));
                    graphic.Add(new XAttribute(Draw + "fill", "solid"), new XAttribute(Draw + "fill-color", Hex(shade)));
                }
                else graphic.Add(new XAttribute(Draw + "fill", "none"));
                var tc = new XElement(Table + "table-cell", new XAttribute(Table + "style-name", Style("table-cell", graphic, cellProps)));
                if (cell.ColSpan > 1) tc.Add(new XAttribute(Table + "number-columns-spanned", cell.ColSpan));
                foreach (var p in cell.Blocks.OfType<DParagraph>()) tc.Add(TextBlock(p, row.Header));
                tr.Add(tc);
                for (int s = 1; s < cell.ColSpan; s++) tr.Add(new XElement(Table + "covered-table-cell"));
            }
            t.Add(tr);
        }
        frame.Add(t);
        return frame;
    }

    /// <summary>Position and size, with rotation expressed as ODF's rotate-then-translate transform.</summary>
    static void Place(XElement el, SlideShape shape)
    {
        var r = shape.Bounds;
        el.Add(new XAttribute(Svg + "width", Len(r.Width)), new XAttribute(Svg + "height", Len(r.Height)));
        if (Math.Abs(shape.Rotation % 360) < 0.01)
        {
            el.Add(new XAttribute(Svg + "x", Len(r.X)), new XAttribute(Svg + "y", Len(r.Y)));
            return;
        }
        // ODF turns counter-clockwise about the top-left corner, then moves that corner into place.
        double a = shape.Rotation * Math.PI / 180;
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        double dx = -r.Width / 2, dy = -r.Height / 2;
        double x = cx + dx * Math.Cos(a) - dy * Math.Sin(a), y = cy + dx * Math.Sin(a) + dy * Math.Cos(a);
        el.Add(new XAttribute(Draw + "transform", string.Create(CultureInfo.InvariantCulture,
            $"rotate ({-a:0.######}) translate ({Len(x)} {Len(y)})")));
    }

    void Stroke(XElement graphic, SlideShape shape)
    {
        if (shape.Line is not { A: > 0 } line)
        {
            graphic.Add(new XAttribute(Draw + "stroke", "none"));
            return;
        }
        graphic.Add(new XAttribute(Draw + "stroke", shape.Dashed ? "dash" : "solid"),
            new XAttribute(Svg + "stroke-color", Hex(line)), new XAttribute(Svg + "stroke-width", Len(shape.LineWidth)));
        if (line.A < 255) graphic.Add(new XAttribute(Svg + "stroke-opacity", Percent(line.A / 255.0)));
        if (shape.Dashed)
        {
            _commonStyles.Add(new XElement(Draw + "stroke-dash", new XAttribute(Draw + "name", "Dash"), new XAttribute(Draw + "style", "rect"),
                new XAttribute(Draw + "dots1", 1), new XAttribute(Draw + "dots1-length", "300%"), new XAttribute(Draw + "distance", "200%")));
            graphic.Add(new XAttribute(Draw + "stroke-dash", "Dash"));
        }
        double arrow = Math.Max(6, shape.LineWidth * 3);
        if (shape.HeadEnd == ArrowHead.Triangle)
            graphic.Add(new XAttribute(Draw + "marker-start", "Arrow"), new XAttribute(Draw + "marker-start-width", Len(arrow)));
        if (shape.TailEnd == ArrowHead.Triangle)
            graphic.Add(new XAttribute(Draw + "marker-end", "Arrow"), new XAttribute(Draw + "marker-end-width", Len(arrow)));
    }

    void FillProperties(XElement props, SlideFill fill)
    {
        if (fill.Image != null)
        {
            var name = $"Bitmap{_commonStyles.Count + 1}";
            _commonStyles.Add(new XElement(Draw + "fill-image", new XAttribute(Draw + "name", name),
                new XAttribute(XLink + "href", PictureFile(fill.Image, default)), new XAttribute(XLink + "type", "simple"),
                new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad")));
            props.Add(new XAttribute(Draw + "fill", "bitmap"), new XAttribute(Draw + "fill-image-name", name),
                new XAttribute(St + "repeat", fill.Tile != null ? "repeat" : "stretch"));
            if (fill.Opacity < 0.999) props.Add(new XAttribute(Draw + "opacity", Percent(fill.Opacity)));
            return;
        }
        if (fill.Gradient is { Count: > 0 } stops)
        {
            var name = $"Gradient{_commonStyles.Count + 1}";
            // ODF's 0° runs top to bottom and turns counter-clockwise; DrawingML's 0° runs left to right, clockwise.
            int angle = (int)Math.Round(((90 - fill.GradientAngle) % 360 + 360) % 360);
            _commonStyles.Add(new XElement(Draw + "gradient", new XAttribute(Draw + "name", name),
                new XAttribute(Draw + "style", fill.RadialGradient ? "radial" : "linear"),
                new XAttribute(Draw + "cx", "50%"), new XAttribute(Draw + "cy", "50%"),
                // A radial gradient starts at its edge in ODF but at its centre in our model.
                new XAttribute(Draw + "start-color", Hex(fill.RadialGradient ? stops[^1].Color : stops[0].Color)),
                new XAttribute(Draw + "end-color", Hex(fill.RadialGradient ? stops[0].Color : stops[^1].Color)),
                new XAttribute(Draw + "start-intensity", "100%"), new XAttribute(Draw + "end-intensity", "100%"),
                new XAttribute(Draw + "angle", $"{angle}deg"), new XAttribute(Draw + "border", "0%")));
            props.Add(new XAttribute(Draw + "fill", "gradient"), new XAttribute(Draw + "fill-gradient-name", name));
            return;
        }
        if (fill.Color is { } c)
        {
            props.Add(new XAttribute(Draw + "fill", "solid"), new XAttribute(Draw + "fill-color", Hex(c)));
            if (c.A < 255) props.Add(new XAttribute(Draw + "opacity", Percent(c.A / 255.0)));
        }
        else props.Add(new XAttribute(Draw + "fill", "none"));
    }

    // ------------------------------------------------------------------ text

    /// <summary>A paragraph, wrapped in a one-item list when it has a bullet or number.</summary>
    XElement TextBlock(DParagraph p, bool bold = false)
    {
        var paraProps = new XElement(St + "paragraph-properties",
            new XAttribute(Fo + "text-align", p.Align switch { TextAlignment.Center => "center", TextAlignment.Right => "end", TextAlignment.Justify => "justify", _ => "start" }),
            new XAttribute(Fo + "margin-left", Len(p.IndentLeft)), new XAttribute(Fo + "text-indent", Len(p.IndentFirst)),
            new XAttribute(Fo + "margin-top", Len(p.SpaceBefore)), new XAttribute(Fo + "margin-bottom", Len(p.SpaceAfter)));
        if (Math.Abs(p.LineSpacing - 1) > 0.01) paraProps.Add(new XAttribute(Fo + "line-height", Percent(p.LineSpacing)));
        var para = new XElement(Text + "p", new XAttribute(Text + "style-name", Style("paragraph", paraProps,
            new XElement(St + "text-properties", new XAttribute(Fo + "font-size", Len(p.MarkSize ?? 18))))));

        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t when t.Text.Length > 0:
                    var span = new XElement(Text + "span", new XAttribute(Text + "style-name", Style("text", TextProperties(t.Style, bold))));
                    AddText(span, t.Text);
                    para.Add(t.Link != null && Uri.TryCreate(t.Link, UriKind.Absolute, out _)
                        ? new XElement(Text + "a", new XAttribute(XLink + "href", t.Link), new XAttribute(XLink + "type", "simple"), span)
                        : span);
                    break;
                case DLineBreak:
                    para.Add(new XElement(Text + "line-break"));
                    break;
                case DField:
                    para.Add(new XElement(Pres + "page-number"));
                    break;
            }
        }
        if (p.List == ListKind.None || string.IsNullOrEmpty(p.ListLabel)) return para;

        // The marker's look and position follow the paragraph's indents.
        var level = new XElement(p.List == ListKind.Number ? Text + "list-level-style-number" : Text + "list-level-style-bullet",
            new XAttribute(Text + "level", 1));
        if (p.List == ListKind.Number)
        {
            var (format, suffix, start) = NumberFormat(p.ListLabel);
            level.Add(new XAttribute(St + "num-format", format), new XAttribute(St + "num-suffix", suffix));
            if (p.ListLabel.TrimStart().StartsWith('(')) level.Add(new XAttribute(St + "num-prefix", "("));
            para = new XElement(Text + "list-item", new XAttribute(Text + "start-value", start), para);
        }
        else
        {
            level.Add(new XAttribute(Text + "bullet-char", p.ListLabel));
            para = new XElement(Text + "list-item", para);
        }
        level.Add(new XElement(St + "list-level-properties",
            new XAttribute(Text + "space-before", Len(Math.Max(0, p.IndentLeft + p.IndentFirst))),
            new XAttribute(Text + "min-label-width", Len(Math.Max(0, -p.IndentFirst)))));
        var markerText = new XElement(St + "text-properties", new XAttribute(Fo + "font-size", Percent(p.ListSize ?? 1)));
        if (p.ListFont != null) markerText.Add(new XAttribute(Fo + "font-family", p.ListFont));
        if ((p.ListColor ?? p.Inlines.OfType<DText>().FirstOrDefault()?.Style.Color) is { } mc) markerText.Add(new XAttribute(Fo + "color", Hex(mc)));
        level.Add(markerText);
        var listStyle = $"L{++_styleCount}";
        _autoStyles.Add(new XElement(Text + "list-style", new XAttribute(St + "name", listStyle), level));
        return new XElement(Text + "list", new XAttribute(Text + "style-name", listStyle), para);
    }

    static (string format, string suffix, int start) NumberFormat(string label)
    {
        var core = label.Trim().TrimStart('(').TrimEnd('.', ')');
        var suffix = label.TrimEnd().EndsWith(')') ? ")" : ".";
        if (int.TryParse(core, out var n)) return ("1", suffix, n);
        if (core.Length > 0 && core.All(ch => "ivxlcdm".Contains(ch))) return ("i", suffix, 1);
        if (core.Length > 0 && core.All(ch => "IVXLCDM".Contains(ch))) return ("I", suffix, 1);
        if (core.Length == 1 && char.IsLetter(core[0])) return (char.IsUpper(core[0]) ? "A" : "a", suffix, char.ToLowerInvariant(core[0]) - 'a' + 1);
        return ("1", suffix, 1);
    }

    static XElement TextProperties(TextStyle s, bool bold)
    {
        var props = new XElement(St + "text-properties", new XAttribute(Fo + "font-size", Len(s.Size ?? 18)));
        if (s.Font is { Length: > 0 } font) props.Add(new XAttribute(Fo + "font-family", SafeXml.Text(font)));
        if (s.Color is { } c) props.Add(new XAttribute(Fo + "color", Hex(c)));
        if (s.Bold || bold) props.Add(new XAttribute(Fo + "font-weight", "bold"));
        if (s.Italic) props.Add(new XAttribute(Fo + "font-style", "italic"));
        if (s.Underline) props.Add(new XAttribute(St + "text-underline-style", "solid"), new XAttribute(St + "text-underline-width", "auto"),
            new XAttribute(St + "text-underline-color", "font-color"));
        if (s.Strike) props.Add(new XAttribute(St + "text-line-through-style", "solid"));
        if (s.Superscript) props.Add(new XAttribute(St + "text-position", "super 58%"));
        else if (s.Subscript) props.Add(new XAttribute(St + "text-position", "sub 58%"));
        if (s.Highlight is { } h) props.Add(new XAttribute(Fo + "background-color", Hex(h)));
        return props;
    }

    /// <summary>ODF collapses runs of spaces and ignores tabs and newlines, so those become elements.</summary>
    static void AddText(XElement span, string text)
    {
        text = SafeXml.Text(text);
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length > 0) span.Add(new XText(sb.ToString()));
            sb.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\t') { Flush(); span.Add(new XElement(Text + "tab")); }
            else if (ch == '\n') { Flush(); span.Add(new XElement(Text + "line-break")); }
            else if (ch == ' ' && (i == 0 || text[i - 1] == ' '))
            {
                int run = 1;
                while (i + 1 < text.Length && text[i + 1] == ' ') { run++; i++; }
                Flush();
                span.Add(new XElement(Text + "s", new XAttribute(Text + "c", run)));
            }
            else if (ch >= ' ' || ch == '\r') { if (ch != '\r') sb.Append(ch); }
        }
        Flush();
    }

    // ------------------------------------------------------------------ styles and files

    /// <summary>An automatic style holding these properties; identical ones are shared.</summary>
    string Style(string family, params XElement[] properties)
    {
        var key = family + string.Concat(properties.Select(p => p.ToString(SaveOptions.DisableFormatting)));
        if (_styleCache.TryGetValue(key, out var existing)) return existing;
        var prefix = family switch { "graphic" => "gr", "paragraph" => "P", "text" => "T", "drawing-page" => "dp", "table-cell" => "ce", _ => "co" };
        var name = $"{prefix}{++_styleCount}";
        _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", family), properties));
        _styleCache[key] = name;
        return name;
    }

    string PictureFile(DImage image, Thickness crop)
    {
        if (crop == default && _pictures.TryGetValue(image, out var known)) return known;
        var data = image.Data;
        var ext = image.Ext.ToLowerInvariant() switch { "jpeg" => "jpg", var e => e };
        if (crop != default && FlowBridge.Decode(data) is { } bitmap)
        {
            // ODF's clipping uses lengths of the original picture; cropping the pixels is simpler and exact.
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            int l = (int)(Math.Max(0, crop.Left) * w), t = (int)(Math.Max(0, crop.Top) * h);
            int cw = w - l - (int)(Math.Max(0, crop.Right) * w), ch = h - t - (int)(Math.Max(0, crop.Bottom) * h);
            if (cw > 1 && ch > 1)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, new Int32Rect(l, t, cw, ch))));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                data = ms.ToArray();
                ext = "png";
            }
        }
        else if (ext is not ("png" or "jpg" or "gif" or "bmp" or "svg" or "tiff" or "emf" or "wmf"))
        {
            data = PptxWriter.ToPng(data) ?? data;
            ext = "png";
        }
        var path = $"Pictures/image{_files.Count + 1}.{ext}";
        var entry = _zip.CreateEntry(path, CompressionLevel.NoCompression);
        using (var stream = entry.Open()) stream.Write(data);
        _files.Add(path);
        if (crop == default) _pictures[image] = path;
        return path;
    }

    static object[] Namespaces() =>
    [
        new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "style", St),
        new XAttribute(XNamespace.Xmlns + "text", Text), new XAttribute(XNamespace.Xmlns + "table", Table),
        new XAttribute(XNamespace.Xmlns + "draw", Draw), new XAttribute(XNamespace.Xmlns + "fo", Fo),
        new XAttribute(XNamespace.Xmlns + "xlink", XLink), new XAttribute(XNamespace.Xmlns + "svg", Svg),
        new XAttribute(XNamespace.Xmlns + "presentation", Pres),
        new XAttribute(XNamespace.Xmlns + "meta", "urn:oasis:names:tc:opendocument:xmlns:meta:1.0"),
        new XAttribute(XNamespace.Xmlns + "dc", "http://purl.org/dc/elements/1.1/"),
    ];

    void Save(string path, XElement root)
    {
        SafeXml.Clean(root);
        var entry = _zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        // No indentation: ODF treats whitespace inside paragraphs as text.
        using var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
    }

    static string Hex(Color c) => "#" + Units.Hex(c).ToLowerInvariant();
    static string Len(double points) => (points / 72 * 2.54).ToString("0.####", CultureInfo.InvariantCulture) + "cm";
    static string Percent(double fraction) => (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    static long Hundredths(double points) => (long)Math.Round(points * 100);
}
