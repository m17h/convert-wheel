using ConvertWheel.Native;

namespace ConvertWheel.Input;

/// <summary>Immutable snapshot of the wheel, read by the hook thread to decide what to hide from other apps.</summary>
/// <param name="PassRadius">Clicks this close to the centre aren't on a slice: they cancel the wheel and go through.</param>
internal sealed record HookState(bool WheelVisible, bool MaskArmed, POINT Center, int Radius, int PassRadius, int[] Trigger)
{
    public static readonly HookState Idle = new(false, false, default, 0, 0, []);
}

/// <summary>The (pure) rules for which input belongs to the wheel while it's open.</summary>
internal static class HookRules
{
    static bool IsWheelKey(int vk) =>
        vk is VirtualKeys.Escape or VirtualKeys.Enter or VirtualKeys.Tab or (>= VirtualKeys.ArrowLeft and <= VirtualKeys.ArrowDown)
            or (>= 0x31 and <= 0x39) or (>= 0x61 and <= 0x69);

    public static bool HideKey(HookState s, int vk, bool altDown)
    {
        if (!s.WheelVisible) return false;
        // Holding a trigger that types would otherwise keep typing while the wheel is up.
        if (Array.IndexOf(s.Trigger, VirtualKeys.Normalize(vk)) >= 0) return !VirtualKeys.IsModifier(vk);
        if (vk == VirtualKeys.Tab && altDown) return false; // Alt+Tab still switches windows
        return IsWheelKey(vk);
    }

    /// <summary>Is this point on the slices (as opposed to the middle or outside the wheel)?</summary>
    public static bool OnSlices(HookState s, POINT p)
    {
        if (!s.WheelVisible) return false;
        long dx = p.X - s.Center.X, dy = p.Y - s.Center.Y, distance = dx * dx + dy * dy;
        return distance <= (long)s.Radius * s.Radius && distance >= (long)s.PassRadius * s.PassRadius;
    }
}
