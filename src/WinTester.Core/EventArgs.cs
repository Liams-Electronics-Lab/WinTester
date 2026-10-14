using System;

namespace WinTester.Core;

public sealed class TestProgressEventArgs : EventArgs
{
    // 0-100.
    public double ProgressPercent { get; init; }

    // Short human-readable stage description (e.g. "Running: dgemm, fma3 (floating point math)"), shown
    // as the running-state modal's stage label. Modules with sub-stages should update it as the stage changes.
    public string StatusText { get; init; } = "";

    // Seconds left in the current stage, when the module knows a fixed duration. Null when there's no
    // fixed duration to count down against; the UI hides the countdown rather than showing "0:00".
    public double? SecondsRemaining { get; init; }
}

public sealed class SensorSampleEventArgs : EventArgs
{
    public required SensorSample Sample { get; init; }
}

public sealed class TestResultEventArgs : EventArgs
{
    public required TestResult Result { get; init; }
}
