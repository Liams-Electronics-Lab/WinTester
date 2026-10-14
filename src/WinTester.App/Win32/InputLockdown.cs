using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinTester.App.Win32;

// Global WH_KEYBOARD_LL low-level keyboard hook that swallows Escape outright and Alt+F4 specifically, for
// the duration of a fullscreen test run (MainViewModel.EnterFullscreen/ExitFullscreen own its lifetime).
// Needed alongside Win32Interop.ReparentAsChild's WS_DISABLED bit because WS_DISABLED only blocks window-
// message routing, while FurMark apparently polls raw keyboard state directly, a system-wide hook
// swallows the keystroke before any application, including one polling raw state, ever sees it. Swallowing
// Escape at this level also blocks it from reaching any window's message queue, including MainWindow's own
// Escape-to-Cancel KeyBinding; the onEscapePressed callback routes around that by telling the app "Escape
// happened" through a channel independent of the blocked queue, so Cancel-via-Escape keeps working. The
// hook runs for the whole fullscreen duration by design, not scoped to the GPU tile alone. A low-level hook
// callback only fires on the thread that installed it while that thread pumps messages, so this class must
// be constructed from the WPF UI thread; every current call path is UI-thread-bound, but this hasn't been
// confirmed on real hardware.
public sealed class InputLockdown : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_ESCAPE = 0x1B;
    private const int VK_F4 = 0x73;
    private const int VK_LMENU = 0xA4; // left Alt
    private const int VK_RMENU = 0xA5; // right Alt

    // Retains a reference to the delegate for the hook's lifetime; otherwise the GC can collect it out
    // from under the unmanaged callback pointer Windows holds, a classic P/Invoke-hook footgun.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action? _onEscapePressed;
    private readonly Dispatcher _dispatcher;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    // onEscapePressed fires via Dispatcher.BeginInvoke (posted, not called inline, so the hook callback
    // returns quickly rather than risking Windows' low-level-hook response-time budget) whenever this hook
    // swallows Escape; wire it to the app's own Escape-to-Cancel action. Optional; null means Escape is
    // swallowed with no further action.
    public InputLockdown(Action? onEscapePressed = null)
    {
        _onEscapePressed = onEscapePressed;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
        if (OperatingSystem.IsWindows())
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                if (vkCode == VK_ESCAPE)
                {
                    if (_onEscapePressed != null)
                        _dispatcher.BeginInvoke(_onEscapePressed);
                    return (IntPtr)1; // swallow; never passed to FurMark or anything else
                }

                if (vkCode == VK_F4 && (IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU)))
                    return (IntPtr)1; // Alt+F4; swallow only when an Alt key is currently held
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsKeyDown(int vKey) => (GetKeyState(vKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
