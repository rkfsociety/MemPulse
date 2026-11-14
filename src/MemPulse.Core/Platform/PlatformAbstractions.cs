namespace MemPulse.Core.Platform;

/// <summary>Cross-platform memory and process telemetry reader.</summary>
public interface IMemoryMonitorService
{
    MemoryUsage ReadMemory();
    IReadOnlyList<ProcessUsage> ReadProcesses();
}

/// <summary>Detects whether a fullscreen window is currently active on the foreground monitor.</summary>
public interface IFullscreenDetector
{
    bool IsForegroundFullscreen();
}

/// <summary>Decorates process snapshots with relative load and optional PNG icon bytes.</summary>
public interface IProcessIconProvider
{
    IReadOnlyList<ProcessUsage> Decorate(IReadOnlyList<ProcessUsage> rows);
}

public sealed class NullFullscreenDetector : IFullscreenDetector
{
    public bool IsForegroundFullscreen() => false;
}

public sealed class DefaultProcessIconProvider : IProcessIconProvider
{
    public IReadOnlyList<ProcessUsage> Decorate(IReadOnlyList<ProcessUsage> rows)
    {
        if (rows.Count == 0)
            return rows;

        long largest = Math.Max(1, rows[0].WorkingBytes);
        var result = new ProcessUsage[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            result[i] = p with
            {
                RelativeLoad = Math.Clamp((double)p.WorkingBytes / largest * 100, 0, 100)
            };
        }
        return result;
    }
}

public static class PlatformServices
{
    public static IMemoryMonitorService CreateMemoryMonitor()
    {
        if (OperatingSystem.IsWindows())
            return new Windows.WindowsMemoryMonitorService();
        if (OperatingSystem.IsLinux())
            return new Linux.LinuxMemoryMonitorService();
        return new UnsupportedMemoryMonitorService();
    }

    public static IFullscreenDetector CreateFullscreenDetector() =>
        OperatingSystem.IsWindows()
            ? new Windows.WindowsFullscreenDetector()
            : new NullFullscreenDetector();

    public static IProcessIconProvider CreateProcessIconProvider() =>
        OperatingSystem.IsWindows()
            ? new Windows.WindowsProcessIconProvider()
            : new DefaultProcessIconProvider();

    private sealed class UnsupportedMemoryMonitorService : IMemoryMonitorService
    {
        public MemoryUsage ReadMemory() => new(0, 0, 0, 0, 0);
        public IReadOnlyList<ProcessUsage> ReadProcesses() => [];
    }
}
