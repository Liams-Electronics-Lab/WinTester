using System;
using System.Diagnostics;

namespace WinTester.Tools.Devices;

// Hands a device off to Windows' own UI. Everything mutating (installing a driver, enabling a device,
// scanning for hardware changes) is deliberately left to Device Manager rather than reimplemented here:
// this tool's job is to find and explain problems, not to fix them behind the operator's back.
public static class DeviceManagerLauncher
{
    public sealed record LaunchResult(bool Success, string? ErrorMessage)
    {
        public static readonly LaunchResult Ok = new(true, null);
        public static LaunchResult Failed(string message) => new(false, message);
    }

    public static LaunchResult OpenDeviceManager()
    {
        if (!OperatingSystem.IsWindows())
            return LaunchResult.Failed("Device Manager is only available on Windows.");

        try
        {
            // mmc.exe with the snap-in named explicitly, rather than shell-executing "devmgmt.msc" and
            // relying on the .msc file association being intact — on a machine broken enough to be on a
            // repair bench, that association is exactly the sort of thing that isn't.
            using var process = Process.Start(new ProcessStartInfo("mmc.exe", "devmgmt.msc")
            {
                UseShellExecute = true
            });

            return LaunchResult.Ok;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed($"Could not open Device Manager: {ex.Message}");
        }
    }

    // Opens the same properties dialog a double-click in Device Manager opens, for one device.
    //
    // DeviceProperties_RunDLL is the entry point Device Manager itself uses, but it is not a documented,
    // supported API — Microsoft has never published its argument contract, and it has no return value, so a
    // successful Process.Start here means "rundll32 launched", not "the dialog appeared". A device ID that
    // no longer resolves generally results in nothing visible happening rather than an error this code can
    // catch, which is why the window keeps the Open Device Manager button as the always-works fallback.
    //
    // The /MachineName "" argument targets the local machine and is required — omitting it makes some
    // Windows builds ignore the /DeviceID that follows.
    public static LaunchResult OpenDeviceProperties(string deviceId)
    {
        if (!OperatingSystem.IsWindows())
            return LaunchResult.Failed("Device properties are only available on Windows.");

        if (string.IsNullOrWhiteSpace(deviceId))
            return LaunchResult.Failed("This device has no instance ID to open properties for.");

        // Device instance IDs contain backslashes and ampersands and so must stay quoted. They never
        // contain a double quote themselves (the PnP ID character set doesn't allow one), so quoting is
        // sufficient here without further escaping.
        var arguments = $"devmgr.dll,DeviceProperties_RunDLL /MachineName \"\" /DeviceID \"{deviceId}\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("rundll32.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return LaunchResult.Ok;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed($"Could not open device properties: {ex.Message}");
        }
    }
}
