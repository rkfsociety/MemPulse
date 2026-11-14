using System.Runtime.InteropServices;

namespace MemPulse.Core;

internal static class FullscreenDetector
{
    public static bool IsForegroundFullscreen()
    {
        try
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero ||
                foreground == Native.GetDesktopWindow() ||
                foreground == Native.GetShellWindow())
                return false;

            if (!Native.IsWindowVisible(foreground) || Native.IsIconic(foreground))
                return false;

            if (!Native.GetWindowRect(foreground, out var windowRect))
                return false;

            IntPtr monitor = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;

            var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(monitor, ref info))
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
