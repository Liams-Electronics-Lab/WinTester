using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinTester.App.Win32;

// Wraps the FlashWindowEx Win32 API (adapted from https://stackoverflow.com/a/11310217, CC BY-SA 3.0) used
// to flash a window's taskbar button. ResultPopupWindow itself is ShowInTaskbar="False" and has no taskbar
// button of its own, so this always targets its owner (MainWindow) instead.
public static class TaskbarFlasher
{
    private const uint FLASHW_STOP = 0;

    // Flash both the window caption and taskbar button.
    private const uint FLASHW_ALL = 3;

    // Flash continuously until the window comes to the foreground.
    private const uint FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    // Starts flashing window's taskbar button; keeps flashing until it's brought to the foreground
    // (clicking back into the app, or the popup itself, stops it automatically).
    public static void Flash(Window? window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    // Explicit stop, called when the popup closes so a flash triggered by one run doesn't linger into
    // the next if the window was never brought to the foreground in between.
    public static void StopFlashing(Window? window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_STOP,
            uCount = 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }
}
