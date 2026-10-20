using System.IO.Compression;
using System.Net.Http;

namespace ConvertWheel.Conversion;

/// <summary>Finds FFmpeg and LibreOffice, and can fetch FFmpeg on first use.</summary>
public sealed class ToolLocator(AppSettings settings)
{
    const string FfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    public const string LibreOfficeUrl = "https://www.libreoffice.org/download/download-libreoffice/";

    public static string ToolsDir { get; } = Path.Combine(Log.DataDir, "tools");

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

    /// <summary>Downloads the FFmpeg build and extracts just ffmpeg.exe into the tools folder.</summary>
    public async Task<string> DownloadFfmpegAsync(IProgress<double?> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        var zipPath = Path.Combine(ToolsDir, "ffmpeg-download.zip");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var response = await http.GetAsync(FfmpegZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(zipPath);
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
}
