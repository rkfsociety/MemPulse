using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemPulse.Core;

public sealed class MonitoringService
{
    private static readonly uint MEMORYSTATUSEX_SIZE = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>();
    private static readonly uint PERFORMANCE_INFORMATION_SIZE = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>();
    public MemoryUsage ReadMemory()
    {
        var status = new Native.MEMORYSTATUSEX { Length = MEMORYSTATUSEX_SIZE };
        if (!Native.GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить сведения об оперативной памяти.");
        ulong cache = 0, committed = 0, commitLimit = 0;
        if (Native.GetPerformanceInfo(out var info, PERFORMANCE_INFORMATION_SIZE))
        {
            cache = (ulong)info.SystemCache * (ulong)info.PageSize;
            committed = (ulong)info.CommitTotal * (ulong)info.PageSize;
            commitLimit = (ulong)info.CommitLimit * (ulong)info.PageSize;
        }
        return new MemoryUsage(status.TotalPhysical, status.AvailablePhysical, cache, committed, commitLimit);
    }

    public IReadOnlyList<ProcessUsage> ReadProcesses()
    {
        var rows = new List<ProcessUsage>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    var name = process.ProcessName;
                    var working = process.WorkingSet64;
                    var privateBytes = process.PrivateMemorySize64;
                    rows.Add(new ProcessUsage(pid, name, working, privateBytes));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected and terminating processes cannot always be inspected.
                }
            }
        }
        rows.Sort((a, b) =>
            {
                int cmp = b.WorkingBytes.CompareTo(a.WorkingBytes);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            return rows;
    }
}
