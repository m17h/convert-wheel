using ConvertWheel.Conversion;

namespace ConvertWheel.Shell;

/// <summary>One thing the hovered name could refer to.</summary>
/// <param name="TypeName">Explorer's "Type" text, or "" when unknown.</param>
internal sealed record ItemCandidate(string Path, string DisplayName, string TypeName, bool IsFolder);

/// <summary>The file under the mouse.</summary>
/// <param name="Selection">The files selected in that view when the hovered one is among them, otherwise empty.</param>
/// <param name="SelectedCount">How many items are selected with it, including any not listed.</param>
internal sealed record HoveredFile(string Path, IReadOnlyList<string> Selection, int SelectedCount);

/// <summary>
/// Decides which file a hovered name means. Names alone are ambiguous: with extensions hidden, the folder
/// "report", report.docx and a shortcut to either all read "report". Picks only when there's one answer. Pure.
/// </summary>
internal static class HoverMatch
{
    /// <param name="type">The hovered item's "Type" text when the view shows it, otherwise "".</param>
    /// <param name="complete">
    /// True when the candidates are the view's own items. Otherwise they're guesses from the folder on disk,
    /// and even a single one must agree with a known type.
    /// </param>
    public static string? Choose(string name, string type, IReadOnlyList<ItemCandidate> candidates, bool complete)
    {
        var named = candidates
            .Where(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (type.Length > 0 && (named.Count > 1 || !complete))
            named = named.Where(c => string.Equals(c.TypeName, type, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count == 1 && !named[0].IsFolder ? named[0].Path : null;
    }
}

/// <summary>Converting several selected files with one pick.</summary>
internal static class Batch
{
    /// <summary>Enough for a folder of pictures, few enough that a slip of the hand isn't a disaster.</summary>
    public const int MaxFiles = 20;

    /// <summary>The wheel for the hovered file, plus the plans of every file the pick applies to (the hovered one first).</summary>
    public static (ConversionPlan? Plan, IReadOnlyList<ConversionPlan> Batch) Plan(HoveredFile hovered)
    {
        var plan = Formats.Plan(hovered.Path);
        if (plan == null) return (null, []);
        var all = new List<ConversionPlan> { plan };
        if (hovered.Selection.Count <= MaxFiles)
            all.AddRange(hovered.Selection
                .Where(p => !string.Equals(p, hovered.Path, StringComparison.OrdinalIgnoreCase))
                .Select(Formats.Plan)
                .OfType<ConversionPlan>()
                .Where(p => p.SourceKind == plan.SourceKind));
        return (plan, all);
    }

    /// <summary>The files a target applies to: a file that already is an MP3 isn't converted to MP3.</summary>
    public static IReadOnlyList<ConversionPlan> For(IReadOnlyList<ConversionPlan> batch, FormatInfo target) =>
        batch.Where(p => p.All.Any(f => f.Ext == target.Ext)).ToList();
}
