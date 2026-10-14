using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using WinTester.Core.HardwareErrors;

namespace WinTester.Tools.HardwareErrors;

// Reads Windows Hardware Error Architecture (WHEA) entries from the System event log. Read-only.
public static class HardwareErrorScanner
{
    // Upper bound on events read. A machine with a failing DIMM can log tens of thousands of corrected
    // errors, and formatting each description is the slow part of a scan. The newest are read first, so
    // hitting the cap drops the oldest.
    public const int MaxEvents = 2000;

    private const string LogName = "System";

    // WHEA events are written to the System channel by this provider.
    private const string Query = "*[System/Provider[@Name=\"Microsoft-Windows-WHEA-Logger\"]]";

    public static Task<HardwareErrorSnapshot> ScanAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(HardwareErrorSnapshot.Failed("Hardware error history is only available on Windows."));

        // EventLogReader is synchronous and formatting thousands of descriptions takes real time, so the
        // scan runs on a thread-pool thread and the window awaits it.
        return Task.Run(Scan);
    }

    [SupportedOSPlatform("windows")]
    private static HardwareErrorSnapshot Scan()
    {
        var events = new List<HardwareErrorEvent>();
        var truncated = false;

        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, Query)
            {
                // Newest first, so the cap keeps the most recent errors rather than the oldest.
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);

            while (true)
            {
                // EventRecord holds native handles and must be disposed per record.
                using var record = reader.ReadEvent();
                if (record is null) break;

                if (events.Count >= MaxEvents)
                {
                    truncated = true;
                    break;
                }

                if (Build(record) is { } built)
                    events.Add(built);
            }
        }
        catch (EventLogNotFoundException ex)
        {
            return HardwareErrorSnapshot.Failed($"The System event log could not be found: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return HardwareErrorSnapshot.Failed(
                "Access denied reading the System event log. Try running WinTester as administrator.");
        }
        catch (EventLogException ex)
        {
            return HardwareErrorSnapshot.Failed($"The System event log could not be read: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Nothing here may escape into the window's async handler.
            return HardwareErrorSnapshot.Failed($"Hardware error scan failed: {ex.Message}");
        }

        return new HardwareErrorSnapshot
        {
            Events = events,
            Truncated = truncated
        };
    }

    [SupportedOSPlatform("windows")]
    private static HardwareErrorEvent? Build(EventRecord record)
    {
        // A real log entry always has a creation time; one without is unusable for an ordered history.
        if (record.TimeCreated is not { } time) return null;

        var description = ReadDescription(record);
        var eventData = WheaEventXmlParser.Parse(ReadXml(record));
        var details = WheaMessageParser.Parse(description);

        // The generic "the details are in the data section" events name nothing in their description
        // (WheaMessageParser.Parse leaves Severity at Other for them), so the severity carried in their own
        // binary error record is the only classification available.
        if (details.Severity == HardwareErrorSeverity.Other && eventData.RecordSeverity is { } recordSeverity)
            details = details with { Severity = recordSeverity };

        return new HardwareErrorEvent
        {
            Timestamp = time,
            EventId = record.Id,
            Level = LevelName(record),
            Description = description,
            Details = details,
            EventData = eventData
        };
    }

    // FormatDescription returns null when the provider's message file can't be found and can throw when
    // the metadata is damaged. Either way the entry is still worth listing by its event ID.
    [SupportedOSPlatform("windows")]
    private static string ReadDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? string.Empty;
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
    }

    // The event's own XML carries the data section that generic descriptions point to. ToXml throws when
    // the record can't be rendered; the event is still listed without its data.
    [SupportedOSPlatform("windows")]
    private static string ReadXml(EventRecord record)
    {
        try
        {
            return record.ToXml() ?? string.Empty;
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
    }

    // The numeric level is read first because LevelDisplayName is localised on non-English Windows, and the
    // window colours by level name. The display name only covers the levels with no canonical name here,
    // and is null when the provider's metadata is missing, so the last fallback is "Unknown".
    [SupportedOSPlatform("windows")]
    private static string LevelName(EventRecord record)
    {
        switch (record.Level)
        {
            case 1: return "Critical";
            case 2: return "Error";
            case 3: return "Warning";
            case 4: return "Information";
        }

        return record.LevelDisplayName is { Length: > 0 } display ? display : "Unknown";
    }
}
