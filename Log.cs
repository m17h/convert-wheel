namespace ConvertWheel;

internal static class Log
{
    static readonly object Gate = new();

    /// <summary>Testing aid: CONVERTWHEEL_DATA=C:\folder keeps the log, tools and settings there instead.</summary>
    static readonly string? TestDir = Environment.GetEnvironmentVariable("CONVERTWHEEL_DATA") is { } dir && Path.IsPathFullyQualified(dir) ? dir : null;

    public static string DataDir { get; } = TestDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertWheel");

    public static string SettingsDir { get; } = TestDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConvertWheel");

    static string FilePath => Path.Combine(DataDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DataDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Write(Exception ex) => Write(ex.ToString());
}
