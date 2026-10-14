using System;
using System.Runtime.Versioning;

namespace WinTester.Tests.Power;

// Classifies desktop vs laptop; keys off a WMI Win32_Battery entry, an ACPI PNP0C0A battery node, or an
// ACPI0003 AC-adapter node; not readable battery capacity alone, since a dead/removed battery still
// reports the AC-adapter node and would otherwise misclassify as desktop.
// Degrades to Desktop on WMI failure or non-Windows host, matching this codebase's "return what you can" WMI posture; an unrelated ACPI battery-class device on a real desktop (e.g. a UPS) remains an unclosed residual risk.
public static class MachineClassifier
{
    public static MachineKind Classify()
    {
        if (!OperatingSystem.IsWindows())
            return MachineKind.Desktop;

        try
        {
            return ClassifyViaWmi();
        }
        catch
        {
            return MachineKind.Desktop;
        }
    }

    [SupportedOSPlatform("windows")]
    private static MachineKind ClassifyViaWmi()
    {
        using var battSearcher = new System.Management.ManagementObjectSearcher(
            "SELECT DeviceID FROM Win32_Battery");
        if (battSearcher.Get().Count > 0)
            return MachineKind.Laptop;

        // PNP0C0A (ACPI battery) and ACPI0003 (AC adapter) device IDs are prefix-matched, not exact, since the full DeviceID carries a per-instance suffix (e.g. "ACPI\PNP0C0A\2&daba3ff&0").
        using var pnpSearcher = new System.Management.ManagementObjectSearcher(
            "SELECT DeviceID FROM Win32_PnPEntity " +
            "WHERE DeviceID LIKE 'ACPI\\\\PNP0C0A%' OR DeviceID LIKE 'ACPI\\\\ACPI0003%'");
        if (pnpSearcher.Get().Count > 0)
            return MachineKind.Laptop;

        return MachineKind.Desktop;
    }
}
