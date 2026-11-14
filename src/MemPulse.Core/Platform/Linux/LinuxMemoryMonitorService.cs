namespace MemPulse.Core.Platform.Linux;

public sealed class LinuxMemoryMonitorService : IMemoryMonitorService
{
    private readonly LinuxProcfs _procfs;

    public LinuxMemoryMonitorService(string root = "/proc")
    {
        _procfs = new LinuxProcfs(root);
    }

    public MemoryUsage ReadMemory() =>
        OperatingSystem.IsLinux() ? _procfs.ReadMemory() : new MemoryUsage(0, 0, 0, 0, 0);

    public IReadOnlyList<ProcessUsage> ReadProcesses() =>
        OperatingSystem.IsLinux() ? _procfs.ReadProcesses() : [];
}
