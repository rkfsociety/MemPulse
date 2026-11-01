using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MemPulse.Core;

public sealed class CleanupService
{
    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x08, SePrivilegeEnabled = 0x02;
    private const uint ProcessSetQuota = 0x0100, ProcessQueryLimitedInformation = 0x1000;
    private const int SystemMemoryListInformation = 80;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public CleanupReport Clean(CleanupOptions options)
    {
        var report = new CleanupReport();
        if (!options.Standby && !options.WorkingSets && !options.ModifiedPages) return report;
        if (!IsElevated)
        {
            report.Steps.Add(new CleanupStep("Нужны права администратора", false, "Перезапустите MemPulse от имени администратора."));
            return report;
        }
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            report.Steps.Add(new CleanupStep("Привилегия", false, "Недоступна привилегия SeProfileSingleProcessPrivilege."));
            return report;
        }

        // SystemMemoryListInformation is an undocumented Windows interface. Errors are reported,
        // never silently treated as success; individual operations are intentionally optional.
        if (options.Standby) Run(report, 4, "Кэш ожидания");
        if (options.WorkingSets) Run(report, 2, "Рабочие наборы процессов");
        if (options.ModifiedPages) Run(report, 3, "Изменённые страницы");
        return report;
    }

    public CleanupReport TrimProcess(int pid)
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

    private static bool EnablePrivilege(string name)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return false;
        try
        {
            if (!Native.LookupPrivilegeValue(null, name, out var luid)) return false;
            var state = new Native.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled
            };
            return Native.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() != 1300; // ERROR_NOT_ALL_ASSIGNED
        }
        finally { Native.CloseHandle(token); }
    }
}
