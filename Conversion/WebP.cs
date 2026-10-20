using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace ConvertWheel.Conversion;

/// <summary>WebP through Skia (bundled; Windows can read WebP but can't write it), including animations.</summary>
internal static class WebP
{
    public static byte[] Encode(BitmapSource bgra, int quality)
    {
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        return Encode(pixels, w, h, quality);
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

    public static BitmapSource? DecodeWithSkia(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.Width * info.Height * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            }
            finally { handle.Free(); }
            var source = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Bgra32, null, pixels, info.Width * 4);
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Skia couldn't decode {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds an animated WebP: each frame is encoded by Skia as a still WebP, then its bitstream is
    /// placed in an ANMF chunk of an extended (VP8X + ANIM) container.
    /// </summary>
    public static void WriteAnimated(List<Media.AnimationFrame> frames, string output)
    {
        int width = frames[0].Width, height = frames[0].Height;
        using var body = new MemoryStream();
        bool alpha = false;

        foreach (var frame in frames)
        {
            var still = Encode(frame.Bgra, width, height, 85);
            var bitstream = new MemoryStream();
            // Copy the ALPH/VP8/VP8L chunks; skip the RIFF header and any VP8X.
            for (int pos = 12; pos + 8 <= still.Length;)
            {
                var id = System.Text.Encoding.ASCII.GetString(still, pos, 4);
                int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(still.AsSpan(pos + 4));
                int total = 8 + chunkSize + (chunkSize & 1);
                if (id is "ALPH" or "VP8 " or "VP8L") bitstream.Write(still, pos, Math.Min(total, still.Length - pos));
                if (id is "ALPH" or "VP8L") alpha = true;
                pos += total;
            }
            var frameHeader = new byte[16];
            Put24(frameHeader, 6, width - 1);
            Put24(frameHeader, 9, height - 1);
            Put24(frameHeader, 12, Math.Min(frame.DelayMs, 0xFFFFFF));
            frameHeader[15] = 0x02; // don't blend with the previous frame
            WriteChunk(body, "ANMF", [.. frameHeader, .. bitstream.ToArray()]);
        }

        using var file = File.Create(output);
        var vp8x = new byte[10];
        vp8x[0] = (byte)(0x02 | (alpha ? 0x10 : 0)); // animation (+ alpha)
        Put24(vp8x, 4, width - 1);
        Put24(vp8x, 7, height - 1);
        var anim = new byte[6]; // transparent background, loop forever

        using var chunks = new MemoryStream();
        WriteChunk(chunks, "VP8X", vp8x);
        WriteChunk(chunks, "ANIM", anim);
        body.Position = 0;
        body.CopyTo(chunks);

        file.Write("RIFF"u8);
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, (int)chunks.Length + 4);
        file.Write(size);
        file.Write("WEBP"u8);
        chunks.Position = 0;
        chunks.CopyTo(file);
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
