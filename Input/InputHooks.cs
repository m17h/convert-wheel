using System.Runtime.InteropServices;
using ConvertWheel.Native;

namespace ConvertWheel.Input;

internal enum HookEventKind { KeyDown, KeyUp, MouseDown, MouseWheel }

/// <param name="Code">Virtual key for keyboard events; 0=left, 1=right, 2=middle, 3=x-button for mouse.</param>
internal readonly record struct HookEvent(HookEventKind Kind, int Code, POINT Point);

/// <summary>
/// Global low-level keyboard and mouse hooks on a dedicated thread so a busy UI thread can never
/// stall system input. The filter runs on the hook thread and must be fast; it returns true to
/// swallow the event.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    /// <summary>dwExtraInfo stamped on input we inject ourselves, so we can ignore it.</summary>
    public const nint InjectedMarker = 0x43574845;

    readonly Func<HookEvent, bool> _filter;
    readonly HookProc _keyboardProc;
    readonly HookProc _mouseProc;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly bool[] _swallowedKeyUps = new bool[256];
    readonly bool[] _swallowedMouseUps = new bool[4];
    uint _threadId;
    nint _keyboardHook, _mouseHook;

    public InputHooks(Func<HookEvent, bool> filter)
    {
        _filter = filter;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Input hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        var module = Win32.GetModuleHandle(null);
        _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboardHook == 0 || _mouseHook == 0)
            Log.Write($"Failed to install input hooks (error {Marshal.GetLastWin32Error()}).");
        _ready.Set();

        while (Win32.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        Win32.UnhookWindowsHookEx(_keyboardHook);
        Win32.UnhookWindowsHookEx(_mouseHook);
    }

    nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != InjectedMarker && SafeKeyboard((int)wParam, (int)(data.vkCode & 0xFF)))
                return 1;
        }
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    bool SafeKeyboard(int msg, int vk)
    {
        try
        {
            if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                if (!_filter(new HookEvent(HookEventKind.KeyDown, vk, default))) return false;
                _swallowedKeyUps[vk] = true;
                return true;
            }
            if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
            {
                bool swallow = _filter(new HookEvent(HookEventKind.KeyUp, vk, default));
                if (_swallowedKeyUps[vk])
                {
                    _swallowedKeyUps[vk] = false;
                    return true;
                }
                return swallow;
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return false;
    }

    nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg != 0x200) // ignore WM_MOUSEMOVE as fast as possible
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != InjectedMarker && SafeMouse(msg, data.pt))
                    return 1;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    bool SafeMouse(int msg, POINT pt)
    {
        try
        {
            switch (msg)
            {
                case Win32.WM_LBUTTONDOWN: return Down(0, pt);
                case Win32.WM_RBUTTONDOWN: return Down(1, pt);
                case Win32.WM_MBUTTONDOWN: return Down(2, pt);
                case Win32.WM_XBUTTONDOWN: return Down(3, pt);
                case Win32.WM_LBUTTONUP: return Up(0);
                case Win32.WM_RBUTTONUP: return Up(1);
                case Win32.WM_MBUTTONUP: return Up(2);
                case Win32.WM_XBUTTONUP: return Up(3);
                case Win32.WM_MOUSEWHEEL:
                case Win32.WM_MOUSEHWHEEL:
                    return _filter(new HookEvent(HookEventKind.MouseWheel, 0, pt));
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return false;
    }

    bool Down(int button, POINT pt)
    {
        if (!_filter(new HookEvent(HookEventKind.MouseDown, button, pt))) return false;
        _swallowedMouseUps[button] = true;
        return true;
    }

    bool Up(int button)
    {
        if (!_swallowedMouseUps[button]) return false;
        _swallowedMouseUps[button] = false;
        return true;
    }

    /// <summary>
    /// Releases an Alt/Win key with an unassigned key tapped first, so Windows doesn't treat the
    /// release as "Alt pressed alone" (menu bar) or "Win pressed alone" (Start menu).
    /// </summary>
    public static void InjectMaskedRelease(int vk)
    {
        uint extended = vk is 0xA5 or 0xA3 or 0x5B or 0x5C ? Win32.KEYEVENTF_EXTENDEDKEY : 0;
        var inputs = new[]
        {
            Key(VirtualKeys.MaskKey, 0),
            Key(VirtualKeys.MaskKey, Win32.KEYEVENTF_KEYUP),
            Key(vk, Win32.KEYEVENTF_KEYUP | extended),
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(int vk, uint flags) => new()
        {
            type = Win32.INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = InjectedMarker } },
        };
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, 0, 0);
    }
}
