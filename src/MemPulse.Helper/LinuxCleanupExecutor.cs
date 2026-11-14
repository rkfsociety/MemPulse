using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using MemPulse.Core;
using MemPulse.Core.Helper;
using MemPulse.Core.Platform.Linux;

namespace MemPulse.Helper;

internal static class LinuxCleanupExecutor
{
    private const long SysPidfdOpen = 434;      // x86_64 and arm64
    private const long SysProcessMadvise = 440; // x86_64 and arm64
    private const long MadvPageout = 21;
    private const ulong MaxPageoutBytes = 128UL * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public IntPtr Base;
        public nuint Length;
    }

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

    public static HelperReport CleanCache()
    {
        if (!IsRoot)
            return Fail("Файловый кэш", "Для очистки системного кэша требуются права root.");

        try
        {
            Sync();
            File.WriteAllText("/proc/sys/vm/drop_caches", "3\n");
            const string msg = "Запрошен сброс файлового кэша Linux.";
            return new HelperReport(true, msg, [new CleanupStep("Файловый кэш", true, msg)], DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Файловый кэш", "Не удалось очистить кэш: " + ex.Message);
        }
    }

    public static HelperReport TrimProcess(int pid, ulong expectedStartTicks)
    {
        if (!IsRoot)
            return Fail("Процесс " + pid, "Для очистки процесса необходимы root и CAP_SYS_NICE.");

        if (pid <= 1 || pid == Environment.ProcessId || expectedStartTicks == 0)
            return Fail("Процесс", "Недопустимый PID или идентификатор процесса.");

        long fd = PidfdOpen(SysPidfdOpen, pid, 0);
        if (fd < 0)
            return Fail("Процесс " + pid, "pidfd_open: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);

        try
        {
            if (!LinuxProcfs.TryReadStartTicks(pid, out var start) || start != expectedStartTicks)
                return Fail("Процесс " + pid, "Процесс завершился или PID был переиспользован. Повторите выбор.");

            var mappings = LinuxProcessMemory.ParseEligibleMappings(
                File.ReadAllText($"/proc/{pid}/maps"), MaxPageoutBytes);
            if (mappings.Count == 0)
                return Fail("Процесс " + pid, "Нет подходящих приватных отображений памяти.");

            ulong advised = 0;
            int rejected = 0;
            int lastError = 0;
            foreach (var mapping in mappings)
            {
                if (!LinuxProcfs.TryReadStartTicks(pid, out start) || start != expectedStartTicks)
                    break;
                var vector = new Iovec { Base = (IntPtr)unchecked((long)mapping.Address), Length = (nuint)mapping.Length };
                long count = ProcessMadvise(SysProcessMadvise, fd, ref vector, 1, MadvPageout, 0);
                if (count >= 0)
                {
                    advised += (ulong)count;
                }
                else
                {
                    rejected++;
                    lastError = Marshal.GetLastPInvokeError();
                    if (lastError is 1 or 13 or 38 or 95)
                        break;
                }
            }

            if (advised == 0)
            {
                string err = lastError is 1 or 13
                    ? "Ядро отклонило операцию: требуются CAP_SYS_NICE и разрешение ptrace."
                    : lastError is 38 or 95
                        ? "process_madvise недоступен в этом ядре Linux."
                        : "Не удалось вытеснить страницы процесса (код " + lastError + ").";
                return Fail("Процесс " + pid, err);
            }

            string msg = "Ядро приняло запрос MADV_PAGEOUT на " +
                (advised / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) +
                " МиБ; фактически освобождённый объём может быть меньше." +
                (rejected > 0 ? " Часть областей пропущена." : "");
            return new HelperReport(true, msg, [new CleanupStep("Процесс " + pid, true, msg)], DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Процесс " + pid, "Не удалось прочитать отображения процесса: " + ex.Message);
        }
        finally
        {
            CloseFd((int)fd);
        }
    }

    public static async Task<HelperReport> RunAutoAsync(
        int threshold,
        int cooldownMinutes,
        int parentPid,
        ulong parentStartTicks,
        string markerPath,
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsRoot)
            return Fail("Автоочистка", "Для автоочистки требуются права root.");

        if (!File.Exists(markerPath))
            return Fail("Автоочистка", "Маркер сессии автоочистки не найден.");

        if (!LinuxProcfs.TryReadStartTicks(parentPid, out var observed) || observed != parentStartTicks)
            return Fail("Автоочистка", "Родительский процесс не найден.");

        HelperReportIo.WriteReport(reportPath,
            new HelperReport(true, "READY", [new CleanupStep("Автоочистка", true, "Активна")], DateTimeOffset.UtcNow));

        DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
        var monitoring = new LinuxProcfs();
        while (!cancellationToken.IsCancellationRequested &&
               File.Exists(markerPath) &&
               LinuxProcfs.TryReadStartTicks(parentPid, out observed) &&
               observed == parentStartTicks)
        {
            try
            {
                var memory = monitoring.ReadMemory();
                var now = DateTimeOffset.UtcNow;
                if (AutoCleanupPolicy.ShouldClean(memory.UsedPercent, threshold, now, nextAttempt))
                {
                    nextAttempt = now.AddMinutes(cooldownMinutes);
                    var stepReport = CleanCache();
                    HelperReportIo.WriteReport(reportPath, stepReport);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Keep monitoring loop resilient against transient procfs races.
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return new HelperReport(true, "Автоочистка завершена.", [new CleanupStep("Автоочистка", true, "Остановлена")], DateTimeOffset.UtcNow);
    }

    private static HelperReport Fail(string step, string reason) =>
        new(false, reason, [new CleanupStep(step, false, reason)], DateTimeOffset.UtcNow);
}
