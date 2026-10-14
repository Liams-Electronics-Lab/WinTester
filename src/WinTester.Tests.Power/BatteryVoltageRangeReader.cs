using System;
using System.Runtime.Versioning;

namespace WinTester.Tests.Power;

// Computes the Battery Drain/Charge report voltage chart's fixed y-axis range, so it reads as "how full is
// the pack" (0%-under-load cutoff to full-charge) instead of auto-scaling to whatever narrow band this
// particular run happened to sample. Same try/degrade-to-null posture as SystemIdentityReader; a WMI
// failure or non-Windows host just means the report's voltage chart falls back to auto-scaling.
//
// Reads Win32_Battery.DesignVoltage (root\cimv2, a different WMI class/namespace than BatteryHealthReader's
// root\WMI BatteryStaticData) and derives a per-cell-count 0% cutoff from it:
//
//   cells    = round(DesignVoltage / 4200)              -- 4200 mV = standard NMC/LCO per-cell max
//   V(0%)    = cells * cutoffMv                          -- cutoffMv = per-cell voltage the BMS treats as 0%
//   V(100%)  = DesignVoltage, unadjusted
//
// e.g. a 16,929 mV pack: cells = round(16929/4200) = round(4.03) = 4, V(0%) = 4 * 3100 = 12,400 mV (12.40V).
//
// cutoffMv defaults to 3100 mV (typical laptop pack under normal load); the operator can pass 3000 for a
// more pessimistic (heavy-load) cutoff or 3200 for a more optimistic (idle/light-load) one.
public static class BatteryVoltageRangeReader
{
    private const double DefaultCutoffMv = 3100.0;
    private const double CellMaxMv = 4200.0;

    public static BatteryVoltageRangeResult Read(double cutoffMv = DefaultCutoffMv)
    {
        if (!OperatingSystem.IsWindows())
            return new BatteryVoltageRangeResult(null, null, null);

        try
        {
            var designMv = ReadDesignVoltageMv();
            if (designMv is not (> 0))
                return new BatteryVoltageRangeResult(null, null, null);

            // floor(x + 0.5) = round-half-up, matching the formula's rounding exactly rather than relying
            // on Math.Round's banker's-rounding default (immaterial here since a fractional cell count is
            // never exactly *.5 in practice, but this keeps the code visibly matching the documented formula).
            var cells = Math.Max(1, (int)Math.Floor(designMv.Value / CellMaxMv + 0.5));
            var minMv = cells * cutoffMv;

            return new BatteryVoltageRangeResult(
                Math.Round(minMv / 1000.0, 2),
                Math.Round(designMv.Value / 1000.0, 2),
                Math.Round(designMv.Value / 1000.0, 2));
        }
        catch
        {
            return new BatteryVoltageRangeResult(null, null, null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static double? ReadDesignVoltageMv()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT DesignVoltage FROM Win32_Battery");
        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            // DesignVoltage is a uint16 per the Win32_Battery schema, but read defensively against whatever
            // numeric type a given provider actually marshals it as, same posture as BatteryHealthReader's
            // WMI field reads.
            if (obj["DesignVoltage"] is { } raw)
            {
                try { return Convert.ToDouble(raw); }
                catch { return null; }
            }
        }
        return null;
    }
}
