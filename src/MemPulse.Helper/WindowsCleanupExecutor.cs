using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using MemPulse.Core;
using MemPulse.Core.Helper;

namespace MemPulse.Helper;

internal static class WindowsCleanupExecutor
{
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x08;
    private const uint SePrivilegeEnabled = 0x02;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SystemMemoryListInformation = 80;

    private static bool _privilegeEnabled;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        bool disableAll,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr processHandle);

    [SupportedOSPlatform("windows")]
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    [SupportedOSPlatform("windows")]
    public static HelperReport Clean(CleanupOptions options)
    {
        var steps = new List<CleanupStep>();
        if (!IsElevated)
        {
            steps.Add(new CleanupStep("Нужны права администратора", false, "Помощник запущен без повышения прав."));
            return BuildReport(steps);
        }

        if (!options.Standby && !options.WorkingSets && !options.ModifiedPages)
        {
            steps.Add(new CleanupStep("Нет выбранных действий очистки", false, "Выберите хотя бы одну операцию."));
            return BuildReport(steps);
        }

        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            steps.Add(new CleanupStep("Привилегия", false, "Недоступна привилегия SeProfileSingleProcessPrivilege."));
            return BuildReport(steps);
        }

        if (options.Standby)
            RunMemoryListCommand(steps, 4, "Кэш ожидания");

        if (options.WorkingSets)
        {
            if (options.ExcludedProcesses is { Count: > 0 })
                TrimAllWorkingSetsExcept(steps, options.ExcludedProcesses);
            else
                RunMemoryListCommand(steps, 2, "Рабочие наборы процессов");
        }

        if (options.ModifiedPages)
            RunMemoryListCommand(steps, 3, "Изменённые страницы");

        return BuildReport(steps);
    }

    [SupportedOSPlatform("windows")]
    public static HelperReport TrimProcess(int pid)
    {
        var steps = new List<CleanupStep>();
        if (!IsElevated)
        {
            steps.Add(new CleanupStep("Нужны права администратора", false, "Помощник запущен без повышения прав."));
            return BuildReport(steps);
        }

        if (pid <= 4 || pid == Environment.ProcessId)
        {
            steps.Add(new CleanupStep("Процесс", false, "Недопустимый PID."));
            return BuildReport(steps);
        }

        IntPtr handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            steps.Add(new CleanupStep("Процесс " + pid, false,
                new Win32Exception(Marshal.GetLastWin32Error()).Message));
            return BuildReport(steps);
        }

        try
        {
            bool ok = EmptyWorkingSet(handle);
            steps.Add(new CleanupStep("Процесс " + pid, ok,
                ok ? "Рабочий набор уменьшен." : new Win32Exception(Marshal.GetLastWin32Error()).Message));
        }
        finally
        {
            CloseHandle(handle);
        }

        return BuildReport(steps);
    }

    private static void TrimAllWorkingSetsExcept(List<CleanupStep> steps, IReadOnlyList<string> excluded)
    {
        int selfPid = Environment.ProcessId;
        int trimmed = 0;
        int skipped = 0;

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    int pid = proc.Id;
                    if (pid <= 4 || pid == selfPid)
                        continue;

                    if (ProcessExclusion.IsExcluded(proc.ProcessName, excluded))
                    {
                        skipped++;
                        continue;
                    }

                    IntPtr handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
                    if (handle == IntPtr.Zero)
                        continue;

                    try
                    {
                        if (EmptyWorkingSet(handle))
                            trimmed++;
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or exiting processes may deny inspection.
                }
            }
        }

        steps.Add(new CleanupStep(
            "Рабочие наборы процессов",
            true,
            $"Очищено: {trimmed}, исключено: {skipped}."));
    }

    private static void RunMemoryListCommand(List<CleanupStep> steps, int command, string label)
    {
        try
        {
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            bool ok = status >= 0;
            steps.Add(new CleanupStep(label, ok,
                ok ? "Запрошено." : "NTSTATUS 0x" + unchecked((uint)status).ToString("X8")));
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            steps.Add(new CleanupStep(label, false, ex.Message));
        }
    }

    private static bool EnablePrivilege(string name)
    {
        if (_privilegeEnabled)
            return true;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                return false;

            var state = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            bool adjusted = AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            if (!adjusted || Marshal.GetLastWin32Error() != 0)
                return false;

            _privilegeEnabled = true;
            return true;
        }
        finally
        {
            if (token != IntPtr.Zero)
                CloseHandle(token);
        }
    }

    private static HelperReport BuildReport(List<CleanupStep> steps)
    {
        bool success = steps.Count > 0 && steps.All(s => s.Success);
        string summary = steps.Count == 0
            ? "Операции не выбраны."
            : string.Join(" · ", steps.Select(s => (s.Success ? "✓ " : "✕ ") + s.Name + (s.Success ? "" : " (" + s.Details + ")")));
        return new HelperReport(success, summary, steps, DateTimeOffset.UtcNow);
    }
}
