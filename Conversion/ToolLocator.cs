namespace ConvertWheel.Conversion;

/// <summary>Finds FFmpeg (bundled, or one already on this PC) and an installed LibreOffice, if any.</summary>
public sealed class ToolLocator
{
    public static string ToolsDir { get; } = Path.Combine(Log.DataDir, "tools");

    public string? FindFfmpeg()
    {
        var candidates = new List<string?>
        {
            BundledTools.ReadyFfmpeg(),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(ToolsDir, "ffmpeg.exe"),
        };
        candidates.AddRange(SearchPath("ffmpeg.exe"));
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <summary>An installed LibreOffice is used as a last resort for old .ppt files when PowerPoint isn't there.</summary>
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
        candidates.Add(Path.Combine(ToolsDir, "LibreOffice", "program", "soffice.exe"));
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
}
