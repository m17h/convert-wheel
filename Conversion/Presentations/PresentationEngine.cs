using System.Buffers.Binary;
using System.IO.Compression;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// Converts PowerPoint and OpenDocument presentations without any Office installed: slides are read
/// into positioned shapes, then drawn to PDF or pictures, or written out in the other format.
/// </summary>
internal static class PresentationEngine
{
    static readonly HashSet<string> Inputs = ["pptx", "odp", "ppt"];
    static readonly HashSet<string> Outputs = ["pdf", "png", "jpg", "webp", "pptx", "odp"];
    const string Protected = "This presentation is password-protected. Remove its password and try again.";

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

    public static Deck Read(string input, CancellationToken ct = default)
    {
        try
        {
            var deck = Open(input, ct);
            SlideLimits.Tidy(deck);
            return deck;
        }
        catch (Exception ex) when (ex is EndOfStreamException
            || ex is not (ConversionException or OperationCanceledException or OutOfMemoryException or UnauthorizedAccessException or IOException))
        {
            Log.Write($"Couldn't read the presentation {input}: {ex}");
            throw SlideLimits.Damaged();
        }
    }

    /// <summary>Goes by what the file holds rather than its name: a renamed file still opens, and a locked one is recognised.</summary>
    static Deck Open(string input, CancellationToken ct)
    {
        var title = Path.GetFileNameWithoutExtension(input);
        using var file = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length == 0) throw new ConversionException("This presentation is empty.");
        var magic = new byte[8];
        int read = file.ReadAtLeast(magic, 8, throwOnEndOfStream: false);
        file.Position = 0;
        if (read == 8 && BinaryPrimitives.ReadUInt64BigEndian(magic) == 0xD0CF11E0A1B11AE1)
        {
            if (file.Length > 1024L * 1024 * 1024) throw SlideLimits.TooLarge();
            var bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            var container = new WordBinaryReader.CompoundFile(bytes);
            if (container.Stream("EncryptedPackage") != null) throw new ConversionException(Protected);
            return PptReader.Read(container, title, Protected, ct);
        }
        // A package is found from its end, so one with a spoiled beginning may still open.
        ZipArchive zip;
        try { zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException)
        {
            throw new ConversionException(magic[0] == 'P' && magic[1] == 'K' ? "This presentation is damaged or incomplete." : "This isn't a presentation, or it is damaged.");
        }
        using (zip)
        {
            if (zip.GetEntry("content.xml") == null) return PptxReader.Read(zip, title, ct);
            if (IsEncrypted(zip)) throw new ConversionException(Protected);
            return OdpReader.Read(zip, title, ct);
        }
    }

    /// <summary>OpenDocument keeps the package readable and lists how each part was encrypted.</summary>
    static bool IsEncrypted(ZipArchive zip)
    {
        if (zip.GetEntry("META-INF/manifest.xml") is not { } entry) return false;
        try
        {
            using var stream = PackageResources.Open(entry);
            return PackageResources.LoadXml(stream).Descendants().Any(e => e.Name.LocalName == "encryption-data");
        }
        catch (System.Xml.XmlException) { return false; }
    }
}
