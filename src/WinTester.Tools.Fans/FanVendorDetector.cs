using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.Versioning;
using WinTester.Core.Fans;

namespace WinTester.Tools.Fans;

// Maps a machine's manufacturer strings to a FanVendor candidate. Read-only, side-effect-free WMI reads
// only — no probing of any vendor-specific mechanism here (that's IFanBackendProbe's job); this class
// only answers what the manufacturer string suggests. Never throws; degrades to FanVendor.Generic on any
// WMI failure or non-Windows host.
public static class FanVendorDetector
{
    public static FanVendor Detect()
    {
        if (!OperatingSystem.IsWindows()) return FanVendor.Generic;

        try
        {
            return DetectViaWmi();
        }
        catch
        {
            return FanVendor.Generic;
        }
    }

    [SupportedOSPlatform("windows")]
    private static FanVendor DetectViaWmi()
    {
        var manufacturer = ReadBaseBoardOrSystemManufacturer();
        if (manufacturer is null) return FanVendor.Generic;

        if (manufacturer.Contains("gigabyte", StringComparison.OrdinalIgnoreCase))
            return FanVendor.Gigabyte;

        // "ASUSTeK COMPUTER INC." is the usual string; matched on the "asus" substring so shorter OEM
        // variants still match.
        if (manufacturer.Contains("asus", StringComparison.OrdinalIgnoreCase))
            return IsLaptopChassis() ? FanVendor.AsusLaptop : FanVendor.AsusDesktop;

        if (manufacturer.Contains("dell", StringComparison.OrdinalIgnoreCase))
            return FanVendor.Dell;

        if (manufacturer.Contains("lenovo", StringComparison.OrdinalIgnoreCase))
            return FanVendor.Lenovo;

        return FanVendor.Generic;
    }

    // Win32_BaseBoard.Manufacturer first: on a desktop this is the actual board maker. Falls back to
    // Win32_ComputerSystem.Manufacturer when the board field is blank, common on laptops where the OEM
    // doesn't always populate a separate board-manufacturer string.
    [SupportedOSPlatform("windows")]
    private static string? ReadBaseBoardOrSystemManufacturer()
    {
        var baseBoard = ReadSingleString("SELECT Manufacturer FROM Win32_BaseBoard", "Manufacturer");
        if (!string.IsNullOrWhiteSpace(baseBoard)) return baseBoard;

        return ReadSingleString("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer");
    }

    // SMBIOS chassis-type codes (Win32_SystemEnclosure.ChassisTypes) commonly seen on portables:
    // 8=Portable, 9=Laptop, 10=Notebook, 11=Hand Held, 12=Docking Station, 14=Sub Notebook, 30=Tablet,
    // 31=Convertible, 32=Detachable.
    private static readonly HashSet<ushort> LaptopChassisTypes = new() { 8, 9, 10, 11, 12, 14, 30, 31, 32 };

    [SupportedOSPlatform("windows")]
    private static bool IsLaptopChassis()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                if (obj["ChassisTypes"] is not ushort[] types) continue;

                foreach (var type in types)
                {
                    if (LaptopChassisTypes.Contains(type)) return true;
                }
            }
        }
        catch
        {
            // Query failed; fall through to "not a laptop" below.
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadSingleString(string query, string field)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementBaseObject obj in searcher.Get())
                return (obj[field] as string)?.Trim();
        }
        catch
        {
            // Query failed; caller falls back to its next candidate or to Generic.
        }

        return null;
    }
}
