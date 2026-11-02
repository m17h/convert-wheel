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
                    settings.TriggerKeys = (settings.TriggerKeys ?? []).Where(k => k is > 0 and < 256)
                        .Select(VirtualKeys.Normalize).Distinct().ToList();
                    if (settings.TriggerKeys.Count == 0) settings.TriggerKeys = [VirtualKeys.Ctrl];
                    settings.HoldDelayMs = Math.Clamp(settings.HoldDelayMs, 100, 2000);
                    settings.StartWithWindows = StartupRegistered();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        return new AppSettings { StartWithWindows = StartupRegistered() };
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Windows startup entry is the truth (the installer may have added it).</summary>
    static bool StartupRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("ConvertWheel") is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Save()
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
        finally
        {
            if (temp != null) try { File.Delete(temp); } catch { }
        }
        Changed?.Invoke();
    }

    public void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartWithWindows) key.SetValue("ConvertWheel", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("ConvertWheel", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }
}
