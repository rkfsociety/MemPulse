using System.Text.Json;

namespace MemPulse.Core;

public sealed class SettingsStore
{
    // All SettingsStore instances in this process share one file-operation gate.
    // Windows can reject overlapping File.Move(overwrite: true) operations even
    // when each writer uses its own unique staging file.
    private static readonly object IoGate = new();
    private readonly string _pathName;
    private readonly Action<string, Exception>? _onLoadError;

    public static string DefaultWindowsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemPulse",
        "settings.json");

    public SettingsStore(string? path = null, Action<string, Exception>? onLoadError = null)
    {
        _pathName = path ?? DefaultWindowsPath;
        _onLoadError = onLoadError;
    }

    public AppSettings Load()
    {
        lock (IoGate)
        {
            try
            {
                if (File.Exists(_pathName))
                    return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_pathName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _onLoadError?.Invoke("Не удалось загрузить настройки, используются значения по умолчанию", ex);
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (IoGate)
        {
            var directory = Path.GetDirectoryName(_pathName)!;
            Directory.CreateDirectory(directory);
            // Unique staging files still isolate interrupted writes and stale .tmp files.
            var temp = Path.Combine(directory, "." + Path.GetFileName(_pathName) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(AppSettings.Normalize(settings.Copy()),
                    new JsonSerializerOptions { WriteIndented = true }));
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, _pathName, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
