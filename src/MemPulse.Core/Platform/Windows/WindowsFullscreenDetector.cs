using System.Runtime.InteropServices;

namespace MemPulse.Core.Platform.Windows;

public sealed class WindowsFullscreenDetector : IFullscreenDetector
{
    public bool IsForegroundFullscreen()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            IntPtr foreground = WindowsNative.GetForegroundWindow();
            if (foreground == IntPtr.Zero ||
                foreground == WindowsNative.GetDesktopWindow() ||
                foreground == WindowsNative.GetShellWindow())
                return false;

            if (!WindowsNative.IsWindowVisible(foreground) || WindowsNative.IsIconic(foreground))
                return false;

            if (!WindowsNative.GetWindowRect(foreground, out var windowRect))
                return false;

            IntPtr monitor = WindowsNative.MonitorFromWindow(foreground, WindowsNative.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;

            var info = new WindowsNative.MONITORINFO { cbSize = (uint)Marshal.SizeOf<WindowsNative.MONITORINFO>() };
            if (!WindowsNative.GetMonitorInfo(monitor, ref info))
                return false;

            return windowRect.Left <= info.rcMonitor.Left &&
                   windowRect.Top <= info.rcMonitor.Top &&
                   windowRect.Right >= info.rcMonitor.Right &&
                   windowRect.Bottom >= info.rcMonitor.Bottom;
        }
        catch
        {
            return false;
        }
    }
}
