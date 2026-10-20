using System.Windows;
using System.Windows.Media;
using ConvertWheel.Conversion;

namespace ConvertWheel.UI;

internal static class Palette
{
    public static readonly FontFamily UiFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly (Color, Color) More = (Hex("#9CA3AF"), Hex("#4B5563"));
    public static readonly (Color, Color) Success = (Hex("#34D399"), Hex("#059669"));
    public static readonly (Color, Color) Error = (Hex("#F87171"), Hex("#DC2626"));
    public static readonly (Color, Color) Neutral = (Hex("#818CF8"), Hex("#6366F1"));

    public static (Color, Color) Accent(FormatKind kind) => kind switch
    {
        FormatKind.Audio => (Hex("#22D3EE"), Hex("#3B82F6")),
        FormatKind.Video => (Hex("#C084FC"), Hex("#EC4899")),
        FormatKind.Image => (Hex("#FBBF24"), Hex("#F97316")),
        FormatKind.Document => (Hex("#60A5FA"), Hex("#6366F1")),
        FormatKind.Spreadsheet => (Hex("#4ADE80"), Hex("#059669")),
        FormatKind.Presentation => (Hex("#FB923C"), Hex("#E11D48")),
        _ => Neutral,
    };

    public static string KindName(FormatKind kind) => kind switch
    {
        FormatKind.Audio => "Audio",
        FormatKind.Video => "Video",
        FormatKind.Image => "Image",
        FormatKind.Document => "Document",
        FormatKind.Spreadsheet => "Spreadsheet",
        FormatKind.Presentation => "Slides",
        _ => "File",
    };

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush Solid(string hex) => Freeze(new SolidColorBrush(Hex(hex)));

    public static LinearGradientBrush Gradient((Color a, Color b) colors) =>
        Freeze(new LinearGradientBrush(colors.a, colors.b, new Point(0, 0), new Point(1, 1)));

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

internal static class WheelGeometry
{
    /// <summary>Point at a radius and angle, where 0° is straight up and angles run clockwise.</summary>
    public static Point Polar(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Sin(rad), center.Y - radius * Math.Cos(rad));
    }

    static double Deg(double radians) => radians * 180 / Math.PI;

    /// <summary>An annular wedge with a constant-width gap between neighbours.</summary>
    public static Geometry Sector(Point c, double r1, double r2, double a0, double a1, double gap)
    {
        double p1 = Deg(gap / 2 / r1), p2 = Deg(gap / 2 / r2);
        double s2 = a0 + p2, e2 = a1 - p2, s1 = a0 + p1, e1 = a1 - p1;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(c, r2, s2), true, true);
            ctx.ArcTo(Polar(c, r2, e2), new Size(r2, r2), 0, e2 - s2 > 180, SweepDirection.Clockwise, true, true);
            ctx.LineTo(Polar(c, r1, e1), true, true);
            ctx.ArcTo(Polar(c, r1, s1), new Size(r1, r1), 0, e1 - s1 > 180, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    public static Geometry Arc(Point c, double r, double a0, double a1, double gap)
    {
        double pad = Deg(gap / 2 / r);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(c, r, a0 + pad), false, false);
            ctx.ArcTo(Polar(c, r, a1 - pad), new Size(r, r), 0, a1 - a0 - 2 * pad > 180, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
