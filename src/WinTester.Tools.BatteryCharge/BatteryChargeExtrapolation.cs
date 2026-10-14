using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTester.Tools.BatteryCharge;

// One synthetic (dotted-line) point for Wide Sweep's un-logged 0-15% span. Shares a real sample's chart-relevant field shape so the report can hand both kinds to Chart.js as one dataset, differentiated only by type at the call site.
public sealed record ExtrapolatedPoint(double ElapsedSeconds, double Percent);

// Result of the extrapolation formula: either synthetic 0%-to-start-% points plus the extra time they add to the chart's x-axis origin, or an explanation for why extrapolation was skipped, see Compute.
public sealed record ExtrapolationResult(
    IReadOnlyList<ExtrapolatedPoint> Points,
    double ExtraDurationSeconds,
    bool Omitted,
    string? OmittedReason);

// Wide Sweep's 0-15% chart extrapolation; a documented assumption, not confirmed against a real charge curve:
// 1. Fits a simple linear (least-squares) percent-vs-time slope across the first 5 minutes of the run (or the whole run if shorter); a constant-current/CC-phase assumption realistic for this low-state-of-charge bulk-charging span.
// 2. Back-projects that slope from the real first sample to 0% to get an extrapolated duration, then generates evenly-spaced synthetic points across that span, landing exactly on the real first sample so the dotted line meets the solid line with no seam.
// 3. A zero or negative fitted slope makes extrapolation meaningless; the dotted segment is omitted and the report says so, rather than showing a nonsensical projection.
// A flat Battery% reading across the whole fit window is a real scenario (the OS's percent sensor can hold
// the same cached value for several minutes before its next coarse update). That case doesn't reliably make
// the least-squares slope compute to exactly 0: the algebraically-correct answer is 0, but computed as a
// difference of two large nearly-equal floating-point sums (catastrophic cancellation), it can land on a
// tiny nonzero epsilon of essentially random sign. A positive epsilon isn't caught by `slope <= 0`, and
// extrapolatedDurationSeconds = startPercent / slope then produces a duration on the order of 1e17 seconds,
// breaking the chart's linear x-axis. Guarded against by detecting the flat-window condition directly [checking the actual observed Battery% range across fitSet, which is exact and doesn't suffer this
// cancellation] rather than trusting the least-squares slope's sign. A duration sanity cap is kept as a
// second line of defense in case some other input combination produces a similarly-tiny slope through a
// different path.
public static class BatteryChargeExtrapolation
{
    private static readonly TimeSpan FitWindow = TimeSpan.FromMinutes(5);
    private const int SyntheticPointCount = 12; // Just needs to look smooth on the chart; not real data.

    // If the observed percent range across the fit window is smaller than this, treat it as "didn't
    // change" rather than trusting whatever tiny slope the least-squares fit happens to compute, the
    // OS's percent sensor reports in whole- or half-percent steps, so anything this far below one step
    // is sensor noise, not a real trend.
    private const double FlatWindowPercentEpsilon = 1e-6;

    // A generous ceiling: even a very slow trickle-charge extrapolating from a near-zero starting percent
    // shouldn't imply a multi-week runway to reach that percent from 0. Anything beyond this points at a
    // degenerate fit (e.g. the flat-window case above slipping past detection) rather than a real answer.
    private const double MaxPlausibleDurationSeconds = 7 * 24 * 60 * 60; // 1 week

    public static ExtrapolationResult Compute(IReadOnlyList<BatteryChargeSample> samples)
    {
        var withPercent = samples.Where(s => s.BatteryPercent is not null).ToList();
        if (withPercent.Count < 2)
            return new ExtrapolationResult(Array.Empty<ExtrapolatedPoint>(), 0,
                Omitted: true, "Not enough logged samples to fit an extrapolation slope.");

        var first = withPercent[0];
        var windowEnd = first.ElapsedSeconds + FitWindow.TotalSeconds;
        var fitSet = withPercent.Where(s => s.ElapsedSeconds <= windowEnd).ToList();
        if (fitSet.Count < 2) fitSet = withPercent; // Run shorter than the fit window; uses everything.

        // Exact check (min/max, no cancellation risk) for "the sensor hasn't reported a real change yet", 
        // done before the least-squares fit so a flat window never reaches the fragile floating-point path.
        var fitPercents = fitSet.Select(s => s.BatteryPercent!.Value).ToList();
        var percentRange = fitPercents.Max() - fitPercents.Min();
        if (percentRange < FlatWindowPercentEpsilon)
            return new ExtrapolationResult(Array.Empty<ExtrapolatedPoint>(), 0, Omitted: true,
                "The battery percent reading didn't change during the first few minutes of the run (the " +
                "OS's percent sensor updates in coarse steps), so the 0-15% span couldn't be meaningfully " +
                "extrapolated.");

        // Least-squares slope (percent per second) over (ElapsedSeconds, Percent) pairs.
        var n = fitSet.Count;
        var sumX = fitSet.Sum(s => s.ElapsedSeconds);
        var sumY = fitSet.Sum(s => s.BatteryPercent!.Value);
        var sumXY = fitSet.Sum(s => s.ElapsedSeconds * s.BatteryPercent!.Value);
        var sumXX = fitSet.Sum(s => s.ElapsedSeconds * s.ElapsedSeconds);
        var denom = n * sumXX - sumX * sumX;

        double slope;
        if (Math.Abs(denom) < 1e-9)
        {
            // Degenerate case; all samples at the same elapsed-time bucket; falls back to the two-point slope.
            var last = fitSet[^1];
            var dt = last.ElapsedSeconds - first.ElapsedSeconds;
            if (dt <= 0)
                return new ExtrapolationResult(Array.Empty<ExtrapolatedPoint>(), 0,
                    Omitted: true, "Could not fit a slope from the run's first samples.");
            slope = (last.BatteryPercent!.Value - first.BatteryPercent!.Value) / dt;
        }
        else
        {
            slope = (n * sumXY - sumX * sumY) / denom;
        }

        if (slope <= 0)
            return new ExtrapolationResult(Array.Empty<ExtrapolatedPoint>(), 0, Omitted: true,
                "The observed charge rate over the first few minutes wasn't positive, so the 0-15% span " +
                "couldn't be meaningfully extrapolated.");

        var startPercent = first.BatteryPercent!.Value;
        if (startPercent <= 0)
            return new ExtrapolationResult(Array.Empty<ExtrapolatedPoint>(), 0, Omitted: true,
                "The run's first logged sample was already at 0%, nothing to extrapolate.");

        var extrapolatedDurationSeconds = startPercent / slope;

        // Second line of defense: an implausibly long projection means the fitted slope is unreliable
        // (whether from this specific flat-window scenario or something else), not a real answer worth
        // drawing.
        if (extrapolatedDurationSeconds > MaxPlausibleDurationSeconds)
            return new ExtrapolationResult(Array.Empty<ExtrapolatedPoint>(), 0, Omitted: true,
                "The fitted charge rate implied an implausibly long run-up to 0%, so the 0-15% span " +
                "couldn't be meaningfully extrapolated.");

        var points = new List<ExtrapolatedPoint>();
        for (var i = 0; i < SyntheticPointCount; i++)
        {
            var frac = (double)i / SyntheticPointCount; // 0 (0%) up to but not including 1 (real start).
            var elapsed = first.ElapsedSeconds - extrapolatedDurationSeconds * (1 - frac);
            var pct = startPercent * frac;
            points.Add(new ExtrapolatedPoint(elapsed, pct));
        }
        // Land exactly on the real first sample so the dotted line meets the solid line with no seam.
        points.Add(new ExtrapolatedPoint(first.ElapsedSeconds, startPercent));

        return new ExtrapolationResult(points, extrapolatedDurationSeconds, Omitted: false, null);
    }
}
