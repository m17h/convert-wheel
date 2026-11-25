using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ConvertWheel.Native;

namespace ConvertWheel.UI;

internal enum ToastGlyph { Working, Success, Warning, Error, Info }

/// <summary>Floating, non-activating cards above the taskbar, on the screen where each conversion was started.</summary>
internal sealed class ToastHost
{
    readonly Dictionary<nint, ToastWindow> _windows = [];

    /// <param name="place">A point on the screen to use; the main screen if null.</param>
    public ToastCard Push(string title, string subtitle, (Color, Color) accent, ToastGlyph glyph, POINT? place = null)
    {
        var point = place ?? default;
        var monitor = Win32.MonitorFromPoint(point, Win32.MONITOR_DEFAULTTONEAREST);
        if (!_windows.TryGetValue(monitor, out var window)) _windows[monitor] = window = new ToastWindow(point);
        var card = new ToastCard(title, subtitle, accent, glyph);
        window.Add(card);
        return card;
    }
}

/// <summary>The stack of cards on one screen.</summary>
internal sealed class ToastWindow : Window
{
    const int MaxVisible = 5, MinFinished = 3;

    readonly POINT _place;
    readonly nint _hwnd;
    readonly StackPanel _stack = new() { Margin = new Thickness(0, 0, 18, 18) };
    readonly TextBlock _more = new() { FontSize = 11.5, Foreground = Palette.Solid("#D0D0DA"), HorizontalAlignment = HorizontalAlignment.Right };
    readonly Border _moreChip;

    IEnumerable<ToastCard> Cards => _stack.Children.OfType<ToastCard>();

    public ToastWindow(POINT place)
    {
        _place = place;
        Title = "Convert Wheel notifications";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        FontFamily = Palette.UiFont;
        Content = _stack;
        _moreChip = new Border
        {
            Child = _more,
            Background = Palette.Solid("#F31B1B22"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
        };
        _stack.Children.Add(_moreChip);
        // Once the window itself has its new size, not only its contents.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        // Onto its screen before anything is laid out, so the size comes out in that screen's DPI.
        var (work, _) = Win32.MonitorAt(place);
        Win32.SetWindowPos(_hwnd, 0, work.Left, work.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public void Add(ToastCard card)
    {
        card.Removed += OnRemoved;
        card.Reappeared += OnReappeared;
        card.Changed += Arrange;
        Present(card);
    }

    void Present(ToastCard card)
    {
        _stack.Children.Insert(_stack.Children.Count - 1, card);
        Arrange();
        if (!IsVisible) Show();
        Reposition();
        Topmost = false;
        Topmost = true;
        card.AnimateIn();
    }

    void OnReappeared(ToastCard card)
    {
        if (!_stack.Children.Contains(card)) Present(card);
    }

    void OnRemoved(ToastCard card)
    {
        _stack.Children.Remove(card);
        Arrange();
        if (!Cards.Any()) Hide();
    }

    /// <summary>Only finished cards make room for new ones; running ones that don't fit wait out of sight.</summary>
    void Arrange()
    {
        var cards = Cards.Where(c => !c.IsLeaving).ToList();
        int busy = cards.Count(c => c.IsBusy);
        var finished = cards.Where(c => !c.IsBusy).OrderBy(c => c.NeedsAttention).ToList(); // plain successes go first
        int surplus = finished.Count - Math.Max(MinFinished, MaxVisible - busy);
        for (int i = 0; i < surplus; i++)
        {
            cards.Remove(finished[i]);
            finished[i].DismissNow(); // calls back here, harmlessly: it's leaving already
        }

        int room = MaxVisible - cards.Count(c => !c.IsBusy), hidden = 0;
        foreach (var card in cards)
        {
            bool show = !card.IsBusy || room-- > 0;
            card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) hidden++;
        }
        _more.Text = hidden == 1 ? "1 more conversion" : $"{hidden} more conversions";
        _moreChip.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Bottom right of the screen's work area, in real pixels (each screen has its own scale).</summary>
    void Reposition()
    {
        if (!GetWindowRect(_hwnd, out var rect)) return;
        var (work, _) = Win32.MonitorAt(_place);
        int x = work.Right - (rect.Right - rect.Left), y = Math.Max(work.Top, work.Bottom - (rect.Bottom - rect.Top));
        if (x != rect.Left || y != rect.Top) Win32.SetWindowPos(_hwnd, 0, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | SWP_NOZORDER);
    }

    const uint SWP_NOZORDER = 0x4;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT rect);
}

/// <summary>A small text button that works by keyboard and screen reader, but doesn't take the focus when clicked.</summary>
internal sealed class ToastButton : Button
{
    static readonly Style ButtonStyle = MakeStyle();

    public ToastButton(string name, object content)
    {
        Style = ButtonStyle;
        Content = content;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        PreviewMouseLeftButtonDown += (_, _) => Focusable = false;
        PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(() => Focusable = true);
        LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(() => Focusable = true);
    }

    static Style MakeStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Chip");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ToastButton)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Solid("#1FFFFFFF"), "Chip"));
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Solid("#A5B4FC"), "Chip"));
        template.Triggers.Add(hover);
        template.Triggers.Add(focus);

        var style = new Style(typeof(ToastButton)) { Setters = { new Setter(OverridesDefaultStyleProperty, true), new Setter(TemplateProperty, template) } };
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(FontFamilyProperty, Palette.UiFont));
        style.Setters.Add(new Setter(ForegroundProperty, Palette.Solid("#A5B4FC")));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(7, 3, 7, 3)));
        style.Seal();
        return style;
    }
}

internal sealed class ToastCard : Border
{
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Solid("#F5F5FA"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _subtitle = new() { FontSize = 11.5, Foreground = Palette.Solid("#A3A3B2"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = 3 * 16, Margin = new Thickness(0, 1, 0, 0) };
    readonly TextBlock _glyph = new() { FontFamily = Palette.IconFont, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly RotateTransform _spin = new(0, 7.5, 7.5);
    readonly Border _circle = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), VerticalAlignment = VerticalAlignment.Top };
    readonly Grid _track = new() { Height = 4, Margin = new Thickness(0, 9, 0, 1), Visibility = Visibility.Collapsed, ClipToBounds = true };
    readonly Border _fill = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TranslateTransform _fillShift = new();
    readonly WrapPanel _actions = new() { Margin = new Thickness(-8, 6, 0, -4), Visibility = Visibility.Collapsed };
    readonly TranslateTransform _slide = new();
    DispatcherTimer? _timer;
    double? _fraction;
    bool _indeterminate, _removing;
    string? _details;

    public event Action<ToastCard>? Removed, Reappeared;
    /// <summary>It started or stopped being busy.</summary>
    public event Action? Changed;

    public bool IsBusy { get; private set; }
    public bool NeedsAttention { get; private set; }
    public bool IsLeaving => _removing;

    public ToastCard(string title, string subtitle, (Color, Color) accent, ToastGlyph glyph)
    {
        Background = Palette.Solid("#F31B1B22");
        BorderBrush = Palette.Solid("#2CFFFFFF");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(14);
        Padding = new Thickness(14, 12, 10, 12);
        Margin = new Thickness(0, 10, 0, 0);
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 3, Opacity = 0.45, Direction = 270 };
        RenderTransform = _slide;
        Opacity = 0;

        _glyph.RenderTransform = _spin;
        _glyph.Width = _glyph.Height = 15;
        _glyph.TextAlignment = TextAlignment.Center;
        _circle.Child = _glyph;

        _track.Children.Add(new Border { Background = Palette.Solid("#24FFFFFF"), CornerRadius = new CornerRadius(2) });
        _fill.RenderTransform = _fillShift;
        _track.Children.Add(_fill);
        _track.SizeChanged += (_, _) => ApplyProgress();

        var text = new StackPanel { Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);
        text.Children.Add(_track);
        text.Children.Add(_actions);

        var close = new ToastButton("Close", new TextBlock { Text = "", FontFamily = Palette.IconFont, FontSize = 10 })
        {
            Foreground = Palette.Solid("#A0FFFFFF"),
            Padding = new Thickness(5),
            VerticalAlignment = VerticalAlignment.Top,
        };
        close.MouseEnter += (_, _) => close.Foreground = Brushes.White;
        close.MouseLeave += (_, _) => close.Foreground = Palette.Solid("#A0FFFFFF");
        close.Click += (_, _) => DismissNow();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(_circle);
        grid.Children.Add(text);
        grid.Children.Add(close);
        Child = grid;

        MouseEnter += (_, _) => _timer?.Stop();
        MouseLeave += (_, _) => { if (!_removing && !IsKeyboardFocusWithin) _timer?.Start(); };
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (IsKeyboardFocusWithin) _timer?.Stop();
            else if (!_removing && !IsMouseOver) _timer?.Start();
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_title, System.Windows.Automation.AutomationLiveSetting.Polite);

        Update(title, subtitle);
        SetState(glyph, accent);
    }

    public void Update(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        _title.ToolTip = title;
        SetTip();
        // Screen readers announce the card as one sentence when it changes.
        System.Windows.Automation.AutomationProperties.SetName(_title, $"{title}. {subtitle}");
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(_title) is { } peer)
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    void SetTip()
    {
        if (string.IsNullOrEmpty(_subtitle.Text)) return;
        var text = _details == null ? _subtitle.Text : $"{_subtitle.Text}\n\n{(_details.Length > 600 ? _details[..600] + "…" : _details)}";
        _subtitle.ToolTip = new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 } };
    }

    /// <param name="reappear">Bring the card back if it was closed while the work was still going on.</param>
    public void SetState(ToastGlyph glyph, (Color, Color) accent, bool reappear = true)
    {
        bool wasBusy = IsBusy;
        IsBusy = glyph == ToastGlyph.Working;
        NeedsAttention = glyph is ToastGlyph.Warning or ToastGlyph.Error;
        if (wasBusy && !IsBusy && _removing && reappear)
        {
            _removing = false;
            Reappeared?.Invoke(this);
            AnimateIn(); // it may not have quite gone yet
        }
        if (wasBusy != IsBusy) Changed?.Invoke();
        _circle.Background = Palette.Gradient(accent);
        _fill.Background = Palette.Gradient(accent);
        _glyph.Text = glyph switch
        {
            ToastGlyph.Working => "",
            ToastGlyph.Success => "",
            ToastGlyph.Error => "",
            ToastGlyph.Warning => "\uE7BA",
            _ => "",
        };
        if (glyph == ToastGlyph.Working)
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <param name="fraction">0..1, or null for an indeterminate bar.</param>
    public void SetProgress(double? fraction)
    {
        if (!IsBusy) return; // a late report after the end
        _track.Visibility = Visibility.Visible;
        _fraction = fraction;
        ApplyProgress();
    }

    public void HideProgress()
    {
        _track.Visibility = Visibility.Collapsed;
        _fillShift.BeginAnimation(TranslateTransform.XProperty, null);
        _indeterminate = false;
    }

    void ApplyProgress()
    {
        double width = _track.ActualWidth;
        if (width <= 0) return;
        if (_fraction is double f)
        {
            if (_indeterminate)
            {
                _fillShift.BeginAnimation(TranslateTransform.XProperty, null);
                _fillShift.X = 0;
                _indeterminate = false;
            }
            if (double.IsNaN(_fill.Width)) _fill.Width = 0; // never shown so far (a card that was waiting out of sight)
            _fill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(4, width * f), TimeSpan.FromMilliseconds(180)));
        }
        else if (!_indeterminate)
        {
            _indeterminate = true;
            _fill.BeginAnimation(WidthProperty, null);
            _fill.Width = width * 0.32;
            _fillShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-width * 0.32, width, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    public void AddAction(string label, Action action)
    {
        var button = new ToastButton(label, label);
        button.Click += (_, _) => action();
        _actions.Children.Add(button);
        _actions.Visibility = Visibility.Visible;
    }

    /// <summary>An action that puts the raw error on the clipboard; hovering the text shows it too.</summary>
    public void AddCopy(string label, string text)
    {
        _details = text;
        SetTip();
        var button = new ToastButton(label, label);
        button.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                button.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException ex) { Log.Write(ex); } // another program is holding the clipboard
        };
        _actions.Children.Add(button);
        _actions.Visibility = Visibility.Visible;
    }

    public void ClearActions()
    {
        _actions.Children.Clear();
        _actions.Visibility = Visibility.Collapsed;
    }

    public void DismissAfter(TimeSpan delay)
    {
        if (_removing) return;
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) => DismissNow();
        if (!IsMouseOver && !IsKeyboardFocusWithin) _timer.Start();
    }

    public void AnimateIn()
    {
        _slide.X = 40;
        BeginAnimation(OpacityProperty, WheelOverlay.Anim(1, 180));
        _slide.BeginAnimation(TranslateTransform.XProperty, WheelOverlay.Anim(0, 260));
    }

    public void DismissNow()
    {
        if (_removing) return;
        _removing = true;
        _timer?.Stop();
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        HideProgress();
        var fade = WheelOverlay.Anim(0, 160);
        fade.Completed += (_, _) =>
        {
            if (_removing) Removed?.Invoke(this);
        };
        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, WheelOverlay.Anim(40, 180));
    }
}
