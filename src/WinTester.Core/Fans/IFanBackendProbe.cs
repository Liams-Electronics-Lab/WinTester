namespace WinTester.Core.Fans;

// Optional capability alongside IFanControlService: a vendor-specific backend implements this so
// FanBackendSelector can confirm the backend's actual mechanism (a WMI class, a vendor DLL, a kernel
// driver) is present before trusting a manufacturer-string match alone. The generic LibreHardwareMonitor
// backend does not implement this — LHM being available is already a precondition for the app running.
// The selector treats "no IFanBackendProbe implementation" as "always eligible," the fallback Generic
// needs.
public interface IFanBackendProbe
{
    // Confirms the backend's underlying mechanism is actually present. Must never throw — return false
    // for any failure (missing WMI class, driver not loaded, DLL not found, etc.).
    bool TryActivate();
}
