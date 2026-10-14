using System;
using System.Collections.Generic;

namespace WinTester.Core;

// Outcome of a single test module run.
public sealed class TestResult
{
    public bool Passed { get; init; }
    public string? FailureReason { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public Dictionary<string, object> KeyMetrics { get; init; } = new();

    // Reference to the raw log/output captured from the underlying engine, for the report appendix.
    public string? RawLogPath { get; init; }

    // Short machine-readable strings (e.g. "GPU_PARTIAL_BENCH_COVERAGE") a module can raise for a
    // degraded-but-not-failed condition. A module that raises a flag here should also add a
    // human-readable line to KeyMetrics so it isn't lost. Empty, never null.
    public List<string> Flags { get; init; } = new();
}
