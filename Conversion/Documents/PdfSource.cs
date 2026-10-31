using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertWheel.Conversion.Documents;

/// <summary>PDF as a source: pages to pictures, or the text (with headings, lists and pictures) reflowed into a document.</summary>
internal static partial class PdfSource
{
    public static string Convert(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        using var pdf = new Pdfium(input);
        if (ext is "png" or "jpg" or "webp" or "tiff")
            return Pages(pdf, output, ext, progress, ct);
        var doc = Reflow(pdf, Path.GetFileNameWithoutExtension(input), progress, ct);
        BuiltInDocuments.Write(doc, ext, output);
        return output;
    }

    // ------------------------------------------------------------------ pages to pictures

    const double Dpi = 150;

    static string Pages(Pdfium pdf, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        int count = pdf.PageCount;
        if (ext == "tiff")
        {
            // TIFF holds every page in one file.
            var tiff = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                tiff.Frames.Add(BitmapFrame.Create(pdf.Render(i, Dpi)));
                progress.Report((double)(i + 1) / count);
            }
            using var file = File.Create(output);
            tiff.Save(file);
            return output;
        }
        return Outputs.Pages(output, count, (i, path) =>
        {
            ct.ThrowIfCancellationRequested();
            var page = pdf.Render(i, Dpi);
            switch (ext)
            {
                case "webp": File.WriteAllBytes(path, WebP.Encode(new FormatConvertedBitmap(page, PixelFormats.Bgra32, null, 0), 90)); break;
                case "jpg": Save(new JpegBitmapEncoder { QualityLevel = 90 }, new FormatConvertedBitmap(page, PixelFormats.Bgr24, null, 0), path); break;
                default: Save(new PngBitmapEncoder(), page, path); break;
            }
            progress.Report((double)(i + 1) / count);
        });
    }

    static void Save(BitmapEncoder encoder, BitmapSource image, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ------------------------------------------------------------------ text reflow

    internal sealed class Line
    {
        public readonly List<(string text, TextStyle style)> Runs = [];
        public double Left = double.MaxValue, Right, Top, Bottom = double.MaxValue, Size;
        public int Page;
        public string Text => string.Concat(Runs.Select(r => r.text));
    }

    internal sealed record PageText(List<Line> Lines, List<(double top, DImage image)> Images, double Width, double Height);

    static DDocument Reflow(Pdfium pdf, string title, IProgress<double?> progress, CancellationToken ct)
    {
        var pages = new List<PageText>();
        bool usedOcr = false;
        for (int i = 0; i < pdf.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (lines, images, width, height) = pdf.ReadPage(i);
            // A page with no text but a picture covering most of it is a scan: read it with OCR.
            double covered = images.Sum(im => im.image.Width * im.image.Height) / Math.Max(1, width * height);
            if (lines.Count == 0 && (images.Count == 0 || covered > 0.4) && Ocr.Available)
            {
                var ocrLines = OcrLines(pdf.Render(i, 300), 300, height, i);
                if (ocrLines.Count > 0)
                {
                    lines = ocrLines;
                    images = images.Where(im => im.image.Width * im.image.Height < width * height * 0.4).ToList();
                    usedOcr = true;
                }
            }
            pages.Add(new PageText(lines, images, width, height));
            progress.Report(0.8 * (i + 1) / pdf.PageCount);
        }
        if (pages.All(p => p.Lines.Count == 0 && p.Images.Count == 0))
            throw new ConversionException(Ocr.Available
                ? "No text was found in this PDF. Try converting it to PNG instead."
                : "This PDF has no text (it may be scanned) and Windows text recognition isn't available. Try converting it to PNG instead.");
        if (usedOcr) Log.Write($"{title}: scanned pages read with Windows OCR.");
        return Reflow(pages, title);
    }

    /// <summary>Recognises text in pictures (photos of pages, screenshots, scans) and lays it out as a document.</summary>
    public static DDocument FromPictures(IReadOnlyList<BitmapSource> pictures, string title, CancellationToken ct)
    {
        var pages = new List<PageText>();
        for (int i = 0; i < pictures.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var picture = pictures[i];
            double dpi = Math.Max(72, Math.Max(picture.DpiX, 96));
            double width = picture.PixelWidth * 72 / dpi, height = picture.PixelHeight * 72 / dpi;
            pages.Add(new PageText(OcrLines(picture, dpi, height, i), [], width, height));
        }
        if (pages.All(p => p.Lines.Count == 0)) throw new ConversionException("No text was found in this picture.");
        var doc = Reflow(pages, title);
        doc.PageWidth = DDocument.DefaultPage.Width;
        doc.PageHeight = DDocument.DefaultPage.Height;
        return doc;
    }

    /// <summary>OCR lines in PDF coordinates (points, y up from the bottom), with font sizes estimated from the word boxes.</summary>
    static List<Line> OcrLines(BitmapSource image, double dpi, double pageHeight, int page)
    {
        double k = 72 / dpi;
        var lines = new List<Line>();
        foreach (var ocr in Ocr.Recognize(image))
        {
            var b = ocr.Bounds;
            if (b.IsEmpty) continue;
            // The tallest word spans ascenders to descenders: about 1.15 em.
            double tallest = ocr.Words.Max(w => w.Bounds.Height) * k;
            var line = new Line
            {
                Page = page, Left = b.Left * k, Right = b.Right * k,
                Top = pageHeight - b.Top * k, Bottom = pageHeight - b.Bottom * k,
                Size = Math.Max(4, Math.Round(tallest / 1.15 * 2) / 2),
            };
            line.Runs.Add((ocr.Text, new TextStyle { Size = line.Size }));
            lines.Add(line);
        }
        // Body lines vary a little in measured height; snap the ones near the common size to it.
        if (lines.Count > 2)
        {
            var sizes = lines.Select(l => l.Size).OrderBy(s => s).ToList();
            double median = sizes[sizes.Count / 2];
            foreach (var l in lines.Where(l => Math.Abs(l.Size - median) <= median * 0.2))
            {
                l.Size = median;
                l.Runs[0] = (l.Runs[0].text, l.Runs[0].style with { Size = median });
            }
        }
        return lines;
    }

    static DDocument Reflow(List<PageText> pages, string title)
    {
        var doc = new DDocument { Title = title };
        if (pages.Count > 0) (doc.PageWidth, doc.PageHeight) = (pages[0].Width, pages[0].Height);
        var allLines = pages.SelectMany(p => p.Lines).ToList();

        double bodySize = allLines.SelectMany(l => l.Runs.Select(r => (size: Math.Round(r.style.Size ?? l.Size), r.text.Length)))
            .GroupBy(x => x.size).OrderByDescending(g => g.Sum(x => x.Length)).Select(g => g.Key).FirstOrDefault(11);
        doc.FontSize = bodySize;

        // Running headers/footers and page numbers repeat on most pages near the edges; leave them out.
        var repeating = new HashSet<string>();
        if (pages.Count >= 3)
            repeating = pages.SelectMany(p => p.Lines.Where(l => l.Top > p.Height * 0.92 || l.Bottom < p.Height * 0.08).Select(l => Normalize(l.Text)).Distinct())
                .GroupBy(t => t).Where(g => g.Count() >= pages.Count / 2.0).Select(g => g.Key).ToHashSet();

        DParagraph? current = null;
        Line? previous = null;
        foreach (var (lines, images, width, _) in pages)
        {
            var pending = images.OrderByDescending(i => i.top).ToList();
            foreach (var line in lines)
            {
                if (repeating.Contains(Normalize(line.Text))) continue;
                while (pending.Count > 0 && pending[0].top >= line.Top)
                {
                    current = null;
                    var picture = new DParagraph { SpaceAfter = 6 };
                    picture.Inlines.Add(pending[0].image);
                    doc.Blocks.Add(picture);
                    pending.RemoveAt(0);
                }

                var text = line.Text.Trim();
                if (text.Length == 0) continue;
                var bullet = BulletRegex().Match(text);
                bool startsNew = current == null || previous == null || bullet.Success
                    || line.Page != previous.Page && EndsSentence(previous.Text)
                    || line.Page == previous.Page && previous.Bottom - line.Top > Math.Max(previous.Size, line.Size) * 0.9
                    || Math.Abs(line.Size - previous.Size) > 1
                    || previous.Right < width * 0.62 && EndsSentence(previous.Text)
                    || current.Heading > 0 && line.Size < previous.Size - 0.5;
                if (startsNew)
                {
                    current = new DParagraph { SpaceAfter = 6 };
                    double ratio = line.Size / Math.Max(1, bodySize);
                    if (!bullet.Success && text.Length < 120)
                        current.Heading = ratio >= 1.7 ? 1 : ratio >= 1.35 ? 2 : ratio >= 1.15 ? 3 : 0;
                    if (current.Heading > 0) current.SpaceBefore = 6;
                    if (bullet.Success)
                    {
                        bool numbered = char.IsDigit(bullet.Value[0]) || char.IsLetter(bullet.Value[0]);
                        current.List = numbered ? ListKind.Number : ListKind.Bullet;
                        current.ListLabel = numbered ? bullet.Groups[1].Value : "•";
                        current.IndentLeft = 36;
                        current.IndentFirst = -18;
                        current.SpaceAfter = 2;
                    }
                    double center = (line.Left + line.Right) / 2;
                    if (Math.Abs(center - width / 2) < width * 0.04 && line.Right - line.Left < width * 0.6 && line.Left > width * 0.2)
                        current.Align = TextAlignment.Center;
                    doc.Blocks.Add(current);
                }
                else
                {
                    // Join with the previous line, undoing hyphenation ("conver-" + "sion").
                    var last = current!.Inlines.LastOrDefault() as DText;
                    if (last != null && last.Text.EndsWith('-') && last.Text.Length > 1 && char.IsLetter(last.Text[^2]) && text.Length > 0 && char.IsLower(text[0]))
                        last.Text = last.Text[..^1];
                    else if (last != null && !last.Text.EndsWith(' ')) last.Text += " ";
                }

                bool firstRun = true;
                foreach (var (runText, style) in line.Runs)
                {
                    var t = firstRun ? runText.TrimStart() : runText;
                    if (firstRun && bullet.Success) t = t.Length >= bullet.Length ? t[bullet.Length..].TrimStart() : "";
                    firstRun = false;
                    if (t.Length == 0) continue;
                    var s = style;
                    if (current!.Heading > 0) s = s with { Size = null, Bold = false };
                    if (current.Inlines.LastOrDefault() is DText prev && prev.Style == s) prev.Text += t;
                    else current.Inlines.Add(new DText(t, s));
                }
                previous = line;
            }
            foreach (var (_, image) in pending)
            {
                var picture = new DParagraph { SpaceAfter = 6 };
                picture.Inlines.Add(image);
                doc.Blocks.Add(picture);
                current = null;
            }
        }
        foreach (var p in doc.Blocks.OfType<DParagraph>())
            if (p.Inlines.LastOrDefault() is DText last) last.Text = last.Text.TrimEnd();
        doc.NormalizeDefaults();
        return doc;
    }

    static bool EndsSentence(string text) => text.TrimEnd() is { Length: > 0 } t && ".!?:;\"”)".Contains(t[^1]);
    static string Normalize(string text) => Regex.Replace(text.Trim(), @"\d+", "#");

    [GeneratedRegex(@"^([•◦▪●○■□–\-*]|\(?\d{1,3}[.)]|[a-zA-Z][.)])\s+")]
    private static partial Regex BulletRegex();

    // ------------------------------------------------------------------ PDFium

    /// <summary>Minimal binding to PDFium (bundled pdfium.dll), the PDF engine used by Chrome.</summary>
    internal sealed class Pdfium : IDisposable
    {
        static readonly object Gate = new();
        static bool _initialised;
        readonly GCHandle _data;
        readonly IntPtr _doc;

        public Pdfium(string path)
        {
            lock (Gate)
            {
                if (!_initialised) { FPDF_InitLibrary(); _initialised = true; }
            }
            var bytes = File.ReadAllBytes(path);
            _data = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            _doc = FPDF_LoadMemDocument(_data.AddrOfPinnedObject(), bytes.Length, null);
            if (_doc == IntPtr.Zero)
            {
                _data.Free();
                throw new ConversionException(FPDF_GetLastError() == 4 ? "This PDF is password-protected." : "This PDF is damaged or can't be read.");
            }
        }

        public int PageCount => FPDF_GetPageCount(_doc);

        public BitmapSource Render(int index, double dpi)
        {
            var page = FPDF_LoadPage(_doc, index);
            try
            {
                double scale = dpi / 72;
                int w = Math.Max(1, (int)Math.Round(FPDF_GetPageWidthF(page) * scale)), h = Math.Max(1, (int)Math.Round(FPDF_GetPageHeightF(page) * scale));
                var bitmap = FPDFBitmap_Create(w, h, 1);
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, 0x01 | 0x800); // annotations, print quality
                    return Copy(bitmap, w, h);
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            finally { FPDF_ClosePage(page); }
        }

        static BitmapSource Copy(IntPtr bitmap, int w, int h)
        {
            int stride = FPDFBitmap_GetStride(bitmap);
            var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, FPDFBitmap_GetBuffer(bitmap), stride * h, stride);
            source.Freeze();
            return source;
        }

        public (List<Line> lines, List<(double top, DImage image)> images, double width, double height) ReadPage(int index)
        {
            var page = FPDF_LoadPage(_doc, index);
            var text = FPDFText_LoadPage(page);
            try
            {
                double width = FPDF_GetPageWidthF(page), height = FPDF_GetPageHeightF(page);
                var lines = new List<Line>();
                var line = new Line { Page = index };
                var run = new StringBuilder();
                TextStyle? runStyle = null;
                var fontName = new byte[256];
                var sizes = new Dictionary<double, int>();

                void FlushRun()
                {
                    if (run.Length > 0 && runStyle != null) line.Runs.Add((run.ToString(), runStyle));
                    run.Clear();
                }
                void FlushLine()
                {
                    FlushRun();
                    if (line.Runs.Count > 0 && line.Text.Trim().Length > 0)
                    {
                        line.Size = sizes.OrderByDescending(kv => kv.Value).First().Key;
                        lines.Add(line);
                    }
                    line = new Line { Page = index };
                    sizes.Clear();
                }

                int count = FPDFText_CountChars(text);
                for (int i = 0; i < count; i++)
                {
                    uint code = FPDFText_GetUnicode(text, i);
                    if (code == '\n') { FlushLine(); continue; }
                    if (code is '\r' or 0 or 0xFFFE or 0xFFFF) continue;
                    var ch = code > 0xFFFF ? char.ConvertFromUtf32((int)code) : ((char)code).ToString();
                    if (ch == "\u0002") ch = "-"; // soft hyphen marker

                    double size = Math.Round(FPDFText_GetFontSize(text, i) * 2) / 2;
                    if (size <= 0) size = line.Size > 0 ? line.Size : 11;
                    if (FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) && !char.IsWhiteSpace(ch[0]))
                    {
                        line.Left = Math.Min(line.Left, left);
                        line.Right = Math.Max(line.Right, right);
                        line.Top = Math.Max(line.Top, top);
                        line.Bottom = Math.Min(line.Bottom, bottom);
                        sizes[size] = sizes.GetValueOrDefault(size) + 1;
                    }
                    int nameLength = (int)FPDFText_GetFontInfo(text, i, fontName, (uint)fontName.Length, out int flags);
                    var name = nameLength > 1 ? Encoding.UTF8.GetString(fontName, 0, nameLength - 1) : "";
                    bool bold = FPDFText_GetFontWeight(text, i) >= 600 || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || (flags & (1 << 18)) != 0;
                    bool italic = (flags & (1 << 6)) != 0 || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                    var style = new TextStyle { Bold = bold, Italic = italic, Size = size };
                    if (char.IsWhiteSpace(ch[0]) && runStyle != null) style = runStyle; // spaces don't break runs
                    if (runStyle != null && style != runStyle) FlushRun();
                    runStyle = style;
                    run.Append(ch);
                }
                FlushLine();

                var images = new List<(double top, DImage image)>();
                int objects = FPDFPage_CountObjects(page);
                for (int i = 0; i < objects && images.Count < 200; i++)
                {
                    var obj = FPDFPage_GetObject(page, i);
                    if (FPDFPageObj_GetType(obj) != 3) continue; // image
                    if (!FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t) || r - l < 24 || t - b < 24) continue;
                    var bitmap = FPDFImageObj_GetRenderedBitmap(_doc, page, obj);
                    if (bitmap == IntPtr.Zero) continue;
                    try
                    {
                        int bw = FPDFBitmap_GetWidth(bitmap), bh = FPDFBitmap_GetHeight(bitmap);
                        if (FPDFBitmap_GetFormat(bitmap) != 4) continue; // BGRA only
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(Copy(bitmap, bw, bh)));
                        using var ms = new MemoryStream();
                        png.Save(ms);
                        images.Add((t, new DImage(ms.ToArray(), "png", r - l, t - b)));
                    }
                    finally { FPDFBitmap_Destroy(bitmap); }
                }
                return (lines, images, width, height);
            }
            finally
            {
                FPDFText_ClosePage(text);
                FPDF_ClosePage(page);
            }
        }

        public void Dispose()
        {
            FPDF_CloseDocument(_doc);
            _data.Free();
        }

        const string Dll = "pdfium.dll";
        [DllImport(Dll)] static extern void FPDF_InitLibrary();
        [DllImport(Dll)] static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
        [DllImport(Dll)] static extern uint FPDF_GetLastError();
        [DllImport(Dll)] static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Dll)] static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Dll)] static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Dll)] static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll)] static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Dll)] static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
        [DllImport(Dll)] static extern IntPtr FPDFBitmap_Create(int w, int h, int alpha);
        [DllImport(Dll)] static extern void FPDFBitmap_FillRect(IntPtr bitmap, int x, int y, int w, int h, uint color);
        [DllImport(Dll)] static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
        [DllImport(Dll)] static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
        [DllImport(Dll)] static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
        [DllImport(Dll)] static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Dll)] static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Dll)] static extern void FPDFText_ClosePage(IntPtr text);
        [DllImport(Dll)] static extern int FPDFText_CountChars(IntPtr text);
        [DllImport(Dll)] static extern uint FPDFText_GetUnicode(IntPtr text, int index);
        [DllImport(Dll)] static extern double FPDFText_GetFontSize(IntPtr text, int index);
        [DllImport(Dll)] static extern int FPDFText_GetFontWeight(IntPtr text, int index);
        [DllImport(Dll)] static extern uint FPDFText_GetFontInfo(IntPtr text, int index, byte[] buffer, uint length, out int flags);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFText_GetCharBox(IntPtr text, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Dll)] static extern int FPDFPage_CountObjects(IntPtr page);
        [DllImport(Dll)] static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
        [DllImport(Dll)] static extern int FPDFPageObj_GetType(IntPtr obj);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFPageObj_GetBounds(IntPtr obj, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll)] static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr doc, IntPtr page, IntPtr obj);
    }
}
