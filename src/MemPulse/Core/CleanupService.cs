using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Principal;

namespace MemPulse.Core;

public sealed class CleanupService
{
    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x08, SePrivilegeEnabled = 0x02;
    private const uint ProcessSetQuota = 0x0100, ProcessQueryLimitedInformation = 0x1000;
    private const int SystemMemoryListInformation = 80;
    public static readonly TimeSpan DefaultHelperTimeout = TimeSpan.FromSeconds(30);

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public CleanupReport Clean(
        CleanupOptions options,
        TimeSpan? helperTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Standby && !options.WorkingSets && !options.ModifiedPages)
        {
            var empty = new CleanupReport();
            empty.Steps.Add(new CleanupStep("Нет выбранных действий очистки", false, "Выберите хотя бы одну опцию в настройках автоочистки."));
            return empty;
        }
        if (IsElevated) return CleanElevated(options);
        var args = "--clean-cache" + (options.Standby ? " --standby" : "") +
            (options.WorkingSets ? " --workingsets" : "") + (options.ModifiedPages ? " --modified" : "");
        return RunHelper(args, "Очистка памяти", helperTimeout, cancellationToken);
    }

    // Запускает короткий экземпляр MemPulse с правами администратора (UAC) и ждёт его завершения с таймаутом.
    private static CleanupReport RunHelper(
        string args,
        string label,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var report = new CleanupReport();
        if (cancellationToken.IsCancellationRequested)
        {
            report.Steps.Add(new CleanupStep(label, false, "Операция отменена."));
            return report;
        }

        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден исполняемый файл.");
            using var proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (proc is null)
            {
                report.Steps.Add(new CleanupStep(label, false, "Не удалось запустить помощник."));
                return report;
            }

            var effectiveTimeout = timeout ?? DefaultHelperTimeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (effectiveTimeout > TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
                cts.CancelAfter(effectiveTimeout);

            try
            {
                proc.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                string reason = cancellationToken.IsCancellationRequested
                    ? "Операция отменена."
                    : $"Превышено время ожидания помощника UAC ({(int)Math.Ceiling(effectiveTimeout.TotalSeconds)} сек.).";
                report.Steps.Add(new CleanupStep(label, false, reason));
                return report;
            }

            bool ok = proc.ExitCode == 0;
            report.Steps.Add(new CleanupStep(label, ok, ok ? "Выполнено помощником с правами администратора." : "Помощник завершился с кодом " + proc.ExitCode + "."));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            report.Steps.Add(new CleanupStep(label, false, "Запрос UAC отклонён."));
        }
        catch (Exception ex)
        {
            report.Steps.Add(new CleanupStep(label, false, ex.Message));
        }
        return report;
    }

    // Точка входа помощника: возвращает код выхода процесса.
    public static int RunHelperMode(string[] args)
    {
        var svc = new CleanupService();
        CleanupReport report;
        int i = Array.FindIndex(args, a => a.Equals("--trim-process", StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            int pid = i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : 0;
            report = svc.TrimProcessDirect(pid);
        }
        else
        {
            bool Has(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
            report = svc.CleanElevated(new CleanupOptions(Has("--standby"), Has("--workingsets"), Has("--modified")));
        }
        return report.Steps.Count > 0 && report.Steps.All(s => s.Success) ? 0 : 1;
    }

    public static bool IsHelperMode(string[] args) =>
        args.Contains("--clean-cache", StringComparer.OrdinalIgnoreCase) ||
        args.Contains("--trim-process", StringComparer.OrdinalIgnoreCase);

    private CleanupReport CleanElevated(CleanupOptions options)
    {
        var report = new CleanupReport();
        if (!IsElevated)
        {
            report.Steps.Add(new CleanupStep("Нужны права администратора", false, "Помощник запущен без повышения прав."));
            return report;
        }
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            report.Steps.Add(new CleanupStep("Привилегия", false, "Недоступна привилегия SeProfileSingleProcessPrivilege."));
            return report;
        }
        if (options.Standby) Run(report, 4, "Кэш ожидания");
        if (options.WorkingSets) Run(report, 2, "Рабочие наборы процессов");
        if (options.ModifiedPages) Run(report, 3, "Изменённые страницы");
        return report;
    }

    public CleanupReport TrimProcess(
        int pid,
        TimeSpan? helperTimeout = null,
        CancellationToken cancellationToken = default) =>
        pid > 0 && !IsElevated
            ? RunHelper("--trim-process " + pid, "Процесс " + pid, helperTimeout, cancellationToken)
            : TrimProcessDirect(pid);
    private CleanupReport TrimProcessDirect(int pid)
    {
        var report = new CleanupReport();
        if (pid <= 0)
        {
            report.Steps.Add(new CleanupStep("Процесс", false, "Некорректный PID."));
            return report;
        }
        IntPtr handle = Native.OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            report.Steps.Add(new CleanupStep("Процесс " + pid, false,
                new Win32Exception(Marshal.GetLastWin32Error()).Message));
            return report;
        }
        try
        {
            bool ok = Native.EmptyWorkingSet(handle);
            report.Steps.Add(new CleanupStep("Process " + pid, ok,
                ok ? "Рабочий набор уменьшен." : new Win32Exception(Marshal.GetLastWin32Error()).Message));
        }
        finally { Native.CloseHandle(handle); }
        return report;
    }

    private static void Run(CleanupReport report, int command, string label)
    {
        try
        {
            int status = Native.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            bool ok = status >= 0;
            report.Steps.Add(new CleanupStep(label, ok,
                ok ? "Запрошено." : "NTSTATUS 0x" + unchecked((uint)status).ToString("X8")));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            report.Steps.Add(new CleanupStep(label, false, ex.Message));
        }
    }

    private static bool _privilegeEnabled = false;
    private static bool EnablePrivilege(string name)
    {
        if (_privilegeEnabled) return true;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return false;
        try
        {
            if (!Native.LookupPrivilegeValue(null, name, out var luid)) return false;
            var state = new Native.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            bool adjusted = Native.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            if (!adjusted || Marshal.GetLastWin32Error() != 0)
                return false;
            _privilegeEnabled = true;
            return true;
        }
        finally
        {
            if (token != IntPtr.Zero) Native.CloseHandle(token);
        }
    }
}
