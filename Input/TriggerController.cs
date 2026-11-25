using System.Windows.Threading;
using ConvertWheel.Conversion;
using ConvertWheel.Native;
using ConvertWheel.Shell;
using ConvertWheel.UI;
using Microsoft.Win32;

namespace ConvertWheel.Input;

/// <summary>
/// Connects the input hooks, the hold-to-open rules (<see cref="HoldGesture"/>), the file lookup and the wheel.
/// </summary>
internal sealed class TriggerController : IDisposable
{
    /// <summary>Real mouse travel (in DIPs) that starts the hold over.</summary>
    const int TravelDip = 24;

    /// <summary>Windows drops input hooks silently, so quietly renew them this often when nothing is held.</summary>
    static readonly TimeSpan HookRefreshInterval = TimeSpan.FromSeconds(30);

    readonly AppSettings _settings;
    readonly ShellResolver _resolver;
    readonly WheelOverlay _wheel;
    readonly ConversionManager _conversions;
    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _timer, _hookRefresh;
    readonly InputHooks _hooks;
    readonly HoldGesture _gesture = new(VirtualKeys.IsPhysicallyDown);

    bool _maskArmed, _resolving, _paused, _disposed;
    int _generation, _travelPx, _unheldTicks;

    /// <summary>Set while the settings window is recording a new shortcut.</summary>
    public bool Suspended { get; set; }

    public TriggerController(AppSettings settings, ShellResolver resolver, WheelOverlay wheel, ConversionManager conversions)
    {
        _settings = settings;
        _resolver = resolver;
        _wheel = wheel;
        _conversions = conversions;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _paused = settings.Paused;
        _gesture.SetTrigger(settings.TriggerKeys);

        _timer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) => Tick();
        _hookRefresh = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = HookRefreshInterval };
        _hookRefresh.Tick += (_, _) => RefreshIdleHooks();

        settings.Changed += OnSettingsChanged;
        wheel.ConvertRequested += conversions.Start;
        wheel.Dismissed += OnWheelClosed;
        wheel.GeometryChanged += Publish;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        _hooks = new InputHooks(e => _dispatcher.BeginInvoke(() => Handle(e)), enabled: !_paused);
        _hookRefresh.IsEnabled = !_paused;
        Publish();
    }

    /// <summary>Tells the hook thread what to hide and whether the mouse matters.</summary>
    void Publish()
    {
        if (_hooks == null) return;
        _hooks.State = new HookState(_wheel.IsOpen, _maskArmed, _wheel.CenterPx, _wheel.HitRadiusPx, _wheel.PassRadiusPx, [.. _settings.TriggerKeys]);
        _hooks.WatchMouse = _wheel.IsOpen || _gesture.Phase != HoldPhase.Idle;
    }

    // ------------------------------------------------------------------ keeping the hooks alive

    void OnSettingsChanged()
    {
        _gesture.SetTrigger(_settings.TriggerKeys);
        if (_settings.Paused != _paused)
        {
            _paused = _settings.Paused;
            StartOver();
            _hooks.Enabled = !_paused; // switching back on installs fresh hooks
            _hookRefresh.IsEnabled = !_paused;
        }
        Sync();
    }

    void RefreshIdleHooks()
    {
        if (_gesture.Phase == HoldPhase.Idle && !_wheel.IsOpen && !_gesture.AnyTriggerDown) _hooks.Refresh();
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => _dispatcher.BeginInvoke(RestartInput);

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) _dispatcher.BeginInvoke(RestartInput);
    }

    void OnDisplayChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => _wheel.Cancel(restoreCursor: false));

    /// <summary>Sleep, lock and user switching can eat key releases and hooks alike.</summary>
    void RestartInput()
    {
        if (_disposed) return;
        StartOver();
        if (!_paused) _hooks.Refresh(forget: true);
    }

    void StartOver()
    {
        _wheel.Cancel(restoreCursor: false);
        _gesture.Reset();
        _maskArmed = false;
        Sync();
    }

    // ------------------------------------------------------------------ input events (UI thread)

    void Handle(HookEvent e)
    {
        if (_disposed) return;
        switch (e.Kind)
        {
            case HookEventKind.KeyDown: OnKeyDown(e.Code, e.Hidden); break;
            case HookEventKind.KeyUp: OnKeyUp(e.Code); break;
            case HookEventKind.MouseDown: OnMouseDown(e.Code, e.Point, e.Hidden); break;
            case HookEventKind.MouseWheel:
                if (_gesture.Phase == HoldPhase.Holding) _gesture.Disturb();
                else if (_gesture.Phase == HoldPhase.Open && !e.Hidden) _wheel.Cancel(restoreCursor: false);
                break;
        }
        Sync();
    }

    void OnKeyDown(int vk, bool hidden)
    {
        bool wasOpen = _gesture.Phase == HoldPhase.Open;
        Win32.GetCursorPos(out var cursor);
        _gesture.Press(vk, allowed: !Suspended && !_settings.Paused, VirtualKeys.AnyMouseButtonDown(), cursor, Environment.TickCount64);

        if (!wasOpen)
        {
            if (_gesture.Phase == HoldPhase.Holding) _travelPx = (int)Math.Round(TravelDip * Win32.MonitorAt(cursor).scale);
            return;
        }
        if (_gesture.IsTrigger(vk))
        {
            if (_wheel.IsSticky) _wheel.Cancel(); // pressing the shortcut again closes the wheel
            return;
        }
        if (!hidden)
        {
            _wheel.Cancel(restoreCursor: false); // some other shortcut: get out of its way
            return;
        }
        switch (vk)
        {
            case VirtualKeys.Escape: _wheel.Cancel(); break;
            case VirtualKeys.Enter: _wheel.CommitHighlighted(); break;
            case VirtualKeys.Tab: _wheel.ToggleExpanded(); break;
            case VirtualKeys.ArrowLeft: _wheel.Step(-1); break;
            case VirtualKeys.ArrowRight: _wheel.Step(1); break;
            case VirtualKeys.ArrowUp: _wheel.StepRing(outwards: true); break;
            case VirtualKeys.ArrowDown: _wheel.StepRing(outwards: false); break;
            case >= 0x31 and <= 0x39: _wheel.CommitIndex(vk - 0x31); break;
            case >= 0x61 and <= 0x69: _wheel.CommitIndex(vk - 0x61); break;
        }
    }

    void OnKeyUp(int vk)
    {
        bool letGo = _gesture.Release(vk);
        if (letGo && _gesture.Phase == HoldPhase.Open) _wheel.ReleaseTrigger();
        if (!_gesture.AnyTriggerDown) _maskArmed = false;
    }

    void OnMouseDown(int button, POINT point, bool hidden)
    {
        if (_gesture.Phase == HoldPhase.Open)
        {
            if (hidden && button == 0) _wheel.ClickAt(point);
            else _wheel.Cancel(restoreCursor: hidden); // a click that went through must land where it was aimed
        }
        else _gesture.Disturb(); // e.g. Ctrl+click to multi-select
    }

    // ------------------------------------------------------------------ holding

    /// <summary>The timer, mouse hook and published state follow the phase.</summary>
    void Sync()
    {
        bool active = _gesture.Phase != HoldPhase.Idle;
        if (active != _timer.IsEnabled)
        {
            _timer.IsEnabled = active;
            _unheldTicks = 0;
        }
        if (!active) _generation++;
        Publish();
    }

    void Tick()
    {
        if (Suspended || _settings.Paused)
        {
            _wheel.Cancel(restoreCursor: false);
            _gesture.Disturb();
        }
        else if (_gesture.Phase == HoldPhase.Open) WatchOpenWheel();
        else HoldTick();
        Sync();
    }

    void WatchOpenWheel()
    {
        // A window switch, UAC prompt or lock screen: whatever the keys do now isn't meant for the wheel.
        if (!_wheel.FocusUnchanged)
        {
            _wheel.Cancel(restoreCursor: false);
            _gesture.ForgetMissedReleases();
            return;
        }
        // The release was never reported (an elevated window has the focus, or the hook is gone). That's no
        // deliberate choice, so close without converting. Two ticks, as the event itself may be on its way.
        bool unheld = !_wheel.IsSticky && !_gesture.Held;
        _unheldTicks = unheld ? _unheldTicks + 1 : 0;
        if (_unheldTicks < 2) return;
        _wheel.Cancel();
        _gesture.ForgetMissedReleases();
        _maskArmed = false;
        _hooks.Refresh();
    }

    void HoldTick()
    {
        if (!Win32.GetCursorPos(out var cursor)) return;
        var step = _gesture.Tick(cursor, Environment.TickCount64, VirtualKeys.AnyMouseButtonDown(),
            ShellResolver.IsOverShellView(cursor), _settings.HoldDelayMs, _travelPx);
        if (step != HoldStep.Resolve || _resolving) return;
        _resolving = true;
        _ = ResolveAndOpen(cursor, ++_generation);
    }

    async Task ResolveAndOpen(POINT point, int generation)
    {
        HoveredFile? hovered = null;
        ConversionPlan? plan = null;
        IReadOnlyList<ConversionPlan> batch = [];
        try
        {
            hovered = await _resolver.ResolveAsync(point);
            // Looking inside files (is this WebP animated?) can be slow on a network drive.
            if (hovered != null) (plan, batch) = await Task.Run(() => Batch.Plan(hovered));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            _resolving = false;
        }

        if (_disposed || generation != _generation || plan == null || Suspended || _settings.Paused) return;
        if (!Win32.GetCursorPos(out var cursor) || !_gesture.StillHolding(point, cursor, VirtualKeys.AnyMouseButtonDown())) return;

        _maskArmed = _settings.TriggerKeys.Any(k => k is VirtualKeys.Alt or VirtualKeys.Win);
        _gesture.Opened();
        _wheel.Open(plan, cursor, _settings.AlwaysShowAll, notice: null, batch, hovered!.SelectedCount);
        Sync();
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
        _gesture.Closed();
        Sync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _wheel.Cancel(restoreCursor: false);
        _wheel.ConvertRequested -= _conversions.Start;
        _wheel.Dismissed -= OnWheelClosed;
        _wheel.GeometryChanged -= Publish;
        _timer.Stop();
        _hookRefresh.Stop();
        _hooks.Dispose();
    }
}
