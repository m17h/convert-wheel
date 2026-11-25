namespace ConvertWheel.Input;

/// <summary>
/// Remembers what happened to each key or button press, so a release always follows its press: hidden
/// presses get hidden releases, and a press that other apps saw never loses its release (which would leave
/// the key stuck down). Pure bookkeeping, used from the hook thread only.
/// </summary>
internal sealed class SwallowTracker(int size)
{
    enum State : byte { Up, Passed, Hidden }

    /// <summary>Longer than Windows' slowest auto-repeat: after this gap a "repeat" is really a new press.</summary>
    const int RepeatGapMs = 1500;

    readonly State[] _state = new State[size];
    readonly long[] _seen = new long[size];

    /// <param name="hide">Whether a press of this key should be hidden right now.</param>
    /// <param name="systemDown">Whether Windows already counted the key as down before this event.</param>
    /// <param name="repeat">True when this is auto-repeat rather than a new press.</param>
    /// <returns>True to hide this event.</returns>
    public bool Down(int code, bool hide, bool systemDown, long now, out bool repeat)
    {
        var state = _state[code];
        // Releases can go unseen (lock screen, UAC prompt, a restarted hook), so don't trust stale state.
        if (state == State.Hidden && now - _seen[code] > RepeatGapMs) state = State.Up;
        if (state == State.Passed && !systemDown) state = State.Up;
        if (state == State.Up && systemDown) state = State.Passed; // pressed before we were watching

        repeat = state != State.Up;
        if (!repeat) state = hide ? State.Hidden : State.Passed;
        _state[code] = state;
        _seen[code] = now;
        return state == State.Hidden || hide;
    }

    /// <returns>True to hide the release.</returns>
    public bool Up(int code)
    {
        bool hidden = _state[code] == State.Hidden;
        _state[code] = State.Up;
        return hidden;
    }

    /// <summary>True while a hidden press is still waiting for its release.</summary>
    public bool Pending => Array.IndexOf(_state, State.Hidden) >= 0;

    public void Reset() => Array.Clear(_state);
}
