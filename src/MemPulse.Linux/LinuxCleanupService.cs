using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MemPulse.Linux;

/// <summary>Linux-only memory reclamation: explicit cache drop, bounded process page-out and an opt-in helper.</summary>
public static class LinuxCleanupService
{
    private const long SysPidfdOpen = 434;        // x86_64 and arm64
    private const long SysProcessMadvise = 440;   // x86_64 and arm64
    private const long MadvPageout = 21;
    private const ulong MaxPageoutBytes = 128UL * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec { public IntPtr Base; public nuint Length; }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "sync")]
    private static extern void Sync();

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PidfdOpen(long number, long pid, long flags);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long ProcessMadvise(long number, long pidfd, ref Iovec vector,
        ulong vectorLength, long advice, long flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseFd(int fd);

    private static bool IsRoot => OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;

    public static int CleanCacheElevated()
    {
        if (!IsRoot)
        {
            Console.Error.WriteLine("Для очистки системного кэша требуются права root.");
            return 1;
        }
        try
        {
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

    // A Linux 5.10+ pidfd pins the process identity while advice is applied.
    // MADV_PAGEOUT is a request to the kernel, not a guarantee that RSS decreases.
    public static int TrimProcessElevated(int pid, ulong expectedStartTicks)
    {
        if (!IsRoot)
        {
            Console.Error.WriteLine("Для очистки процесса необходимы root и CAP_SYS_NICE.");
            return 1;
        }
        if (pid <= 1 || pid == Environment.ProcessId || expectedStartTicks == 0)
        {
            Console.Error.WriteLine("Недопустимый PID или идентификатор процесса.");
            return 1;
        }

        long fd = PidfdOpen(SysPidfdOpen, pid, 0);
        if (fd < 0)
        {
            Console.Error.WriteLine("pidfd_open: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            return 1;
        }
        try
        {
            if (!LinuxProcfs.TryReadStartTicks(pid, out var start) || start != expectedStartTicks)
            {
                Console.Error.WriteLine("Процесс завершился или PID был переиспользован. Повторите выбор.");
                return 1;
            }
            var mappings = LinuxProcessMemory.ParseEligibleMappings(
                File.ReadAllText($"/proc/{pid}/maps"), MaxPageoutBytes);
            if (mappings.Count == 0)
            {
                Console.Error.WriteLine("Нет подходящих приватных отображений памяти.");
                return 1;
            }

            ulong advised = 0;
            int rejected = 0;
            int lastError = 0;
            foreach (var mapping in mappings)
            {
                if (!LinuxProcfs.TryReadStartTicks(pid, out start) || start != expectedStartTicks)
                    break;
                var vector = new Iovec { Base = (IntPtr)unchecked((long)mapping.Address), Length = (nuint)mapping.Length };
                long count = ProcessMadvise(SysProcessMadvise, fd, ref vector, 1, MadvPageout, 0);
                if (count >= 0) advised += (ulong)count;
                else
                {
                    rejected++;
                    lastError = Marshal.GetLastPInvokeError();
                    if (lastError is 1 or 13 or 38 or 95) break; // no permission / unsupported syscall
                }
            }
            if (advised == 0)
            {
                Console.Error.WriteLine(lastError is 1 or 13
                    ? "Ядро отклонило операцию: требуются CAP_SYS_NICE и разрешение ptrace."
                    : lastError is 38 or 95
                        ? "process_madvise недоступен в этом ядре Linux."
                        : "Не удалось вытеснить страницы процесса (код " + lastError + ").");
                return 1;
            }
            Console.WriteLine("Ядро приняло запрос MADV_PAGEOUT на " +
                (advised / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) +
                " МиБ; фактически освобождённый объём может быть меньше." +
                (rejected > 0 ? " Часть областей пропущена." : ""));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Не удалось прочитать отображения процесса: " + ex.Message);
            return 1;
        }
        finally { CloseFd((int)fd); }
    }

    // This helper is intentionally bound to the originating GUI instance:
    // it stops when either the sentinel disappears or that exact process exits.
    public static async Task<int> RunAutoElevatedAsync(string[] args)
    {
        if (!IsRoot || args.Length != 5 ||
            !int.TryParse(args[0], out int threshold) || threshold is < 70 or > 98 ||
            !int.TryParse(args[1], out int cooldown) || cooldown is < 5 or > 120 ||
            !int.TryParse(args[2], out int parentPid) || parentPid <= 1 ||
            !ulong.TryParse(args[3], out ulong parentStartTicks) || parentStartTicks == 0)
        {
            Console.Error.WriteLine("Недопустимые настройки автоочистки или нет прав root.");
            return 1;
        }
        string marker = args[4];
        if (!Path.IsPathFullyQualified(marker) ||
            !Path.GetFileName(marker).StartsWith("mempulse-auto-", StringComparison.Ordinal) ||
            marker.Length > 512 || !File.Exists(marker))
        {
            Console.Error.WriteLine("Некорректный маркер автоочистки.");
            return 1;
        }
        if (!LinuxProcfs.TryReadStartTicks(parentPid, out var observed) || observed != parentStartTicks)
            return 1;

        Console.WriteLine("READY");
        Console.Out.Flush();

        DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
        var monitoring = new LinuxProcfs();
        while (File.Exists(marker) &&
               LinuxProcfs.TryReadStartTicks(parentPid, out observed) && observed == parentStartTicks)
        {
            try
            {
                var memory = monitoring.ReadMemory();
                var now = DateTimeOffset.UtcNow;
                if (LinuxAutoCleanupPolicy.ShouldClean(memory.UsedPercent, threshold, now,
                    nextAttempt))
                {
                    // Cooldown covers failed attempts too, avoiding a tight retry loop.
                    nextAttempt = now.AddMinutes(cooldown);
                    CleanCacheElevated();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine("Автоочистка: " + ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return 0;
    }

    public static Task<(bool Success, string Message)> RequestCacheDropAsync() =>
        ExecuteElevatedAsync("--clean-cache");

    public static async Task<(bool Success, string Message)> RequestTrimProcessAsync(int pid, ulong startTicks)
    {
        if (pid <= 1 || startTicks == 0) return (false, "Некорректный PID или процесс уже завершился.");
        return await ExecuteElevatedAsync("--trim-process",
            pid.ToString(CultureInfo.InvariantCulture),
            startTicks.ToString(CultureInfo.InvariantCulture));
    }

    private static ProcessStartInfo ElevatedCommand(params string[] args)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var start = new ProcessStartInfo("pkexec")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        // In SDK/development mode the app is launched with dotnet <assembly.dll>.
        if (Path.GetFileName(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Не найдена исполняемая сборка.");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add(assembly);
        }
        else start.ArgumentList.Add(executable);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task<(bool Success, string Message)> ExecuteElevatedAsync(params string[] args)
    {
        if (!OperatingSystem.IsLinux()) return (false, "Операция доступна только на Linux.");
        try
        {
            using var process = Process.Start(ElevatedCommand(args))
                ?? throw new InvalidOperationException("Не удалось запустить pkexec.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string message = process.ExitCode == 0 ? await output : await errors;
            if (string.IsNullOrWhiteSpace(message))
                message = process.ExitCode == 0 ? "Запрос выполнен." : "Операция отменена или отклонена polkit.";
            return (process.ExitCode == 0, message.Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return (false, "Не удалось запустить pkexec (polkit): " + ex.Message);
        }
    }

    public static async Task<(AutoCleanupSession? Session, string Message)> StartAutoAsync(
        int threshold, int cooldown)
    {
        if (!OperatingSystem.IsLinux()) return (null, "Только Linux.");
        string? marker = null;
        Process? process = null;
        try
        {
            int parentPid = Environment.ProcessId;
            if (!LinuxProcfs.TryReadStartTicks(parentPid, out var startTicks) || startTicks == 0)
                return (null, "Не удалось определить идентификатор процесса MemPulse.");

            marker = Path.Combine(Path.GetTempPath(), "mempulse-auto-" + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(marker, "");
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            process = Process.Start(ElevatedCommand("--auto-clean",
                threshold.ToString(CultureInfo.InvariantCulture),
                cooldown.ToString(CultureInfo.InvariantCulture),
                parentPid.ToString(CultureInfo.InvariantCulture),
                startTicks.ToString(CultureInfo.InvariantCulture), marker))
                ?? throw new InvalidOperationException("Не удалось запустить pkexec.");

            // The helper announces READY only after validating the parent and permissions.
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromMinutes(2));
            if (ready != "READY" || process.HasExited)
            {
                string error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "Авторизация отклонена или процесс автоочистки завершился."
                    : error.Trim());
            }
            return (new AutoCleanupSession(process, marker), "Автоочистка активна до закрытия MemPulse.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or TimeoutException)
        {
            process?.Dispose();
            if (marker is not null) TryDelete(marker);
            return (null, "Не удалось включить автоочистку: " + ex.Message);
        }
    }

    private static void TryDelete(string marker)
    {
        try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public sealed class AutoCleanupSession : IDisposable
    {
        private readonly Process _process;
        private readonly string _marker;
        private bool _disposed;

        internal AutoCleanupSession(Process process, string marker)
        {
            _process = process;
            _marker = marker;
        }

        public bool Running
        {
            get
            {
                try { return !_disposed && !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TryDelete(_marker);
            // Do not try to kill a privileged process. It notices the missing marker
            // within five seconds and exits; it also watches for GUI process exit.
            _process.Dispose();
        }
    }
}

public static class LinuxAutoCleanupPolicy
{
    public static bool ShouldClean(double usedPercent, int threshold, DateTimeOffset now,
        DateTimeOffset nextAttempt) =>
        double.IsFinite(usedPercent) && threshold is >= 70 and <= 98 &&
        usedPercent >= threshold && now >= nextAttempt;
}

public readonly record struct LinuxMemoryMapping(ulong Address, ulong Length);

public static class LinuxProcessMemory
{
    /// <summary>Only private, writable anonymous mappings: no shared/file/device/stack mappings.</summary>
    public static IReadOnlyList<LinuxMemoryMapping> ParseEligibleMappings(string maps, ulong maxBytes, int pageSize = 0)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (pageSize == 0) pageSize = Environment.SystemPageSize;
        if (pageSize <= 0 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be a power of two.");

        var result = new List<LinuxMemoryMapping>();
        ulong remaining = maxBytes;
        foreach (string line in maps.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (remaining == 0 || result.Count >= 128) break;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // A missing pathname alone does not prove a mapping is anonymous.
            // Reject file-backed regions even when procfs omits their pathname.
            if (parts.Length < 5 || parts[1] != "rw-p" ||
                parts[3] != "00:00" || parts[4] != "0") continue;
            // Anonymous mappings (no pathname), or a conventional heap. Avoid live stacks.
            if (parts.Length >= 6 && parts[5] != "[heap]" &&
                !parts[5].StartsWith("[anon:", StringComparison.Ordinal)) continue;
            string[] ends = parts[0].Split('-');
            if (ends.Length != 2 ||
                !ulong.TryParse(ends[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start) ||
                !ulong.TryParse(ends[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end) ||
                end <= start) continue;
            ulong length = Math.Min(end - start, remaining);
            // /proc mappings are page-aligned; respect 64 KiB ARM64 kernels too.
            length &= ~((ulong)pageSize - 1);
            if (length == 0) continue;
            result.Add(new LinuxMemoryMapping(start, length));
            remaining -= length;
        }
        return result;
    }
}
