using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tests.Gpu;

// Plain (x, y, width, height) screen rect in physical pixels; deliberately not a WPF Rect/Int32Rect so this project doesn't need a UseWPF dependency just to describe "where on screen." GpuStressTestModule's caller (MainViewModel, which already has real WPF coordinates via Visual.PointToScreen returning device pixels) converts into this shape at the boundary.
public readonly record struct WindowScreenRect(int X, int Y, int Width, int Height)
{
    public static WindowScreenRect FullPrimaryScreen() =>
        new(0, 0, Win32Interop.GetPrimaryScreenWidth(), Win32Interop.GetPrimaryScreenHeight());
}

// Win32 P/Invoke helpers for window placement: find FurMark's render window once it exists, then either reparent it as a true WS_CHILD of our own UI (ReparentAsChild, the primary path) or, if no parent HWND is available, position/lock it as an independent top-level window (PositionAndLockWindow, the fallback for a caller with no WPF host).
internal static class Win32Interop
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_REMOTESESSION = 0x1000;

    public static int GetPrimaryScreenWidth() => OperatingSystem.IsWindows() ? GetSystemMetrics(SM_CXSCREEN) : 1920;
    public static int GetPrimaryScreenHeight() => OperatingSystem.IsWindows() ? GetSystemMetrics(SM_CYSCREEN) : 1080;

    // Running a render-heavy pass over Remote Desktop can lock up the whole machine, not just the test. RDP's virtual display driver doesn't give a remoted-in session normal access to the real GPU/display pipeline, and a full-screen-style OpenGL/Direct3D/Vulkan app grabbing that pipeline anyway is a common trigger for a display-driver TDR hang; older/integrated Intel drivers are particularly prone, and the hang can affect the whole machine, sometimes needing a hard power cycle. GetSystemMetrics(SM_REMOTESESSION) is the standard way to detect a Remote Desktop session; GpuStressTestModule.StartAsync uses it to skip the render-heavy passes entirely rather than risk this on every remoted-into shop PC.
    public static bool IsRunningUnderRemoteSession() =>
        OperatingSystem.IsWindows() && GetSystemMetrics(SM_REMOTESESSION) != 0;

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint uMode);
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    // Mitigates GravityMark crashing with a blocking WER "has stopped working" dialog on an iGPU it can't run Vulkan against, which freezes the whole run since our overlay has no way to dismiss another process's system dialog. Best-effort, not a guarantee: CreateProcess (which Process.Start() calls) inherits the caller's error mode into the child unless CREATE_DEFAULT_ERROR_MODE is explicitly passed, which .NET's Process class never does; so a child launched after this call should inherit SEM_NOGPFAULTERRORBOX and crash silently instead of popping WerFault.exe's UI. Not confirmed on real hardware. Call once before starting the child process; safe to call more than once, since it just sets process-wide state. Deliberately not applied to FurmarkRunner, which already fails silently as-is.
    public static void SuppressChildProcessCrashDialogs()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        }
        catch
        {
            // Best-effort only; never let a failure here take down the pass it was meant to protect.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_DISABLED = 0x08000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_SHOWNOACTIVATE = 4;

    // Polls Process.MainWindowHandle until the render window exists. MainWindowHandle reads zero if queried before the window is created and is cached per Process snapshot, so this calls Refresh() between polls.
    public static async Task<IntPtr> WaitForMainWindowHandleAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return IntPtr.Zero;

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;

            await Task.Delay(150, ct);
        }
        return IntPtr.Zero;
    }

    // Positions the window over our "picture frame" with no title bar, always on top of our own UI. WS_DISABLED is the primary defense against Escape-quits-FurMark and other undocumented hotkeys, applied only after the window exists and is placed rather than mid-init. Also strips caption/resize/system-menu bits defensively even though --title-bar 0 is already passed on the command line, in case that flag doesn't fully suppress every style bit on every FurMark2 build.
    //
    // This is GravityMark's actual working path, not just a fallback: cross-process SetParent reparenting (ReparentAsChild) was confirmed on real hardware to kill its Vulkan surface outright (VK_ERROR_SURFACE_LOST_KHR), observed after the WinTester GUI closed while the reparented GravityMark window kept running and its swapchain died once the cross-process parent/child relationship tore down unevenly. Since GravityMark is now a genuine independent top-level window rather than a clipped child, it also strips WS_EX_APPWINDOW and adds WS_EX_TOOLWINDOW so it doesn't get its own Alt+Tab/taskbar entry; no third-party UI should ever surface to the operator, even though true containment isn't viable for this engine.
    //
    // fullscreenAvoidanceInsetPx exists because GetGpuEmbedRect() now always passes the exact primary-monitor rect during a run, and a borderless top-level window whose client area exactly equals the monitor's resolution is the same heuristic Windows/DWM and DXGI's Fullscreen Optimizations use to grant implicit-exclusive-fullscreen treatment. Two windows matching that heuristic at once (this one and WinTester's own RunOverlayWindow) is the suspected cause of both GUI-occlusion and a spurious "resolution changed" self-abort. Shrinking the requested rect by a few pixels on every edge stops the window from exactly matching monitor resolution without being visually noticeable against the borderless picture-frame background. Not confirmed on real hardware; the 2px default is a starting guess, not a measured minimum.
    public static void PositionAndLockWindow(IntPtr hwnd, WindowScreenRect rect, bool applyDisabledStyle = true, int fullscreenAvoidanceInsetPx = 2)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;

        if (fullscreenAvoidanceInsetPx > 0)
        {
            rect = rect with
            {
                X = rect.X + fullscreenAvoidanceInsetPx,
                Y = rect.Y + fullscreenAvoidanceInsetPx,
                Width = Math.Max(1, rect.Width - fullscreenAvoidanceInsetPx * 2),
                Height = Math.Max(1, rect.Height - fullscreenAvoidanceInsetPx * 2)
            };
        }

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        // applyDisabledStyle lets GravityMarkRunner's top-level path opt out the same way ReparentAsChild's flag does; otherwise this fallback would silently reintroduce the WS_DISABLED variable already removed from the reparent path over a blank-window suspicion, breaking the reparenting-vs-not isolation test. Default stays true so PositionAndLockWindow's existing callers (FurmarkRunner's fallback branch) are unaffected.
        if (applyDisabledStyle) style |= WS_DISABLED;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        // Hides from Alt+Tab/taskbar now that GravityMark is a real top-level window; extended-style bits only, so this doesn't touch rendering/swapchain behavior. Doesn't replace InputLockdown's global Escape hook; just closes the "operator sees it in the taskbar/switcher" gap.
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        ShowWindow(hwnd, SW_SHOWNOACTIVATE); // don't steal focus from our own MainWindow when (re)shown
        SetWindowPos(hwnd, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // Re-asserts position/topmost without touching the disabled/style bits [used when the embed target rect changes mid-run (e.g. the operator resizes/moves MainWindow, rare given the auto-fullscreen behavior during a run) rather than the one-time setup above. Only meaningful for the top-level positioned-overlay path] a reparented child uses ResizeChild instead, since child coordinates are parent-relative and "topmost" doesn't apply the same way to a child.
    public static void Reposition(IntPtr hwnd, WindowScreenRect rect)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE);
    }

    private const long WS_POPUP = unchecked((long)0x80000000L);
    private const long WS_CHILD = 0x40000000L;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    // Makes hwnd a genuine WS_CHILD of parentHwnd (a FurmarkEmbedHost container in WinTester.App) instead of merely positioning/disabling it as an independent top-level window. A child window has no independent identity to the desktop window manager [it can't be brought forward over other apps, doesn't show up in Alt+Tab, and is clipped/stacked within the parent's client area] which is what actually fixes "the operator can click it away," unlike PositionAndLockWindow's approach of asking a still-independent window to behave.
    //
    // Real risk, not a formality: reparenting a live OpenGL/Vulkan swapchain window can misbehave depending on GPU vendor/driver, since the window's relationship to its owning monitor/DWM composition can change under it. Not yet tried on real hardware as of this writing; if the embedded output goes black, stalls, or the driver hangs, that's this method, not a caller bug.
    //
    // WS_DISABLED is applied afterward as an extra layer by default, but is not load-bearing for input isolation on its own. applyDisabledStyle lets a caller opt out entirely; added for GravityMarkRunner, since a disabled window not being presented to by a Vulkan/D3D engine that skips rendering when it thinks it's inactive is a plausible, unconfirmed explanation for a reparented GravityMark window going blank/white (no source access to GravityMark's Tellusim engine). This flag removes that variable without touching PositionAndLockWindow's or FurmarkRunner's own default behavior.
    public static void ReparentAsChild(IntPtr hwnd, IntPtr parentHwnd, int width, int height, bool applyDisabledStyle = true)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return;

        SetParent(hwnd, parentHwnd);

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD;
        if (applyDisabledStyle) style |= WS_DISABLED;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        // Child-window coordinates are relative to the parent's client area, not the screen; (0,0) is correct here, unlike PositionAndLockWindow's screen-absolute rect. SWP_FRAMECHANGED is required for the style change above to take visual effect, since Windows otherwise caches non-client-area sizing.
        SetWindowPos(hwnd, HWND_TOP, 0, 0, width, height, SWP_FRAMECHANGED | SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    // Resizes an already-reparented child (e.g. the picture frame's bounds change mid-run): the Reposition counterpart for the true-reparenting path.
    public static void ResizeChild(IntPtr hwnd, int width, int height)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

}
