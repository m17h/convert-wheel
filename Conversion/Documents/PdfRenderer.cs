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

    public static void Render(DDocument doc, string output) => Render(FlowBridge.ToFlow(doc), output, doc.Title);

    public static void Render(FlowDocument flow, string output, string? title)
    {
        var pdf = new PdfDocument();
        if (!string.IsNullOrEmpty(title)) pdf.Info.Title = title;
        pdf.Info.Creator = "Convert Wheel";

        var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
        paginator.PageSize = new Size(flow.PageWidth, flow.PageHeight);
        paginator.ComputePageCount();

        var images = new Dictionary<ImageSource, XImage>();
        for (int i = 0; i < paginator.PageCount; i++)
        {
            using var page = paginator.GetPage(i);
            var pdfPage = pdf.AddPage();
            pdfPage.Width = XUnit.FromPoint(Units.ToPoints(flow.PageWidth));
            pdfPage.Height = XUnit.FromPoint(Units.ToPoints(flow.PageHeight));
            using var gfx = XGraphics.FromPdfPage(pdfPage);
            gfx.ScaleTransform(72.0 / 96.0); // draw in WPF device-independent pixels
            new PageWriter(gfx, images).Visual(page.Visual, Matrix.Identity);
        }
        if (pdf.PageCount == 0) pdf.AddPage();
        pdf.Save(output);
    }

    sealed class PageWriter(XGraphics gfx, Dictionary<ImageSource, XImage> images)
    {
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
            var brush = fill is { } f ? new XSolidBrush(f) : null;
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
