using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinTester.Core;

// Prevents the display/system from sleeping during a long-running, unattended test (Battery Charge,
// Battery Drain, burn-in). Sleep mid-run suspends the CLR and every child engine process, producing a
// misleading jump in elapsed time and battery % on wake. Wraps SetThreadExecutionState as a disposable
// guard: construct via Begin() at the start of a run, dispose when the run ends to restore normal power
// management. Uses ES_DISPLAY_REQUIRED (keeps the display on, not just the system) since these are
// shop-floor tests an operator may be watching. Never throws: degrades to an inert no-op on a
// non-Windows host or if the P/Invoke call fails.
public sealed class PowerRequestGuard : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private bool _active;
    private bool _disposed;

    private PowerRequestGuard(bool active) => _active = active;

    // Starts preventing sleep/display-off. Always returns a disposable guard; on a non-Windows host or a
    // failed call, the guard is simply inert.
    public static PowerRequestGuard Begin()
    {
        if (!OperatingSystem.IsWindows())
            return new PowerRequestGuard(active: false);

        try
        {
            SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
            return new PowerRequestGuard(active: true);
        }
        catch
        {
            return new PowerRequestGuard(active: false);
        }
    }

    public void Dispose()
    {
        if (_disposed || !_active) { _disposed = true; return; }
        _disposed = true;
        try
        {
            // Clearing SystemRequired/DisplayRequired (leaving Continuous) restores normal idle/sleep behavior.
            SetThreadExecutionState(ExecutionState.Continuous);
        }
        catch
        {
            // Best-effort release; worst case the sleep timer stays suppressed until process exit.
        }
    }
}
