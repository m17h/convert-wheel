using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ConvertWheel.Conversion;

/// <summary>
/// ImageMagick (bundled through Magick.NET) for the long tail of image formats. It only ever sees a stream and
/// the one format we say it is, so a file can't pass for one of its scripting formats or pull in other files.
/// </summary>
internal static partial class MagickImages
{
    static MagickImages()
    {
        try
        {
            var files = ImageMagick.Configuration.ConfigurationFiles.Default;
            files.Policy.Data = Policy;
            files.Delegates.Data = "<delegatemap></delegatemap>"; // no outside programs (it would run an installed Inkscape for SVGs)
            var folder = Path.Combine(Path.GetTempPath(), $"cw-magick-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                MagickNET.Initialize(files, folder);
                // The settings are read on first use, after which the files aren't needed.
                using var first = new MagickImage(new MagickColor("white"), 1, 1);
                first.ToByteArray(MagickFormat.Png32);
            }
            finally { try { Directory.Delete(folder, recursive: true); } catch { } }
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"Couldn't apply the image policy: {ex.Message}");
        }

        // Room for gigapixel pictures, but not for a tiny file that claims to be endless.
        ulong memory = (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        ResourceLimits.Memory = Math.Clamp(memory / 2, 1UL << 30, 8UL << 30);
        ResourceLimits.Area = Math.Clamp(memory / 2, 1UL << 30, 8UL << 30);
        ResourceLimits.Disk = 8UL << 30;
        ResourceLimits.Width = ResourceLimits.Height = 200_000;
        ResourceLimits.ListLength = 10_000;
        ResourceLimits.MaxProfileSize = 64UL << 20;
    }

    /// <summary>Only picture formats: no scripts (MSL, MVG), text, PostScript, web addresses, screen grabs or outside programs.</summary>
    const string Policy = """
        <policymap>
          <policy domain="delegate" rights="none" pattern="*" />
          <policy domain="filter" rights="none" pattern="*" />
          <policy domain="path" rights="none" pattern="@*" />
          <policy domain="coder" rights="none" pattern="*" />
          <policy domain="coder" rights="read|write" pattern="{PNG,PNG8,PNG24,PNG32,PNG64,JPEG,JPG,GIF,GIF87,BMP,BMP2,BMP3,DIB,TIFF,TIFF64,PTIF,WEBP,ICO,CUR,ICON,TGA,PSD,PSB,XCF,PCX,DCX,DDS,DXT1,DXT5,EXR,HDR,QOI,JXL,AVIF,HEIC,HEIF,JP2,J2K,J2C,JPC,JPT,JPM,JNG,MNG,SVG,SVGZ,MSVG,RSVG,XML,EMF,WMF,BGRA,RGBA,RGB,BGR,XC,CANVAS,NULL,ICC,ICM,EXIF,XMP,IPTC,8BIM,APP1}" />
          <policy domain="coder" rights="read" pattern="{DNG,DCRAW,3FR,ARW,CR2,CR3,CRW,DCR,ERF,FFF,IIQ,K25,KDC,MDC,MEF,MOS,MRW,NEF,NRW,ORF,PEF,RAF,RAW,RMF,RW2,RWL,SR2,SRF,SRW,STI,X3F}" />
        </policymap>
        """;

    public static BitmapSource? Decode(string path) => Decode(path, out _);

    /// <param name="profile">The picture's ICC colour profile, when it has an RGB one.</param>
    public static BitmapSource? Decode(string path, out byte[]? profile)
    {
        profile = null;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (FormatOf(ext, Head(path)) is not { } format)
            {
                Log.Write($"Not a picture ImageMagick should read: {path}");
                return null;
            }
            using var image = format == MagickFormat.Svg ? ReadVector(File.ReadAllBytes(path)) : Open(path, format);
            image.AutoOrient();
            if (ext is ".psd" or ".psb") image.ColorSpace = ColorSpace.sRGB;
            if (image.ColorSpace == ColorSpace.sRGB && image.GetColorProfile() is { ColorSpace: ColorSpace.sRGB or ColorSpace.RGB } icc) profile = icc.ToByteArray();
            return ToBitmap(image);
        }
        catch (Exception ex) when (ex is MagickException or IOException or InvalidOperationException or InvalidDataException or XmlException or UnauthorizedAccessException or InsufficientExecutionStackException)
        {
            Log.Write($"ImageMagick couldn't read {path}: {ex.Message}");
            return null;
        }
    }

    static MagickImage Open(string path, MagickFormat format)
    {
        using var stream = File.OpenRead(path);
        return new MagickImage(stream, new MagickReadSettings { Format = format });
    }

    static byte[] Head(string path)
    {
        var head = new byte[64];
        using var f = File.OpenRead(path);
        int n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..n];
    }

    /// <summary>Formats without a signature, or whose signature they share (camera RAW files look like TIFFs).</summary>
    static MagickFormat? FormatFromName(string ext) => ext switch
    {
        ".svg" or ".svgz" => MagickFormat.Svg,
        ".tga" => MagickFormat.Tga,
        ".pcx" => MagickFormat.Pcx,
        ".wmf" => MagickFormat.Wmf,
        ".emf" => MagickFormat.Emf,
        ".dng" => MagickFormat.Dng, ".cr2" => MagickFormat.Cr2, ".cr3" => MagickFormat.Cr3, ".crw" => MagickFormat.Crw,
        ".nef" => MagickFormat.Nef, ".nrw" => MagickFormat.Nrw, ".arw" => MagickFormat.Arw, ".srf" => MagickFormat.Srf,
        ".sr2" => MagickFormat.Sr2, ".orf" => MagickFormat.Orf, ".rw2" => MagickFormat.Rw2, ".raf" => MagickFormat.Raf,
        ".pef" => MagickFormat.Pef, ".srw" => MagickFormat.Srw, ".x3f" => MagickFormat.X3f,
        _ => null,
    };

    /// <summary>What a file is, going by its first bytes, or by its name for formats without a signature.</summary>
    static MagickFormat? FormatOf(string ext, byte[] head)
    {
        var byName = FormatFromName(ext);
        bool signatureFirst = ext is ".tga" or ".pcx" or ".wmf" or ".emf" || byName == null;
        return signatureFirst ? Sniff(head) ?? byName : byName;
    }

    static MagickFormat? Sniff(byte[] h)
    {
        bool Has(int at, ReadOnlySpan<byte> bytes) => h.Length >= at + bytes.Length && h.AsSpan(at, bytes.Length).SequenceEqual(bytes);
        if (Has(0, [0xFF, 0xD8, 0xFF])) return MagickFormat.Jpeg;
        if (Has(0, [0x89, (byte)'P', (byte)'N', (byte)'G'])) return MagickFormat.Png;
        if (Has(0, "GIF8"u8)) return MagickFormat.Gif;
        if (Has(0, "RIFF"u8) && Has(8, "WEBP"u8)) return MagickFormat.WebP;
        if (Has(0, "BM"u8)) return MagickFormat.Bmp;
        if (Has(0, "II*\0"u8) || Has(0, "MM\0*"u8) || Has(0, "II+\0"u8) || Has(0, "MM\0+"u8)) return MagickFormat.Tiff;
        if (Has(0, "8BPS"u8)) return MagickFormat.Psd;
        if (Has(0, "gimp xcf "u8)) return MagickFormat.Xcf;
        if (Has(0, "DDS "u8)) return MagickFormat.Dds;
        if (Has(0, [0x76, 0x2F, 0x31, 0x01])) return MagickFormat.Exr;
        if (Has(0, "#?RADIANCE"u8) || Has(0, "#?RGBE"u8)) return MagickFormat.Hdr;
        if (Has(0, "qoif"u8)) return MagickFormat.Qoi;
        if (Has(0, [0xFF, 0x0A]) || Has(0, [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' '])) return MagickFormat.Jxl;
        if (Has(4, "jP  "u8)) return MagickFormat.Jp2;
        if (Has(0, [0xFF, 0x4F, 0xFF, 0x51])) return MagickFormat.J2k;
        if (Has(4, "ftyp"u8)) return Has(8, "avi"u8) ? MagickFormat.Avif : MagickFormat.Heic;
        if (Has(0, [0, 0, 1, 0])) return MagickFormat.Ico;
        if (Has(40, " EMF"u8)) return MagickFormat.Emf;
        if (Has(0, [0xD7, 0xCD, 0xC6, 0x9A])) return MagickFormat.Wmf;
        return null;
    }

    /// <summary>Whether a WebP is animated: the VP8X header's animation flag.</summary>
    public static bool IsAnimatedWebP(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[21];
            if (f.Read(head, 0, head.Length) < head.Length) return false;
            return head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && head.AsSpan(8, 8).SequenceEqual("WEBPVP8X"u8) && (head[20] & 0x02) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Vector art is rasterised big enough (≥1024 px) to stay sharp as icons or photos.</summary>
    static MagickImage ReadVector(byte[] data)
    {
        // The renderer is native code that recurses into the drawing, so it gets a roomy stack of its own.
        MagickImage? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = Rasterise(LocalSvg(data)); }
            catch (Exception ex) { error = ex; }
        }, 128 << 20) { IsBackground = true };
        thread.Start();
        thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result!;
    }

    static MagickImage Rasterise(byte[] svg)
    {
        var image = new MagickImage(svg, new MagickReadSettings { Format = MagickFormat.Rsvg, BackgroundColor = MagickColors.Transparent });
        uint longest = Math.Max(image.Width, image.Height);
        if (longest is > 0 and < 1024)
        {
            double density = Math.Min(600, 96.0 * 1024 / longest);
            image.Dispose();
            image = new MagickImage(svg, new MagickReadSettings { Format = MagickFormat.Rsvg, BackgroundColor = MagickColors.Transparent, Density = new Density(density) });
        }
        return image;
    }

    /// <summary>
    /// The drawing without anything that points outside itself: linked pictures, style sheets and entities in
    /// other files or on the web. Pictures embedded in the drawing stay. Absurdly nested drawings are refused.
    /// </summary>
    static byte[] LocalSvg(byte[] data)
    {
        Stream stream = new MemoryStream(data);
        if (data is [0x1F, 0x8B, ..]) stream = new GZipStream(stream, CompressionMode.Decompress);
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 10_000_000, MaxCharactersInDocument = 256L << 20 };
        ms.Position = 0;
        using (var scan = XmlReader.Create(ms, settings))
            while (scan.Read())
                if (scan.Depth > MaxSvgDepth) throw new InvalidDataException("This SVG is nested too deeply.");
        ms.Position = 0;
        XDocument doc;
        using (var reader = XmlReader.Create(ms, settings)) doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (doc.Root == null) throw new InvalidDataException("This SVG is empty.");

        doc.DocumentType?.Remove();
        if (doc.Root.GetPrefixOfNamespace(XLink) == null) doc.Root.SetAttributeValue(XNamespace.Xmlns + "xlink", XLink.NamespaceName);
        foreach (var node in doc.DescendantNodes().OfType<XProcessingInstruction>().ToList()) node.Remove();
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "script" || e.Name.NamespaceName == "http://www.w3.org/2001/XInclude").ToList()) e.Remove();
        foreach (var e in doc.Descendants())
        {
            foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList())
            {
                if (a.Name.LocalName == "base" || a.Name.LocalName is "href" or "src" && e.Name.LocalName != "a" && !IsLocal(a.Value)) a.Remove();
                else if (a.Value.Contains('(')) a.Value = LocalCss(a.Value);
            }
            // The renderer only knows the older spelling of SVG 2's plain "href".
            if (e.Attribute("href") is { } href && e.Attribute(XLink + "href") == null) e.SetAttributeValue(XLink + "href", href.Value);
            if (e.Name.LocalName == "style" && e.Value.Length > 0) e.Value = CssImport().Replace(LocalCss(e.Value), "");
        }
        CheckDepth(doc.Root);
        ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false) })) doc.Save(writer);
        return ms.ToArray();
    }

    const int MaxSvgDepth = 500;
    static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>Counts nesting the way a renderer meets it, following references (use, clip paths, masks, patterns).</summary>
    static void CheckDepth(XElement root)
    {
        var byId = new Dictionary<string, XElement>();
        foreach (var e in root.DescendantsAndSelf())
            if ((string?)e.Attribute("id") is { } id) byId.TryAdd(id, e);
        var known = new Dictionary<XElement, (int height, long size)>();
        var active = new HashSet<XElement>();
        Visit(root, 1);

        // A small file can also draw the same parts over and over, a million times: "size" counts those.
        (int height, long size) Visit(XElement e, int depth)
        {
            if (depth > MaxSvgDepth) throw new InvalidDataException("This SVG is nested too deeply.");
            if (known.TryGetValue(e, out var result))
            {
                if (depth + result.height > MaxSvgDepth) throw new InvalidDataException("This SVG is nested too deeply.");
                return result;
            }
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (!active.Add(e)) return (0, 0); // a loop, which the renderer detects
            int height = 0;
            long size = 1;
            void Add((int height, long size) part)
            {
                height = Math.Max(height, part.height);
                size += part.size;
                if (size > 1_000_000) throw new InvalidDataException("This SVG has too many parts.");
            }
            foreach (var child in e.Elements()) Add(Visit(child, depth + 1));
            foreach (var a in e.Attributes())
                if (a.Name.LocalName == "href" || a.Value.Contains("url("))
                    foreach (Match m in LocalReference().Matches(a.Value))
                        if (byId.TryGetValue(m.Groups[1].Value, out var target)) Add(Visit(target, depth + 1));
            active.Remove(e);
            return known[e] = (height + 1, size);
        }
    }

    [GeneratedRegex(@"#([^)'"" ]+)")]
    private static partial Regex LocalReference();

    static bool IsLocal(string reference)
    {
        reference = reference.Trim().Trim('"', '\'').Trim();
        return reference.Length == 0 || reference[0] == '#' || EmbeddedPicture().IsMatch(reference);
    }

    [GeneratedRegex(@"^data:image/(png|jpe?g|gif|webp|avif|bmp)[;,]", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedPicture();

    static string LocalCss(string css) => CssUrl().Replace(css, m => IsLocal(m.Groups[1].Value) ? m.Value : "none");

    [GeneratedRegex(@"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"@import[^;]*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    /// <summary>Pictures embedded in documents that Windows can't decode (EMF, WMF, SVG…).</summary>
    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var head = data.AsSpan(0, Math.Min(64, data.Length)).ToArray();
            var start = Encoding.UTF8.GetString(data, 0, Math.Min(4096, data.Length)).TrimStart((char)0xFEFF, ' ', '\t', '\r', '\n');
            bool svg = start.StartsWith('<') && start.Contains("<svg");
            if ((svg ? MagickFormat.Svg : Sniff(head)) is not { } format) return null;
            using var image = svg ? ReadVector(data) : new MagickImage(data, new MagickReadSettings { Format = format });
            return ToBitmap(image);
        }
        catch (Exception ex) when (ex is MagickException or XmlException or InvalidDataException or InvalidOperationException or InsufficientExecutionStackException)
        {
            Log.Write($"ImageMagick couldn't read an embedded picture: {ex.Message}");
            return null;
        }
    }

    static BitmapSource ToBitmap(MagickImage image)
    {
        int width = (int)image.Width, height = (int)image.Height;
        if ((long)width * height * 4 > Array.MaxLength) throw new ConversionException("This picture is too large to convert.");
        if (image.ColorSpace != ColorSpace.sRGB) image.ColorSpace = ColorSpace.sRGB;
        double dpiX = 96, dpiY = 96;
        if (image.Density is { X: > 1, Y: > 1 } density && density.Units != DensityUnit.Undefined)
        {
            density = density.ChangeUnits(DensityUnit.PixelsPerInch);
            (dpiX, dpiY) = (density.X, density.Y);
        }
        byte[] pixels;
        using (var data = image.GetPixelsUnsafe())
            pixels = data.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("The picture has no pixels.");
        var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Encode(BitmapSource bgra, string output, string ext, byte[]? profile = null)
    {
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[checked(width * 4 * height)];
        bgra.CopyPixels(pixels, width * 4, 0);
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        if (image.IsOpaque) image.HasAlpha = false;
        if (bgra.DpiX > 0) image.Density = new Density(bgra.DpiX, bgra.DpiY, DensityUnit.PixelsPerInch);
        if (profile != null) image.SetProfile(new ColorProfile(profile));
        switch (ext)
        {
            case "avif":
                image.Quality = 60; // visually close to JPEG 90 at about half the size
                image.Format = MagickFormat.Avif;
                break;
            case "jxl":
                image.Quality = 90;
                image.Format = MagickFormat.Jxl;
                break;
            case "tga":
                image.Format = MagickFormat.Tga;
                break;
        }
        using var file = File.Create(output);
        image.Write(file);
    }
}

/// <summary>Pictures to PDF: one page each, fitted onto A4/Letter with a margin.</summary>
internal static class ImagePdf
{
    public static void Write(List<BitmapSource> pages, string output, string title, CancellationToken ct)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = title;
        pdf.Info.Creator = "Convert Wheel";
        var paper = Documents.DDocument.DefaultPage;
        const double Margin = 36;

        foreach (var bitmap in pages)
        {
            ct.ThrowIfCancellationRequested();
            bool landscape = bitmap.PixelWidth > bitmap.PixelHeight;
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(landscape ? paper.Height : paper.Width);
            page.Height = XUnit.FromPoint(landscape ? paper.Width : paper.Height);
            double areaW = page.Width.Point - 2 * Margin, areaH = page.Height.Point - 2 * Margin;
            // Natural size at 96 dpi, shrunk to fit, never enlarged.
            double w = bitmap.PixelWidth * 0.75, h = bitmap.PixelHeight * 0.75;
            double scale = Math.Min(1, Math.Min(areaW / w, areaH / h));
            w *= scale;
            h *= scale;
            using var gfx = XGraphics.FromPdfPage(page);
            using var stream = Encode(bitmap);
            using var image = XImage.FromStream(stream);
            gfx.DrawImage(image, (page.Width.Point - w) / 2, (page.Height.Point - h) / 2, w, h);
            AddTextLayer(gfx, bitmap, (page.Width.Point - w) / 2, (page.Height.Point - h) / 2, w / bitmap.PixelWidth);
        }
        ct.ThrowIfCancellationRequested();
        pdf.Save(output);
    }

    /// <summary>
    /// Invisible text over the picture, placed word by word where OCR found it, so the PDF can be searched
    /// and its text selected and copied, like a scanner's "searchable PDF".
    /// </summary>
    static void AddTextLayer(XGraphics gfx, BitmapSource bitmap, double x, double y, double scale)
    {
        if (!Ocr.Available) return;
        List<Ocr.Line> lines;
        try { lines = Ocr.Recognize(bitmap); }
        catch (Exception ex) when (ex is ConversionException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"No text layer: {ex.Message}");
            return;
        }
        var clear = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        foreach (var line in lines)
        foreach (var word in line.Words)
        {
            if (word.Text.Length == 0 || word.Bounds.Width <= 0 || word.Bounds.Height <= 0) continue;
            // One size per line (from its tallest word), so copied text keeps an even size.
            double height = line.Words.Max(w => w.Bounds.Height) * scale, width = word.Bounds.Width * scale;
            var font = Documents.PdfRenderer.SystemFont("Arial", Math.Max(1, height));
            double natural = gfx.MeasureString(word.Text + " ", font).Width;
            if (natural <= 0) continue;
            var state = gfx.Save();
            gfx.TranslateTransform(x + word.Bounds.X * scale, y + word.Bounds.Bottom * scale);
            gfx.ScaleTransform(width / natural * (1 + 1.0 / Math.Max(1, word.Text.Length)), 1);
            gfx.DrawString(word.Text + " ", font, clear, 0, -height * 0.2, XStringFormats.BaseLineLeft);
            gfx.Restore(state);
        }
    }

    /// <summary>Photos as JPEG, anything with transparency as PNG.</summary>
    static MemoryStream Encode(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        bool alpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 255) { alpha = true; break; }
        BitmapEncoder encoder = alpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(alpha ? bgra : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
        var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return ms;
    }
}
