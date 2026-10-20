using System.Diagnostics;
using System.Windows;
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
    readonly AppSettings _settings;
    readonly ToolLocator _tools;
    readonly Action<bool> _suspendTrigger;
    readonly Action _downloadFfmpeg;
    readonly HashSet<int> _held = [];
    readonly HashSet<int> _recorded = [];
    bool _recording;
    bool _loading = true; // controls raise change events while InitializeComponent builds them

    internal SettingsWindow(AppSettings settings, ToolLocator tools, Action<bool> suspendTrigger, Action downloadFfmpeg)
    {
        _settings = settings;
        _tools = tools;
        _suspendTrigger = suspendTrigger;
        _downloadFfmpeg = downloadFfmpeg;
        InitializeComponent();

        Logo.Source = LoadLogo();
        Icon = Logo.Source;

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Win32.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Activated += (_, _) => RefreshTools();
        Deactivated += (_, _) => StopRecording(commit: false);
        Closed += (_, _) => StopRecording(commit: false);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;

        Load();
    }

    static BitmapSource LoadLogo()
    {
        var ico = IconFactory.BuildIco();
        var decoder = new IconBitmapDecoder(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    void OnSettingsChanged() => Dispatcher.BeginInvoke(() => { if (!_recording) Load(); });

    void Load()
    {
        _loading = true;
        EnabledToggle.IsChecked = !_settings.Paused;
        StatusText.Text = _settings.Paused
            ? "Paused. Turn it back on with the switch."
            : $"Running. Hold {VirtualKeys.Describe(_settings.TriggerKeys)} over any file.";
        KeyButton.Content = VirtualKeys.Describe(_settings.TriggerKeys);
        DelaySlider.Value = _settings.HoldDelayMs;
        DelayValue.Text = $"{_settings.HoldDelayMs} ms";
        ShowAllToggle.IsChecked = _settings.AlwaysShowAll;
        KeepOriginal.IsChecked = !_settings.ReplaceOriginal;
        ReplaceOriginal.IsChecked = _settings.ReplaceOriginal;
        SameFolder.IsChecked = !_settings.UseCustomOutputFolder;
        CustomFolder.IsChecked = _settings.UseCustomOutputFolder;
        CustomFolderText.Text = string.IsNullOrEmpty(_settings.CustomOutputFolder) ? "No folder chosen yet" : _settings.CustomOutputFolder;
        StartupToggle.IsChecked = _settings.StartWithWindows;
        NotifyToggle.IsChecked = _settings.ShowNotifications;
        RefreshTools();
        _loading = false;
    }

    void RefreshTools()
    {
        var ffmpeg = _tools.FindFfmpeg();
        FfmpegStatus.Text = ffmpeg != null
            ? $"Ready · {ffmpeg}"
            : "Not installed yet. It downloads automatically the first time you convert audio or video.";
        FfmpegDownload.Visibility = ffmpeg == null ? Visibility.Visible : Visibility.Collapsed;

        var office = ToolLocator.FindLibreOffice();
        LibreOfficeStatus.Text = office != null ? $"Ready · {office}" : "Not installed. Needed only for documents.";
        LibreOfficeButton.Visibility = office == null ? Visibility.Visible : Visibility.Collapsed;
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

    void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        FfmpegDownload.IsEnabled = false;
        FfmpegStatus.Text = "Downloading… progress is shown in the corner of your screen.";
        _downloadFfmpeg();
    }

    void OnChooseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg.exe|ffmpeg.exe|Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        _settings.FfmpegPath = dialog.FileName;
        _settings.Save();
    }

    void OnGetLibreOffice(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(ToolLocator.LibreOfficeUrl) { UseShellExecute = true });

    // ------------------------------------------------------------------ shortcut recording

    void OnKeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording(commit: false);
            return;
        }
        _recording = true;
        _held.Clear();
        _recorded.Clear();
        _suspendTrigger(true);
        KeyButton.Tag = "recording";
        KeyButton.Content = "Press keys…";
        KeyHint.Text = "Hold the key or combination, then let go. Esc cancels.";
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
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _held.Remove(VirtualKeys.Normalize(KeyInterop.VirtualKeyFromKey(key)));
        if (_held.Count == 0 && _recorded.Count > 0) StopRecording(commit: true);
    }

    void StopRecording(bool commit)
    {
        if (!_recording) return;
        _recording = false;
        _suspendTrigger(false);
        KeyButton.Tag = null;
        KeyHint.Text = "Click the button, then press the key or combination you want to hold.";

        if (commit && _recorded.Count is > 0 and <= 3)
        {
            _settings.TriggerKeys = [.. _recorded];
            _settings.Save();
            if (!_recorded.Any(k => k is VirtualKeys.Ctrl or VirtualKeys.Alt or VirtualKeys.Shift or VirtualKeys.Win))
                KeyHint.Text = "Tip: a single letter or number key will still type while you hold it. Modifier keys work best.";
        }
        Load();
    }
}
