using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MemPulse.Core;

public sealed class SettingsStore
{
    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemPulse", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(PathName))
                return AppSettings.Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt/unreadable settings fall back to safe defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(PathName)!;
        Directory.CreateDirectory(directory);
        var temp = PathName + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(AppSettings.Normalize(settings.Copy()),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, PathName, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
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
