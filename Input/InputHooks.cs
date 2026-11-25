using System.Runtime.InteropServices;
using ConvertWheel.Native;

namespace ConvertWheel.Input;

internal enum HookEventKind { KeyDown, KeyUp, MouseDown, MouseWheel }

/// <param name="Code">Virtual key for keyboard events; 0=left, 1=right, 2=middle, 3 and 4=x-buttons for mouse.</param>
/// <param name="Hidden">Whether the event was kept from other apps.</param>
internal readonly record struct HookEvent(HookEventKind Kind, int Code, POINT Point, bool Hidden);

/// <summary>
/// Global low-level keyboard and mouse hooks on a dedicated thread so a busy UI thread can never
/// stall system input. The callbacks only look at a snapshot and post the event on; nothing in them waits.
/// The mouse hook exists only while the wheel needs it. Windows drops a hook without telling anyone (a slow
/// callback, sleep, a locked session), so <see cref="Refresh"/> puts fresh ones in place.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    /// <summary>dwExtraInfo stamped on input we inject ourselves, so we can ignore it.</summary>
    public const nint InjectedMarker = 0x43574845;

    const uint WM_SYNC = Win32.WM_APP + 1;

    readonly Action<HookEvent> _post;
    readonly HookProc _keyboardProc; // fields, so the delegates outlive the native hooks
    readonly HookProc _mouseProc;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly SwallowTracker _keys = new(256);
    readonly SwallowTracker _buttons = new(5);
    volatile uint _threadId;
    volatile bool _stopping, _enabled, _mouseWanted, _refresh, _forget;
    volatile HookState _state = HookState.Idle;
    nint _keyboardHook, _mouseHook;

    /// <param name="post">Called on the hook thread for every event of interest; must return at once.</param>
    public InputHooks(Action<HookEvent> post, bool enabled = true)
    {
        _post = post;
        _enabled = enabled;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Input hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>What the hooks should hide from now on.</summary>
    public HookState State { set => _state = value; }

    /// <summary>No hooks at all while the app is paused.</summary>
    public bool Enabled
    {
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value) Refresh(forget: true);
            else Sync();
        }
    }

    /// <summary>The mouse is only watched while a trigger is held or the wheel is open.</summary>
    public bool WatchMouse
    {
        set
        {
            if (_mouseWanted == value) return;
            _mouseWanted = value;
            Sync();
        }
    }

    /// <summary>(Keyboard, mouse) hooks currently installed, for diagnostics.</summary>
    public (bool Keyboard, bool Mouse) Installed => (Volatile.Read(ref _keyboardHook) != 0, Volatile.Read(ref _mouseHook) != 0);

    /// <summary>Puts fresh hooks in place of ones Windows may have dropped.</summary>
    /// <param name="forget">Also forget pending presses (their releases went to the lock screen, say).</param>
    public void Refresh(bool forget = false)
    {
        _refresh = true;
        if (forget) _forget = true;
        Sync();
    }

    void Sync()
    {
        if (_threadId != 0 && !_stopping) Win32.PostThreadMessage(_threadId, WM_SYNC, 0, 0);
    }

    void Run()
    {
        try
        {
            Win32.PeekMessage(out _, 0, 0, 0, 0); // create the queue before anyone posts to it
            _threadId = Win32.GetCurrentThreadId();
            Apply();
            _ready.Set();
            while (!_stopping && Win32.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd == 0 && msg.message == WM_SYNC)
                {
                    Apply();
                    continue;
                }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            _threadId = 0;
            _ready.Set();
            Remove(ref _keyboardHook);
            Remove(ref _mouseHook);
        }
    }

    /// <summary>Brings the installed hooks in line with what's wanted (hook thread).</summary>
    void Apply()
    {
        if (_forget)
        {
            _forget = false;
            _keys.Reset();
            _buttons.Reset();
        }
        bool refresh = _refresh;
        _refresh = false;

        Arrange(ref _keyboardHook, _enabled, refresh, Win32.WH_KEYBOARD_LL, _keyboardProc);
        // A hidden button press must get its release hidden too, even if the wheel has closed since.
        Arrange(ref _mouseHook, _enabled && (_mouseWanted || _buttons.Pending), refresh, Win32.WH_MOUSE_LL, _mouseProc);
    }

    static void Arrange(ref nint hook, bool wanted, bool refresh, int id, HookProc proc)
    {
        if (!wanted)
        {
            Remove(ref hook);
            return;
        }
        if (hook != 0 && !refresh) return;
        var fresh = Win32.SetWindowsHookEx(id, proc, Win32.GetModuleHandle(null), 0);
        if (fresh == 0)
        {
            Log.Write($"Failed to install an input hook (type {id}, error {Marshal.GetLastWin32Error()}).");
            return; // keep the old one, if any
        }
        Remove(ref hook);
        Volatile.Write(ref hook, fresh);
    }

    static void Remove(ref nint hook)
    {
        if (hook != 0) Win32.UnhookWindowsHookEx(hook);
        Volatile.Write(ref hook, 0);
    }

    nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.dwExtraInfo != InjectedMarker && OnKey((int)wParam, (int)(data.vkCode & 0xFF)))
                return 1;
        }
        return Win32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    bool OnKey(int msg, int vk)
    {
        try
        {
            var s = _state;
            if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                bool wanted = HookRules.HideKey(s, vk, s.WheelVisible && VirtualKeys.Down(VirtualKeys.Alt));
                bool hide = _keys.Down(vk, wanted, VirtualKeys.Down(vk), Environment.TickCount64, out bool repeat);
                if (!repeat) _post(new HookEvent(HookEventKind.KeyDown, vk, default, hide));
                return hide;
            }
            if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
            {
                bool hide = _keys.Up(vk);
                // Replace an Alt/Win release with a masked one. If that isn't allowed here (an elevated
                // window has the focus), let the real release through: a menu is better than a stuck key.
                if (!hide && s.MaskArmed && VirtualKeys.IsAltOrWin(vk)) hide = InjectMaskedRelease(vk);
                _post(new HookEvent(HookEventKind.KeyUp, vk, default, hide));
                return hide;
            }
        }
        catch (Exception ex)
        {
            LogLater(ex);
        }
        return false;
    }

    nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg != Win32.WM_MOUSEMOVE) // ignore movement as fast as possible
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != InjectedMarker && OnMouse(msg, data.pt, (int)(data.mouseData >> 16)))
                    return 1;
            }
        }
        return Win32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    bool OnMouse(int msg, POINT pt, int xButton)
    {
        try
        {
            switch (msg)
            {
                case Win32.WM_LBUTTONDOWN: return Down(0, pt);
                case Win32.WM_RBUTTONDOWN: return Down(1, pt);
                case Win32.WM_MBUTTONDOWN: return Down(2, pt);
                case Win32.WM_XBUTTONDOWN: return Down(xButton == 2 ? 4 : 3, pt);
                case Win32.WM_LBUTTONUP: return Up(0);
                case Win32.WM_RBUTTONUP: return Up(1);
                case Win32.WM_MBUTTONUP: return Up(2);
                case Win32.WM_XBUTTONUP: return Up(xButton == 2 ? 4 : 3);
                case Win32.WM_MOUSEWHEEL:
                case Win32.WM_MOUSEHWHEEL:
                    if (!_mouseWanted) return false;
                    bool hide = HookRules.OnSlices(_state, pt);
                    _post(new HookEvent(HookEventKind.MouseWheel, 0, pt, hide));
                    return hide;
            }
        }
        catch (Exception ex)
        {
            LogLater(ex);
        }
        return false;
    }

    bool Down(int button, POINT pt)
    {
        if (!_mouseWanted) return false;
        bool hide = _buttons.Down(button, HookRules.OnSlices(_state, pt), systemDown: false, Environment.TickCount64, out _);
        _post(new HookEvent(HookEventKind.MouseDown, button, pt, hide));
        return hide;
    }

    bool Up(int button)
    {
        bool hide = _buttons.Up(button);
        if (hide && !_mouseWanted) Sync(); // that was what the mouse hook was being kept for
        return hide;
    }

    /// <summary>File access has no place in an input callback.</summary>
    static void LogLater(Exception ex) => ThreadPool.QueueUserWorkItem(static e => Log.Write((Exception)e!), ex);

    /// <summary>
    /// Releases an Alt/Win key with an unassigned key tapped first, so Windows doesn't treat the
    /// release as "Alt pressed alone" (menu bar) or "Win pressed alone" (Start menu).
    /// </summary>
    static bool InjectMaskedRelease(int vk)
    {
        uint extended = vk is 0xA5 or 0xA3 or 0x5B or 0x5C ? Win32.KEYEVENTF_EXTENDEDKEY : 0;
        var inputs = new[]
        {
            Key(VirtualKeys.MaskKey, 0),
            Key(VirtualKeys.MaskKey, Win32.KEYEVENTF_KEYUP),
            Key(vk, Win32.KEYEVENTF_KEYUP | extended),
        };
        return Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

        static INPUT Key(int vk, uint flags) => new()
        {
            type = Win32.INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = InjectedMarker } },
        };
    }

    public void Dispose()
    {
        _stopping = true;
        uint id = _threadId;
        if (id != 0) Win32.PostThreadMessage(id, Win32.WM_QUIT, 0, 0);
        if (_thread.Join(TimeSpan.FromSeconds(1))) _ready.Dispose();
    }
}
