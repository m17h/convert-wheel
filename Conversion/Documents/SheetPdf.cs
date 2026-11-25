using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ConvertWheel.Conversion.Documents;

/// <summary>
/// Lays sheets out as tables in a PDF. Pages are drawn one after another, so long tables take time and memory in
/// proportion; wide ones turn the page, then shrink, then continue on further pages to the right.
/// </summary>
internal sealed class SheetPdf
{
    // Everything is in WPF units (1/96 inch).
    const double Margin = 48, BaseSize = 12, PadX = 4, PadY = 1.5, NarrowestColumn = 26, WidestColumn = 340;
    /// <summary>Past this PDFs take too long to write and no viewer enjoys them.</summary>
    const long MostCells = 2_000_000;

    static readonly Brush Ink = Brushes.Black;
    static readonly XPen Grid = new(XColor.FromArgb(0x80, 0x80, 0x80), 0.6);

    readonly PdfDocument _pdf;
    readonly CancellationToken _ct;
    readonly Dictionary<ImageSource, XImage> _images = [];
    readonly TextMeasure _text = new();

    SheetPdf(PdfDocument pdf, CancellationToken ct) => (_pdf, _ct) = (pdf, ct);

    public static void Write(List<Sheet> sheets, string path, string title, CancellationToken ct)
    {
        long cells = sheets.Sum(s => (long)s.Rows.Sum(r => (long)r.Length));
        if (cells > MostCells) throw new ConversionException($"This spreadsheet is too large for a PDF ({cells:N0} cells). Try CSV or HTML instead.");
        using var pdf = new PdfDocument();
        if (!string.IsNullOrEmpty(title)) pdf.Info.Title = title;
        pdf.Info.Creator = "Convert Wheel";
        var writer = new SheetPdf(pdf, ct);
        foreach (var sheet in sheets) new SheetPages(writer, sheet, sheets.Count > 1 ? sheet.Name : null).Draw();
        if (pdf.PageCount == 0)
        {
            var page = pdf.AddPage();
            (page.Width, page.Height) = (XUnit.FromPoint(DDocument.DefaultPage.Width), XUnit.FromPoint(DDocument.DefaultPage.Height));
        }
        ct.ThrowIfCancellationRequested();
        pdf.Save(path);
    }

    // ------------------------------------------------------------------ text

    /// <summary>Measures and wraps text in the sheet font; anything with shaping or missing from the font is left to WPF.</summary>
    sealed class TextMeasure
    {
        public readonly GlyphTypeface Face;
        public readonly Typeface Typeface;
        /// <summary>Line height and baseline as multiples of the font size.</summary>
        public readonly double LineHeight, Baseline;
        readonly float[] _advance = new float[65536]; // 0 unknown, -1 not for us

        public TextMeasure()
        {
            Typeface = new Typeface(new FontFamily("Calibri, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!new Typeface("Calibri").TryGetGlyphTypeface(out Face!) && !new Typeface("Segoe UI").TryGetGlyphTypeface(out Face!)) new Typeface("Arial").TryGetGlyphTypeface(out Face!);
            (LineHeight, Baseline) = (Math.Max(1.15, Face.Height), Face.Baseline);
        }

        float Advance(char c)
        {
            float a = _advance[c];
            if (a != 0) return a;
            a = !PdfRenderer.IsComplex(c) && !char.IsSurrogate(c) && Face.CharacterToGlyphMap.TryGetValue(c, out var glyph) ? (float)Face.AdvanceWidths[glyph] : -1;
            return _advance[c] = a == 0 ? 1e-6f : a;
        }

        /// <summary>Width of one line as a multiple of the font size, or null when WPF has to lay the text out.</summary>
        public double? Width(ReadOnlySpan<char> line)
        {
            double width = 0;
            foreach (char c in line)
            {
                float a = Advance(c);
                if (a < 0) return null;
                width += a;
            }
            return width;
        }

        public FormattedText Format(string text, double size, double width, bool right)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, size, Ink, 1) { LineHeight = size * LineHeight, Trimming = TextTrimming.None };
            if (width > 0) formatted.MaxTextWidth = width;
            formatted.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
            return formatted;
        }

        /// <summary>Breaks text into lines no wider than <paramref name="width"/> (in font sizes), between words where possible.</summary>
        public List<string>? Wrap(string text, double width)
        {
            var lines = new List<string>();
            foreach (var paragraph in text.Split('\n'))
            {
                int start = 0, space = -1;
                double used = 0, atSpace = 0;
                for (int i = 0; i < paragraph.Length; i++)
                {
                    float a = Advance(paragraph[i]);
                    if (a < 0) return null;
                    if (used + a > width && i > start && paragraph[i] != ' ')
                    {
                        bool atWord = space > start;
                        lines.Add(paragraph[start..(atWord ? space : i)].TrimEnd());
                        start = atWord ? space + 1 : i;
                        used = atWord ? used - atSpace : 0;
                        space = -1;
                    }
                    used += a;
                    if (paragraph[i] == ' ') (space, atSpace) = (i, used);
                }
                lines.Add(paragraph[start..]);
            }
            return lines;
        }
    }

    static string Clean(object? value)
    {
        var text = Spreadsheets.Display(value);
        if (text.AsSpan().IndexOfAny('\r', '\t') >= 0) text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        return text;
    }

    static bool IsRightToLeft(string text) => text.Any(c => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= 'ﻼ');

    // ------------------------------------------------------------------ one sheet

    /// <param name="From">The first line of the row's text on this page (rows taller than a page continue on the next).</param>
    readonly record struct Slice(int Row, int From, int Lines);

    sealed class SheetPages(SheetPdf owner, Sheet sheet, string? heading)
    {
        readonly TextMeasure _text = owner._text;
        double _pageWidth, _pageHeight, _size, _line;
        (PdfPage page, double y)? _last;
        int[] _columns = [];
        double[] _widths = [];

        double ContentWidth => _pageWidth - 2 * Margin;
        double ContentHeight => _pageHeight - 2 * Margin;
        double HeadingHeight => heading == null ? 0 : 32;

        public void Draw()
        {
            int count = sheet.ColumnCount;
            _columns = Enumerable.Range(0, count).Where(c => !sheet.HiddenColumns.Contains(c)).ToArray();
            (_pageWidth, _pageHeight) = (Units.ToDip(DDocument.DefaultPage.Width), Units.ToDip(DDocument.DefaultPage.Height));
            _size = BaseSize;
            var groups = _columns.Length == 0 ? [] : Arrange();
            _line = _size * _text.LineHeight;

            if (groups.Count > 0)
            {
                bool header = HasHeader();
                var pages = Paginate(header, out int headerLines);
                foreach (var group in groups)
                    for (int p = 0; p < pages.Count; p++)
                    {
                        owner._ct.ThrowIfCancellationRequested();
                        DrawPage(group, pages[p], header && p > 0 ? headerLines : 0, title: p == 0);
                    }
            }
            // Charts follow on below a table that has room left; after a split table they start a page.
            if (groups.Count != 1) _last = null;
            DrawCharts(startsSheet: groups.Count == 0);
        }

        // -------------------------------------------------------------- columns

        /// <summary>Chooses the page direction, text size and column widths, and which columns share a page.</summary>
        List<int[]> Arrange()
        {
            var natural = new double[sheet.ColumnCount];
            var tight = new double[sheet.ColumnCount]; // the least a column can do with, wrapping its text
            foreach (var row in sheet.Rows)
            {
                owner._ct.ThrowIfCancellationRequested();
                for (int c = 0; c < row.Length; c++)
                {
                    if (row[c] is null) continue;
                    var text = Clean(row[c]);
                    double widest = 0;
                    foreach (var line in text.AsSpan().EnumerateLines())
                        if (line.Length * 1.3 * BaseSize > natural[c]) widest = Math.Max(widest, _text.Width(line) * BaseSize ?? _text.Format(line.Length > 400 ? line[..400].ToString() : line.ToString(), BaseSize, 0, false).WidthIncludingTrailingWhitespace);
                    natural[c] = Math.Max(natural[c], widest);
                    if (widest > tight[c]) tight[c] = Math.Max(tight[c], Spreadsheets.IsNumeric(row[c]) ? widest : Math.Min(widest, 100));
                }
            }
            for (int c = 0; c < natural.Length; c++)
            {
                natural[c] = Math.Clamp(natural[c] + 2 * PadX + 1, NarrowestColumn, WidestColumn);
                tight[c] = Math.Clamp(tight[c] + 2 * PadX + 1, NarrowestColumn, natural[c]);
            }
            double Sum(double[] w) => _columns.Sum(c => w[c]);
            List<int[]> all = [_columns];

            _widths = natural;
            if (Sum(natural) <= ContentWidth) return all;
            (_pageWidth, _pageHeight) = (_pageHeight, _pageWidth);
            if (Sum(natural) <= ContentWidth) return all;
            double tightSum = Sum(tight), naturalSum = Sum(natural);
            if (ContentWidth / naturalSum is >= 0.75 and var shrink)
            {
                _size = BaseSize * shrink;
                _widths = natural.Select(w => w * shrink).ToArray();
                return all;
            }
            if (tightSum <= ContentWidth)
            {
                double give = (ContentWidth - tightSum) / (naturalSum - tightSum);
                _widths = natural.Select((n, c) => tight[c] + (n - tight[c]) * give).ToArray();
                return all;
            }
            if (ContentWidth / tightSum is >= 0.67 and var scale)
            {
                _size = BaseSize * scale;
                _widths = tight.Select(w => w * scale).ToArray();
                return all;
            }

            // Too wide for one page: the columns continue on further pages, with the first one repeated as a label.
            const double small = 0.8;
            _size = BaseSize * small;
            _widths = natural.Select((n, c) => Math.Min(Math.Max(tight[c], Math.Min(n, 220)) * small, ContentWidth)).ToArray();
            var groups = new List<int[]>();
            bool label = _widths[_columns[0]] <= ContentWidth * 0.25;
            var current = new List<int>();
            double used = 0;
            foreach (int c in _columns)
            {
                if (current.Count > 0 && used + _widths[c] > ContentWidth + 0.01)
                {
                    groups.Add([.. current]);
                    current = label ? [_columns[0]] : [];
                    used = current.Sum(k => _widths[k]);
                }
                current.Add(c);
                used += _widths[c];
            }
            groups.Add([.. current]);
            return groups;
        }

        /// <summary>The first row repeats on later pages when it reads like headings: text in (nearly) every column.</summary>
        bool HasHeader()
        {
            if (sheet.Rows.Count < 3 || sheet.HiddenRows.Contains(0)) return false;
            var first = sheet.Rows[0];
            int filled = _columns.Count(c => c < first.Length && first[c] is not null);
            return filled >= _columns.Length * 0.6 && _columns.All(c => c >= first.Length || first[c] is null or string && !Spreadsheets.IsNumeric(first[c]));
        }

        // -------------------------------------------------------------- pages

        int Lines(int r)
        {
            var row = sheet.Rows[r];
            int most = 1;
            foreach (int c in _columns)
            {
                if (c >= row.Length || row[c] is null) continue;
                var text = Clean(row[c]);
                double inner = _widths[c] - 2 * PadX + 0.5;
                // Short text is nearly always one line.
                if (text.Length * 1.3 * _size < inner && !text.Contains('\n') && text.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0) continue;
                if (!text.Contains('\n') && _text.Width(text) * _size <= inner) continue;
                most = Math.Max(most, _text.Wrap(text, inner / _size)?.Count ?? (int)Math.Round(_text.Format(text, _size, inner, false).Height / _line));
            }
            return most;
        }

        double Height(int lines) => lines * _line + 2 * PadY;

        List<List<Slice>> Paginate(bool header, out int headerLines)
        {
            var pages = new List<List<Slice>>();
            var page = new List<Slice>();
            headerLines = header ? Lines(0) : 0;
            double headerHeight = header ? Height(headerLines) : 0;
            if (headerHeight > ContentHeight / 3) (headerHeight, headerLines) = (0, 0);
            double left = ContentHeight - HeadingHeight;
            int blanks = 0;
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                if ((r & 63) == 0) owner._ct.ThrowIfCancellationRequested();
                if (sheet.HiddenRows.Contains(r)) continue;
                // Long empty stretches would be pages of nothing.
                blanks = sheet.Rows[r].Length == 0 ? blanks + 1 : 0;
                if (blanks > 40) continue;
                int lines = Lines(r), from = 0;
                while (lines > 0)
                {
                    double fresh = ContentHeight - headerHeight;
                    if (Height(lines) > left && page.Count > 0 && (Height(lines) <= fresh || left < fresh / 2)) NextPage();
                    int take = Math.Min(lines, Math.Max(1, (int)((left - 2 * PadY + 0.01) / _line)));
                    page.Add(new Slice(r, from, take));
                    left -= Height(take);
                    from += take;
                    lines -= take;
                    if (lines > 0) NextPage();
                }
            }
            if (page.Count > 0) pages.Add(page);
            return pages;

            void NextPage()
            {
                pages.Add(page);
                page = [];
                left = ContentHeight - headerHeight;
            }
        }

        PdfPage AddPage()
        {
            var page = owner._pdf.AddPage();
            page.Width = XUnit.FromPoint(Units.ToPoints(_pageWidth));
            page.Height = XUnit.FromPoint(Units.ToPoints(_pageHeight));
            return page;
        }

        void DrawPage(int[] columns, List<Slice> slices, int headerLines, bool title)
        {
            var page = AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.ScaleTransform(72.0 / 96.0);
            var writer = new PdfRenderer.PageWriter(gfx, owner._images, page);
            var font = PdfRenderer.FontFor(_text.Face, _size);
            double y = Margin;
            if (title && heading != null) y += DrawHeading(writer, y);
            double top = y, width = columns.Sum(c => _widths[c]);

            void Row(Slice slice)
            {
                gfx.DrawLine(Grid, Margin, y, Margin + width, y);
                var row = sheet.Rows[slice.Row];
                double x = Margin;
                foreach (int c in columns)
                {
                    if (c < row.Length && row[c] is { } value) DrawCell(gfx, writer, font, value, x + PadX, y + PadY, _widths[c] - 2 * PadX, slice);
                    x += _widths[c];
                }
                y += Height(slice.Lines);
            }
            if (headerLines > 0) Row(new Slice(0, 0, headerLines));
            foreach (var slice in slices) Row(slice);
            gfx.DrawLine(Grid, Margin, y, Margin + width, y);
            double line = Margin;
            gfx.DrawLine(Grid, line, top, line, y);
            foreach (int c in columns)
            {
                line += _widths[c];
                gfx.DrawLine(Grid, line, top, line, y);
            }
            writer.Finish();
            _last = (page, y);
        }

        double DrawHeading(PdfRenderer.PageWriter writer, double y)
        {
            var text = new FormattedText(heading!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Calibri, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 17.3, Ink, 1)
            { MaxTextWidth = ContentWidth, MaxLineCount = 1 };
            DrawFormatted(writer, text, Margin, y, double.NegativeInfinity, double.PositiveInfinity);
            return HeadingHeight;
        }

        void DrawCell(XGraphics gfx, PdfRenderer.PageWriter writer, XFont font, object value, double x, double y, double inner, Slice slice)
        {
            var text = Clean(value);
            if (text.Length == 0) return;
            bool right = Spreadsheets.IsNumeric(value);
            List<string>? lines = null;
            if (!text.Contains('\n') && _text.Width(text) is { } width)
            {
                if (width * _size <= inner + 0.5) lines = [text];
                else lines = _text.Wrap(text, (inner + 0.5) / _size);
            }
            else lines = _text.Wrap(text, (inner + 0.5) / _size);

            if (lines == null)
            {
                var formatted = _text.Format(text, _size, inner + 0.5, right || IsRightToLeft(text));
                double top = slice.From * _line;
                DrawFormatted(writer, formatted, x, y - top, y, y + slice.Lines * _line);
                return;
            }
            for (int i = slice.From; i < Math.Min(lines.Count, slice.From + slice.Lines); i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                double at = right ? x + inner - (_text.Width(lines[i]) ?? 0) * _size : x;
                gfx.DrawString(lines[i], font, XBrushes.Black, new XPoint(at, y + (i - slice.From) * _line + _text.Baseline * _size), XStringFormats.BaseLineLeft);
            }
        }

        static void DrawFormatted(PdfRenderer.PageWriter writer, FormattedText text, double x, double y, double from, double to)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawText(text, new Point(x, y));
            writer.Visual(visual, Matrix.Identity, from, to);
        }

        // -------------------------------------------------------------- charts

        /// <summary>A sheet's charts after its data, shrunk to fit the page.</summary>
        void DrawCharts(bool startsSheet)
        {
            XGraphics? gfx = null;
            PdfRenderer.PageWriter? writer = null;
            double y = 0;
            try
            {
                foreach (var chart in sheet.Charts)
                {
                    owner._ct.ThrowIfCancellationRequested();
                    if (FlowBridge.Decode(chart.Data) is not { } source) continue;
                    double w = Units.ToDip(chart.Width), h = Units.ToDip(chart.Height);
                    if (w < 1 || h < 1) continue;
                    double room = ContentHeight - (startsSheet ? HeadingHeight : 0);
                    double scale = Math.Min(1, Math.Min(ContentWidth / w, room / h));
                    (w, h) = (w * scale, h * scale);
                    bool below = writer == null && _last is { } end && end.y + 16 + h <= _pageHeight - Margin;
                    if (writer == null || y + h > _pageHeight - Margin)
                    {
                        gfx?.Dispose();
                        var page = below ? _last!.Value.page : AddPage();
                        gfx = XGraphics.FromPdfPage(page);
                        gfx.ScaleTransform(72.0 / 96.0);
                        writer = new PdfRenderer.PageWriter(gfx, owner._images, page);
                        y = below ? _last!.Value.y + 16 : Margin;
                        if (startsSheet && heading != null) y += DrawHeading(writer, y);
                        startsSheet = false;
                    }
                    writer.Picture(source, new Rect(Margin, y, w, h));
                    y += h + 16;
                }
            }
            finally { gfx?.Dispose(); }
        }
    }
}
