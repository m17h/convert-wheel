using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Draws slides as WPF visuals: shapes and pictures directly, text through the document engine's
/// layout. The same visual becomes a vector PDF page or a picture. Must run on an STA thread.
/// </summary>
internal sealed class SlideRenderer(Deck deck)
{
    readonly Dictionary<DImage, BitmapSource?> _bitmaps = [];
    bool _transparent;

    /// <summary>Draws loose shapes (a Word drawing, a group) as one transparent picture of the given size in points.</summary>
    public static DImage ToPicture(IEnumerable<SlideShape> shapes, double widthPt, double heightPt)
    {
        var deck = new Deck { Width = Math.Max(1, widthPt), Height = Math.Max(1, heightPt) };
        var slide = new Slide();
        slide.Shapes.AddRange(shapes);
        var renderer = new SlideRenderer(deck) { _transparent = true };
        var (visual, release) = renderer.Build(slide);
        try
        {
            double w = Units.ToDip(deck.Width), h = Units.ToDip(deck.Height);
            double scale = Math.Clamp(1600 / Math.Max(w, h), 1, 4);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            png.Save(ms);
            return new DImage(ms.ToArray(), "png", deck.Width, deck.Height);
        }
        finally { release(); }
    }

    public void ToPdf(string output, IProgress<double?> progress, CancellationToken ct)
    {
        int count = deck.Slides.Count;
        PdfRenderer.RenderVisuals(output, deck.Width, deck.Height, count, i =>
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((double)i / count);
            return Build(deck.Slides[i]);
        }, deck.Title);
    }

    /// <returns>The picture, or a folder of them when there are several slides.</returns>
    public string ToImages(string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        int count = deck.Slides.Count;
        return Outputs.Pages(output, count, (i, path) =>
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((double)i / count);
            var (visual, release) = Build(deck.Slides[i]);
            try { Save(Rasterize(visual, 1920), path, ext); }
            finally { release(); }
        });
    }

    BitmapSource Rasterize(Visual visual, int pixelWidth)
    {
        double width = Units.ToDip(deck.Width), height = Units.ToDip(deck.Height);
        double dpi = 96.0 * pixelWidth / width;
        var bitmap = new RenderTargetBitmap(pixelWidth, (int)Math.Round(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static void Save(BitmapSource image, string path, string ext)
    {
        BitmapEncoder encoder = ext switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "webp" => new PngBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        if (ext == "webp")
        {
            File.WriteAllBytes(path, WebP.Encode(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0), 90));
            return;
        }
        encoder.Frames.Add(BitmapFrame.Create(ext == "jpg" ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0) : image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ------------------------------------------------------------------ building a slide

    /// <returns>The slide's visual, and an action that frees its text layouts once it has been drawn.</returns>
    public (Visual visual, Action release) Build(Slide slide)
    {
        var pages = new List<DocumentPage>();
        var root = new ContainerVisual();
        var area = new Rect(0, 0, Units.ToDip(deck.Width), Units.ToDip(deck.Height));

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            if (!_transparent) dc.DrawRectangle(Brushes.White, null, area);
            Paint(dc, slide.Background, new RectangleGeometry(area), area);
        }
        root.Children.Add(background);

        foreach (var shape in slide.Shapes)
        {
            try { AddShape(root, shape, pages); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Log.Write($"Skipped drawing a shape: {ex.Message}");
            }
        }
        return (root, () => { foreach (var p in pages) p.Dispose(); });
    }

    void AddShape(ContainerVisual root, SlideShape shape, List<DocumentPage> pages)
    {
        var r = Dip(shape.Bounds);
        var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        // Shapes and pictures mirror with their flips; text only turns.
        var shapeMatrix = Matrix.Identity;
        if (shape.FlipH) shapeMatrix.ScaleAt(-1, 1, center.X, center.Y);
        if (shape.FlipV) shapeMatrix.ScaleAt(1, -1, center.X, center.Y);
        if (shape.Rotation != 0) shapeMatrix.RotateAt(shape.Rotation, center.X, center.Y);

        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(shapeMatrix));
            var geometry = Outline(shape, r);
            if (shape.Picture != null && Bitmap(shape.Picture) is { } bitmap)
            {
                var picture = Cropped(bitmap, shape.Crop);
                // Pictures cut to a shape (circle portraits, rounded corners) keep that outline.
                dc.DrawImage(shape.Geometry == ShapeGeometry.Rect || geometry == null ? picture : ShapedPicture(picture, new SlideFill(), geometry, r), r);
            }
            else if (geometry != null && !IsConnector(shape))
            {
                Paint(dc, shape.Fill, geometry, r);
            }
            if (shape.Line is { A: > 0 } line && geometry != null)
            {
                var pen = new Pen(new SolidColorBrush(line), Math.Max(0.5, Units.ToDip(shape.LineWidth)))
                {
                    DashStyle = shape.Dashed ? DashStyles.Dash : DashStyles.Solid,
                    LineJoin = PenLineJoin.Round,
                };
                dc.DrawGeometry(null, pen, geometry);
                if (IsConnector(shape)) ArrowHeads(dc, shape, r, line, pen.Thickness);
            }
            dc.Pop();
        }
        root.Children.Add(drawing);

        if (shape.HasText) AddText(root, shape, r, center, pages);
    }

    void AddText(ContainerVisual root, SlideShape shape, Rect r, Point center, List<DocumentPage> pages)
    {
        bool sideways = shape.TextRotation is 90 or 270;
        double boxWidth = sideways ? r.Height : r.Width, boxHeight = sideways ? r.Width : r.Height;
        var insets = shape.Insets;
        double shift = 0;
        double layoutWidth = Units.ToPoints(boxWidth);
        if (!shape.Wrap)
        {
            // Unwrapped text runs past its box rather than breaking; give it room, keeping its alignment.
            const double Extra = 400;
            var align = shape.Text.OfType<DParagraph>().FirstOrDefault()?.Align ?? TextAlignment.Left;
            shift = align switch { TextAlignment.Center => -Extra / 2, TextAlignment.Right => -Extra, _ => 0 };
            layoutWidth += Extra;
        }

        var doc = new DDocument { Font = deck.Font, FontSize = 18, PageHeight = Math.Max(deck.Height, 200) * 20 };
        var (page, height) = Layout(doc, shape.Text, layoutWidth, insets);
        // Shrink to fit, as PowerPoint does for placeholders whose text overflows.
        for (int attempt = 0; shape.ShrinkToFit && height > boxHeight * 1.02 && attempt < 8; attempt++)
        {
            page.Dispose();
            Scale(shape.Text, 0.92);
            (page, height) = Layout(doc, shape.Text, layoutWidth, insets);
        }
        pages.Add(page);

        double offset = shape.Anchor switch
        {
            TextAnchor.Middle => (boxHeight - height) / 2,
            TextAnchor.Bottom => boxHeight - height,
            _ => 0,
        };
        var m = Matrix.Identity;
        m.Translate(center.X - boxWidth / 2 + Units.ToDip(shift), center.Y - boxHeight / 2 + offset);
        double turn = shape.Rotation + shape.TextRotation + (shape.FlipV ? 180 : 0);
        if (turn % 360 != 0) m.RotateAt(turn, center.X, center.Y);

        var holder = new ContainerVisual { Transform = new MatrixTransform(m) };
        holder.Children.Add(page.Visual);
        root.Children.Add(holder);
    }

    static (DocumentPage page, double height) Layout(DDocument doc, List<DBlock> blocks, double width, Thickness insets)
    {
        var flow = FlowBridge.PartFlow(new FlowBridge.FlowContext(doc), blocks, width, insets);
        var (page, height) = PdfRenderer.Layout(flow);
        return (page, height);
    }

    static void Scale(List<DBlock> blocks, double factor)
    {
        foreach (var block in blocks)
        {
            if (block is DParagraph p)
            {
                foreach (var t in p.Inlines.OfType<DText>()) t.Style = t.Style with { Size = (t.Style.Size ?? 18) * factor };
                if (p.MarkSize is { } mark) p.MarkSize = mark * factor;
                p.SpaceBefore *= factor;
                p.SpaceAfter *= factor;
            }
            else if (block is DTable t)
                foreach (var cell in t.Rows.SelectMany(row => row.Cells)) Scale(cell.Blocks, factor);
        }
    }

    // ------------------------------------------------------------------ fills and pictures

    void Paint(DrawingContext dc, SlideFill? fill, Geometry geometry, Rect bounds)
    {
        if (fill == null) return;
        if (fill.Image != null)
        {
            if (Bitmap(fill.Image) is not { } bitmap || bounds.Width < 1 || bounds.Height < 1) return;
            if (fill.Tile == null && fill.Opacity >= 0.999 && geometry is RectangleGeometry { RadiusX: 0, Transform: null } rect && rect.Rect == bounds)
            {
                dc.DrawImage(bitmap, bounds); // plain stretched picture: keep it as is
                return;
            }
            dc.DrawImage(ShapedPicture(bitmap, fill, geometry, bounds), bounds);
            return;
        }
        if (Brush(fill) is { } brush) dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>
    /// A picture fill cut to the shape's outline (and tiled if asked), baked into one transparent bitmap
    /// so pictures and PDF pages show the same thing.
    /// </summary>
    static BitmapSource ShapedPicture(BitmapSource bitmap, SlideFill fill, Geometry geometry, Rect bounds)
    {
        double scale = Math.Min(2, 2400 / Math.Max(bounds.Width, bounds.Height));
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill, Opacity = fill.Opacity };
        if (fill.Tile is { } tile)
        {
            double w = bitmap.PixelWidth * 96 / bitmap.DpiX * tile, h = bitmap.PixelHeight * 96 / bitmap.DpiY * tile;
            brush.TileMode = TileMode.Tile;
            brush.ViewportUnits = BrushMappingMode.Absolute;
            brush.Viewport = new Rect(bounds.X, bounds.Y, Math.Max(4, w), Math.Max(4, h));
        }
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.DrawGeometry(brush, null, geometry);
        }
        var target = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    static Brush? Brush(SlideFill fill)
    {
        if (fill.Gradient is { Count: > 0 } stops)
        {
            var collection = new GradientStopCollection(stops.Select(s => new GradientStop(s.Color, s.Position)));
            if (fill.RadialGradient) return new RadialGradientBrush(collection);
            double a = fill.GradientAngle * Math.PI / 180;
            double dx = Math.Cos(a) / 2, dy = Math.Sin(a) / 2;
            return new LinearGradientBrush(collection, new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
        }
        return fill.Color is { A: > 0 } c ? new SolidColorBrush(c) : null;
    }

    BitmapSource? Bitmap(DImage image)
    {
        if (!_bitmaps.TryGetValue(image, out var bitmap))
            _bitmaps[image] = bitmap = FlowBridge.Decode(image.Data);
        return bitmap;
    }

    static BitmapSource Cropped(BitmapSource source, Thickness crop)
    {
        if (crop == default) return source;
        int w = source.PixelWidth, h = source.PixelHeight;
        int left = (int)(Math.Max(0, crop.Left) * w), top = (int)(Math.Max(0, crop.Top) * h);
        int right = (int)(Math.Max(0, crop.Right) * w), bottom = (int)(Math.Max(0, crop.Bottom) * h);
        if (left + right >= w - 1 || top + bottom >= h - 1) return source;
        var cropped = new CroppedBitmap(source, new Int32Rect(left, top, w - left - right, h - top - bottom));
        cropped.Freeze();
        return cropped;
    }

    // ------------------------------------------------------------------ geometry

    static Rect Dip(Rect pt) => new(Units.ToDip(pt.X), Units.ToDip(pt.Y), Units.ToDip(pt.Width), Units.ToDip(pt.Height));

    internal static Geometry? Outline(SlideShape shape, Rect r)
    {
        double x = r.X, y = r.Y, w = r.Width, h = r.Height, m = Math.Min(w, h);
        Geometry Poly(params (double x, double y)[] pts)
        {
            var figure = new PathFigure { StartPoint = new Point(x + pts[0].x * w, y + pts[0].y * h), IsClosed = true };
            figure.Segments.Add(new PolyLineSegment(pts.Skip(1).Select(p => new Point(x + p.x * w, y + p.y * h)), true));
            return new PathGeometry([figure]);
        }
        // Adjustments are fractions of the shorter side; convert to fractions of width/height.
        double Fw(double fallback) => (shape.Adjust ?? fallback) * m / Math.Max(w, 0.01);
        double Fh(double fallback) => (shape.Adjust ?? fallback) * m / Math.Max(h, 0.01);

        switch (shape.Geometry)
        {
            case ShapeGeometry.Rect:
                return new RectangleGeometry(r);
            case ShapeGeometry.RoundRect:
                double radius = Math.Min(shape.Adjust ?? 0.16667, 0.5) * m;
                return new RectangleGeometry(r, radius, radius);
            case ShapeGeometry.Ellipse:
                return new EllipseGeometry(r);
            case ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve:
                return Connector(shape, r);
            case ShapeGeometry.Line:
                return w < 0.01 && h < 0.01 ? null : new LineGeometry(r.TopLeft, r.BottomRight);
            case ShapeGeometry.Triangle:
                double apex = shape.Adjust ?? 0.5;
                return Poly((apex, 0), (1, 1), (0, 1));
            case ShapeGeometry.RightTriangle:
                return Poly((0, 0), (1, 1), (0, 1));
            case ShapeGeometry.Diamond:
                return Poly((0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5));
            case ShapeGeometry.Parallelogram:
                double pa = Fw(0.25);
                return Poly((pa, 0), (1, 0), (1 - pa, 1), (0, 1));
            case ShapeGeometry.Trapezoid:
                double ta = Fw(0.25);
                return Poly((ta, 0), (1 - ta, 0), (1, 1), (0, 1));
            case ShapeGeometry.HomePlate:
                double ha = Fw(0.5);
                return Poly((0, 0), (1 - ha, 0), (1, 0.5), (1 - ha, 1), (0, 1));
            case ShapeGeometry.Chevron:
                double ca = Fw(0.5);
                return Poly((0, 0), (1 - ca, 0), (1, 0.5), (1 - ca, 1), (0, 1), (ca, 0.5));
            case ShapeGeometry.Hexagon:
                double xa = Fw(0.25);
                return Poly((xa, 0), (1 - xa, 0), (1, 0.5), (1 - xa, 1), (xa, 1), (0, 0.5));
            case ShapeGeometry.Octagon:
                double ow = Fw(0.29289), oh = Fh(0.29289);
                return Poly((ow, 0), (1 - ow, 0), (1, oh), (1, 1 - oh), (1 - ow, 1), (ow, 1), (0, 1 - oh), (0, oh));
            case ShapeGeometry.Pentagon:
                return Poly(Fit(Enumerable.Range(0, 5).Select(i => Polar(i * 72 - 90, 0.5))));
            case ShapeGeometry.Star5:
                return Poly(Fit(Enumerable.Range(0, 10).Select(i => Polar(i * 36 - 90, i % 2 == 0 ? 0.5 : 0.19))));
            case ShapeGeometry.Plus:
                double pw = Fw(0.25), ph = Fh(0.25);
                return Poly((pw, 0), (1 - pw, 0), (1 - pw, ph), (1, ph), (1, 1 - ph), (1 - pw, 1 - ph), (1 - pw, 1),
                    (pw, 1), (pw, 1 - ph), (0, 1 - ph), (0, ph), (pw, ph));
            case ShapeGeometry.RightArrow or ShapeGeometry.LeftArrow or ShapeGeometry.UpArrow or ShapeGeometry.DownArrow:
                bool horizontal = shape.Geometry is ShapeGeometry.RightArrow or ShapeGeometry.LeftArrow;
                double shaft = Math.Clamp(shape.Adjust ?? 0.5, 0.05, 1) / 2;
                double head = Math.Min(0.5 * m / Math.Max(horizontal ? w : h, 0.01), 0.9);
                (double, double)[] right = [(0, 0.5 - shaft), (1 - head, 0.5 - shaft), (1 - head, 0), (1, 0.5), (1 - head, 1), (1 - head, 0.5 + shaft), (0, 0.5 + shaft)];
                return Poly(right.Select(p => shape.Geometry switch
                {
                    ShapeGeometry.LeftArrow => (1 - p.Item1, p.Item2),
                    ShapeGeometry.DownArrow => (p.Item2, p.Item1),
                    ShapeGeometry.UpArrow => (p.Item2, 1 - p.Item1),
                    _ => p,
                }).ToArray());
            case ShapeGeometry.Custom when shape.Path != null:
                var path = shape.Path.Clone();
                var t = new TransformGroup();
                t.Children.Add(new ScaleTransform(Units.DipPerPoint, Units.DipPerPoint));
                t.Children.Add(new TranslateTransform(x, y));
                path.Transform = t;
                return path;
            default:
                return new RectangleGeometry(r);
        }

        static (double, double) Polar(double degrees, double radius)
        {
            double a = degrees * Math.PI / 180;
            return (0.5 + radius * Math.Cos(a), 0.5 + radius * Math.Sin(a));
        }

        // Regular polygons fill their box top to bottom, as PowerPoint draws them.
        static (double, double)[] Fit(IEnumerable<(double x, double y)> points)
        {
            var list = points.ToList();
            double top = list.Min(p => p.y), span = Math.Max(0.01, list.Max(p => p.y) - top);
            return list.Select(p => (p.x, (p.y - top) / span)).ToArray();
        }
    }

    static void ArrowHeads(DrawingContext dc, SlideShape shape, Rect r, Color color, double thickness)
    {
        void Head(Point tip, Point from)
        {
            var dir = tip - from;
            if (dir.Length < 0.01) return;
            dir.Normalize();
            double size = Math.Max(6, thickness * 3);
            var back = tip - dir * size;
            var side = new Vector(-dir.Y, dir.X) * size / 2;
            var figure = new PathFigure { StartPoint = tip, IsClosed = true };
            figure.Segments.Add(new PolyLineSegment([back + side, back - side], true));
            dc.DrawGeometry(new SolidColorBrush(color), null, new PathGeometry([figure]));
        }
        // Each head points along the connector's end segment.
        double bend = r.X + r.Width * (shape.Adjust ?? 0.5);
        var (startFrom, endFrom) = shape.Geometry switch
        {
            ShapeGeometry.Elbow or ShapeGeometry.Curve => (new Point(bend, r.Y), new Point(bend, r.Bottom)),
            ShapeGeometry.Corner => (new Point(r.Right, r.Y), new Point(r.Right, r.Y)),
            _ => (r.BottomRight, r.TopLeft),
        };
        if (shape.HeadEnd == ArrowHead.Triangle) Head(r.TopLeft, startFrom);
        if (shape.TailEnd == ArrowHead.Triangle) Head(r.BottomRight, endFrom);
    }

    static bool IsConnector(SlideShape s) => s.Geometry is ShapeGeometry.Line or ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve;

    static Geometry Connector(SlideShape shape, Rect r)
    {
        double bend = r.X + r.Width * (shape.Adjust ?? 0.5);
        var figure = new PathFigure { StartPoint = r.TopLeft, IsFilled = false };
        switch (shape.Geometry)
        {
            case ShapeGeometry.Elbow:
                figure.Segments.Add(new PolyLineSegment([new Point(bend, r.Y), new Point(bend, r.Bottom), r.BottomRight], true));
                break;
            case ShapeGeometry.Corner:
                figure.Segments.Add(new PolyLineSegment([new Point(r.Right, r.Y), r.BottomRight], true));
                break;
            default:
                figure.Segments.Add(new BezierSegment(new Point(bend, r.Y), new Point(bend, r.Bottom), r.BottomRight, true));
                break;
        }
        return new PathGeometry([figure]);
    }
}
