using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinTester.Tools.BatteryDrain;

// One cross-check comparison: label, our own figure, the independent figure it's checked against, and the resulting discrepancy. OtherWh/DiscrepancyPercent are null when the independent source had nothing usable [distinct from a real 0% discrepancy] and Note explains why either way.
public sealed record SanityCheckComparison(
    string Label, double? OurWh, double? OtherWh, double? DiscrepancyPercent, string Note);

// Result of one run's cross-validation pass; see BatteryDrainSanityCheck for what each comparison checks.
public sealed record BatteryDrainSanityCheckResult(
    IReadOnlyList<SanityCheckComparison> Comparisons,
    bool ImplausibleRateFlagged,
    string? ImplausibleRateNote);

// Cross-validation pass: a headline drain figure can look inconsistent against a powercfg row purely
// because of a mismatched time window, not a real discrepancy; this class makes that distinction
// explicit and visible in every report rather than a one-off note. Three independent checks, each a
// visible line that never silently overrides the headline "Battery Drained" figure:
// - Overlap-windowed powercfg comparison: sums only drain events whose [start, start+duration) overlaps this run's window, instead of comparing against an arbitrary separately-windowed row; no overlap reports "nothing to compare," not a failed/zero discrepancy.
// - WMI-vs-sensor cross-check: re-derives drained-Wh from sensor-service percent (LibreHardwareMonitorLib) times BatteryHealthReader's full-charge capacity; an independent path from the headline WMI BatteryStatus reading, which should agree if all sources are healthy.
// - Coarse implausible-rate guard: flags a physically impossible rate (net capacity increase off AC, or an implausibly fast full drain) as a sanity floor/ceiling, independent of whether a comparison source was available.
// Best-effort throughout: a missing input degrades just that comparison to "nothing to compare," never the whole report.
public static class BatteryDrainSanityCheck
{
    // Fallback ceiling in watts when full-charge capacity is unknown; the capacity-multiple check below is preferred whenever available since it scales with the actual battery.
    private const double AbsoluteImplausibleRateWattsFloor = 250.0;

    // A rate implying the whole battery would empty this many times over in one hour is treated as a sensor glitch, not a genuinely fast drain.
    private const double ImplausibleCapacityMultiplePerHour = 20.0;

    // Net capacity increase (Wh) below this during a discharge run is treated as sensor noise, not a real anomaly.
    private const double NegativeDrainNoiseToleranceWh = 0.05;

    public static BatteryDrainSanityCheckResult Evaluate(
        DateTime runStart,
        DateTime effectiveEndTime,
        double? measuredDrainedWh,
        double? startPercent,
        double? endPercent,
        double? fullChargeCapacityWh,
        BatteryHistoryResult? history)
    {
        var comparisons = new List<SanityCheckComparison>
        {
            OverlapPowerCfgComparison(runStart, effectiveEndTime, measuredDrainedWh, history),
            PercentDerivedComparison(measuredDrainedWh, startPercent, endPercent, fullChargeCapacityWh)
        };

        var (flagged, rateNote) = CheckImplausibleRate(runStart, effectiveEndTime, measuredDrainedWh, fullChargeCapacityWh);

        return new BatteryDrainSanityCheckResult(comparisons, flagged, rateNote);
    }

    private static SanityCheckComparison OverlapPowerCfgComparison(
        DateTime runStart, DateTime effectiveEndTime, double? measuredDrainedWh, BatteryHistoryResult? history)
    {
        const string label = "vs. powercfg (overlap-windowed)";

        if (history is null || !history.Success || history.DrainEvents.Count == 0)
            return new SanityCheckComparison(label, measuredDrainedWh, null, null,
                "powercfg /batteryreport history wasn't available for this run, nothing to compare.");

        double overlapMwh = 0;
        var anyOverlap = false;

        foreach (var e in history.DrainEvents)
        {
            if (e.StartTime is not { } evStart || e.EnergyDrainedMwh is not { } evMwh)
                continue;

            var duration = ParseHms(e.DurationText) ?? TimeSpan.Zero;
            var evEnd = evStart + duration;

            // Overlap test: the event's [start, end) span intersects this run's [runStart, effectiveEndTime) span; a zero-duration event still counts as a point-overlap if its instant falls inside the window.
            var overlaps = evStart < effectiveEndTime && (evEnd > runStart || (duration == TimeSpan.Zero && evStart >= runStart));
            if (!overlaps) continue;

            overlapMwh += evMwh;
            anyOverlap = true;
        }

        if (!anyOverlap)
            return new SanityCheckComparison(label, measuredDrainedWh, null, null,
                "No powercfg drain-event row overlaps this run's own time window; this run likely hasn't " +
                "surfaced in powercfg's log yet, or the report's lookback window doesn't reach back far " +
                "enough. Comparing against a non-overlapping row would be a mismatched-time-window " +
                "comparison, not a real discrepancy check (see session notes).");

        var overlapWh = overlapMwh / 1000.0;
        double? discrepancy = measuredDrainedWh is { } m && Math.Abs(m) > 0.001
            ? Math.Abs(m - overlapWh) / Math.Abs(m) * 100.0
            : null;

        var note = discrepancy is { } d
            ? $"powercfg's own overlapping drain-event row(s) for this exact run window report {overlapWh:0.00} Wh."
            : "Overlapping powercfg row(s) found, but this run's own measured figure isn't available yet to compare against.";

        return new SanityCheckComparison(label, measuredDrainedWh, overlapWh, discrepancy, note);
    }

    private static SanityCheckComparison PercentDerivedComparison(
        double? measuredDrainedWh, double? startPercent, double? endPercent, double? fullChargeCapacityWh)
    {
        const string label = "vs. sensor % × full-charge capacity";

        if (startPercent is null || endPercent is null || fullChargeCapacityWh is null)
            return new SanityCheckComparison(label, measuredDrainedWh, null, null,
                "Sensor-service percent and/or BatteryHealthReader's full-charge capacity weren't both " +
                "available yet, nothing to compare.");

        var percentDerivedWh = (startPercent.Value - endPercent.Value) / 100.0 * fullChargeCapacityWh.Value;
        double? discrepancy = measuredDrainedWh is { } m && Math.Abs(m) > 0.001
            ? Math.Abs(m - percentDerivedWh) / Math.Abs(m) * 100.0
            : null;

        var note = discrepancy is not null
            ? "Independently re-derived from sensor-service (LibreHardwareMonitor) battery percent and " +
              "BatteryHealthReader's full-charge capacity, rather than the live remaining-capacity mWh " +
              "reading the headline figure uses."
            : "This run's own measured remaining-capacity figure isn't available yet to compare against.";

        return new SanityCheckComparison(label, measuredDrainedWh, percentDerivedWh, discrepancy, note);
    }

    private static (bool flagged, string? note) CheckImplausibleRate(
        DateTime runStart, DateTime effectiveEndTime, double? measuredDrainedWh, double? fullChargeCapacityWh)
    {
        if (measuredDrainedWh is not { } drainedWh)
            return (false, null);

        if (drainedWh < -NegativeDrainNoiseToleranceWh)
            return (true, $"Remaining capacity *increased* by {-drainedWh:0.00} Wh during what should be a " +
                           "discharge-only run; more than sensor noise would explain. The run should have " +
                           "stopped automatically if AC was actually reconnected; treat this figure with " +
                           "suspicion rather than as a real reading.");

        var elapsedHours = (effectiveEndTime - runStart).TotalHours;
        if (elapsedHours <= 0) return (false, null); // Avoids div-by-zero on a same-instant read; too early to judge.

        var rateWattsHour = drainedWh / elapsedHours;
        var ceiling = fullChargeCapacityWh is { } fcc && fcc > 0
            ? fcc * ImplausibleCapacityMultiplePerHour
            : AbsoluteImplausibleRateWattsFloor;

        if (rateWattsHour > ceiling)
        {
            var basis = fullChargeCapacityWh is { } fcc2 && fcc2 > 0
                ? $"{ImplausibleCapacityMultiplePerHour:0}x this battery's own full-charge capacity ({fcc2:0.#} Wh)"
                : $"a fixed {AbsoluteImplausibleRateWattsFloor:0} W floor (full-charge capacity unknown)";
            return (true, $"Implied drain rate ({rateWattsHour:0.0} W) exceeds {basis}: likely a sensor " +
                           "glitch or bad capacity read rather than a genuine reading.");
        }

        return (false, null);
    }

    private static readonly Regex HmsRegex = new(@"^(\d+):(\d{2}):(\d{2})$", RegexOptions.Compiled);

    // Parses powercfg's "H:MM:SS" duration text (e.g. "1:15:32"); hours are unbounded since an "Active" span can exceed a day.
    private static TimeSpan? ParseHms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = HmsRegex.Match(text.Trim());
        if (!m.Success) return null;

        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            && int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
            && int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? new TimeSpan(h, min, s)
            : null;
    }
}
