using System.IO;
using Microsoft.Win32;

namespace MemPulse.Core;

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
