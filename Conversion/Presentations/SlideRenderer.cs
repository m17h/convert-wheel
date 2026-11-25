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
    const double PdfScale = 300.0 / 96; // pictures in PDFs: print resolution

    readonly SlidePictures _pictures = new();
    /// <summary>Pixels per device-independent unit of what's being drawn.</summary>
    double _scale = 2;
    bool _transparent, _pdf;

    /// <summary>Draws loose shapes (a Word drawing, a group) as one transparent picture of the given size in points.</summary>
    public static DImage ToPicture(IEnumerable<SlideShape> shapes, double widthPt, double heightPt)
    {
        var deck = new Deck { Width = RasterLimits.Dimension(widthPt, 720), Height = RasterLimits.Dimension(heightPt, 405) };
        var slide = new Slide();
        slide.Shapes.AddRange(shapes);
        double w = Units.ToDip(deck.Width), h = Units.ToDip(deck.Height);
        double scale = Math.Clamp(1600 / Math.Max(w, h), 1, 4);
        if (!RasterLimits.TrySize(w, h, ref scale, out int pw, out int ph)) throw new ConversionException("This drawing has an invalid size.");
        var renderer = new SlideRenderer(deck) { _transparent = true, _scale = scale };
        var (visual, release) = renderer.Build(slide);
        try
        {
            var bitmap = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
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
        NormalizeSize();
        _pdf = _pictures.Pdf = true;
        _scale = PdfScale;
        var slides = deck.Shown;
        int count = slides.Count;
        PdfRenderer.RenderVisuals(output, deck.Width, deck.Height, count, i =>
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((double)i / count);
            return Build(slides[i], ct);
        }, deck.Title, ct);
    }

    /// <returns>The picture, or a folder of them when there are several slides.</returns>
    public string ToImages(string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        NormalizeSize();
        var slides = deck.Shown;
        int count = slides.Count;
        _scale = 1920 / Units.ToDip(deck.Width);
        return Outputs.Pages(output, count, (i, path) =>
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((double)i / count);
            var (visual, release) = Build(slides[i], ct);
            try { Save(Rasterize(visual, 1920), path, ext); }
            finally { release(); }
        });
    }

    void NormalizeSize()
    {
        deck.Width = RasterLimits.Dimension(deck.Width, 720);
        deck.Height = RasterLimits.Dimension(deck.Height, 405);
    }

    BitmapSource Rasterize(Visual visual, int pixelWidth)
    {
        double width = Units.ToDip(deck.Width), height = Units.ToDip(deck.Height);
        double scale = pixelWidth / width;
        if (!RasterLimits.TrySize(width, height, ref scale, out int pw, out int ph)) throw new ConversionException("This slide has an invalid size.");
        var bitmap = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
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
    public (Visual visual, Action release) Build(Slide slide, CancellationToken ct = default)
    {
        NormalizeSize();
        var pages = new List<DocumentPage>();
        void Release() { foreach (var p in pages) p.Dispose(); }
        var root = new ContainerVisual();
        var area = new Rect(0, 0, Units.ToDip(deck.Width), Units.ToDip(deck.Height));

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            if (!_transparent) dc.DrawRectangle(Brushes.White, null, area);
            try { Paint(dc, slide.Background, new RectangleGeometry(area), area); }
            catch (Exception ex) when (SlideLimits.CanSkip(ex)) { Log.Write($"Skipped drawing a background: {ex.Message}"); }
        }
        root.Children.Add(background);

        foreach (var shape in slide.Shapes)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                AddShape(root, shape, pages);
            }
            catch (Exception ex) when (SlideLimits.CanSkip(ex))
            {
                Log.Write($"Skipped drawing a shape: {ex.Message}");
            }
            catch
            {
                Release();
                throw;
            }
        }
        return (root, Release);
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
            if (shape.Picture != null)
            {
                // Pictures cut to a shape (circle portraits, rounded corners) keep that outline.
                var fill = new SlideFill { Image = shape.Picture, Crop = shape.Crop, Opacity = shape.PictureOpacity };
                PaintPicture(dc, fill, geometry ?? new RectangleGeometry(r), r, shape.Geometry == ShapeGeometry.Rect || geometry == null ? null : Outlined(shape));
            }
            else if (geometry != null && !IsConnector(shape))
            {
                Paint(dc, shape.Fill, geometry, r, shape.Geometry == ShapeGeometry.Rect ? null : Outlined(shape));
            }
            if (shape.Line is { A: > 0 } line && geometry != null)
            {
                var pen = new Pen(new SolidColorBrush(line), Math.Max(0.5, Units.ToDip(shape.LineWidth)))
                {
                    DashStyle = shape.Dashed ? DashStyles.Dash : DashStyles.Solid,
                    LineJoin = PenLineJoin.Round,
                };
                dc.DrawGeometry(null, pen, shape.Geometry == ShapeGeometry.Custom ? Strokable(geometry) : geometry);
                if (IsConnector(shape)) ArrowHeads(dc, shape, r, line, pen.Thickness);
                else if (shape.Geometry == ShapeGeometry.Custom && (shape.HeadEnd != ArrowHead.None || shape.TailEnd != ArrowHead.None)
                         && Flatten(geometry) is [{ closed: false } open] && open.points.Count > 1)
                {
                    Head(dc, shape.HeadEnd, open.points[0], open.points[1], line, pen.Thickness);
                    Head(dc, shape.TailEnd, open.points[^1], open.points[^2], line, pen.Thickness);
                }
            }
            dc.Pop();
        }
        root.Children.Add(drawing);

        if (shape.Text is [DTable table]) AddTable(root, shape, table, r, center, pages);
        else if (shape.HasText) AddText(root, shape, r, center, pages);
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
            double extra = Math.Max(400, deck.Width);
            var align = shape.Text.OfType<DParagraph>().FirstOrDefault()?.Align ?? TextAlignment.Left;
            shift = align switch { TextAlignment.Center => -extra / 2, TextAlignment.Right => -extra, _ => 0 };
            layoutWidth += extra;
        }
        // A box narrower than its own margins still shows its text.
        if (insets.Left + insets.Right > layoutWidth - 4)
        {
            double over = insets.Left + insets.Right - Math.Max(0, layoutWidth - 4), total = insets.Left + insets.Right;
            insets = new Thickness(Math.Max(0, insets.Left - over * insets.Left / total), insets.Top, Math.Max(0, insets.Right - over * insets.Right / total), insets.Bottom);
            layoutWidth = Math.Max(layoutWidth, 5);
        }

        var doc = new DDocument { Font = deck.Font, FontSize = 18, PageHeight = Math.Max(deck.Height, 200) * 20 };
        var (page, height) = Layout(doc, shape.Text, layoutWidth, insets);
        // Shrink to fit, as PowerPoint does for placeholders whose text overflows.
        for (int attempt = 0; shape.ShrinkToFit && height > boxHeight * 1.05 && attempt < 10; attempt++)
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

    /// <summary>A table on its grid: cells, then their edges, then their text. Rows grow to hold what's in them.</summary>
    void AddTable(ContainerVisual root, SlideShape shape, DTable table, Rect r, Point center, List<DocumentPage> pages)
    {
        var grid = new TableGrid(table);
        if (grid.Rows == 0) return;
        var x = new double[grid.Columns + 1];
        x[0] = r.X;
        var widths = grid.Widths(shape.Bounds.Width);
        for (int c = 0; c < grid.Columns; c++) x[c + 1] = x[c] + Units.ToDip(widths[c]);

        var doc = new DDocument { Font = deck.Font, FontSize = 18, PageHeight = Math.Max(deck.Height, 200) * 20 };
        var laidOut = new List<(TableGrid.Place place, DocumentPage page, double height)>();
        var heights = table.Rows.Select(row => Units.ToDip(row.Height ?? 0)).ToArray();
        // A table without row heights fills its frame.
        if (heights.All(h => h == 0)) Array.Fill(heights, r.Height / grid.Rows);
        foreach (var place in grid.Cells)
        {
            double width = x[place.Column + place.ColumnSpan] - x[place.Column];
            var pad = place.Cell.Look?.Padding ?? table.CellPadding ?? new Thickness(7.2, 3.6, 7.2, 3.6);
            double room = Math.Max(5, Units.ToPoints(width));
            if (pad.Left + pad.Right > room - 4) pad = new Thickness(1, pad.Top, 1, pad.Bottom);
            bool empty = place.Cell.Blocks.All(b => b is DParagraph { Inlines.Count: 0 });
            double height = 0;
            if (empty)
                height = Units.ToDip(pad.Top + pad.Bottom + place.Cell.Blocks.Cast<DParagraph>().Sum(p => (p.MarkSize ?? 18) * 1.22 * p.LineSpacing + p.SpaceBefore + p.SpaceAfter));
            else
            {
                (var page, height) = Layout(doc, place.Cell.Blocks, room, pad);
                pages.Add(page);
                laidOut.Add((place, page, height));
            }
            // An empty cell never makes its row taller than the source says it is.
            if (place.RowSpan == 1 && !(empty && table.Rows[place.Row].Height > 0)) heights[place.Row] = Math.Max(heights[place.Row], height);
        }
        foreach (var (place, _, height) in laidOut.Where(l => l.place.RowSpan > 1))
        {
            double have = heights.Skip(place.Row).Take(place.RowSpan).Sum();
            if (height > have) heights[place.Row + place.RowSpan - 1] += height - have;
        }
        var y = new double[grid.Rows + 1];
        y[0] = r.Y;
        for (int i = 0; i < grid.Rows; i++) y[i + 1] = y[i] + heights[i];
        Rect Area(TableGrid.Place p) => new(x[p.Column], y[p.Row], x[p.Column + p.ColumnSpan] - x[p.Column], y[p.Row + p.RowSpan] - y[p.Row]);

        var turn = new MatrixTransform(new RotateTransform(shape.Rotation, center.X, center.Y).Value);
        var drawing = new DrawingVisual { Transform = turn };
        using (var dc = drawing.RenderOpen())
        {
            foreach (var place in grid.Cells)
                if (place.Cell.Shading is { A: > 0 } shade) dc.DrawRectangle(new SolidColorBrush(shade), null, Area(place));
            var plain = table.Borders ? new CellEdge(0.5, table.BorderColor ?? Color.FromRgb(0x80, 0x80, 0x80)) : null;
            // Where two cells differ about the edge between them, the heavier line shows.
            var edges = new List<(CellEdge edge, Point from, Point to)>();
            foreach (var place in grid.Cells)
            {
                var a = Area(place);
                void Edge(CellEdge? edge, Point from, Point to)
                {
                    if ((edge ?? plain) is { Color.A: > 0 } visible) edges.Add((visible, from, to));
                }
                Edge(place.Cell.Look?.Left, a.TopLeft, a.BottomLeft);
                Edge(place.Cell.Look?.Top, a.TopLeft, a.TopRight);
                Edge(place.Cell.Look?.Right, a.TopRight, a.BottomRight);
                Edge(place.Cell.Look?.Bottom, a.BottomLeft, a.BottomRight);
            }
            foreach (var (edge, from, to) in edges.OrderBy(e => e.edge.Width))
                dc.DrawLine(new Pen(new SolidColorBrush(edge.Color!.Value), Math.Max(0.67, Units.ToDip(edge.Width))) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }, from, to);
        }
        root.Children.Add(drawing);

        foreach (var (place, page, height) in laidOut)
        {
            var a = Area(place);
            double down = place.Cell.Look?.Anchor switch { TextAnchor.Middle => (a.Height - height) / 2, TextAnchor.Bottom => a.Height - height, _ => 0 };
            var m = Matrix.Identity;
            m.Translate(a.X, a.Y + Math.Max(0, down));
            m.Append(turn.Matrix);
            var holder = new ContainerVisual { Transform = new MatrixTransform(m) };
            holder.Children.Add(page.Visual);
            root.Children.Add(holder);
        }
    }

    static (DocumentPage page, double height) Layout(DDocument doc, List<DBlock> blocks, double width, Thickness insets)
    {
        var flow = FlowBridge.PartFlow(new FlowBridge.FlowContext(doc), blocks, width, insets);
        var (page, _) = PdfRenderer.Layout(flow);
        // Lines count at their full height, not just where there's ink, and so do empty ones at the end.
        double bottom = TextBottom(page.Visual, Matrix.Identity);
        if (bottom <= 0) bottom = Units.ToDip(insets.Top);
        foreach (var p in blocks.AsEnumerable().Reverse().OfType<DParagraph>().TakeWhile(p => p.Inlines.Count == 0))
            bottom += Units.ToDip((p.MarkSize ?? 18) * 1.22 * p.LineSpacing + p.SpaceBefore);
        return (page, bottom + Units.ToDip(insets.Bottom));
    }

    static double TextBottom(Visual visual, Matrix parent)
    {
        var local = VisualTreeHelper.GetTransform(visual)?.Value ?? Matrix.Identity;
        var offset = VisualTreeHelper.GetOffset(visual);
        local.Translate(offset.X, offset.Y);
        var matrix = local * parent;
        double bottom = VisualTreeHelper.GetDrawing(visual) is { } drawing ? TextBottom(drawing, matrix) : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child) bottom = Math.Max(bottom, TextBottom(child, matrix));
        return bottom;
    }

    static double TextBottom(Drawing drawing, Matrix matrix)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                var m = (group.Transform?.Value ?? Matrix.Identity) * matrix;
                return group.Children.Select(c => TextBottom(c, m)).DefaultIfEmpty(0).Max();
            case GlyphRunDrawing { GlyphRun: { } run }:
                var face = run.GlyphTypeface;
                return matrix.Transform(new Point(run.BaselineOrigin.X, run.BaselineOrigin.Y + (face.Height - face.Baseline) * run.FontRenderingEmSize)).Y;
            case ImageDrawing image:
                return Rect.Transform(image.Rect, matrix).Bottom;
            default:
                return 0;
        }
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

    /// <param name="outline">What the outline is, for sharing the drawn result; null for a plain rectangle.</param>
    void Paint(DrawingContext dc, SlideFill? fill, Geometry geometry, Rect bounds, string? outline = null)
    {
        if (fill == null) return;
        if (fill.Image != null)
        {
            PaintPicture(dc, fill, geometry, bounds, outline);
            return;
        }
        if (Brush(fill, bounds) is not { } brush) return;
        // PDF's own gradients only run straight between two solid colours; others are drawn as a picture.
        if (_pdf && fill.Gradient is { } stops && bounds is { Width: >= 1, Height: >= 1 }
            && (fill.RadialGradient || stops.Count != 2 || stops[0].Position > 0.001 || stops[^1].Position < 0.999 || stops.Any(s => s.Color.A < 255)))
        {
            var key = $"gradient {string.Join(";", stops)} {fill.GradientAngle} {fill.RadialGradient} {outline} {bounds.Width:0.#}x{bounds.Height:0.#}";
            if (_pictures.Shared(key, () => Baked(brush, geometry, bounds, outline == null ? 1200 : 2400)) is { } drawn)
            {
                dc.DrawImage(drawn, bounds);
                return;
            }
        }
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>
    /// A picture stretched over its area as it is, or (cut to an outline, tiled, see-through) baked into one
    /// transparent bitmap, so pictures and PDF pages show the same thing.
    /// </summary>
    void PaintPicture(DrawingContext dc, SlideFill fill, Geometry geometry, Rect bounds, string? outline)
    {
        var image = fill.Image!;
        if (bounds.Width < 0.5 || bounds.Height < 0.5) return;
        if (fill.Tile is { } tile)
        {
            double w = Math.Max(4, Units.ToDip(image.Width) * tile), h = Math.Max(4, Units.ToDip(image.Height) * tile);
            var tiled = _pictures.Shared($"tile {_pictures.Id(image)} {tile:0.###} {fill.Opacity:0.##} {outline} {bounds.Width:0.#}x{bounds.Height:0.#}", () =>
                _pictures.Get(image, w * _scale) is not { } one ? null : Baked(new ImageBrush(one)
                {
                    Stretch = Stretch.Fill, Opacity = fill.Opacity, TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(bounds.X, bounds.Y, w, h),
                }, geometry, bounds, 2400));
            if (tiled != null) dc.DrawImage(tiled, bounds);
            return;
        }

        var (area, crop) = SlideShape.PictureArea(bounds, fill.Crop);
        if (area.Width < 0.5 || area.Height < 0.5) return;
        double shown = Math.Max(0.01, 1 - crop.Left - crop.Right);
        BitmapSource? Picture() => _pictures.Cropped(image, crop, area.Width / shown * _scale);
        if (outline == null && fill.Opacity >= 0.999)
        {
            if (Picture() is { } plain) dc.DrawImage(plain, area);
            return;
        }
        var key = $"shaped {_pictures.Id(image)} {fill.Crop} {fill.Opacity:0.##} {outline} {bounds.Width:0.#}x{bounds.Height:0.#}";
        var shaped = _pictures.Shared(key, () => Picture() is not { } source ? null
            : Baked(new ImageBrush(source) { Stretch = Stretch.Fill, Opacity = fill.Opacity, ViewportUnits = BrushMappingMode.Absolute, Viewport = area }, geometry, bounds, 2400));
        if (shaped != null) dc.DrawImage(shaped, bounds);
    }

    /// <summary>An outline filled with a brush, as a transparent bitmap covering its bounds.</summary>
    BitmapSource? Baked(Brush brush, Geometry geometry, Rect bounds, double longest)
    {
        double scale = Math.Min(_scale, longest / Math.Max(bounds.Width, bounds.Height));
        if (!RasterLimits.TrySize(bounds.Width, bounds.Height, ref scale, out int pw, out int ph)) return null;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(pw / bounds.Width, ph / bounds.Height));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            dc.DrawGeometry(brush, null, geometry);
        }
        var target = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Names an outline by everything that shapes it (its size aside).</summary>
    string Outlined(SlideShape shape) => $"{shape.Geometry} {shape.Adjust:0.####} {(shape.Path != null ? _pictures.Id(shape.Path) : 0)}";

    static Brush? Brush(SlideFill fill, Rect bounds)
    {
        if (fill.Gradient is { Count: > 0 } stops)
        {
            var collection = new GradientStopCollection(stops.Select(s => new GradientStop(s.Color, s.Position)));
            if (fill.RadialGradient)
            {
                // The last colour is reached in the corners.
                double reach = Math.Sqrt(0.5);
                return new RadialGradientBrush(collection) { RadiusX = reach, RadiusY = reach };
            }
            // The angle is the one seen on the slide; the first and last colours meet the corners it points from and to.
            double a = fill.GradientAngle * Math.PI / 180;
            var along = new Vector(Math.Cos(a), Math.Sin(a));
            double half = (Math.Abs(along.X) * bounds.Width + Math.Abs(along.Y) * bounds.Height) / 2;
            var middle = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            return new LinearGradientBrush(collection, middle - along * half, middle + along * half) { MappingMode = BrushMappingMode.Absolute };
        }
        return fill.Color is { A: > 0 } c ? new SolidColorBrush(c) : null;
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
                if (path.Transform is { Value: { IsIdentity: false } own }) t.Children.Add(new MatrixTransform(own));
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
        // Each head points along the connector's end segment.
        double bend = r.X + r.Width * (shape.Adjust ?? 0.5);
        var (startFrom, endFrom) = shape.Geometry switch
        {
            ShapeGeometry.Elbow or ShapeGeometry.Curve => (new Point(bend, r.Y), new Point(bend, r.Bottom)),
            ShapeGeometry.Corner => (new Point(r.Right, r.Y), new Point(r.Right, r.Y)),
            _ => (r.BottomRight, r.TopLeft),
        };
        Head(dc, shape.HeadEnd, r.TopLeft, startFrom, color, thickness);
        Head(dc, shape.TailEnd, r.BottomRight, endFrom, color, thickness);
    }

    static void Head(DrawingContext dc, ArrowHead kind, Point tip, Point from, Color color, double thickness)
    {
        var dir = tip - from;
        if (kind == ArrowHead.None || dir.Length < 0.01) return;
        dir.Normalize();
        double size = Math.Max(6, thickness * 3);
        var across = new Vector(-dir.Y, dir.X) * size / 2;
        var brush = new SolidColorBrush(color);
        void Filled(params Point[] points)
        {
            var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
            figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
            dc.DrawGeometry(brush, null, new PathGeometry([figure]));
        }
        var back = tip - dir * size;
        switch (kind)
        {
            case ArrowHead.Oval:
                dc.DrawGeometry(brush, null, new EllipseGeometry(tip, size / 2, size / 2));
                break;
            case ArrowHead.Diamond:
                Filled(tip + dir * size / 2, tip + across, tip - dir * size / 2, tip - across);
                break;
            case ArrowHead.Stealth:
                Filled(tip, back + across, tip - dir * size * 0.6, back - across);
                break;
            case ArrowHead.Open:
                var figure = new PathFigure { StartPoint = back + across };
                figure.Segments.Add(new PolyLineSegment([tip, back - across], true));
                dc.DrawGeometry(null, new Pen(brush, thickness) { LineJoin = PenLineJoin.Miter }, new PathGeometry([figure]));
                break;
            default:
                Filled(tip, back + across, back - across);
                break;
        }
    }

    /// <summary>The same outline with every part of it drawn: flattening leaves out parts that aren't filled.</summary>
    static Geometry Strokable(Geometry geometry)
    {
        var path = PathGeometry.CreateFromGeometry(geometry).Clone();
        foreach (var figure in path.Figures) figure.IsFilled = true;
        return path;
    }

    /// <summary>An outline as straight runs of points, in its own units.</summary>
    internal static List<(List<Point> points, bool closed, bool filled)> Flatten(Geometry geometry)
    {
        var runs = new List<(List<Point>, bool, bool)>();
        var path = PathGeometry.CreateFromGeometry(geometry);
        foreach (var original in path.Figures)
        {
            var figure = original.Clone();
            figure.IsFilled = true;
            foreach (var flat in new PathGeometry([figure], FillRule.Nonzero, path.Transform).GetFlattenedPathGeometry(0.25, ToleranceType.Absolute).Figures)
            {
                var points = new List<Point> { flat.StartPoint };
                foreach (var segment in flat.Segments)
                {
                    if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                    else if (segment is LineSegment line) points.Add(line.Point);
                }
                runs.Add((points, original.IsClosed, original.IsFilled));
            }
        }
        return runs;
    }

    static bool IsConnector(SlideShape s) => s.Geometry is ShapeGeometry.Line or ShapeGeometry.Elbow or ShapeGeometry.Corner or ShapeGeometry.Curve;

    static Geometry Connector(SlideShape shape, Rect r)
    {
        double bend = r.X + r.Width * (shape.Adjust ?? 0.5);
        var figure = new PathFigure { StartPoint = r.TopLeft }; // only ever stroked
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
