using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ConvertWheel.Conversion;
using ConvertWheel.Native;
using ConvertWheel.Shell;

namespace ConvertWheel.UI;

/// <summary>
/// The radial menu. It is a click-through, non-activating, topmost layered window: all input is
/// read from the global hooks and the cursor position, so it never steals focus from Explorer.
/// Screen readers see it as a list of slices.
/// </summary>
internal sealed class WheelOverlay : Window
{
    const double Extent = WheelLayout.R4 + 44;
    const double Side = Extent * 2;
    static readonly Point C = new(Extent, Extent);

    readonly nint _hwnd;
    readonly WheelStage _stage;
    readonly ScaleTransform _stageScale = new(1, 1, Extent, Extent);
    readonly ScaleTransform _fitScale = new(1, 1, Extent, Extent);
    readonly Canvas _innerLayer = new();
    readonly Canvas _outerLayer = new();
    readonly ScaleTransform _outerScale = new(1, 1, Extent, Extent);
    readonly ScaleTransform _outerBackdropScale = new(1, 1, Extent, Extent);
    Canvas _outerBackdrop = new();

    readonly Image _thumb = new() { Width = 46, Height = 46, Stretch = Stretch.Uniform };
    readonly Border _badge = new() { Width = 42, Height = 42, CornerRadius = new CornerRadius(11) };
    readonly TextBlock _badgeText = HubText(11.5, FontWeights.Bold);
    readonly TextBlock _fileName = HubText(11.5, FontWeights.SemiBold);
    readonly TextBlock _fileMeta = HubText(11, FontWeights.Normal);
    readonly TextBlock _hotCaption = HubText(11, FontWeights.Normal);
    readonly TextBlock _hotLabel = HubText(25, FontWeights.Bold);
    readonly TextBlock _hotHint = HubText(11, FontWeights.Normal);
    readonly TextBlock _status = new() { Opacity = 0, IsHitTestVisible = false };
    readonly StackPanel _idlePanel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly StackPanel _hotPanel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0 };

    readonly List<WheelSegment> _inner = [];
    readonly List<WheelSegment> _outer = [];
    WheelTheme? _theme;
    ConversionPlan? _plan;
    IReadOnlyList<ConversionPlan> _batch = [];
    string? _notice;
    WheelSegment? _hot;
    bool _expanded, _placing, _armed;
    double _scale = 1, _fit = 1;
    POINT _center, _openCursor;
    nint _foreground;
    POINT? _restoreCursor, _keyboardCursor;
    long _moreHoverStart;
    int _selectedCount;

    public event Action<ConversionPlan, FormatInfo>? ConvertRequested;
    public event Action? Dismissed;
    public event Action? GeometryChanged;

    public bool IsOpen { get; private set; }

    /// <summary>False while the trigger is held (direction picks, release converts); true once pinned (click or Enter to pick).</summary>
    public bool IsSticky { get; private set; }

    public bool IsExpanded => _expanded;

    public POINT CenterPx => _center;
    public int HitRadiusPx => (int)Math.Ceiling(WheelLayout.HitRadius(_expanded) * Unit);

    /// <summary>
    /// Clicks this close to the middle go through to the file underneath. None do when the wheel had to move
    /// (and the cursor with it): something else is under it then.
    /// </summary>
    public int PassRadiusPx => _restoreCursor == null ? (int)(WheelLayout.PassRadius * Unit) : 0;

    /// <summary>Physical pixels per wheel unit.</summary>
    double Unit => _scale * _fit;

    /// <summary>Animations follow Windows' "Animation effects" setting.</summary>
    internal static bool Motion => MotionOverride ?? SystemParameters.ClientAreaAnimation;
    internal static bool? MotionOverride { get; set; }

    /// <summary>Draws in this theme instead of the one Windows is set to.</summary>
    internal WheelTheme? ThemeOverride { get; set; }

    public WheelOverlay()
    {
        Title = "Convert Wheel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = Side;
        Height = Side;
        FontFamily = Palette.UiFont;

        _stage = new WheelStage(this)
        {
            Width = Side,
            Height = Side,
            RenderTransform = new TransformGroup { Children = { _stageScale, _fitScale } },
        };
        _outerLayer.RenderTransform = _outerScale;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Assertive);
        BuildHubContents();
        Content = _stage;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT);
        HwndSource.FromHwnd(_hwnd)!.AddHook(OnWindowMessage);
    }

    // ------------------------------------------------------------------ building

    static TextBlock HubText(double size, FontWeight weight) => new()
    {
        FontSize = size,
        FontWeight = weight,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static void Place(FrameworkElement element, double radius)
    {
        element.Width = element.Height = radius * 2;
        Canvas.SetLeft(element, C.X - radius);
        Canvas.SetTop(element, C.Y - radius);
    }

    /// <summary>A disc with a soft drop shadow (a gradient, far cheaper than a blur effect).</summary>
    static Canvas Disc(double radius, Brush fill, Brush edge, bool shadowed)
    {
        var canvas = new Canvas();
        if (shadowed)
        {
            double shadowR = radius + 36;
            var shadow = new Ellipse
            {
                Fill = Palette.Freeze(new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(120, 0, 0, 0), radius / shadowR - 0.05),
                        new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
                    },
                }),
            };
            Place(shadow, shadowR);
            Canvas.SetTop(shadow, C.Y - shadowR + 8);
            canvas.Children.Add(shadow);
        }

        var disc = new Ellipse { Fill = fill, Stroke = edge, StrokeThickness = shadowed ? 1.2 : 2 };
        Place(disc, radius);
        canvas.Children.Add(disc);
        return canvas;
    }

    void BuildHubContents()
    {
        _badge.Child = _badgeText;
        _badgeText.VerticalAlignment = VerticalAlignment.Center;
        _thumb.Clip = new RectangleGeometry(new Rect(0, 0, 46, 46), 9, 9);
        var image = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 0, 4) };
        image.Children.Add(_badge);
        image.Children.Add(_thumb);
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);

        _fileName.MaxWidth = 92;
        _fileMeta.MaxWidth = 88;
        _idlePanel.Children.Add(image);
        _idlePanel.Children.Add(_fileName);
        _idlePanel.Children.Add(_fileMeta);

        _hotLabel.Margin = new Thickness(0, -2, 0, -1);
        _hotLabel.MaxWidth = 104;
        _hotCaption.MaxWidth = _hotHint.MaxWidth = 92;
        _hotPanel.Children.Add(_hotCaption);
        _hotPanel.Children.Add(_hotLabel);
        _hotPanel.Children.Add(_hotHint);
    }

    /// <summary>Everything that depends on the colours but not on the file.</summary>
    void ApplyTheme(WheelTheme theme)
    {
        if (ReferenceEquals(theme, _theme)) return;
        _theme = theme;
        bool shadowed = !theme.HighContrast;

        _outerBackdrop = Disc(WheelLayout.OuterRim, theme.OuterDisc, theme.DiscEdge, shadowed);
        _outerBackdrop.RenderTransform = _outerBackdropScale;

        (_idlePanel.Parent as Panel)?.Children.Clear();
        var hub = new Grid();
        Place(hub, WheelLayout.HubR);
        hub.Children.Add(new Ellipse { Fill = theme.Hub, Stroke = theme.HubEdge, StrokeThickness = shadowed ? 1.2 : 2 });
        hub.Children.Add(_idlePanel);
        hub.Children.Add(_hotPanel);
        _badgeText.Foreground = theme.HotText;
        _fileName.Foreground = theme.Text;
        _hotCaption.Foreground = _hotHint.Foreground = theme.SubtleText;

        _stage.Children.Clear();
        _stage.Children.Add(_outerBackdrop);
        _stage.Children.Add(_outerLayer);
        _stage.Children.Add(Disc(WheelLayout.InnerRim, theme.InnerDisc, theme.DiscEdge, shadowed));
        _stage.Children.Add(_innerLayer);
        _stage.Children.Add(hub);
        _stage.Children.Add(_status);
    }

    void BuildSegments(ConversionPlan plan, bool dimmed)
    {
        Fill(_inner, _innerLayer, WheelRing.Inner, plan.Suggested.Count == 0 ? [] : plan.HasMore ? [.. plan.Suggested, null] : [.. plan.Suggested]);
        Fill(_outer, _outerLayer, WheelRing.Outer, [.. plan.All]);

        void Fill(List<WheelSegment> segments, Canvas layer, WheelRing ring, List<FormatInfo?> formats)
        {
            segments.Clear();
            layer.Children.Clear();
            for (int i = 0; i < formats.Count; i++)
            {
                var segment = new WheelSegment(this, _theme!, C, formats[i], ring, i, formats.Count, dimmed && formats[i] != null);
                segments.Add(segment);
                layer.Children.Add(segment);
            }
            if (segments.Count == 0) return;
            // One size for the whole ring, and all of its hints or none: a patchwork looks broken.
            // (The middle shows the hint of the slice being pointed at either way.)
            foreach (var (hint, smallest) in new (double?, double)[] { (11, 15), (10.5, 12), (null, 11) })
            {
                var sizes = segments.Where(s => !s.IsMore).Select(s => s.FittingSize(C, hint, smallest)).ToList();
                if (hint != null && sizes.Any(s => s == null)) continue;
                foreach (var segment in segments) segment.LayOut(C, hint, sizes.Min(s => s ?? smallest));
                break;
            }
        }
    }

    // ------------------------------------------------------------------ opening & closing

    /// <param name="batch">Every file the pick applies to when several are selected (the hovered one first).</param>
    /// <param name="selectedCount">How many items are selected along with the hovered file.</param>
    public void Open(ConversionPlan plan, POINT cursor, bool startExpanded, string? notice,
        IReadOnlyList<ConversionPlan>? batch = null, int selectedCount = 0)
    {
        Win32.GetCursorPos(out _openCursor);
        _foreground = Win32.GetForegroundWindow();
        ShowPlan(plan, startExpanded, notice, batch, selectedCount);
        _restoreCursor = null;
        Position(cursor);
        Win32.GetCursorPos(out _openCursor);

        _stage.BeginAnimation(OpacityProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _stage.Opacity = 0;
        _stageScale.ScaleX = _stageScale.ScaleY = 0.82;

        if (!IsVisible) Show();
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        _stage.BeginAnimation(OpacityProperty, Anim(1, 120));
        var pop = Anim(1, 260, Motion ? new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } : null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        for (int i = 0; i < _inner.Count; i++) _inner[i].FadeIn(i * 18);
        if (_expanded) for (int i = 0; i < _outer.Count; i++) _outer[i].FadeIn(60 + i * 8);

        IsOpen = true;
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
        GeometryChanged?.Invoke();
        RefreshPeers();
        Raise(AutomationEvents.MenuOpened);
        Announce(_batch.Count > 1
            ? $"Convert {_batch.Count} files. {_inner.Count} choices. Arrow keys to choose, Enter to convert, Escape to close."
            : $"Convert {_fileName.Text}. {_inner.Count} choices. Arrow keys to choose, Enter to convert, Escape to close.");
    }

    /// <summary>Fills the wheel for a file without showing it.</summary>
    internal void ShowPlan(ConversionPlan plan, bool startExpanded, string? notice, IReadOnlyList<ConversionPlan>? batch = null, int selectedCount = 0)
    {
        _plan = plan;
        _batch = batch is { Count: > 1 } ? batch : [plan];
        _selectedCount = selectedCount;
        _notice = notice;
        _hot = null;
        _keyboardCursor = null;
        _armed = false;
        _moreHoverStart = 0;
        IsSticky = false;
        ApplyTheme(ThemeOverride ?? WheelTheme.Current);
        BuildSegments(plan, dimmed: notice != null);

        _badge.Background = _theme!.HotFill(plan.SourceKind);
        _badge.BeginAnimation(OpacityProperty, null);
        _badge.Opacity = 1;
        _badgeText.Text = plan.SourceExt.ToUpperInvariant();
        _thumb.BeginAnimation(OpacityProperty, null);
        _thumb.Source = null;
        _fileName.Text = _batch.Count > 1 ? $"{_batch.Count} files" : System.IO.Path.GetFileName(plan.SourcePath);
        _idlePanel.BeginAnimation(OpacityProperty, null);
        _hotPanel.BeginAnimation(OpacityProperty, null);
        _idlePanel.Opacity = 1;
        _hotPanel.Opacity = 0;
        RefreshMeta();
        SetExpanded(startExpanded || _inner.Count == 0, animate: false);
    }

    void Position(POINT cursor)
    {
        var (work, scale) = Win32.MonitorAt(cursor);
        double oldUnit = Unit;
        var oldCenter = IsOpen ? _center : cursor;
        _scale = scale;
        (_center, _fit) = WheelLayout.Place(cursor, work, scale, _expanded);
        _fitScale.ScaleX = _fitScale.ScaleY = _fit;

        // Near a screen edge the wheel shifts to stay fully visible; move the cursor with it so
        // directions still line up, and put it back afterwards.
        if ((_center.X != oldCenter.X || _center.Y != oldCenter.Y || (IsOpen && Unit != oldUnit)) && Win32.GetCursorPos(out var at))
        {
            _restoreCursor ??= _openCursor;
            double ratio = IsOpen ? Unit / oldUnit : 1;
            Win32.SetCursorPos(_center.X + (int)Math.Round((at.X - oldCenter.X) * ratio), _center.Y + (int)Math.Round((at.Y - oldCenter.Y) * ratio));
            if (_keyboardCursor != null && Win32.GetCursorPos(out var parked)) _keyboardCursor = parked; // that move was ours
        }

        // Twice: the first move may cross monitors and trigger a DPI rescale that shifts the window.
        int size = (int)Math.Round(Side * scale);
        _placing = true;
        for (int i = 0; i < 2; i++)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _center.X - size / 2, _center.Y - size / 2, size, size, Win32.SWP_NOACTIVATE);
        _placing = false;
    }

    /// <summary>
    /// True while the window the user was in still has the focus. (Screen readers choosing a slice make Windows
    /// bring the wheel forward for a moment; that's not the user going elsewhere.)
    /// </summary>
    public bool FocusUnchanged => Win32.GetForegroundWindow() is var now && (now == _foreground || (now == _hwnd && now != 0));

    nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Hand the focus straight back to Explorer.
        if (msg == Win32.WM_ACTIVATE && (wParam & 0xFFFF) != 0 && _foreground != 0 && _foreground != _hwnd)
            Dispatcher.BeginInvoke(() => Win32.SetForegroundWindow(_foreground));
        return 0;
    }

    /// <summary>The screen's scale changed under an open wheel: its measurements no longer hold.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsOpen && !_placing && Math.Abs(newDpi.DpiScaleX - _scale) > 0.01) Close(restoreCursor: false);
    }

    void Close(bool restoreCursor)
    {
        if (!IsOpen) return;
        IsOpen = false;
        IsSticky = false;
        CompositionTarget.Rendering -= OnFrame;
        if (restoreCursor && _restoreCursor is { } back) Win32.SetCursorPos(back.X, back.Y);
        _restoreCursor = null;

        var fade = Anim(0, 150);
        fade.Completed += (_, _) => { if (!IsOpen) Hide(); };
        _stage.BeginAnimation(OpacityProperty, fade);
        var shrink = Anim(0.9, 170);
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        GeometryChanged?.Invoke();
        RefreshPeers();
        Raise(AutomationEvents.MenuClosed);
        Dismissed?.Invoke();
    }

    public void SetThumbnail(string path, BitmapSource? image)
    {
        if (!IsOpen || image == null || _plan?.SourcePath != path) return;
        _thumb.Source = image;
        _thumb.Opacity = 0;
        _thumb.BeginAnimation(OpacityProperty, Anim(1, 160));
        _badge.BeginAnimation(OpacityProperty, Anim(0, 160));
    }

    // ------------------------------------------------------------------ interaction

    /// <summary>The trigger was let go: convert to the slice aimed at, or (in the middle) keep the wheel up for a click.</summary>
    public void ReleaseTrigger()
    {
        if (!IsOpen || IsSticky) return;
        if (Win32.GetCursorPos(out var cursor)) FollowPointer(cursor);
        if (_hot?.Format != null)
        {
            Commit(_hot);
            return;
        }
        if (_hot?.IsMore == true) SetExpanded(true, animate: true);
        Pin();
    }

    public void ClickAt(POINT pt)
    {
        if (!IsOpen) return;
        if (At(pt, exactly: true) is { } segment) Choose(segment);
        else Close(restoreCursor: true);
    }

    public void CommitHighlighted()
    {
        if (_hot?.Format != null) Commit(_hot);
        else if (_hot?.IsMore == true) ToggleExpanded();
    }

    public void CommitIndex(int index)
    {
        if (IsOpen && index >= 0 && index < _inner.Count) Choose(_inner[index]);
    }

    /// <summary>Converts to a slice, or opens/closes the outer ring for the "All" slice.</summary>
    public void Choose(WheelSegment segment)
    {
        if (!IsShowing(segment)) return;
        if (segment.Format != null)
        {
            Commit(segment);
            return;
        }
        SetExpanded(!_expanded, animate: true);
        Pin();
    }

    public void ToggleExpanded()
    {
        if (IsOpen && _inner.Count > 0) SetExpanded(!_expanded, animate: true);
    }

    /// <summary>Highlights a slice from the keyboard or a screen reader. From then on only a click or Enter converts.</summary>
    public void Highlight(WheelSegment segment)
    {
        if (!IsShowing(segment)) return;
        Win32.GetCursorPos(out var cursor);
        _keyboardCursor = cursor;
        Pin();
        SetHot(segment);
    }

    /// <summary>Arrow keys: to the next or previous slice of the ring.</summary>
    public void Step(int by)
    {
        if (!IsOpen) return;
        var ring = _hot?.Ring == WheelRing.Outer && _expanded || _inner.Count == 0 ? _outer : _inner;
        if (ring.Count == 0) return;
        int at = _hot != null && ring.Contains(_hot) ? (_hot.Index + by + ring.Count) % ring.Count : by > 0 ? 0 : ring.Count - 1;
        Highlight(ring[at]);
    }

    /// <summary>Arrow keys: out to every format, or back in to the suggestions.</summary>
    public void StepRing(bool outwards)
    {
        if (!IsOpen) return;
        if (outwards && !_expanded) SetExpanded(true, animate: true);
        var (from, to) = outwards ? (_inner, _outer) : (_outer, _inner);
        if (to.Count == 0) return;
        if (_hot?.Ring == to[0].Ring) Highlight(_hot);
        else Highlight(to[_hot == null || from.Count == 0 ? 0 : WheelLayout.Nearest(_hot.Index, from.Count, to.Count)]);
    }

    public void Cancel(bool restoreCursor = true) => Close(restoreCursor);

    /// <summary>Keeps the wheel up without the trigger.</summary>
    void Pin()
    {
        if (IsSticky) return;
        IsSticky = true;
        RefreshMeta();
        GeometryChanged?.Invoke();
    }

    void Commit(WheelSegment segment)
    {
        var format = segment.Format!;
        var files = Batch.For(_batch, format);
        segment.Pulse();
        Close(restoreCursor: true);
        foreach (var plan in files) ConvertRequested?.Invoke(plan, format);
    }

    public int CountIn(WheelRing ring) => (ring == WheelRing.Outer ? _outer : _inner).Count;

    public bool IsShowing(WheelSegment segment) =>
        IsOpen && (segment.Ring == WheelRing.Outer ? _expanded && _outer.Contains(segment) : _inner.Contains(segment));

    void OnFrame(object? sender, EventArgs e)
    {
        if (!IsOpen || !Win32.GetCursorPos(out var p)) return;
        FollowPointer(p);

        // Resting on "All" while holding opens the full ring without releasing.
        if (!IsSticky && _hot?.IsMore == true && !_expanded)
        {
            if (_moreHoverStart == 0) _moreHoverStart = Environment.TickCount64;
            else if (Environment.TickCount64 - _moreHoverStart > 380) SetExpanded(true, animate: true);
        }
        else
        {
            _moreHoverStart = 0;
        }
    }

    void FollowPointer(POINT p)
    {
        // Arrow keys stay in charge until the mouse really moves; and nothing is chosen before it ever has.
        if (_keyboardCursor is { } parked)
        {
            if (Moved(parked, p) < 8 * _scale) return;
            _keyboardCursor = null;
        }
        _armed |= Moved(_openCursor, p) >= 8 * _scale;
        var hit = _armed ? At(p, exactly: IsSticky) : null;
        if (hit != _hot) SetHot(hit);

        static double Moved(POINT a, POINT b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    WheelSegment? At(POINT p, bool exactly)
    {
        var (distance, angle) = WheelLayout.Polar(p, _center, Unit);
        var found = exactly
            ? WheelLayout.Hit(distance, angle, _inner.Count, _outer.Count, _expanded)
            : WheelLayout.Flick(distance, angle, _inner.Count, _outer.Count, _expanded);
        return found is { } f ? (f.Ring == WheelRing.Outer ? _outer : _inner)[f.Index] : null;
    }

    internal void SetHot(WheelSegment? segment)
    {
        _hot?.SetHot(false);
        _hot = segment;
        segment?.SetHot(true);

        if (segment?.Format is { } format)
        {
            int files = Batch.For(_batch, format).Count;
            _hotCaption.Text = _batch.Count == 1 ? "Convert to" : files == _batch.Count ? $"{files} files to" : $"{files} of {_batch.Count} files to";
            _hotLabel.Text = format.Label;
            _hotLabel.FontSize = format.Label.Length > 5 ? 20 : 25;
            _hotLabel.Foreground = _theme!.HubAccent(format.Kind);
            _hotHint.Text = format.Hint;
        }
        else if (segment != null)
        {
            _hotCaption.Text = _expanded ? "Hide" : "Show";
            _hotLabel.Text = _expanded ? "Less" : "All";
            _hotLabel.FontSize = 25;
            _hotLabel.Foreground = _theme!.Text;
            _hotHint.Text = $"{_plan?.All.Count} formats";
        }

        _idlePanel.BeginAnimation(OpacityProperty, Anim(segment == null ? 1 : 0, 110));
        _hotPanel.BeginAnimation(OpacityProperty, Anim(segment == null ? 0 : 1, 110));
        if (segment == null) return;
        segment.RaiseSelected();
        Announce(segment.SpokenName);
    }

    void RefreshMeta()
    {
        if (_plan == null || _theme == null) return;
        _fileMeta.Foreground = _notice != null ? _theme.Notice : _theme.SubtleText;
        _fileMeta.Text = _notice
            ?? (IsSticky ? "Click a format"
            : _batch.Count > 1 ? $"selected · {Palette.KindName(_plan.SourceKind)}"
            : _selectedCount > 1 ? $"1 of {_selectedCount} selected"
            : $"{_plan.SourceExt.ToUpperInvariant()} · {Palette.KindName(_plan.SourceKind)}");
    }

    void SetExpanded(bool expanded, bool animate)
    {
        if (_inner.Count > 0 && _plan is { HasMore: false }) expanded = false; // the outer ring would only repeat the inner one
        bool changed = _expanded != expanded;
        _expanded = expanded;
        foreach (var s in _inner) s.SetExpanded(expanded);

        if (!animate)
        {
            _outerLayer.BeginAnimation(OpacityProperty, null);
            _outerBackdrop.BeginAnimation(OpacityProperty, null);
            _outerLayer.Opacity = _outerBackdrop.Opacity = expanded ? 1 : 0;
            _outerLayer.Visibility = _outerBackdrop.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            foreach (var scale in new[] { _outerScale, _outerBackdropScale })
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = 1;
            }
            return;
        }
        if (!changed) return;

        double from = WheelLayout.InnerRim / WheelLayout.OuterRim;
        if (expanded)
        {
            Position(_center); // only now is the room for the outer ring needed
            _outerLayer.Visibility = _outerBackdrop.Visibility = Visibility.Visible;
            _outerLayer.BeginAnimation(OpacityProperty, Anim(1, 160));
            _outerBackdrop.BeginAnimation(OpacityProperty, Anim(1, 120));
            var grow = new DoubleAnimation(Motion ? 0.88 : 1, 1, Dur(240)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
            _outerScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _outerScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var disc = new DoubleAnimation(Motion ? from : 1, 1, Dur(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _outerBackdropScale.BeginAnimation(ScaleTransform.ScaleXProperty, disc);
            _outerBackdropScale.BeginAnimation(ScaleTransform.ScaleYProperty, disc);
            for (int i = 0; i < _outer.Count; i++) _outer[i].FadeIn(i * 10);
        }
        else
        {
            if (_hot?.Ring == WheelRing.Outer) SetHot(null);
            var hide = Anim(0, 140);
            hide.Completed += (_, _) =>
            {
                if (_expanded) return;
                _outerLayer.Visibility = _outerBackdrop.Visibility = Visibility.Collapsed;
            };
            _outerLayer.BeginAnimation(OpacityProperty, hide);
            _outerBackdrop.BeginAnimation(OpacityProperty, Anim(0, 140));
            _outerBackdropScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(from, 160));
            _outerBackdropScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(from, 160));
        }

        if (_hot?.IsMore == true) SetHot(_hot);
        GeometryChanged?.Invoke();
        RefreshPeers();
    }

    internal static Duration Dur(int ms) => new(TimeSpan.FromMilliseconds(Motion ? ms : 1));

    internal static DoubleAnimation Anim(double to, int ms, IEasingFunction? ease = null) =>
        new(to, Dur(ms)) { EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut } };

    // ------------------------------------------------------------------ accessibility

    /// <summary>The visible slices, in reading order.</summary>
    internal IEnumerable<WheelSegment> ShownSegments => !IsOpen ? [] : _expanded ? _inner.Concat(_outer) : _inner;

    internal WheelSegment? HotSegment => _hot;

    internal TextBlock Status => _status;

    void Announce(string text)
    {
        _status.Text = text;
        AutomationProperties.SetName(_status, text);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void Raise(AutomationEvents change)
    {
        if (AutomationPeer.ListenerExists(change))
            UIElementAutomationPeer.CreatePeerForElement(_stage)?.RaiseAutomationEvent(change);
    }

    /// <summary>The list of slices changed.</summary>
    void RefreshPeers()
    {
        if (UIElementAutomationPeer.FromElement(_stage) is not { } peer) return;
        peer.ResetChildrenCache();
        peer.InvalidatePeer();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WheelPeer(this);

    sealed class WheelPeer(WheelOverlay owner) : WindowAutomationPeer(owner)
    {
        protected override string GetNameCore() => "Convert Wheel";
        protected override List<AutomationPeer> GetChildrenCore()
        {
            var wheel = (WheelOverlay)Owner;
            return [CreatePeerForElement(wheel._stage), CreatePeerForElement(wheel._status)];
        }
    }

    sealed class WheelStage(WheelOverlay wheel) : Canvas
    {
        public WheelOverlay Wheel => wheel;
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        sealed class Peer(WheelStage owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
        {
            WheelOverlay Wheel => ((WheelStage)Owner).Wheel;
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
            protected override string GetClassNameCore() => "WheelSlices";
            protected override string GetNameCore() => "Formats";
            protected override string GetHelpTextCore() => "Arrow keys or numbers choose a format, Enter converts, Tab shows every format, Escape closes.";
            protected override bool IsControlElementCore() => true;
            protected override bool IsContentElementCore() => true;
            protected override List<AutomationPeer> GetChildrenCore() => Wheel.ShownSegments.Select(CreatePeerForElement).ToList();
            public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Selection ? this : base.GetPattern(pattern);
            public IRawElementProviderSimple[] GetSelection() =>
                Wheel.HotSegment is { } hot && CreatePeerForElement(hot) is { } peer ? [ProviderFromPeer(peer)] : [];
            public bool CanSelectMultiple => false;
            public bool IsSelectionRequired => false;
        }
    }
}
