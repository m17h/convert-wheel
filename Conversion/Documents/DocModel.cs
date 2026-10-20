using System.Text;
using System.Windows;
using System.Windows.Media;

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
    public string? Link { get; } = link;
}

internal sealed class DLineBreak : DInline;

/// <param name="Data">Encoded image bytes (PNG, JPEG, GIF, BMP or TIFF).</param>
internal sealed class DImage(byte[] data, string ext, double width, double height) : DInline
{
    public byte[] Data { get; } = data;
    public string Ext { get; } = ext;
    public double Width { get; set; } = width;
    public double Height { get; set; } = height;
}

internal abstract class DBlock;

internal enum ListKind { None, Bullet, Number }

internal sealed class DParagraph : DBlock
{
    public List<DInline> Inlines { get; } = [];
    public int Heading { get; set; }
    public ListKind List { get; set; }
    public int ListLevel { get; set; }
    /// <summary>The rendered marker, e.g. "•" or "3.", computed by the reader.</summary>
    public string? ListLabel { get; set; }
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

    public string PlainText()
    {
        var sb = new StringBuilder();
        foreach (var inline in Inlines)
        {
            if (inline is DText t) sb.Append(t.Text);
            else if (inline is DLineBreak) sb.Append('\n');
        }
        return sb.ToString();
    }
}

internal sealed class DTable : DBlock
{
    public List<DRow> Rows { get; } = [];
    public bool Borders { get; set; }
    public List<double>? ColumnWidths { get; set; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.ColSpan)));
}

internal sealed class DRow
{
    public List<DCell> Cells { get; } = [];
    public bool Header { get; set; }
}

internal sealed class DCell
{
    public List<DBlock> Blocks { get; } = [];
    public int ColSpan { get; set; } = 1;
    public Color? Shading { get; set; }
}

internal sealed class DDocument
{
    public List<DBlock> Blocks { get; } = [];
    public string Font { get; set; } = "Calibri";
    public double FontSize { get; set; } = 11;
    public double PageWidth { get; set; } = DefaultPage.Width;
    public double PageHeight { get; set; } = DefaultPage.Height;
    public Thickness Margins { get; set; } = new(72);
    public string? Title { get; set; }

    public double ContentWidth => PageWidth - Margins.Left - Margins.Right;

    /// <summary>A4 in metric regions, US Letter elsewhere.</summary>
    public static Size DefaultPage => System.Globalization.RegionInfo.CurrentRegion.IsMetric ? new Size(595.3, 841.9) : new Size(612, 792);

    public IEnumerable<DParagraph> AllParagraphs() => Paragraphs(Blocks);

    static IEnumerable<DParagraph> Paragraphs(IEnumerable<DBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is DParagraph p) yield return p;
            else if (block is DTable t)
                foreach (var row in t.Rows)
                    foreach (var cell in row.Cells)
                        foreach (var inner in Paragraphs(cell.Blocks))
                            yield return inner;
        }
    }

    /// <summary>
    /// Makes the most common font and size the document defaults and strips them from individual
    /// runs, so writers only emit formatting that actually differs.
    /// </summary>
    public void NormalizeDefaults()
    {
        var texts = AllParagraphs().SelectMany(p => p.Inlines.OfType<DText>()).Where(t => t.Text.Length > 0).ToList();
        if (texts.Count == 0) return;

        var font = texts.Where(t => t.Style.Font != null).GroupBy(t => t.Style.Font!)
            .OrderByDescending(g => g.Sum(t => t.Text.Length)).FirstOrDefault()?.Key;
        var size = texts.Where(t => t.Style.Size != null).GroupBy(t => t.Style.Size!.Value)
            .OrderByDescending(g => g.Sum(t => t.Text.Length)).FirstOrDefault()?.Key;
        if (font != null) Font = font;
        if (size != null) FontSize = size.Value;

        foreach (var t in texts)
        {
            var style = t.Style;
            if (style.Font == Font) style = style with { Font = null };
            if (style.Size == FontSize) style = style with { Size = null };
            if (style.Color is { } c && c.R == 0 && c.G == 0 && c.B == 0) style = style with { Color = null };
            t.Style = style;
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
        if (!double.TryParse(value[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
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
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
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
