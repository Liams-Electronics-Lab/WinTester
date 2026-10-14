using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTester.Tools.BatteryCharge;

// Detects the Constant-Current (CC) -> Constant-Voltage (CV) transition in a completed charge run, so the
// report charts can draw "CC start/end"/"CV start/end" section markers.
//
// Uses a per-tick instantaneous V/I classification (CC: voltage below the run's own peak minus a
// per-cell margin, with current still flowing; CV: voltage at/above that point, current still flowing;
// Full/Idle: current has dropped to essentially nothing). Detect is only ever called on the final report
// (see BatteryChargeReportBuilder.Render) over the complete sample set, not on every live write, so it
// cannot re-label the run's phase-so-far mid-test.
//
// V_max is the completed run's own observed peak voltage, not the pack's Win32_Battery.DesignVoltage
// (voltageRange.MaxVoltageV: still the right figure for the voltage chart's own axis ceiling, unrelated
// to this class). DesignVoltage is a nominal/nameplate figure, not the pack's real charge-target voltage,
// and can sit below the run's actual CV plateau; using it as the CV threshold can misclassify an entire
// run as CV from the first sample. The observed peak is always available once isFinal and reflects what
// the charger actually did rather than a nameplate figure.
//
// IR-drop note: voltage here is read via the OS/WMI battery interface, not a probe at the cell terminals,
// so this classification has no way to correct for wiring resistance, a documented limitation of the
// available telemetry, not treated as a defect.
public static class BatteryChargePhaseDetector
{
    // Per-cell max used to derive a rough cell count from the pack's own peak voltage: same round-half-up
    // convention as BatteryVoltageRangeReader.CellMaxMv. Duplicated rather than shared: this class only
    // needs to stay internally consistent with that convention, not call the same code path.
    private const double CellMaxMv = 4200.0;

    // How far below the pack's own observed peak voltage still counts as CC, expressed as a fixed per-cell
    // volt margin (0.05-0.1 V/cell) rather than the old flat percent-of-design-voltage band. Mid-point of
    // that range; isolated here as an assumed default, not operator-confirmed, for an easy adjustment.
    private const double EpsVoltsPerCell = 0.075;

    // I_min: at or below this, treat the pack as not meaningfully charging (Full/Idle) rather than CC or
    // CV. Mirrors BatteryChargeRunner's own zeroEpsilonMa "current is basically off" convention (a real
    // charging sensor can read a few hundredths of a mA of noise at true float-charge) rather than a bare
    // zero comparison; kept as this class's own constant since the two files don't share one today.
    private const double CurrentCutoffMa = 1.0;

    // CV confirmation window: a rolling trailing average of Charge Current, checked tick-to-tick, so a
    // single noisy reading can't flip the classification back and forth. Verified against the real 174940
    // run's own logging cadence (BatteryChargeOptions.PollInterval defaults to 5s) before picking a number:
    // a 5s window is only one sample wide at that cadence and smooths nothing, so this uses the wide end of
    // the 5-10s range rather than the middle, giving 2-3 real samples of smoothing at the default cadence.
    private const double RollingWindowSeconds = 10.0;

    // Needs enough real samples, with BOTH voltage and current present, to classify anything meaningfully.
    // The previous voltage-only detector didn't need current; this formula does, so a run that never
    // detected charge current at all (Finding A's all-null-current case) correctly yields no markers here.
    private const int MinSamplesToDetect = 20;

    private enum TickPhase { Cc, Cv, FullIdle }

    public readonly record struct PhaseResult(
        double? CcStartElapsed, double? CcEndElapsed,
        double? CvStartElapsed, double? CvEndElapsed);

    public static PhaseResult? Detect(IReadOnlyList<BatteryChargeSample> samples)
    {
        var ordered = samples
            .Where(s => s.VoltageV is > 0 && s.ChargeCurrentMa is not null)
            .OrderBy(s => s.ElapsedSeconds)
            .ToList();
        if (ordered.Count < MinSamplesToDetect) return null;

        var vMax = ordered.Max(s => s.VoltageV!.Value);
        var cells = Math.Max(1, (int)Math.Floor(vMax * 1000.0 / CellMaxMv + 0.5));
        var cvVoltageThreshold = vMax - cells * EpsVoltsPerCell;

        // Trailing rolling mean of Charge Current, one value per ordered sample.
        var n = ordered.Count;
        var smoothed = new double[n];
        var windowStart = 0;
        var windowSum = 0.0;
        for (var i = 0; i < n; i++)
        {
            windowSum += ordered[i].ChargeCurrentMa!.Value;
            while (ordered[i].ElapsedSeconds - ordered[windowStart].ElapsedSeconds > RollingWindowSeconds)
            {
                windowSum -= ordered[windowStart].ChargeCurrentMa!.Value;
                windowStart++;
            }
            smoothed[i] = windowSum / (i - windowStart + 1);
        }

        var phase = new TickPhase[n];
        for (var i = 0; i < n; i++)
        {
            var iLive = ordered[i].ChargeCurrentMa!.Value;
            if (iLive <= CurrentCutoffMa) { phase[i] = TickPhase.FullIdle; continue; }
            var rawCv = ordered[i].VoltageV!.Value >= cvVoltageThreshold;
            // Confirm CV only when the rolling-averaged current isn't still rising [an instantaneous
            // voltage reading above threshold while current is flat/climbing is CC-phase ripple (this
            // pack's own charger alternates between two current levels through most of CC] see the
            // health algorithm's Finding C), not a real CC->CV transition.
            var trendingDown = i == 0 || smoothed[i] <= smoothed[i - 1];
            phase[i] = rawCv && trendingDown ? TickPhase.Cv : TickPhase.Cc;
        }

        // Single monotonic CC->CV transition: the CC phase, if any, ends at the LAST tick still classified
        // CC; CV is only scanned for after that point. This generalizes the old backward-scan detector's
        // own "once CV starts it doesn't revert" assumption rather than dropping it; without it, this
        // pack's own CC-phase current oscillation (confirmed real, not sensor noise) could otherwise
        // fracture one run into several alternating CC/CV segments instead of one clean transition.
        var firstCcIndex = Array.IndexOf(phase, TickPhase.Cc);
        var lastCcIndex = Array.LastIndexOf(phase, TickPhase.Cc);
        var transitionIndex = lastCcIndex + 1;

        double? ccStart = firstCcIndex >= 0 ? ordered[firstCcIndex].ElapsedSeconds : null;
        double? ccEnd = lastCcIndex >= 0 ? ordered[lastCcIndex].ElapsedSeconds : null;

        double? cvStart = null, cvEnd = null;
        for (var i = transitionIndex; i < n; i++)
        {
            if (phase[i] != TickPhase.Cv) continue;
            cvStart ??= ordered[i].ElapsedSeconds;
            cvEnd = ordered[i].ElapsedSeconds;
        }

        if (ccStart is null && cvStart is null) return null; // nothing but idle/full ticks - no phases to mark
        return new PhaseResult(ccStart, ccEnd, cvStart, cvEnd);
    }
}
