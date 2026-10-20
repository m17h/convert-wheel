using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion;

public sealed class ConversionException(string message) : Exception(message);

public sealed class ToolMissingException(string tool) : Exception($"{tool} is not installed.")
{
    public string Tool { get; } = tool;
}

/// <summary>
/// Picks an engine per conversion: Windows imaging (WIC) for common still images, FFmpeg for
/// audio/video and exotic images, and for documents Microsoft Office, LibreOffice or the
/// built-in document engine, whichever is available and best.
/// </summary>
public sealed partial class Converter(AppSettings settings)
{

    public async Task<string> ConvertAsync(
        ConversionPlan plan, FormatInfo target, Func<Task<string>> ensureFfmpeg, IProgress<double?> progress, CancellationToken ct)
    {
        var output = ReserveOutputPath(plan.SourcePath, target.Ext);
        try
        {
            if (plan.IsOfficeDocument)
            {
                await DocumentAsync(plan, target, output, ct);
            }
            else if (await RunSta(() => BuiltInMedia(plan, target, output, progress, ct)))
            {
                // Done by the built-in engines (Windows codecs plus bundled open-source ones).
            }
            else
            {
                // Rare formats only: FFmpeg is an optional download.
                if (Environment.GetEnvironmentVariable("CONVERTWHEEL_MEDIA_ENGINE") == "built-in")
                    throw new ConversionException("Built-in engine failed (FFmpeg fallback disabled for testing).");
                await FfmpegAsync(await ensureFfmpeg(), plan, target, output, progress, ct);
            }
            progress.Report(1);
            return output;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
    }

    /// <summary>
    /// Tries the converters that ship with the app. Returns false (after cleaning up) when the format or
    /// codec is beyond them, so the caller can fall back to FFmpeg.
    /// </summary>
    static bool BuiltInMedia(ConversionPlan plan, FormatInfo target, string output, IProgress<double?> progress, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CONVERTWHEEL_MEDIA_ENGINE") == "ffmpeg") return false;
        try
        {
            if (plan.SourceKind == FormatKind.Image && ImageConverter.TryConvert(plan.SourcePath, output, target.Ext)) return true;
            if (plan.SourceKind is FormatKind.Audio or FormatKind.Video && target.Kind == FormatKind.Audio && Media.AudioEngine.CanEncode(target.Ext))
            {
                Media.AudioEngine.Convert(plan.SourcePath, output, target.Ext, progress, ct);
                return true;
            }
            if (Media.VideoEngine.CanConvert(plan, target))
            {
                Media.VideoEngine.Convert(plan, target, output, progress, ct);
                return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ConversionException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Built-in engine couldn't convert {plan.SourcePath} to {target.Ext}: {ex.Message}");
        }
        // Leave an empty reserved file for the next engine to overwrite.
        try { using (File.Create(output)) { } } catch { }
        return false;
    }

    string ReserveOutputPath(string input, string ext)
    {
        var dir = settings.UseCustomOutputFolder && Directory.Exists(settings.CustomOutputFolder)
            ? settings.CustomOutputFolder!
            : Path.GetDirectoryName(input)!;
        var stem = Path.GetFileNameWithoutExtension(input);
        for (int i = 0; ; i++)
        {
            var path = Path.Combine(dir, i == 0 ? $"{stem}.{ext}" : $"{stem} ({i}).{ext}");
            try
            {
                // Create the file now so two simultaneous conversions can't pick the same name.
                using (new FileStream(path, FileMode.CreateNew)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
            }
        }
    }

    // ---------------------------------------------------------------- FFmpeg

    sealed record Probe(double? Duration, string? VideoCodec, string? AudioCodec);

    async Task FfmpegAsync(string ffmpeg, ConversionPlan plan, FormatInfo target, string output, IProgress<double?> progress, CancellationToken ct)
    {
        var probe = await ProbeAsync(ffmpeg, plan.SourcePath, ct);
        var attempts = new List<List<string>>();
        if (CopyArgs(plan, target, probe) is { } copy) attempts.Add(copy);
        attempts.Add(EncodeArgs(plan, target));

        for (int i = 0; i < attempts.Count; i++)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", plan.SourcePath };
            args.AddRange(attempts[i]);
            args.AddRange(["-progress", "pipe:1", "-nostats", output]);

            var (exit, stderr) = await RunAsync(ffmpeg, args, line => ReportProgress(line, probe.Duration, progress), ct);
            if (exit == 0) return;
            Log.Write($"ffmpeg failed ({exit}): {string.Join(' ', args)}\n{stderr}");
            if (i == attempts.Count - 1) throw new ConversionException(LastError(stderr));
        }
    }

    static async Task<Probe> ProbeAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var (_, info) = await RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", input], null, ct);
        double? duration = null;
        var d = DurationRegex().Match(info);
        if (d.Success)
            duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 +
                       double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);

        string? video = null, audio = null;
        foreach (Match m in StreamRegex().Matches(info))
        {
            if (m.Groups[1].Value == "Video" && video == null && !m.Value.Contains("attached pic")) video = m.Groups[2].Value;
            if (m.Groups[1].Value == "Audio" && audio == null) audio = m.Groups[2].Value;
        }
        return new Probe(duration > 0 ? duration : null, video, audio);
    }

    static void ReportProgress(string line, double? duration, IProgress<double?> progress)
    {
        if (duration is not > 0 || !line.StartsWith("out_time_us=")) return;
        if (long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0)
            progress.Report(Math.Clamp(us / 1e6 / duration.Value, 0, 0.99));
    }

    /// <summary>When the streams already fit the target container, remuxing is instant and lossless.</summary>
    static List<string>? CopyArgs(ConversionPlan plan, FormatInfo target, Probe probe)
    {
        if (plan.SourceKind is not (FormatKind.Audio or FormatKind.Video)) return null;

        if (target.Kind == FormatKind.Audio)
        {
            var codec = target.Ext switch
            {
                "mp3" => "mp3", "m4a" or "aac" => "aac", "opus" => "opus", "ogg" => "vorbis", "flac" => "flac", _ => null,
            };
            return codec != null && probe.AudioCodec == codec
                ? ["-vn", "-sn", "-dn", "-map_metadata", "0", "-c:a", "copy"]
                : null;
        }

        if (target.Kind != FormatKind.Video || probe.VideoCodec == null) return null;
        bool fits = target.Ext switch
        {
            "mkv" => true,
            "mp4" or "mov" => probe.VideoCodec is "h264" or "hevc" or "av1" or "mpeg4" &&
                              probe.AudioCodec is null or "aac" or "mp3" or "alac" or "ac3" or "eac3",
            "webm" => probe.VideoCodec is "vp8" or "vp9" or "av1" && probe.AudioCodec is null or "vorbis" or "opus",
            _ => false,
        };
        if (!fits) return null;
        var args = new List<string> { "-map", "0:v:0", "-map", "0:a?", "-c", "copy" };
        if (target.Ext is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
        return args;
    }

    static List<string> EncodeArgs(ConversionPlan plan, FormatInfo target)
    {
        const string Even = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        var a = new List<string>();
        switch (target.Kind)
        {
            case FormatKind.Audio:
                a.AddRange(["-vn", "-sn", "-dn", "-map_metadata", "0"]);
                a.AddRange(target.Ext switch
                {
                    "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
                    "wav" => ["-c:a", "pcm_s16le"],
                    "flac" => ["-c:a", "flac"],
                    "ogg" => ["-c:a", "libvorbis", "-q:a", "6"],
                    "m4a" => ["-c:a", "aac", "-b:a", "256k"],
                    "opus" => ["-c:a", "libopus", "-b:a", "160k"],
                    "aac" => ["-c:a", "aac", "-b:a", "256k"],
                    "wma" => ["-c:a", "wmav2", "-b:a", "192k"],
                    "aiff" => ["-c:a", "pcm_s16be"],
                    _ => Array.Empty<string>(),
                });
                break;

            case FormatKind.Video:
                a.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"]);
                a.AddRange(target.Ext switch
                {
                    "mp4" or "mov" => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-vf", Even,
                                       "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
                    "mkv" => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-vf", Even, "-c:a", "aac", "-b:a", "192k"],
                    "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                               "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k"],
                    "avi" => ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "3", "-vf", Even, "-c:a", "libmp3lame", "-q:a", "3"],
                    "wmv" => ["-c:v", "wmv2", "-b:v", "5M", "-vf", Even, "-c:a", "wmav2", "-b:a", "192k"],
                    _ => Array.Empty<string>(),
                });
                break;

            case FormatKind.Image when target.Ext == "gif" && plan.SourceKind == FormatKind.Video:
                a.AddRange(["-an", "-vf",
                    "fps=15,scale='min(720,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
                    "-loop", "0"]);
                break;

            case FormatKind.Image when target.Ext == "webp" && plan.SourceExt == "gif":
                a.AddRange(["-an", "-c:v", "libwebp", "-quality", "90", "-loop", "0"]);
                break;

            case FormatKind.Image:
                a.AddRange(["-frames:v", "1", "-update", "1"]);
                a.AddRange(target.Ext switch
                {
                    "jpg" => ["-q:v", "2"],
                    "webp" => ["-c:v", "libwebp", "-quality", "90"],
                    "avif" => ["-c:v", "libaom-av1", "-crf", "28", "-cpu-used", "6", "-still-picture", "1", "-pix_fmt", "yuv420p"],
                    "ico" => ["-vf", "scale=w='min(256,iw)':h='min(256,ih)':force_original_aspect_ratio=decrease"],
                    _ => Array.Empty<string>(),
                });
                break;
        }
        return a;
    }

    static string LastError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Stream mapping") && !l.StartsWith("Press [q]"))
            .ToList();
        var message = lines.LastOrDefault(l => l.Contains("rror") || l.Contains("nvalid") || l.Contains("not ")) ?? lines.LastOrDefault();
        return string.IsNullOrWhiteSpace(message) ? "FFmpeg couldn't convert this file." : message.Length > 220 ? message[..220] + "…" : message;
    }

    // ---------------------------------------------------------------- documents

    /// <summary>
    /// Office gives a pixel-perfect result for its own formats, so it goes first when installed,
    /// then an installed LibreOffice, then the built-in engine. The built-in engine goes first for
    /// plain text and CSV (no layout to preserve) and for PDF output, because Office's PDF export
    /// runs through its print pipeline and can stall for a minute or more on some PCs.
    /// </summary>
    public static bool CanConvertDocumentWithoutDownload(ConversionPlan plan) =>
        BuiltInDocuments.CanRead(plan) || OfficeAutomation.CanOpen(plan) || ToolLocator.FindLibreOffice() != null;

    static async Task DocumentAsync(ConversionPlan plan, FormatInfo target, string output, CancellationToken ct)
    {
        var engines = new List<(string name, Func<Task> run)>();
        bool builtIn = BuiltInDocuments.CanConvert(plan, target);
        bool builtInFirst = plan.SourceExt is "txt" or "csv" || target.Ext == "pdf";
        var builtInEngine = ("built-in", (Func<Task>)(() => RunSta(() => { BuiltInDocuments.Convert(plan, target, output); return true; })));

        if (builtIn && builtInFirst) engines.Add(builtInEngine);
        if (OfficeAutomation.CanConvert(plan, target)) engines.Add(("Microsoft Office", () => OfficeAutomation.ConvertAsync(plan, target, output, ct)));
        if (ToolLocator.FindLibreOffice() != null) engines.Add(("LibreOffice", () => LibreOfficeAsync(plan, target, output, ct)));
        if (builtIn && !builtInFirst) engines.Add(builtInEngine);

        // Testing aid: CONVERTWHEEL_DOC_ENGINE=built-in (or "Microsoft Office", "LibreOffice") forces one engine.
        if (Environment.GetEnvironmentVariable("CONVERTWHEEL_DOC_ENGINE") is { Length: > 0 } forced)
            engines.RemoveAll(e => !e.name.Equals(forced, StringComparison.OrdinalIgnoreCase));
        if (engines.Count == 0) throw new ToolMissingException("LibreOffice");

        Exception? last = null;
        foreach (var (name, run) in engines)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await run();
                if (File.Exists(output) && new FileInfo(output).Length > 0) return;
                last = new ConversionException($"{name} produced an empty file.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write($"Document engine {name} failed for {plan.SourcePath} -> {target.Ext}: {ex}");
                last = ex;
            }
        }
        throw last is ConversionException ? last : new ConversionException(last?.Message ?? "Couldn't convert this document.");
    }

    // ---------------------------------------------------------------- LibreOffice

    static async Task LibreOfficeAsync(ConversionPlan plan, FormatInfo target, string output, CancellationToken ct)
    {
        var soffice = ToolLocator.FindLibreOffice() ?? throw new ToolMissingException("LibreOffice");
        var filter = target.Ext switch
        {
            "docx" => "docx:MS Word 2007 XML",
            "txt" => "txt:Text (encoded):UTF8",
            "xlsx" => "xlsx:Calc MS Excel 2007 XML",
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76",
            "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            _ => target.Ext,
        };

        var temp = Path.Combine(Path.GetTempPath(), "ConvertWheel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // A private profile avoids clashing with a LibreOffice window the user already has open.
            var profile = new Uri(Path.Combine(Log.DataDir, "libreoffice-profile")).AbsoluteUri;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var (exit, stderr) = await RunAsync(soffice,
                [$"-env:UserInstallation={profile}", "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
                 "--convert-to", filter, "--outdir", temp, plan.SourcePath],
                null, timeout.Token);

            var produced = Directory.GetFiles(temp).FirstOrDefault();
            if (produced == null)
            {
                Log.Write($"LibreOffice failed ({exit}): {stderr}");
                throw new ConversionException("LibreOffice couldn't convert this file.");
            }
            File.Move(produced, output, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- helpers

    static async Task<(int exit, string stderr)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onStdout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onStdout?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        process.WaitForExit(); // flush redirected output
        lock (stderr) return (process.ExitCode, stderr.ToString());
    }

    static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?: (Video|Audio): ([A-Za-z0-9_]+)[^\r\n]*")]
    private static partial Regex StreamRegex();
}
