namespace ConvertWheel.Conversion.Media;

/// <summary>
/// Phones store video sideways plus a clockwise "rotate on playback" angle. Windows' video processor
/// applies it for some decoders (H.264) but not others; when it doesn't, these helpers turn the pixels.
/// </summary>
internal static class FrameRotation
{
    public static int Normalize(int? degrees) => ((degrees ?? 0) % 360 + 360) % 360 is var d && d is 90 or 180 or 270 ? d : 0;

    public static (int width, int height) Size(int width, int height, int degrees) =>
        degrees is 90 or 270 ? (height, width) : (width, height);

    /// <summary>Rotates a plane of <paramref name="bytesPerPixel"/>-byte pixels clockwise.</summary>
    static void Plane(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int bytesPerPixel, int degrees)
    {
        var (dw, _) = Size(width, height, degrees);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (nx, ny) = degrees switch
                {
                    90 => (height - 1 - y, x),
                    180 => (width - 1 - x, height - 1 - y),
                    270 => (y, width - 1 - x),
                    _ => (x, y),
                };
                src.Slice((y * width + x) * bytesPerPixel, bytesPerPixel).CopyTo(dst.Slice((ny * dw + nx) * bytesPerPixel, bytesPerPixel));
            }
    }

    public static byte[] Bgra(byte[] src, int width, int height, int degrees)
    {
        if (degrees == 0) return src;
        var dst = new byte[src.Length];
        Plane(src, dst, width, height, 4, degrees);
        return dst;
    }

    /// <summary>NV12: a full-size luma plane followed by a half-size plane of interleaved U/V pairs.</summary>
    public static byte[] Nv12(byte[] src, int width, int height, int degrees)
    {
        if (degrees == 0) return src;
        var dst = new byte[width * height * 3 / 2];
        int luma = width * height;
        Plane(src.AsSpan(0, luma), dst.AsSpan(0, luma), width, height, 1, degrees);
        Plane(src.AsSpan(luma, luma / 2), dst.AsSpan(luma), width / 2, height / 2, 2, degrees);
        return dst;
    }
}
