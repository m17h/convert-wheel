using System.Diagnostics;
using System.Windows;
using ConvertWheel.UI;
using Microsoft.VisualBasic.FileIO;

namespace ConvertWheel.Conversion;

/// <summary>Runs conversions in the background and reports them through toast cards.</summary>
internal sealed class ConversionManager(AppSettings settings, ToolLocator tools, ToastHost toasts)
{
    readonly Converter _converter = new(settings);
    readonly SemaphoreSlim _slots = new(2);
    Task<string>? _ffmpegUnpack;

    public async void Start(ConversionPlan plan, FormatInfo target)
    {
        var name = Path.GetFileName(plan.SourcePath);
        var accent = Palette.Accent(target.Kind);
        var toast = toasts.Push($"Converting to {target.Label}", name, accent, ToastGlyph.Working);
        using var cts = new CancellationTokenSource();
        toast.AddAction("Cancel", cts.Cancel);
        toast.SetProgress(null);

        try
        {
            await _slots.WaitAsync(cts.Token);
            string output;
            try
            {
                var progress = new Progress<double?>(toast.SetProgress);
                output = await _converter.ConvertAsync(plan, target, () => EnsureFfmpegAsync(cts.Token), progress, cts.Token);
            }
            finally
            {
                _slots.Release();
            }

            bool replaced = settings.ReplaceOriginal && TryRecycle(plan.SourcePath);
            toast.ClearActions();
            toast.HideProgress();
            toast.SetState(ToastGlyph.Success, Palette.Success);
            toast.Update($"Converted to {target.Label}", replaced ? $"{Path.GetFileName(output)} · original moved to Recycle Bin" : Path.GetFileName(output));
            toast.AddAction("Show in folder", () => ShowInFolder(output));
            toast.AddAction("Open", () => OpenFile(output));
            if (settings.ShowNotifications) toast.DismissAfter(TimeSpan.FromSeconds(7));
            else toast.DismissNow();
        }
        catch (OperationCanceledException)
        {
            Finish(toast, ToastGlyph.Info, Palette.More, "Conversion cancelled", name, 3);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Finish(toast, ToastGlyph.Error, Palette.Error, $"Couldn't convert to {target.Label}", ex.Message, 15);
        }
    }

    static void Finish(ToastCard toast, ToastGlyph glyph, (System.Windows.Media.Color, System.Windows.Media.Color) accent, string title, string subtitle, int seconds)
    {
        toast.ClearActions();
        toast.HideProgress();
        toast.SetState(glyph, accent);
        toast.Update(title, subtitle);
        toast.DismissAfter(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// FFmpeg ships inside the app; the first time it's needed it's unpacked (a few seconds, once).
    /// Concurrent conversions share the same unpacking.
    /// </summary>
    Task<string> EnsureFfmpegAsync(CancellationToken ct) => Application.Current.Dispatcher.Invoke(() =>
    {
        if (BundledTools.ReadyFfmpeg() is { } ready) return Task.FromResult(ready);
        if (!BundledTools.HasFfmpeg)
            return Task.FromResult(tools.FindFfmpeg() ?? throw new ConversionException("This format needs the video tools, which aren't included in this build."));
        return (_ffmpegUnpack ??= UnpackFfmpegAsync()).WaitAsync(ct);
    });

    async Task<string> UnpackFfmpegAsync()
    {
        var toast = toasts.Push("Preparing video tools", "One-time setup, a few seconds", Palette.Neutral, ToastGlyph.Working);
        toast.SetProgress(null);
        try
        {
            var path = await Task.Run(BundledTools.UnpackFfmpeg);
            toast.DismissNow();
            return path;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _ffmpegUnpack = null;
            Finish(toast, ToastGlyph.Error, Palette.Error, "Couldn't prepare the video tools", ex.Message, 15);
            throw new ConversionException("The video tools couldn't be prepared.");
        }
    }

    /// <summary>Unpacks the bundled tools ahead of time (from Settings).</summary>
    public void PrepareFfmpeg()
    {
        if (BundledTools.HasFfmpeg && BundledTools.ReadyFfmpeg() == null) _ffmpegUnpack ??= UnpackFfmpegAsync();
    }

    static bool TryRecycle(string path)
    {
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return false;
        }
    }

    static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write(ex); }
    }
}
