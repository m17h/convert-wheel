using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertWheel.Conversion;

/// <summary>
/// Image conversion through Windows' built-in codecs, with bundled Skia (WebP) and ImageMagick (HEIC,
/// AVIF, JPEG XL, PSD, camera RAW, SVG, TGA and dozens more) for everything else. Must run on an STA thread.
/// </summary>
internal static class ImageConverter
{
    /// <param name="Image">8-bit BGRA, upright.</param>
    /// <param name="Deep">The same picture with its 16 bits per channel, when it has them.</param>
    /// <param name="Profile">The colour profile its values are in; null means sRGB.</param>
    sealed record Picture(BitmapSource Image, BitmapSource? Deep = null, ColorContext? Profile = null, bool Opaque = false);

    /// <returns>false if no bundled decoder can read it, so the caller can fall back to FFmpeg.</returns>
    public static bool TryConvert(string input, string output, string ext, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Animated GIFs stay animated as WebP.
        if (ext == "webp" && Path.GetExtension(input).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var frames = Media.Animation.Read(input);
            if (frames.Delays.Count > 1)
            {
                WebP.WriteAnimated(frames, output, ct);
                return true;
            }
        }

        // Animated WebPs stay animated as GIF.
        if (ext == "gif" && MagickImages.IsAnimatedWebP(input))
        {
            Media.Animation.WriteGif(Media.Animation.Read(input), output, ct);
            return true;
        }

        // Text in the picture (screenshots, photos of pages, scans), read with Windows' OCR.
        if (ext is "txt" or "docx")
        {
            var pictures = DecodeAllPages(input);
            if (pictures.Count == 0) return false;
            var doc = Documents.PdfSource.FromPictures(pictures, Path.GetFileNameWithoutExtension(input), ct);
            Documents.BuiltInDocuments.Write(doc, ext, output);
            return true;
        }

        if (ext == "pdf")
        {
            var pages = DecodeAllPages(input);
            if (pages.Count == 0) return false;
            ImagePdf.Write(pages, output, Path.GetFileNameWithoutExtension(input), ct);
            return true;
        }

        var picture = Decode(input);
        if (picture == null) return false;
        ct.ThrowIfCancellationRequested();

        // Formats without colour profiles get sRGB values, so the colours stay the same.
        if (ext is "gif" or "bmp" or "ico" or "tga") picture = new Picture(ToSrgb(picture), Opaque: picture.Opaque);
        var image = picture.Image;
        switch (ext)
        {
            case "avif" or "jxl" or "tga": MagickImages.Encode(image, output, ext, ProfileBytes(picture.Profile)); break;
            case "webp": File.WriteAllBytes(output, WebP.Encode(image, quality: 90, ProfileBytes(picture.Profile))); break;
            case "png": Save(new PngBitmapEncoder(), picture.Deep ?? image, output, picture.Profile); break;
            case "gif": Save(new GifBitmapEncoder(), image, output); break;
            case "tiff": Save(new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }, picture.Deep ?? image, output, picture.Profile); break;
            case "jpg": Save(new JpegBitmapEncoder { QualityLevel = 92 }, Flatten(picture), output, picture.Profile); break;
            case "bmp": Save(new BmpBitmapEncoder(), Flatten(picture), output); break;
            case "ico": File.WriteAllBytes(output, IcoWriter.FromImage(image)); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Formats Windows' decoders misread or only partly support; these go straight to ImageMagick.</summary>
    static readonly HashSet<string> MagickFirst = [".svg", ".svgz", ".psd", ".psb", ".xcf", ".emf", ".wmf", ".exr", ".hdr", ".dds", ".tga", ".pcx", ".qoi", ".jxl"];

    /// <summary>Decodes with Windows imaging (honouring EXIF orientation), then Skia, then ImageMagick.</summary>
    static Picture? Decode(string input)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (MagickFirst.Contains(ext) && DecodeWithMagick(input) is { } early) return early;
        try
        {
            using var stream = File.OpenRead(input);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            // Icons hold several sizes of the same picture.
            var frame = ext is ".ico" or ".cur"
                ? decoder.Frames.OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight).ThenByDescending(f => f.Format.BitsPerPixel).First()
                : decoder.Frames[0];
            return FromFrame(frame);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return WebP.DecodeWithSkia(input) is { } skia ? new Picture(skia) : DecodeWithMagick(input);
        }
    }

    static Picture? DecodeWithMagick(string input) =>
        MagickImages.Decode(input, out var profile) is { } image ? new Picture(image, null, profile == null ? null : ContextFrom(profile)) : null;

    static Picture FromFrame(BitmapFrame frame)
    {
        var format = frame.Format;
        bool opaque = format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb24 || format == PixelFormats.Gray8 || format == PixelFormats.Cmyk32;
        bool gray = format == PixelFormats.Gray8 || format == PixelFormats.Gray16 || format == PixelFormats.Gray4 || format == PixelFormats.Gray2 || format == PixelFormats.BlackWhite;
        var upright = Orient(frame);
        var context = Context(frame);
        var space = context == null ? "" : ProfileSpace(context);

        BitmapSource? image = null;
        if (format == PixelFormats.Cmyk32 && space == "CMYK")
        {
            try
            {
                // Print colours go through their profile to the screen's colours.
                image = new FormatConvertedBitmap(new ColorConvertedBitmap(upright, context, new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgr24), PixelFormats.Bgra32, null, 0);
                image.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException) { image = null; }
        }
        if (image == null)
        {
            image = new FormatConvertedBitmap(upright, PixelFormats.Bgra32, null, 0);
            image.Freeze();
        }

        BitmapSource? deep = null;
        PixelFormat? deepFormat = format == PixelFormats.Gray16 ? PixelFormats.Gray16 : format == PixelFormats.Rgb48 ? PixelFormats.Rgb48
            : format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 ? PixelFormats.Rgba64 : null;
        if (deepFormat is { } df)
        {
            deep = df == format ? upright : new FormatConvertedBitmap(upright, df, null, 0);
            deep.Freeze();
        }
        return new Picture(image, deep, space == "RGB " && !gray ? context : null, opaque);
    }

    /// <summary>Every page of a multi-page TIFF; a single picture otherwise. In sRGB.</summary>
    static List<BitmapSource> DecodeAllPages(string input)
    {
        if (Path.GetExtension(input).ToLowerInvariant() is ".tif" or ".tiff")
        {
            try
            {
                using var stream = File.OpenRead(input);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 1)
                    return decoder.Frames.Select(f => ToSrgb(FromFrame(f))).ToList();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException) { }
        }
        return Decode(input) is { } single ? [ToSrgb(single)] : [];
    }

    static void Save(BitmapEncoder encoder, BitmapSource image, string output, ColorContext? profile = null)
    {
        encoder.Frames.Add(profile == null ? BitmapFrame.Create(image) : BitmapFrame.Create(image, null, null, new([profile])));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    // ------------------------------------------------------------------ colour profiles

    static ColorContext? Context(BitmapFrame frame)
    {
        try { return frame.ColorContexts is { Count: > 0 } contexts ? contexts[0] : null; }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or InvalidOperationException or ArgumentException) { return null; }
    }

    static byte[]? ProfileBytes(ColorContext? context)
    {
        if (context == null) return null;
        try
        {
            using var stream = context.OpenProfileStream();
            if (stream == null) return null;
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.Length >= 128 ? ms.ToArray() : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>The kind of values a profile describes: "RGB ", "CMYK" or "GRAY".</summary>
    static string ProfileSpace(ColorContext context) => ProfileBytes(context) is { } p ? System.Text.Encoding.ASCII.GetString(p, 16, 4) : "";

    /// <summary>WPF only gets colour contexts from picture files, so this puts the profile in a tiny TIFF.</summary>
    static ColorContext? ContextFrom(byte[] profile)
    {
        try
        {
            var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            void Tag(int tag, int type, int count, int value) { w.Write((short)tag); w.Write((short)type); w.Write(count); w.Write(value); }
            const int Tags = 10, Data = 8 + 2 + Tags * 12 + 4;
            w.Write("II*\0"u8);
            w.Write(8);
            w.Write((short)Tags);
            Tag(256, 4, 1, 1); // 1 × 1 pixel
            Tag(257, 4, 1, 1);
            Tag(258, 3, 3, Data); // 8 bits for each of 3 channels
            Tag(259, 3, 1, 1);
            Tag(262, 3, 1, 2); // RGB
            Tag(273, 4, 1, Data + 6);
            Tag(277, 3, 1, 3);
            Tag(278, 4, 1, 1);
            Tag(279, 4, 1, 3);
            Tag(34675, 7, profile.Length, Data + 10); // the ICC profile
            w.Write(0);
            for (int i = 0; i < 3; i++) w.Write((short)8);
            w.Write(0);
            w.Write(profile);
            ms.Position = 0;
            var context = Context(BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            return context != null && ProfileBytes(context) != null ? context : null;
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or COMException or InvalidOperationException or ArgumentException)
        {
            Log.Write($"Couldn't use a colour profile: {ex.Message}");
            return null;
        }
    }

    static BitmapSource ToSrgb(Picture picture)
    {
        if (picture.Profile == null) return picture.Image;
        try
        {
            var converted = new ColorConvertedBitmap(picture.Image, picture.Profile, new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            converted.Freeze();
            return converted;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException)
        {
            Log.Write($"Couldn't apply a colour profile: {ex.Message}");
            return picture.Image;
        }
    }

    /// <summary>Applies the EXIF orientation so phone photos don't come out sideways.</summary>
    static BitmapSource Orient(BitmapFrame frame)
    {
        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("System.Photo.Orientation") &&
                meta.GetQuery("System.Photo.Orientation") is ushort value)
                orientation = value;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or COMException) { }
        return Rotate(frame, orientation);
    }

    /// <param name="orientation">EXIF orientation, 1-8.</param>
    internal static BitmapSource Rotate(BitmapSource source, int orientation)
    {
        var (rotate, mirror) = orientation switch
        {
            2 => (0, true), 3 => (180, false), 4 => (180, true), 5 => (90, true),
            6 => (90, false), 7 => (270, true), 8 => (270, false), _ => (0, false),
        };
        var result = source;
        if (rotate != 0) result = new TransformedBitmap(result, new RotateTransform(rotate));
        if (mirror) result = new TransformedBitmap(result, new ScaleTransform(-1, 1));
        return result;
    }

    /// <summary>Composites transparency onto white for formats without alpha.</summary>
    static BitmapSource Flatten(Picture picture)
    {
        var bgra = picture.Image;
        BitmapSource result;
        if (picture.Opaque)
        {
            result = new FormatConvertedBitmap(bgra, PixelFormats.Bgr32, null, 0);
            result.Freeze();
            return result;
        }
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        if ((long)stride * height > Array.MaxLength) throw new ConversionException("This picture is too large to convert.");
        var px = new byte[stride * height];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int alpha = px[i + 3];
            if (alpha == 255) continue;
            int white = 255 * (255 - alpha);
            px[i] = (byte)((px[i] * alpha + white) / 255);
            px[i + 1] = (byte)((px[i + 1] * alpha + white) / 255);
            px[i + 2] = (byte)((px[i + 2] * alpha + white) / 255);
            px[i + 3] = 255;
        }
        result = BitmapSource.Create(width, height, bgra.DpiX, bgra.DpiY, PixelFormats.Bgr32, null, px, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>Builds .ico files from PNG-compressed frames.</summary>
internal static class IcoWriter
{
    static readonly int[] Sizes = [256, 128, 64, 48, 32, 24, 16];

    public static byte[] FromImage(BitmapSource source)
    {
        int largest = Math.Max(source.PixelWidth, source.PixelHeight);
        var sizes = Sizes.Where(s => s <= Math.Max(largest, 16)).ToList();
        return Build(sizes.Select(size => (size, RenderPng(size, dc =>
        {
            double scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
            double w = source.PixelWidth * scale, h = source.PixelHeight * scale;
            dc.DrawImage(source, new Rect((size - w) / 2, (size - h) / 2, w, h));
        }))));
    }

    public static byte[] RenderPng(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Build(IEnumerable<(int size, byte[] png)> frames)
    {
        var list = frames.ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)list.Count);
        int offset = 6 + 16 * list.Count;
        foreach (var (size, png) in list)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in list) w.Write(png);
        w.Flush();
        return ms.ToArray();
    }
}
