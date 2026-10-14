using System;
using WinTester.Tools.BatteryDrain;

namespace WinTester.Tools.BatteryCharge;

// The three named sweep modes, each with its own start-percent gate and stop-condition set; see BatteryChargeOptions for the exact numbers and BatteryChargeRunner for where each is enforced. No mode requires operator-typed input; QuickTest's starting percent is a fixed 95% gate, so every mode always has a non-null FixedStartThresholdPercent. None of the three gates block a run either; BatteryChargeRunner's discharge-prep routine runs automatically instead of failing the run when the battery starts above the selected mode's gate.
public enum BatteryChargeSweepMode
{
    // Start gate: battery below 15%. The only mode producing an extrapolated (dotted-line) 0-15% chart segment, since it's the only start gate below the chart's natural 0% origin.
    WideSweep,

    // Start gate: battery below 95%, fixed rather than operator-typed. No extrapolation; the chart starts exactly where the run started, at 0:00 elapsed.
    QuickTest,

    // Start gate: battery below 50%. Adds a peak-current-stall stop condition on top of the zero-current/100%/manual conditions every mode shares, see BatteryChargeRunner.
    PeakSweep
}

// Operator-set options for one Battery Charge Test run; immutable once started, same posture as BatteryDrainOptions. The test never drives its own CPU/GPU load during logging; it always logs at idle, since load can suppress or throttle charging and would contaminate the charge-current/rate figures this test measures. No mode requires operator-typed input and none fails the run outright for starting too high: each has a fixed FixedStartThresholdPercent (15%/95%/50%), and if the battery is at or above it when Start is hit, BatteryChargeRunner runs an automatic discharge-prep routine first [unplug charger, quick CPU-only stress until below threshold, reconnect charger] before falling through to the normal "connect charger" gate and idle logging.
public sealed class BatteryChargeOptions
{
    public required BatteryChargeSweepMode SweepMode { get; init; }

    // Off by default: whether the discharge-prep routine also runs a GPU load (off-screen FurMark2 OpenGL, same recipe as PowerCalibrationRunner) alongside the CPU-only math_primenumber stress, purely to drain below the threshold faster. Off by default since CPU alone is normally enough and extra engines add failure surface (driver reset, GPU crash, extra heat) to what's meant to be a quick setup step. A documented default, not confirmed; flip if CPU-only prep proves too slow.
    public bool UseGpuLoadInPrep { get; init; } = false;

    // How often BatteryChargeRunner samples sensors/writes a log line during the actual charge-logging
    // phase (not the discharge-prep or charger-wait phases, which keep their own fixed 1s cadence; those
    // are short setup steps, not the long-running measurement). Default of 5s rather than 1s since a Wide
    // Sweep run can span hours, and 1s would produce far more samples/log volume than the charts or grading
    // algorithm need. Exposed as a settable property (not a fixed constant like ZeroCurrentStopAfter) so a
    // UI control can override it; no such control exists yet; wiring an operator-facing input is tracked
    // separately.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    // How long charge current must sit at (LHM-reported) zero before the "stopped/disconnected" stop
    // condition fires: shared across all modes. Set to 20 minutes rather than a shorter window since a
    // slow-updating charge-current sensor can otherwise false-stop a run that's still actively charging;
    // see BatteryChargeRunner's zero-current tracking, which also resets this window whenever Battery %
    // itself is visibly still climbing (independent proof charging is still happening even if the current
    // reading looks stuck at zero).
    public static readonly TimeSpan ZeroCurrentStopAfter = TimeSpan.FromMinutes(20);

    // Battery % rise (in percentage points) that counts as proof charging is still active even when the
    // charge-current reading sits at/near zero: see BatteryChargeRunner's zero-current tracking. An
    // assumed default, not operator-confirmed: small enough that real charging progress over the 20-minute
    // window is virtually guaranteed to clear it, large enough that ordinary percent-reading noise/rounding
    // won't falsely reset the stall clock forever.
    public const double ZeroCurrentPercentRiseResetThreshold = 0.5;

    // Voltage rise (in volts) since the zero-current window opened that counts as proof charging is still
    // active, same reset mechanism as the percent-based threshold above but aimed at deep-discharge
    // precharge; a badly-depleted pack can take real charge in ~1mV BMS steps well before Battery % itself
    // visibly moves or a slow current sensor shows anything but ~zero. Set to 2mV (two steps) rather than
    // 1mV so a single noisy/rounded voltage reading can't false-reset the window on its own; an assumed
    // default, not operator-confirmed.
    public const double ZeroCurrentVoltageRiseResetThresholdV = 0.002;

    // How long charge current must hold near its rolling-average peak before Peak Sweep's stall-stop
    // condition fires. Gated by EnablePeakStallDetection/PeakStallMinBatteryPercent below so it can only be
    // evaluated once the pack is genuinely near full; the only point in a charge where "current stopped
    // climbing" is actually suspicious rather than normal CC-phase tapering. 45 minutes is wide enough that
    // an early turn-on transient can't become a spurious "peak" the pack's own steady current never beats.
    // Exposed as a settable instance property so a setup-panel control can override it, no such control
    // exists yet; tracked alongside PollInterval's own not-yet-wired UI control.
    public TimeSpan PeakStallStopAfter { get; init; } = TimeSpan.FromMinutes(45);

    // Whether Peak Sweep's stall-stop condition runs at all. Defaults to on since the condition is still
    // useful for catching a genuinely stalled/failing charge near full, but an unusual pack/charger combo
    // that legitimately behaves oddly near the gate below should be able to turn it off entirely rather than
    // just widening the window. Same not-yet-wired-to-UI posture as PeakStallStopAfter above.
    public bool EnablePeakStallDetection { get; init; } = true;

    // Battery % the pack must have reached before the stall-stop condition is even evaluated; below this,
    // a flat or declining charge current is normal CC-phase behavior, not a sign of stalling. 90% is an
    // assumed default, not operator-confirmed: most packs are well into CV taper by then, which is where a
    // genuine current-plateau-without-reaching-100% actually is suspicious. Same not-yet-wired-to-UI posture
    // as the two properties above.
    public double PeakStallMinBatteryPercent { get; init; } = 90.0;

    // Fraction below the best rolling-average charge current seen so far (PeakStallRollingWindow) that still
    // counts as "holding at peak" rather than declining, since the pack's normal, steady CC current can sit
    // a few percent below a noise spike or its own known two-current-level alternation [see
    // BatteryChargePhaseDetector] without the charge actually stalling. 5% is an assumed default, not
    // operator-confirmed; tune if a real run shows either false stops or a stall that runs unreasonably long.
    public const double PeakStallToleranceFraction = 0.05;

    // Window used to average charge-current samples together before comparing against the peak for the
    // stall condition above; smooths out a single noisy/duty-cycled sample so it isn't judged against the
    // single best-ever instantaneous reading. Independent of PollInterval; BatteryChargeRunner works out how
    // many samples this spans from whatever PollInterval the run is actually using.
    public static readonly TimeSpan PeakStallRollingWindow = TimeSpan.FromSeconds(60);

    // The mode-specific "must be below this percent to log" gate: see each BatteryChargeSweepMode member for the exact figures. Starting at or above this value doesn't reject the run: BatteryChargeRunner's discharge-prep routine runs automatically instead.
    public double FixedStartThresholdPercent => SweepMode switch
    {
        BatteryChargeSweepMode.WideSweep => 15.0,
        BatteryChargeSweepMode.QuickTest => 95.0,
        BatteryChargeSweepMode.PeakSweep => 50.0,
        _ => throw new ArgumentOutOfRangeException(nameof(SweepMode))
    };

    // Sanity check only: mode-threshold enforcement lives in BatteryChargeRunner's discharge-prep routine. The one thing rejected up front: a battery already at 100%, since there's nothing to log and no discharge-prep target makes sense at the ceiling. Returns null when valid, or a human-readable rejection reason.
    public string? ValidateStartGate(double? currentBatteryPercent)
    {
        if (currentBatteryPercent is { } pct && pct >= 100.0)
            return "Battery is already at 100%, nothing to log.";
        return null;
    }

    public override string ToString()
    {
        var modeText = SweepMode switch
        {
            BatteryChargeSweepMode.WideSweep => "Wide Sweep (<15% \u2192 100%)",
            BatteryChargeSweepMode.QuickTest => "Quick Test (<95% \u2192 100%)",
            BatteryChargeSweepMode.PeakSweep => "Peak Sweep (<50% \u2192 100%)",
            _ => SweepMode.ToString()
        };
        var prepText = UseGpuLoadInPrep ? "CPU+GPU discharge-prep" : "CPU-only discharge-prep";
        return $"{modeText} / idle (no CPU/GPU load during logging) / {prepText}";
    }
}
