using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>
/// The decoded pictures of one conversion: each is decoded no larger than it's shown and shared by every
/// slide it's on, and the ones not used lately make room once they add up. One renderer, one thread.
/// </summary>
internal sealed class SlidePictures
{
    const long Budget = 192L * 1024 * 1024;
    const long MaxPixels = 1_000_000_000;

    sealed record Entry(object Key, BitmapSource? Source, long Bytes, int Width);

    readonly LinkedList<Entry> _recent = [];
    readonly Dictionary<object, LinkedListNode<Entry>> _entries = [];
    readonly Dictionary<DImage, (int width, int height)> _sizes = [];
    readonly Dictionary<object, int> _ids = [];
    // Pictures Windows can't decode by itself (EMF, SVG…), redrawn as PNG.
    readonly Dictionary<DImage, byte[]?> _redrawn = [];
    long _used;
    int _lastId;

    /// <summary>
    /// For PDF pages: pictures stay undecoded until the PDF takes their pixels, and are never let go,
    /// because the PDF recognises a repeated picture by its being the same object.
    /// </summary>
    public bool Pdf { get; set; }

    public int Id(object item)
    {
        if (!_ids.TryGetValue(item, out int id)) _ids[item] = id = ++_lastId;
        return id;
    }

    /// <summary>A picture with at least the pixels needed to show it this wide.</summary>
    public BitmapSource? Get(DImage image, double wantedPixels)
    {
        var (width, height) = SizeOf(image);
        if (width <= 0 || (long)width * height > MaxPixels) return null;
        // Steps of 256 pixels, so slightly different sizes share one copy.
        int target = double.IsFinite(wantedPixels) ? (int)Math.Min(width, Math.Ceiling(Math.Max(1, wantedPixels) / 256) * 256) : width;
        if (target > width * 0.8) target = width;
        if (_entries.TryGetValue(image, out var node) && node.Value.Width >= target) return Use(node).Source;
        var source = Decode(_redrawn.GetValueOrDefault(image) ?? image.Data, target < width ? target : 0);
        if (source == null && !_redrawn.ContainsKey(image) && Redraw(image) is { } png) source = Decode(png, target < width ? target : 0);
        return Keep(image, source, target).Source;
    }

    /// <summary>The part of a picture inside a crop; a crop's negative sides aren't part of the picture.</summary>
    public BitmapSource? Cropped(DImage image, Thickness crop, double wantedPixels)
    {
        if (Get(image, wantedPixels) is not { } source) return null;
        if (crop == default) return source;
        int w = source.PixelWidth, h = source.PixelHeight;
        int left = (int)Math.Round(Math.Clamp(crop.Left, 0, 1) * w), top = (int)Math.Round(Math.Clamp(crop.Top, 0, 1) * h);
        int right = (int)Math.Round(Math.Clamp(crop.Right, 0, 1) * w), bottom = (int)Math.Round(Math.Clamp(crop.Bottom, 0, 1) * h);
        if (left + right >= w || top + bottom >= h) return source;
        var key = $"crop {Id(source)} {left} {top} {right} {bottom}";
        return Shared(key, () =>
        {
            var cropped = new CroppedBitmap(source, new Int32Rect(left, top, w - left - right, h - top - bottom));
            if (cropped.CanFreeze) cropped.Freeze();
            return cropped;
        });
    }

    /// <summary>A drawn result (a picture cut to a shape, a gradient) shared by everything that looks the same.</summary>
    public BitmapSource? Shared(string key, Func<BitmapSource?> make)
    {
        if (_entries.TryGetValue(key, out var node)) return Use(node).Source;
        var made = make();
        if (Pdf && made is RenderTargetBitmap drawn) made = Decode(Png(drawn), 0);
        return Keep(key, made, 0).Source;
    }

    Entry Use(LinkedListNode<Entry> node)
    {
        _recent.Remove(node);
        _recent.AddFirst(node);
        return node.Value;
    }

    Entry Keep(object key, BitmapSource? source, int width)
    {
        if (_entries.Remove(key, out var old)) Drop(old);
        long bytes = source == null ? 0 : (long)source.PixelWidth * source.PixelHeight * 4;
        var entry = new Entry(key, source, bytes, width);
        _entries[key] = _recent.AddFirst(entry);
        _used += bytes;
        while (!Pdf && _used > Budget && _recent.Count > 1) Drop(_recent.Last!);
        return entry;
    }

    void Drop(LinkedListNode<Entry> node)
    {
        _recent.Remove(node);
        _entries.Remove(node.Value.Key);
        if (node.Value.Source is { } source) _ids.Remove(source);
        _used -= node.Value.Bytes;
    }

    (int width, int height) SizeOf(DImage image)
    {
        if (_sizes.TryGetValue(image, out var size)) return size;
        size = Measure(image.Data);
        if (size.width == 0)
        {
            // Some pictures only open once ImageMagick has redrawn them.
            if (Redraw(image) is { } png) size = Measure(png);
        }
        return _sizes[image] = size;
    }

    byte[]? Redraw(DImage image) => _redrawn[image] = MagickImages.Decode(image.Data) is { } bitmap ? Png(bitmap) : null;

    static (int, int) Measure(byte[] data)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(data, writable: false), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (Undecodable(ex)) { return (0, 0); }
    }

    BitmapSource? Decode(byte[] data, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = Pdf ? BitmapCacheOption.None : BitmapCacheOption.OnLoad;
            image.CreateOptions = Pdf ? BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation : BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) image.DecodePixelWidth = width;
            image.StreamSource = new MemoryStream(data, writable: false);
            image.EndInit();
            if (!Pdf) image.Freeze();
            return image;
        }
        catch (Exception ex) when (Undecodable(ex))
        {
            Log.Write($"Left out a picture that can't be decoded: {ex.Message}");
            return null;
        }
    }

    /// <summary>Windows reports damaged pictures in many ways, running out of memory among them.</summary>
    static bool Undecodable(Exception ex) => ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
        or OverflowException or IOException or OutOfMemoryException or System.Runtime.InteropServices.COMException;

    static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
