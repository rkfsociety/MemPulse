using System.Text.Json;
using MemPulse.Core;

namespace MemPulse.Linux;

/// <summary>Per-user Linux settings; never touches the Windows Run registry key.</summary>
public sealed class LinuxSettingsStore
{
    public static string SettingsPath
    {
        get
        {
            string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "MemPulse", "linux-settings.json");
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Не удалось прочитать настройки MemPulse: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(AppSettings.Normalize(settings.Copy()),
                new JsonSerializerOptions { WriteIndented = true }));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
