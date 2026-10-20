using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ConvertWheel.Conversion;

/// <summary>Finds FFmpeg and LibreOffice, and can fetch either into the app's private tools folder.</summary>
public sealed partial class ToolLocator(AppSettings settings)
{
    // gyan.dev's "essentials" build has every codec we use at roughly half the size of the full builds.
    static readonly string[] FfmpegZipUrls =
    [
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    ];
    const string LibreOfficeIndexUrl = "https://download.documentfoundation.org/libreoffice/stable/";
    const string LibreOfficeFallbackVersion = "26.8.1";

    public const int FfmpegDownloadMb = 110;
    public const int LibreOfficeDownloadMb = 360;

    public static string ToolsDir { get; } = Path.Combine(Log.DataDir, "tools");
    static string LibreOfficeDir => Path.Combine(ToolsDir, "LibreOffice");

    public string? FindFfmpeg()
    {
        var candidates = new List<string?>
        {
            settings.FfmpegPath,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(ToolsDir, "ffmpeg.exe"),
        };
        candidates.AddRange(SearchPath("ffmpeg.exe"));
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    public static string? FindLibreOffice()
    {
        var candidates = new List<string?>();
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            candidates.Add(Path.Combine(Environment.GetFolderPath(root), "LibreOffice", "program", "soffice.exe"));
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\soffice.exe");
            candidates.Add(key?.GetValue(null) as string);
        }
        catch { }
        candidates.Add(Path.Combine(LibreOfficeDir, "program", "soffice.exe"));
        candidates.AddRange(SearchPath("soffice.exe"));
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    static IEnumerable<string> SearchPath(string exe)
    {
        // Read the persisted PATH too, so tools installed after this app started are still found.
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        foreach (var dir in paths.Where(p => p != null).SelectMany(p => p!.Split(';', StringSplitOptions.RemoveEmptyEntries)))
        {
            string full;
            try { full = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), exe); }
            catch { continue; }
            yield return full;
        }
    }

    /// <summary>Downloads an FFmpeg build and extracts just ffmpeg.exe into the tools folder.</summary>
    public async Task<string> DownloadFfmpegAsync(IProgress<double?> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        var zipPath = Path.Combine(ToolsDir, "ffmpeg-download.zip");
        try
        {
            Exception? last = null;
            foreach (var url in FfmpegZipUrls)
            {
                try
                {
                    await DownloadAsync(url, zipPath, progress, ct);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Log.Write($"FFmpeg download from {url} failed: {ex.Message}");
                    last = ex;
                }
            }
            if (last != null) throw last;

            progress.Report(null);
            var target = Path.Combine(ToolsDir, "ffmpeg.exe");
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The FFmpeg download didn't contain ffmpeg.exe.");
                entry.ExtractToFile(target, overwrite: true);
            }, ct);
            return target;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    /// <summary>
    /// Downloads the official LibreOffice installer and unpacks it into the tools folder with an
    /// administrative extract: nothing is installed system-wide and no admin rights are needed.
    /// </summary>
    public static async Task<string> DownloadLibreOfficeAsync(IProgress<double?> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        var msi = Path.Combine(ToolsDir, "libreoffice-download.msi");
        var staging = Path.Combine(ToolsDir, "LibreOffice-unpacking");
        try
        {
            var version = await LatestLibreOfficeVersionAsync(ct);
            var url = $"{LibreOfficeIndexUrl}{version}/win/x86_64/LibreOffice_{version}_Win_x86-64.msi";
            await DownloadAsync(url, msi, progress, ct);

            progress.Report(null);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/a", msi, "/qn", $"TARGETDIR={staging}" }) psi.ArgumentList.Add(arg);
            using (var process = Process.Start(psi)!)
            {
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) throw new ConversionException($"Unpacking LibreOffice failed (error {process.ExitCode}).");
            }

            var soffice = Directory.EnumerateFiles(staging, "soffice.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new ConversionException("The LibreOffice download didn't contain soffice.exe.");
            var root = Path.GetDirectoryName(Path.GetDirectoryName(soffice)!)!; // ...\program\soffice.exe -> install root
            if (Directory.Exists(LibreOfficeDir)) Directory.Delete(LibreOfficeDir, recursive: true);
            Directory.Move(root, LibreOfficeDir);
            return FindLibreOffice() ?? throw new ConversionException("LibreOffice couldn't be set up.");
        }
        finally
        {
            try { File.Delete(msi); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    static async Task<string> LatestLibreOfficeVersionAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var html = await http.GetStringAsync(LibreOfficeIndexUrl, ct);
            var latest = VersionLinkRegex().Matches(html)
                .Select(m => Version.TryParse(m.Groups[1].Value, out var v) ? v : null)
                .Where(v => v != null)
                .Max();
            if (latest != null) return latest.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Write($"Couldn't look up the latest LibreOffice version: {ex.Message}");
        }
        return LibreOfficeFallbackVersion;
    }

    static async Task DownloadAsync(string url, string path, IProgress<double?> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ConvertWheel/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(path);
        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            progress.Report(total > 0 ? (double)read / total.Value : null);
        }
    }

    [GeneratedRegex(@"href=""(\d+\.\d+\.\d+)/""")]
    private static partial Regex VersionLinkRegex();
}
