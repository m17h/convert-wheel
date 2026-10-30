using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConvertWheel.Conversion.Media;

/// <summary>A decoded, fully composited animation frame (BGRA, straight alpha).</summary>
internal sealed record AnimationFrame(byte[] Bgra, int Width, int Height, int DelayMs);

/// <summary>
/// Writes animated GIFs: one shared median-cut palette, ordered dithering (stable between frames, so
/// still areas don't shimmer), and only the pixels that change are stored after the first frame.
/// </summary>
internal sealed class GifWriter : IDisposable
{
    const int Transparent = 255;
    static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    readonly Stream _out;
    readonly int _width, _height;
    readonly byte[] _palette; // 256 * RGB
    readonly byte[] _lookup;  // 15-bit RGB -> palette index
    byte[]? _previous;
    bool _first = true;

    public GifWriter(string path, int width, int height, ColorHistogram histogram)
    {
        _out = new BufferedStream(File.Create(path), 1 << 16);
        _width = width;
        _height = height;
        (_palette, _lookup) = histogram.BuildPalette(255);

        Write("GIF89a"u8);
        U16(width);
        U16(height);
        _out.WriteByte(0xF7); // global colour table, 8 bits, 256 entries
        _out.WriteByte(0);
        _out.WriteByte(0);
        _out.Write(_palette);
        // Loop forever (NETSCAPE2.0 application extension).
        _out.Write([0x21, 0xFF, 0x0B]);
        Write("NETSCAPE2.0"u8);
        _out.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    public void AddFrame(byte[] bgra, int delayMs)
    {
        var indices = new byte[_width * _height];
        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
            {
                int i = (y * _width + x) * 4;
                int b0 = bgra[i], g0 = bgra[i + 1], r0 = bgra[i + 2];
                int direct = _lookup[(r0 >> 3) << 10 | (g0 >> 3) << 5 | (b0 >> 3)];
                int dr = r0 - _palette[direct * 3], dg = g0 - _palette[direct * 3 + 1], db = b0 - _palette[direct * 3 + 2];
                if (dr * dr + dg * dg + db * db <= 48)
                {
                    indices[y * _width + x] = (byte)direct; // close enough: flat areas stay clean
                    continue;
                }
                // Gradients: ordered dithering, which stays put between frames instead of crawling.
                int t = Bayer[(y & 3) * 4 + (x & 3)] - 8; // -8..7
                int b = Math.Clamp(b0 + t, 0, 255), g = Math.Clamp(g0 + t, 0, 255), r = Math.Clamp(r0 + t, 0, 255);
                indices[y * _width + x] = _lookup[(r >> 3) << 10 | (g >> 3) << 5 | (b >> 3)];
            }

        int left = 0, top = 0, right = _width - 1, bottom = _height - 1;
        var frame = indices;
        if (!_first && _previous != null)
        {
            // Store only the changed rectangle; unchanged pixels inside it become transparent.
            left = _width; top = _height; right = -1; bottom = -1;
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                    if (indices[y * _width + x] != _previous[y * _width + x])
                    {
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
            if (right < 0) { left = top = right = bottom = 0; }
            frame = new byte[(right - left + 1) * (bottom - top + 1)];
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                {
                    byte v = indices[y * _width + x];
                    frame[(y - top) * (right - left + 1) + x - left] = v == _previous[y * _width + x] ? (byte)Transparent : v;
                }
        }

        // Graphic control extension: keep previous frame, delay, transparency.
        int delay = Math.Max(2, (int)Math.Round(delayMs / 10.0));
        _out.Write([0x21, 0xF9, 0x04, (byte)(0x04 | (_first ? 0 : 1)), (byte)delay, (byte)(delay >> 8), Transparent, 0x00]);
        _out.WriteByte(0x2C);
        U16(left); U16(top); U16(right - left + 1); U16(bottom - top + 1);
        _out.WriteByte(0);
        Lzw.Encode(frame, 8, _out);

        _previous = indices;
        _first = false;
    }

    void Write(ReadOnlySpan<byte> bytes) => _out.Write(bytes);
    void U16(int v) { _out.WriteByte((byte)v); _out.WriteByte((byte)(v >> 8)); }

    public void Dispose()
    {
        _out.WriteByte(0x3B);
        _out.Dispose();
    }

    /// <summary>GIF-flavoured LZW with variable code sizes, packed into 255-byte sub-blocks.</summary>
    static class Lzw
    {
        public static void Encode(byte[] data, int minCodeSize, Stream output)
        {
            output.WriteByte((byte)minCodeSize);
            int clear = 1 << minCodeSize, end = clear + 1;
            var children = new short[4096 * 256]; // [prefix code * 256 + byte] -> code (0 = none)
            var used = new List<int>(4096);
            int codeSize = minCodeSize + 1, next = end + 1;
            var block = new byte[256];
            int blockLen = 0, bitBuffer = 0, bitCount = 0;

            void Emit(int code)
            {
                bitBuffer |= code << bitCount;
                bitCount += codeSize;
                while (bitCount >= 8)
                {
                    block[++blockLen] = (byte)bitBuffer;
                    bitBuffer >>= 8;
                    bitCount -= 8;
                    if (blockLen == 255) FlushBlock();
                }
            }
            void FlushBlock()
            {
                if (blockLen == 0) return;
                block[0] = (byte)blockLen;
                output.Write(block, 0, blockLen + 1);
                blockLen = 0;
            }
            void Reset()
            {
                foreach (var slot in used) children[slot] = 0;
                used.Clear();
                codeSize = minCodeSize + 1;
                next = end + 1;
            }

            Emit(clear);
            if (data.Length == 0)
            {
                Emit(end);
            }
            else
            {
                int prefix = data[0];
                for (int i = 1; i < data.Length; i++)
                {
                    int slot = prefix * 256 + data[i];
                    int child = children[slot];
                    if (child != 0)
                    {
                        prefix = child;
                        continue;
                    }
                    Emit(prefix);
                    if (next < 4096)
                    {
                        children[slot] = (short)next;
                        used.Add(slot);
                        if (next == 1 << codeSize && codeSize < 12) codeSize++;
                        next++;
                    }
                    else
                    {
                        Emit(clear);
                        Reset();
                    }
                    prefix = data[i];
                }
                Emit(prefix);
                // The decoder adds one more table entry after reading the last code, which can widen codes.
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
                Emit(end);
            }
            if (bitCount > 0) block[++blockLen] = (byte)bitBuffer;
            FlushBlock();
            output.WriteByte(0);
        }
    }
}

/// <summary>Collects colours across frames and builds a median-cut palette from them.</summary>
internal sealed class ColorHistogram
{
    readonly int[] _counts = new int[32768];
    readonly long[] _sumR = new long[32768], _sumG = new long[32768], _sumB = new long[32768];

    public void Add(byte[] bgra, int step = 1)
    {
        for (int i = 0; i < bgra.Length; i += 4 * step)
        {
            int bin = (bgra[i + 2] >> 3) << 10 | (bgra[i + 1] >> 3) << 5 | (bgra[i] >> 3);
            _counts[bin]++;
            _sumR[bin] += bgra[i + 2]; _sumG[bin] += bgra[i + 1]; _sumB[bin] += bgra[i];
        }
    }

    public (byte[] palette, byte[] lookup) BuildPalette(int colors)
    {
        var boxes = new List<List<int>> { Enumerable.Range(0, 32768).Where(c => _counts[c] > 0).ToList() };
        if (boxes[0].Count == 0) boxes[0].Add(0);
        while (boxes.Count < colors)
        {
            // Split the box with the most pixels × widest channel range.
            int best = -1;
            long bestScore = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                var (range, _) = WidestChannel(boxes[b]);
                long score = (long)range * boxes[b].Sum(c => (long)_counts[c]);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            if (best < 0) break;
            var box = boxes[best];
            var (_, shift) = WidestChannel(box);
            box.Sort((a, c) => ((a >> shift) & 31).CompareTo((c >> shift) & 31));
            long total = box.Sum(c => (long)_counts[c]), half = 0;
            int split = 1;
            for (; split < box.Count - 1; split++)
            {
                half += _counts[box[split - 1]];
                if (half >= total / 2) break;
            }
            boxes[best] = box.GetRange(0, split);
            boxes.Add(box.GetRange(split, box.Count - split));
        }

        var palette = new byte[768];
        var rgb = new (int r, int g, int b)[boxes.Count];
        for (int b = 0; b < boxes.Count; b++)
        {
            long r = 0, g = 0, bl = 0, n = 0;
            foreach (var c in boxes[b])
            {
                // Average the exact colours that fell in each bin, not the bin centres, so pure colours stay pure.
                if (_counts[c] == 0) { r += ((c >> 10) & 31) * 255 / 31; g += ((c >> 5) & 31) * 255 / 31; bl += (c & 31) * 255 / 31; n++; continue; }
                r += _sumR[c]; g += _sumG[c]; bl += _sumB[c]; n += _counts[c];
            }
            rgb[b] = ((int)(r / n), (int)(g / n), (int)(bl / n));
            palette[b * 3] = (byte)rgb[b].r;
            palette[b * 3 + 1] = (byte)rgb[b].g;
            palette[b * 3 + 2] = (byte)rgb[b].b;
        }

        var lookup = new byte[32768];
        Parallel.For(0, 32768, c =>
        {
            int r = ((c >> 10) & 31) * 255 / 31, g = ((c >> 5) & 31) * 255 / 31, b = (c & 31) * 255 / 31;
            int bestIndex = 0, bestDistance = int.MaxValue;
            for (int p = 0; p < rgb.Length; p++)
            {
                int dr = r - rgb[p].r, dg = g - rgb[p].g, db = b - rgb[p].b;
                int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db; // weighted for perception
                if (d < bestDistance) { bestDistance = d; bestIndex = p; }
            }
            lookup[c] = (byte)bestIndex;
        });
        return (palette, lookup);
    }

    (int range, int shift) WidestChannel(List<int> box)
    {
        int best = -1, bestShift = 0;
        foreach (var shift in new[] { 10, 5, 0 })
        {
            int lo = 31, hi = 0;
            foreach (var c in box) { int v = (c >> shift) & 31; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            if (hi - lo > best) { best = hi - lo; bestShift = shift; }
        }
        return (best, bestShift);
    }
}

/// <summary>Animated GIF and WebP as plain frame lists.</summary>
internal static class Animation
{
    public static bool IsAnimated(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => true,
        ".webp" => MagickImages.IsAnimatedWebP(path),
        _ => false,
    };

    public static List<AnimationFrame> Read(string path) =>
        Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase) ? MagickImages.ReadAnimatedWebP(path) : GifReader.Read(path);

    public static void WriteGif(List<AnimationFrame> frames, string output)
    {
        var histogram = new ColorHistogram();
        foreach (var f in frames.Where((_, i) => i % Math.Max(1, frames.Count / 30) == 0)) histogram.Add(f.Bgra, step: 3);
        using var gif = new GifWriter(output, frames[0].Width, frames[0].Height, histogram);
        foreach (var f in frames) gif.AddFrame(f.Bgra, f.DelayMs);
    }
}

/// <summary>Reads animated GIFs through Windows imaging and composites each frame onto the canvas.</summary>
internal static class GifReader
{
    public static List<AnimationFrame> Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int width = Query<ushort>(decoder.Metadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        int height = Query<ushort>(decoder.Metadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;

        var canvas = new byte[width * height * 4];
        var frames = new List<AnimationFrame>();
        foreach (var frame in decoder.Frames)
        {
            var meta = frame.Metadata as BitmapMetadata;
            int left = Query<ushort>(meta, "/imgdesc/Left") ?? 0, top = Query<ushort>(meta, "/imgdesc/Top") ?? 0;
            int delay = (Query<ushort>(meta, "/grctlext/Delay") ?? 10) * 10;
            if (delay < 20) delay = 100; // browsers treat 0-1 cs as 100 ms
            int disposal = Query<byte>(meta, "/grctlext/Disposal") ?? 0;

            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
            var pixels = new byte[fw * fh * 4];
            bgra.CopyPixels(pixels, fw * 4, 0);
            var before = disposal == 3 ? (byte[])canvas.Clone() : null;

            for (int y = 0; y < fh && top + y < height; y++)
                for (int x = 0; x < fw && left + x < width; x++)
                {
                    int s = (y * fw + x) * 4, d = ((top + y) * width + left + x) * 4;
                    if (pixels[s + 3] == 0) continue;
                    Buffer.BlockCopy(pixels, s, canvas, d, 4);
                }
            frames.Add(new AnimationFrame((byte[])canvas.Clone(), width, height, delay));

            if (disposal == 2)
                for (int y = top; y < Math.Min(height, top + fh); y++)
                    Array.Clear(canvas, (y * width + left) * 4, Math.Min(fw, width - left) * 4);
            else if (before != null) canvas = before;
        }
        return frames;
    }

    static T? Query<T>(BitmapMetadata? meta, string query) where T : struct
    {
        try { return meta?.ContainsQuery(query) == true && meta.GetQuery(query) is T value ? value : null; }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
    }
}
