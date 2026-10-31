using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Paginates a FlowDocument with WPF's own layout engine and replays every page's drawing
/// (text, lines, fills and images) into a PDF with PDFsharp. Text stays real, selectable text
/// with the actual Windows fonts embedded.
/// </summary>
internal static class PdfRenderer
{
    static PdfRenderer()
    {
        GlobalFontSettings.FontResolver = WindowsFontResolver.Instance;
    }

    /// <summary>
    /// Renders a document: WPF lays out the body (footnotes and wrapped floats included); then each page
    /// gets its header/footer (with live page numbers) and any behind/in-front pictures painted around it.
    /// Sections with their own page size or columns are laid out separately and numbered on.
    /// </summary>
    public static void Render(DDocument doc, string output)
    {
        var pdf = new PdfDocument();
        if (!string.IsNullOrEmpty(doc.Title)) pdf.Info.Title = doc.Title;
        pdf.Info.Creator = "Convert Wheel";

        var parts = Sections(doc);
        var earlierEndnotes = new List<DNote>();
        var laidOut = new List<(DDocument part, FlowBridge.FlowContext ctx, DynamicDocumentPaginator paginator)>();
        for (int k = 0; k < parts.Count; k++)
        {
            bool last = k == parts.Count - 1;
            var (ctx, paginator) = LayOut(parts[k], hold: !last, earlierEndnotes);
            if (!last) earlierEndnotes.AddRange(ctx.Endnotes);
            laidOut.Add((parts[k], ctx, paginator));
        }
        int total = laidOut.Sum(l => l.paginator.PageCount);

        var images = new Dictionary<ImageSource, XImage>();
        int number = 0;
        foreach (var (part, ctx, paginator) in laidOut)
        {
            // Which page each overlay belongs to: the page holding its anchor.
            var overlays = ctx.Overlays.Select(o => (o.Float, page: PageOf(paginator, o.Anchor))).ToList();
            for (int i = 0; i < paginator.PageCount; i++)
            {
                number++;
                using var page = paginator.GetPage(i);
                var pdfPage = pdf.AddPage();
                pdfPage.Width = XUnit.FromPoint(part.PageWidth);
                pdfPage.Height = XUnit.FromPoint(part.PageHeight);
                using var gfx = XGraphics.FromPdfPage(pdfPage);
                gfx.ScaleTransform(72.0 / 96.0); // draw in WPF device-independent pixels
                var writer = new PageWriter(gfx, images);
                var here = overlays.Where(o => o.page == i).Select(o => o.Float).ToList();

                foreach (var f in here.Where(f => f.Wrap == FloatWrap.Behind)) DrawFloat(writer, part, ctx, f, Units.ToDip(part.Margins.Top));
                writer.Visual(page.Visual, Matrix.Identity);
                foreach (var f in here.Where(f => f.Wrap != FloatWrap.Behind)) DrawFloat(writer, part, ctx, f, Units.ToDip(part.Margins.Top));

                var (header, footer) = part.HeaderFooterFor(number, firstOfSection: i == 0);
                DrawHeaderFooter(writer, part, header, number, total, isHeader: true);
                DrawHeaderFooter(writer, part, footer, number, total, isHeader: false);
            }
        }
        if (pdf.PageCount == 0) pdf.AddPage();
        pdf.Save(output);
    }

    /// <summary>Lays out one section; a first pass finds which page each footnote lands on.</summary>
    static (FlowBridge.FlowContext ctx, DynamicDocumentPaginator paginator) LayOut(DDocument part, bool hold, List<DNote> earlierEndnotes)
    {
        var ctx = new FlowBridge.FlowContext(part) { HoldEndnotes = hold, EarlierEndnotes = [.. earlierEndnotes] };
        var paginator = Paginate(FlowBridge.ToFlow(ctx));
        // Footnotes: a first layout shows which page each reference lands on; the final layout then gives
        // each page one block holding its notes in order.
        var footnotes = ctx.NoteAnchors.Where(kv => !kv.Key.Endnote).ToList();
        if (footnotes.Count > 1)
        {
            var pagesOfNotes = footnotes.ToDictionary(kv => kv.Key, kv => PageOf(paginator, kv.Value));
            ctx = new FlowBridge.FlowContext(part) { NotePages = pagesOfNotes, HoldEndnotes = hold, EarlierEndnotes = [.. earlierEndnotes] };
            paginator = Paginate(FlowBridge.ToFlow(ctx));
        }
        return (ctx, paginator);
    }

    /// <summary>The document split where a section with different page settings begins.</summary>
    static List<DDocument> Sections(DDocument doc)
    {
        if (!doc.Blocks.Skip(1).Any(b => b.Section != null)) return [doc];
        var parts = new List<DDocument>();
        DDocument? current = null;
        foreach (var block in doc.Blocks)
        {
            if (current == null || block.Section != null && current.Blocks.Count > 0)
            {
                var setup = current == null ? null : block.Section;
                current = new DDocument
                {
                    Font = doc.Font, FontSize = doc.FontSize, Title = doc.Title,
                    PageWidth = setup?.Width ?? doc.PageWidth, PageHeight = setup?.Height ?? doc.PageHeight,
                    Margins = setup?.Margins ?? doc.Margins, Columns = setup?.Columns ?? doc.Columns, ColumnGap = setup?.ColumnGap ?? doc.ColumnGap,
                    Header = doc.Header, Footer = doc.Footer, FirstHeader = doc.FirstHeader, FirstFooter = doc.FirstFooter,
                    EvenHeader = doc.EvenHeader, EvenFooter = doc.EvenFooter, DifferentFirstPage = doc.DifferentFirstPage,
                    HeaderDistance = doc.HeaderDistance, FooterDistance = doc.FooterDistance,
                };
                parts.Add(current);
            }
            current.Blocks.Add(block);
        }
        return parts;
    }

    /// <summary>An embeddable Windows font for PDFsharp drawing outside the document renderer.</summary>
    internal static XFont SystemFont(string family, double size)
    {
        if (new Typeface(family).TryGetGlyphTypeface(out var glyphs)) return WindowsFontResolver.Instance.FontFor(glyphs, size);
        return WindowsFontResolver.Instance.Fallback(size);
    }

    /// <summary>Writes ready-made WPF visuals (e.g. slides), one per page, as vector PDF pages.</summary>
    /// <param name="page">Builds page <c>i</c>; the returned action releases it once drawn.</param>
    public static void RenderVisuals(string output, double widthPt, double heightPt, int count, Func<int, (Visual visual, Action release)> page, string? title)
    {
        var pdf = new PdfDocument();
        if (!string.IsNullOrEmpty(title)) pdf.Info.Title = title;
        pdf.Info.Creator = "Convert Wheel";
        var images = new Dictionary<ImageSource, XImage>();
        for (int i = 0; i < count; i++)
        {
            var pdfPage = pdf.AddPage();
            pdfPage.Width = XUnit.FromPoint(widthPt);
            pdfPage.Height = XUnit.FromPoint(heightPt);
            var (visual, release) = page(i);
            try
            {
                using var gfx = XGraphics.FromPdfPage(pdfPage);
                gfx.ScaleTransform(72.0 / 96.0);
                new PageWriter(gfx, images).Visual(visual, Matrix.Identity);
            }
            finally { release(); }
        }
        if (pdf.PageCount == 0) pdf.AddPage();
        pdf.Save(output);
    }

    /// <summary>
    /// The area actually painted (text, pictures, visible lines and fills). WPF's own bounds include the
    /// invisible page rectangle, which is useless for measuring a header or footer.
    /// </summary>
    static Rect InkBounds(Visual visual, Matrix parent)
    {
        var local = VisualTreeHelper.GetTransform(visual)?.Value ?? Matrix.Identity;
        var offset = VisualTreeHelper.GetOffset(visual);
        local.Translate(offset.X, offset.Y);
        var matrix = local * parent;
        var bounds = Rect.Empty;
        if (VisualTreeHelper.GetDrawing(visual) is { } drawing) bounds.Union(InkBounds(drawing, matrix));
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child) bounds.Union(InkBounds(child, matrix));
        return bounds;
    }

    static Rect InkBounds(Drawing drawing, Matrix matrix)
    {
        static bool Visible(Brush? b) => b is SolidColorBrush { Color.A: > 0 } || b is GradientBrush or ImageBrush;
        var bounds = Rect.Empty;
        switch (drawing)
        {
            case DrawingGroup group:
                var m = (group.Transform?.Value ?? Matrix.Identity) * matrix;
                foreach (var child in group.Children) bounds.Union(InkBounds(child, m));
                return bounds;
            case GlyphRunDrawing or ImageDrawing:
                bounds = drawing.Bounds;
                break;
            case GeometryDrawing g when Visible(g.Brush) || (g.Pen != null && Visible(g.Pen.Brush)):
                bounds = drawing.Bounds;
                break;
            default:
                return Rect.Empty;
        }
        if (!bounds.IsEmpty) bounds.Transform(matrix);
        return bounds;
    }

    static DynamicDocumentPaginator Paginate(FlowDocument flow)
    {
        var paginator = (DynamicDocumentPaginator)((IDocumentPaginatorSource)flow).DocumentPaginator;
        paginator.PageSize = new Size(flow.PageWidth, flow.PageHeight);
        paginator.ComputePageCount();
        return paginator;
    }

    static int PageOf(DynamicDocumentPaginator paginator, TextElement element)
    {
        try { return paginator.GetPageNumber(paginator.GetObjectPosition(element)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return 0; }
    }

    static void DrawHeaderFooter(PageWriter writer, DDocument doc, DHeaderFooter? part, int page, int pages, bool isHeader)
    {
        if (part == null || part.IsEmpty) return;
        var ctx = new FlowBridge.FlowContext(doc) { Page = page, Pages = pages, AllFloatsAsOverlays = true };
        var (visual, height) = Layout(FlowBridge.PartFlow(ctx, part.Blocks, doc.ContentWidth));
        using (visual)
        {
            double top = isHeader ? Units.ToDip(doc.HeaderDistance) : Units.ToDip(doc.PageHeight - doc.FooterDistance) - height;
            foreach (var f in ctx.Overlays.Select(o => o.Float).Where(f => f.Wrap == FloatWrap.Behind)) DrawFloat(writer, doc, ctx, f, top);
            var m = Matrix.Identity;
            m.Translate(Units.ToDip(doc.Margins.Left), top);
            writer.Visual(visual.Visual, m);
            foreach (var f in ctx.Overlays.Select(o => o.Float).Where(f => f.Wrap != FloatWrap.Behind)) DrawFloat(writer, doc, ctx, f, top);
        }
    }

    /// <summary>Lays out a small flow (header, text box) on one page and measures how tall its content is.</summary>
    internal static (DocumentPage page, double height) Layout(FlowDocument flow)
    {
        var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
        paginator.PageSize = new Size(flow.PageWidth, flow.PageHeight);
        var page = paginator.GetPage(0);
        var bounds = InkBounds(page.Visual, Matrix.Identity);
        return (page, bounds.IsEmpty ? 0 : bounds.Bottom + flow.PagePadding.Bottom);
    }

    /// <summary>Paints a positioned picture or text box at its place on the page (sizes in points, drawing in DIPs).</summary>
    static void DrawFloat(PageWriter writer, DDocument doc, FlowBridge.FlowContext ctx, DFloat f, double paragraphTop)
    {
        double w = Units.ToDip(f.Width), h = Units.ToDip(f.Height);
        double pageW = Units.ToDip(doc.PageWidth), pageH = Units.ToDip(doc.PageHeight);
        var m = doc.Margins;
        var (left, areaW) = f.HorizontalFrom == FloatFrom.Page ? (0.0, pageW) : (Units.ToDip(m.Left), Units.ToDip(doc.ContentWidth));
        var (top, areaH) = f.VerticalFrom switch
        {
            FloatFrom.Page => (0.0, pageH),
            FloatFrom.Margin => (Units.ToDip(m.Top), pageH - Units.ToDip(m.Top + m.Bottom)),
            _ => (paragraphTop, pageH - paragraphTop),
        };
        double x = f.HorizontalAlign switch
        {
            FloatAlign.Start => left,
            FloatAlign.Center => left + (areaW - w) / 2,
            FloatAlign.End => left + areaW - w,
            _ => left + Units.ToDip(f.X),
        };
        double y = f.VerticalAlign switch
        {
            FloatAlign.Start => top,
            FloatAlign.Center => top + (areaH - h) / 2,
            FloatAlign.End => top + areaH - h,
            _ => top + Units.ToDip(f.Y),
        };

        var rect = new Rect(x, y, w, h);
        writer.Box(rect, f.Fill, f.Border);
        if (f.Image != null && FlowBridge.Decode(f.Image.Data) is { } source) writer.Picture(source, rect);
        if (f.Blocks.Count > 0)
        {
            var inner = new FlowBridge.FlowContext(doc) { Page = ctx.Page, Pages = ctx.Pages };
            var (visual, _) = Layout(FlowBridge.PartFlow(inner, f.Blocks, f.Width, new Thickness(7.2, 3.6, 7.2, 3.6)));
            using (visual)
            {
                var t = Matrix.Identity;
                t.Translate(x, y);
                writer.Visual(visual.Visual, t);
            }
        }
    }

    sealed class PageWriter(XGraphics gfx, Dictionary<ImageSource, XImage> images)
    {
        public void Box(Rect rect, Color? fill, Color? border)
        {
            if (fill == null && border == null) return;
            var brush = fill is { } f ? new XSolidBrush(XColor.FromArgb(f.A, f.R, f.G, f.B)) : null;
            var pen = border is { } b ? new XPen(XColor.FromArgb(b.A, b.R, b.G, b.B), 1) : null;
            if (pen != null && brush != null) gfx.DrawRectangle(pen, brush, rect.X, rect.Y, rect.Width, rect.Height);
            else if (brush != null) gfx.DrawRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
            else gfx.DrawRectangle(pen!, rect.X, rect.Y, rect.Width, rect.Height);
        }

        public void Picture(BitmapSource source, Rect rect)
        {
            if (!images.TryGetValue(source, out var image))
            {
                image = XImage.FromStream(Encode(source));
                images[source] = image;
            }
            gfx.DrawImage(image, rect.X, rect.Y, rect.Width, rect.Height);
        }

        public void Visual(Visual visual, Matrix parent)
        {
            var local = VisualTreeHelper.GetTransform(visual)?.Value ?? Matrix.Identity;
            var offset = VisualTreeHelper.GetOffset(visual);
            local.Translate(offset.X, offset.Y);
            var matrix = local * parent;

            if (visual is UIElement { Visibility: not Visibility.Visible }) return;
            if (VisualTreeHelper.GetDrawing(visual) is { } drawing) Drawing(drawing, matrix);
            int count = VisualTreeHelper.GetChildrenCount(visual);
            for (int i = 0; i < count; i++)
                if (VisualTreeHelper.GetChild(visual, i) is Visual child) Visual(child, matrix);
        }

        void Drawing(Drawing drawing, Matrix matrix)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    var m = (group.Transform?.Value ?? Matrix.Identity) * matrix;
                    foreach (var child in group.Children) Drawing(child, m);
                    break;
                case GlyphRunDrawing text:
                    GlyphRun(text.GlyphRun, text.ForegroundBrush, matrix);
                    break;
                case GeometryDrawing geometry:
                    Geometry(geometry, matrix);
                    break;
                case ImageDrawing image:
                    Image(image, matrix);
                    break;
            }
        }

        void WithTransform(Matrix m, Action draw)
        {
            var state = gfx.Save();
            gfx.MultiplyTransform(new XMatrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY));
            try { draw(); }
            finally { gfx.Restore(state); }
        }

        void GlyphRun(GlyphRun run, Brush brush, Matrix matrix)
        {
            var chars = run.Characters;
            if (chars == null || chars.Count == 0 || ColorOf(brush) is not { } color) return;
            var advances = run.AdvanceWidths;
            var map = run.ClusterMap;
            var x = new double[advances.Count + 1];
            for (int g = 0; g < advances.Count; g++) x[g + 1] = x[g] + advances[g];

            var xbrush = new XSolidBrush(color);
            var font = WindowsFontResolver.Instance.FontFor(run.GlyphTypeface, run.FontRenderingEmSize);
            var origin = run.BaselineOrigin;

            WithTransform(matrix, () =>
            {
                // Draw word by word at WPF's positions, so justified and kerned spacing is preserved.
                int i = 0;
                while (i < chars.Count)
                {
                    if (char.IsWhiteSpace(chars[i]) || chars[i] == '​') { i++; continue; }
                    int start = i;
                    while (i < chars.Count && !char.IsWhiteSpace(chars[i]) && chars[i] != '​') i++;
                    int glyph = map != null && start < map.Count ? map[start] : start;
                    var word = new string(chars.Skip(start).Take(i - start).ToArray());
                    var at = new XPoint(origin.X + x[Math.Min(glyph, x.Length - 1)], origin.Y);
                    try
                    {
                        gfx.DrawString(word, font, xbrush, at, XStringFormats.BaseLineLeft);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Log.Write($"PDF text fallback for {run.GlyphTypeface.FontUri}: {ex.Message}");
                        font = WindowsFontResolver.Instance.Fallback(run.FontRenderingEmSize);
                        gfx.DrawString(word, font, xbrush, at, XStringFormats.BaseLineLeft);
                    }
                }
            });
        }

        void Geometry(GeometryDrawing drawing, Matrix matrix)
        {
            var fill = ColorOf(drawing.Brush);
            var stroke = drawing.Pen != null ? ColorOf(drawing.Pen.Brush) : null;
            if ((fill == null && stroke == null) || drawing.Geometry == null || drawing.Geometry.IsEmpty()) return;

            var flat = drawing.Geometry.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
            var path = new XGraphicsPath { FillMode = flat.FillRule == FillRule.Nonzero ? XFillMode.Winding : XFillMode.Alternate };
            foreach (var figure in flat.Figures)
            {
                var points = new List<XPoint> { new(figure.StartPoint.X, figure.StartPoint.Y) };
                foreach (var segment in figure.Segments)
                {
                    if (segment is PolyLineSegment poly) points.AddRange(poly.Points.Select(p => new XPoint(p.X, p.Y)));
                    else if (segment is LineSegment line) points.Add(new XPoint(line.Point.X, line.Point.Y));
                }
                if (points.Count < 2) continue;
                path.StartFigure();
                path.AddLines(points.ToArray());
                if (figure.IsClosed) path.CloseFigure();
            }

            var pen = stroke is { } s ? new XPen(s, Math.Max(0.1, drawing.Pen!.Thickness)) : null;
            if (pen != null && drawing.Pen!.DashStyle is { Dashes.Count: > 0 } dash && dash != DashStyles.Solid) pen.DashStyle = XDashStyle.Dash;
            XBrush? brush = fill is { } f ? new XSolidBrush(f) : null;
            // Linear gradients (slide backgrounds, shapes) as real PDF gradients between their end colours.
            if (drawing.Brush is LinearGradientBrush { GradientStops.Count: >= 2 } lg && ColorOf(lg) != null)
            {
                var bounds = flat.Bounds;
                Point At(Point p) => lg.MappingMode == BrushMappingMode.RelativeToBoundingBox
                    ? new Point(bounds.X + p.X * bounds.Width, bounds.Y + p.Y * bounds.Height) : p;
                var stops = lg.GradientStops.OrderBy(g => g.Offset).ToList();
                XColor C(Color c) => XColor.FromArgb((int)(c.A * lg.Opacity), c.R, c.G, c.B);
                var (a, b) = (At(lg.StartPoint), At(lg.EndPoint));
                if ((a - b).Length > 0.01)
                    brush = new XLinearGradientBrush(new XPoint(a.X, a.Y), new XPoint(b.X, b.Y), C(stops[0].Color), C(stops[^1].Color));
            }
            WithTransform(matrix, () =>
            {
                if (pen != null && brush != null) gfx.DrawPath(pen, brush, path);
                else if (brush != null) gfx.DrawPath(brush, path);
                else gfx.DrawPath(pen!, path);
            });
        }

        void Image(ImageDrawing drawing, Matrix matrix)
        {
            if (drawing.ImageSource is not BitmapSource source) return;
            if (!images.TryGetValue(source, out var image))
            {
                image = XImage.FromStream(Encode(source));
                images[source] = image;
            }
            var rect = drawing.Rect;
            WithTransform(matrix, () => gfx.DrawImage(image, rect.X, rect.Y, rect.Width, rect.Height));
        }

        /// <summary>Photos go in as JPEG, anything with transparency as PNG.</summary>
        static MemoryStream Encode(BitmapSource source)
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            bool alpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) { alpha = true; break; }

            BitmapEncoder encoder = alpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(alpha ? bgra : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
            var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return ms;
        }

        static XColor? ColorOf(Brush? brush)
        {
            var c = brush switch
            {
                SolidColorBrush solid => solid.Color,
                GradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
                _ => (Color?)null,
            };
            if (c is not { } color || color.A == 0) return null;
            double opacity = brush!.Opacity;
            return XColor.FromArgb((int)(color.A * opacity), color.R, color.G, color.B);
        }
    }

    /// <summary>
    /// Hands PDFsharp the exact font file WPF used for each glyph run (including fonts inside .ttc
    /// collections and fallback fonts for other scripts).
    /// </summary>
    sealed class WindowsFontResolver : IFontResolver
    {
        public static readonly WindowsFontResolver Instance = new();
        static readonly PropertyInfo? FaceIndexProperty = typeof(GlyphTypeface).GetProperty("FaceIndex", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly ConcurrentDictionary<string, (Uri uri, int index, StyleSimulations sims)> _faces = new();
        readonly ConcurrentDictionary<Uri, byte[]> _files = new();
        readonly ConcurrentDictionary<(string key, double size), XFont> _fonts = new();
        readonly ConcurrentDictionary<string, bool> _broken = new();

        public XFont FontFor(GlyphTypeface typeface, double size)
        {
            int index = FaceIndexProperty?.GetValue(typeface) is int i ? i : 0;
            var key = $"cw:{typeface.FontUri.AbsoluteUri}#{index}#{(int)typeface.StyleSimulations}";
            if (_broken.ContainsKey(key)) return Fallback(size);
            _faces.TryAdd(key, (typeface.FontUri, index, typeface.StyleSimulations));
            try
            {
                return _fonts.GetOrAdd((key, size), k => new XFont(k.key, k.size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // e.g. CFF-flavoured OpenType fonts, which PDFsharp can't embed.
                Log.Write($"PDF font {typeface.FontUri} unusable: {ex.Message}");
                _broken[key] = true;
                return Fallback(size);
            }
        }

        public XFont Fallback(double size)
        {
            var typeface = new Typeface("Segoe UI");
            if (!typeface.TryGetGlyphTypeface(out var glyphs)) new Typeface("Arial").TryGetGlyphTypeface(out glyphs);
            int index = 0;
            var key = $"cw:{glyphs.FontUri.AbsoluteUri}#0#0";
            _faces.TryAdd(key, (glyphs.FontUri, index, StyleSimulations.None));
            return _fonts.GetOrAdd((key, size), k => new XFont(k.key, k.size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)));
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (!_faces.TryGetValue(familyName, out var face)) return null;
            return new FontResolverInfo(familyName,
                face.sims.HasFlag(StyleSimulations.BoldSimulation),
                face.sims.HasFlag(StyleSimulations.ItalicSimulation));
        }

        public byte[]? GetFont(string faceName)
        {
            if (!_faces.TryGetValue(faceName, out var face)) return null;
            var file = _files.GetOrAdd(face.uri, uri =>
            {
                using var stream = new GlyphTypeface(uri).GetFontStream();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            });
            return IsCollection(file) ? ExtractFace(file, face.index) : file;
        }

        static bool IsCollection(byte[] data) => data.Length > 12 && data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f';

        /// <summary>Copies one font out of a .ttc collection into a standalone .ttf.</summary>
        static byte[] ExtractFace(byte[] ttc, int index)
        {
            static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
            static int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];
            static void Put32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

            int count = (int)U32(ttc, 8);
            int header = (int)U32(ttc, 12 + 4 * Math.Clamp(index, 0, count - 1));
            int tables = U16(ttc, header + 4);
            int directorySize = 12 + 16 * tables;

            var records = new List<(int record, int offset, int length)>();
            int total = directorySize;
            for (int t = 0; t < tables; t++)
            {
                int rec = header + 12 + 16 * t;
                int length = (int)U32(ttc, rec + 12);
                records.Add((rec, (int)U32(ttc, rec + 8), length));
                total += (length + 3) & ~3;
            }

            var ttf = new byte[total];
            Array.Copy(ttc, header, ttf, 0, directorySize);
            int position = directorySize;
            for (int t = 0; t < tables; t++)
            {
                var (_, offset, length) = records[t];
                Array.Copy(ttc, offset, ttf, position, length);
                Put32(ttf, 12 + 16 * t + 8, (uint)position);
                position += (length + 3) & ~3;
            }
            return ttf;
        }
    }
}
