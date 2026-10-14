using System;
using System.Collections.Generic;

namespace WinTester.Core.Session;

// Written once at session start and rewritten (not appended) at session end; the only file in the
// session folder that's overwritten; everything else stays append-only so a crash mid-run never
// corrupts history.
public sealed class SessionMetadata
{
    public required string SessionId { get; init; }
    public required string SessionFolderName { get; init; }
    public required string BrandCompanyName { get; init; }
    public required string BrandAppName { get; init; }
    public required IReadOnlyList<string> TestsSelected { get; init; }
    public bool AbortOnFail { get; init; }
    public DateTime StartTime { get; init; }

    // Null while the session is still in progress; a reader seeing null should treat it as still-running
    // or crashed-mid-run, not as malformed data.
    public DateTime? EndTime { get; set; }

    public bool? OverallPassed { get; set; }

    // Machine identification pulled from sensors/WMI: CPU/GPU model, RAM size, storage model, BIOS/board
    // info. Free-form key/value; populate what's available, omit what isn't.
    public Dictionary<string, string> MachineInfo { get; init; } = new();

    // Whether the matching HWiNFO exe (HWiNFO64.exe or HWiNFO32.exe under vendor\hwinfo\) was found on
    // disk when this run started. Reflects exe presence only, not whether HWiNFO's shared memory came up
    // in time; a run can have this true with no HWiNFO data elsewhere in the report if the shared memory
    // wait timed out.
    public bool HwInfoDetected { get; init; }
}

// Kinds of entries appended to events.jsonl.
public enum SessionEventType
{
    SessionStarted,
    TestStarted,
    TestProgress,
    TestCompleted,
    SessionAborted,
    SessionCompleted
}

// One line of events.jsonl. Deliberately one flat shape for every event type so the file stays
// line-parseable; unused fields for a given Type are simply left null/default.
public sealed class SessionEvent
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public SessionEventType Type { get; init; }
    public string? TestId { get; init; }
    public string? Message { get; init; }
    public double? ProgressPercent { get; init; }
    public bool? Passed { get; init; }
    public string? FailureReason { get; init; }

    // Snapshot of TestResult.KeyMetrics, flattened to strings for stable JSON round-tripping. Set only on
    // TestCompleted events.
    public Dictionary<string, string>? KeyMetrics { get; init; }
}

// Which kind of reading a SessionSampleRecord line carries.
public enum SessionSampleKind
{
    // A shared hardware poll; the ISensorService.GetSnapshot() read every module and the report's charts draw from.
    HardwareSnapshot,

    // Module-specific extra telemetry raised via ITestModule.SensorSampleCaptured.
    ModuleSample,

    // Mapped HWiNFO event/text readings from one shared-memory poll.
    HwInfoEvents
}

// One line of samples.jsonl. Both the shared hardware poll and module-specific samples land in this
// single append-only file, distinguished by Kind, giving one crash-survivable telemetry timeline per session.
public sealed class SessionSampleRecord
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public SessionSampleKind Kind { get; init; }

    // Set only when Kind == ModuleSample.
    public string? TestId { get; init; }

    // Set only when Kind == HardwareSnapshot.
    public SensorSnapshot? Snapshot { get; init; }

    // Sensor Fallback, Phase B (WINTESTER_SENSOR_FALLBACK_TRACKER.md, Section 6, point 3): which
    // Snapshot fields (if any) this poll filled from HWiNFO rather than LibreHardwareMonitor, and which
    // raw HWiNFO label won. Null/empty on every tick where nothing needed a fallback, so a normal machine
    // produces no extra noise in samples.jsonl. Set only when Kind == HardwareSnapshot.
    public IReadOnlyList<string>? SensorFallbackNotes { get; init; }

    // Set only when Kind == ModuleSample.
    public Dictionary<string, double>? ModuleValues { get; init; }

    // Set only when Kind == HwInfoEvents.
    public HwInfoEventSample? HwInfo { get; init; }
}
