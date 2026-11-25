using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ConvertWheel.Conversion;
using Path = System.Windows.Shapes.Path;

namespace ConvertWheel.UI;

/// <summary>One slice of the wheel: a format, or (with no format) the "All" slice.</summary>
internal sealed class WheelSegment : Canvas
{
    const double Gap = 5;
    const double BadgeSize = 11;

    readonly WheelOverlay _wheel;
    readonly WheelTheme _theme;
    readonly Canvas _tileLayer;
    readonly Path _tile;
    readonly Path? _strip;
    readonly StackPanel _panel = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _label;
    readonly TextBlock? _hint, _moreGlyph, _number;
    readonly Rect _numberBox = Rect.Empty;
    readonly ScaleTransform _scale;
    readonly Brush _hotFill;
    readonly Color _glow;
    readonly double _baseOpacity, _r1, _r2, _a0, _a1;

    public FormatInfo? Format { get; }
    public WheelRing Ring { get; }
    public int Index { get; }
    public bool IsMore => Format == null;
    public bool IsHot { get; private set; }

    public WheelSegment(WheelOverlay wheel, WheelTheme theme, Point center, FormatInfo? format, WheelRing ring, int index, int count, bool dimmed)
    {
        _wheel = wheel;
        _theme = theme;
        Format = format;
        Ring = ring;
        Index = index;
        bool outer = ring == WheelRing.Outer;
        double span = 360.0 / Math.Max(count, outer ? 2 : 1);
        (_a0, _a1) = (index * span - span / 2, index * span + span / 2);
        (_r1, _r2) = outer ? (WheelLayout.R3, WheelLayout.R4) : (WheelLayout.R1, WheelLayout.R2);
        _scale = new ScaleTransform(1, 1, center.X, center.Y);
        _hotFill = theme.HotFill(format?.Kind);
        _glow = (format == null ? Palette.More : Palette.Accent(format.Kind)).Item1;

        _tileLayer = new Canvas { Opacity = theme.TileOpacity };
        var shape = WheelGeometry.Sector(center, _r1 + 3, _r2 - 3, _a0, _a1, Gap + 6);
        if (theme.TileEdge != null) _tileLayer.Children.Add(Tile(shape, theme.TileEdge, 9));
        _tile = Tile(shape, theme.Tile, 6); // a round-joined stroke in the fill colour gives soft corners
        _tileLayer.Children.Add(_tile);
        if (format != null && !theme.HighContrast)
        {
            _strip = new Path
            {
                Data = WheelGeometry.Arc(center, _r2 - 9, _a0, _a1, Gap + 22),
                Stroke = theme.Accent(format.Kind),
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.85,
            };
            _tileLayer.Children.Add(_strip);
        }
        Children.Add(_tileLayer);

        if (format == null)
        {
            _moreGlyph = Text("\uE712", 16, theme.Text);
            _moreGlyph.FontFamily = Palette.IconFont;
            _panel.Children.Add(_moreGlyph);
        }
        _label = Text(format?.Label ?? "All", outer ? 13 : 17, theme.Text);
        _label.FontWeight = FontWeights.SemiBold;
        _panel.Children.Add(_label);
        if (format != null)
        {
            _hint = Text(format.Hint, 11, theme.SubtleText);
            _panel.Children.Add(_hint);
        }
        Children.Add(_panel);

        if (!outer && index < 9)
        {
            _number = Text((index + 1).ToString(), BadgeSize, theme.SubtleText);
            _number.Measure(Infinite);
            Centre(_number, WheelGeometry.Polar(center, _r1 + 14, (_a0 + _a1) / 2));
            Children.Add(_number);
            _numberBox = new Rect(GetLeft(_number), GetTop(_number) + 2, _number.DesiredSize.Width, _number.DesiredSize.Height - 4);
            _numberBox.Inflate(1, 0);
        }

        RenderTransform = _scale;
        _baseOpacity = dimmed ? 0.42 : 1;
        Opacity = _baseOpacity;
    }

    static readonly Size Infinite = new(double.PositiveInfinity, double.PositiveInfinity);

    static Path Tile(Geometry shape, Brush brush, double stroke) => new()
    {
        Data = shape,
        Fill = brush,
        Stroke = brush,
        StrokeThickness = stroke,
        StrokeLineJoin = PenLineJoin.Round,
    };

    static TextBlock Text(string text, double size, Brush brush) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static void Centre(UIElement element, Point at)
    {
        SetLeft(element, at.X - element.DesiredSize.Width / 2);
        SetTop(element, at.Y - element.DesiredSize.Height / 2);
    }

    // ------------------------------------------------------------------ text

    /// <summary>The largest label size at which everything fits inside the slice, or null.</summary>
    public double? FittingSize(Point center, double? hintSize, double smallest)
    {
        ShowHint(hintSize);
        double full = _label.FontSize;
        for (double size = full; size >= smallest; size -= 0.5)
        {
            _label.FontSize = size;
            bool fits = PlaceText(center);
            _label.FontSize = full;
            if (fits) return size;
        }
        return null;
    }

    /// <summary>Lays the text out inside the slice, shortening it if it still doesn't fit.</summary>
    public void LayOut(Point center, double? hintSize, double size)
    {
        ShowHint(hintSize);
        _label.FontSize = size;
        _panel.MaxWidth = double.PositiveInfinity;
        if (PlaceText(center)) return;
        for (double width = 120; width >= 24; width -= 4)
        {
            _panel.MaxWidth = width;
            if (PlaceText(center)) return;
        }
    }

    void ShowHint(double? size)
    {
        if (_hint == null) return;
        _hint.Visibility = size != null ? Visibility.Visible : Visibility.Collapsed;
        _hint.FontSize = size ?? 11;
    }

    bool PlaceText(Point center)
    {
        _panel.InvalidateMeasure();
        _panel.Measure(Infinite);
        double inside = _r1 + 5, outside = _r2 - 11.5; // clear of the coloured edge
        double best = (_r1 + _r2) / 2, middle = (_a0 + _a1) / 2;
        for (int i = 0; i <= 40; i++)
        {
            double r = best + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 1.5;
            var at = WheelGeometry.Polar(center, r, middle);
            var box = new Rect(at.X - _panel.DesiredSize.Width / 2, at.Y - _panel.DesiredSize.Height / 2, _panel.DesiredSize.Width, _panel.DesiredSize.Height);
            if (box.Height > 12) box.Inflate(1, -2); // the line spacing above and below the letters is free to use
            if (_numberBox.IntersectsWith(box)) continue;
            if (!WheelLayout.Fits(center, inside, outside, _a0, _a1, Gap / 2 + 2, box)) continue;
            Centre(_panel, at);
            return true;
        }
        Centre(_panel, WheelGeometry.Polar(center, best, middle));
        return false;
    }

    /// <summary>The text's box on the stage, for checking the layout.</summary>
    internal Rect TextBox => new(GetLeft(_panel), GetTop(_panel), _panel.DesiredSize.Width, _panel.DesiredSize.Height);

    internal (double R1, double R2, double A0, double A1) Wedge => (_r1, _r2, _a0, _a1);

    // ------------------------------------------------------------------ states

    public void SetExpanded(bool expanded)
    {
        if (!IsMore) return;
        _label.Text = expanded ? "Less" : "All";
        _moreGlyph!.Text = expanded ? "\uE73F" : "\uE712";
    }

    public void SetHot(bool hot)
    {
        IsHot = hot;
        _tile.Fill = _tile.Stroke = hot ? _hotFill : _theme.Tile;
        _tileLayer.Opacity = hot ? 1 : _theme.TileOpacity;
        _tileLayer.Effect = hot && !_theme.HighContrast
            ? new DropShadowEffect { Color = _glow, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.75 }
            : null;
        if (_strip != null) _strip.Visibility = hot ? Visibility.Hidden : Visibility.Visible;
        _label.Foreground = hot ? _theme.HotText : _theme.Text;
        if (_moreGlyph != null) _moreGlyph.Foreground = _label.Foreground;
        if (_hint != null) _hint.Foreground = hot ? _theme.HotText : _theme.SubtleText;
        if (_number != null) _number.Foreground = hot ? _theme.HotText : _theme.SubtleText;

        double target = hot ? (Ring == WheelRing.Outer ? 1.05 : 1.065) : 1;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, WheelOverlay.Anim(target, 120));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, WheelOverlay.Anim(target, 120));
    }

    public void FadeIn(int delayMs)
    {
        if (!WheelOverlay.Motion) return;
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, _baseOpacity, WheelOverlay.Dur(140))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        });
    }

    public void Pulse()
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, WheelOverlay.Anim(1.14, 150));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, WheelOverlay.Anim(1.14, 150));
    }

    // ------------------------------------------------------------------ accessibility

    public string SpokenName => Format is { } f ? $"{f.Label}, {f.Hint}" : _wheel.IsExpanded ? "Fewer formats" : "All formats";

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    public void RaiseSelected()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected))
            UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
    }

    sealed class Peer(WheelSegment owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider, ISelectionItemProvider
    {
        WheelSegment Segment => (WheelSegment)Owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "WheelSlice";
        protected override string GetNameCore() => Segment.SpokenName;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override int GetPositionInSetCore() => Segment.Index + 1;
        protected override int GetSizeOfSetCore() => Segment._wheel.CountIn(Segment.Ring);
        protected override string GetAcceleratorKeyCore() => Segment is { Ring: WheelRing.Inner, Index: < 9 } s ? (s.Index + 1).ToString() : "";
        protected override bool IsOffscreenCore() => !Segment._wheel.IsShowing(Segment);

        protected override Rect GetBoundingRectangleCore()
        {
            if (!Segment._wheel.IsShowing(Segment) || PresentationSource.FromVisual(Segment) == null) return Rect.Empty;
            var bounds = Segment._tile.Data.Bounds;
            return new Rect(Segment.PointToScreen(bounds.TopLeft), Segment.PointToScreen(bounds.BottomRight));
        }

        public override object? GetPattern(PatternInterface pattern) =>
            pattern is PatternInterface.Invoke or PatternInterface.SelectionItem ? this : base.GetPattern(pattern);

        // Clients wait for these calls, so they only queue the work.
        public void Invoke() => Segment.Dispatcher.BeginInvoke(() => Segment._wheel.Choose(Segment));
        public void Select() => Segment.Dispatcher.BeginInvoke(() => Segment._wheel.Highlight(Segment));
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { }
        public bool IsSelected => Segment.IsHot;
        public IRawElementProviderSimple? SelectionContainer => null;
    }
}
