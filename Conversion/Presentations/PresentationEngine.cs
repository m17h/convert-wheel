namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Converts PowerPoint and OpenDocument presentations without any Office installed: slides are read
/// into positioned shapes, then drawn to PDF or pictures, or written out in the other format.
/// </summary>
internal static class PresentationEngine
{
    static readonly HashSet<string> Inputs = ["pptx", "odp", "ppt"];
    static readonly HashSet<string> Outputs = ["pdf", "png", "jpg", "webp", "pptx", "odp"];

    public static bool CanConvert(string sourceExt, string targetExt) => Inputs.Contains(sourceExt) && Outputs.Contains(targetExt);

    /// <summary>Runs on an STA thread (slides are laid out with WPF).</summary>
    public static string Convert(string input, string output, string ext, IProgress<double?> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var deck = Read(input, ct);
        ct.ThrowIfCancellationRequested();
        switch (ext)
        {
            case "pdf":
                new SlideRenderer(deck).ToPdf(output, progress, ct);
                return output;
            case "png" or "jpg" or "webp":
                return new SlideRenderer(deck).ToImages(output, ext, progress, ct);
            case "pptx":
                PptxWriter.Write(deck, output, ct);
                return output;
            case "odp":
                OdpWriter.Write(deck, output, ct);
                return output;
            default:
                throw new ConversionException($"Can't convert presentations to {ext.ToUpperInvariant()}.");
        }
    }

    public static Deck Read(string input, CancellationToken ct = default) => Path.GetExtension(input).ToLowerInvariant() switch
    {
        ".odp" => OdpReader.Read(input, ct),
        ".ppt" or ".pps" or ".pot" => PptReader.Read(input, ct),
        _ => PptxReader.Read(input, ct),
    };
}
