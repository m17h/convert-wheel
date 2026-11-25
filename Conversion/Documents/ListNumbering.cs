using System.Globalization;
using System.Text.RegularExpressions;

namespace ConvertWheel.Conversion.Documents;

/// <summary>A list marker taken apart: "(iii)" is "(", 3 in lowerRoman, ")".</summary>
internal sealed partial record ListMarker(string Prefix, int Number, string Format, string Suffix)
{
    /// <summary>The number as the marker spells it.</summary>
    public string Core => ListLabels.Format(Number, Format);

    /// <param name="expected">The number that would come next: tells a letter "i" from a Roman one.</param>
    /// <param name="format">The format of the list so far, for the same reason.</param>
    public static ListMarker? Parse(string? label, int? expected = null, string? format = null)
    {
        if (label == null || label.Length > 60) return null;
        var m = Pattern().Match(label.Trim());
        if (!m.Success) return null;
        string prefix = m.Groups[1].Value, core = m.Groups[2].Value, suffix = m.Groups[3].Value;
        ListMarker Of(int n, string format) => new(prefix, n, format, suffix);
        if (int.TryParse(core, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            return core == number.ToString(CultureInfo.InvariantCulture) ? Of(number, "decimal") : core.Length == 2 ? Of(number, "decimalZero") : null;

        bool upper = core.All(char.IsAsciiLetterUpper);
        if (!upper && !core.All(char.IsAsciiLetterLower)) return null;
        var big = core.ToUpperInvariant();
        int letters = big.Length == 1 ? big[0] - 'A' + 1 : big.Length == 2 ? (big[0] - 'A' + 1) * 26 + big[1] - 'A' + 1 : 0;
        int roman = big.All(c => "IVXLCDM".Contains(c)) ? Roman(big) : 0;
        string letterFormat = upper ? "upperLetter" : "lowerLetter", romanFormat = upper ? "upperRoman" : "lowerRoman";
        if (roman > 0 && roman == expected) return Of(roman, romanFormat);
        if (letters > 0 && letters == expected) return Of(letters, letterFormat);
        if (roman > 0 && format == romanFormat) return Of(roman, romanFormat);
        if (letters is > 0 and <= 26 && format == letterFormat) return Of(letters, letterFormat);
        if (roman > 0 && (big.Length > 1 || big == "I")) return Of(roman, romanFormat);
        return letters is > 0 and <= 26 ? Of(letters, letterFormat) : null;
    }

    static int Roman(string s)
    {
        for (int n = 1, last = s.Length > 12 ? 0 : 400; n <= last; n++)
            if (ListLabels.Format(n, "upperRoman") == s) return n;
        return 0;
    }

    [GeneratedRegex(@"^(.*?)([0-9]{1,9}|[A-Za-z]{1,12})([^\p{L}\p{N}]*)$")]
    private static partial Regex Pattern();
}

/// <summary>
/// Works out the lists behind a document's list paragraphs, whose markers are already text ("2.", "(a)", "1.3"),
/// so formats that number lists themselves come out with the same numbers.
/// </summary>
internal sealed class ListPlanner
{
    public sealed record LevelLook(ListKind Kind, string Format, string Text, int Start);

    public sealed class ListDefinition(int id)
    {
        public int Id { get; } = id;
        public LevelLook?[] Levels { get; } = new LevelLook?[9];
        internal readonly int?[] Counts = new int?[9];
        internal readonly string?[] Cores = new string?[9];
        /// <summary>The level's number is only there for deeper levels to show: no item of it has been written.</summary>
        internal readonly bool[] Borrowed = new bool[9];
    }

    public List<ListDefinition> Definitions { get; } = [];

    const int MaxDefinitions = 1500;

    /// <summary>The list and level of a paragraph, or null when its marker has to be written out as text.</summary>
    public (ListDefinition list, int level)? Next(DParagraph p)
    {
        int level = Math.Clamp(p.ListLevel, 0, 8);
        var current = Definitions.LastOrDefault();
        if (p.List == ListKind.Bullet)
        {
            var bullet = p.ListLabel is { Length: > 0 } b ? b : "•";
            foreach (var list in Recent())
                if (list.Levels[level] is { Kind: ListKind.Bullet } look && look.Text == bullet || list == current && list.Levels[level] == null)
                    return Use(list, level, new LevelLook(ListKind.Bullet, "bullet", bullet, 1), null, null);
            return New() is { } made ? Use(made, level, new LevelLook(ListKind.Bullet, "bullet", bullet, 1), null, null) : null;
        }

        var label = p.ListLabel?.Trim() ?? "";
        foreach (var list in Recent())
        {
            int? expected = list.Borrowed[level] ? list.Levels[level]?.Start : list.Counts[level] + 1 ?? list.Levels[level]?.Start;
            if (ListMarker.Parse(label, expected, list.Levels[level]?.Format) is not { } marker || Template(list, level, marker) is not { } text) continue;
            var look = list.Levels[level];
            if (look == null ? list == current : look.Kind == ListKind.Number && look.Format == marker.Format && look.Text == text && expected == marker.Number)
                return Use(list, level, new LevelLook(ListKind.Number, marker.Format, text, marker.Number), marker.Number, marker.Core);
        }

        if (New() is not { } fresh) return null;
        // Outer levels carry on, so "2.1" still has its 2.
        if (current != null)
            for (int i = 0; i < level; i++)
            {
                (fresh.Counts[i], fresh.Cores[i], fresh.Borrowed[i]) = (current.Counts[i], current.Cores[i], true);
                if (current.Levels[i] is { } outer) fresh.Levels[i] = outer with { Start = current.Counts[i] ?? outer.Start };
            }
        if (ListMarker.Parse(label) is { } first && Template(fresh, level, first) is { } template)
            return Use(fresh, level, new LevelLook(ListKind.Number, first.Format, template, first.Number), first.Number, first.Core);
        return Use(fresh, level, new LevelLook(ListKind.Number, "none", label.Replace("%", ""), 1), null, null);

        IEnumerable<ListDefinition> Recent() => Definitions.AsEnumerable().Reverse().Take(4);
        ListDefinition? New()
        {
            if (Definitions.Count >= MaxDefinitions) return null;
            Definitions.Add(new ListDefinition(Definitions.Count + 1));
            return Definitions[^1];
        }
    }

    static (ListDefinition, int) Use(ListDefinition list, int level, LevelLook look, int? number, string? core)
    {
        list.Levels[level] ??= look;
        (list.Counts[level], list.Cores[level], list.Borrowed[level]) = (number, core, false);
        for (int i = level + 1; i < 9; i++) (list.Counts[i], list.Cores[i], list.Borrowed[i]) = (null, null, false);
        return (list, level);
    }

    /// <summary>The marker in Word's notation, e.g. "%1.%2)", with the outer levels' numbers recognised.</summary>
    static string? Template(ListDefinition list, int level, ListMarker marker)
    {
        if (marker.Prefix.Contains('%') || marker.Suffix.Contains('%')) return null;
        string prefix = marker.Prefix;
        for (int from = 0; from < level; from++)
        {
            if (Enumerable.Range(from, level - from).Any(i => list.Cores[i] == null)) continue;
            var outer = string.Concat(Enumerable.Range(from, level - from).Select(i => list.Cores[i] + "."));
            if (!prefix.EndsWith(outer, StringComparison.Ordinal)) continue;
            prefix = prefix[..^outer.Length] + string.Concat(Enumerable.Range(from, level - from).Select(i => $"%{i + 1}."));
            break;
        }
        return $"{prefix}%{level + 1}{marker.Suffix}";
    }
}
