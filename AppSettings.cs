using System.Text.Json;
using ConvertWheel.Input;
using Microsoft.Win32;

namespace ConvertWheel;

public sealed class AppSettings
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConvertWheel", "settings.json");

    /// <summary>Normalized virtual keys that must all be held to open the wheel.</summary>
    public List<int> TriggerKeys { get; set; } = [VirtualKeys.Ctrl];
    public int HoldDelayMs { get; set; } = 300;
    public bool AlwaysShowAll { get; set; }
    /// <summary>When true the original is sent to the Recycle Bin after a successful conversion.</summary>
    public bool ReplaceOriginal { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool UseCustomOutputFolder { get; set; }
    public string? CustomOutputFolder { get; set; }
    public bool StartWithWindows { get; set; }
    public string? FfmpegPath { get; set; }
    public bool Paused { get; set; }
    public bool HasShownWelcome { get; set; }

    public event Action? Changed;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (settings != null)
                {
                    settings.TriggerKeys = settings.TriggerKeys.Select(VirtualKeys.Normalize).Distinct().ToList();
                    if (settings.TriggerKeys.Count == 0) settings.TriggerKeys = [VirtualKeys.Ctrl];
                    settings.HoldDelayMs = Math.Clamp(settings.HoldDelayMs, 100, 2000);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        Changed?.Invoke();
    }

    public void ApplyStartup()
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (StartWithWindows) key.SetValue("ConvertWheel", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("ConvertWheel", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}
