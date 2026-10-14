using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTester.Core.Session;

// Everything read back from one session folder: what WinTester.Reporting builds its report from. A
// separate type from the live-writing SessionDataStore so read and write paths don't share mutable
// state, and so a crashed/partial session (missing EndTime) can still be constructed.
public sealed class SessionRecord
{
    public required SessionMetadata Metadata { get; init; }
    public required IReadOnlyList<SessionEvent> Events { get; init; }
    public required IReadOnlyList<SessionSampleRecord> Samples { get; init; }
    public required string SessionDirectory { get; init; }

    // testId -> absolute path of raw/<testid>.log, for whichever tests have one.
    public required IReadOnlyDictionary<string, string> RawLogPaths { get; init; }

    // Empty when the run recorded no WHEA entries or never queried the log.
    public IReadOnlyList<WheaEventRecord> WheaEvents { get; init; } = Array.Empty<WheaEventRecord>();

    // Empty when the run never read PCIe device properties (HWiNFO not bundled, or the read failed).
    public IReadOnlyList<PcieDeviceLinkRecord> PcieDevices { get; init; } = Array.Empty<PcieDeviceLinkRecord>();
}

// Static reader for a session folder written by SessionDataStore. Tolerant of a crashed mid-run session:
// a malformed trailing line is skipped rather than failing the whole read.
public static class SessionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static SessionRecord Load(string sessionDir)
    {
        var metadataPath = Path.Combine(sessionDir, "session.json");
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException("session.json not found; not a valid WinTester session folder.", metadataPath);

        var metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(metadataPath), JsonOptions)
            ?? throw new InvalidDataException("session.json deserialized to null.");

        var events = ReadJsonLines<SessionEvent>(Path.Combine(sessionDir, "events.jsonl"));
        var samples = ReadJsonLines<SessionSampleRecord>(Path.Combine(sessionDir, "samples.jsonl"));

        var rawDir = Path.Combine(sessionDir, "raw");
        var rawLogPaths = Directory.Exists(rawDir)
            ? Directory.GetFiles(rawDir, "*.log")
                .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p)
            : new Dictionary<string, string>();

        return new SessionRecord
        {
            Metadata = metadata,
            Events = events,
            Samples = samples,
            SessionDirectory = sessionDir,
            RawLogPaths = rawLogPaths,
            WheaEvents = ReadJsonLines<WheaEventRecord>(Path.Combine(sessionDir, "whea.jsonl")),
            PcieDevices = ReadJsonLines<PcieDeviceLinkRecord>(Path.Combine(sessionDir, "pcie.jsonl"))
        };
    }

    // Field run 2026-09-27 (sessions `212632` Broadwell and `212743` 12700H) caught this reading empty on a
    // live session on both machines: `File.ReadLines`/`StreamReader(path)` open with FileShare.Read only,
    // but SessionDataStore's writers for events.jsonl/samples.jsonl hold the file open for Write for the
    // whole session (FileMode.Append, FileAccess.Write, FileShare.Read -- see SessionDataStore). A Write
    // handle already open elsewhere is only compatible with a new open request whose own share flags permit
    // Write too, regardless of what share flags the existing handle itself was opened with; a reader that
    // only asks to share Read collides with it and throws IOException ("...being used by another process")
    // any time this runs while the session recorder is still active -- which PowerTestModule's session-
    // telemetry reuse always does, since it reads the running session's own events/samples mid-run, not
    // after the session folder is closed. FileShare.ReadWrite here lets this coexist with that live writer.
    private static List<T> ReadJsonLines<T>(string path)
    {
        var results = new List<T>();
        if (!File.Exists(path)) return results;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (item is not null) results.Add(item);
            }
            catch (JsonException)
            {
                // Skip a truncated/corrupt trailing line (e.g. write interrupted by a crash, or this read
                // racing the writer mid-line) rather than failing the entire read.
            }
        }

        return results;
    }
}
