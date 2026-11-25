using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace ConvertWheel.Conversion.Documents;

// A small, format-neutral document model. Every reader produces it and every writer consumes it,
// so N input formats and M output formats need N + M pieces of code instead of N × M.
// All lengths are in points (1/72 inch).

internal sealed record TextStyle
{
    public static readonly TextStyle Plain = new();

    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }
    public bool Superscript { get; init; }
    public bool Subscript { get; init; }
    public double? Size { get; init; }
    public Color? Color { get; init; }
    public Color? Highlight { get; init; }
    public string? Font { get; init; }
}

internal abstract class DInline;

internal sealed class DText(string text, TextStyle style, string? link = null) : DInline
{
    public string Text { get; set; } = text;
    public TextStyle Style { get; set; } = style;
    public string? Link { get; set; } = link;
}

internal sealed class DLineBreak : DInline;

/// <param name="Data">Encoded image bytes (PNG, JPEG, GIF, BMP or TIFF).</param>
internal sealed class DImage(byte[] data, string ext, double width, double height) : DInline
{
    public byte[] Data { get; } = data;
    public string Ext { get; } = ext;
    public double Width { get; set; } = width;
    public double Height { get; set; } = height;
    /// <summary>What the picture shows, for people who can't see it.</summary>
    public string? Alt { get; set; }
}

/// <summary>A place other text links to as "#name".</summary>
internal sealed class DAnchor(string name) : DInline
{
    public string Name { get; } = name;
}

internal enum FieldKind { Page, NumPages }

/// <summary>A page-number field; its text is filled in per page when rendering.</summary>
internal sealed class DField(FieldKind kind, TextStyle style) : DInline
{
    public FieldKind Kind { get; } = kind;
    public TextStyle Style { get; set; } = style;
}

/// <summary>A footnote or endnote: the reference mark in the text plus the note's own content.</summary>
internal sealed class DNote(int number, bool endnote, TextStyle style) : DInline
{
    public int Number { get; } = number;
    public bool Endnote { get; } = endnote;
    public TextStyle Style { get; set; } = style;
    public List<DBlock> Blocks { get; } = [];
}

internal enum FloatWrap { Square, TopAndBottom, Behind, InFront }
internal enum FloatFrom { Page, Margin, Paragraph }
internal enum FloatAlign { None, Start, Center, End }

/// <summary>
/// A positioned picture or text box (anchored to the paragraph it appears in). Offsets are from the
/// edge chosen by <see cref="HorizontalFrom"/> / <see cref="VerticalFrom"/>, unless an alignment is set.
/// </summary>
internal sealed class DFloat : DInline
{
    public DImage? Image { get; set; }
    public List<DBlock> Blocks { get; } = [];
    public double Width { get; set; }
    public double Height { get; set; }
    public FloatFrom HorizontalFrom { get; set; } = FloatFrom.Margin;
    public FloatFrom VerticalFrom { get; set; } = FloatFrom.Paragraph;
    public double X { get; set; }
    public double Y { get; set; }
    public FloatAlign HorizontalAlign { get; set; }
    public FloatAlign VerticalAlign { get; set; }
    public FloatWrap Wrap { get; set; } = FloatWrap.Square;
    public Color? Fill { get; set; }
    public Color? Border { get; set; }
}

/// <summary>Running header or footer content.</summary>
internal sealed class DHeaderFooter
{
    public List<DBlock> Blocks { get; } = [];
    public bool IsEmpty => Blocks.Count == 0 || Blocks.All(b => b is DParagraph p && p.Inlines.Count == 0);
}

/// <summary>Page settings of a document section.</summary>
internal sealed record PageSetup(double Width, double Height, Thickness Margins, int Columns = 1, double ColumnGap = 36)
{
    public double ContentWidth => Width - Margins.Left - Margins.Right;
}

internal abstract class DBlock
{
    /// <summary>A new section starts at this block, on a new page with these settings (landscape pages, a different size, columns).</summary>
    public PageSetup? Section { get; set; }
}

internal enum ListKind { None, Bullet, Number }

internal sealed class DParagraph : DBlock
{
    public List<DInline> Inlines { get; } = [];
    public int Heading { get; set; }
    public ListKind List { get; set; }
    public int ListLevel { get; set; }
    /// <summary>The rendered marker, e.g. "•" or "3.", computed by the reader.</summary>
    public string? ListLabel { get; set; }
    /// <summary>Marker look when it differs from the item text (PowerPoint's bullet font, colour and size).</summary>
    public string? ListFont { get; set; }
    public Color? ListColor { get; set; }
    public double? ListSize { get; set; }
    public TextAlignment Align { get; set; } = TextAlignment.Left;
    public double SpaceBefore { get; set; }
    public double SpaceAfter { get; set; }
    public double IndentLeft { get; set; }
    /// <summary>First-line offset from <see cref="IndentLeft"/>; negative for a hanging indent.</summary>
    public double IndentFirst { get; set; }
    /// <summary>Line height as a multiple of single spacing.</summary>
    public double LineSpacing { get; set; } = 1;
    public bool PageBreakBefore { get; set; }
    /// <summary>Font size of an empty paragraph's line.</summary>
    public double? MarkSize { get; set; }
    /// <summary>Runs right to left (Arabic, Hebrew); <see cref="Align"/> still names the side it's on.</summary>
    public bool RightToLeft { get; set; }

    public string PlainText()
    {
        var sb = new StringBuilder();
        foreach (var inline in Inlines)
        {
            if (inline is DText t) sb.Append(t.Text);
            else if (inline is DLineBreak) sb.Append('\n');
            else if (inline is DNote n) sb.Append('[').Append(n.Endnote ? ListLabels.Format(n.Number, "lowerRoman") : n.Number.ToString()).Append(']');
            else if (inline is DFloat { Blocks.Count: > 0 } f)
                sb.Append(' ').Append(string.Join(" ", DDocument.Paragraphs(f.Blocks).Select(p => p.PlainText()).Where(s => s.Length > 0))).Append(' ');
        }
        return sb.ToString();
    }
}

internal sealed class DTable : DBlock
{
    public List<DRow> Rows { get; } = [];
    public bool Borders { get; set; }
    public List<double>? ColumnWidths { get; set; }
    /// <summary>Grid colour and cell padding (points) when they differ from a document table's (slides).</summary>
    public Color? BorderColor { get; set; }
    public Thickness? CellPadding { get; set; }

    public int ColumnCount
    {
        get
        {
            int columns = 0;
            foreach (var (_, slots) in Slots())
                if (slots.Count > 0) columns = Math.Max(columns, slots[^1].Column + slots[^1].Span);
            return columns;
        }
    }

    /// <summary>
    /// Every row's cells with the grid column each starts in, including (as <see cref="DSlot.Covered"/>) the
    /// places taken by a cell that reaches down from a row above, which the row itself doesn't list.
    /// </summary>
    public IEnumerable<(DRow row, List<DSlot> slots)> Slots()
    {
        // Cells still reaching down: the column they start in, the cell, and how many more rows they cover.
        var reaching = new SortedDictionary<int, (DCell cell, int rows)>();
        foreach (var row in Rows)
        {
            var slots = new List<DSlot>();
            var started = new List<(int column, DCell cell)>();
            int column = 0;
            var above = reaching.ToList();
            int next = 0;
            void Covered(bool all)
            {
                for (; next < above.Count && (all || above[next].Key <= column); next++)
                {
                    var (start, (cell, _)) = above[next];
                    if (start < column) continue; // damaged: cells on top of each other
                    slots.Add(new DSlot(cell, start, Math.Max(1, cell.ColSpan), true));
                    column = start + Math.Max(1, cell.ColSpan);
                }
            }
            foreach (var cell in row.Cells)
            {
                Covered(all: false);
                int span = Math.Max(1, cell.ColSpan);
                if (span > 16384 - column) throw new ConversionException("This table has too many columns.");
                slots.Add(new DSlot(cell, column, span, false));
                if (cell.RowSpan > 1) started.Add((column, cell));
                column += span;
            }
            Covered(all: true);
            foreach (var (start, (cell, rows)) in above)
            {
                if (rows <= 1) reaching.Remove(start);
                else reaching[start] = (cell, rows - 1);
            }
            foreach (var (start, cell) in started) reaching[start] = (cell, cell.RowSpan - 1);
            yield return (row, slots);
        }
    }

    /// <summary>The table's rows in tables of at most <paramref name="size"/> rows. A cell that reaches into the next one goes on there, empty.</summary>
    public IEnumerable<DTable> Pieces(int size)
    {
        DTable? piece = null;
        var continued = new Dictionary<DCell, DCell>(ReferenceEqualityComparer.Instance);
        int index = 0;
        var startRow = new Dictionary<DCell, int>(ReferenceEqualityComparer.Instance);
        foreach (var (row, slots) in Slots())
        {
            if (piece == null || piece.Rows.Count >= size)
            {
                if (piece != null) yield return piece;
                piece = new DTable { Borders = Borders, ColumnWidths = ColumnWidths, BorderColor = BorderColor, CellPadding = CellPadding };
                continued.Clear();
            }
            int first = index - piece.Rows.Count;
            foreach (var slot in slots)
                if (!slot.Covered && slot.Cell.RowSpan > 1) startRow[slot.Cell] = index;
            if (!slots.Any(s => s.Covered && startRow.GetValueOrDefault(s.Cell) < first)) piece.Rows.Add(row);
            else
            {
                var copy = new DRow { Header = row.Header };
                foreach (var slot in slots)
                {
                    if (!slot.Covered) copy.Cells.Add(slot.Cell);
                    else if (startRow.GetValueOrDefault(slot.Cell) < first && !continued.ContainsKey(slot.Cell))
                    {
                        var rest = new DCell { ColSpan = slot.Span, RowSpan = startRow[slot.Cell] + slot.Cell.RowSpan - index, Shading = slot.Cell.Shading };
                        copy.Cells.Add(continued[slot.Cell] = rest);
                    }
                }
                piece.Rows.Add(copy);
            }
            index++;
        }
        if (piece != null) yield return piece;
    }
}

/// <summary>Where a cell sits in its table's grid.</summary>
/// <param name="Covered">The cell started in a row above and reaches into this one.</param>
internal readonly record struct DSlot(DCell Cell, int Column, int Span, bool Covered);

internal sealed class DRow
{
    public List<DCell> Cells { get; } = [];
    public bool Header { get; set; }
    /// <summary>The least height, in points, when the source sets one (slides).</summary>
    public double? Height { get; set; }
}

internal sealed class DCell
{
    public List<DBlock> Blocks { get; } = [];
    public int ColSpan { get; set; } = 1;
    /// <summary>Rows the cell covers; the rows below don't list the cells it covers.</summary>
    public int RowSpan { get; set; } = 1;
    public Color? Shading { get; set; }
    /// <summary>A slide table cell's own anchor, padding and edges.</summary>
    public Presentations.CellLook? Look { get; set; }
}

internal sealed class DDocument
{
    public List<DBlock> Blocks { get; } = [];
    public string Font { get; set; } = "Calibri";
    public double FontSize { get; set; } = 11;
    public double PageWidth { get; set; } = DefaultPage.Width;
    public double PageHeight { get; set; } = DefaultPage.Height;
    /// <summary>Newspaper-style columns on the (first section's) pages.</summary>
    public int Columns { get; set; } = 1;
    public double ColumnGap { get; set; } = 36;
    public Thickness Margins { get; set; } = new(72);
    public string? Title { get; set; }

    public DHeaderFooter? Header { get; set; }
    public DHeaderFooter? Footer { get; set; }
    /// <summary>Used on the first page when set (Word's "different first page").</summary>
    public DHeaderFooter? FirstHeader { get; set; }
    public DHeaderFooter? FirstFooter { get; set; }
    /// <summary>Used on even pages when set.</summary>
    public DHeaderFooter? EvenHeader { get; set; }
    public DHeaderFooter? EvenFooter { get; set; }
    public bool DifferentFirstPage { get; set; }
    /// <summary>Distance from the page's top edge to the header, and bottom edge to the footer.</summary>
    public double HeaderDistance { get; set; } = 36;
    public double FooterDistance { get; set; } = 36;

    /// <param name="firstOfSection">The page opens a section (each section has its own first page).</param>
    public (DHeaderFooter? header, DHeaderFooter? footer) HeaderFooterFor(int pageNumber, bool firstOfSection = false)
    {
        if ((pageNumber == 1 || firstOfSection) && DifferentFirstPage) return (FirstHeader, FirstFooter);
        if (pageNumber % 2 == 0 && (EvenHeader != null || EvenFooter != null)) return (EvenHeader ?? Header, EvenFooter ?? Footer);
        return (Header, Footer);
    }

    public IEnumerable<DNote> Notes() => NotesIn(Blocks);

    static IEnumerable<DNote> NotesIn(IEnumerable<DBlock> blocks) => Paragraphs(blocks)
        .SelectMany(p => p.Inlines).SelectMany(i => i is DNote n ? [n] : i is DFloat f ? NotesIn(f.Blocks) : []);

    public double ContentWidth => PageWidth - Margins.Left - Margins.Right;

    /// <summary>A4 in metric regions, US Letter elsewhere.</summary>
    public static Size DefaultPage => System.Globalization.RegionInfo.CurrentRegion.IsMetric ? new Size(595.3, 841.9) : new Size(612, 792);

    public IEnumerable<DParagraph> AllParagraphs() => Paragraphs(Blocks);

    /// <summary>Every paragraph including those in headers, footers, notes and text boxes.</summary>
    public IEnumerable<DParagraph> EveryParagraph()
    {
        var parts = new[] { Header, Footer, FirstHeader, FirstFooter, EvenHeader, EvenFooter }.Where(h => h != null).SelectMany(h => h!.Blocks);
        return Paragraphs(Blocks.Concat(parts), boxes: true);
    }

    /// <summary>The paragraphs of some blocks and of the tables in them, in reading order.</summary>
    /// <param name="boxes">Also those inside notes and text boxes.</param>
    internal static IEnumerable<DParagraph> Paragraphs(IEnumerable<DBlock> blocks, bool boxes = false)
    {
        // A stack of what's left rather than recursion: however deep the tables go, this stays cheap.
        var pending = new Stack<IEnumerator<DBlock>>();
        pending.Push(blocks.GetEnumerator());
        while (pending.Count > 0)
        {
            if (!pending.Peek().MoveNext()) { pending.Pop().Dispose(); continue; }
            var block = pending.Peek().Current;
            if (block is DTable t) pending.Push(t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).GetEnumerator());
            else if (block is DParagraph p)
            {
                yield return p;
                if (boxes && p.Inlines.Any(i => i is DNote or DFloat))
                    pending.Push(p.Inlines.SelectMany(i => i is DNote n ? n.Blocks : i is DFloat f ? f.Blocks : Enumerable.Empty<DBlock>()).ToList().GetEnumerator());
            }
        }
    }

    /// <summary>
    /// Makes the most common font and size the document defaults and strips them from individual
    /// runs, so writers only emit formatting that actually differs.
    /// </summary>
    public void NormalizeDefaults()
    {
        var texts = EveryParagraph().SelectMany(p => p.Inlines.OfType<DText>()).Where(t => t.Text.Length > 0).ToList();
        if (texts.Count == 0) return;

        // Unset values already mean "the document default", so count them as such (this keeps a second call harmless).
        var font = texts.GroupBy(t => t.Style.Font ?? Font).OrderByDescending(g => g.Sum(t => t.Text.Length)).First().Key;
        var size = texts.GroupBy(t => t.Style.Size ?? FontSize).OrderByDescending(g => g.Sum(t => t.Text.Length)).First().Key;
        if (font != Font || size != FontSize)
        {
            // Make the old defaults explicit before switching, so no run silently changes.
            foreach (var t in texts)
                t.Style = t.Style with { Font = t.Style.Font ?? Font, Size = t.Style.Size ?? FontSize };
        }
        Font = font;
        FontSize = size;

        TextStyle Strip(TextStyle style)
        {
            if (style.Font == Font) style = style with { Font = null };
            if (style.Size == FontSize) style = style with { Size = null };
            if (style.Color is { } c && c.R == 0 && c.G == 0 && c.B == 0) style = style with { Color = null };
            return style;
        }
        foreach (var inline in EveryParagraph().SelectMany(p => p.Inlines))
        {
            if (inline is DText t) t.Style = Strip(t.Style);
            else if (inline is DField f) f.Style = Strip(f.Style);
            else if (inline is DNote n) n.Style = Strip(n.Style);
        }
    }
}

internal static class Units
{
    public const double DipPerPoint = 96.0 / 72.0;
    public static double ToDip(double points) => points * DipPerPoint;
    public static double ToPoints(double dips) => dips / DipPerPoint;
    public static int ToTwips(double points) => (int)Math.Round(points * 20);
    public static long ToEmu(double points) => (long)Math.Round(points * 12700);

    /// <summary>Parses CSS/ODF lengths like "2.5cm", "1in", "12pt", "10mm" into points.</summary>
    public static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        int i = 0;
        while (i < value.Length && (char.IsDigit(value[i]) || value[i] is '.' or '-' or '+')) i++;
        if (!double.TryParse(value[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > 1e6)
            return null;
        return value[i..].Trim().ToLowerInvariant() switch
        {
            "pt" or "" => n,
            "in" => n * 72,
            "cm" => n * 72 / 2.54,
            "mm" => n * 72 / 25.4,
            "pc" => n * 12,
            "px" => n * 0.75,
            _ => null,
        };
    }

    public static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    public static int OdfSpaceCount(string? value)
    {
        // A bad count shouldn't fail the whole document; just keep it within reason.
        return int.TryParse(value, out var n) ? Math.Clamp(n, 1, 100_000) : 1;
    }
}

internal static class SafeXml
{
    public static string Text(string text)
    {
        if (IsFine(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is 9 or 10 or 13 || rune.Value >= 0x20 && rune.Value is not (0xFFFE or 0xFFFF))
                sb.Append(rune.ToString());
        return sb.ToString();
    }

    public static void Clean(XElement root)
    {
        foreach (var text in root.DescendantNodes().OfType<XText>())
            if (!IsFine(text.Value)) text.Value = Text(text.Value);
        foreach (var attribute in root.DescendantsAndSelf().Attributes())
            if (!IsFine(attribute.Value)) attribute.Value = Text(attribute.Value);
    }

    static bool IsFine(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x20 ? c is '\t' or '\n' or '\r' : c < 0xD800 || !char.IsSurrogate(c) && c < 0xFFFE) continue;
            if (!char.IsHighSurrogate(c) || i + 1 == text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
            i++;
        }
        return true;
    }
}

internal static class ListLabels
{
    public static string Format(int n, string format) => format switch
    {
        "lowerLetter" or "a" => Letters(n).ToLowerInvariant(),
        "upperLetter" or "A" => Letters(n),
        "lowerRoman" or "i" => Roman(n).ToLowerInvariant(),
        "upperRoman" or "I" => Roman(n),
        "none" or "" => "",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    static string Letters(int n)
    {
        if (n < 1) return n.ToString();
        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    static string Roman(int n)
    {
        if (n is < 1 or > 3999) return n.ToString();
        string[] m = ["", "M", "MM", "MMM"], c = ["", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM"];
        string[] x = ["", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC"], i = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
        return m[n / 1000] + c[n % 1000 / 100] + x[n % 100 / 10] + i[n % 10];
    }

    /// <summary>Bullets stored as private-use Symbol/Wingdings code points become real Unicode.</summary>
    public static string NormalizeBullet(string text) => text switch
    {
        "" or "·" or "" => "•",
        "" or "§" => "▪",
        "" => "➢",
        "" => "✓",
        "o" => "◦",
        "-" or "–" => "–",
        _ => text.Length == 1 && text[0] >= '' ? "•" : text,
    };
}
