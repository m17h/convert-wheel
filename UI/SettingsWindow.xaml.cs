using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ConvertWheel.Conversion;
using ConvertWheel.Input;
using ConvertWheel.Native;
using Microsoft.Win32;

namespace ConvertWheel.UI;

public partial class SettingsWindow : Window
{
    const string KeyHelp = "Click the button, then press the key or combination you want to hold.";

    /// <summary>Space around the window so its frame and shadow stay on screen.</summary>
    const double ScreenMargin = 24;

    readonly AppSettings _settings;
    readonly ToolLocator _tools;
    readonly Action<bool> _suspendTrigger;
    readonly HashSet<int> _held = [];
    readonly HashSet<int> _recorded = [];
    readonly ResourceDictionary _highContrast = new() { Source = new Uri("/ConvertWheel;component/UI/ThemeHighContrast.xaml", UriKind.Relative) };
    string? _refusal;
    bool _recording;
    bool _loading = true; // controls raise change events while InitializeComponent builds them

    internal SettingsWindow(AppSettings settings, ToolLocator tools, Action<bool> suspendTrigger)
    {
        _settings = settings;
        _tools = tools;
        _suspendTrigger = suspendTrigger;
        InitializeComponent();
        FitToScreen();
        ApplyHighContrast(SystemParameters.HighContrast);

        Logo.Source = LoadLogo();
        Icon = Logo.Source;
        VersionText.Text = $"Convert Wheel {Version}";
        LicencesButton.Visibility = FindLicences(AppContext.BaseDirectory) != null ? Visibility.Visible : Visibility.Collapsed;

        SourceInitialized += (_, _) => ColourTitleBar();
        Activated += (_, _) => RefreshTools();
        Deactivated += (_, _) => StopRecording(commit: false);
        Closed += (_, _) => StopRecording(commit: false);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        KeyDown += OnKeyDown;
        // WPF lets access keys work without Alt, so a stray "r" would switch to replacing originals.
        PreviewTextInput += (_, e) => e.Handled = true;
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

        Load();
    }

    static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    static BitmapSource LoadLogo()
    {
        var ico = IconFactory.BuildIco();
        var decoder = new IconBitmapDecoder(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    // ------------------------------------------------------------------ size & colours

    /// <summary>Fits the window on the screen it's about to open on (the one with the mouse).</summary>
    void FitToScreen()
    {
        if (!Win32.GetCursorPos(out var cursor)) return;
        var (work, scale) = Win32.MonitorAt(cursor);
        (Width, Height) = Fit(Width, Height, work, scale);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    /// <summary>A size in DIPs, no larger than the work area (physical pixels) allows.</summary>
    internal static (double Width, double Height) Fit(double width, double height, RECT work, double scale) => (
        Math.Min(width, Math.Max(200, (work.Right - work.Left) / scale - ScreenMargin)),
        Math.Min(height, Math.Max(200, (work.Bottom - work.Top) / scale - ScreenMargin)));

    /// <summary>Moved to a screen with another scale, which may be too small for the current size.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (WindowState != WindowState.Normal) return;
        var at = PointToScreen(new Point(ActualWidth / 2, 20));
        var (work, _) = Win32.MonitorAt(new POINT((int)at.X, (int)at.Y));
        (Width, Height) = Fit(Width, Height, work, newDpi.DpiScaleX);
    }

    void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.BeginInvoke(() =>
            {
                ApplyHighContrast(SystemParameters.HighContrast);
                ColourTitleBar();
            });
    }

    internal void ApplyHighContrast(bool on)
    {
        Resources.MergedDictionaries.Remove(_highContrast);
        if (on) Resources.MergedDictionaries.Add(_highContrast);
    }

    void ColourTitleBar()
    {
        int dark = SystemParameters.HighContrast ? 0 : 1;
        Win32.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    // ------------------------------------------------------------------ loading

    void OnSettingsChanged() => Dispatcher.BeginInvoke(() => { if (!_recording) Load(); });

    void Load()
    {
        _loading = true;
        var trigger = VirtualKeys.Describe(_settings.TriggerKeys);
        EnabledToggle.IsChecked = !_settings.Paused;
        StatusText.Text = _settings.Paused
            ? "Paused. Turn it back on with the switch."
            : $"Running. Hold {trigger} over any file.";
        KeyButton.Content = trigger;
        AutomationProperties.SetName(KeyButton, $"Hold to open the wheel: {trigger}");
        ShowKeyHint();
        DelaySlider.Maximum = Math.Max(1000, _settings.HoldDelayMs);
        DelaySlider.Value = _settings.HoldDelayMs;
        DelayValue.Text = $"{_settings.HoldDelayMs} ms";
        ShowAllToggle.IsChecked = _settings.AlwaysShowAll;
        KeepOriginal.IsChecked = !_settings.ReplaceOriginal;
        ReplaceOriginal.IsChecked = _settings.ReplaceOriginal;
        SameFolder.IsChecked = !_settings.UseCustomOutputFolder;
        CustomFolder.IsChecked = _settings.UseCustomOutputFolder;
        CustomFolderText.Text = string.IsNullOrEmpty(_settings.CustomOutputFolder) ? "No folder chosen yet" : _settings.CustomOutputFolder;
        CustomFolderText.ToolTip = string.IsNullOrEmpty(_settings.CustomOutputFolder) ? null : _settings.CustomOutputFolder;
        StartupToggle.IsChecked = _settings.StartWithWindows;
        NotifyToggle.IsChecked = _settings.ShowNotifications;
        RefreshTools();
        _loading = false;
    }

    /// <summary>Why the last key was refused, what's risky about the current one, or how to change it.</summary>
    void ShowKeyHint()
    {
        var (verdict, message) = TriggerPolicy.Check(_settings.TriggerKeys, _settings.HoldDelayMs);
        bool warn = _refusal != null || verdict != TriggerVerdict.Good;
        KeyHint.Text = _refusal ?? (verdict == TriggerVerdict.Good ? KeyHelp : message);
        KeyHint.SetResourceReference(ForegroundProperty, warn ? "WarningBrush" : "SubtleTextBrush");
    }

    void RefreshTools()
    {
        var apps = new[] { "Word", "Excel", "PowerPoint" }.Where(OfficeAutomation.IsInstalled).ToList();
        OfficeStatus.Text = apps.Count > 0
            ? $"Found {string.Join(", ", apps)}. Used automatically for the most faithful conversions of Office files."
            : "Not installed. That's fine: everything converts without it.";

    }

    // ------------------------------------------------------------------ simple settings

    void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        _settings.Paused = EnabledToggle.IsChecked != true;
        _settings.Save();
    }

    void OnDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DelayValue == null) return;
        DelayValue.Text = $"{(int)DelaySlider.Value} ms";
        if (_loading) return;
        _settings.HoldDelayMs = (int)DelaySlider.Value;
        _settings.Save();
    }

    void OnSettingClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AlwaysShowAll = ShowAllToggle.IsChecked == true;
        _settings.ReplaceOriginal = ReplaceOriginal.IsChecked == true;
        _settings.ShowNotifications = NotifyToggle.IsChecked == true;

        bool wantsCustom = CustomFolder.IsChecked == true;
        if (wantsCustom && string.IsNullOrEmpty(_settings.CustomOutputFolder) && !PickFolder())
        {
            SameFolder.IsChecked = true;
            wantsCustom = false;
        }
        _settings.UseCustomOutputFolder = wantsCustom;

        if (_settings.StartWithWindows != (StartupToggle.IsChecked == true))
        {
            _settings.StartWithWindows = StartupToggle.IsChecked == true;
            _settings.ApplyStartup();
        }
        _settings.Save();
    }

    void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        if (!PickFolder()) return;
        _settings.UseCustomOutputFolder = true;
        _settings.Save();
    }

    bool PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Save converted files to…" };
        if (!string.IsNullOrEmpty(_settings.CustomOutputFolder)) dialog.InitialDirectory = _settings.CustomOutputFolder;
        if (dialog.ShowDialog(this) != true) return false;
        _settings.CustomOutputFolder = dialog.FolderName;
        return true;
    }

    // ------------------------------------------------------------------ about

    /// <summary>The licence texts shipped next to the app, or null.</summary>
    internal static string? FindLicences(string appFolder)
    {
        var folder = Path.Combine(appFolder, "licenses");
        if (Directory.Exists(folder)) return folder;
        return new[] { "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.txt" }
            .Select(name => Path.Combine(appFolder, name))
            .FirstOrDefault(File.Exists);
    }

    void OnLicences(object sender, RoutedEventArgs e)
    {
        if (FindLicences(AppContext.BaseDirectory) is not { } path) return;
        // Markdown files often have no program to open them; Notepad always reads text.
        if (Directory.Exists(path)) Open(path);
        else Open("notepad.exe", $"\"{path}\"");
    }

    void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(Log.DataDir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Open(Log.DataDir);
    }

    static void Open(string path, string arguments = "")
    {
        try { Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write(ex); }
    }

    // ------------------------------------------------------------------ shortcut recording

    void OnKeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording(commit: false);
            return;
        }
        _recording = true;
        _refusal = null;
        _held.Clear();
        _recorded.Clear();
        _suspendTrigger(true);
        KeyButton.Tag = "recording";
        KeyButton.Content = "Press keys…";
        KeyHint.Text = "Hold the key or combination, then let go. Esc cancels.";
        KeyHint.SetResourceReference(ForegroundProperty, "SubtleTextBrush");
        AnnounceKeyHint();
        KeyButton.Focus();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording(commit: false);
            return;
        }
        int vk = VirtualKeys.Normalize(KeyInterop.VirtualKeyFromKey(key));
        if (vk == 0) return;
        _held.Add(vk);
        _recorded.Add(vk);
        KeyButton.Content = VirtualKeys.Describe(_recorded);
        // Say so at once: letting go of some of these (the Windows key) takes the focus elsewhere.
        if (TriggerPolicy.Check(_recorded, _settings.HoldDelayMs).Verdict == TriggerVerdict.Rejected) StopRecording(commit: true);
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _held.Remove(VirtualKeys.Normalize(KeyInterop.VirtualKeyFromKey(key)));
        if (_held.Count == 0 && _recorded.Count > 0) StopRecording(commit: true);
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        Close();
    }

    void StopRecording(bool commit)
    {
        if (!_recording) return;
        _recording = false;
        _suspendTrigger(false);
        KeyButton.Tag = null;

        if (commit && _recorded.Count > 0)
        {
            var (verdict, message) = TriggerPolicy.Check(_recorded, _settings.HoldDelayMs);
            if (verdict == TriggerVerdict.Rejected) _refusal = message;
            else
            {
                _settings.TriggerKeys = [.. _recorded];
                _settings.Save();
            }
        }
        Load();
        AnnounceKeyHint();
    }

    void AnnounceKeyHint()
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(KeyHint)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
