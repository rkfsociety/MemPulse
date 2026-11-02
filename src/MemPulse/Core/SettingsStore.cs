using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MemPulse.Core;

public sealed class SettingsStore
{
    // All SettingsStore instances in this process share one file-operation gate.
    // Windows can reject overlapping File.Move(overwrite: true) operations even
    // when each writer uses its own unique staging file.
    private static readonly object IoGate = new();
    private readonly string _pathName;

    public SettingsStore(string? path = null) =>
        _pathName = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemPulse", "settings.json");

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
                StartupLog.Warn("Не удалось загрузить настройки, используются значения по умолчанию", ex);
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
                File.Move(temp, _pathName, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

public static class StartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MemPulse";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true)
            ?? Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (!enabled) { key.DeleteValue(ValueName, false); return; }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу.");
        string command = "\"" + executable + "\"";
        if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new InvalidOperationException("Для включения автозапуска сначала необходимо опубликовать приложение.");
            command += " \"" + assemblyPath + "\"";
        }
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
