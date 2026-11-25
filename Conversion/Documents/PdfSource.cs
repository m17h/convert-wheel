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
        using var pdf = new Pdfium(input, ct);
        if (ext is "png" or "jpg" or "webp" or "tiff")
            return Pages(pdf, output, ext, progress, ct);
        var doc = Reflow(pdf, Path.GetFileNameWithoutExtension(input), progress, ct);
        BuiltInDocuments.Write(doc, ext, output, ct);
        return output;
    }

    // ------------------------------------------------------------------ pages to pictures

    const double Dpi = 150;

    static string Pages(Pdfium pdf, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        int count = pdf.PageCount;
        if (ext == "tiff")
        {
            if (count <= 0) throw new ConversionException("This file has no pages to convert.");
            // TIFF holds every page in one file.
            using var tiff = new TiffPages(output);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                tiff.Add(pdf.Render(i, Dpi, ct));
                progress.Report((double)(i + 1) / count);
            }
            return output;
        }
        return Outputs.Pages(output, count, (i, path) =>
        {
            ct.ThrowIfCancellationRequested();
            var page = pdf.Render(i, Dpi, ct);
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

    /// <summary>Writes a TIFF one page at a time (WPF's encoder wants every page in memory at once).</summary>
    sealed class TiffPages(string path) : IDisposable
    {
        static readonly System.Drawing.Imaging.ImageCodecInfo Codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Tiff.Guid);
        System.Drawing.Bitmap? _file;

        static System.Drawing.Imaging.EncoderParameters Options(System.Drawing.Imaging.EncoderValue action)
        {
            var options = new System.Drawing.Imaging.EncoderParameters(2);
            options.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)action);
            options.Param[1] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)System.Drawing.Imaging.EncoderValue.CompressionLZW);
            return options;
        }

        public void Add(BitmapSource page)
        {
            var bitmap = new System.Drawing.Bitmap(page.PixelWidth, page.PixelHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                bitmap.SetResolution((float)page.DpiX, (float)page.DpiY);
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                try { new FormatConvertedBitmap(page, PixelFormats.Bgr24, null, 0).CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * data.Height, data.Stride); }
                finally { bitmap.UnlockBits(data); }

                using var options = Options(_file == null ? System.Drawing.Imaging.EncoderValue.MultiFrame : System.Drawing.Imaging.EncoderValue.FrameDimensionPage);
                if (_file != null)
                {
                    _file.SaveAdd(bitmap, options);
                    return;
                }
                bitmap.Save(path, Codec, options);
                _file = bitmap; // the first page stays open: the others are added through it
            }
            catch (ExternalException ex) { throw new IOException($"The TIFF couldn't be written: {ex.Message}", ex); }
            finally { if (!ReferenceEquals(bitmap, _file)) bitmap.Dispose(); }
        }

        public void Dispose()
        {
            if (_file == null) return;
            try
            {
                using var options = Options(System.Drawing.Imaging.EncoderValue.Flush);
                _file.SaveAdd(options);
            }
            catch (ExternalException ex) { Log.Write($"TIFF: {ex.Message}"); }
            _file.Dispose();
            _file = null;
        }
    }

    // ------------------------------------------------------------------ text reflow

    internal sealed class Line
    {
        public readonly List<(string text, TextStyle style)> Runs = [];
        public double Left = double.MaxValue, Right, Top, Bottom = double.MaxValue, Size;
        public int Page;
        /// <summary>Where each run starts; a run ends at a wide gap, so table cells and form fields can be told apart.</summary>
        public readonly List<double> Starts = [];
        /// <summary>The line has gaps as wide as those between table columns.</summary>
        public bool Tabular;
        /// <summary>The line ends in a hyphen PDFium takes to split a word.</summary>
        public bool Hyphenated;
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
            var (lines, images, width, height) = pdf.ReadPage(i, ct);
            // A page with no text but a picture covering most of it is a scan: read it with OCR.
            double covered = images.Sum(im => im.image.Width * im.image.Height) / Math.Max(1, width * height);
            if (lines.Count == 0 && (images.Count == 0 || covered > 0.4) && Ocr.Available)
            {
                var scan = pdf.Render(i, 300, ct);
                var ocrLines = OcrLines(scan, scan.PixelWidth * 72.0 / width, height, i);
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
        return Reflow(pages, title, ct);
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
        var doc = Reflow(pages, title, ct);
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

    static DDocument Reflow(List<PageText> pages, string title, CancellationToken ct = default)
    {
        var doc = new DDocument { Title = title };
        if (pages.Count > 0) (doc.PageWidth, doc.PageHeight) = (pages[0].Width, pages[0].Height);
        var allLines = pages.SelectMany(p => p.Lines).ToList();

        double bodySize = allLines.SelectMany(l => l.Runs.Select(r => (size: Math.Round(r.style.Size ?? l.Size), r.text.Length)))
            .GroupBy(x => x.size).OrderByDescending(g => g.Sum(x => x.Length)).Select(g => g.Key).FirstOrDefault(11);
        doc.FontSize = bodySize;

        var running = RunningLines(pages);
        var words = allLines.SelectMany(l => WordRegex().Matches(l.Text).Select(m => m.Value.ToLowerInvariant())).ToHashSet();
        var margins = Margins(pages);

        DParagraph? current = null;
        Line? previous = null;
        foreach (var (lines, images, width, _) in pages)
        {
            ct.ThrowIfCancellationRequested();
            var pending = images.OrderByDescending(i => i.top).ToList();
            foreach (var line in lines)
            {
                if (running.Contains(line)) continue;
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
                    || line.Tabular || previous.Tabular
                    || !previous.Hyphenated && SplitWord(previous.Text.TrimEnd(), text, words) == null && BreaksEarly(previous, line, margins)
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
                    if (last != null && SplitWord(last.Text.TrimEnd(), text, words) is { } keep)
                        last.Text = last.Text.TrimEnd()[..^(keep ? 0 : 1)];
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

    /// <summary>
    /// Whether a line ending in a hyphen runs on into the next line without a space, and if so whether the hyphen stays
    /// ("well-" + "known") or only marked the line break ("conver-" + "sion"). Null when the lines don't join that way.
    /// </summary>
    static bool? SplitWord(string before, string after, HashSet<string> words)
    {
        if (before.Length < 2 || after.Length == 0 || before[^1] is not ('-' or '\u00AD' or '\u2010')) return null;
        if (before[^1] == '\u00AD') return false; // a soft hyphen only ever shows at a line end
        int start = before.Length - 1;
        while (start > 0 && (char.IsLetterOrDigit(before[start - 1]) || before[start - 1] is '-' or '\'' or '’')) start--;
        int end = 0;
        while (end < after.Length && (char.IsLetterOrDigit(after[end]) || after[end] is '-' or '\'' or '’')) end++;
        var (head, tail) = (before[start..^1], after[..end]);
        if (head.Length == 0 || tail.Length == 0) return null; // a dash, or a minus sign
        if (!char.IsLetter(head[^1]) || !char.IsLower(tail[0])) return char.IsLetterOrDigit(head[^1]) && char.IsLetterOrDigit(tail[0]) ? true : null; // "2020-" + "21", "Jean-" + "Paul"
        if (head.Contains('-') || tail.Contains('-') && !words.Contains((head + tail).ToLowerInvariant())) return true; // "state-of-the-" + "art"
        // The document itself is the best guide; after that, beginnings that usually keep their hyphen.
        if (words.Contains($"{head}-{tail}".ToLowerInvariant())) return true;
        if (words.Contains((head + tail).ToLowerInvariant())) return false;
        return head.Length == 1 || Compounds.Contains(head.ToLowerInvariant()) && !Endings.Contains(tail);
    }

    static readonly HashSet<string> Compounds =
    [
        "all", "class", "cross", "double", "eight", "ex", "far", "first", "five", "four", "free", "full", "half", "hard", "high", "large", "left", "long", "low", "medium",
        "mid", "nine", "non", "north", "old", "one", "open", "quarter", "real", "right", "second", "self", "seven", "short", "single", "six", "small", "so", "soft", "south",
        "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
        "ten", "third", "three", "top", "triple", "two", "user", "well", "wide", "world", "year", "zero",
    ];
    /// <summary>Endings that make one word of the two halves: "short-" + "age", "open-" + "ing".</summary>
    static readonly HashSet<string> Endings =
    [
        "able", "age", "al", "ally", "an", "ant", "ar", "ary", "ate", "ation", "cence", "ed", "ence", "ened", "ener", "ening", "ens", "ent", "er", "ergy", "ers", "ery", "es", "est", "ful", "fully", "hood",
        "ic", "ical", "ied", "ies", "ing", "ings", "ion", "ions", "ish", "ism", "ist", "istic", "ity", "ization", "ized", "less", "like", "ly", "ment", "ments", "mer", "ness", "ous", "ship", "some", "ther", "thing", "tion", "ward", "wards", "y",
    ];

    [GeneratedRegex(@"\p{L}+(?:-\p{L}+)*")]
    private static partial Regex WordRegex();

    // ------------------------------------------------------------------ running headers and footers

    /// <summary>
    /// Running headers, footers and page numbers: lines in the top or bottom edge of the page, apart from the text, that
    /// come back at the same height on most pages. Their words must match, and their numbers may only count pages, so the
    /// first and last rows of a table are never taken for one.
    /// </summary>
    static HashSet<Line> RunningLines(List<PageText> pages)
    {
        var result = new HashSet<Line>();
        if (pages.Count < 3) return result;
        // Headers and footers are the outermost few lines of their page.
        var candidates = new List<(int page, Line line, string key)>();
        for (int p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            var byHeight = page.Lines.OrderByDescending(l => l.Top).ToList();
            for (int i = 0; i < byHeight.Count; i++)
            {
                var line = byHeight[i];
                bool top = line.Bottom > page.Height * 0.88, bottom = line.Top < page.Height * 0.12;
                if (!top && !bottom) continue;
                int fromEdge = top ? i : byHeight.Count - 1 - i;
                if (fromEdge > 5) continue;
                candidates.Add((p, line, (top ? "T" : "B") + NumbersRegex().Replace(line.Text.Trim(), "#")));
            }
        }
        foreach (var group in candidates.GroupBy(c => c.key))
        {
            // At the same height, give or take a little; odd and even pages may mirror each other sideways.
            foreach (var band in Bands(group.OrderBy(c => c.line.Bottom).ToList()))
            {
                var perPage = band.GroupBy(c => c.page).Select(g => g.First()).OrderBy(c => c.page).ToList();
                // Left and right pages may have their own.
                bool alternate = perPage.Count >= 3 && perPage.Zip(perPage.Skip(1)).All(x => (x.Second.page - x.First.page) % 2 == 0);
                int possible = alternate ? (pages.Count + 1) / 2 : pages.Count;
                if (perPage.Count < 3 || perPage.Count < possible * 0.5) continue;
                if (perPage.Count != band.Count) continue; // several alike on one page: rows
                if (group.Key.Contains('#') && !CountsPages(perPage)) continue;
                if (perPage.Count(c => PartOfBody(pages[c.page], c.line)) > perPage.Count / 3) continue;
                // Page numbers go; a title or an invoice number stays once, at the start (or a footer's at the end).
                bool says = NumbersRegex().Replace(PageWordsRegex().Replace(group.Key[1..], ""), "").Any(char.IsLetter);
                var kept = !says ? null : group.Key[0] == 'T' ? perPage[0].line : perPage[^1].line;
                foreach (var c in perPage) if (c.line != kept) result.Add(c.line);
            }
        }
        return result;

        static IEnumerable<List<(int page, Line line, string key)>> Bands(List<(int page, Line line, string key)> sorted)
        {
            var band = new List<(int page, Line line, string key)>();
            foreach (var c in sorted)
            {
                if (band.Count > 0 && c.line.Bottom - band[^1].line.Bottom > 3) { yield return band; band = []; }
                band.Add(c);
            }
            if (band.Count > 0) yield return band;
        }
    }

    /// <summary>The numbers in these lines stay the same from page to page, or go up by one with every page.</summary>
    static bool CountsPages(List<(int page, Line line, string key)> lines)
    {
        var numbers = lines.Select(l => NumbersRegex().Matches(l.line.Text).Select(m => long.TryParse(m.Value, out var n) ? n : -1).ToArray()).ToList();
        int steps = 0;
        bool changes = false;
        for (int k = 0; k < numbers[0].Length; k++)
        {
            if (numbers.All(n => n[k] == numbers[0][k])) continue;
            changes = true;
            // A page or two may be numbered differently (a cover, a restart); the rest have to follow.
            for (int i = 1; i < numbers.Count; i++)
                if (numbers[i][k] - numbers[i - 1][k] != lines[i].page - lines[i - 1].page) steps++;
            if (steps > (numbers.Count - 1) / 10) return false;
        }
        // Nothing but a number that never changes isn't a page number.
        return changes || lines[0].line.Text.Any(char.IsLetter);
    }

    /// <summary>The line belongs to a paragraph or table that carries on into the page: line after line at its own pitch, at the same left or right edge.</summary>
    static bool PartOfBody(PageText page, Line line)
    {
        bool top = line.Bottom > page.Height / 2;
        for (var at = line; ;)
        {
            var next = page.Lines.Where(o => (Math.Abs(o.Left - at.Left) < 3 || Math.Abs(o.Right - at.Right) < 3) && Math.Abs(o.Size - at.Size) < 0.6
                    && (top ? o.Top < at.Top - 2 && at.Bottom - o.Top < at.Size * 1.2 : o.Bottom > at.Bottom + 2 && o.Bottom - at.Top < at.Size * 1.2))
                .MinBy(o => top ? -o.Top : o.Bottom);
            if (next == null) return false;
            if (top ? next.Bottom <= page.Height * 0.88 : next.Top >= page.Height * 0.12) return true;
            at = next;
        }
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumbersRegex();

    [GeneratedRegex(@"\b(page|pg|p|of|seite|von|página|pagina|pág|pag|de|di|sur|sida|av|strona|z|bladzijde|van|side|af)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PageWordsRegex();

    // ------------------------------------------------------------------ line breaks that were meant

    /// <summary>By page and left edge: how far to the right the lines there reach, and how long they usually are.</summary>
    static Dictionary<(int page, int left), (double right, int length, int count)> Margins(List<PageText> pages)
    {
        var result = new Dictionary<(int, int), (double, int, int)>();
        foreach (var group in pages.SelectMany(p => p.Lines).GroupBy(l => (l.Page, (int)Math.Round(l.Left / 6))))
        {
            var lengths = group.Select(l => l.Text.Length).Order().ToList();
            result[group.Key] = (group.Max(l => l.Right), lengths[lengths.Count / 2], lengths.Count);
        }
        return result;
    }

    /// <summary>The line stopped although the next line's first word would have fitted: an address, a list or a column of values rather than running text.</summary>
    static bool BreaksEarly(Line line, Line next, Dictionary<(int page, int left), (double right, int length, int count)> margins)
    {
        if (line.Page != next.Page || !margins.TryGetValue((line.Page, (int)Math.Round(line.Left / 6)), out var margin) || Math.Abs(line.Left - next.Left) > 6) return false;
        // Lines this short, one below the other, aren't wrapped text.
        if (margin.count >= 3 && margin.length < 18) return true;
        int length = Math.Max(1, next.Text.Trim().Length), word = Math.Max(1, next.Text.Trim().IndexOf(' ') is > 0 and var space ? space : length);
        double wordWidth = (next.Right - next.Left) * word / length;
        return margin.count >= 3 && margin.right - line.Right > wordWidth + line.Size * 3;
    }

    [GeneratedRegex(@"^([•◦▪●○■□–\-*]|\(?\d{1,3}[.)]|[a-zA-Z][.)])\s+")]
    private static partial Regex BulletRegex();

    // ------------------------------------------------------------------ PDFium

    /// <summary>
    /// Minimal binding to PDFium (bundled pdfium.dll), the PDF engine used by Chrome. PDFium shares state between all its
    /// documents and isn't thread-safe, so every call is made holding one lock. It's held for a page (or a picture) at a
    /// time, so conversions running side by side take turns; encoding happens outside it.
    /// </summary>
    internal sealed class Pdfium : IDisposable
    {
        static readonly object Gate = new();
        static bool _initialised;
        readonly GCHandle _data;
        IntPtr _doc, _form;
        GCHandle _formInfo;
        bool _disposed;

        /// <summary>Waits for a turn with PDFium, giving up when the conversion is cancelled.</summary>
        readonly struct Turn : IDisposable
        {
            public Turn(CancellationToken ct)
            {
                while (!Monitor.TryEnter(Gate, 100)) ct.ThrowIfCancellationRequested();
            }
            public void Dispose() => Monitor.Exit(Gate);
        }

        public int PageCount { get; }

        public Pdfium(string path, CancellationToken ct = default)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) throw new ConversionException("This file is empty.");
            _data = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                using var turn = new Turn(ct);
                if (!_initialised) { FPDF_InitLibrary(); _initialised = true; }
                _doc = FPDF_LoadMemDocument(_data.AddrOfPinnedObject(), bytes.Length, null);
                if (_doc == IntPtr.Zero)
                    throw new ConversionException(FPDF_GetLastError() == 4 ? "This PDF is password-protected." : "This PDF is damaged or can't be read.");
                PageCount = FPDF_GetPageCount(_doc);
            }
            catch
            {
                _data.Free();
                throw;
            }
        }

        IntPtr LoadPage(int index)
        {
            var page = FPDF_LoadPage(_doc, index);
            return page != IntPtr.Zero ? page : throw new ConversionException($"Page {index + 1} of this PDF is damaged and can't be read.");
        }

        /// <summary>Posters and plans are drawn smaller rather than in hundreds of megabytes.</summary>
        const double MostPixels = 40_000_000;

        public BitmapSource Render(int index, double dpi, CancellationToken ct = default)
        {
            using var turn = new Turn(ct);
            var page = LoadPage(index);
            try
            {
                double scale = dpi / 72, width = FPDF_GetPageWidthF(page), height = FPDF_GetPageHeightF(page);
                scale = Math.Min(scale, Math.Sqrt(MostPixels / Math.Max(1, width * height)));
                if (!RasterLimits.TrySize(width, height, ref scale, out int w, out int h))
                    throw new ConversionException("This PDF page has an invalid size.");
                // PDFium draws straight into the picture's own memory.
                var image = new WriteableBitmap(w, h, scale * 72, scale * 72, PixelFormats.Bgr32, null);
                image.Lock();
                try
                {
                    var bitmap = FPDFBitmap_CreateEx(w, h, 3, image.BackBuffer, image.BackBufferStride); // BGRx
                    if (bitmap == IntPtr.Zero) throw new ConversionException("This PDF page could not be rendered.");
                    try
                    {
                        FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, 0x01 | 0x800); // annotations, print quality
                    }
                    finally { FPDFBitmap_Destroy(bitmap); }
                    image.AddDirtyRect(new Int32Rect(0, 0, w, h));
                }
                finally { image.Unlock(); }
                image.Freeze();
                return image;
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

        public (List<Line> lines, List<(double top, DImage image)> images, double width, double height) ReadPage(int index, CancellationToken ct = default)
        {
            IntPtr page;
            double width, height;
            List<Line> lines;
            var pictures = new List<(int index, double top, double width, double height)>();
            using (new Turn(ct))
            {
                page = LoadPage(index);
                try
                {
                    (width, height) = (FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page));
                    var map = new PageMap(page, width, height);
                    lines = ReadText(page, index, map);
                    foreach (var field in Fields(page, map)) Place(field, lines, index);
                    int objects = FPDFPage_CountObjects(page);
                    for (int i = 0; i < objects && pictures.Count < 200; i++)
                    {
                        var obj = FPDFPage_GetObject(page, i);
                        if (FPDFPageObj_GetType(obj) != 3 || !FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t)) continue; // image
                        var box = map.Box(l, b, r, t);
                        if (box.right - box.left >= 24 && box.top - box.bottom >= 24) pictures.Add((i, box.top, box.right - box.left, box.top - box.bottom));
                    }
                }
                catch
                {
                    FPDF_ClosePage(page);
                    throw;
                }
            }

            var images = new List<(double top, DImage image)>();
            try
            {
                foreach (var (i, top, w, h) in pictures)
                {
                    BitmapSource? source = null;
                    using (new Turn(ct)) source = Picture(page, i);
                    if (source == null) continue;
                    ct.ThrowIfCancellationRequested();
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(source));
                    using var ms = new MemoryStream();
                    png.Save(ms);
                    images.Add((top, new DImage(ms.ToArray(), "png", w, h)));
                }
            }
            finally { using (new Turn(default)) FPDF_ClosePage(page); }
            return (lines, images, width, height);
        }

        BitmapSource? Picture(IntPtr page, int index)
        {
            var obj = FPDFPage_GetObject(page, index);
            if (FPDFImageObj_GetImagePixelSize(obj, out uint pw, out uint ph) && (double)pw * ph > 100_000_000) return null;
            var bitmap = FPDFImageObj_GetRenderedBitmap(_doc, page, obj);
            if (bitmap == IntPtr.Zero) return null;
            try
            {
                if (FPDFBitmap_GetFormat(bitmap) != 4) return null; // BGRA only
                return Copy(bitmap, FPDFBitmap_GetWidth(bitmap), FPDFBitmap_GetHeight(bitmap));
            }
            finally { FPDFBitmap_Destroy(bitmap); }
        }

        /// <summary>Turns PDF coordinates into ones where the page is upright with its origin at the bottom left.</summary>
        sealed class PageMap
        {
            readonly int _rotation;
            readonly double _width, _height, _x, _y;

            public PageMap(IntPtr page, double width, double height)
            {
                (_rotation, _width, _height) = (FPDFPage_GetRotation(page) & 3, width, height);
                if (FPDFPage_GetCropBox(page, out var l, out var b, out _, out _) || FPDFPage_GetMediaBox(page, out l, out b, out _, out _)) (_x, _y) = (l, b);
            }

            (double x, double y) Point(double x, double y)
            {
                (x, y) = (x - _x, y - _y);
                return _rotation switch { 1 => (y, _height - x), 2 => (_width - x, _height - y), 3 => (_width - y, x), _ => (x, y) };
            }

            public (double left, double bottom, double right, double top) Box(double l, double b, double r, double t)
            {
                var (x1, y1) = Point(l, b);
                var (x2, y2) = Point(r, t);
                return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
            }
        }

        static List<Line> ReadText(IntPtr page, int index, PageMap map)
        {
            var lines = new List<Line>();
            var text = FPDFText_LoadPage(page);
            if (text == IntPtr.Zero) return lines;
            try
            {
                var line = new Line { Page = index };
                var run = new StringBuilder();
                TextStyle? runStyle = null;
                var fontName = new byte[256];
                var sizes = new Dictionary<double, int>();
                double runStart = 0, lastRight = double.NaN;

                void FlushRun()
                {
                    if (run.Length > 0 && runStyle != null)
                    {
                        line.Runs.Add((run.ToString(), runStyle));
                        line.Starts.Add(runStart);
                    }
                    run.Clear();
                }
                void FlushLine(bool hyphenated = false)
                {
                    FlushRun();
                    if (line.Runs.Count > 0 && line.Text.Trim().Length > 0)
                    {
                        line.Size = sizes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault(11);
                        line.Hyphenated = hyphenated;
                        lines.Add(line);
                    }
                    line = new Line { Page = index };
                    lastRight = double.NaN;
                    sizes.Clear();
                }

                int count = FPDFText_CountChars(text);
                for (int i = 0; i < count; i++)
                {
                    uint code = FPDFText_GetUnicode(text, i);
                    if (code == '\n') { FlushLine(); continue; }
                    if (code is '\r' or 0 or 0xFFFE or 0xFFFF) continue;
                    // Characters beyond the first plane may come as two halves.
                    string ch;
                    if (code is >= 0xD800 and <= 0xDBFF && i + 1 < count && FPDFText_GetUnicode(text, i + 1) is >= 0xDC00 and <= 0xDFFF and var low)
                    {
                        ch = new string([(char)code, (char)low]);
                        i++;
                    }
                    else ch = code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF ? "�" : code > 0xFFFF ? char.ConvertFromUtf32((int)code) : ((char)code).ToString();
                    bool hyphen = ch == "\u0002"; // a hyphen that splits a word between lines, which PDFium runs together
                    if (hyphen) ch = "-";

                    double size = Math.Round(FPDFText_GetFontSize(text, i) * 2) / 2;
                    if (size <= 0) size = line.Size > 0 ? line.Size : 11;
                    double charLeft = double.NaN;
                    if (FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) && !char.IsWhiteSpace(ch[0]))
                    {
                        var box = map.Box(left, bottom, right, top);
                        charLeft = box.left;
                        // PDFium takes a column of single digits for vertical writing.
                        if (ch[0] < '⺀' && box.top < line.Bottom - size * 0.1 && line.Bottom < double.MaxValue) FlushLine();
                        if (charLeft - lastRight > size * 1.5)
                        {
                            FlushRun();
                            line.Tabular |= charLeft - lastRight > size * 2.2;
                        }
                        lastRight = box.right;
                        line.Left = Math.Min(line.Left, box.left);
                        line.Right = Math.Max(line.Right, box.right);
                        line.Top = Math.Max(line.Top, box.top);
                        line.Bottom = Math.Min(line.Bottom, box.bottom);
                        sizes[size] = sizes.GetValueOrDefault(size) + 1;
                    }
                    int nameLength = (int)FPDFText_GetFontInfo(text, i, fontName, (uint)fontName.Length, out int flags);
                    var name = nameLength > 1 && nameLength <= fontName.Length ? Encoding.UTF8.GetString(fontName, 0, nameLength - 1) : "";
                    bool bold = FPDFText_GetFontWeight(text, i) >= 600 || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || (flags & (1 << 18)) != 0;
                    bool italic = (flags & (1 << 6)) != 0 || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                    var style = new TextStyle { Bold = bold, Italic = italic, Size = size };
                    if (char.IsWhiteSpace(ch[0]) && runStyle != null) style = runStyle; // spaces don't break runs
                    if (runStyle != null && style != runStyle) FlushRun();
                    if (run.Length == 0 && !double.IsNaN(charLeft)) runStart = charLeft;
                    runStyle = style;
                    run.Append(ch);
                    if (hyphen) FlushLine(hyphenated: true);
                }
                FlushLine();
                return lines;
            }
            finally { FPDFText_ClosePage(text); }
        }

        // -------------------------------------------------------------- forms

        sealed record Field(string Text, double Left, double Bottom, double Right, double Top);

        /// <summary>What's filled into the page's form: it isn't part of the page's own text.</summary>
        List<Field> Fields(IntPtr page, PageMap map)
        {
            var fields = new List<Field>();
            int count = FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annotation = FPDFPage_GetAnnot(page, i);
                if (annotation == IntPtr.Zero) continue;
                try
                {
                    if (FPDFAnnot_GetSubtype(annotation) != 20 || (FPDFAnnot_GetFlags(annotation) & 0x22) != 0) continue; // visible form fields
                    if (_form == IntPtr.Zero && !OpenForm()) return fields;
                    if (!FPDFAnnot_GetRect(annotation, out var rect)) continue;
                    var value = FPDFAnnot_GetFormFieldType(_form, annotation) switch
                    {
                        2 or 3 => IsChecked(annotation) ? "☑" : "☐", // check boxes and radio buttons
                        4 or 5 => FieldText(annotation), // lists
                        6 when (FPDFAnnot_GetFormFieldFlags(_form, annotation) & (1 << 13)) == 0 => FieldText(annotation), // text, but not passwords
                        _ => "",
                    };
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var (left, bottom, right, top) = map.Box(Math.Min(rect.Left, rect.Right), Math.Min(rect.Bottom, rect.Top), Math.Max(rect.Left, rect.Right), Math.Max(rect.Bottom, rect.Top));
                    fields.Add(new Field(value.ReplaceLineEndings("\n").Trim(), left, bottom, right, top));
                }
                finally { FPDFPage_CloseAnnot(annotation); }
            }
            return fields;
        }

        string FieldText(IntPtr annotation)
        {
            uint bytes = FPDFAnnot_GetFormFieldValue(_form, annotation, null, 0);
            if (bytes <= 2 || bytes > 4_000_000) return "";
            var buffer = new byte[bytes];
            FPDFAnnot_GetFormFieldValue(_form, annotation, buffer, bytes);
            return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
        }

        /// <summary>The state the box is drawn in.</summary>
        bool IsChecked(IntPtr annotation)
        {
            var buffer = new byte[256];
            uint bytes = FPDFAnnot_GetStringValue(annotation, "AS", buffer, (uint)buffer.Length);
            if (bytes <= 2 || bytes > buffer.Length) return FPDFAnnot_IsChecked(_form, annotation);
            return Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2) != "Off";
        }

        bool OpenForm()
        {
            // Only reading, so none of the callbacks PDFium would use while someone types are needed.
            var info = new byte[1024];
            info[0] = 2; // the structure's version
            _formInfo = GCHandle.Alloc(info, GCHandleType.Pinned);
            _form = FPDFDOC_InitFormFillEnvironment(_doc, _formInfo.AddrOfPinnedObject());
            return _form != IntPtr.Zero;
        }

        /// <summary>Puts a field's value where it stands: after the text to its left on the same line, or on a line of its own.</summary>
        static void Place(Field field, List<Line> lines, int page)
        {
            double middle = (field.Top + field.Bottom) / 2;
            double size = Math.Clamp(Math.Round((field.Top - field.Bottom) * 0.7 * 2) / 2, 6, 12);
            var first = field.Text.Split('\n')[0];
            Line? line = lines.Where(l => l.Bottom < field.Top && l.Top > field.Bottom && (l.Top + l.Bottom) / 2 > middle - size && (l.Top + l.Bottom) / 2 < middle + Math.Max(size, (field.Top - field.Bottom) / 2))
                .OrderBy(l => Math.Abs((l.Top + l.Bottom) / 2 - middle)).FirstOrDefault();
            if (line != null && line.Runs.Count > 0)
            {
                int at = line.Starts.FindIndex(x => x > field.Left - 1);
                if (at < 0) at = line.Runs.Count;
                var style = line.Runs[Math.Min(at, line.Runs.Count - 1)].style;
                if (at > 0 && !line.Runs[at - 1].text.EndsWith(' ')) line.Runs[at - 1] = (line.Runs[at - 1].text + " ", line.Runs[at - 1].style);
                line.Runs.Insert(at, (field.Text.Replace('\n', ' ') + (at < line.Runs.Count ? " " : ""), style));
                line.Starts.Insert(at, field.Left);
                line.Left = Math.Min(line.Left, field.Left);
                return;
            }
            // The first line that starts below the field comes after it.
            int index = lines.FindIndex(l => l.Top < middle && l.Left < field.Right);
            if (index < 0) index = lines.Count;
            double top = field.Top;
            foreach (var text in field.Text.Split('\n'))
            {
                var own = new Line { Page = page, Left = field.Left, Right = Math.Min(field.Right, field.Left + text.Length * size * 0.5), Top = Math.Min(top, field.Top), Bottom = Math.Min(top, field.Top) - size, Size = size, Tabular = true };
                own.Runs.Add((text, new TextStyle { Size = size }));
                own.Starts.Add(field.Left);
                if (text.Trim().Length > 0) lines.Insert(index++, own);
                top -= size * 1.2;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            using (new Turn(default))
            {
                if (_form != IntPtr.Zero) FPDFDOC_ExitFormFillEnvironment(_form);
                FPDF_CloseDocument(_doc);
            }
            if (_formInfo.IsAllocated) _formInfo.Free();
            _data.Free();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RectF { public float Left, Top, Right, Bottom; }

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
        [DllImport(Dll)] static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFPage_GetMediaBox(IntPtr page, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFPage_GetCropBox(IntPtr page, out float left, out float bottom, out float right, out float top);
        [DllImport(Dll)] static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
        [DllImport(Dll)] static extern IntPtr FPDFBitmap_CreateEx(int w, int h, int format, IntPtr scan0, int stride);
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
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFImageObj_GetImagePixelSize(IntPtr obj, out uint width, out uint height);
        [DllImport(Dll)] static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr doc, IntPtr page, IntPtr obj);
        [DllImport(Dll)] static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr doc, IntPtr info);
        [DllImport(Dll)] static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
        [DllImport(Dll)] static extern int FPDFPage_GetAnnotCount(IntPtr page);
        [DllImport(Dll)] static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
        [DllImport(Dll)] static extern void FPDFPage_CloseAnnot(IntPtr annotation);
        [DllImport(Dll)] static extern int FPDFAnnot_GetSubtype(IntPtr annotation);
        [DllImport(Dll)] static extern int FPDFAnnot_GetFlags(IntPtr annotation);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFAnnot_GetRect(IntPtr annotation, out RectF rect);
        [DllImport(Dll)] static extern uint FPDFAnnot_GetStringValue(IntPtr annotation, [MarshalAs(UnmanagedType.LPStr)] string key, byte[] buffer, uint bytes);
        [DllImport(Dll)] static extern int FPDFAnnot_GetFormFieldType(IntPtr form, IntPtr annotation);
        [DllImport(Dll)] static extern int FPDFAnnot_GetFormFieldFlags(IntPtr form, IntPtr annotation);
        [DllImport(Dll)] static extern uint FPDFAnnot_GetFormFieldValue(IntPtr form, IntPtr annotation, byte[]? buffer, uint bytes);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FPDFAnnot_IsChecked(IntPtr form, IntPtr annotation);
    }
}
