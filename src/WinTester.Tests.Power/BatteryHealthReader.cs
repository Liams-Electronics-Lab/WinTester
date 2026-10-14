using System;
using System.Runtime.Versioning;

namespace WinTester.Tests.Power;

// Battery Health: Health % = full-charge capacity ÷ design capacity × 100, computed independently from root\WMI (BatteryStaticData/BatteryFullChargedCapacity) and SetupAPI/IOCTL (Win32BatteryInterop, powercfg's own route); deliberately queries both rather than falling back, since the two can disagree, and shows whichever gives the lower (more conservative) %, with Wh figures taken from that same winning source.
// SerialNumber/ManufacturerName/DeviceName are IOCTL-only (WMI doesn't expose them) and merged in whenever IOCTL succeeded, independent of which source won on %; CycleCount/Chemistry are available from both sources and merged with their own preference order (see the merge branch below).
// Degrades to an explicit HasBatteryInterface = false [never a misleading 0%] on query failure, non-Windows host, or no battery present.
public static class BatteryHealthReader
{
    // lhm layers a third health-percent candidate (LHM's Degradation Level sensor) and a near-full-charge capacity override (LHM's Remaining Capacity sensor) on top of the WMI-vs-IOCTL comparison below; omitting it preserves the original two-source-only behavior for callers without a live SensorSnapshot.
    public static BatteryHealthResult Read(LhmBatteryHint? lhm = null)
    {
        if (!OperatingSystem.IsWindows())
            return new BatteryHealthResult(false, null, null, null, "Not running on Windows.");

        BatteryHealthResult? wmiResult = null;
        string? wmiError = null;
        try
        {
            wmiResult = ReadViaWmi();
        }
        catch (Exception ex)
        {
            wmiError = ex.Message;
        }

        BatteryHealthResult? iocResult = null;
        string? iocError = null;
        try
        {
            iocResult = Win32BatteryInterop.TryReadBatteryInformation();
        }
        catch (Exception ex)
        {
            iocError = ex.Message;
        }

        var wmiOk = wmiResult is { HasBatteryInterface: true };
        var iocOk = iocResult is { HasBatteryInterface: true };

        if (!wmiOk && !iocOk)
        {
            return new BatteryHealthResult(false, null, null, null,
                $"No battery interface detected (root\\WMI: {wmiError ?? wmiResult?.Detail ?? "no instance"}; " +
                $"SetupAPI/IOCTL: {iocError ?? iocResult?.Detail ?? "no instance"}).");
        }

        // Only one source produced a usable instance; used as-is but still run through the LHM layer below rather than returning early, so single-source machines still get the third-source check and capacity override.
        BatteryHealthResult baseline;
        string baselineDetailSuffix;
        if (wmiOk && !iocOk)
        {
            baseline = wmiResult!;
            baselineDetailSuffix = " [Only root\\WMI produced a usable battery instance this run.]";
        }
        else if (iocOk && !wmiOk)
        {
            baseline = iocResult!;
            baselineDetailSuffix = " [Only SetupAPI/IOCTL produced a usable battery instance this run.]";
        }
        else
        {
            // Both sources have a reading; pick the lower HealthPercent (more conservative), preferring whichever side has one if the other lacks it. Serial Number/Manufacturer/Device Name always come from iocResult (WMI never exposes them), regardless of which side wins on %. Cycle Count and Chemistry are merged rather than forced to one side: Cycle Count prefers whichever answered (IOCTL first, the original source), Chemistry prefers WMI (what powercfg itself reads), falling back to IOCTL's FourCC decode only when WMI's column is empty.
            var w = wmiResult!;
            var i = iocResult!;
            BatteryHealthResult chosen =
                w.HealthPercent is null && i.HealthPercent is null ? w :
                w.HealthPercent is null ? i :
                i.HealthPercent is null ? w :
                w.HealthPercent <= i.HealthPercent ? w : i;

            var chosenLabel = ReferenceEquals(chosen, w) ? "root\\WMI" : "SetupAPI/IOCTL";
            var otherPercent = ReferenceEquals(chosen, w) ? i.HealthPercent : w.HealthPercent;

            baseline = chosen with
            {
                CycleCount = i.CycleCount ?? w.CycleCount,
                SerialNumber = i.SerialNumber,
                Chemistry = w.Chemistry ?? i.Chemistry,
                ManufacturerName = i.ManufacturerName,
                DeviceName = i.DeviceName,
            };
            baselineDetailSuffix = otherPercent is { } op
                ? $" [Cross-checked against the other source's {op:0.0}%; the lower of the two ({chosenLabel}) is shown.]"
                : " [Only this source produced a usable health % this run.]";
        }

        return ApplyLhmLayer(baseline, baselineDetailSuffix, lhm);
    }

    // Applies two LHM-derived adjustments on top of Read's WMI/IOCTL baseline. First, a near-full-charge capacity override: if CurrentPercent is ≥99% and RemainingCapacityMwh is lower than the baseline full-charge capacity, LHM's more conservative figure replaces it and Health % is recomputed from the same Design Capacity (gated to near-full-charge since a lower-charge reading isn't comparable).
    // Second, a third-source Degradation Level check: 100 - DegradationPercent is compared against the (possibly overridden) baseline and the lower one wins, same conservative-source rule as the WMI-vs-IOCTL comparison; LHM has no Wh figures of its own, so a win there changes only the displayed Health %.
    private static BatteryHealthResult ApplyLhmLayer(BatteryHealthResult baseline, string baselineDetailSuffix, LhmBatteryHint? lhm)
    {
        var result = baseline;
        var extraNotes = new System.Text.StringBuilder();

        if (lhm?.RemainingCapacityMwh is { } lhmCapMwh && lhm.CurrentPercent is >= 99.0 &&
            result.DesignCapacityWh is { } designWh && designWh > 0 &&
            result.FullChargeCapacityWh is { } baselineFullWh && lhmCapMwh / 1000.0 < baselineFullWh)
        {
            var lhmFullWh = lhmCapMwh / 1000.0;
            var recomputedHealth = Math.Round((lhmFullWh / designWh) * 100.0, 1);
            extraNotes.Append($" [Near-full charge: LHM's own Remaining Capacity sensor read a lower " +
                               $"{lhmFullWh:0.0} Wh than the {baselineFullWh:0.0} Wh WMI/IOCTL full-charge " +
                               "figure above; using LHM's more conservative reading for both Wh and health%.]");
            result = result with { FullChargeCapacityWh = Math.Round(lhmFullWh, 1), HealthPercent = recomputedHealth };
        }

        if (lhm?.DegradationPercent is { } degradation)
        {
            var lhmHealth = Math.Round(100.0 - degradation, 1);
            if (result.HealthPercent is null || lhmHealth < result.HealthPercent)
            {
                var otherPercent = result.HealthPercent;
                extraNotes.Append(otherPercent is { } op
                    ? $" [LHM's Degradation Level sensor implies a lower {lhmHealth:0.0}% health than the " +
                      $"{op:0.0}% WMI/IOCTL figure [showing LHM's (more conservative) figure; Wh values above " +
                      "are still from WMI/IOCTL, which has no equivalent LHM-side figure of its own.]"
                    : $" [No WMI/IOCTL health % was available] using LHM's Degradation Level sensor " +
                      $"({lhmHealth:0.0}% health) instead.]");
                result = result with { HealthPercent = lhmHealth };
            }
            else
            {
                extraNotes.Append($" [Cross-checked against LHM's Degradation Level sensor ({lhmHealth:0.0}% " +
                                   "health); the WMI/IOCTL figure above was already the more conservative of the two.]");
            }
        }

        return result with { Detail = result.Detail + baselineDetailSuffix + extraNotes };
    }

    [SupportedOSPlatform("windows")]
    private static BatteryHealthResult ReadViaWmi()
    {
        // SELECT now also pulls Chemistry, not just DesignedCapacity, so WMI can answer the report's Chemistry field on machines that have it. BatteryStaticData.Chemistry is schema-defined as a 4-byte uint8 array (raw FourCC bytes, unlike IOCTL's packed ULONG), but DecodeWmiChemistry reads it defensively in case a provider marshals it differently.
        using var designSearcher = new System.Management.ManagementObjectSearcher(
            @"root\WMI", "SELECT DesignedCapacity, Chemistry FROM BatteryStaticData");

        double? designMwh = null;
        string? chemistry = null;
        foreach (System.Management.ManagementBaseObject obj in designSearcher.Get())
        {
            if (obj["DesignedCapacity"] is uint dc)
                designMwh = dc;

            chemistry ??= DecodeWmiChemistry(obj["Chemistry"]);

            if (designMwh is not null) break;
        }

        if (designMwh is null)
            return new BatteryHealthResult(false, null, null, null, "No battery interface detected.");

        using var fullSearcher = new System.Management.ManagementObjectSearcher(
            @"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");

        double? fullMwh = null;
        foreach (System.Management.ManagementBaseObject obj in fullSearcher.Get())
        {
            if (obj["FullChargedCapacity"] is uint fc)
            {
                fullMwh = fc;
                break;
            }
        }

        // Second, independent Cycle Count source: some driver/firmware combinations populate WMI's BatteryCycleCount class when IOCTL's CycleCount field doesn't (or vice versa), so both are queried independently. This class doesn't exist on some systems; best-effort, must not fail the whole read, same posture as Win32BatteryInterop.ReadStringInfo.
        int? cycleCount = null;
        try
        {
            using var cycleSearcher = new System.Management.ManagementObjectSearcher(
                @"root\WMI", "SELECT CycleCount FROM BatteryCycleCount");
            foreach (System.Management.ManagementBaseObject obj in cycleSearcher.Get())
            {
                // Same 0/missing = unknown convention as the IOCTL path; 0 here isn't a trustworthy "0 cycles," it's this provider's unset convention.
                if (obj["CycleCount"] is uint cc and > 0 and < 0xFFFFFFFF)
                {
                    cycleCount = (int)cc;
                    break;
                }
            }
        }
        catch
        {
            // Class not present on this system/driver; leave cycleCount null, same as other unsupported-optional-field cases in this file.
        }

        double designWh = designMwh.Value / 1000.0;

        if (fullMwh is null)
        {
            return new BatteryHealthResult(true, Math.Round(designWh, 1), null, null,
                "Design capacity found, but no BatteryFullChargedCapacity reading was available, " +
                "health % cannot be computed.", CycleCount: cycleCount, Chemistry: chemistry);
        }

        double fullWh = fullMwh.Value / 1000.0;
        double healthPercent = designWh > 0 ? (fullWh / designWh) * 100.0 : 0;

        return new BatteryHealthResult(
            true,
            Math.Round(designWh, 1),
            Math.Round(fullWh, 1),
            Math.Round(healthPercent, 1),
            $"Design capacity {designWh:0.0}Wh, current full-charge capacity {fullWh:0.0}Wh.",
            CycleCount: cycleCount,
            Chemistry: chemistry);
    }

    // Handles both shapes a WMI provider might return for chemistry: a byte[] (BatteryStaticData's documented shape) decoded directly, or a packed uint reusing Win32BatteryInterop.DecodeChemistry rather than duplicating it. Returns null, never garbage, for any other shape or non-printable bytes.
    private static string? DecodeWmiChemistry(object? raw)
    {
        switch (raw)
        {
            case byte[] { Length: > 0 } bytes:
            {
                var chars = new char[bytes.Length];
                int len = 0;
                foreach (var b in bytes)
                {
                    if (b == 0) break;
                    if (b < 0x20 || b > 0x7E) return null; // not printable ASCII; unreported, not garbage
                    chars[len++] = (char)b;
                }
                return len > 0 ? new string(chars, 0, len) : null;
            }
            case uint u:
                return Win32BatteryInterop.DecodeChemistry(u);
            default:
                return null;
        }
    }
}
