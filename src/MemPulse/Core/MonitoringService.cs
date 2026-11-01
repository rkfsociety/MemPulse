using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemPulse.Core;

public sealed class MonitoringService
{
    public MemoryUsage ReadMemory()
    {
        var status = new Native.MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read system memory.");
        ulong cache = 0, committed = 0, commitLimit = 0;
        if (Native.GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()))
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
        return rows.OrderByDescending(row => row.WorkingBytes).ThenBy(row => row.Name).ToArray();
    }
}
