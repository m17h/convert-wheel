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
    Task<string>? _ffmpegDownload;

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
            if (plan.NeedsLibreOffice && ToolLocator.FindLibreOffice() == null)
                throw new ToolMissingException("LibreOffice");

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
        catch (ToolMissingException ex) when (ex.Tool == "LibreOffice")
        {
            Finish(toast, ToastGlyph.Error, Palette.Error, "LibreOffice is needed for documents", "It's free. Install it, then try again.", 20);
            toast.AddAction("Get LibreOffice", () => OpenFile(ToolLocator.LibreOfficeUrl));
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

    /// <summary>Returns FFmpeg's path, downloading it once (shared by concurrent jobs) if it isn't installed.</summary>
    Task<string> EnsureFfmpegAsync(CancellationToken ct)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = tools.FindFfmpeg();
            if (existing != null) return Task.FromResult(existing);
            _ffmpegDownload ??= DownloadFfmpegAsync();
            return _ffmpegDownload.WaitAsync(ct);
        });
    }

    async Task<string> DownloadFfmpegAsync()
    {
        var toast = toasts.Push("Setting up FFmpeg", "One-time download (about 200 MB) for audio & video", Palette.Neutral, ToastGlyph.Download);
        toast.SetProgress(0);
        try
        {
            var path = await tools.DownloadFfmpegAsync(new Progress<double?>(toast.SetProgress), CancellationToken.None);
            Finish(toast, ToastGlyph.Success, Palette.Success, "FFmpeg is ready", "Audio and video conversions are now available.", 4);
            return path;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            _ffmpegDownload = null;
            Finish(toast, ToastGlyph.Error, Palette.Error, "Couldn't download FFmpeg", "Check your connection, or set ffmpeg.exe in Settings.", 15);
            throw new ConversionException("FFmpeg isn't available.");
        }
    }

    public void PrefetchFfmpeg()
    {
        if (tools.FindFfmpeg() == null) _ffmpegDownload ??= DownloadFfmpegAsync();
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
