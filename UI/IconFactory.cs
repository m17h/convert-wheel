using System.Windows;
using System.Windows.Media;
using ConvertWheel.Conversion;

namespace ConvertWheel.UI;

/// <summary>Draws the app icon (a little colour wheel) so no binary assets are needed.</summary>
internal static class IconFactory
{
    static readonly string[] Colors = ["#22D3EE", "#3B82F6", "#A78BFA", "#EC4899", "#F97316", "#FBBF24"];
    static readonly string[] Greys = ["#9CA3AF", "#6B7280", "#9CA3AF", "#6B7280", "#9CA3AF", "#6B7280"];
    static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static byte[] BuildIco(bool paused = false) =>
        IcoWriter.Build(Sizes.Select(size => (size, IcoWriter.RenderPng(size, dc => Draw(dc, size, paused)))));

    public static System.Drawing.Icon TrayIcon(bool paused)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize;
        return new System.Drawing.Icon(new MemoryStream(BuildIco(paused)), size);
    }

    static void Draw(DrawingContext dc, double size, bool paused)
    {
        var center = new Point(size / 2, size / 2);
        double outer = size * 0.49, inner = size * 0.24, gap = Math.Max(1, size * 0.07);
        var palette = paused ? Greys : Colors;
        for (int i = 0; i < 6; i++)
        {
            double a0 = i * 60 - 30;
            dc.DrawGeometry(Palette.Solid(palette[i]), null, WheelGeometry.Sector(center, inner, outer, a0, a0 + 60, gap));
        }
        dc.DrawEllipse(Palette.Solid(paused ? "#D1D5DB" : "#F8FAFC"), null, center, size * 0.13, size * 0.13);
    }
}
