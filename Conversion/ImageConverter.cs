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
    /// <returns>false if no bundled decoder can read it, so the caller can fall back to FFmpeg.</returns>
    public static bool TryConvert(string input, string output, string ext)
    {
        // Animated GIFs stay animated as WebP.
        if (ext == "webp" && Path.GetExtension(input).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            var frames = Media.GifReader.Read(input);
            if (frames.Count > 1)
            {
                WebP.WriteAnimated(frames, output);
                return true;
            }
        }

        // Animated WebPs stay animated as GIF.
        if (ext == "gif" && MagickImages.IsAnimatedWebP(input))
        {
            Media.Animation.WriteGif(MagickImages.ReadAnimatedWebP(input), output);
            return true;
        }

        if (ext == "pdf")
        {
            var pages = DecodeAllPages(input);
            if (pages.Count == 0) return false;
            ImagePdf.Write(pages, output, Path.GetFileNameWithoutExtension(input));
            return true;
        }

        var image = Decode(input);
        if (image == null) return false;

        switch (ext)
        {
            case "avif" or "jxl" or "tga": MagickImages.Encode(image, output, ext); break;
            case "webp": File.WriteAllBytes(output, WebP.Encode(image, quality: 90)); break;
            case "png": Save(new PngBitmapEncoder(), image, output); break;
            case "gif": Save(new GifBitmapEncoder(), image, output); break;
            case "tiff": Save(new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }, image, output); break;
            case "jpg": Save(new JpegBitmapEncoder { QualityLevel = 92 }, Flatten(image), output); break;
            case "bmp": Save(new BmpBitmapEncoder(), Flatten(image), output); break;
            case "ico": File.WriteAllBytes(output, IcoWriter.FromImage(image)); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Formats Windows' decoders misread or only partly support; these go straight to ImageMagick.</summary>
    static readonly HashSet<string> MagickFirst = [".svg", ".svgz", ".psd", ".psb", ".xcf", ".emf", ".wmf", ".exr", ".hdr", ".dds", ".tga", ".pcx", ".qoi", ".jxl"];

    /// <summary>Decodes with Windows imaging (honouring EXIF orientation), then Skia, then ImageMagick.</summary>
    internal static BitmapSource? Decode(string input)
    {
        if (MagickFirst.Contains(Path.GetExtension(input).ToLowerInvariant()) && MagickImages.Decode(input) is { } early) return early;
        try
        {
            using var stream = File.OpenRead(input);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var image = new FormatConvertedBitmap(Orient(decoder.Frames[0]), PixelFormats.Bgra32, null, 0);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException or InvalidOperationException)
        {
            return WebP.DecodeWithSkia(input) ?? MagickImages.Decode(input);
        }
    }

    /// <summary>Every page of a multi-page TIFF; a single picture otherwise.</summary>
    static List<BitmapSource> DecodeAllPages(string input)
    {
        if (Path.GetExtension(input).ToLowerInvariant() is ".tif" or ".tiff")
        {
            try
            {
                using var stream = File.OpenRead(input);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 1)
                    return decoder.Frames.Select(f => (BitmapSource)new FormatConvertedBitmap(Orient(f), PixelFormats.Bgra32, null, 0)).ToList();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException) { }
        }
        return Decode(input) is { } single ? [single] : [];
    }

    static void Save(BitmapEncoder encoder, BitmapSource image, string output)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(output);
        encoder.Save(stream);
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

        var (rotate, mirror) = orientation switch
        {
            2 => (0, true), 3 => (180, false), 4 => (180, true), 5 => (90, true),
            6 => (90, false), 7 => (270, true), 8 => (270, false), _ => (0, false),
        };
        BitmapSource result = frame;
        if (rotate != 0) result = new TransformedBitmap(result, new RotateTransform(rotate));
        if (mirror) result = new TransformedBitmap(result, new ScaleTransform(-1, 1));
        return result;
    }

    /// <summary>Composites transparency onto white for formats without alpha.</summary>
    static BitmapSource Flatten(BitmapSource bgra)
    {
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
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
        var result = BitmapSource.Create(width, height, bgra.DpiX, bgra.DpiY, PixelFormats.Bgr32, null, px, stride);
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
