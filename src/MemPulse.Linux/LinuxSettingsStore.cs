using System.Text.Json;
using MemPulse.Core;

namespace MemPulse.Linux;

/// <summary>Per-user Linux settings; never touches the Windows Run registry key.</summary>
public sealed class LinuxSettingsStore
{
    // Synchronize readers and writers across instances in this process.
    private static readonly object IoGate = new();
    private readonly string? _overridePath;

    public LinuxSettingsStore(string? path = null) => _overridePath = path;
    private string PathName => _overridePath ?? SettingsPath;

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
        lock (IoGate)
        {
            try
            {
                if (File.Exists(PathName))
                    return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Не удалось прочитать настройки MemPulse: " + ex.Message);
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (IoGate)
        {
            string path = PathName;
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
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
