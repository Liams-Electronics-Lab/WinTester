using System;
using System.Runtime.Versioning;

namespace WinTester.Tools.BatteryDrain;

// One live read of root\WMI's BatteryStatus class, the same namespace BatteryHealthReader uses for the one-shot design/full-charge pull but a different class; BatteryStatus exposes the live remaining-capacity (mWh) reading that BatteryStaticData/BatteryFullChargedCapacity don't. Feeds the Battery Drain popup's "Current CAPACITY REMAINING mWh" field. Polled on a slower 10s cadence by BatteryDrainRunner since it's a heavier WMI query and mWh doesn't shift second-to-second the way percent/voltage can.
public sealed record BatteryRuntimeStatus(
    double? RemainingCapacityMwh,
    double? VoltageV,
    double? RateMw,
    bool? Charging,
    bool? Discharging);

public static class BatteryRuntimeStatusReader
{
    public static BatteryRuntimeStatus? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            return ReadViaWmi();
        }
        catch
        {
            // Degrades to N/A on failure, same posture as BatteryHealthReader; never crashes the drain run.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static BatteryRuntimeStatus? ReadViaWmi()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            @"root\WMI",
            "SELECT RemainingCapacity, Voltage, ChargeRate, DischargeRate, Charging, Discharging FROM BatteryStatus");

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            double? remainingMwh = obj["RemainingCapacity"] is uint rc ? rc : null;
            double? voltageV = obj["Voltage"] is uint v ? v / 1000.0 : null; // BatteryStatus reports in mV.
            bool? charging = obj["Charging"] as bool?;
            bool? discharging = obj["Discharging"] as bool?;

            double? rateMw = null;
            if (charging == true && obj["ChargeRate"] is uint cr) rateMw = cr;
            else if (discharging == true && obj["DischargeRate"] is uint dr) rateMw = dr;

            return new BatteryRuntimeStatus(remainingMwh, voltageV, rateMw, charging, discharging);
        }

        return null; // No battery instance; genuinely absent, not an error, matching BatteryHealthReader's WMI path.
    }
}
