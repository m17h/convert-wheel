using System.Windows;
using System.Windows.Threading;
using ConvertWheel.Conversion;
using ConvertWheel.Input;
using ConvertWheel.Shell;
using ConvertWheel.UI;

namespace ConvertWheel;

public partial class App : Application
{
    const string InstanceName = "ConvertWheel.SingleInstance";
    const string ShowSettingsEvent = "ConvertWheel.ShowSettings";

    Mutex? _instanceMutex;
    EventWaitHandle? _showSettingsSignal;
    AppSettings _settings = null!;
    ToolLocator _tools = null!;
    ShellResolver? _resolver;
    TriggerController? _controller;
    ConversionManager? _conversions;
    TrayIcon? _tray;
    SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int exportAt = Array.IndexOf(e.Args, "--export-icon");
        if (exportAt >= 0 && exportAt + 1 < e.Args.Length)
        {
            File.WriteAllBytes(e.Args[exportAt + 1], IconFactory.BuildIco());
            Shutdown();
            return;
        }

        int convertAt = Array.IndexOf(e.Args, "--convert");
        if (convertAt >= 0 && convertAt + 2 < e.Args.Length)
        {
            RunCommandLineConversion(e.Args[convertAt + 1], e.Args[convertAt + 2]);
            return;
        }

        if (e.Args.Contains("--setup-libreoffice"))
        {
            SetupLibreOffice();
            return;
        }

        int previewAt = Array.IndexOf(e.Args, "--preview-wheel");
        if (previewAt >= 0 && previewAt + 1 < e.Args.Length)
        {
            PreviewWheel(e.Args[previewAt + 1], e.Args.Skip(previewAt + 2).ToArray());
            return;
        }

        _instanceMutex = new Mutex(true, InstanceName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that instance to show its settings instead.
            Native.Win32.AllowSetForegroundWindow(-1); // we were launched by the user, so we may hand focus over
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEvent, out var signal)) signal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write(args.ExceptionObject.ToString() ?? "Unknown error");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write(args.Exception);
            args.SetObserved();
        };

        _settings = AppSettings.Load();
        _tools = new ToolLocator(_settings);
        var toasts = new ToastHost();
        _conversions = new ConversionManager(_settings, _tools, toasts);
        _resolver = new ShellResolver();
        _controller = new TriggerController(_settings, _resolver, new WheelOverlay(), _conversions);
        _tray = new TrayIcon(_settings, ShowSettings, () => Shutdown());
        if (_settings.StartWithWindows) _settings.ApplyStartup();

        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
        new Thread(() =>
        {
            while (_showSettingsSignal.WaitOne()) Dispatcher.BeginInvoke(ShowSettings);
        })
        { IsBackground = true, Name = "Single instance listener" }.Start();

        bool background = e.Args.Contains("--background");
        if (!_settings.HasShownWelcome)
        {
            _settings.HasShownWelcome = true;
            _settings.Save();
            var welcome = toasts.Push("Convert Wheel is running",
                $"Hold {VirtualKeys.Describe(_settings.TriggerKeys)} over any file to convert it. Find me in the taskbar's hidden icons.",
                Palette.Neutral, ToastGlyph.Info);
            welcome.AddAction("Settings", ShowSettings);
            welcome.DismissAfter(TimeSpan.FromSeconds(12));
        }
        else if (!background)
        {
            ShowSettings();
        }
    }

    /// <summary>ConvertWheel.exe --convert "file.ogg" mp3 : converts without any UI and prints the output path.</summary>
    async void RunCommandLineConversion(string file, string ext)
    {
        int exitCode = 1;
        try
        {
            var settings = AppSettings.Load();
            var tools = new ToolLocator(settings);
            var plan = Formats.Plan(Path.GetFullPath(file)) ?? throw new ConversionException("Unsupported file type.");
            var target = plan.All.FirstOrDefault(f => f.Ext == Formats.Canonical(ext.TrimStart('.').ToLowerInvariant()))
                ?? throw new ConversionException($"Can't convert {plan.SourceExt} to {ext}.");
            var output = await new Converter(settings).ConvertAsync(plan, target,
                () => Task.FromResult(tools.FindFfmpeg() ?? throw new ToolMissingException("FFmpeg")),
                new Progress<double?>(), CancellationToken.None);
            Console.WriteLine(output);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Log.Write(ex);
        }
        Shutdown(exitCode);
    }

    /// <summary>ConvertWheel.exe --setup-libreoffice : fetches the optional LibreOffice helper ahead of time.</summary>
    async void SetupLibreOffice()
    {
        int exitCode = 1;
        try
        {
            int lastPercent = -1;
            var progress = new Progress<double?>(p =>
            {
                int percent = p is double f ? (int)(f * 100) : -1;
                if (percent / 10 != lastPercent / 10 || (p == null && lastPercent != -1)) Console.WriteLine(p == null ? "Unpacking…" : $"{percent}%");
                lastPercent = percent;
            });
            Console.WriteLine(await ToolLocator.DownloadLibreOfficeAsync(progress, CancellationToken.None));
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Log.Write(ex);
        }
        Shutdown(exitCode);
    }

    /// <summary>Developer aid: shows the wheel for a file in the middle of the primary screen for a few seconds.</summary>
    void PreviewWheel(string file, string[] options)
    {
        var plan = Formats.Plan(Path.GetFullPath(file));
        if (plan == null)
        {
            Shutdown(1);
            return;
        }
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var center = new Native.POINT(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        Native.Win32.SetCursorPos(center.X, center.Y);
        var wheel = new WheelOverlay();
        wheel.Open(plan, center, options.Contains("--expanded"), null);

        var resolver = new ShellResolver();
        resolver.GetThumbnailAsync(plan.SourcePath, 128).ContinueWith(t => wheel.SetThumbnail(plan.SourcePath, t.Result),
            TaskScheduler.FromCurrentSynchronizationContext());

        int aimAt = Array.IndexOf(options, "--aim");
        if (aimAt >= 0 && aimAt + 2 < options.Length)
        {
            double angle = double.Parse(options[aimAt + 1], System.Globalization.CultureInfo.InvariantCulture) * Math.PI / 180;
            double distance = double.Parse(options[aimAt + 2], System.Globalization.CultureInfo.InvariantCulture);
            var (_, scale) = Native.Win32.MonitorAt(center);
            Native.Win32.SetCursorPos(center.X + (int)(Math.Sin(angle) * distance * scale), center.Y - (int)(Math.Cos(angle) * distance * scale));
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => Shutdown();
        timer.Start();
    }

    void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, _tools,
                suspend => { if (_controller != null) _controller.Suspended = suspend; },
                () => _conversions?.PrefetchFfmpeg(),
                () => _conversions?.EnsureLibreOfficeAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Topmost = true; // nudge past focus-stealing prevention, then behave normally
        _settingsWindow.Activate();
        _settingsWindow.Topmost = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _resolver?.Dispose();
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
