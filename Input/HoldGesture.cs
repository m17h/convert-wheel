using ConvertWheel.Native;

namespace ConvertWheel.Input;

internal enum HoldPhase { Idle, Holding, Open }

internal enum HoldStep
{
    /// <summary>Nothing to do yet.</summary>
    Wait,
    /// <summary>The hold is off (until the trigger is released and pressed again).</summary>
    Cancelled,
    /// <summary>Held long enough over one spot: find out what's under the cursor.</summary>
    Resolve,
}

/// <summary>
/// Decides when holding the trigger means "open the wheel". Only exactly the trigger keys, held still over
/// one spot with no mouse button down, count. Any other key, click or scroll cancels until the trigger is let
/// go, and real mouse travel starts the hold over, so shortcuts, Ctrl+click and dragging keep working.
/// Pure: time, cursor and key state all come from the caller.
/// </summary>
internal sealed class HoldGesture(Func<int, bool> isPhysicallyDown)
{
    /// <summary>The mouse may still be on its way to Explorer this long after the press.</summary>
    public const int ArriveMs = 150;
    /// <summary>The cursor must rest this long before the file under it is looked up.</summary>
    public const int StillMs = 90;
    const int StillPx = 3;

    readonly HashSet<int> _down = [];
    int[] _trigger = [];
    long _pressed, _start, _lastMove;
    POINT _anchor, _lastPoint;
    POINT? _lastTried;

    public HoldPhase Phase { get; private set; }

    /// <summary>True when this press of the trigger was used for something else.</summary>
    public bool Spoilt { get; private set; }

    public bool AnyTriggerDown => _trigger.Any(_down.Contains);

    public void SetTrigger(IEnumerable<int> keys)
    {
        _trigger = [.. keys];
        if (Phase == HoldPhase.Holding) Phase = HoldPhase.Idle;
        Spoilt = AnyTriggerDown;
    }

    public bool IsTrigger(int vk) => _trigger.Contains(VirtualKeys.Normalize(vk));

    /// <summary>All trigger keys are down according to Windows (not just the events we've seen).</summary>
    public bool Held => _trigger.Length > 0 && _trigger.All(isPhysicallyDown);

    /// <summary>A key went down.</summary>
    /// <param name="allowed">False while paused or while Settings records a shortcut.</param>
    public void Press(int vk, bool allowed, bool buttonDown, POINT cursor, long now)
    {
        int key = VirtualKeys.Normalize(vk);
        bool fresh = _down.Add(key);
        if (Phase == HoldPhase.Open)
        {
            if (!_trigger.Contains(key) && AnyTriggerDown) Spoilt = true;
            return;
        }
        if (!fresh) return;
        Evaluate(key);

        if (Phase != HoldPhase.Idle || Spoilt || !Exact) return;
        if (!allowed || buttonDown)
        {
            Spoilt = true; // e.g. Ctrl pressed half-way through a drag, to copy instead of move
            return;
        }
        Phase = HoldPhase.Holding;
        _pressed = _start = now;
        _lastMove = now - StillMs;
        _anchor = _lastPoint = cursor;
        _lastTried = null;
    }

    /// <summary>A key came up.</summary>
    /// <returns>True when that let go of the trigger.</returns>
    public bool Release(int vk)
    {
        int key = VirtualKeys.Normalize(vk);
        // Keep e.g. Ctrl down if the other Ctrl is still held. Only ask about the other side: the released
        // key's own state isn't updated until the hook returns, which may be after this runs.
        if (VirtualKeys.OtherSide(vk) is not int other || !isPhysicallyDown(other)) _down.Remove(key);
        bool letGo = _trigger.Contains(key) && !_down.Contains(key);
        Evaluate(null);
        return letGo;
    }

    /// <summary>A click, a scroll or anything else that shows the trigger is being used for something else.</summary>
    public void Disturb()
    {
        Spoilt = AnyTriggerDown;
        if (Phase == HoldPhase.Holding) Phase = HoldPhase.Idle;
    }

    public HoldStep Tick(POINT cursor, long now, bool buttonDown, bool overItems, int delayMs, int travelPx)
    {
        if (Phase != HoldPhase.Holding) return HoldStep.Cancelled;
        if (!Held)
        {
            ForgetMissedReleases();
            return Phase == HoldPhase.Holding ? Cancel() : HoldStep.Cancelled;
        }
        if (buttonDown) return Cancel();

        if (Distance(cursor, _lastPoint) > StillPx)
        {
            _lastPoint = cursor;
            _lastMove = now;
        }
        if (!overItems)
        {
            // Give up soon, so a trigger held in a game or editor never keeps the mouse hook in place.
            if (now - _pressed > Math.Min(ArriveMs, delayMs / 2)) return Cancel();
        }
        else if (Distance(cursor, _anchor) > travelPx)
        {
            // Still on the way to a file: the hold counts from where the mouse comes to rest.
            _anchor = cursor;
            _start = now;
        }

        if (now - _start < delayMs || now - _lastMove < StillMs) return HoldStep.Wait;
        if (_lastTried is { } tried && Distance(tried, cursor) <= StillPx) return HoldStep.Wait;
        _lastTried = cursor;
        return HoldStep.Resolve;
    }

    /// <summary>Last check before the wheel opens on what was found at <paramref name="point"/>.</summary>
    public bool StillHolding(POINT point, POINT cursor, bool buttonDown) =>
        Phase == HoldPhase.Holding && Held && !buttonDown && Distance(point, cursor) <= StillPx;

    public void Opened() => Phase = HoldPhase.Open;

    /// <summary>The wheel closed. Still holding the trigger? Don't pop it straight back up.</summary>
    public void Closed()
    {
        if (Phase == HoldPhase.Open) Phase = HoldPhase.Idle;
        if (AnyTriggerDown) Spoilt = true;
    }

    /// <summary>Start over: key releases may have gone unseen (session locked, hooks restarted).</summary>
    public void Reset()
    {
        Phase = HoldPhase.Idle;
        _down.Clear();
        Spoilt = false;
    }

    /// <summary>Drops keys whose release never reached us.</summary>
    public void ForgetMissedReleases() => Evaluate(null);

    bool Exact => _trigger.Length > 0 && _trigger.All(_down.Contains) && _down.Count == _trigger.Length;

    HoldStep Cancel()
    {
        Disturb();
        return HoldStep.Cancelled;
    }

    void Evaluate(int? justPressed)
    {
        _down.RemoveWhere(k => k != justPressed && !isPhysicallyDown(k));
        bool any = AnyTriggerDown;
        if (!any) Spoilt = false;
        else if (_down.Any(k => !_trigger.Contains(k))) Spoilt = true;
        if (Phase == HoldPhase.Holding && (Spoilt || !Exact)) Phase = HoldPhase.Idle;
    }

    static double Distance(POINT a, POINT b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
