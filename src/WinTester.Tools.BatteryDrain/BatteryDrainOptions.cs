using System;

namespace WinTester.Tools.BatteryDrain;

// Load engine(s) driving the drain; reuses the Power tile's CPU/GPU calibration shape (PowerCalibrationRunner) instead of a new one. Options dialog enforces at least one selection before Start is enabled.
public enum BatteryDrainMethod
{
    CpuOnly,
    GpuOnly,
    CpuAndGpu
}

// Operator-set options for one Battery Drain run; immutable once started. BatteryDrainWindow builds this from its options panel and hands it to BatteryDrainRunner. MaxDuration and StopPercent race independently; whichever is reached first stops the run; null MaxDuration just removes the time side of that race. SafetyFloorPercent is a hard floor on top of both, never overridden.
public sealed class BatteryDrainOptions
{
    // Checked every tick; run stops once battery percent drops to or below this, independent of MaxDuration. Typically set to 5-10% for a meaningful runtime check without fully deep-discharging the pack.
    public required int StopPercent { get; init; }

    // Null = infinite, leaving StopPercent (or the safety floor) as the only stop condition; non-null races against StopPercent as a wall-clock cap. Built from the UI's infinite/3h/2h/1h presets or a custom entry.
    public TimeSpan? MaxDuration { get; init; }

    public required BatteryDrainMethod Method { get; init; }

    // How often BatteryDrainRunner samples sensors/writes a log line during the actual drain-logging phase
    // (not the "unplug charger" wait gate, which keeps its own fixed 1s cadence; that's a short setup step,
    // not the long-running measurement). Configurable rather than a fixed constant, mirroring
    // BatteryChargeOptions.PollInterval: a drain run can span hours, and a 1s cadence produces far more
    // samples/log volume than the charts need. No UI control wires it yet.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    // Hard floor applied regardless of mode; stops any run immediately at this level since a true 0% shutdown mid-stress-test risks corruption and yields no useful data. Fixed, not operator-configurable, for now.
    public const int SafetyFloorPercent = 2;

    public override string ToString()
    {
        var lengthText = MaxDuration is { } d ? FormatDuration(d) : "Infinite";
        var methodText = Method switch
        {
            BatteryDrainMethod.CpuOnly => "CPU stress",
            BatteryDrainMethod.GpuOnly => "GPU stress (FurMark, off-screen)",
            BatteryDrainMethod.CpuAndGpu => "CPU + GPU stress",
            _ => Method.ToString()
        };
        return $"Stop at {StopPercent}% or max length {lengthText}, whichever is first / {methodText}";
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{d.Minutes}m";
}
