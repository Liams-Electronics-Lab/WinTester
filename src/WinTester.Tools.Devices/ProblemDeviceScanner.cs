using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using WinTester.Core.Devices;

namespace WinTester.Tools.Devices;

// Enumerates devices Windows is reporting a Configuration Manager problem for, via Win32_PnPEntity.
// Read-only: nothing here installs, enables, disables, or otherwise touches a device — the Devices window
// hands anything mutating off to Device Manager itself.
public static class ProblemDeviceScanner
{
    // Every device, not just the broken ones. Filtering with "WHERE ConfigManagerErrorCode <> 0" in WQL
    // looks tidier but silently drops devices whose ConfigManagerErrorCode is NULL rather than 0 (WQL
    // comparisons against NULL are false), and a NULL there is itself worth knowing about. Pulling the
    // whole set also gives TotalDevicesScanned for free, which is the difference between "this machine is
    // clean" and "the scan didn't actually see anything".
    //
    // Only the properties actually used are selected: Win32_PnPEntity has dozens, and SELECT * makes WMI
    // marshal all of them for every device on the machine.
    private const string Query =
        "SELECT Name, PNPClass, DeviceID, ConfigManagerErrorCode, Status, Manufacturer FROM Win32_PnPEntity";

    public static Task<DeviceProblemSnapshot> ScanAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(DeviceProblemSnapshot.Failed("Device status is only available on Windows."));

        // Win32_PnPEntity enumeration routinely takes several seconds on a machine with a lot of devices,
        // and always blocks — there's no async WMI path worth the ManagementOperationObserver plumbing here,
        // so it goes on a thread-pool thread and the window awaits it.
        return Task.Run(Scan);
    }

    [SupportedOSPlatform("windows")]
    private static DeviceProblemSnapshot Scan()
    {
        var problems = new List<ProblemDevice>();
        var total = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(Query);
            using var results = searcher.Get();

            foreach (var item in results)
            {
                using var device = (ManagementObject)item;
                total++;

                var code = ReadErrorCode(device);
                if (code is null or 0) continue;

                problems.Add(Build(device, code.Value));
            }
        }
        catch (ManagementException ex)
        {
            // Typically a WMI service that's stopped or a repository that needs rebuilding; occasionally a
            // permissions failure on a locked-down machine.
            return DeviceProblemSnapshot.Failed($"WMI query failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return DeviceProblemSnapshot.Failed(
                $"Access denied reading device status: {ex.Message} Try running WinTester as administrator.");
        }

        return new DeviceProblemSnapshot
        {
            Devices = Sort(problems),
            TotalDevicesScanned = total
        };
    }

    [SupportedOSPlatform("windows")]
    private static ProblemDevice Build(ManagementObject device, uint code)
    {
        var info = DeviceErrorCodes.Describe(code);
        var pnpClass = Text(device, "PNPClass");

        return new ProblemDevice
        {
            // A device with no driver often has no friendly Name either; Device Manager falls back to
            // "Unknown device" in exactly that case, so this matches rather than showing a blank row.
            Name = Text(device, "Name") is { Length: > 0 } name ? name : "Unknown device",
            PnpClass = pnpClass,
            ClassDisplayName = DeviceClassNames.Friendly(pnpClass),
            DeviceId = Text(device, "DeviceID"),
            Manufacturer = Text(device, "Manufacturer") is { Length: > 0 } mfr ? mfr : null,
            ErrorCode = code,
            ErrorSummary = info.Summary,
            ErrorDetail = info.Detail,
            Category = info.Category,
            Status = Text(device, "Status") is { Length: > 0 } status ? status : null
        };
    }

    // ConfigManagerErrorCode is a uint32 in the schema but comes back null for devices WMI has no
    // configuration record for, so it's read defensively rather than cast straight to uint.
    [SupportedOSPlatform("windows")]
    private static uint? ReadErrorCode(ManagementObject device)
    {
        try
        {
            var raw = device["ConfigManagerErrorCode"];
            return raw is null ? null : Convert.ToUInt32(raw);
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string Text(ManagementObject device, string property)
    {
        try
        {
            return device[property]?.ToString()?.Trim() ?? string.Empty;
        }
        catch (ManagementException)
        {
            // A property that isn't populated for this particular device throws rather than returning null.
            return string.Empty;
        }
    }

    // Driver problems first, then device errors, then the deliberately-disabled ones: roughly the order a
    // tech would work through them. Within a category, group by class and then by name so the tree's rows
    // land in a stable order between scans rather than WMI's enumeration order.
    private static List<ProblemDevice> Sort(List<ProblemDevice> devices) =>
        devices
            .OrderBy(d => (int)d.Category)
            .ThenBy(d => d.ClassDisplayName == DeviceClassNames.Unclassified ? 0 : 1)
            .ThenBy(d => d.ClassDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
