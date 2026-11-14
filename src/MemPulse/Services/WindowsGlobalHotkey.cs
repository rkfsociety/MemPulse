using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace MemPulse.Services;

internal sealed partial class WindowsGlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;
    private const int HotkeyId = 0x4D50;

    private readonly Window _window;
    private readonly Action _callback;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _hookAttached;

    public WindowsGlobalHotkey(Window window, Action callback)
    {
        _window = window;
        _callback = callback;
    }

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        EnsureHook();
        if (_hwnd == IntPtr.Zero)
            return;

        if (enabled && !_registered)
            _registered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkM);
        else if (!enabled && _registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    private void EnsureHook()
    {
        if (_hwnd == IntPtr.Zero)
        {
            var handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
                return;
            _hwnd = handle;
        }

        if (!_hookAttached)
        {
            Win32Properties.AddWndProcHookCallback(_window, WndProc);
            _hookAttached = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == (IntPtr)HotkeyId)
        {
            _callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
        if (_hookAttached)
        {
            Win32Properties.RemoveWndProcHookCallback(_window, WndProc);
            _hookAttached = false;
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
