using System.Windows.Media;
using WinTester.Core.HardwareErrors;

namespace WinTester.App.ViewModels;

// One row in the Hardware Errors list: a group of identical WHEA events shown as a single entry. Built once
// per scan and never mutated, so no change notification is needed. The detail popup uses the same instance
// as its data context.
public sealed class HardwareErrorRowViewModel
{
    // "Processor Core — Cache Hierarchy Error", falling back to the headline sentence and then the bare
    // event ID when the message named nothing.
    public required string Title { get; init; }

    // The subheading is shown as three coloured runs: "Event 18" (green), the level ("Error", coloured by
    // seriousness) and the rest, which stays grey.
    public required string EventText { get; init; }
    public required string LevelText { get; init; }

    // Starts with its own " · " separator: " · 2026-09-24 10:14:03", or " · last 2026-09-24 10:14:03" when
    // the error repeated (only the most recent time; the popup lists them all), plus the processor APIC ID
    // when the message carried one.
    public required string MetaRestText { get; init; }

    public Brush EventBrush { get; init; } = HardwareErrorBrushes.EventId;
    public Brush LevelBrush { get; init; } = HardwareErrorBrushes.Other;

    // The event's own description, exactly as Windows formats it.
    public string DescriptionText { get; init; } = string.Empty;

    // "PCI\VEN_8086&DEV_A340", or empty. Shown on its own line because it is what the lookup searches for.
    public string DeviceText { get; init; } = string.Empty;

    // "occurred 37 times" on repeated errors; empty for a single occurrence.
    public string CountText { get; init; } = string.Empty;

    public Brush AccentBrush { get; init; } = Brushes.Gray;

    public required string LookupUrl { get; init; }

    // The whole group behind this row, for the detail popup.
    public required HardwareErrorGroup Group { get; init; }

    public bool HasDescription => DescriptionText.Length > 0;
    public bool HasDevice => DeviceText.Length > 0;
    public bool HasCount => CountText.Length > 0;

    public HardwareErrorSeverity Severity { get; init; }
}
