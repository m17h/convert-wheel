namespace ConvertWheel.Input;

internal enum TriggerVerdict { Good, Caution, Rejected }

/// <summary>Which keys make a sensible "hold" shortcut, and what to tell the user about the rest. Pure.</summary>
internal static class TriggerPolicy
{
    public const int MaxKeys = 3;

    /// <summary>Below this, a short pause in Ctrl+click or Shift+click can be taken for a hold.</summary>
    public const int SafeDelayMs = 400;

    public static (TriggerVerdict Verdict, string Message) Check(IReadOnlyCollection<int> keys, int holdDelayMs)
    {
        if (keys.Count == 0) return (TriggerVerdict.Rejected, "Press at least one key.");
        if (keys.Count > MaxKeys) return (TriggerVerdict.Rejected, $"That's a lot of keys. Use up to {MaxKeys}.");

        foreach (int key in keys)
            if (WhyNot(key) is { } reason)
                return (TriggerVerdict.Rejected, $"{VirtualKeys.Name(key)} {reason} Ctrl, Alt or Shift work best.");

        if (keys.Any(k => !VirtualKeys.IsModifier(k)))
        {
            if (keys.Contains(VirtualKeys.CapsLock))
                return (TriggerVerdict.Caution, "Caps Lock will also switch capital letters on and off each time you hold it.");
            if (keys.Any(k => k is >= 0x70 and <= 0x7B))
                return (TriggerVerdict.Caution, "Function keys do things in File Explorer too (F2 renames, F5 refreshes). Ctrl, Alt or Shift work best.");
            return (TriggerVerdict.Caution, "This key keeps its normal job too. Ctrl, Alt or Shift work best.");
        }
        if (keys.Count == 1 && holdDelayMs < SafeDelayMs && keys.First() is VirtualKeys.Ctrl or VirtualKeys.Shift)
            return (TriggerVerdict.Caution,
                $"{VirtualKeys.Name(keys.First())} is also used to select files. A hold time of 500 ms or more keeps the wheel out of the way.");
        if (keys.Count == 1 && keys.First() == VirtualKeys.Shift)
            return (TriggerVerdict.Caution, "Pressing Shift five times quickly opens Windows' Sticky Keys.");
        return (TriggerVerdict.Good, "");
    }

    static string? WhyNot(int vk) => vk switch
    {
        VirtualKeys.Escape => "closes the wheel, so it can't open it.",
        VirtualKeys.Win => "belongs to Windows and opens the Start menu.",
        VirtualKeys.Enter => "would open the file instead.",
        VirtualKeys.Tab => "moves around in windows, and picks in the wheel.",
        VirtualKeys.Space => "types and selects.",
        0x08 or 0x2E => "can delete or go back while you hold it.",
        0x21 or 0x22 or 0x23 or 0x24 or (>= VirtualKeys.ArrowLeft and <= VirtualKeys.ArrowDown) => "moves the selection.",
        >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69 => "types a number, and picks in the wheel.",
        >= 0x41 and <= 0x5A => "types a letter.",
        >= 0x6A and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2 => "types a character.",
        0x5D => "opens a menu.",
        0x2C => "takes a screenshot.",
        0x90 => "changes what the number pad does.",
        >= 0xA6 and <= 0xB7 => "is already used by Windows.",
        _ => null,
    };
}
