using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinTester.Tests.Power;

// Single native GetSystemPowerStatus call (kernel32); cheapest AC/battery query available, no WMI or device enumeration. Used as a fast cross-check alongside BatteryHealthReader, not a replacement: it exposes only AC/DC state, live percent, and remaining/full battery life, never capacity, cycle count, chemistry, or serial number.
public sealed record SystemPowerStatusResult(
    bool Success,
    string AcLineStatus,
    int? BatteryLifePercent,
    string BatteryFlagDescription,
    int? BatteryLifeTimeSeconds,
    int? BatteryFullLifeTimeSeconds,
    string Detail);

public static class SystemPowerStatusReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private const byte BATTERY_PERCENT_UNKNOWN = 255;
    private const uint BATTERY_LIFE_UNKNOWN = 0xFFFFFFFF;

    public static SystemPowerStatusResult Read()
    {
        if (!OperatingSystem.IsWindows())
            return new SystemPowerStatusResult(false, "Unknown", null, "Unknown", null, null, "Not running on Windows.");

        try
        {
            return ReadNative();
        }
        catch (Exception ex)
        {
            return new SystemPowerStatusResult(false, "Unknown", null, "Unknown", null, null,
                $"GetSystemPowerStatus failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SystemPowerStatusResult ReadNative()
    {
        if (!GetSystemPowerStatus(out var s))
            return new SystemPowerStatusResult(false, "Unknown", null, "Unknown", null, null,
                $"GetSystemPowerStatus returned failure (Win32 error {Marshal.GetLastWin32Error()}).");

        var acLine = s.ACLineStatus switch
        {
            0 => "Offline (DC/battery)",
            1 => "Online (AC)",
            _ => "Unknown"
        };

        int? percent = s.BatteryLifePercent == BATTERY_PERCENT_UNKNOWN ? null : s.BatteryLifePercent;

        // BatteryFlag is a bit field, not an enum; 128 (no battery) and 255 (unknown) are sentinel exceptions to that.
        string flagDesc;
        if (s.BatteryFlag == 255) flagDesc = "Unknown";
        else if ((s.BatteryFlag & 128) != 0) flagDesc = "No system battery";
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((s.BatteryFlag & 1) != 0) parts.Add("High");
            if ((s.BatteryFlag & 2) != 0) parts.Add("Low");
            if ((s.BatteryFlag & 4) != 0) parts.Add("Critical");
            if ((s.BatteryFlag & 8) != 0) parts.Add("Charging");
            flagDesc = parts.Count > 0 ? string.Join(" + ", parts) : "Normal";
        }

        int? lifeTime = s.BatteryLifeTime == BATTERY_LIFE_UNKNOWN ? null : (int)s.BatteryLifeTime;
        int? fullLifeTime = s.BatteryFullLifeTime == BATTERY_LIFE_UNKNOWN ? null : (int)s.BatteryFullLifeTime;

        return new SystemPowerStatusResult(true, acLine, percent, flagDesc, lifeTime, fullLifeTime,
            "Read via GetSystemPowerStatus (kernel32.dll); quick AC/DC + percent cross-check, not a " +
            "capacity/health source.");
    }
}
