using System;
using System.Collections.Generic;

namespace WinTester.Core.HardwareErrors;

// How serious a WHEA record is. Read from the wording of the event's own description ("A fatal hardware
// error has occurred" / "A corrected hardware error has occurred"); when the description names nothing (the
// generic "the details are in the data section" events), the severity stored in the event's own binary
// error record is used instead. Corrected errors were fixed by the hardware itself and are a warning sign
// at volume; fatal ones are the machine-check class that crash the system; recoverable ones were
// uncorrected but the system survived; informational records carry no error at all, only context.
public enum HardwareErrorSeverity
{
    Fatal,
    Corrected,
    Recoverable,
    Informational,
    Other
}

// Fields pulled out of a WHEA-Logger event description. Everything except Headline and Severity is null
// when the description doesn't carry it, which is normal: processor and memory events have no PCI device,
// and PCIe events have no APIC ID.
public sealed record WheaMessageDetails
{
    // First non-empty line of the description, e.g. "A corrected hardware error has occurred."
    public string Headline { get; init; } = string.Empty;

    public string? Component { get; init; }
    public string? ErrorSource { get; init; }
    public string? ErrorType { get; init; }

    // Normalised "PCI\VEN_8086&DEV_A340" (vendor and device only, no subsystem or revision).
    public string? PciDeviceId { get; init; }

    public string? ProcessorApicId { get; init; }

    public HardwareErrorSeverity Severity { get; init; } = HardwareErrorSeverity.Other;
}

// One WHEA-Logger entry from the System event log.
public sealed record HardwareErrorEvent
{
    // Local time.
    public required DateTime Timestamp { get; init; }

    public required int EventId { get; init; }

    // "Error", "Warning", "Information", "Critical" or "Unknown".
    public required string Level { get; init; }

    // Empty when Windows has no message template for the event, which happens on a machine missing the
    // provider's message file.
    public required string Description { get; init; }

    public required WheaMessageDetails Details { get; init; }

    // Everything else the event carries: its System header, its data section and the full XML. Empty when
    // Windows would not render the event's XML.
    public WheaEventData EventData { get; init; } = WheaEventData.Empty;
}

// One labelled value from an event: a System header entry, a named field of its data section, or a line
// decoded from its binary record.
public sealed record HardwareErrorField(string Name, string Value);

// The parts of a WHEA-Logger event beyond its formatted description. Many WHEA events have only a generic
// description ("an informational record describing the condition is contained in the data section") and
// keep the real content here.
public sealed record WheaEventData
{
    public static readonly WheaEventData Empty = new();

    // Provider, event ID, version, level, task, opcode, keywords, record ID, process/thread, channel,
    // computer and user, as the XML header gives them.
    public IReadOnlyList<HardwareErrorField> SystemFields { get; init; } = Array.Empty<HardwareErrorField>();

    // Every named field of the event's data section, plus any raw binary blob, in the order they appear.
    public IReadOnlyList<HardwareErrorField> DataFields { get; init; } = Array.Empty<HardwareErrorField>();

    // Header, section list, and the body of any section whose layout is known, decoded from a binary WHEA
    // error record found in the data section; empty when the data holds no such record.
    public IReadOnlyList<HardwareErrorField> ErrorRecordFields { get; init; } = Array.Empty<HardwareErrorField>();

    // The severity stored in that binary record's own header; null when the data held no such record. Used
    // as a fallback when the event's description does not classify the error (see HardwareErrorSeverity).
    public HardwareErrorSeverity? RecordSeverity { get; init; }

    // The event's XML, indented; empty when it could not be read.
    public string Xml { get; init; } = string.Empty;
}

// Result of one scan. A failed scan returns an empty list plus ErrorMessage rather than throwing, so the
// window always has something to render.
public sealed record HardwareErrorSnapshot
{
    // Newest first.
    public required IReadOnlyList<HardwareErrorEvent> Events { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // True when the log held more entries than the scan's cap and the oldest were left unread.
    public bool Truncated { get; init; }

    public string? ErrorMessage { get; init; }

    public bool HasErrors => Events.Count > 0;

    public static HardwareErrorSnapshot Failed(string message) => new()
    {
        Events = Array.Empty<HardwareErrorEvent>(),
        ErrorMessage = message
    };
}

// Repeats of the same error collapsed into one row. A failing DIMM or a marginal PCIe link can log the
// same corrected error hundreds of times, which as individual rows buries everything else.
public sealed record HardwareErrorGroup
{
    public required int Count { get; init; }
    public required DateTime First { get; init; }
    public required DateTime Last { get; init; }

    // Every occurrence's time, newest first, so Occurrences.Count == Count. The export and the detail
    // popup list all of them.
    public required IReadOnlyList<DateTime> Occurrences { get; init; }

    // The most recent occurrence; supplies the description and lookup link for the whole group.
    public required HardwareErrorEvent Latest { get; init; }

    public HardwareErrorSeverity Severity => Latest.Details.Severity;
}
