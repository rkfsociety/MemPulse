using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemPulse.Linux;

/// <summary>Explicit, privileged cache drop. Never runs automatically or against individual processes.</summary>
public static class LinuxCleanupService
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "sync")]
    private static extern void Sync();

    public static int CleanCacheElevated()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            Console.Error.WriteLine("Для очистки системного кэша требуются права root.");
            return 1;
        }
        try
        {
            // drop_caches=3 clears reclaimable page cache, dentries and inodes.
            // sync first to avoid discarding only clean pages before dirty data is flushed.
            Sync();
            File.WriteAllText("/proc/sys/vm/drop_caches", "3\n");
            Console.WriteLine("Запрошен сброс файлового кэша Linux.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Не удалось очистить кэш: " + ex.Message);
            return 1;
        }
    }

    public static async Task<(bool Success, string Message)> RequestCacheDropAsync()
    {
        if (!OperatingSystem.IsLinux())
            return (false, "Эта функция доступна только на Linux.");

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return (false, "Не удалось определить путь к приложению.");

        var start = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Support both a published apphost and development startup via dotnet <app.dll>.
        if (Path.GetFileName(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                return (false, "Не удалось найти сборку для повышения прав.");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(assembly);
        }
        else start.ArgumentList.Add(executable);
        start.ArgumentList.Add("--clean-cache");

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить pkexec.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (true, "Запрос на очистку файлового кэша выполнен.")
                : (false, string.IsNullOrWhiteSpace(error)
                    ? "Очистка отменена или не разрешена (код " + process.ExitCode + ")."
                    : "Очистка не выполнена: " + error.Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (false, "Не удалось запустить pkexec. Установите polkit: " + ex.Message);
        }
    }
}
