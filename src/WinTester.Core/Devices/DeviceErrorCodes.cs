using System.Collections.Generic;

namespace WinTester.Core.Devices;

// Maps a Win32_PnPEntity ConfigManagerErrorCode (the same number Device Manager shows as "This device
// cannot start. (Code 10)") to a short badge label, a plain-English explanation, and the category the
// Devices tool groups it under.
//
// Summaries are deliberately close to Device Manager's own wording rather than reworded, so a tech can
// match what this tool says against what Device Manager and web searches say about the same code. Codes
// 0-52 are the documented set; anything outside it still renders, with a generic description, rather than
// being dropped from the list.
public static class DeviceErrorCodes
{
    public sealed record ErrorInfo(string Summary, string Detail, DeviceProblemCategory Category);

    // Code 0 is included so a caller that hands over a working device gets a sensible answer rather than
    // the unknown-code fallback; the scanner filters these out long before this point.
    private static readonly Dictionary<uint, ErrorInfo> Codes = new()
    {
        [0] = new("Working properly", "This device is working properly.", DeviceProblemCategory.DeviceError),
        [1] = new("Not configured correctly", "This device is not configured correctly. Usually means no driver, or the wrong driver, is installed.", DeviceProblemCategory.DriverProblem),
        [3] = new("Driver corrupted or low memory", "The driver for this device might be corrupted, or the system may be running low on memory or other resources.", DeviceProblemCategory.DriverProblem),
        [9] = new("Invalid hardware ID", "Windows cannot identify this hardware because it does not have a valid hardware identification number.", DeviceProblemCategory.DeviceError),
        [10] = new("Cannot start", "This device cannot start. Often a hardware fault, a resource conflict, or a driver that failed to initialise.", DeviceProblemCategory.DeviceError),
        [12] = new("Not enough resources", "This device cannot find enough free resources that it can use (IRQ, DMA, memory range).", DeviceProblemCategory.DeviceError),
        [14] = new("Restart required", "This device cannot work properly until the computer is restarted.", DeviceProblemCategory.DeviceError),
        [16] = new("Resources not identified", "Windows cannot identify all the resources this device uses.", DeviceProblemCategory.DeviceError),
        [18] = new("Reinstall drivers", "Reinstall the drivers for this device.", DeviceProblemCategory.DriverProblem),
        [19] = new("Registry configuration damaged", "Windows cannot start this device because its registry configuration is incomplete or damaged.", DeviceProblemCategory.DriverProblem),
        [21] = new("Being removed", "Windows is removing this device. The entry should disappear after a restart.", DeviceProblemCategory.DeviceError),
        [22] = new("Disabled", "This device is disabled. Usually deliberate — enable it in Device Manager if it should be in use.", DeviceProblemCategory.Disabled),
        [24] = new("Not present or drivers missing", "This device is not present, is not working properly, or does not have all its drivers installed.", DeviceProblemCategory.DriverProblem),
        [28] = new("Drivers not installed", "The drivers for this device are not installed.", DeviceProblemCategory.DriverProblem),
        [29] = new("Firmware gave no resources", "This device is disabled because the firmware of the device did not give it the required resources. Check the BIOS/UEFI.", DeviceProblemCategory.DeviceError),
        [31] = new("Cannot load drivers", "This device is not working properly because Windows cannot load the drivers required for this device.", DeviceProblemCategory.DriverProblem),
        [32] = new("Driver service disabled", "A driver for this device has been disabled. An alternate driver may be providing this functionality.", DeviceProblemCategory.DriverProblem),
        [33] = new("Resources undetermined", "Windows cannot determine which resources are required for this device. Usually a hardware fault.", DeviceProblemCategory.DeviceError),
        [34] = new("Needs manual configuration", "Windows cannot determine the settings for this device. It must be configured manually.", DeviceProblemCategory.DeviceError),
        [35] = new("Firmware info missing", "The computer's system firmware does not include enough information to properly configure and use this device. Check for a BIOS/UEFI update.", DeviceProblemCategory.DeviceError),
        [36] = new("Interrupt translation failure", "This device is requesting a PCI interrupt but is configured for an ISA interrupt, or the reverse. Change the IRQ settings in the BIOS/UEFI.", DeviceProblemCategory.DeviceError),
        [37] = new("Driver failed to initialise", "Windows cannot initialize the device driver for this hardware — the driver returned a failure from its entry point.", DeviceProblemCategory.DriverProblem),
        [38] = new("Previous driver instance in memory", "Windows cannot load the device driver because a previous instance of the driver is still in memory. A restart normally clears it.", DeviceProblemCategory.DriverProblem),
        [39] = new("Driver corrupted or missing", "Windows cannot load the device driver for this hardware. The driver may be corrupted or missing.", DeviceProblemCategory.DriverProblem),
        [40] = new("Registry service key wrong", "Windows cannot access this hardware because its service key information in the registry is missing or recorded incorrectly.", DeviceProblemCategory.DriverProblem),
        [41] = new("Driver loaded, hardware not found", "Windows successfully loaded the device driver but cannot find the hardware device.", DeviceProblemCategory.DeviceError),
        [42] = new("Duplicate device running", "Windows cannot load the device driver because there is a duplicate device already running in the system.", DeviceProblemCategory.DeviceError),
        [43] = new("Stopped, reported problems", "Windows has stopped this device because it has reported problems. Commonly a failing or unstable piece of hardware.", DeviceProblemCategory.DeviceError),
        [44] = new("Stopped by software", "An application or service has shut down this hardware device.", DeviceProblemCategory.DeviceError),
        [45] = new("Not connected", "This hardware device is not connected to the computer. The entry is a leftover from the last time it was attached.", DeviceProblemCategory.DeviceError),
        [46] = new("Unavailable, shutting down", "This device is not available because the system is shutting down.", DeviceProblemCategory.DeviceError),
        [47] = new("Prepared for removal", "This device has been prepared for safe removal but has not been removed from the computer.", DeviceProblemCategory.DeviceError),
        [48] = new("Blocked as incompatible", "The software for this device has been blocked from starting because it is known to have problems with Windows. Needs a newer driver.", DeviceProblemCategory.DriverProblem),
        [49] = new("Registry hive too large", "Windows cannot start new hardware devices because the system hive is too large (exceeds the registry size limit).", DeviceProblemCategory.DeviceError),
        [50] = new("Properties not applied", "Windows cannot apply all of the properties for this device.", DeviceProblemCategory.DeviceError),
        [51] = new("Waiting on another device", "This device is currently waiting on another device or set of devices to start.", DeviceProblemCategory.DeviceError),
        [52] = new("Driver signature unverified", "Windows cannot verify the digital signature for the drivers required for this device.", DeviceProblemCategory.DriverProblem)
    };

    public static ErrorInfo Describe(uint code) =>
        Codes.TryGetValue(code, out var info)
            ? info
            : new ErrorInfo(
                $"Unknown error (Code {code})",
                "Windows reported a Configuration Manager error code that isn't in the documented 0-52 range. " +
                "Open the device's properties for the message Windows itself shows.",
                DeviceProblemCategory.DeviceError);

    public static string CategoryDisplayName(DeviceProblemCategory category) => category switch
    {
        DeviceProblemCategory.DriverProblem => "Driver missing or failed",
        DeviceProblemCategory.DeviceError => "Device errors",
        DeviceProblemCategory.Disabled => "Disabled devices",
        _ => "Other"
    };
}
