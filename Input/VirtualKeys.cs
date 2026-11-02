using System.Windows.Input;
using ConvertWheel.Native;

namespace ConvertWheel.Input;

internal static class VirtualKeys
{
    public const int Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Escape = 0x1B, Win = 0x5B, MaskKey = 0xE8;

    /// <summary>Collapses left/right variants so "Ctrl" means either Ctrl key.</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => Shift,
        0xA2 or 0xA3 => Ctrl,
        0xA4 or 0xA5 => Alt,
        0x5C => Win,
        _ => vk,
    };

    public static bool IsPhysicallyDown(int vk) => vk == Win
        ? Down(0x5B) || Down(0x5C)
        : Down(vk);

    public static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>The other side of a left/right modifier (left Ctrl → right Ctrl), or null.</summary>
    public static int? OtherSide(int vk) => vk switch
    {
        0xA0 => 0xA1, 0xA1 => 0xA0,
        0xA2 => 0xA3, 0xA3 => 0xA2,
        0xA4 => 0xA5, 0xA5 => 0xA4,
        0x5B => 0x5C, 0x5C => 0x5B,
        _ => null,
    };

    public static bool IsAltOrWin(int vk) => Normalize(vk) is Alt or Win;

    static int Order(int vk) => vk switch { Ctrl => 0, Alt => 1, Shift => 2, Win => 3, _ => 10 };

    public static string Name(int vk) => vk switch
    {
        Ctrl => "Ctrl",
        Shift => "Shift",
        Alt => "Alt",
        Win => "Win",
        0x14 => "Caps Lock",
        0x20 => "Space",
        0x13 => "Pause",
        0x91 => "Scroll Lock",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x5D => "Menu",
        0xC0 => "`",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };

    public static string Describe(IEnumerable<int> keys) =>
        string.Join(" + ", keys.OrderBy(Order).ThenBy(k => k).Select(Name));
}
