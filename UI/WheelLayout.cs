using System.Windows;
using ConvertWheel.Native;

namespace ConvertWheel.UI;

internal enum WheelRing { Inner, Outer }

/// <summary>The wheel's measurements and geometry: where it goes, and what a position on it means. Pure.</summary>
internal static class WheelLayout
{
    public const double HubR = 58, R1 = 68, R2 = 178, R3 = 190, R4 = 262;
    public const double InnerRim = R2 + 9, OuterRim = R4 + 10;

    /// <summary>Until the mouse leaves the middle nothing is chosen, so small wobbles don't convert anything.</summary>
    public const double DeadZone = 46;

    /// <summary>Clicks nearer than this miss every slice.</summary>
    public const double PassRadius = R1 - 4;

    /// <summary>How far the wheel reaches from its centre, with a little air.</summary>
    public static double Reach(bool expanded) => (expanded ? R4 : R2) + 16;

    /// <summary>How close a click must be to belong to the wheel.</summary>
    public static double HitRadius(bool expanded) => (expanded ? R4 : R2) + 10;

    /// <summary>
    /// The wheel's centre (physical pixels) for a wanted centre, kept fully on screen. <c>Fit</c> is below 1
    /// when the screen is too small for the whole wheel, which is then drawn smaller.
    /// </summary>
    public static (POINT Center, double Fit) Place(POINT wanted, RECT area, double scale, bool expanded)
    {
        int width = Math.Max(1, area.Right - area.Left), height = Math.Max(1, area.Bottom - area.Top);
        double full = Reach(expanded) * scale;
        double fit = Math.Min(1, Math.Min(width, height) / (2 * full));
        int reach = (int)Math.Floor(full * fit);
        return (new POINT(Clamp(wanted.X, area.Left, area.Right), Clamp(wanted.Y, area.Top, area.Bottom)), fit);

        int Clamp(int value, int lo, int hi) => hi - lo < reach * 2 ? lo + (hi - lo) / 2 : Math.Clamp(value, lo + reach, hi - reach);
    }

    /// <summary>Distance and clockwise angle from straight up, in wheel units.</summary>
    public static (double Distance, double Angle) Polar(POINT p, POINT center, double unit)
    {
        double dx = (p.X - center.X) / unit, dy = (p.Y - center.Y) / unit;
        return (Math.Sqrt(dx * dx + dy * dy), (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360);
    }

    /// <summary>What flicking in a direction means (while the trigger is held), or null in the middle.</summary>
    public static (WheelRing Ring, int Index)? Flick(double distance, double angle, int inner, int outer, bool expanded)
    {
        if (distance < DeadZone) return null;
        bool useOuter = expanded && outer > 0 && (distance > R2 + 6 || inner == 0);
        return useOuter ? (WheelRing.Outer, ByAngle(angle, outer)) : inner > 0 ? (WheelRing.Inner, ByAngle(angle, inner)) : null;
    }

    /// <summary>The slice exactly at a position, or null.</summary>
    public static (WheelRing Ring, int Index)? Hit(double distance, double angle, int inner, int outer, bool expanded)
    {
        if (inner > 0 && distance >= PassRadius && distance <= R2 + 4) return (WheelRing.Inner, ByAngle(angle, inner));
        if (expanded && outer > 0 && distance >= R3 - 4 && distance <= R4 + 6) return (WheelRing.Outer, ByAngle(angle, outer));
        return null;
    }

    /// <summary>The first slice is centred straight up, the rest follow clockwise.</summary>
    public static int ByAngle(double angle, int count)
    {
        double span = 360.0 / count;
        return (int)(((angle + span / 2) % 360 + 360) % 360 / span) % count;
    }

    /// <summary>The slice of another ring nearest to this one's direction.</summary>
    public static int Nearest(int index, int count, int otherCount) => ByAngle(index * 360.0 / count, otherCount);

    /// <summary>Does a box fit inside an annular wedge, keeping <paramref name="margin"/> clear of its sides?</summary>
    public static bool Fits(Point c, double r1, double r2, double a0, double a1, double margin, Rect box)
    {
        double nearX = Math.Clamp(c.X, box.Left, box.Right) - c.X, nearY = Math.Clamp(c.Y, box.Top, box.Bottom) - c.Y;
        if (Math.Sqrt(nearX * nearX + nearY * nearY) < r1) return false;
        foreach (var corner in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight })
        {
            double dx = corner.X - c.X, dy = corner.Y - c.Y, r = Math.Sqrt(dx * dx + dy * dy);
            if (r > r2) return false;
            if (a1 - a0 >= 180) continue;
            double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
            if (Side(r, angle - a0) < margin || Side(r, a1 - angle) < margin) return false;
        }
        return true;

        static double Side(double r, double degrees) => r * Math.Sin(degrees * Math.PI / 180);
    }
}
