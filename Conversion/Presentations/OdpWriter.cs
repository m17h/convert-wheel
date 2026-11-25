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
    /// <summary>LibreOffice's extensions, for what it understands beyond the standard (or used to, before it kept to it).</summary>
    static readonly XNamespace LoExt = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";
    static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    readonly ZipArchive _zip;
    readonly Deck _deck;
    readonly CancellationToken _ct;
    readonly List<XElement> _autoStyles = [];
    readonly List<XElement> _commonStyles = [];
    readonly Dictionary<string, string> _styleCache = [];
    readonly Dictionary<(DImage image, Thickness crop, bool flipH, bool flipV), string> _pictures = [];
    readonly Dictionary<string, string> _fillImages = [];
    readonly List<string> _files = [];
    int _styleCount;
    bool _dash;
    SlideNumbering _numbering = new();
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

        void Marker(ArrowHead kind, string box, string outline) => _commonStyles.Add(new XElement(Draw + "marker", new XAttribute(Draw + "name", MarkerName(kind)),
            new XAttribute(Svg + "viewBox", box), new XAttribute(Svg + "d", outline)));
        Marker(ArrowHead.Triangle, "0 0 20 30", "M10 0l-10 30h20z");
        Marker(ArrowHead.Stealth, "0 0 20 30", "M10 0l-10 30 10-9 10 9z");
        Marker(ArrowHead.Open, "0 0 20 20", "M10 0l-10 17 3 3 7-12 7 12 3-3z");
        Marker(ArrowHead.Diamond, "0 0 20 20", "M10 0l-10 10 10 10 10-10z");
        Marker(ArrowHead.Oval, "0 0 20 20", "M20 10c0 5.5-4.5 10-10 10s-10-4.5-10-10 4.5-10 10-10 10 4.5 10 10z");

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
                new XAttribute(Manifest + "media-type", f.EndsWith(".xml") ? "text/xml" : f.EndsWith(".png") ? "image/png" : f.EndsWith(".jpg") ? "image/jpeg" : f.EndsWith(".svg") ? "image/svg+xml"
                : f.EndsWith(".emf") ? "image/x-emf" : f.EndsWith(".wmf") ? "image/x-wmf" : "image/" + Path.GetExtension(f).TrimStart('.'))));
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
        if (slide.Hidden) props.SetAttributeValue(Pres + "visibility", "hidden");
        var page = new XElement(Draw + "page", new XAttribute(Draw + "name", $"page{index + 1}"),
            new XAttribute(Draw + "style-name", Style("drawing-page", props)), new XAttribute(Draw + "master-page-name", master));
        foreach (var shape in slide.Shapes)
        {
            _ct.ThrowIfCancellationRequested();
            try { page.Add(Shape(shape)); }
            catch (Exception ex) when (SlideLimits.CanSkip(ex))
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
        // Half a turn is the same as mirroring both ways, which readers place more reliably.
        if (Math.Abs(Math.Abs(shape.Rotation % 360) - 180) < 0.01)
        {
            shape.Rotation = 0;
            shape.FlipH = !shape.FlipH;
            shape.FlipV = !shape.FlipV;
        }
        if (shape.Picture != null && shape.Geometry == ShapeGeometry.Rect) return Picture(shape);
        if (shape.Text.Count == 1 && shape.Text[0] is DTable table) return TableFrame(shape, table);
        if (shape.Geometry is ShapeGeometry.Line) return Line(shape);
        if (shape.Geometry is ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve) return Connector(shape);

        var graphic = new XElement(St + "graphic-properties");
        // A picture cut to a shape is that shape filled with the picture.
        var fill = shape.Picture != null ? new SlideFill { Image = shape.Picture, Crop = shape.Crop, Opacity = shape.PictureOpacity } : shape.Fill;
        if (fill != null) FillProperties(graphic, fill, shape);
        else graphic.Add(new XAttribute(Draw + "fill", "none"));
        Stroke(graphic, shape);
        graphic.Add(
            new XAttribute(Draw + "textarea-vertical-align", shape.Anchor switch { TextAnchor.Middle => "middle", TextAnchor.Bottom => "bottom", _ => "top" }),
            new XAttribute(Draw + "auto-grow-height", "false"), new XAttribute(Draw + "auto-grow-width", "false"),
            new XAttribute(Fo + "padding-left", Len(shape.Insets.Left)), new XAttribute(Fo + "padding-top", Len(shape.Insets.Top)),
            new XAttribute(Fo + "padding-right", Len(shape.Insets.Right)), new XAttribute(Fo + "padding-bottom", Len(shape.Insets.Bottom)),
            new XAttribute(Fo + "wrap-option", shape.Wrap ? "wrap" : "no-wrap"),
            new XAttribute(Draw + "shadow", "hidden"));
        if (shape.ShrinkToFit) graphic.Add(new XAttribute(St + "shrink-to-fit", "true"));
        if (shape.TextRotation is 90 or 270) graphic.Add(new XAttribute(St + "writing-mode", "tb-rl"));

        var el = new XElement(Draw + "custom-shape", new XAttribute(Draw + "style-name", Style("graphic", graphic)));
        if (shape.Role == "title") el.Add(new XAttribute(Draw + "name", "Title"));
        Place(el, shape);
        _numbering = new SlideNumbering();
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
        // Mirrored shapes are drawn mirrored; upside-down ones turn their text too.
        bool upsideDown = shape.FlipV ^ shape.TextRotation is 180 or 270;
        if (shape.HasText && upsideDown) geometry.Add(new XAttribute(Draw + "text-rotate-angle", "180"));
        var outline = SlideRenderer.Outline(shape, new Rect(0, 0, Units.ToDip(w), Units.ToDip(h)));
        if (outline != null)
        {
            var sb = new StringBuilder();
            string P(Point p)
            {
                double x = Units.ToPoints(p.X), y = Units.ToPoints(p.Y);
                return $"{Hundredths(shape.FlipH ? w - x : x)} {Hundredths(shape.FlipV ? h - y : y)}";
            }
            foreach (var (points, closed, filled) in SlideRenderer.Flatten(outline))
            {
                sb.Append("M ").Append(P(points[0]));
                if (points.Count > 1) sb.Append(" L ").Append(string.Join(" ", points.Skip(1).Select(P)));
                if (closed) sb.Append(" Z");
                if (!filled) sb.Append(" F");
                sb.Append(" N ");
            }
            geometry.Add(new XAttribute(Draw + "enhanced-path", sb.ToString().TrimEnd()));
        }
        return geometry;
    }

    XElement Line(SlideShape shape)
    {
        var r = shape.Bounds;
        bool flipH = shape.FlipH, flipV = shape.FlipV;
        var turn = new RotateTransform(shape.Rotation, r.X + r.Width / 2, r.Y + r.Height / 2);
        var from = turn.Transform(new Point(flipH ? r.Right : r.X, flipV ? r.Bottom : r.Y));
        var to = turn.Transform(new Point(flipH ? r.X : r.Right, flipV ? r.Y : r.Bottom));
        var (head, tail) = (shape.HeadEnd, shape.TailEnd);
        if (to.X < from.X)
        {
            // Not every reader follows lines that run backwards.
            (from, to) = (to, from);
            (head, tail) = (tail, head);
        }
        var graphic = new XElement(St + "graphic-properties", new XAttribute(Draw + "fill", "none"));
        Stroke(graphic, new SlideShape { Line = shape.Line, LineWidth = shape.LineWidth, Dashed = shape.Dashed, HeadEnd = head, TailEnd = tail });
        return new XElement(Draw + "line", new XAttribute(Draw + "style-name", Style("graphic", graphic)),
            new XAttribute(Svg + "x1", Len(from.X)), new XAttribute(Svg + "y1", Len(from.Y)),
            new XAttribute(Svg + "x2", Len(to.X)), new XAttribute(Svg + "y2", Len(to.Y)),
            new XElement(Text + "p"));
    }

    /// <summary>Elbow and curved connectors as plain lines of the same course, which keep their arrow heads everywhere.</summary>
    XElement Connector(SlideShape shape)
    {
        var graphic = new XElement(St + "graphic-properties", new XAttribute(Draw + "fill", "none"));
        Stroke(graphic, shape);
        double w = Math.Max(shape.Bounds.Width, 0.01), h = Math.Max(shape.Bounds.Height, 0.01);
        var box = new XAttribute(Svg + "viewBox", $"0 0 {Math.Max(1, Hundredths(w))} {Math.Max(1, Hundredths(h))}");
        long X(double fraction) => Hundredths(w * (shape.FlipH ? 1 - fraction : fraction));
        long Y(double fraction) => Hundredths(h * (shape.FlipV ? 1 - fraction : fraction));
        double bend = shape.Adjust ?? 0.5;
        var el = new XElement(Draw + "path", box, new XAttribute(Svg + "d", $"M{X(0)} {Y(0)}" + shape.Geometry switch
        {
            ShapeGeometry.Curve => $"C{X(bend)} {Y(0)} {X(bend)} {Y(1)} {X(1)} {Y(1)}",
            ShapeGeometry.Corner => $"L{X(1)} {Y(0)}L{X(1)} {Y(1)}",
            _ => $"L{X(bend)} {Y(0)}L{X(bend)} {Y(1)}L{X(1)} {Y(1)}",
        }));
        el.Add(new XAttribute(Draw + "style-name", Style("graphic", graphic)));
        Place(el, shape);
        return el;
    }

    XElement Picture(SlideShape shape)
    {
        // Upside down is mirrored and turned half round: only sideways mirroring is understood everywhere.
        // Upright pictures are simply stored upside down.
        var (area, crop) = SlideShape.PictureArea(shape.Bounds, shape.Crop);
        bool upright = Math.Abs(shape.Rotation % 360) < 0.01, stored = upright && shape.FlipV;
        bool mirror = !stored && shape.FlipH ^ shape.FlipV;
        var placed = new SlideShape { Bounds = area, Rotation = shape.Rotation };
        if (area != shape.Bounds)
        {
            // Cropped outwards: the picture is only part of its frame, wherever that has been turned to.
            var whole = new SlideShape { Bounds = area };
            var middle = new Point(shape.Bounds.X + shape.Bounds.Width / 2, shape.Bounds.Y + shape.Bounds.Height / 2);
            var turn = Matrix.Identity;
            if (shape.FlipH) turn.ScaleAt(-1, 1, middle.X, middle.Y);
            if (shape.FlipV) turn.ScaleAt(1, -1, middle.X, middle.Y);
            turn.RotateAt(shape.Rotation, middle.X, middle.Y);
            whole.Transform(turn);
            placed.Bounds = whole.Bounds;
        }
        if (shape.FlipV && !stored) placed.Rotation += 180;
        var graphic = new XElement(St + "graphic-properties", new XAttribute(Draw + "fill", "none"));
        if (mirror) graphic.Add(new XAttribute(St + "mirror", "horizontal"));
        if (shape.PictureOpacity < 0.999) graphic.Add(new XAttribute(Draw + "image-opacity", Percent(shape.PictureOpacity)));
        Stroke(graphic, shape);
        var frame = new XElement(Draw + "frame", new XAttribute(Draw + "style-name", Style("graphic", graphic)));
        Place(frame, placed);
        var href = stored ? PictureFile(shape.Picture!, crop, shape.FlipH, true) : PictureFile(shape.Picture!, crop);
        frame.Add(new XElement(Draw + "image", new XAttribute(XLink + "href", href), new XAttribute(XLink + "type", "simple"),
            new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad"), new XElement(Text + "p")));
        return frame;
    }

    XElement TableFrame(SlideShape shape, DTable table)
    {
        var frame = new XElement(Draw + "frame");
        Place(frame, shape);
        var grid = new TableGrid(table);
        var t = new XElement(Table + "table");
        foreach (var cw in grid.Widths(shape.Bounds.Width))
            t.Add(new XElement(Table + "table-column", new XAttribute(Table + "style-name",
                Style("table-column", new XElement(St + "table-column-properties", new XAttribute(St + "column-width", Len(cw)))))));
        var plain = table.Borders ? new CellEdge(0.75, table.BorderColor ?? Color.FromRgb(0x80, 0x80, 0x80)) : new CellEdge(0, null);
        double known = table.Rows.Sum(r => r.Height ?? 0);
        int unknown = table.Rows.Count(r => r.Height is not > 0);
        double share = unknown > 0 ? Math.Max(0, shape.Bounds.Height - known) / unknown : 0;
        for (int r = 0; r < grid.Rows; r++)
        {
            var row = table.Rows[r];
            double height = row.Height is > 0 and var h ? h : share;
            var tr = new XElement(Table + "table-row");
            if (height > 0)
                tr.Add(new XAttribute(Table + "style-name", Style("table-row", new XElement(St + "table-row-properties", new XAttribute(St + "row-height", Len(height))))));
            for (int c = 0; c < grid.Columns; c++)
            {
                var place = grid.At(r, c);
                if (place != null && (place.Row != r || place.Column != c))
                {
                    tr.Add(new XElement(Table + "covered-table-cell"));
                    continue;
                }
                var tc = new XElement(Table + "table-cell", new XAttribute(Table + "style-name", CellStyle(place, grid, table, plain)));
                if (place is { ColumnSpan: > 1 }) tc.Add(new XAttribute(Table + "number-columns-spanned", place.ColumnSpan));
                if (place is { RowSpan: > 1 }) tc.Add(new XAttribute(Table + "number-rows-spanned", place.RowSpan));
                _numbering = new SlideNumbering();
                foreach (var p in place?.Cell.Blocks.OfType<DParagraph>() ?? []) tc.Add(TextBlock(p, row.Header));
                tr.Add(tc);
            }
            t.Add(tr);
        }
        frame.Add(t);
        return frame;
    }

    /// <summary>
    /// A cell's look as the standard says it, plus the drawing and paragraph properties LibreOffice went by before it
    /// read cell properties in presentations.
    /// </summary>
    string CellStyle(TableGrid.Place? place, TableGrid grid, DTable table, CellEdge plain)
    {
        var cell = place?.Cell;
        var look = cell?.Look;
        var box = new XElement(St + "table-cell-properties");
        var drawn = new XElement(LoExt + "graphic-properties");
        var lines = new XElement(St + "paragraph-properties");
        if (cell?.Shading is { } shade)
        {
            box.Add(new XAttribute(Fo + "background-color", Hex(shade)));
            drawn.Add(new XAttribute(Draw + "fill", "solid"), new XAttribute(Draw + "fill-color", Hex(shade)));
            if (shade.A < 255) drawn.Add(new XAttribute(Draw + "opacity", Percent(shade.A / 255.0)));
        }
        else drawn.Add(new XAttribute(Draw + "fill", "none"));
        foreach (var (side, where) in new[] { ("left", TableGrid.Side.Left), ("right", TableGrid.Side.Right), ("top", TableGrid.Side.Top), ("bottom", TableGrid.Side.Bottom) })
        {
            if ((place == null ? plain : grid.Edge(place, where, plain)) is not { } line) continue;
            var text = line.Color is { } color ? $"{Points(Math.Max(0.25, line.Width))} solid {Hex(color)}" : "none";
            box.Add(new XAttribute(Fo + $"border-{side}", text));
            lines.Add(new XAttribute(Fo + $"border-{side}", text));
        }
        var pad = look?.Padding ?? table.CellPadding ?? new Thickness(7.2, 3.6, 7.2, 3.6);
        foreach (var (side, width) in new[] { ("left", pad.Left), ("right", pad.Right), ("top", pad.Top), ("bottom", pad.Bottom) })
        {
            box.Add(new XAttribute(Fo + $"padding-{side}", Len(width)));
            drawn.Add(new XAttribute(Fo + $"padding-{side}", Len(width)));
        }
        var anchor = look?.Anchor switch { TextAnchor.Middle => "middle", TextAnchor.Bottom => "bottom", _ => "top" };
        box.Add(new XAttribute(St + "vertical-align", anchor));
        drawn.Add(new XAttribute(Draw + "textarea-vertical-align", anchor));
        return Style("table-cell", drawn, box, lines);
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
            if (!_dash) _commonStyles.Add(new XElement(Draw + "stroke-dash", new XAttribute(Draw + "name", "Dash"), new XAttribute(Draw + "style", "rect"),
                new XAttribute(Draw + "dots1", 1), new XAttribute(Draw + "dots1-length", "300%"), new XAttribute(Draw + "distance", "200%")));
            _dash = true;
            graphic.Add(new XAttribute(Draw + "stroke-dash", "Dash"));
        }
        double arrow = Math.Max(6, shape.LineWidth * 3);
        if (shape.HeadEnd != ArrowHead.None)
            graphic.Add(new XAttribute(Draw + "marker-start", MarkerName(shape.HeadEnd)), new XAttribute(Draw + "marker-start-width", Len(arrow)),
                new XAttribute(Draw + "marker-start-center", shape.HeadEnd is ArrowHead.Oval or ArrowHead.Diamond ? "true" : "false"));
        if (shape.TailEnd != ArrowHead.None)
            graphic.Add(new XAttribute(Draw + "marker-end", MarkerName(shape.TailEnd)), new XAttribute(Draw + "marker-end-width", Len(arrow)),
                new XAttribute(Draw + "marker-end-center", shape.TailEnd is ArrowHead.Oval or ArrowHead.Diamond ? "true" : "false"));
    }

    static string MarkerName(ArrowHead kind) => kind switch
    {
        ArrowHead.Stealth => "Stealth", ArrowHead.Open => "OpenArrow", ArrowHead.Diamond => "Diamond", ArrowHead.Oval => "Oval", _ => "Arrow",
    };

    /// <param name="shape">The shape being filled: a mirrored one mirrors its picture.</param>
    void FillProperties(XElement props, SlideFill fill, SlideShape? shape = null)
    {
        if (fill.Image != null)
        {
            var href = PictureFile(fill.Image, fill.Tile == null ? fill.Crop : default, shape?.FlipH == true, shape?.FlipV == true);
            if (!_fillImages.TryGetValue(href, out var name))
            {
                _fillImages[href] = name = $"Bitmap{_commonStyles.Count + 1}";
                _commonStyles.Add(new XElement(Draw + "fill-image", new XAttribute(Draw + "name", name),
                    new XAttribute(XLink + "href", href), new XAttribute(XLink + "type", "simple"),
                    new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad")));
            }
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
            var kind = new XAttribute(Draw + "style", fill.RadialGradient ? "radial" : "linear");
            var gradient = new XElement(Draw + "gradient", new XAttribute(Draw + "name", name),
                kind,
                new XAttribute(Draw + "cx", "50%"), new XAttribute(Draw + "cy", "50%"),
                // A radial gradient starts at its edge in ODF but at its centre in our model.
                new XAttribute(Draw + "start-color", Hex(fill.RadialGradient ? stops[^1].Color : stops[0].Color)),
                new XAttribute(Draw + "end-color", Hex(fill.RadialGradient ? stops[0].Color : stops[^1].Color)),
                new XAttribute(Draw + "start-intensity", "100%"), new XAttribute(Draw + "end-intensity", "100%"),
                new XAttribute(Draw + "angle", $"{angle}deg"), new XAttribute(Draw + "border", "0%"));
            // The standard has two colours; LibreOffice reads the rest from its extension.
            if (stops.Count > 2)
                foreach (var (position, color) in fill.RadialGradient ? stops.AsEnumerable().Reverse().Select(s => (1 - s.Position, s.Color)) : stops)
                    gradient.Add(new XElement(LoExt + "gradient-stop", new XAttribute(Svg + "offset", position.ToString("0.####", CultureInfo.InvariantCulture)),
                        new XAttribute(LoExt + "color-type", "rgb"), new XAttribute(LoExt + "color-value", Hex(color))));
            _commonStyles.Add(gradient);
            props.Add(new XAttribute(Draw + "fill", "gradient"), new XAttribute(Draw + "fill-gradient-name", name));
            double first = stops[0].Color.A / 255.0, last = stops[^1].Color.A / 255.0;
            if (first != last)
            {
                var fade = $"Fade{_commonStyles.Count + 1}";
                _commonStyles.Add(new XElement(Draw + "opacity", new XAttribute(Draw + "name", fade), new XAttribute(kind),
                    new XAttribute(Draw + "cx", "50%"), new XAttribute(Draw + "cy", "50%"),
                    new XAttribute(Draw + "start", Percent(fill.RadialGradient ? last : first)), new XAttribute(Draw + "end", Percent(fill.RadialGradient ? first : last)),
                    new XAttribute(Draw + "angle", $"{angle}deg"), new XAttribute(Draw + "border", "0%")));
                props.Add(new XAttribute(Draw + "opacity-name", fade));
            }
            else if (first < 0.999) props.Add(new XAttribute(Draw + "opacity", Percent(first)));
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
            new XAttribute(Fo + "text-align", p.Align switch
            {
                TextAlignment.Center => "center",
                // Apps disagree about where right-to-left text "starts", so its side is spelled out.
                TextAlignment.Right => p.RightToLeft ? "right" : "end",
                TextAlignment.Justify => "justify",
                _ => p.RightToLeft ? "left" : "start",
            }),
            new XAttribute(Fo + "margin-left", Len(p.IndentLeft)), new XAttribute(Fo + "text-indent", Len(p.IndentFirst)),
            new XAttribute(Fo + "margin-top", Len(p.SpaceBefore)), new XAttribute(Fo + "margin-bottom", Len(p.SpaceAfter)));
        if (p.RightToLeft) paraProps.Add(new XAttribute(St + "writing-mode", "rl-tb"));
        if (Math.Abs(p.LineSpacing - 1) > 0.01) paraProps.Add(new XAttribute(Fo + "line-height", Percent(p.LineSpacing)));
        var para = new XElement(Text + "p", new XAttribute(Text + "style-name", Style("paragraph", paraProps,
            new XElement(St + "text-properties", new XAttribute(Fo + "font-size", Points(p.MarkSize ?? 18))))));

        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DText t when t.Text.Length > 0:
                    var span = new XElement(Text + "span", new XAttribute(Text + "style-name", Style("text", TextProperties(t.Style, bold))));
                    var words = span;
                    if (SlideLimits.Link(t.Link) is { } address)
                        span.Add(words = new XElement(Text + "a", new XAttribute(XLink + "href", address), new XAttribute(XLink + "type", "simple")));
                    AddText(words, t.Text);
                    para.Add(span);
                    break;
                case DLineBreak:
                    para.Add(new XElement(Text + "line-break"));
                    break;
                case DField:
                    para.Add(new XElement(Text + "page-number"));
                    break;
            }
        }
        var (format, around, number, _) = _numbering.Next(p);
        if (p.List == ListKind.None || string.IsNullOrEmpty(p.ListLabel)) return para;

        // The marker's look and position follow the paragraph's indents.
        var level = new XElement(p.List == ListKind.Number ? Text + "list-level-style-number" : Text + "list-level-style-bullet",
            new XAttribute(Text + "level", 1));
        if (p.List == ListKind.Number)
        {
            level.Add(new XAttribute(Text + "start-value", number), new XAttribute(St + "num-format", format), new XAttribute(St + "num-suffix", around[(around.IndexOf('x') + 1)..]));
            if (around.StartsWith('(')) level.Add(new XAttribute(St + "num-prefix", "("));
            para = new XElement(Text + "list-item", new XAttribute(Text + "start-value", number), para);
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

    static XElement TextProperties(TextStyle s, bool bold)
    {
        var props = new XElement(St + "text-properties", new XAttribute(Fo + "font-size", Points(s.Size ?? 18)));
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
        var prefix = family switch { "graphic" => "gr", "paragraph" => "P", "text" => "T", "drawing-page" => "dp", "table-cell" => "ce", "table-row" => "ro", _ => "co" };
        var name = $"{prefix}{++_styleCount}";
        _autoStyles.Add(new XElement(St + "style", new XAttribute(St + "name", name), new XAttribute(St + "family", family), properties));
        _styleCache[key] = name;
        return name;
    }

    string PictureFile(DImage image, Thickness crop, bool flipH = false, bool flipV = false)
    {
        var key = (image, crop, flipH, flipV);
        if (_pictures.TryGetValue(key, out var known)) return known;
        var data = image.Data;
        var ext = image.Ext.ToLowerInvariant() switch { "jpeg" => "jpg", "tif" => "tiff", var e => e };
        // ODF's clipping uses lengths of the original picture; cropping the pixels is simpler and exact.
        if ((crop != default || flipH || flipV) && FlowBridge.Decode(data) is { } bitmap)
        {
            BitmapSource part = bitmap;
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            int l = (int)(Math.Clamp(crop.Left, 0, 1) * w), t = (int)(Math.Clamp(crop.Top, 0, 1) * h);
            int cw = w - l - (int)(Math.Clamp(crop.Right, 0, 1) * w), ch = h - t - (int)(Math.Clamp(crop.Bottom, 0, 1) * h);
            if (cw > 1 && ch > 1 && (cw < w || ch < h)) part = new CroppedBitmap(part, new Int32Rect(l, t, cw, ch));
            if (flipH || flipV) part = new TransformedBitmap(part, new ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1));
            BitmapEncoder encoder = ext == "jpg" ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(part));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            data = ms.ToArray();
            if (ext != "jpg") ext = "png";
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
        _pictures[key] = path;
        return path;
    }

    static object[] Namespaces() =>
    [
        new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "style", St),
        new XAttribute(XNamespace.Xmlns + "text", Text), new XAttribute(XNamespace.Xmlns + "table", Table),
        new XAttribute(XNamespace.Xmlns + "draw", Draw), new XAttribute(XNamespace.Xmlns + "fo", Fo),
        new XAttribute(XNamespace.Xmlns + "xlink", XLink), new XAttribute(XNamespace.Xmlns + "svg", Svg),
        new XAttribute(XNamespace.Xmlns + "presentation", Pres), new XAttribute(XNamespace.Xmlns + "loext", LoExt),
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
    static string Points(double points) => points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";
    static string Percent(double fraction) => (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    static long Hundredths(double points) => (long)Math.Round(points * 100);
}
