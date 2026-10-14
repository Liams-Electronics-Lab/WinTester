using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTester.Core.Session;

// Owns one run's session folder: session.json (metadata, rewritten at start/end), samples.jsonl
// (append-only telemetry), events.jsonl (append-only lifecycle events), whea.jsonl (WHEA log entries,
// created on first write), pcie.jsonl (PCIe link capability per display device, created on first write),
// and raw/<testid>.log (raw engine output for the report appendix). Every write flushes immediately, making burn-in logging
// crash-survivable; the report generator always reads back through SessionReader rather than trusting a
// live/rendered report as the source of truth. One instance = one run; the orchestrator creates exactly
// one per invocation.
public sealed class SessionDataStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _sessionDir;
    private readonly string _sessionJsonPath;
    private readonly StreamWriter _samplesWriter;
    private readonly StreamWriter _eventsWriter;
    private readonly object _writeLock = new();
    private bool _disposed;

    public SessionMetadata Metadata { get; }
    public string SessionDirectory => _sessionDir;

    private SessionDataStore(string sessionDir, SessionMetadata metadata)
    {
        _sessionDir = sessionDir;
        Metadata = metadata;
        _sessionJsonPath = Path.Combine(_sessionDir, "session.json");

        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(Path.Combine(_sessionDir, "raw"));

        // AutoFlush makes every AppendXxx call durable the moment it returns, with no reliance on a
        // graceful shutdown or Dispose() call.
        _samplesWriter = new StreamWriter(
            new FileStream(Path.Combine(_sessionDir, "samples.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };

        _eventsWriter = new StreamWriter(
            new FileStream(Path.Combine(_sessionDir, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };

        WriteMetadata();
    }

    // Starts a new session under sessionsRootDir and writes the initial session.json immediately, so a
    // crash before any test completes still leaves a folder with a start time and the selected tests.
    public static SessionDataStore CreateNew(
        string sessionsRootDir,
        string brandCompanyName,
        string brandAppName,
        IReadOnlyList<string> testsSelected,
        bool abortOnFail,
        IReadOnlyDictionary<string, string>? machineInfo = null,
        bool hwInfoDetected = false)
    {
        var startTime = DateTime.Now;
        var folderName = startTime.ToString("yyyy-MM-dd_HHmmss");
        var sessionDir = Path.Combine(sessionsRootDir, folderName);

        // Guards against silently overwriting a prior session on a same-second folder-name collision.
        int suffix = 2;
        while (Directory.Exists(sessionDir))
        {
            sessionDir = Path.Combine(sessionsRootDir, $"{folderName}_{suffix}");
            suffix++;
        }

        var metadata = new SessionMetadata
        {
            SessionId = Guid.NewGuid().ToString(),
            SessionFolderName = Path.GetFileName(sessionDir),
            BrandCompanyName = brandCompanyName,
            BrandAppName = brandAppName,
            TestsSelected = testsSelected,
            AbortOnFail = abortOnFail,
            StartTime = startTime,
            MachineInfo = machineInfo is null ? new() : new Dictionary<string, string>(machineInfo),
            HwInfoDetected = hwInfoDetected
        };

        var store = new SessionDataStore(sessionDir, metadata);
        store.AppendEvent(new SessionEvent
        {
            Type = SessionEventType.SessionStarted,
            Message = $"Session started. Tests selected: {string.Join(", ", testsSelected)}."
        });
        return store;
    }

    // Appends one line to samples.jsonl for a shared hardware poll. fallbackNotes carries Sensor
    // Fallback's per-field HWiNFO resolution notes (Section 6, point 3) for this same poll, when any
    // field needed one; omitted (null) on every ordinary tick.
    public void AppendSnapshot(SensorSnapshot snapshot, IReadOnlyList<string>? fallbackNotes = null)
    {
        AppendSampleLine(new SessionSampleRecord
        {
            Timestamp = snapshot.Timestamp,
            Kind = SessionSampleKind.HardwareSnapshot,
            Snapshot = snapshot,
            SensorFallbackNotes = fallbackNotes is { Count: > 0 } ? fallbackNotes : null
        });
    }

    // Appends one line to samples.jsonl for a module-specific extra sample.
    public void AppendModuleSample(string testId, SensorSample sample)
    {
        AppendSampleLine(new SessionSampleRecord
        {
            Timestamp = sample.Timestamp,
            Kind = SessionSampleKind.ModuleSample,
            TestId = testId,
            ModuleValues = sample.Values
        });
    }

    // Appends one line to samples.jsonl for a mapped HWiNFO poll.
    public void AppendHwInfoSample(HwInfoEventSample sample)
    {
        AppendSampleLine(new SessionSampleRecord
        {
            Timestamp = sample.Timestamp,
            Kind = SessionSampleKind.HwInfoEvents,
            HwInfo = sample
        });
    }

    // Appends one line to whea.jsonl; the file only exists for runs that recorded at least one entry.
    public void AppendWheaEvent(WheaEventRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);
        lock (_writeLock)
        {
            if (_disposed) return;
            File.AppendAllText(Path.Combine(_sessionDir, "whea.jsonl"), line + Environment.NewLine);
        }
    }

    // Appends one line to pcie.jsonl; the file only exists for runs that read at least one device.
    public void AppendPcieDevice(PcieDeviceLinkRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);
        lock (_writeLock)
        {
            if (_disposed) return;
            File.AppendAllText(Path.Combine(_sessionDir, "pcie.jsonl"), line + Environment.NewLine);
        }
    }

    private void AppendSampleLine(SessionSampleRecord record)
    {
        var line = JsonSerializer.Serialize(record, JsonOptions);
        lock (_writeLock)
        {
            if (_disposed) return;
            _samplesWriter.WriteLine(line);
        }
    }

    // Appends one line to events.jsonl: test start/progress/result or session lifecycle.
    public void AppendEvent(SessionEvent evt)
    {
        var line = JsonSerializer.Serialize(evt, JsonOptions);
        lock (_writeLock)
        {
            if (_disposed) return;
            _eventsWriter.WriteLine(line);
        }
    }

    // Writes (overwriting if called again for the same test) the raw output captured from a vendored
    // engine, for the report's raw-log appendix.
    public string WriteRawLog(string testId, string content)
    {
        var path = Path.Combine(_sessionDir, "raw", $"{SanitizeFileName(testId)}.log");
        File.WriteAllText(path, content);
        return path;
    }

    // Rewrites session.json with the final end time and result; the only file in the store that's
    // overwritten rather than appended. Safe to call at most once per store.
    public void FinalizeSession(bool overallPassed)
    {
        Metadata.EndTime = DateTime.Now;
        Metadata.OverallPassed = overallPassed;
        WriteMetadata();

        AppendEvent(new SessionEvent
        {
            Type = SessionEventType.SessionCompleted,
            Passed = overallPassed,
            Message = overallPassed ? "Session completed: PASS." : "Session completed: FAIL."
        });
    }

    private void WriteMetadata()
    {
        var json = JsonSerializer.Serialize(Metadata, MetadataJsonOptions);
        lock (_writeLock)
        {
            File.WriteAllText(_sessionJsonPath, json);
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _samplesWriter.Flush();
            _samplesWriter.Dispose();
            _eventsWriter.Flush();
            _eventsWriter.Dispose();
        }
    }
}
