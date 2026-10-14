namespace WinTester.Core.Fans;

// Vendor candidate a machine's manufacturer/chassis strings can map to. Generic means either no vendor
// match was found or no dedicated backend is registered for that vendor yet; FanBackendSelectionResult
// carries which applied. AsusDesktop/AsusLaptop are separate values because they route to different
// backends with different risk profiles: a plain WMI class for desktops, a vendor-owned DLL for laptops.
public enum FanVendor
{
    Generic,
    Gigabyte,
    AsusDesktop,
    AsusLaptop,
    Dell,
    Lenovo
}
