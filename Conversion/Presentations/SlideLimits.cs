using System.Windows;
using System.Xml.Linq;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>Keeps damaged and hostile presentations within what the rest of the app can handle.</summary>
internal static class SlideLimits
{
    /// <summary>Elements inside elements: far beyond any real deck, well within what the readers' recursion can take.</summary>
    public const int Depth = 200;
    const int MaxSlides = 20_000, MaxShapes = 500_000;
    public const int MaxTableCells = 20_000, MaxColumns = 1000;
    /// <summary>In points; the largest slide PowerPoint allows is 4032.</summary>
    const double MaxCoordinate = 100_000;

    public static ConversionException TooLarge() => new("This presentation is too large to convert.");
    public static ConversionException Damaged() => new("This presentation is damaged or uses features that can't be read.");

    public static void CheckSlides(int count)
    {
        if (count > MaxSlides) throw TooLarge();
    }

    public static void CheckShapes(int count)
    {
        if (count > MaxShapes) throw TooLarge();
    }

    /// <summary>Whether a shape that can't be read may be left out, rather than giving up on the whole deck.</summary>
    public static bool CanSkip(Exception ex) => ex is not (ConversionException or OperationCanceledException or OutOfMemoryException);

    /// <summary>Refuses XML nested so deeply that reading it could overflow the stack.</summary>
    public static XElement Check(XElement root)
    {
        int depth = 0;
        XNode? node = root;
        while (node != null)
        {
            if (node is XElement { FirstNode: { } first })
            {
                if (++depth > Depth) throw Damaged();
                node = first;
                continue;
            }
            while (node != null && node != root && node.NextNode == null)
            {
                node = node.Parent;
                depth--;
            }
            node = node == null || node == root ? null : node.NextNode;
        }
        return root;
    }

    /// <summary>Web and e-mail addresses stay links; anything else (files, scripts) is only text.</summary>
    public static string? Link(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? uri.AbsoluteUri : null;

    // ------------------------------------------------------------------ making sense of sizes

    public static void Tidy(Deck deck)
    {
        (deck.Width, deck.Height) = (Side(deck.Width, 720), Side(deck.Height, 540));
        foreach (var shape in deck.Slides.SelectMany(s => s.Shapes)) Tidy(shape);
    }

    /// <summary>A PDF page can't be larger than 14400 points, and a slide smaller than a point shows nothing.</summary>
    static double Side(double value, double fallback) => double.IsFinite(value) && value >= 1 ? Math.Min(value, 14400) : fallback;

    static void Tidy(SlideShape shape)
    {
        shape.Bounds = Tidy(shape.Bounds);
        shape.Rotation = double.IsFinite(shape.Rotation) ? shape.Rotation % 360 : 0;
        shape.LineWidth = Within(shape.LineWidth, 0, 1584, 1);
        if (shape.Adjust is { } adjust && !double.IsFinite(adjust)) shape.Adjust = null;
        var i = shape.Insets;
        shape.Insets = new Thickness(Within(i.Left, 0, 4000, 0), Within(i.Top, 0, 4000, 0), Within(i.Right, 0, 4000, 0), Within(i.Bottom, 0, 4000, 0));
        Tidy(shape.Text, 0);
    }

    static Rect Tidy(Rect r) => r.IsEmpty ? default : new(
        Within(r.X, -MaxCoordinate, MaxCoordinate, 0), Within(r.Y, -MaxCoordinate, MaxCoordinate, 0),
        Within(r.Width, 0, MaxCoordinate, 0), Within(r.Height, 0, MaxCoordinate, 0));

    static void Tidy(List<DBlock> blocks, int depth)
    {
        foreach (var block in blocks)
        {
            if (block is DParagraph p)
            {
                p.SpaceBefore = Within(p.SpaceBefore, 0, 1584, 0);
                p.SpaceAfter = Within(p.SpaceAfter, 0, 1584, 0);
                p.IndentLeft = Within(p.IndentLeft, 0, 4032, 0);
                p.IndentFirst = Within(p.IndentFirst, -4032, 4032, 0);
                p.LineSpacing = Within(p.LineSpacing, 0.1, 100, 1);
                if (p.MarkSize is { } mark) p.MarkSize = FontSize(mark);
                if (p.ListSize is { } size) p.ListSize = Within(size, 0.1, 10, 1);
                foreach (var t in p.Inlines.OfType<DText>())
                    if (t.Style.Size is { } s && FontSize(s) != s) t.Style = t.Style with { Size = FontSize(s) };
            }
            else if (block is DTable t && depth < Depth)
            {
                if (t.ColumnWidths != null) t.ColumnWidths = t.ColumnWidths.Select(w => Within(w, 0, MaxCoordinate, 0)).ToList();
                foreach (var row in t.Rows)
                {
                    if (row.Height is { } h) row.Height = Within(h, 0, MaxCoordinate, 0);
                    foreach (var cell in row.Cells) Tidy(cell.Blocks, depth + 1);
                }
            }
        }
    }

    static double FontSize(double size) => Within(size, 1, 4000, 18);

    static double Within(double value, double least, double most, double fallback) => double.IsFinite(value) ? Math.Clamp(value, least, most) : fallback;
}

/// <summary>Where each cell of a table sits on its grid, with merged cells kept inside the table and off one another.</summary>
internal sealed class TableGrid
{
    public sealed record Place(DCell Cell, int Row, int Column, int RowSpan, int ColumnSpan);

    public int Rows { get; }
    public int Columns { get; }
    public List<Place> Cells { get; } = [];
    readonly DTable _table;
    readonly Dictionary<(int row, int column), Place> _at = [];

    public TableGrid(DTable table)
    {
        _table = table;
        Rows = table.Rows.Count;
        var busy = new List<int>(); // the row each column is free again from
        for (int r = 0; r < Rows; r++)
        {
            int column = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (column < busy.Count && busy[column] > r) column++;
                int wanted = Math.Clamp(cell.ColSpan, 1, SlideLimits.MaxColumns), span = 1;
                while (span < wanted && (column + span >= busy.Count || busy[column + span] <= r)) span++;
                if (column + span > SlideLimits.MaxColumns) throw SlideLimits.TooLarge();
                int rows = Math.Clamp(cell.RowSpan, 1, Rows - r);
                var place = new Place(cell, r, column, rows, span);
                Cells.Add(place);
                while (busy.Count < column + span) busy.Add(0);
                for (int c = column; c < column + span; c++)
                {
                    busy[c] = r + rows;
                    for (int k = 0; k < rows; k++) _at[(r + k, c)] = place;
                }
                if (_at.Count > SlideLimits.MaxTableCells) throw SlideLimits.TooLarge();
                column += span;
            }
        }
        Columns = Math.Max(1, Math.Max(busy.Count, Math.Min(table.ColumnWidths?.Count ?? 0, SlideLimits.MaxColumns)));
    }

    /// <summary>The cell that starts at or covers a grid position.</summary>
    public Place? At(int row, int column) => _at.GetValueOrDefault((row, column));

    public enum Side { Left, Top, Right, Bottom }

    /// <summary>The line that shows on one side of a grid position: where the cells either side of it differ, the heavier one.</summary>
    public CellEdge? Edge(int row, int column, Side side, CellEdge? plain)
    {
        var (otherRow, otherColumn, opposite) = side switch
        {
            Side.Left => (row, column - 1, Side.Right),
            Side.Top => (row - 1, column, Side.Bottom),
            Side.Right => (row, column + 1, Side.Left),
            _ => (row + 1, column, Side.Top),
        };
        Place? cell = At(row, column), other = At(otherRow, otherColumn);
        if (cell != null && cell == other) return null; // inside a merged cell
        var own = cell == null ? null : Of(cell.Cell.Look, side) ?? plain;
        var theirs = other == null ? null : Of(other.Cell.Look, opposite) ?? plain;
        double Weight(CellEdge? edge) => edge?.Color is { A: > 0 } ? edge.Width : -1;
        return Weight(theirs) > Weight(own) ? theirs : own;
    }

    /// <summary>The line along a whole side of a cell; nothing where the cells next to it make it uneven, which leaves it to them.</summary>
    public CellEdge? Edge(Place place, Side side, CellEdge? plain)
    {
        var along = side is Side.Left or Side.Right
            ? Enumerable.Range(place.Row, place.RowSpan).Select(r => Edge(r, side == Side.Left ? place.Column : place.Column + place.ColumnSpan - 1, side, plain))
            : Enumerable.Range(place.Column, place.ColumnSpan).Select(c => Edge(side == Side.Top ? place.Row : place.Row + place.RowSpan - 1, c, side, plain));
        var distinct = along.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    static CellEdge? Of(CellLook? look, Side side) => side switch { Side.Left => look?.Left, Side.Top => look?.Top, Side.Right => look?.Right, _ => look?.Bottom };

    /// <summary>Column widths in points: the table's own, or equal shares of what's left over.</summary>
    public List<double> Widths(double total)
    {
        var known = _table.ColumnWidths ?? [];
        double used = known.Take(Columns).Sum();
        int missing = Columns - known.Take(Columns).Count(w => w > 0);
        double share = missing > 0 ? Math.Max(18, (total - used) / missing) : 0;
        return Enumerable.Range(0, Columns).Select(c => c < known.Count && known[c] > 0 ? known[c] : share).ToList();
    }
}

/// <summary>Reads list labels back into numbering schemes, so written lists stay real lists.</summary>
internal sealed class SlideNumbering
{
    // Per level: the scheme, what surrounds the number, the last number, and the one its run of items started from.
    readonly Dictionary<int, (string scheme, string around, int number, int start)> _levels = [];

    /// <returns>The scheme ("1", "a", "A", "i" or "I"), what surrounds the number ("(x)", "x)", "x." or "x"), this number, and the first of its run.</returns>
    public (string scheme, string around, int number, int start) Next(DParagraph p)
    {
        foreach (var deeper in _levels.Keys.Where(k => k > p.ListLevel).ToList()) _levels.Remove(deeper);
        if (p.List != ListKind.Number || string.IsNullOrWhiteSpace(p.ListLabel))
        {
            if (p.Inlines.Count > 0) _levels.Remove(p.ListLevel);
            return ("1", "x.", 1, 1);
        }
        var label = p.ListLabel.Trim();
        var core = label.TrimStart('(').TrimEnd('.', ')');
        var around = label.StartsWith('(') ? "(x)" : label.EndsWith(')') ? "x)" : label.EndsWith('.') ? "x." : "x";
        bool running = _levels.TryGetValue(p.ListLevel, out var last);
        foreach (var scheme in Schemes(core))
            if (running && last.scheme == scheme && last.around == around && Value(core, scheme) == last.number + 1)
                return _levels[p.ListLevel] = last with { number = last.number + 1 };
        var chosen = Schemes(core).FirstOrDefault(s => Value(core, s) > 0) ?? "1";
        int number = Math.Clamp(Value(core, chosen), 1, 32767);
        return _levels[p.ListLevel] = (chosen, around, number, number);
    }

    /// <summary>A lone "i" starts a Roman list; other single letters start a lettered one.</summary>
    static IEnumerable<string> Schemes(string core)
    {
        if (core.Length == 0) yield break;
        if (char.IsDigit(core[0])) { yield return "1"; yield break; }
        bool upper = char.IsUpper(core[0]);
        bool roman = core.All(ch => "ivxlcdm".Contains(char.ToLowerInvariant(ch)));
        bool romanFirst = roman && (core.Length > 1 || char.ToLowerInvariant(core[0]) == 'i');
        if (romanFirst) yield return upper ? "I" : "i";
        if (core.All(char.IsAsciiLetter)) yield return upper ? "A" : "a";
        if (roman && !romanFirst) yield return upper ? "I" : "i";
    }

    static int Value(string core, string scheme)
    {
        switch (scheme)
        {
            case "1":
                return int.TryParse(core, out var n) ? n : 0;
            case "a" or "A":
                int letters = 0;
                if (core.Length > 3) return 0;
                foreach (var ch in core) letters = letters * 26 + (char.ToLowerInvariant(ch) - 'a' + 1);
                return letters;
            default:
                int total = 0, previous = 0;
                if (core.Length > 15) return 0;
                foreach (var ch in core.Reverse())
                {
                    int v = char.ToLowerInvariant(ch) switch { 'i' => 1, 'v' => 5, 'x' => 10, 'l' => 50, 'c' => 100, 'd' => 500, 'm' => 1000, _ => 0 };
                    total += v < previous ? -v : v;
                    previous = Math.Max(previous, v);
                }
                return total;
        }
    }
}
