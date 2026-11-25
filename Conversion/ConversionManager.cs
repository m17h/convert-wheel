using System.Diagnostics;
using System.Runtime;
using System.Windows;
using ConvertWheel.Native;
using ConvertWheel.UI;

namespace ConvertWheel.Conversion;

/// <summary>Runs conversions in the background and reports them through toast cards. Used from the UI thread.</summary>
internal sealed class ConversionManager(AppSettings settings, ToolLocator tools, ToastHost toasts)
{
    /// <summary>Memory is tidied up once nothing has run for this long.</summary>
    static TimeSpan IdleDelay = TimeSpan.FromSeconds(6);

    sealed class Job(CancellationTokenSource cancel)
    {
        public readonly CancellationTokenSource Cancel = cancel;
        public Task Work = Task.CompletedTask;
        public volatile bool Stopped;
    }

    readonly Converter _converter = new(settings);
    readonly SemaphoreSlim _slots = new(2);
    readonly List<Job> _jobs = [];
    readonly object _unpackGate = new();
    Task<string>? _ffmpegUnpack;
    CancellationTokenSource? _tidy;
    bool _closing;

    /// <summary>Conversions running or waiting their turn.</summary>
    public int ActiveCount
    {
        get { lock (_jobs) return _jobs.Count; }
    }

    /// <summary>Raised on the UI thread when <see cref="ActiveCount"/> changes.</summary>
    public event Action? ActiveChanged;

    public async void Start(ConversionPlan plan, FormatInfo target)
    {
        if (_closing) return;
        Win32.GetCursorPos(out var place);
        var name = Path.GetFileName(plan.SourcePath);
        var toast = toasts.Push($"Converting to {target.Label}", name, Palette.Accent(target.Kind), ToastGlyph.Working, place);
        using var cts = new CancellationTokenSource();
        toast.AddAction("Cancel", () => _ = cts.CancelAsync());
        toast.SetProgress(null);

        var job = new Job(cts);
        lock (_jobs) _jobs.Add(job);
        _tidy?.Cancel();
        ActiveChanged?.Invoke();
        try
        {
            // Off the UI thread all the way, so closing the app can wait for a conversion to clean up.
            var progress = new Progress<double?>(toast.SetProgress);
            var work = Task.Run(() => RunAsync(job, plan, target, progress, place, cts.Token));
            job.Work = work;
            var (result, replaced, kept) = await work;

            var file = Path.GetFileName(result.Path);
            toast.ClearActions();
            toast.HideProgress();
            bool attention = result.Problem != null || kept != null;
            toast.SetState(result.Problem != null ? ToastGlyph.Warning : ToastGlyph.Success, result.Problem != null ? Palette.More : Palette.Success,
                reappear: attention || settings.ShowNotifications);
            toast.Update(result.Problem != null ? $"{target.Label} may be incomplete" : $"Converted to {target.Label}",
                replaced ? $"{file} · original moved to Recycle Bin"
                : attention ? $"Saved as {file}. {string.Join(' ', new[] { result.Problem, kept }.OfType<string>())}"
                : file);
            toast.AddAction("Show in folder", () => ShowInFolder(result.Path));
            toast.AddAction("Open", () => OpenFile(result.Path));
            if (attention) toast.DismissAfter(TimeSpan.FromSeconds(20));
            else if (settings.ShowNotifications) toast.DismissAfter(TimeSpan.FromSeconds(7));
            else toast.DismissNow();
        }
        catch (OperationCanceledException)
        {
            Finish(toast, ToastGlyph.Info, Palette.More, "Conversion cancelled", name, 3);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Finish(toast, ToastGlyph.Error, Palette.Error, $"Couldn't convert to {target.Label}", ErrorText.Describe(ex), 20,
                ErrorText.Details(ex, plan.SourcePath, target.Label));
        }
        finally
        {
            lock (_jobs) _jobs.Remove(job);
            ActiveChanged?.Invoke();
            if (ActiveCount == 0 && !_closing) TidyWhenIdle();
        }
    }

    async Task<(ConversionResult result, bool replaced, string? kept)> RunAsync(
        Job job, ConversionPlan plan, FormatInfo target, IProgress<double?> progress, POINT place, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (job.Stopped) throw new OperationCanceledException(ct); // one that stopped just before made room for it
            var before = Originals.Snapshot.Of(plan.SourcePath);
            var result = await _converter.ConvertAsync(plan, target, () => EnsureFfmpegAsync(place, ct), progress, ct).ConfigureAwait(false);
            if (!settings.ReplaceOriginal) return (result, false, null);
            var (replaced, why) = Originals.Replace(plan, target, result, before);
            return (result, replaced, why);
        }
        finally
        {
            _slots.Release();
        }
    }

    static void Finish(ToastCard toast, ToastGlyph glyph, (System.Windows.Media.Color, System.Windows.Media.Color) accent, string title, string subtitle, int seconds, string? details = null)
    {
        toast.ClearActions();
        toast.HideProgress();
        toast.SetState(glyph, accent);
        toast.Update(title, subtitle);
        if (details != null) toast.AddCopy("Copy details", details);
        toast.DismissAfter(TimeSpan.FromSeconds(seconds));
    }

    // ------------------------------------------------------------------ stopping

    public void CancelAll()
    {
        lock (_jobs)
        {
            foreach (var job in _jobs) job.Stopped = true;
            foreach (var job in _jobs)
                try { _ = job.Cancel.CancelAsync(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Refuses new conversions and cancels the running ones; the task ends when they've cleaned up after themselves.</summary>
    public Task CloseAsync()
    {
        _closing = true;
        Task[] work;
        lock (_jobs) work = _jobs.Select(j => j.Work).ToArray();
        CancelAll();
        return Task.WhenAll(work).ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ memory

    /// <summary>Big pictures and pages leave hundreds of megabytes of free but unreturned memory behind.</summary>
    async void TidyWhenIdle()
    {
        _tidy?.Cancel();
        using var tidy = _tidy = new CancellationTokenSource();
        try
        {
            await Task.Delay(IdleDelay, tidy.Token);
            if (ActiveCount > 0) return;
            await Task.Run(() =>
            {
                // Twice: the first lets finalizers free what .NET only holds a handle to (pictures, PDF pages).
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_tidy == tidy) _tidy = null;
        }
    }

    // ------------------------------------------------------------------ FFmpeg

    /// <summary>
    /// FFmpeg ships inside the app; the first time it's needed it's unpacked (a few seconds, once).
    /// Concurrent conversions share the same unpacking.
    /// </summary>
    async Task<string> EnsureFfmpegAsync(POINT place, CancellationToken ct)
    {
        if (BundledTools.ReadyFfmpeg() is { } ready) return ready;
        if (!BundledTools.HasFfmpeg)
            return tools.FindFfmpeg() ?? throw new ConversionException("This format needs the video tools, which aren't included in this build.");
        Task<string> unpack;
        lock (_unpackGate)
        {
            // A finished one no longer counts: it failed, or its files have been deleted since.
            if (_ffmpegUnpack is not { IsCompleted: false }) _ffmpegUnpack = UnpackFfmpegAsync(place);
            unpack = _ffmpegUnpack;
        }
        return await unpack.WaitAsync(ct).ConfigureAwait(false);
    }

    async Task<string> UnpackFfmpegAsync(POINT place)
    {
        await Task.Yield();
        var ui = Application.Current.Dispatcher;
        ToastCard? toast = null;
        _ = ui.BeginInvoke(() =>
        {
            if (_closing) return;
            toast = toasts.Push("Preparing video tools", "One-time setup, a few seconds", Palette.Neutral, ToastGlyph.Working, place);
            toast.SetProgress(null);
        });
        try
        {
            var path = await Task.Run(BundledTools.UnpackFfmpeg).ConfigureAwait(false);
            _ = ui.BeginInvoke(() => toast?.DismissNow());
            return path;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            var reason = ErrorText.Describe(ex);
            _ = ui.BeginInvoke(() => toast?.DismissNow()); // the conversion that needed them says why
            throw new ConversionException(reason == ErrorText.Other ? "The video tools couldn't be prepared." : $"The video tools couldn't be prepared. {reason}", ex.ToString());
        }
    }

    // ------------------------------------------------------------------ opening the result

    static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write(ex); }
    }

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write(ex); }
    }
}
