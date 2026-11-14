using Microsoft.Win32;

namespace MemPulse.Services;

internal static class WindowsStartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MemPulse";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true)
            ?? Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу.");
        string command = "\"" + executable + "\"";
        if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            string entryDll = Path.Combine(AppContext.BaseDirectory, "MemPulse.dll");
            if (!File.Exists(entryDll))
                throw new InvalidOperationException("Для включения автозапуска сначала необходимо опубликовать приложение.");
            command += " \"" + entryDll + "\"";
        }
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
