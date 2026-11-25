using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ConvertWheel.Conversion;

/// <summary>WebP through Skia (bundled; Windows can read WebP but can't write it), including animations.</summary>
internal static class WebP
{
    const int MaxSide = 16383;

    public static byte[] Encode(BitmapSource bgra, int quality, byte[]? profile = null)
    {
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        if (w > MaxSide || h > MaxSide) throw new ConversionException($"WebP pictures can be at most {MaxSide} pixels wide or tall, and this one is {w} × {h}.");
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var still = Encode(pixels, w, h, quality);
        return profile == null ? still : WithProfile(still, w, h, profile);
    }

    static byte[] Encode(byte[] bgra, int width, int height, int quality)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            using var image = SKImage.FromPixels(pixmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, quality)
                ?? throw new ConversionException("Couldn't encode WebP.");
            return data.ToArray();
        }
        finally { handle.Free(); }
    }

    /// <summary>The picture chunks of a still WebP (ALPH, VP8, VP8L), without the container around them.</summary>
    static (byte[] chunks, bool alpha) Bitstream(byte[] still)
    {
        var chunks = new MemoryStream();
        bool alpha = false;
        for (int pos = 12; pos + 8 <= still.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(still, pos, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(still.AsSpan(pos + 4));
            int total = 8 + chunkSize + (chunkSize & 1);
            if (id is "ALPH" or "VP8 " or "VP8L") chunks.Write(still, pos, Math.Min(total, still.Length - pos));
            if (id is "ALPH" or "VP8L") alpha = true;
            pos += total;
        }
        return (chunks.ToArray(), alpha);
    }

    static byte[] WithProfile(byte[] still, int width, int height, byte[] profile)
    {
        var (chunks, alpha) = Bitstream(still);
        using var ms = new MemoryStream();
        ms.Write("RIFF\0\0\0\0WEBP"u8);
        WriteChunk(ms, "VP8X", Extended(0x20 | (alpha ? 0x10 : 0), width, height));
        WriteChunk(ms, "ICCP", profile);
        ms.Write(chunks);
        var file = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        return file;
    }

    static byte[] Extended(int flags, int width, int height)
    {
        var vp8x = new byte[10];
        vp8x[0] = (byte)flags;
        Put24(vp8x, 4, width - 1);
        Put24(vp8x, 7, height - 1);
        return vp8x;
    }

    public static BitmapSource? DecodeWithSkia(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
            if (info.BytesSize64 > Array.MaxLength) return null;
            var pixels = new byte[info.Width * info.Height * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            }
            finally { handle.Free(); }
            var source = ImageConverter.Rotate(BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Bgra32, null, pixels, info.Width * 4), (int)codec.EncodedOrigin);
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Skia couldn't decode {path}: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ animations

    /// <summary>The frames of an animated WebP, decoded one at a time onto the full canvas.</summary>
    public sealed class AnimatedReader : Media.Animation
    {
        readonly string _path;
        readonly SKCodecFrameInfo[] _frames;

        public AnimatedReader(string path)
        {
            _path = path;
            using var codec = Open();
            // FFmpeg can't read these either, so there's no point in passing a broken one on.
            if (IsCutShort(path)) throw new ConversionException($"This animation is cut short: only {codec.FrameCount} whole frames are left of it.");
            Width = codec.Info.Width;
            Height = codec.Info.Height;
            _frames = codec.FrameInfo;
            // Very short delays mean "as fast as possible", like GIF.
            Delays = _frames.Select(f => f.Duration < 20 ? 100 : f.Duration).ToList();
        }

        static bool IsCutShort(string path)
        {
            using var f = File.OpenRead(path);
            var head = new byte[8];
            return f.ReadAtLeast(head, 8, false) == 8 && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8L > f.Length + 1;
        }

        SKCodec Open() => SKCodec.Create(_path) ?? throw new ConversionException("Couldn't read this animation.");

        public override IEnumerator<Media.AnimationFrame> GetEnumerator()
        {
            using var codec = Open();
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
            CheckSize();
            var canvas = new byte[Width * Height * 4];
            for (int i = 0; i < _frames.Length; i++)
            {
                // The canvas still holds the previous frame, which Skia then draws onto (after disposing of it).
                int required = _frames[i].RequiredFrame;
                var options = required == i - 1 && required >= 0 ? new SKCodecOptions(i, required) : new SKCodecOptions(i);
                var handle = GCHandle.Alloc(canvas, GCHandleType.Pinned);
                SKCodecResult result;
                try { result = codec.GetPixels(info, handle.AddrOfPinnedObject(), options); }
                finally { handle.Free(); }
                if (result != SKCodecResult.Success) throw new InvalidOperationException($"This animation is cut short or damaged: only {i} of {_frames.Length} frames could be read.");
                yield return new Media.AnimationFrame((byte[])canvas.Clone(), Width, Height, Delays[i]);
            }
        }
    }

    public static void WriteAnimated(Media.Animation frames, string output, CancellationToken ct)
    {
        using var writer = new AnimatedWriter(output, frames.Width, frames.Height, 85);
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            writer.AddFrame(frame.Bgra, frame.DelayMs);
        }
        writer.Finish();
    }

    /// <summary>
    /// Builds an animated WebP: each frame is encoded by Skia as a still WebP, then its bitstream is
    /// placed in an ANMF chunk of an extended (VP8X + ANIM) container, written as it arrives.
    /// </summary>
    internal sealed class AnimatedWriter : IDisposable
    {
        readonly FileStream _file;
        readonly int _width, _height, _quality;
        bool _alpha;

        public AnimatedWriter(string output, int width, int height, int quality)
        {
            if (width > MaxSide || height > MaxSide) throw new ConversionException($"WebP pictures can be at most {MaxSide} pixels wide or tall.");
            (_width, _height, _quality) = (width, height, quality);
            _file = File.Create(output);
            _file.Write("RIFF\0\0\0\0WEBP"u8);
            WriteChunk(_file, "VP8X", Extended(0x02, width, height));
            WriteChunk(_file, "ANIM", new byte[6]); // transparent background, loop forever
        }

        public void AddFrame(byte[] bgra, int delayMs)
        {
            var (bitstream, alpha) = Bitstream(Encode(bgra, _width, _height, _quality));
            _alpha |= alpha;
            var frameHeader = new byte[16];
            Put24(frameHeader, 6, _width - 1);
            Put24(frameHeader, 9, _height - 1);
            Put24(frameHeader, 12, Math.Min(delayMs, 0xFFFFFF));
            frameHeader[15] = 0x02; // don't blend with the previous frame
            WriteChunk(_file, "ANMF", [.. frameHeader, .. bitstream]);
            if (_file.Length > uint.MaxValue - 10) throw new ConversionException("This animation is too long for a WebP file.");
        }

        /// <summary>Fills in the size and transparency, which are only known now.</summary>
        public void Finish()
        {
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(_file.Length - 8));
            _file.Position = 4;
            _file.Write(size);
            _file.Position = 20;
            _file.WriteByte((byte)(0x02 | (_alpha ? 0x10 : 0))); // animation (+ alpha)
        }

        public void Dispose() => _file.Dispose();
    }

    static void WriteChunk(Stream s, string id, byte[] data)
    {
        s.Write(System.Text.Encoding.ASCII.GetBytes(id));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        s.Write(size);
        s.Write(data);
        if ((data.Length & 1) != 0) s.WriteByte(0);
    }

    static void Put24(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
    }
}
