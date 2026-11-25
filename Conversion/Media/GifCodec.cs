namespace ConvertWheel.Conversion.Media;

/// <summary>A decoded, fully composited animation frame (BGRA, straight alpha).</summary>
internal sealed record AnimationFrame(byte[] Bgra, int Width, int Height, int DelayMs);

/// <summary>
/// Writes animated GIFs: one shared median-cut palette, ordered dithering (stable between frames, so
/// still areas don't shimmer), and delta frames for opaque animations.
/// </summary>
internal sealed class GifWriter : IDisposable
{
    const int Transparent = 255;
    static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    readonly Stream _out;
    readonly int _width, _height;
    readonly byte[] _palette; // 256 * RGB
    readonly byte[] _lookup;  // 15-bit RGB -> palette index
    readonly bool _hasTransparency;
    byte[]? _previous;
    bool _first = true;
    long _time, _written; // milliseconds asked for, hundredths written

    public GifWriter(string path, int width, int height, ColorHistogram histogram, bool hasTransparency = false)
    {
        _out = new BufferedStream(File.Create(path), 1 << 16);
        _width = width;
        _height = height;
        _hasTransparency = hasTransparency || histogram.HasTransparency;
        (_palette, _lookup) = histogram.BuildPalette(255);

        Write("GIF89a"u8);
        U16(width);
        U16(height);
        _out.WriteByte(0xF7); // global colour table, 8 bits, 256 entries
        _out.WriteByte(_hasTransparency ? (byte)Transparent : (byte)0);
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
                if (bgra[i + 3] < 128)
                {
                    indices[y * _width + x] = Transparent;
                    continue;
                }
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
        if (!_hasTransparency && !_first && _previous != null)
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

        // Transparent animations must clear the whole canvas before drawing the next full frame.
        // GIF counts in hundredths of a second: carry what rounding leaves over, so long clips don't drift.
        _time += delayMs;
        int delay = (int)Math.Clamp((long)Math.Round(_time / 10.0) - _written, 2, ushort.MaxValue);
        _written += delay;
        int control = _hasTransparency ? 0x09 : 0x04 | (_first ? 0 : 1);
        _out.Write([0x21, 0xF9, 0x04, (byte)control, (byte)delay, (byte)(delay >> 8), Transparent, 0x00]);
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

    public bool HasTransparency { get; private set; }

    public void Add(byte[] bgra, int step = 1)
    {
        for (int i = 0; i < bgra.Length; i += 4 * step)
        {
            if (bgra[i + 3] < 128) { HasTransparency = true; continue; }
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

/// <summary>An animated GIF or WebP whose frames are decoded one at a time, as often as needed.</summary>
internal abstract class Animation : IEnumerable<AnimationFrame>
{
    public int Width { get; protected init; }
    public int Height { get; protected init; }
    /// <summary>How long each frame shows, in milliseconds.</summary>
    public IReadOnlyList<int> Delays { get; protected init; } = [];

    public abstract IEnumerator<AnimationFrame> GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    protected void CheckSize()
    {
        if (Width <= 0 || Height <= 0 || (long)Width * Height > 250_000_000) throw new ConversionException("This animation is too large to convert.");
    }

    public static bool IsAnimated(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => true,
        ".webp" => MagickImages.IsAnimatedWebP(path),
        _ => false,
    };

    public static Animation Read(string path) =>
        Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase) ? new WebP.AnimatedReader(path) : new GifReader(path);

    public static void WriteGif(Animation frames, string output, CancellationToken ct)
    {
        var histogram = new ColorHistogram();
        int every = Math.Max(1, frames.Delays.Count / 30), index = 0;
        bool hasTransparency = false;
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            if (index++ % every == 0) histogram.Add(f.Bgra, step: 3);
            for (int i = 3; i < f.Bgra.Length && !hasTransparency; i += 4) hasTransparency = f.Bgra[i] < 128;
        }
        if (index == 0) throw new ConversionException("This animation has no frames.");
        using var gif = new GifWriter(output, frames.Width, frames.Height, histogram, hasTransparency);
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            gif.AddFrame(f.Bgra, f.DelayMs);
        }
    }
}

/// <summary>Reads animated GIFs, compositing each frame onto the canvas as it's decoded.</summary>
internal sealed class GifReader : Animation
{
    sealed record Frame(int Left, int Top, int Width, int Height, int Palette, int PaletteSize, bool Interlaced, int Transparent, int Disposal, int Data);

    readonly byte[] _file;
    readonly List<Frame> _frames = [];

    public GifReader(string path)
    {
        _file = File.ReadAllBytes(path);
        if (_file.Length < 13 || !_file.AsSpan(0, 4).SequenceEqual("GIF8"u8)) throw new InvalidOperationException("This isn't a GIF.");
        int width = U16(6), height = U16(8), pos = 13;
        int globalPalette = pos, globalSize = (_file[10] & 0x80) != 0 ? 2 << (_file[10] & 7) : 0;
        pos += globalSize * 3;

        var delays = new List<int>();
        int delay = 100, disposal = 0, transparent = -1;
        while (pos < _file.Length && _file[pos] != 0x3B)
        {
            if (_file[pos] == 0x21 && pos + 1 < _file.Length)
            {
                if (_file[pos + 1] == 0xF9 && pos + 7 < _file.Length)
                {
                    int flags = _file[pos + 3];
                    disposal = flags >> 2 & 7;
                    delay = U16(pos + 4) * 10;
                    if (delay < 20) delay = 100; // browsers treat 0-1 cs as 100 ms
                    transparent = (flags & 1) != 0 ? _file[pos + 6] : -1;
                }
                pos = SkipBlocks(pos + 2);
            }
            else if (_file[pos] == 0x2C && pos + 10 < _file.Length)
            {
                int flags = _file[pos + 9], localSize = (flags & 0x80) != 0 ? 2 << (flags & 7) : 0;
                int data = pos + 10 + localSize * 3;
                int end = SkipBlocks(data + 1);
                var frame = new Frame(U16(pos + 1), U16(pos + 3), U16(pos + 5), U16(pos + 7), localSize > 0 ? pos + 10 : globalPalette,
                    localSize > 0 ? localSize : globalSize, (flags & 0x40) != 0, transparent, disposal, data);
                // The last frame can be cut off; everything before it is still good, but say so rather than lose it quietly.
                if (end < 0) throw new InvalidOperationException($"This GIF is cut short after {_frames.Count} complete frames.");
                _frames.Add(frame);
                delays.Add(delay);
                (delay, disposal, transparent) = (100, 0, -1);
                pos = end;
            }
            else pos++; // stray bytes between blocks
            if (pos < 0) break;
        }
        if (_frames.Count > 0 && (width == 0 || height == 0)) (width, height) = (_frames[0].Left + _frames[0].Width, _frames[0].Top + _frames[0].Height);
        Width = width;
        Height = height;
        Delays = delays;
    }

    int U16(int at) => _file[at] | _file[at + 1] << 8;

    /// <summary>The position after a run of data blocks, or -1 if the file ends first.</summary>
    int SkipBlocks(int pos)
    {
        while (pos < _file.Length)
        {
            int length = _file[pos++];
            if (length == 0) return pos;
            pos += length;
        }
        return -1;
    }

    public override IEnumerator<AnimationFrame> GetEnumerator()
    {
        CheckSize();
        var canvas = new byte[Width * Height * 4];
        for (int i = 0; i < _frames.Count; i++)
        {
            var f = _frames[i];
            var before = f.Disposal == 3 ? (byte[])canvas.Clone() : null;
            Draw(f, canvas);
            yield return new AnimationFrame((byte[])canvas.Clone(), Width, Height, Delays[i]);
            if (f.Disposal == 2)
            {
                int width = Math.Min(f.Width, Width - f.Left);
                if (width > 0)
                    for (int y = f.Top; y < Math.Min(Height, f.Top + f.Height); y++)
                        Array.Clear(canvas, (y * Width + f.Left) * 4, width * 4);
            }
            else if (before != null) canvas = before;
        }
    }

    void Draw(Frame f, byte[] canvas)
    {
        var indices = Lzw(f);
        int at = 0;
        // Interlaced frames store every 8th row, then the rows in between.
        foreach (var (start, step) in f.Interlaced ? [(0, 8), (4, 8), (2, 4), (1, 2)] : new[] { (0, 1) })
            for (int y = start; y < f.Height; y += step)
                for (int x = 0; x < f.Width; x++, at++)
                {
                    if (at >= indices.Length) return;
                    int index = indices[at], cx = f.Left + x, cy = f.Top + y;
                    if (index == f.Transparent || index >= f.PaletteSize || cx >= Width || cy >= Height) continue;
                    int color = f.Palette + index * 3, d = (cy * Width + cx) * 4;
                    canvas[d] = _file[color + 2];
                    canvas[d + 1] = _file[color + 1];
                    canvas[d + 2] = _file[color];
                    canvas[d + 3] = 255;
                }
    }

    /// <summary>The frame's colour indices. Damaged data gives as many as could be read.</summary>
    ReadOnlySpan<byte> Lzw(Frame f)
    {
        int minSize = _file[f.Data];
        if (minSize is < 1 or > 11) return [];
        int clear = 1 << minSize, end = clear + 1, next = end + 1, size = minSize + 1;
        if ((long)f.Width * f.Height > 250_000_000) throw new ConversionException("This animation is too large to convert.");
        var output = new byte[f.Width * f.Height];
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var stack = new byte[4097];
        for (int i = 0; i < clear && i < 256; i++) suffix[i] = (byte)i;
        int written = 0, bits = 0, bitCount = 0, previous = -1, pos = f.Data + 1, blockLeft = 0;
        byte first = 0;
        while (written < output.Length)
        {
            while (bitCount < size)
            {
                if (blockLeft == 0)
                {
                    blockLeft = _file[pos++];
                    if (blockLeft == 0) return output.AsSpan(0, written);
                }
                bits |= _file[pos++] << bitCount;
                bitCount += 8;
                blockLeft--;
            }
            int code = bits & (1 << size) - 1;
            bits >>= size;
            bitCount -= size;
            if (code == end) break;
            if (code == clear)
            {
                next = end + 1;
                size = minSize + 1;
                previous = -1;
                continue;
            }
            if (code > next || code == next && previous < 0 || previous < 0 && code >= clear) break;
            int depth = 0, walk = code;
            if (code == next)
            {
                stack[depth++] = first;
                walk = previous;
            }
            while (walk >= clear && depth < 4096)
            {
                stack[depth++] = suffix[walk];
                walk = prefix[walk];
            }
            first = suffix[walk];
            stack[depth++] = first;
            while (depth > 0 && written < output.Length) output[written++] = stack[--depth];
            if (previous >= 0 && next < 4096)
            {
                prefix[next] = (short)previous;
                suffix[next] = first;
                next++;
                if (next == 1 << size && size < 12) size++;
            }
            previous = code;
        }
        return output.AsSpan(0, written);
    }
}
