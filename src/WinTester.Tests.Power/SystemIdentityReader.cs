using System;
using System.Runtime.Versioning;

namespace WinTester.Tests.Power;

// Reads system identity (Win32_ComputerSystem Manufacturer/Model, Win32_Bios SerialNumber) via System.Management, same try/degrade-on-failure posture as MachineClassifier. BatteryDrainRunner uses this for Device Name/Manufacturer (matching powercfg's own header) instead of a battery-IOCTL field hardware often doesn't answer, and for Serial Number only as a fallback.
// Each field is read independently so one empty property can't blank the others; degrades to all-null (never throws) on WMI failure or non-Windows host.
public static class SystemIdentityReader
{
    public static SystemIdentityResult Read()
    {
        if (!OperatingSystem.IsWindows())
            return new SystemIdentityResult(null, null, null);

        try
        {
            return ReadViaWmi();
        }
        catch
        {
            return new SystemIdentityResult(null, null, null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static SystemIdentityResult ReadViaWmi()
    {
        string? manufacturer = null;
        string? model = null;

        try
        {
            using var csSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementBaseObject obj in csSearcher.Get())
            {
                manufacturer = CleanString(obj["Manufacturer"] as string);
                model = CleanString(obj["Model"] as string);
                break;
            }
        }
        catch
        {
            // Query failed; leave manufacturer/model null; the independent Bios read below is still worth attempting.
        }

        string? serial = null;

        try
        {
            using var biosSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT SerialNumber FROM Win32_Bios");
            foreach (System.Management.ManagementBaseObject obj in biosSearcher.Get())
            {
                serial = CleanString(obj["SerialNumber"] as string);
                break;
            }
        }
        catch
        {
            // Query failed; leave serial null.
        }

        return new SystemIdentityResult(manufacturer, model, serial);
    }

    // Many OEM BIOS/SMBIOS tables leave these fields as placeholder junk ("System Serial Number", "To Be Filled By O.E.M.") rather than blank; treated as not-reported, same posture as Win32BatteryInterop.DecodeChemistry.
    private static string? CleanString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        return trimmed.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}
