using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ConvertWheel.Conversion;
using ConvertWheel.Native;
using Path = System.Windows.Shapes.Path;

namespace ConvertWheel.UI;

/// <summary>
/// The radial menu. It is a click-through, non-activating, topmost layered window: all input is
/// read from the global hooks and the cursor position, so it never steals focus from Explorer.
/// </summary>
internal sealed class WheelOverlay : Window
{
    const double HubR = 58, R1 = 68, R2 = 178, R3 = 190, R4 = 262;
    const double Extent = R4 + 44;
    const double Side = Extent * 2;
    const double Gap = 5;
    const double DeadZone = 22;
    static readonly Point C = new(Extent, Extent);

    readonly nint _hwnd;
    readonly Canvas _stage = new() { Width = Side, Height = Side };
    readonly ScaleTransform _stageScale = new(1, 1, Extent, Extent);
    readonly Canvas _innerLayer = new();
    readonly Canvas _outerLayer = new();
    readonly ScaleTransform _outerScale = new(1, 1, Extent, Extent);
    readonly Canvas _outerBackdrop;
    readonly ScaleTransform _outerBackdropScale = new(1, 1, Extent, Extent);

    readonly Image _thumb = new() { Width = 46, Height = 46, Stretch = Stretch.Uniform };
    readonly Border _badge = new() { Width = 42, Height = 42, CornerRadius = new CornerRadius(11) };
    readonly TextBlock _badgeText = Text(11.5, FontWeights.Bold, "#FFFFFF");
    readonly TextBlock _fileName = Text(11, FontWeights.SemiBold, "#F2F2F7");
    readonly TextBlock _fileMeta = Text(9.5, FontWeights.Normal, "#8C8C9A");
    readonly TextBlock _hotCaption = Text(9.5, FontWeights.Normal, "#9A9AA8");
    readonly TextBlock _hotLabel = Text(25, FontWeights.Bold, "#FFFFFF");
    readonly TextBlock _hotHint = Text(9.5, FontWeights.Normal, "#B4B4C0");
    readonly StackPanel _idlePanel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly StackPanel _hotPanel = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0 };

    readonly List<Segment> _inner = [];
    readonly List<Segment> _outer = [];
    ConversionPlan? _plan;
    string? _notice;
    Segment? _hot;
    bool _expanded;
    double _scale = 1;
    POINT _center;
    POINT? _restoreCursor;
    long _moreHoverStart;

    public event Action<ConversionPlan, FormatInfo>? ConvertRequested;
    public event Action? Dismissed;
    public event Action? GeometryChanged;

    public bool IsOpen { get; private set; }

    /// <summary>False while the trigger is held (direction picks, release converts); true once released (click to pick).</summary>
    public bool IsSticky { get; private set; }

    public POINT CenterPx => _center;
    public int HitRadiusPx => (int)Math.Ceiling(((_expanded ? R4 : R2) + 10) * _scale);

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

        _stage.RenderTransform = _stageScale;
        _outerLayer.RenderTransform = _outerScale;
        _outerBackdrop = Disc(R4 + 10, "#E6141419", "#DC0D0D11");
        _outerBackdrop.RenderTransform = _outerBackdropScale;

        _stage.Children.Add(_outerBackdrop);
        _stage.Children.Add(_outerLayer);
        _stage.Children.Add(Disc(R2 + 9, "#F21A1A21", "#EB0F0F13"));
        _stage.Children.Add(_innerLayer);
        _stage.Children.Add(BuildHub());
        Content = _stage;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT);
    }

    // ------------------------------------------------------------------ building

    static TextBlock Text(double size, FontWeight weight, string color) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = Palette.Solid(color),
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

    /// <summary>A dark glass disc with a soft drop shadow (a gradient, far cheaper than a blur effect).</summary>
    static Canvas Disc(double radius, string inner, string outer)
    {
        var canvas = new Canvas();
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

        var disc = new Ellipse
        {
            Fill = Palette.Freeze(new RadialGradientBrush(Palette.Hex(inner), Palette.Hex(outer))),
            Stroke = Palette.Freeze(new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(14, 255, 255, 255), 90)),
            StrokeThickness = 1.2,
        };
        Place(disc, radius);
        canvas.Children.Add(shadow);
        canvas.Children.Add(disc);
        return canvas;
    }

    UIElement BuildHub()
    {
        var hub = new Grid();
        Place(hub, HubR);
        hub.Children.Add(new Ellipse
        {
            Fill = Palette.Freeze(new RadialGradientBrush(Palette.Hex("#2E2E39"), Palette.Hex("#1A1A21")) { GradientOrigin = new Point(0.5, 0.25) }),
            Stroke = Palette.Freeze(new LinearGradientBrush(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90)),
            StrokeThickness = 1.2,
        });

        _badge.Child = _badgeText;
        _badgeText.VerticalAlignment = VerticalAlignment.Center;
        _thumb.Clip = new RectangleGeometry(new Rect(0, 0, 46, 46), 9, 9);
        var image = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 0, 5) };
        image.Children.Add(_badge);
        image.Children.Add(_thumb);
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);

        _fileName.MaxWidth = 90;
        _fileMeta.MaxWidth = 92;
        _idlePanel.Children.Add(image);
        _idlePanel.Children.Add(_fileName);
        _idlePanel.Children.Add(_fileMeta);

        _hotLabel.Margin = new Thickness(0, -2, 0, -1);
        _hotLabel.MaxWidth = 104;
        _hotPanel.Children.Add(_hotCaption);
        _hotPanel.Children.Add(_hotLabel);
        _hotPanel.Children.Add(_hotHint);

        hub.Children.Add(_idlePanel);
        hub.Children.Add(_hotPanel);
        return hub;
    }

    void BuildSegments(ConversionPlan plan, bool dimmed)
    {
        _inner.Clear();
        _outer.Clear();
        _innerLayer.Children.Clear();
        _outerLayer.Children.Clear();

        if (plan.Suggested.Count > 0)
        {
            var items = plan.Suggested.Select(f => (FormatInfo?)f).Append(null).ToList();
            double span = 360.0 / items.Count;
            for (int i = 0; i < items.Count; i++)
            {
                var seg = new Segment(items[i], i * span - span / 2, i * span + span / 2, R1, R2, outer: false,
                    badge: i < 9 ? i + 1 : null, dimmed: dimmed && items[i] != null, expanded: false);
                _inner.Add(seg);
                _innerLayer.Children.Add(seg.Root);
            }
        }

        double outerSpan = 360.0 / Math.Max(plan.All.Count, 2);
        for (int i = 0; i < plan.All.Count; i++)
        {
            var seg = new Segment(plan.All[i], i * outerSpan - outerSpan / 2, i * outerSpan + outerSpan / 2, R3, R4, outer: true,
                badge: null, dimmed: dimmed, expanded: false);
            _outer.Add(seg);
            _outerLayer.Children.Add(seg.Root);
        }
    }

    // ------------------------------------------------------------------ opening & closing

    public void Open(ConversionPlan plan, POINT cursor, bool startExpanded, string? notice)
    {
        _plan = plan;
        _notice = notice;
        _hot = null;
        _moreHoverStart = 0;
        IsSticky = false;
        BuildSegments(plan, dimmed: notice != null);

        var accent = Palette.Accent(plan.SourceKind);
        _badge.Background = Palette.Gradient(accent);
        _badge.Opacity = 1;
        _badgeText.Text = plan.SourceExt.ToUpperInvariant();
        _thumb.BeginAnimation(OpacityProperty, null);
        _thumb.Source = null;
        _fileName.Text = System.IO.Path.GetFileName(plan.SourcePath);
        _idlePanel.Opacity = 1;
        _hotPanel.Opacity = 0;
        RefreshMeta();

        SetExpanded(startExpanded || _inner.Count == 0, animate: false);
        Position(cursor);

        _stage.BeginAnimation(OpacityProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _stage.Opacity = 0;
        _stageScale.ScaleX = _stageScale.ScaleY = 0.82;

        if (!IsVisible) Show();
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

        _stage.BeginAnimation(OpacityProperty, Anim(1, 120));
        var pop = Anim(1, 260, new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut });
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        for (int i = 0; i < _inner.Count; i++) _inner[i].FadeIn(i * 18);
        if (_expanded) for (int i = 0; i < _outer.Count; i++) _outer[i].FadeIn(60 + i * 8);

        IsOpen = true;
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
        GeometryChanged?.Invoke();
    }

    void Position(POINT cursor)
    {
        var (work, scale) = Win32.MonitorAt(cursor);
        _scale = scale;
        int size = (int)Math.Round(Side * scale);
        int reach = (int)Math.Ceiling((R4 + 16) * scale);
        _center = new POINT(Clamp(cursor.X, work.Left, work.Right, reach), Clamp(cursor.Y, work.Top, work.Bottom, reach));

        // Near a screen edge the wheel shifts to stay fully visible; move the cursor with it so
        // directions still line up, and put it back afterwards.
        _restoreCursor = null;
        if (_center.X != cursor.X || _center.Y != cursor.Y)
        {
            _restoreCursor = cursor;
            Win32.SetCursorPos(_center.X, _center.Y);
        }

        // Twice: the first move may cross monitors and trigger a DPI rescale that shifts the window.
        for (int i = 0; i < 2; i++)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _center.X - size / 2, _center.Y - size / 2, size, size, Win32.SWP_NOACTIVATE);

        static int Clamp(int value, int lo, int hi, int reach) =>
            hi - lo < reach * 2 ? (lo + hi) / 2 : Math.Clamp(value, lo + reach, hi - reach);
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

    public void ReleaseTrigger()
    {
        if (!IsOpen || IsSticky) return;
        if (_hot?.Format != null)
        {
            Commit(_hot);
            return;
        }
        if (_hot?.IsMore == true) SetExpanded(true, animate: true);
        IsSticky = true; // released in the middle: keep the wheel up and let the user click
        RefreshMeta();
        GeometryChanged?.Invoke();
    }

    public void ClickAt(POINT pt)
    {
        if (!IsOpen) return;
        var hit = HitTest(pt);
        if (hit?.Format != null) Commit(hit);
        else if (hit?.IsMore == true)
        {
            IsSticky = true;
            SetExpanded(!_expanded, animate: true);
        }
        else Close(restoreCursor: true);
    }

    public void CommitHighlighted()
    {
        if (_hot?.Format != null) Commit(_hot);
        else if (_hot?.IsMore == true) ToggleExpanded();
    }

    public void CommitIndex(int index)
    {
        if (!IsOpen || index < 0 || index >= _inner.Count) return;
        if (_inner[index].Format != null) Commit(_inner[index]);
        else ToggleExpanded();
    }

    public void ToggleExpanded()
    {
        if (IsOpen && _inner.Count > 0) SetExpanded(!_expanded, animate: true);
    }

    public void Cancel(bool restoreCursor = true) => Close(restoreCursor);

    void Commit(Segment segment)
    {
        var plan = _plan!;
        var format = segment.Format!;
        segment.Pulse();
        Close(restoreCursor: true);
        ConvertRequested?.Invoke(plan, format);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (!IsOpen) return;
        Win32.GetCursorPos(out var p);
        var hit = IsSticky ? HitTest(p) : Directional(p);
        if (hit != _hot) SetHot(hit);

        // Resting on "All" while holding opens the full ring without releasing.
        if (!IsSticky && hit?.IsMore == true && !_expanded)
        {
            if (_moreHoverStart == 0) _moreHoverStart = Environment.TickCount64;
            else if (Environment.TickCount64 - _moreHoverStart > 380) SetExpanded(true, animate: true);
        }
        else
        {
            _moreHoverStart = 0;
        }
    }

    (double distance, double angle) Polar(POINT p)
    {
        double dx = (p.X - _center.X) / _scale, dy = (p.Y - _center.Y) / _scale;
        return (Math.Sqrt(dx * dx + dy * dy), (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360);
    }

    Segment? Directional(POINT p)
    {
        var (distance, angle) = Polar(p);
        if (distance < DeadZone) return null;
        if (_expanded && (distance > R2 + 6 || _inner.Count == 0)) return ByAngle(_outer, angle);
        return ByAngle(_inner, angle);
    }

    Segment? HitTest(POINT p)
    {
        var (distance, angle) = Polar(p);
        if (distance >= R1 - 3 && distance <= R2 + 4) return ByAngle(_inner, angle);
        if (_expanded && distance >= R3 - 4 && distance <= R4 + 6) return ByAngle(_outer, angle);
        return null;
    }

    static Segment? ByAngle(List<Segment> segments, double angle) =>
        segments.FirstOrDefault(s => (angle - s.A0 + 360) % 360 < s.A1 - s.A0);

    void SetHot(Segment? segment)
    {
        _hot?.SetHot(false);
        _hot = segment;
        segment?.SetHot(true);

        if (segment?.Format is { } format)
        {
            _hotCaption.Text = "Convert to";
            _hotLabel.Text = format.Label;
            _hotLabel.Foreground = Palette.Gradient(Lighten(Palette.Accent(format.Kind)));
            _hotHint.Text = format.Hint;
        }
        else if (segment?.IsMore == true)
        {
            _hotCaption.Text = _expanded ? "Hide" : "Show";
            _hotLabel.Text = _expanded ? "Less" : "All";
            _hotLabel.Foreground = Palette.Solid("#F2F2F7");
            _hotHint.Text = $"{_plan?.All.Count} formats";
        }

        _idlePanel.BeginAnimation(OpacityProperty, Anim(segment == null ? 1 : 0, 110));
        _hotPanel.BeginAnimation(OpacityProperty, Anim(segment == null ? 0 : 1, 110));
    }

    static (Color, Color) Lighten((Color a, Color b) c) =>
        (Mix(c.a, Colors.White, 0.35), Mix(c.b, Colors.White, 0.2));

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    void RefreshMeta()
    {
        if (_plan == null) return;
        if (_notice != null)
        {
            _fileMeta.Text = _notice;
            _fileMeta.Foreground = Palette.Solid("#FBBF24");
        }
        else
        {
            _fileMeta.Text = IsSticky ? "Click a format" : $"{_plan.SourceExt.ToUpperInvariant()} · {Palette.KindName(_plan.SourceKind)}";
            _fileMeta.Foreground = Palette.Solid("#8C8C9A");
        }
    }

    void SetExpanded(bool expanded, bool animate)
    {
        bool changed = _expanded != expanded;
        _expanded = expanded;
        foreach (var s in _inner) s.SetMoreState(expanded);

        if (!animate)
        {
            _outerLayer.BeginAnimation(OpacityProperty, null);
            _outerBackdrop.BeginAnimation(OpacityProperty, null);
            _outerLayer.Opacity = _outerBackdrop.Opacity = expanded ? 1 : 0;
            _outerLayer.Visibility = _outerBackdrop.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            _outerScale.ScaleX = _outerScale.ScaleY = 1;
            _outerBackdropScale.ScaleX = _outerBackdropScale.ScaleY = 1;
        }
        else if (changed)
        {
            double from = (R2 + 9) / (R4 + 10);
            if (expanded)
            {
                _outerLayer.Visibility = _outerBackdrop.Visibility = Visibility.Visible;
                _outerLayer.BeginAnimation(OpacityProperty, Anim(1, 160));
                _outerBackdrop.BeginAnimation(OpacityProperty, Anim(1, 120));
                var grow = new DoubleAnimation(0.88, 1, Dur(240)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
                _outerScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                _outerScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                var disc = new DoubleAnimation(from, 1, Dur(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                _outerBackdropScale.BeginAnimation(ScaleTransform.ScaleXProperty, disc);
                _outerBackdropScale.BeginAnimation(ScaleTransform.ScaleYProperty, disc);
                for (int i = 0; i < _outer.Count; i++) _outer[i].FadeIn(i * 10);
            }
            else
            {
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
        }

        if (_hot?.IsMore == true) SetHot(_hot);
        if (changed) GeometryChanged?.Invoke();
    }

    static Duration Dur(int ms) => new(TimeSpan.FromMilliseconds(ms));

    internal static DoubleAnimation Anim(double to, int ms, IEasingFunction? ease = null) =>
        new(to, Dur(ms)) { EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut } };

    // ------------------------------------------------------------------ segment

    sealed class Segment
    {
        static readonly SolidColorBrush NormalFill = Palette.Solid("#2A2A34");
        static readonly SolidColorBrush LabelNormal = Palette.Solid("#ECECF2");
        static readonly SolidColorBrush HintNormal = Palette.Solid("#8B8B9A");
        static readonly SolidColorBrush HintHot = Palette.Solid("#EAFFFFFF");

        readonly Canvas _tileLayer = new() { Opacity = 0.92 };
        readonly Path _tile;
        readonly Path? _strip;
        readonly TextBlock _label;
        readonly TextBlock? _hint;
        readonly TextBlock? _moreGlyph;
        readonly ScaleTransform _scale = new(1, 1, C.X, C.Y);
        readonly Brush _hotFill;
        readonly Color _glow;
        readonly double _baseOpacity;
        readonly bool _outer;

        public FormatInfo? Format { get; }
        public bool IsMore => Format == null;
        public double A0 { get; }
        public double A1 { get; }
        public Canvas Root { get; } = new();

        public Segment(FormatInfo? format, double a0, double a1, double r1, double r2, bool outer, int? badge, bool dimmed, bool expanded)
        {
            Format = format;
            A0 = a0;
            A1 = a1;
            _outer = outer;
            var colors = format == null ? Palette.More : Palette.Accent(format.Kind);
            _hotFill = Palette.Gradient(colors);
            _glow = colors.Item1;

            _tile = new Path
            {
                Data = WheelGeometry.Sector(C, r1 + 3, r2 - 3, a0, a1, Gap + 6),
                Fill = NormalFill,
                Stroke = NormalFill,
                StrokeThickness = 6, // a round-joined stroke in the fill colour gives soft corners
                StrokeLineJoin = PenLineJoin.Round,
            };
            _tileLayer.Children.Add(_tile);
            if (format != null)
            {
                _strip = new Path
                {
                    Data = WheelGeometry.Arc(C, r2 - 9, a0, a1, Gap + 22),
                    Stroke = _hotFill,
                    StrokeThickness = 2.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = 0.85,
                };
                _tileLayer.Children.Add(_strip);
            }
            Root.Children.Add(_tileLayer);

            double mid = (a0 + a1) / 2;
            double labelR = r1 + (r2 - r1) * (outer ? 0.5 : 0.53);
            double arcLength = labelR * (a1 - a0) * Math.PI / 180;
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            if (format == null)
            {
                _moreGlyph = new TextBlock
                {
                    Text = "",
                    FontFamily = Palette.IconFont,
                    FontSize = 16,
                    Foreground = LabelNormal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                panel.Children.Add(_moreGlyph);
            }

            _label = new TextBlock
            {
                Text = format?.Label ?? "All",
                FontSize = outer ? (arcLength < 52 ? 11 : 12.5) : 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = LabelNormal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            panel.Children.Add(_label);

            if (format != null && arcLength >= (outer ? 66 : 50))
            {
                _hint = new TextBlock
                {
                    Text = format.Hint,
                    FontSize = outer ? 9 : 10.5,
                    Foreground = HintNormal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                panel.Children.Add(_hint);
            }

            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var at = WheelGeometry.Polar(C, labelR, mid);
            Canvas.SetLeft(panel, at.X - panel.DesiredSize.Width / 2);
            Canvas.SetTop(panel, at.Y - panel.DesiredSize.Height / 2);
            Root.Children.Add(panel);

            if (badge is int n)
            {
                var number = new TextBlock { Text = n.ToString(), FontSize = 9, Foreground = Palette.Solid("#5E5E6E") };
                number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var bp = WheelGeometry.Polar(C, r1 + 14, mid);
                Canvas.SetLeft(number, bp.X - number.DesiredSize.Width / 2);
                Canvas.SetTop(number, bp.Y - number.DesiredSize.Height / 2);
                Root.Children.Add(number);
            }

            Root.RenderTransform = _scale;
            _baseOpacity = dimmed ? 0.42 : 1;
            Root.Opacity = _baseOpacity;
            SetMoreState(expanded);
        }

        public void SetMoreState(bool expanded)
        {
            if (!IsMore) return;
            _label.Text = expanded ? "Less" : "All";
            _moreGlyph!.Text = expanded ? "" : "";
        }

        public void SetHot(bool hot)
        {
            _tile.Fill = _tile.Stroke = hot ? _hotFill : NormalFill;
            _tileLayer.Opacity = hot ? 1 : 0.92;
            _tileLayer.Effect = hot ? new DropShadowEffect { Color = _glow, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.75 } : null;
            if (_strip != null) _strip.Visibility = hot ? Visibility.Hidden : Visibility.Visible;
            _label.Foreground = hot ? Brushes.White : LabelNormal;
            if (_moreGlyph != null) _moreGlyph.Foreground = _label.Foreground;
            if (_hint != null) _hint.Foreground = hot ? HintHot : HintNormal;

            double target = hot ? (_outer ? 1.05 : 1.065) : 1;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(target, 120));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(target, 120));
        }

        public void FadeIn(int delayMs)
        {
            Root.Opacity = 0;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, _baseOpacity, Dur(140))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
            });
        }

        public void Pulse()
        {
            var pulse = new DoubleAnimation(1.14, Dur(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
    }
}
