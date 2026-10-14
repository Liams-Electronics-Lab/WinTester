using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinTester.Core.HardwareErrors;

// Plain-text rendering of hardware errors: the one-line title and summary the window shows, and the
// exportable report. Pure logic with no Windows dependency, so the window, the detail popup and the export
// all describe an error the same way.
public static class HardwareErrorText
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private const string Bar = "========================================";
    private const string Rule = "----------------------------------------";

    // "Processor Core — Cache Hierarchy Error", falling back to the headline sentence and then the bare
    // event ID when the message named nothing. The separator is a parameter so the export can stay ASCII.
    public static string Title(HardwareErrorEvent error, string separator = " — ")
    {
        var details = error.Details;
        var named = new[] { details.Component, details.ErrorType }.Where(s => !string.IsNullOrEmpty(s)).ToList();

        if (named.Count > 0) return string.Join(separator, named);
        if (details.Headline.Length > 0) return details.Headline;
        return $"WHEA event {error.EventId}";
    }

    public static string SeverityLabel(HardwareErrorSeverity severity) => severity switch
    {
        HardwareErrorSeverity.Fatal => "Fatal",
        HardwareErrorSeverity.Corrected => "Corrected",
        HardwareErrorSeverity.Recoverable => "Recoverable",
        HardwareErrorSeverity.Informational => "Informational",
        _ => "Not classified"
    };

    // "occurred 37 times": the marker on an error that Windows logged more than once.
    public static string OccurredText(int count) => $"occurred {count} time{(count == 1 ? "" : "s")}";

    // "37 hardware error events in 4 groups: 1 fatal, 36 corrected. 2 errors repeated; the most frequent
    // occurred 36 times." Recoverable and informational counts are appended only when there are any, so an
    // ordinary machine's summary doesn't grow a permanent "0 recoverable" clause. The last sentence appears
    // only when something repeated.
    public static string Summary(HardwareErrorSnapshot snapshot, IReadOnlyList<HardwareErrorGroup> groups)
    {
        var count = snapshot.Events.Count;

        var counts = new List<string>
        {
            $"{CountOf(snapshot, HardwareErrorSeverity.Fatal)} fatal",
            $"{CountOf(snapshot, HardwareErrorSeverity.Corrected)} corrected"
        };

        var recoverable = CountOf(snapshot, HardwareErrorSeverity.Recoverable);
        if (recoverable > 0) counts.Add($"{recoverable} recoverable");

        var informational = CountOf(snapshot, HardwareErrorSeverity.Informational);
        if (informational > 0) counts.Add($"{informational} informational");

        var text = $"{count} hardware error event{(count == 1 ? "" : "s")} " +
                   $"in {groups.Count} group{(groups.Count == 1 ? "" : "s")}: {string.Join(", ", counts)}.";

        var repeated = groups.Where(g => g.Count > 1).ToList();
        if (repeated.Count == 1)
            text += $" 1 error repeated: it {OccurredText(repeated[0].Count)}.";
        else if (repeated.Count > 1)
            text += $" {repeated.Count} errors repeated; the most frequent {OccurredText(repeated.Max(g => g.Count))}.";

        return text;
    }

    private static int CountOf(HardwareErrorSnapshot snapshot, HardwareErrorSeverity severity) =>
        snapshot.Events.Count(e => e.Details.Severity == severity);

    // Everything known about one event as sectioned plain text: Windows' description, the event's data
    // section, the decoded error record when the data holds one, the System header and the full XML. Shown in
    // the detail popup and written into each export entry, so the two cannot drift.
    public static string BuildDetailText(HardwareErrorEvent error)
    {
        var sb = new StringBuilder();
        var data = error.EventData;

        sb.AppendLine("Description:");
        if (string.IsNullOrWhiteSpace(error.Description))
        {
            sb.AppendLine("  (Windows did not provide a description for this event.)");
        }
        else
        {
            foreach (var line in error.Description.Trim().Split('\n'))
                sb.AppendLine("  " + line.TrimEnd());
        }

        AppendFieldSection(sb, "Event data", data.DataFields, "(this event carries no data section)");

        if (data.ErrorRecordFields.Count > 0)
            AppendFieldSection(sb, "Error record (decoded from the event data)", data.ErrorRecordFields, string.Empty);

        if (data.SystemFields.Count > 0)
            AppendFieldSection(sb, "System", data.SystemFields, string.Empty);

        if (data.Xml.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Raw event XML:");
            foreach (var line in data.Xml.Split('\n'))
                sb.AppendLine("  " + line.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendFieldSection(
        StringBuilder sb, string title, IReadOnlyList<HardwareErrorField> fields, string whenEmpty)
    {
        sb.AppendLine();
        sb.AppendLine(title + ":");

        if (fields.Count == 0)
        {
            sb.AppendLine("  " + whenEmpty);
            return;
        }

        // Names are padded to a common width so the values line up, capped so one long name doesn't push
        // every value off to the right.
        var width = Math.Min(30, fields.Max(f => f.Name.Length));
        foreach (var field in fields)
            sb.AppendLine($"  {field.Name.PadRight(width)} : {field.Value}");
    }

    // One entry per group, each with its counts, parsed fields, web-search link and full description. ASCII
    // only in the text this class writes, so the file reads the same in any editor; the description is
    // Windows' own text and is passed through as is.
    public static string BuildReport(
        HardwareErrorSnapshot snapshot,
        IReadOnlyList<HardwareErrorGroup> groups,
        DateTime generatedAt,
        string computerName)
    {
        var sb = new StringBuilder();

        sb.AppendLine(Bar);
        sb.AppendLine("  Hardware Errors");
        sb.AppendLine($"  Generated: {generatedAt.ToString(TimeFormat)}");
        sb.AppendLine($"  Computer : {computerName}");
        sb.AppendLine(Bar);
        sb.AppendLine();

        if (groups.Count == 0)
        {
            sb.AppendLine("No hardware errors found in the System log.");
            return sb.ToString();
        }

        sb.AppendLine(Summary(snapshot, groups));
        if (snapshot.Truncated)
            sb.AppendLine($"Only the newest {snapshot.Events.Count} events were read; older entries are not included.");
        sb.AppendLine("Identical errors are grouped. Each entry ends with every date and time it occurred.");
        sb.AppendLine();

        for (var i = 0; i < groups.Count; i++)
        {
            AppendGroup(sb, groups[i], i + 1, groups.Count);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, HardwareErrorGroup group, int number, int total)
    {
        var latest = group.Latest;
        var details = latest.Details;

        sb.AppendLine(Rule);
        sb.AppendLine($"Error {number} of {total}: {Title(latest, " - ")}");
        sb.AppendLine(Rule);

        AppendField(sb, "Event ID", latest.EventId.ToString());
        AppendField(sb, "Level", latest.Level);
        AppendField(sb, "Severity", SeverityLabel(group.Severity));

        if (group.Count > 1)
            AppendField(sb, "Occurred", $"{group.Count} times");

        AppendField(sb, "Component", details.Component);
        AppendField(sb, "Error source", details.ErrorSource);
        AppendField(sb, "Error type", details.ErrorType);
        AppendField(sb, "PCI device", details.PciDeviceId);
        AppendField(sb, "Processor APIC ID", details.ProcessorApicId);
        AppendField(sb, "Look up", HardwareErrorLookup.BuildUrl(latest));

        sb.AppendLine();
        if (group.Count > 1)
        {
            sb.AppendLine("The description and data below are from the most recent occurrence.");
            sb.AppendLine();
        }

        sb.AppendLine(BuildDetailText(latest));

        // Last in the entry, newest first, one per line.
        sb.AppendLine();
        sb.AppendLine(group.Count > 1 ? $"Occurred at ({group.Count} times):" : "Occurred at:");
        foreach (var time in group.Occurrences)
            sb.AppendLine("    " + time.ToString(TimeFormat));
    }

    // Skips fields the event didn't carry, so an entry lists only what it actually has.
    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.AppendLine($"{label,-18}: {value}");
    }
}
