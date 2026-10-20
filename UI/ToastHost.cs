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

internal enum ToastGlyph { Working, Success, Error, Download, Info }

/// <summary>Floating, non-activating stack of progress/result cards above the taskbar.</summary>
internal sealed class ToastHost : Window
{
    readonly StackPanel _stack = new() { Margin = new Thickness(0, 0, 18, 18) };

    public ToastHost()
    {
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
        SizeChanged += (_, _) => Reposition();
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Win32.AddExStyle(hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
    }

    public ToastCard Push(string title, string subtitle, (Color, Color) accent, ToastGlyph glyph)
    {
        var card = new ToastCard(title, subtitle, accent, glyph);
        card.Removed += () =>
        {
            _stack.Children.Remove(card);
            if (_stack.Children.Count == 0) Hide();
        };
        _stack.Children.Add(card);
        while (_stack.Children.Count > 5) ((ToastCard)_stack.Children[0]).DismissNow();
        if (!IsVisible) Show();
        Reposition();
        Topmost = false;
        Topmost = true;
        card.AnimateIn();
        return card;
    }

    void Reposition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth;
        Top = work.Bottom - ActualHeight;
    }
}

internal sealed class ToastCard : Border
{
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Solid("#F5F5FA"), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _subtitle = new() { FontSize = 11.5, Foreground = Palette.Solid("#A3A3B2"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
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

    public event Action? Removed;

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

        var close = new TextBlock
        {
            Text = "",
            FontFamily = Palette.IconFont,
            FontSize = 10,
            Foreground = Palette.Solid("#70FFFFFF"),
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        close.MouseEnter += (_, _) => close.Foreground = Brushes.White;
        close.MouseLeave += (_, _) => close.Foreground = Palette.Solid("#70FFFFFF");
        close.MouseLeftButtonUp += (_, _) => DismissNow();

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
        MouseLeave += (_, _) => _timer?.Start();

        Update(title, subtitle);
        SetState(glyph, accent);
    }

    public void Update(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetState(ToastGlyph glyph, (Color, Color) accent)
    {
        _circle.Background = Palette.Gradient(accent);
        _fill.Background = Palette.Gradient(accent);
        _glyph.Text = glyph switch
        {
            ToastGlyph.Working => "",
            ToastGlyph.Success => "",
            ToastGlyph.Error => "",
            ToastGlyph.Download => "",
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
        var link = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Solid("#A5B4FC"),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.Hand,
        };
        var chip = new Border { Child = link, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        chip.MouseEnter += (_, _) => chip.Background = Palette.Solid("#1FFFFFFF");
        chip.MouseLeave += (_, _) => chip.Background = Brushes.Transparent;
        chip.MouseLeftButtonUp += (_, _) => action();
        _actions.Children.Add(chip);
        _actions.Visibility = Visibility.Visible;
    }

    public void ClearActions()
    {
        _actions.Children.Clear();
        _actions.Visibility = Visibility.Collapsed;
    }

    public void DismissAfter(TimeSpan delay)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) => DismissNow();
        if (!IsMouseOver) _timer.Start();
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
        var fade = WheelOverlay.Anim(0, 160);
        fade.Completed += (_, _) => Removed?.Invoke();
        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, WheelOverlay.Anim(40, 180));
    }
}
