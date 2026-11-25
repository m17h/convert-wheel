using System.Runtime.InteropServices;
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
    const string ExitEvent = "ConvertWheel.Exit"; // what older versions listen to
    /// <summary>Signalled to make a running copy stop its conversions, clean up and exit without asking.</summary>
    const string QuitEvent = @"Local\ConvertWheel.Quit";

    static readonly TimeSpan CleanUpTime = TimeSpan.FromSeconds(5);
    /// <summary>Windows gives up on a program that takes 5 seconds to answer when it shuts down.</summary>
    static readonly TimeSpan SessionEndTime = TimeSpan.FromSeconds(4);

    Mutex? _instanceMutex;
    EventWaitHandle? _showSettingsSignal, _exitSignal, _quitSignal;
    AppSettings _settings = null!;
    ToolLocator _tools = null!;
    ShellResolver? _resolver;
    TriggerController? _controller;
    ConversionManager? _conversions;
    TrayIcon? _tray;
    SettingsWindow? _settingsWindow;
    bool _askingToExit, _exiting;

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

        // ConvertWheel.exe --exit : asks a running copy to close (the installer uses this before updating).
        if (e.Args.Contains("--exit"))
        {
            Signal(QuitEvent);
            Signal(ExitEvent);
            Shutdown();
            return;
        }

        int convertAt = Array.IndexOf(e.Args, "--convert");
        if (convertAt >= 0)
        {
            if (convertAt + 2 >= e.Args.Length)
            {
                UseParentConsole();
                Console.Error.WriteLine("Usage: ConvertWheel.exe --convert \"file\" format    (for example: --convert \"song.ogg\" mp3)");
                Shutdown(2);
                return;
            }
            RunCommandLineConversion(e.Args[convertAt + 1], e.Args[convertAt + 2]);
            return;
        }

        int previewAt = Array.IndexOf(e.Args, "--preview-wheel");
        if (previewAt >= 0 && previewAt + 1 < e.Args.Length)
        {
            PreviewWheel(e.Args[previewAt + 1], e.Args.Skip(previewAt + 2).ToArray());
            return;
        }

        bool firstInstance = false, outOfReach = false;
        try { _instanceMutex = new Mutex(true, InstanceName, out firstInstance); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException) { outOfReach = true; }
        if (!firstInstance)
        {
            // Already running: ask that instance to show its settings instead.
            Native.Win32.AllowSetForegroundWindow(-1); // we were launched by the user, so we may hand focus over
            if (!outOfReach) Signal(ShowSettingsEvent);
            else if (!e.Args.Contains("--background"))
                MessageBox.Show("Convert Wheel is already running as administrator, so this copy can't reach it. Look for it in the taskbar's hidden icons.",
                    "Convert Wheel", MessageBoxButton.OK, MessageBoxImage.Information);
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

        try
        {
            StartApp(e.Args);
        }
        catch (Exception ex)
        {
            // Half-started, the app would sit there unseen and keep the next copy from starting.
            Log.Write(ex);
            Release();
            MessageBox.Show($"Convert Wheel couldn't start.\n\n{ex.Message}", "Convert Wheel", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    void StartApp(string[] args)
    {
        _settings = AppSettings.Load();
        _tools = new ToolLocator();
        var toasts = new ToastHost();
        _conversions = new ConversionManager(_settings, _tools, toasts);
        _resolver = new ShellResolver();
        _controller = new TriggerController(_settings, _resolver, new WheelOverlay(), _conversions);
        _tray = new TrayIcon(_settings, _conversions, ShowSettings, AskToExit);
        if (_settings.StartWithWindows) _settings.ApplyStartup();

        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
        new Thread(() =>
        {
            while (_showSettingsSignal.WaitOne()) Dispatcher.BeginInvoke(ShowSettings);
        })
        { IsBackground = true, Name = "Single instance listener" }.Start();

        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEvent);
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
        new Thread(() =>
        {
            WaitHandle.WaitAny([_quitSignal, _exitSignal]);
            Dispatcher.BeginInvoke(ExitCleanly);
        })
        { IsBackground = true, Name = "Exit listener" }.Start();
        _ = Task.Run(OutputReservation.CleanLeftovers);

        bool background = args.Contains("--background");
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

    /// <summary>Sets another copy's event; false if there is none, or it's out of reach (running as administrator).</summary>
    static bool Signal(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return false;
            using (signal) return signal.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    // ------------------------------------------------------------------ exiting

    void AskToExit()
    {
        if (_askingToExit || _exiting) return;
        int running = _conversions?.ActiveCount ?? 0;
        if (running > 0)
        {
            _askingToExit = true;
            var answer = MessageBox.Show(
                running == 1 ? "1 conversion is still running. Stop it and exit?" : $"{running} conversions are still running. Stop them and exit?",
                "Convert Wheel", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            _askingToExit = false;
            if (answer != MessageBoxResult.Yes) return;
        }
        ExitCleanly();
    }

    /// <summary>Stops the conversions, gives them a moment to remove their half-made files, and exits.</summary>
    async void ExitCleanly()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (_conversions != null) await _conversions.CloseAsync().WaitAsync(CleanUpTime);
        }
        catch (TimeoutException)
        {
            Log.Write("Some conversions didn't stop in time; their leftovers are removed at the next start.");
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        Shutdown();
    }

    /// <summary>Signing out or shutting down: same, but Windows doesn't wait for anything that happens later.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (_conversions == null || _conversions.ActiveCount == 0) return;
        _exiting = true;
        try { _conversions.CloseAsync().Wait(SessionEndTime); }
        catch (Exception ex) { Log.Write(ex); }
    }

    // ------------------------------------------------------------------ command line

    /// <summary>
    /// ConvertWheel.exe --convert "file.ogg" mp3 : converts without any UI and prints the output path.
    /// Exits with 0, 1 if it failed, 2 for wrong arguments, or 3 if it was saved but may be incomplete.
    /// </summary>
    async void RunCommandLineConversion(string file, string ext)
    {
        int exitCode = 1;
        UseParentConsole();
        try
        {
            var settings = AppSettings.Load();
            var tools = new ToolLocator();
            var plan = Formats.Plan(Path.GetFullPath(file), patience: 10_000) ?? throw new ConversionException("Unsupported file type.");
            var target = plan.All.FirstOrDefault(f => f.Ext == Formats.Canonical(ext.TrimStart('.').ToLowerInvariant()))
                ?? throw new ConversionException($"Can't convert {plan.SourceExt} to {ext}.");
            var result = await new Converter(settings).ConvertAsync(plan, target,
                () => Task.FromResult(BundledTools.HasFfmpeg ? BundledTools.UnpackFfmpeg() : tools.FindFfmpeg() ?? throw new ConversionException("FFmpeg isn't available.")),
                new Progress<double?>(), CancellationToken.None);
            Console.WriteLine(result.Path);
            if (result.Problem != null) Console.Error.WriteLine(result.Problem);
            exitCode = result.Problem != null ? 3 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ErrorText.Describe(ex));
            Log.Write(ex);
            if ((ex as ConversionException)?.Details is { } details) Log.Write(details);
        }
        Shutdown(exitCode);
    }

    /// <summary>A window program has no console of its own; started from one, it can write there.</summary>
    static void UseParentConsole()
    {
        const int ParentProcess = -1, StdOutput = -11, StdError = -12;
        // Output that's already redirected to a file or pipe is left alone.
        if (GetStdHandle(StdOutput) is 0 or -1 && GetStdHandle(StdError) is 0 or -1) AttachConsole(ParentProcess);
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int handle);

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
                suspend => { if (_controller != null) _controller.Suspended = suspend; });
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
        Release();
        base.OnExit(e);
    }

    /// <summary>Puts back everything outside the app: hooks, the tray icon, and the claim to be the only copy.</summary>
    void Release()
    {
        foreach (var part in new IDisposable?[] { _controller, _resolver, _tray, _instanceMutex })
            try { part?.Dispose(); } catch (Exception ex) { Log.Write(ex); }
        (_controller, _resolver, _tray, _instanceMutex) = (null, null, null, null);
    }
}
