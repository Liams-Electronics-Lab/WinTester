using System.Windows.Media;

namespace WinTester.App.ViewModels;

// One display row in the running-state modal's live telemetry cluster grid. Immutable per tick, 
// MainViewModel rebuilds the whole TelemetryRows collection on every poll rather than mutating rows in
// place, since the set of rows itself changes per running test (CPU always shown, others only when
// relevant), not just the values within a fixed set.
public sealed class TelemetryRowViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

// One row in the always-on, top-left "system stats" overlay, modelled after RTSS/MSI Afterburner-style
// on-screen displays: bold, large, color-coded readouts (load, temp, clock, power, RAM/VRAM, fan speeds,
// model names). Always shows everything available rather than filtering to the running test the way
// TelemetryRowViewModel's cluster grid does; an operator watching a GPU test still wants CPU numbers
// holding steady in view, not just GPU ones.
public sealed class SystemStatRowViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }

    // Category accent color (e.g. cyan for CPU, magenta for GPU, amber for RAM) so the overlay reads at a
    // glance like RTSS's OSD, rather than a flat wall of white text.
    public required Brush AccentBrush { get; init; }

    // True when this row is a diagnostic/warning line (e.g. "Not running as Administrator...") rather than
    // a sensor reading; rendered in a distinct warning color regardless of its assigned AccentBrush.
    public bool IsWarning { get; init; }
}
