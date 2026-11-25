using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion;

/// <summary>A failure explained in plain words, safe to show as it is.</summary>
public sealed class ConversionException(string message, string? details = null) : Exception(message)
{
    /// <summary>The raw error behind it, for the log and "Copy details".</summary>
    public string? Details { get; } = details;
}

public sealed class ToolMissingException(string tool) : Exception($"{tool} is not installed.")
{
    public string Tool { get; } = tool;
}

/// <param name="Path">The converted file, or a folder when the result is one picture per page or slide.</param>
/// <param name="Problem">Set when the result is worth keeping but may be incomplete: what to tell the user.</param>
public sealed record ConversionResult(string Path, string? Problem = null);

/// <summary>
/// Picks an engine per conversion: Windows imaging (WIC) for common still images, FFmpeg for
/// audio/video and exotic images, and for documents Microsoft Office, LibreOffice or the
/// built-in document engine, whichever is available and best.
/// </summary>
public sealed partial class Converter(AppSettings settings)
{
    /// <summary>How long a tool may go without reading or progressing before it counts as stuck.</summary>
    static TimeSpan StallLimit = TimeSpan.FromMinutes(10);
    static TimeSpan LibreOfficeLimit = TimeSpan.FromMinutes(5);

    public async Task<ConversionResult> ConvertAsync(
        ConversionPlan plan, FormatInfo target, Func<Task<string>> ensureFfmpeg, IProgress<double?> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reservation = ReserveOutput(plan, target.Ext);
        var output = reservation.WorkPath;
        var made = output;
        string? problem = null;
        switch (plan.SourceKind)
        {
            case FormatKind.Archive:
                await Task.Run(() => Archives.Convert(plan.SourcePath, output, target.Ext, progress, ct), ct);
                break;
            case FormatKind.Subtitle:
                await Task.Run(() => Subtitles.Convert(plan.SourcePath, output, target.Ext), ct);
                break;
            case FormatKind.Data:
                await Task.Run(() => DataFiles.Convert(plan.SourcePath, output, target.Ext), ct);
                break;
            case FormatKind.Document when plan.SourceExt == "pdf":
                made = await RunSta(() => PdfSource.Convert(plan.SourcePath, output, target.Ext, progress, ct));
                break;
            case FormatKind.Presentation:
                made = await PresentationAsync(plan, target, output, progress, ct);
                break;
            case FormatKind.Document or FormatKind.Spreadsheet:
                await DocumentAsync(plan, target, output, ct);
                break;
            default:
                problem = await MediaAsync(plan, target, output, ensureFfmpeg, progress, ct);
                break;
        }
        ct.ThrowIfCancellationRequested();
        CheckNotEmpty(made);
        // From here on it's done: cancelling no longer undoes anything.
        var result = new ConversionResult(reservation.Commit(made), problem);
        progress.Report(1);
        return result;
    }

    static void CheckNotEmpty(string made)
    {
        bool empty = Directory.Exists(made)
            ? Directory.GetFiles(made) is var pages && (pages.Length == 0 || pages.Any(p => new FileInfo(p).Length == 0))
            : !File.Exists(made) || new FileInfo(made).Length == 0;
        if (empty) throw new ConversionException("There was nothing in this file to convert.", "The converter made an empty file.");
    }

    /// <returns>What to tell the user if the result may be incomplete.</returns>
    static async Task<string?> MediaAsync(ConversionPlan plan, FormatInfo target, string output, Func<Task<string>> ensureFfmpeg, IProgress<double?> progress, CancellationToken ct)
    {
        var forced = Environment.GetEnvironmentVariable("CONVERTWHEEL_MEDIA_ENGINE");
        bool builtIn;
        try { builtIn = await RunSta(() => BuiltInMedia(plan, target, output, progress, ct)); }
        catch (ConversionException ex) when (plan.SourceKind == FormatKind.Video && target.Kind == FormatKind.Audio && ex.Message.Contains("no audio"))
        {
            throw new ConversionException(ErrorText.NoAudio, ex.Message);
        }
        if (builtIn)
        {
            var problem = await CheckMediaAsync(plan, target, output, null, ensureFfmpeg, ct);
            // Windows gave up part-way through a file that isn't cut short: FFmpeg copes with more damage.
            if (problem is not { tryFfmpeg: true } || forced == "built-in") return problem?.text;
            Log.Write($"Built-in engine stopped early on {plan.SourcePath}: {problem.Value.text}");
        }
        // Formats Windows has no codec for: the FFmpeg bundled inside the app.
        else if (forced == "built-in") throw new ConversionException("Built-in engine failed (FFmpeg fallback disabled for testing).");

        if (plan.SourceExt == "webp" && target.Kind == FormatKind.Video)
        {
            // FFmpeg can't decode animated WebP: decode it ourselves into a temporary MP4 first.
            var temp = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}.mp4");
            try
            {
                await RunSta(() =>
                {
                    Media.MF.EnsureStarted();
                    Media.VideoEngine.FramesToVideo(Media.Animation.Read(plan.SourcePath), temp, "mp4", p => progress.Report(p * 0.3), ct);
                    return true;
                });
                await FfmpegAsync(await ensureFfmpeg(), plan with { SourcePath = temp, SourceExt = "mp4", SourceKind = FormatKind.Video }, target, output, progress, ct);
            }
            finally { TryDelete(temp); }
            return null;
        }
        var run = await FfmpegAsync(await ensureFfmpeg(), plan, target, output, progress, ct);
        return (await CheckMediaAsync(plan, target, output, run, ensureFfmpeg, ct))?.text;
    }

    /// <summary>
    /// PowerPoint (when installed) gives a perfect PDF/PPTX/ODP; otherwise, and for slide pictures,
    /// the built-in presentation engine renders the slides itself.
    /// </summary>
    static async Task<string> PresentationAsync(ConversionPlan plan, FormatInfo target, string output, IProgress<double?> progress, CancellationToken ct)
    {
        if (OfficeEncryption.IsProtected(plan.SourcePath)) throw new ConversionException(ErrorText.Password);
        var forced = Environment.GetEnvironmentVariable("CONVERTWHEEL_DOC_ENGINE");
        bool builtInCan = Presentations.PresentationEngine.CanConvert(plan.SourceExt, target.Ext);
        if (forced != "built-in" && OfficeAutomation.CanConvert(plan, target))
        {
            try
            {
                await OfficeAutomation.ConvertAsync(plan, target, output, ct);
                if (new FileInfo(output).Length > 0) return output;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (builtInCan) { Log.Write($"PowerPoint failed, using the built-in engine: {ex.Message}"); }
        }
        if (!builtInCan)
        {
            if (ToolLocator.FindLibreOffice() != null)
            {
                await LibreOfficeAsync(plan, target, output, ct);
                return output;
            }
            throw new ConversionException($"Can't convert {plan.SourceExt.ToUpperInvariant()} to {target.Label}.");
        }
        return await RunSta(() => Presentations.PresentationEngine.Convert(plan.SourcePath, output, target.Ext, progress, ct));
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
            if (plan.SourceKind == FormatKind.Image && ImageConverter.TryConvert(plan.SourcePath, output, target.Ext, ct)) return true;
            if (plan.SourceKind is FormatKind.Audio or FormatKind.Video && target.Kind == FormatKind.Audio && Media.AudioEngine.CanEncode(target.Ext))
            {
                Media.AudioEngine.Convert(plan.SourcePath, output, target.Ext, progress, ct);
                return new FileInfo(output).Length > 0;
            }
            if (Media.VideoEngine.CanConvert(plan, target))
            {
                Media.VideoEngine.Convert(plan, target, output, progress, ct);
                return new FileInfo(output).Length > 0;
            }
        }
        catch (OperationCanceledException) { throw; }
        // A file that was cut short goes on to FFmpeg, which saves what's there; the check afterwards says what's missing.
        catch (ConversionException ex) when (!ex.Message.Contains("cut short")) { throw; }
        catch (IOException ex) when (ErrorText.Describe(ex) == ErrorText.DiskFull) { throw; } // another engine won't find more room
        catch (Exception ex)
        {
            Log.Write($"Built-in engine couldn't convert {plan.SourcePath} to {target.Ext}: {ex.Message}");
        }
        // Leave an empty file for the next engine to overwrite.
        try { using (File.Create(output)) { } } catch { }
        return false;
    }

    OutputReservation ReserveOutput(ConversionPlan plan, string ext)
    {
        var input = plan.SourcePath;
        var dir = settings.UseCustomOutputFolder && Directory.Exists(settings.CustomOutputFolder)
            ? settings.CustomOutputFolder!
            : Path.GetDirectoryName(input)!;
        var stem = Path.GetFileNameWithoutExtension(input);
        // backup.tar.gz → backup.zip, not backup.tar.zip
        if (plan.SourceKind == FormatKind.Archive && stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        if (ext == "tgz") ext = "tar.gz";
        return new OutputReservation(dir, stem, ext);
    }

    // ---------------------------------------------------------------- FFmpeg

    /// <param name="Reliable">False when FFmpeg only worked the duration out from the bitrate.</param>
    /// <param name="FineAudio">The sound is lossless and finer than 16 bits. Lossy sound also decodes to more, but has nothing to put in it.</param>
    sealed record Probe(double? Duration, bool Reliable, string? VideoCodec, string? AudioCodec, bool AnyStream, int AudioTracks, bool FineAudio);

    sealed record FfmpegRun(string Stderr, double? OutTime, Probe Probe);

    /// <summary>
    /// The kinds of file FFmpeg may open. Everything that only points at other files or addresses (playlists,
    /// concat lists, DASH, SDP, AviSynth scripts, numbered picture sequences…) is left out, so a file
    /// can't make a conversion pull in something else.
    /// </summary>
    const string AllowedFormats =
        "aa,aac,aax,ac3,ac4,act,adf,adx,aiff,alaw,amr,amrnb,amrwb,anm,apc,ape,apm,apng,aptx,aptx_hd,asf,asf_o,ast,au,av1,avi,avr,avs2,avs3," +
        "bfi,bink,binka,boa,bonk,brstm,c93,caf,cavsvideo,cdg,cdxl,cine,codec2,dfa,dfpwm,dhav,dirac,dnxhd,dsf,dsicin,dss,dts,dtshd,dv,dxa,ea,eac3," +
        "film_cpk,filmstrip,flac,flic,flv,g722,g723_1,g726,g729,gif,gsm,gxf,h261,h263,h264,hca,hcom,hevc,iamf,ico,idcin,iff,ifv,ilbc,ingenient," +
        "ipmovie,ircam,iss,iv8,ivf,jpegxl_anim,jv,kux,kvag,laf,libgme,libopenmpt,live_flv,loas,lvf,lxf,m4v,matroska,webm,mgsts,mjpeg,mjpeg_2000,mlp,mlv,mm,mmf," +
        "mov,mp4,m4a,3gp,3g2,mj2,mp3,mpc,mpc8,mpeg,mpegts,mpegtsraw,mpegvideo,msf,msnwctcp,mtv,mulaw,mv,mvi,mxf,mxg,nistsphere,nsp,nsv,nut,nuv," +
        "obu,ogg,oma,osq,paf,pmp,pva,pvf,qcp,qoa,r3d,rka,rl2,rm,roq,rpl,rsd,rso,sbc,sds,sdx,shn,siff,sln,smjpeg,smk,sol,sox,spdif,swf,tak," +
        "thp,tiertexseq,tmv,truehd,tta,tty,ty,vc1,vc1test,vividas,vivo,vmd,voc,vqf,vvc,w64,wav,wc3movie,wsaud,wsd,wsvqa,wtv,wv,wve,xa,xmv,xvag,xwma,yop,yuv4mpegpipe," +
        "bmp_pipe,cri_pipe,dds_pipe,dpx_pipe,exr_pipe,gem_pipe,gif_pipe,hdr_pipe,image2pipe,j2k_pipe,jpeg_pipe,jpegls_pipe,jpegxl_pipe,jpegxs_pipe," +
        "pam_pipe,pbm_pipe,pcx_pipe,pfm_pipe,pgm_pipe,pgmyuv_pipe,pgx_pipe,phm_pipe,photocd_pipe,pictor_pipe,png_pipe,ppm_pipe,psd_pipe,qdraw_pipe," +
        "qoi_pipe,sgi_pipe,sunrast_pipe,svg_pipe,tiff_pipe,vbn_pipe,webp_pipe,xbm_pipe,xpm_pipe,xwd_pipe";

    /// <summary>Pictures FFmpeg reads one file at a time, by name.</summary>
    static readonly HashSet<string> PictureInputs =
        [".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".tif", ".tiff", ".tga", ".webp", ".jp2", ".j2k", ".exr", ".hdr", ".pcx", ".qoi", ".dds", ".jxl"];

    /// <summary>The input and the options before it: local files only, and that one file only.</summary>
    static List<string> InputArgs(string input)
    {
        var args = new List<string> { "-protocol_whitelist", "file,pipe" };
        // Otherwise "pic%03d.png" would mean a numbered sequence.
        if (PictureInputs.Contains(Path.GetExtension(input).ToLowerInvariant())) args.AddRange(["-format_whitelist", "image2", "-f", "image2", "-pattern_type", "none"]);
        else args.AddRange(["-format_whitelist", AllowedFormats]);
        args.AddRange(["-i", input]);
        return args;
    }

    static async Task<FfmpegRun> FfmpegAsync(string ffmpeg, ConversionPlan plan, FormatInfo target, string output, IProgress<double?> progress, CancellationToken ct)
    {
        var probe = await ProbeAsync(ffmpeg, plan.SourcePath, ct);
        if (target.Kind == FormatKind.Audio && probe is { AnyStream: true, AudioCodec: null })
            throw new ConversionException(plan.SourceKind == FormatKind.Video ? ErrorText.NoAudio : "This file has no audio to convert.");
        var attempts = new List<List<string>>();
        if (CopyArgs(plan, target, probe) is { } copy) attempts.Add(copy);
        attempts.Add(EncodeArgs(plan, target, probe));
        // A container or encoder that can't take every audio track still gets the first one.
        if (target.Kind == FormatKind.Video && probe.AudioTracks > 1) attempts.Add(EncodeArgs(plan, target, probe, allAudio: false));
        // If Windows' H.264 encoder (used through FFmpeg) is unavailable, fall back to MPEG-4.
        if (target.Ext is "mp4" or "mov" or "mkv")
            attempts.Add(EncodeArgs(plan, target, probe, mpeg4Fallback: true, allAudio: probe.AudioTracks <= 1));

        for (int i = 0; ; i++)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "level+verbose" };
            args.AddRange(InputArgs(plan.SourcePath));
            args.AddRange(attempts[i]);
            args.AddRange(["-progress", "pipe:1", "-nostats", output]);

            double? outTime = null;
            var (exit, stderr) = await RunAsync(ffmpeg, args, line =>
            {
                if (OutTime(line) is not { } time) return;
                outTime = time;
                if (probe.Duration is { } total) progress.Report(Math.Clamp(time / total, 0, 0.99));
            }, ct);
            bool nothing = exit == 0 && (stderr.Contains("Output file is empty, nothing was encoded") || NothingWrittenRegex().IsMatch(stderr));
            if (exit == 0 && !nothing) return new FfmpegRun(stderr, outTime, probe);
            Log.Write($"ffmpeg {(nothing ? "made an empty file" : $"failed ({exit})")}: {string.Join(' ', args)}\n{ImportantLines(stderr)}");
            if (i < attempts.Count - 1) continue;
            if (nothing) throw new ConversionException(target.Kind == FormatKind.Audio ? "This file has no audio to convert." : "There's nothing in this file to convert.");
            throw new ConversionException(ErrorText.FromFfmpeg(ImportantLines(stderr)), ImportantLines(stderr));
        }
    }

    static double? OutTime(string line) =>
        line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan("out_time_us=".Length), out var us) && us > 0 ? us / 1e6 : null;

    static async Task<Probe> ProbeAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var (_, info) = await RunAsync(ffmpeg, ["-hide_banner", "-nostdin", .. InputArgs(input)], null, ct);
        double? duration = null;
        var d = DurationRegex().Match(info);
        if (d.Success)
            duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 +
                       double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);

        string? video = null, audio = null;
        int tracks = 0;
        bool fine = false;
        foreach (Match m in StreamRegex().Matches(info))
        {
            if (m.Groups[1].Value == "Video" && video == null && !m.Value.Contains("attached pic")) video = m.Groups[2].Value;
            if (m.Groups[1].Value != "Audio" || tracks++ > 0) continue;
            audio = m.Groups[2].Value;
            fine = LosslessAudio(audio, m.Value) && FineSampleRegex().IsMatch(m.Value);
        }
        return new Probe(duration > 0 ? duration : null, !info.Contains("Estimating duration from bitrate"), video, audio, info.Contains("Stream #0:"), tracks, fine);
    }

    static bool LosslessAudio(string codec, string line) =>
        codec.StartsWith("pcm_") || codec.StartsWith("dsd_") || line.Contains("DTS-HD MA") ||
        codec is "flac" or "alac" or "wavpack" or "tta" or "ape" or "truehd" or "mlp" or "tak" or "shorten" or "mp4als" or "wmalossless" or "ralf" or "dst" or "osq";

    /// <summary>
    /// Whether a finished sound or video file covers all of its source. A download that stopped half-way
    /// converts "successfully" into half a file, and that must never replace the original unnoticed.
    /// </summary>
    static async Task<(string text, bool tryFfmpeg)?> CheckMediaAsync(
        ConversionPlan plan, FormatInfo target, string made, FfmpegRun? run, Func<Task<string>> ensureFfmpeg, CancellationToken ct)
    {
        if (plan.SourceKind is not (FormatKind.Audio or FormatKind.Video) || target.Kind is not (FormatKind.Audio or FormatKind.Video)) return null;
        var claims = MediaLength.Read(plan.SourcePath);
        double? expected = claims.Seconds ?? (run?.Probe is { Reliable: true } probe ? probe.Duration : null);
        double? actual = MediaLength.MadeLength(made, target.Ext) is { } length ? Math.Max(length, run?.OutTime ?? 0) : run?.OutTime;
        bool shorter = expected is { } e && actual is { } a && MediaLength.MuchShorter(a, e);

        if (claims.CutOff || run != null && CutOffRegex().IsMatch(run.Stderr))
            return (shorter ? Only(expected!.Value) : "The file may be damaged: its end is missing.", false);
        if (!shorter)
            return run != null && DecodeErrors(run.Stderr) ? ("The file may be damaged: parts of it couldn't be read.", false) : null;

        // A track can simply be shorter than its file (a video whose sound stops early, subtitles that run on),
        // so see how far the tracks themselves go before calling anything damaged.
        try
        {
            var ffmpeg = await ensureFfmpeg();
            string[] converted = target.Kind == FormatKind.Audio ? ["-map", "0:a:0"] : ["-map", "0:v:0?", "-map", "0:a?"];
            if (await TrackEndAsync(ffmpeg, plan.SourcePath, converted, ct) is { } end)
            {
                if (MediaLength.MuchShorter(actual!.Value, end)) return (Only(end), run == null);
                if (claims.Whole) return null;
                if (await TrackEndAsync(ffmpeg, plan.SourcePath, ["-map", "0", "-ignore_unknown"], ct) is { } all && !MediaLength.MuchShorter(all, expected!.Value)) return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"Couldn't check {plan.SourcePath}: {ex.Message}");
            if (target.Kind == FormatKind.Audio && plan.SourceKind == FormatKind.Video) return null;
        }
        return (Only(expected!.Value), false);

        string Only(double of) => $"The file may be damaged: only {MediaLength.Clock(actual!.Value)} of {MediaLength.Clock(of)} could be converted.";
    }

    /// <summary>When the last of these tracks ends, read without decoding; null if that can't be told.</summary>
    static async Task<double?> TrackEndAsync(string ffmpeg, string input, string[] tracks, CancellationToken ct)
    {
        double? end = null;
        var (exit, _) = await RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", .. InputArgs(input), .. tracks, "-c", "copy", "-progress", "pipe:1", "-nostats", "-f", "null", "-"],
            line => end = OutTime(line) ?? end, ct);
        return exit == 0 ? end : null;
    }

    /// <summary>More than the odd bad packet that plenty of healthy files have.</summary>
    static bool DecodeErrors(string stderr)
    {
        foreach (Match m in DecodeRegex().Matches(stderr))
            if (long.TryParse(m.Groups[1].Value, out var packets) && long.TryParse(m.Groups[2].Value, out var errors) && errors > Math.Max(3, packets / 100)) return true;
        return false;
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
            // MKV holds anything, but old codecs (Flash, early WMV) only play in a few apps: re-encode those.
            "mkv" => probe.VideoCodec is "h264" or "hevc" or "av1" or "vp8" or "vp9" or "mpeg4" or "mpeg2video" &&
                     (probe.AudioCodec is null || !probe.AudioCodec.StartsWith("wma") && probe.AudioCodec is not ("nellymoser" or "adpcm_swf")),
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

    static List<string> EncodeArgs(ConversionPlan plan, FormatInfo target, Probe probe, bool mpeg4Fallback = false, bool allAudio = true)
    {
        const string Even = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        string[] h264 = mpeg4Fallback ? ["-c:v", "mpeg4", "-q:v", "3"] : ["-c:v", "h264_mf", "-b:v", "8M", "-pix_fmt", "nv12"];
        var a = new List<string>();
        switch (target.Kind)
        {
            case FormatKind.Audio:
                a.AddRange(["-vn", "-sn", "-dn", "-map_metadata", "0"]);
                a.AddRange(target.Ext switch
                {
                    "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
                    "wav" => ["-c:a", probe.FineAudio ? "pcm_s24le" : "pcm_s16le"],
                    "flac" => ["-c:a", "flac"],
                    "ogg" => ["-c:a", "libvorbis", "-q:a", "6"],
                    "m4a" => ["-c:a", "aac", "-b:a", "256k"],
                    "opus" => ["-c:a", "libopus", "-b:a", "160k"],
                    "aac" => ["-c:a", "aac", "-b:a", "256k"],
                    "wma" => ["-c:a", "wmav2", "-b:a", "192k"],
                    "aiff" => ["-c:a", probe.FineAudio ? "pcm_s24be" : "pcm_s16be"],
                    "ac3" => ["-c:a", "ac3", "-b:a", "384k"],
                    "m4r" => ["-c:a", "aac", "-b:a", "256k", "-f", "ipod"],
                    _ => Array.Empty<string>(),
                });
                break;

            case FormatKind.Video:
                // This is the LGPL FFmpeg build: H.264 comes from Windows' own encoder (h264_mf), not x264.
                a.AddRange(["-map", "0:v:0", "-map", allAudio ? "0:a?" : "0:a:0?", "-sn", "-dn"]);
                a.AddRange(target.Ext switch
                {
                    "mp4" or "mov" => [.. h264, "-vf", Even,
                                       "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
                    "mkv" => [.. h264, "-vf", Even, "-c:a", "aac", "-b:a", "192k"],
                    "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                               "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k"],
                    "avi" => ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "3", "-vf", Even, "-c:a", "libmp3lame", "-q:a", "3"],
                    "wmv" => ["-c:v", "wmv2", "-b:v", "5M", "-vf", Even, "-c:a", "wmav2", "-b:a", "192k"],
                    "mpg" => ["-c:v", "mpeg2video", "-b:v", "8M", "-maxrate", "9M", "-bufsize", "1835k", "-pix_fmt", "yuv420p", "-vf", Even,
                              "-c:a", "mp2", "-b:a", "224k", "-ar", "48000"],
                    _ => Array.Empty<string>(),
                });
                break;

            case FormatKind.Image when target.Ext == "gif" && plan.SourceKind == FormatKind.Video:
                a.AddRange(["-an", "-vf",
                    "fps=15,scale='min(720,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
                    "-loop", "0"]);
                break;

            case FormatKind.Image when target.Ext == "webp" && plan.SourceKind == FormatKind.Video:
                a.AddRange(["-an", "-vf", "fps=15,scale='min(720,iw)':-2:flags=lanczos", "-c:v", "libwebp_anim", "-quality", "75", "-loop", "0"]);
                break;

            case FormatKind.Image when target.Ext == "webp" && plan.SourceExt == "gif":
                a.AddRange(["-an", "-c:v", "libwebp_anim", "-quality", "90", "-loop", "0"]);
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

    /// <summary>The part of FFmpeg's log that says what went wrong.</summary>
    static string ImportantLines(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var important = lines.Where(l => l.Contains("[error]") || l.Contains("[fatal]") || l.Contains("[panic]")).ToArray();
        return string.Join("\r\n", (important.Length > 0 ? important : lines).TakeLast(12));
    }

    // ---------------------------------------------------------------- documents

    /// <summary>
    /// Office gives a pixel-perfect result for its own formats, so it goes first when installed,
    /// then an installed LibreOffice, then the built-in engine. The built-in engine goes first for
    /// plain text and CSV (no layout to preserve), for web pages (Office's need a folder of loose
    /// files next to them) and for PDF output, because Office's PDF export runs through its print
    /// pipeline and can stall for a minute or more on some PCs.
    /// </summary>
    static async Task DocumentAsync(ConversionPlan plan, FormatInfo target, string output, CancellationToken ct)
    {
        // Office would wait for a password nobody can type. A workbook may still open with Excel's
        // well-known default password, which the built-in reader tries.
        bool locked = OfficeEncryption.IsProtected(plan.SourcePath);
        if (locked && plan.SourceKind != FormatKind.Spreadsheet) throw new ConversionException(ErrorText.Password);

        var engines = new List<(string name, Func<Task> run)>();
        bool builtIn = BuiltInDocuments.CanConvert(plan, target);
        bool builtInFirst = plan.SourceExt is "txt" or "csv" || target.Ext is "pdf" or "html" ||
                            (plan.SourceKind == FormatKind.Spreadsheet && target.Ext is "txt" or "md");
        var builtInEngine = ("built-in", (Func<Task>)(() => RunSta(() => { BuiltInDocuments.Convert(plan, target, output, ct); return true; })));

        if (builtIn && builtInFirst) engines.Add(builtInEngine);
        if (!locked && OfficeAutomation.CanConvert(plan, target)) engines.Add(("Microsoft Office", () => OfficeAutomation.ConvertAsync(plan, target, output, ct)));
        if (!locked && ToolLocator.FindLibreOffice() != null) engines.Add(("LibreOffice", () => LibreOfficeAsync(plan, target, output, ct)));
        if (builtIn && !builtInFirst) engines.Add(builtInEngine);

        // Testing aid: CONVERTWHEEL_DOC_ENGINE=built-in (or "Microsoft Office", "LibreOffice") forces one engine.
        if (Environment.GetEnvironmentVariable("CONVERTWHEEL_DOC_ENGINE") is { Length: > 0 } forced)
            engines.RemoveAll(e => !e.name.Equals(forced, StringComparison.OrdinalIgnoreCase));
        if (engines.Count == 0) throw new ConversionException(locked ? ErrorText.Password : $"Can't convert {plan.SourceExt.ToUpperInvariant()} to {target.Label}.");

        Exception? last = null, plain = null;
        foreach (var (name, run) in engines)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await run();
                if (File.Exists(output) && new FileInfo(output).Length > 0) return;
                last = new ConversionException("There was nothing in this file to convert.", $"{name} produced an empty file.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write($"Document engine {name} failed for {plan.SourcePath} -> {target.Ext}: {ex}");
                last = ex;
            }
            // The built-in engine explains itself best, so its reason isn't lost to a later engine's.
            if (name == "built-in" && last is ConversionException) plain = last;
        }
        if (locked) throw new ConversionException(ErrorText.Password, last?.ToString());
        ExceptionDispatchInfo.Throw(plain ?? last!);
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
            // Pictures inside the page rather than as loose files next to it.
            "html" => plan.SourceKind == FormatKind.Spreadsheet ? "html:HTML (StarCalc):EmbedImages" : "html:HTML (StarWriter):EmbedImages",
            _ => target.Ext,
        };

        var temp = Path.Combine(Path.GetTempPath(), "ConvertWheel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // A private profile avoids clashing with a LibreOffice window the user already has open.
            var profile = new Uri(Path.Combine(temp, "profile")).AbsoluteUri;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LibreOfficeLimit);
            int exit;
            string stderr;
            try
            {
                (exit, stderr) = await RunAsync(soffice,
                    [$"-env:UserInstallation={profile}", "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
                     "--convert-to", filter, "--outdir", temp, plan.SourcePath],
                    null, timeout.Token, watch: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ConversionException("LibreOffice stopped responding.");
            }

            var produced = Directory.GetFiles(temp, $"*.{target.Ext}").FirstOrDefault();
            if (produced == null)
            {
                Log.Write($"LibreOffice failed ({exit}): {stderr}");
                throw new ConversionException("LibreOffice couldn't convert this file.", stderr);
            }
            File.Move(produced, output, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <param name="watch">Stop the tool when it neither reads anything nor makes progress for a long time.</param>
    static async Task<(int exit, string stderr)> RunAsync(string exe, IEnumerable<string> args, Action<string>? onStdout, CancellationToken ct, bool watch = true)
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

        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = psi };
        var stderr = new BoundedText();
        long lastSign = Environment.TickCount64;
        var values = new Dictionary<string, string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (IsNews(values, e.Data)) Interlocked.Exchange(ref lastSign, Environment.TickCount64);
            onStdout?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Add(e.Data); };
        process.Start();
        ProcessJob.Attach(process);
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var exited = process.WaitForExitAsync(ct);
            ulong read = 0;
            while (watch && await Task.WhenAny(exited, Task.Delay(StallLimit / 20, ct)) != exited)
            {
                ct.ThrowIfCancellationRequested();
                // Some steps (building a GIF's palette) report nothing for a long time, but do keep reading.
                if (BytesRead(process) is var now && now != read)
                {
                    read = now;
                    Interlocked.Exchange(ref lastSign, Environment.TickCount64);
                }
                if (Environment.TickCount64 - Interlocked.Read(ref lastSign) < StallLimit.TotalMilliseconds) continue;
                Log.Write($"{exe} made no progress for {StallLimit}: {string.Join(' ', psi.ArgumentList)}");
                throw new ConversionException("The conversion got stuck, so it was stopped.", stderr.ToString());
            }
            await exited;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConversionException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            // Its files are only free once it's gone, but a process stuck on a dead network drive may never go.
            for (int i = 0; i < 250 && !process.HasExited; i++) await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        process.WaitForExit(); // flush redirected output
        return (process.ExitCode, stderr.ToString());
    }

    /// <summary>Whether a "key=value" progress line moves the conversion on (FFmpeg repeats them all the time).</summary>
    static bool IsNews(Dictionary<string, string> values, string line)
    {
        int at = line.IndexOf('=');
        if (at < 0 || line.AsSpan(0, at) is not ("frame" or "out_time_us" or "total_size")) return false;
        var (key, value) = (line[..at], line[(at + 1)..]);
        if (values.GetValueOrDefault(key) == value) return false;
        values[key] = value;
        return true;
    }

    static ulong BytesRead(Process process)
    {
        try { return GetProcessIoCounters(process.Handle, out var io) ? io.ReadBytes : 0; }
        catch (InvalidOperationException) { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    /// <summary>The beginning and end of a log that may be endless.</summary>
    sealed class BoundedText
    {
        const int Half = 48 * 1024;
        readonly StringBuilder _head = new();
        readonly Queue<string> _tail = new();
        int _tailLength;
        bool _cut;

        public void Add(string line)
        {
            if (line.Length > 4096) line = line[..4096];
            lock (_head)
            {
                if (_head.Length < Half)
                {
                    _head.AppendLine(line);
                    return;
                }
                _tail.Enqueue(line);
                _tailLength += line.Length + 2;
                while (_tailLength > Half)
                {
                    _tailLength -= _tail.Dequeue().Length + 2;
                    _cut = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_head) return _head + (_cut ? "…" + Environment.NewLine : "") + string.Join(Environment.NewLine, _tail);
        }
    }

    static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            T result;
            try { result = func(); }
            catch (Exception ex)
            {
                EndDispatcher();
                tcs.SetException(ex);
                return;
            }
            EndDispatcher();
            tcs.SetResult(result);
        })
        { IsBackground = true, Name = "Converter" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;

        // WPF objects give the thread a dispatcher, which keeps everything made on it alive unless it's shut down.
        static void EndDispatcher()
        {
            try { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Stream #\d+:\d+.*?: (Video|Audio): ([A-Za-z0-9_]+)[^\r\n]*")]
    private static partial Regex StreamRegex();

    [GeneratedRegex(@"Input stream #\d+:\d+ \(\w+\): (\d+) packets read[^\r\n]*? (\d+) decode errors")]
    private static partial Regex DecodeRegex();

    [GeneratedRegex(@"Total: \d+ packets \(0 bytes\) muxed")]
    private static partial Regex NothingWrittenRegex();

    [GeneratedRegex(@", (s32p?|fltp?|dblp?)\b")]
    private static partial Regex FineSampleRegex();

    [GeneratedRegex(@"\[error\][^\r\n]*(partial file|File ended prematurely)")]
    private static partial Regex CutOffRegex();
}
