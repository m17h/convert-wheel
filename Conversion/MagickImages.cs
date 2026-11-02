using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ConvertWheel.Conversion;

/// <summary>ImageMagick (bundled through Magick.NET) for the long tail of image formats.</summary>
internal static class MagickImages
{
    public static BitmapSource? Decode(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using var image = ext is ".svg" or ".svgz" ? ReadVector(path) : new MagickImage(path);
            image.AutoOrient();
            if (ext is ".psd" or ".psb") image.ColorSpace = ColorSpace.sRGB;
            return ToBitmap(image);
        }
        catch (Exception ex) when (ex is MagickException or IOException or InvalidOperationException)
        {
            Log.Write($"ImageMagick couldn't read {path}: {ex.Message}");
            return null;
        }
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

    /// <summary>Every frame of an animated WebP, composited onto the full canvas.</summary>
    public static List<Media.AnimationFrame> ReadAnimatedWebP(string path)
    {
        using var images = new MagickImageCollection(path);
        images.Coalesce();
        var frames = new List<Media.AnimationFrame>();
        foreach (var image in images)
        {
            int width = (int)image.Width, height = (int)image.Height;
            var bgra = image.GetPixelsUnsafe().ToByteArray(PixelMapping.BGRA) ?? throw new ConversionException("Couldn't read this animation.");
            // Magick keeps WebP frame durations in 1/100 s ticks; very short delays mean "as fast as possible", like GIF.
            int delay = (int)(image.AnimationDelay * 1000 / Math.Max(1u, image.AnimationTicksPerSecond));
            frames.Add(new Media.AnimationFrame(bgra, width, height, delay < 20 ? 100 : delay));
        }
        return frames;
    }

    /// <summary>Vector art is rasterised big enough (≥1024 px) to stay sharp as icons or photos.</summary>
    static MagickImage ReadVector(string path)
    {
        var image = new MagickImage(path, new MagickReadSettings { BackgroundColor = MagickColors.Transparent });
        uint longest = Math.Max(image.Width, image.Height);
        if (longest is > 0 and < 1024)
        {
            double density = Math.Min(600, 96.0 * 1024 / longest);
            image.Dispose();
            image = new MagickImage(path, new MagickReadSettings { BackgroundColor = MagickColors.Transparent, Density = new Density(density) });
        }
        return image;
    }

    /// <summary>Pictures embedded in documents that Windows can't decode (EMF, WMF, TGA…).</summary>
    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            using var image = new MagickImage(data);
            return ToBitmap(image);
        }
        catch (MagickException ex)
        {
            Log.Write($"ImageMagick couldn't read an embedded picture: {ex.Message}");
            return null;
        }
    }

    static BitmapSource ToBitmap(MagickImage image)
    {
        var png = image.ToByteArray(MagickFormat.Png32);
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Encode(BitmapSource bgra, string output, string ext)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bgra));
        using var ms = new MemoryStream();
        png.Save(ms);
        using var image = new MagickImage(ms.ToArray());
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
        image.Write(output);
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
