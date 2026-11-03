using System.Windows.Threading;
using ConvertWheel.Conversion;
using ConvertWheel.Native;
using ConvertWheel.Shell;
using ConvertWheel.UI;

namespace ConvertWheel.Input;

/// <summary>
/// Hold-to-open state machine. Holding exactly the trigger keys (nothing else) over a file for the
/// configured delay opens the wheel. Any other key, click or scroll during the hold cancels it until
/// the trigger is released, so ordinary shortcuts like Ctrl+C and Ctrl+click keep working.
/// </summary>
internal sealed class TriggerController : IDisposable
{
    /// <summary>Immutable snapshot read by the hook thread to decide what to swallow.</summary>
    sealed record HookState(bool WheelVisible, bool MouseInterest, bool MaskArmed, POINT Center, int Radius);

    enum Phase { Idle, Holding, Open }

    const int StillMs = 90;

    readonly AppSettings _settings;
    readonly ShellResolver _resolver;
    readonly WheelOverlay _wheel;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _timer;
    readonly InputHooks _hooks;
    readonly HashSet<int> _down = [];
    volatile HookState _hookState = new(false, false, false, default, 0);

    Phase _phase;
    bool _tainted, _maskArmed, _resolving;
    long _holdStart, _lastMove;
    POINT _lastPoint;
    POINT? _lastTried;
    int _generation;
    int[] _trigger;

    /// <summary>Set while the settings window is recording a new shortcut.</summary>
    public bool Suspended { get; set; }

    public TriggerController(AppSettings settings, ShellResolver resolver, WheelOverlay wheel, ConversionManager conversions)
    {
        _settings = settings;
        _resolver = resolver;
        _wheel = wheel;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _trigger = [.. settings.TriggerKeys];

        settings.Changed += () =>
        {
            _trigger = [.. _settings.TriggerKeys];
            if (_phase == Phase.Holding) EndHolding();
            if (_settings.Paused) _wheel.Cancel(restoreCursor: false);
        };
        wheel.ConvertRequested += conversions.Start;
        wheel.Dismissed += OnWheelClosed;
        wheel.GeometryChanged += Publish;

        _timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Tick();

        Publish();
        _hooks = new InputHooks(OnHookThread);
    }

    // ------------------------------------------------------------------ hook thread

    bool OnHookThread(HookEvent e)
    {
        var s = _hookState;
        bool swallow = false;
        switch (e.Kind)
        {
            case HookEventKind.KeyDown:
                swallow = s.WheelVisible && IsWheelKey(e.Code);
                break;
            case HookEventKind.KeyUp:
                if (s.MaskArmed && VirtualKeys.IsAltOrWin(e.Code))
                {
                    InputHooks.InjectMaskedRelease(e.Code);
                    swallow = true;
                }
                break;
            case HookEventKind.MouseDown:
                if (!s.MouseInterest) return false;
                swallow = s.WheelVisible && Inside(e.Point, s);
                break;
            case HookEventKind.MouseWheel:
                if (!s.MouseInterest) return false;
                swallow = s.WheelVisible;
                break;
        }
        _dispatcher.BeginInvoke(() => Handle(e, swallow));
        return swallow;
    }

    static bool IsWheelKey(int vk) =>
        vk is VirtualKeys.Escape or VirtualKeys.Enter or VirtualKeys.Tab or (>= 0x31 and <= 0x39) or (>= 0x61 and <= 0x69);

    static bool Inside(POINT p, HookState s)
    {
        long dx = p.X - s.Center.X, dy = p.Y - s.Center.Y;
        return dx * dx + dy * dy <= (long)s.Radius * s.Radius;
    }

    void Publish() => _hookState = new HookState(
        _wheel.IsOpen,
        _phase != Phase.Idle || _wheel.IsOpen,
        _maskArmed,
        _wheel.CenterPx,
        _wheel.HitRadiusPx);

    // ------------------------------------------------------------------ UI thread

    void Handle(HookEvent e, bool swallowed)
    {
        switch (e.Kind)
        {
            case HookEventKind.KeyDown: OnKeyDown(e.Code); break;
            case HookEventKind.KeyUp: OnKeyUp(e.Code); break;
            case HookEventKind.MouseDown: OnMouseDown(e.Code, e.Point, swallowed); break;
            case HookEventKind.MouseWheel:
                if (_phase == Phase.Holding) Taint();
                break;
        }
    }

    void OnKeyDown(int vk)
    {
        int key = VirtualKeys.Normalize(vk);
        bool repeat = !_down.Add(key);

        if (_phase == Phase.Open)
        {
            if (repeat) return;
            switch (vk)
            {
                case VirtualKeys.Escape: _wheel.Cancel(); return;
                case VirtualKeys.Enter: _wheel.CommitHighlighted(); return;
                case VirtualKeys.Tab: _wheel.ToggleExpanded(); return;
                case >= 0x31 and <= 0x39: _wheel.CommitIndex(vk - 0x31); return;
                case >= 0x61 and <= 0x69: _wheel.CommitIndex(vk - 0x61); return;
            }
            if (_trigger.Contains(key))
            {
                if (_wheel.IsSticky) _wheel.Cancel(); // pressing the shortcut again closes the wheel
                return;
            }
            _wheel.Cancel(restoreCursor: false);
            _tainted = true;
            return;
        }

        if (!repeat) Evaluate(justPressed: key);
    }

    void OnKeyUp(int vk)
    {
        int key = VirtualKeys.Normalize(vk);
        // Keep e.g. Ctrl down if the other Ctrl is still held. Only ask about the other side: the released
        // key's own state isn't updated until the hook returns, which may be after this runs.
        if (VirtualKeys.OtherSide(vk) is not int other || !VirtualKeys.Down(other)) _down.Remove(key);
        if (_phase == Phase.Open && !_wheel.IsSticky && _trigger.Contains(key) && !_down.Contains(key)) _wheel.ReleaseTrigger();
        Evaluate(justPressed: null);
    }

    void OnMouseDown(int button, POINT point, bool insideWheel)
    {
        if (_phase == Phase.Holding)
        {
            Taint(); // e.g. Ctrl+click to multi-select
            return;
        }
        if (_phase != Phase.Open) return;
        if (!insideWheel) _wheel.Cancel(restoreCursor: false);
        else if (button == 0) _wheel.ClickAt(point);
        else _wheel.Cancel();
    }

    void Evaluate(int? justPressed)
    {
        // Drop keys whose release we never saw (e.g. the workstation was locked mid-press).
        _down.RemoveWhere(k => k != justPressed && !VirtualKeys.IsPhysicallyDown(k));

        bool anyTrigger = _trigger.Any(_down.Contains);
        bool exact = _trigger.All(_down.Contains) && _down.Count == _trigger.Length;
        bool extra = _down.Any(k => !_trigger.Contains(k));

        if (!anyTrigger)
        {
            _tainted = false;
            if (_maskArmed)
            {
                _maskArmed = false;
                Publish();
            }
        }

        if (_phase == Phase.Holding && !exact)
        {
            EndHolding();
            if (extra && anyTrigger) _tainted = true;
        }
        else if (_phase == Phase.Idle)
        {
            if (extra && anyTrigger) _tainted = true;
            if (exact && !_tainted && !Suspended && !_settings.Paused) BeginHolding();
        }
    }

    void Taint()
    {
        _tainted = true;
        if (_phase == Phase.Holding) EndHolding();
    }

    void BeginHolding()
    {
        _phase = Phase.Holding;
        _holdStart = Environment.TickCount64;
        _lastMove = _holdStart - StillMs;
        Win32.GetCursorPos(out _lastPoint);
        _lastTried = null;
        _timer.Start();
        Publish();
    }

    void EndHolding()
    {
        _phase = Phase.Idle;
        _generation++;
        _timer.Stop();
        Publish();
    }

    void Tick()
    {
        if (Suspended || _settings.Paused)
        {
            if (_phase == Phase.Holding) EndHolding();
            _wheel.Cancel(restoreCursor: false);
            return;
        }
        bool held = _trigger.All(VirtualKeys.IsPhysicallyDown);
        if (_phase == Phase.Open)
        {
            if (!held && !_wheel.IsSticky) SyncMissedReleases();
            return;
        }
        if (_phase != Phase.Holding) return;
        if (!held)
        {
            SyncMissedReleases();
            return;
        }

        long now = Environment.TickCount64;
        Win32.GetCursorPos(out var point);
        if (Distance(point, _lastPoint) > 3)
        {
            _lastPoint = point;
            _lastMove = now;
        }

        if (now - _holdStart < _settings.HoldDelayMs || now - _lastMove < StillMs || _resolving) return;
        if (_lastTried is { } tried && Distance(tried, point) <= 3) return;
        _lastTried = point;
        if (!ShellResolver.IsOverShellView(point)) return;

        _resolving = true;
        _ = ResolveAndOpen(point, ++_generation);
    }

    void SyncMissedReleases()
    {
        foreach (var key in _down.Where(k => !VirtualKeys.IsPhysicallyDown(k)).ToList()) OnKeyUp(key);
    }

    async Task ResolveAndOpen(POINT point, int generation)
    {
        string? path = null;
        try
        {
            path = await _resolver.ResolveFileAsync(point);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            _resolving = false;
        }

        if (generation != _generation || _phase != Phase.Holding || path == null) return;
        if (Suspended || _settings.Paused || !_trigger.All(VirtualKeys.IsPhysicallyDown)) return;
        if (!Win32.GetCursorPos(out var current) || Distance(current, point) > 3) return;
        var plan = Formats.Plan(path);
        if (plan == null) return;

        _phase = Phase.Open;
        _maskArmed = _trigger.Any(k => k is VirtualKeys.Alt or VirtualKeys.Win);
        Win32.GetCursorPos(out var cursor);
        _wheel.Open(plan, cursor, _settings.AlwaysShowAll, notice: null);
        Publish();
        LoadThumbnail(plan.SourcePath);
    }

    async void LoadThumbnail(string path)
    {
        try
        {
            _wheel.SetThumbnail(path, await _resolver.GetThumbnailAsync(path, 128));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    void OnWheelClosed()
    {
        _phase = Phase.Idle;
        _generation++;
        // Still holding the trigger: don't pop the wheel straight back up.
        if (_trigger.Any(_down.Contains)) _tainted = true;
        _timer.Stop();
        Publish();
    }

    static double Distance(POINT a, POINT b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    public void Dispose()
    {
        _generation++;
        _phase = Phase.Idle;
        _wheel.Cancel(restoreCursor: false);
        _timer.Stop();
        _hooks.Dispose();
    }
}
