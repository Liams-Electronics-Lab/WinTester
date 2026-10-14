using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace WinTester.Tools.BatteryDrain;

// One row of powercfg /batteryreport's "Battery usage" table; an active-vs-suspended span with energy drained if on battery. AC rows show "-"/"-" in the source; surfaced here as null, matching the codebase's not-found-vs-zero convention.
public sealed record BatteryDrainEvent(
    DateTime? StartTime, string State, string? DurationText, double? EnergyDrainedPercent, double? EnergyDrainedMwh);

// One row of powercfg /batteryreport's "Recent usage" table [a point-in-time percent plus AC/DC source, the same shape iOS's Battery Level chart draws from. Source is "AC", "DC", or null for rows like "Battery changed" that carry neither. CapacityRemainingMwh is the row's paired mWh reading (same column pairing as BatteryDrainEvent's Percent/Mwh)] null for rows like "Battery changed" that carry neither, matching Percent's own null convention for that case.
public sealed record BatteryUsagePoint(DateTime? Time, double? Percent, string State, string? Source, double? CapacityRemainingMwh = null);

public sealed record BatteryHistoryResult(
    bool Success,
    IReadOnlyList<BatteryDrainEvent> DrainEvents,
    IReadOnlyList<BatteryUsagePoint> UsagePoints,
    string Detail);

// Shells out to powercfg /batteryreport [the same kernel power-event log powercfg's own HTML report
// reads] and parses its "Battery usage" (drain spans) and "Recent usage" (percent-over-time + AC/DC)
// tables, driving our own shaded charge-period graph instead of reproducing powercfg's weekly "Usage
// history" (see BatteryDrainReportBuilder). Reuses the OS tool rather than re-deriving from SRUM/ETW
// directly, matching the process-isolation posture used for stress engines. Best-effort throughout: a
// missing section, bad row, or launch failure degrades to Success=false with an explanatory Detail rather
// than throwing. Parser matches English-locale heading/state text only for now.
public static class PowerCfgBatteryHistoryReader
{
    public static BatteryHistoryResult Read(int durationDays = 3)
    {
        if (!OperatingSystem.IsWindows())
            return new BatteryHistoryResult(false, Array.Empty<BatteryDrainEvent>(), Array.Empty<BatteryUsagePoint>(),
                "Not running on Windows.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"WinTester_powercfg_{Guid.NewGuid():N}.html");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"/batteryreport /output \"{tempPath}\" /duration {durationDays}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new BatteryHistoryResult(false, Array.Empty<BatteryDrainEvent>(), Array.Empty<BatteryUsagePoint>(),
                    "Could not launch powercfg.exe.");

            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* Best-effort kill; ignore failure. */ }
                return new BatteryHistoryResult(false, Array.Empty<BatteryDrainEvent>(), Array.Empty<BatteryUsagePoint>(),
                    "powercfg /batteryreport timed out after 15s.");
            }

            if (!File.Exists(tempPath))
                return new BatteryHistoryResult(false, Array.Empty<BatteryDrainEvent>(), Array.Empty<BatteryUsagePoint>(),
                    $"powercfg /batteryreport did not produce a report file (exit code {proc.ExitCode}).");

            var html = File.ReadAllText(tempPath);
            var drainEvents = ParseDrainEvents(html);
            var usagePoints = ParseUsagePoints(html);

            if (drainEvents.Count == 0 && usagePoints.Count == 0)
                return new BatteryHistoryResult(false, drainEvents, usagePoints,
                    "powercfg /batteryreport ran, but no recognizable usage rows were found in its output " +
                    "(possibly a non-English OS display language, or a machine with no battery history yet).");

            return new BatteryHistoryResult(true, drainEvents, usagePoints,
                $"Parsed from powercfg /batteryreport's own {durationDays}-day window.");
        }
        catch (Exception ex)
        {
            return new BatteryHistoryResult(false, Array.Empty<BatteryDrainEvent>(), Array.Empty<BatteryUsagePoint>(),
                $"powercfg /batteryreport failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* Best-effort cleanup; ignore failure. */ }
        }
    }

    private static readonly Regex RowRegex = new(@"<tr\s+class=""(?<cls>[^""]*)"">(?<body>.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DateSpan = new(@"<span class=""date"">([^<]*)</span>", RegexOptions.Compiled);
    private static readonly Regex TimeSpan_ = new(@"<span class=""time"">([^<]*)</span>", RegexOptions.Compiled);
    private static readonly Regex StateCell = new(@"class=""state"">(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HmsCell = new(@"class=""hms"">([^<]*)</td>", RegexOptions.Compiled);
    private static readonly Regex PercentCell = new(@"class=""percent"">([^<%]*)", RegexOptions.Compiled);
    private static readonly Regex MwCell = new(@"class=""mw"">([^<]*)</td>", RegexOptions.Compiled);
    private static readonly Regex AcdcCell = new(@"class=""acdc"">(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);

    // Isolates the HTML between one <h2> heading and the next, so the row regex only scans the target section.
    private static string ExtractSection(string html, string headingText)
    {
        var start = html.IndexOf($">{headingText}<", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            // powercfg pads headings with whitespace/newlines; retry with a looser match on the <h2> content itself.
            var m = Regex.Match(html, $@"<h2>\s*{Regex.Escape(headingText)}\s*</h2>", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            start = m.Index + m.Length;
        }
        else
        {
            start = html.IndexOf('<', start + 1); // Skip past the heading text to the next tag.
        }

        var nextHeading = html.IndexOf("<h2", start, StringComparison.OrdinalIgnoreCase);
        return nextHeading > start ? html[start..nextHeading] : html[start..];
    }

    private static IReadOnlyList<BatteryDrainEvent> ParseDrainEvents(string html)
    {
        var section = ExtractSection(html, "Battery usage");
        var events = new List<BatteryDrainEvent>();
        if (section.Length == 0) return events;

        string? lastDate = null;
        foreach (Match row in RowRegex.Matches(section))
        {
            var cls = row.Groups["cls"].Value;
            if (cls.Contains("noncontigbreak")) continue; // Filler/spacer row, not real data.

            var body = row.Groups["body"].Value;
            var (start, lastDateOut) = ParseDateTime(body, lastDate);
            lastDate = lastDateOut;

            var state = CleanText(StateCell.Match(body) is { Success: true } sm ? sm.Groups[1].Value : "");
            var duration = HmsCell.Match(body) is { Success: true } hm ? hm.Groups[1].Value.Trim() : null;

            double? pct = null, mwh = null;
            var pm = PercentCell.Match(body);
            var mm = MwCell.Match(body);
            if (pm.Success && mm.Success)
            {
                pct = ParseDouble(pm.Groups[1].Value);
                mwh = ParseDouble(mm.Groups[1].Value);
            }

            if (state.Length == 0 && duration is null) continue; // Not a real data row.

            events.Add(new BatteryDrainEvent(start, state, duration, pct, mwh));
        }

        return events;
    }

    private static IReadOnlyList<BatteryUsagePoint> ParseUsagePoints(string html)
    {
        var section = ExtractSection(html, "Recent usage");
        var points = new List<BatteryUsagePoint>();
        if (section.Length == 0) return points;

        string? lastDate = null;
        foreach (Match row in RowRegex.Matches(section))
        {
            var cls = row.Groups["cls"].Value;
            if (cls.Contains("noncontigbreak")) continue;

            var body = row.Groups["body"].Value;
            var (time, lastDateOut) = ParseDateTime(body, lastDate);
            lastDate = lastDateOut;

            var state = CleanText(StateCell.Match(body) is { Success: true } sm ? sm.Groups[1].Value : "");
            var acdcRaw = CleanText(AcdcCell.Match(body) is { Success: true } am ? am.Groups[1].Value : "");
            // powercfg's own "Recent usage" table spells the on-battery value "Battery", not "DC", kept as
            // "DC" on this side of the parser to match BatteryUsagePoint's documented Source values (and the
            // existing AC-shading/tooltip logic downstream that already keys off "AC"/"DC"); display code
            // maps "DC" back to powercfg's own "Battery" wording. The old switch compared against "DC"
            // directly, so every on-battery row silently resolved to null Source; fixed here.
            string? source = acdcRaw switch
            {
                "AC" => "AC",
                "Battery" => "DC",
                _ => null
            };

            double? pct = null, mwh = null;
            var pm = PercentCell.Match(body);
            var mm = MwCell.Match(body);
            if (pm.Success && mm.Success)
            {
                pct = ParseDouble(pm.Groups[1].Value);
                mwh = ParseDouble(mm.Groups[1].Value);
            }

            if (state.Length == 0 && time is null) continue;

            points.Add(new BatteryUsagePoint(time, pct, state, source, mwh));
        }

        return points;
    }

    // powercfg leaves the date span blank after the first row of a given day; carryForwardDate threads the last non-blank date through row-by-row so every point still resolves to a real DateTime.
    private static (DateTime? value, string? lastDate) ParseDateTime(string rowBody, string? carryForwardDate)
    {
        var dateM = DateSpan.Match(rowBody);
        var timeM = TimeSpan_.Match(rowBody);
        if (!timeM.Success) return (null, carryForwardDate);

        var datePart = dateM.Success ? dateM.Groups[1].Value.Trim() : "";
        var effectiveDate = datePart.Length > 0 ? datePart : carryForwardDate;
        var timePart = timeM.Groups[1].Value.Trim();

        if (effectiveDate is null) return (null, carryForwardDate);

        var combined = $"{effectiveDate} {timePart}";
        var ok = DateTime.TryParseExact(combined, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt);
        return ok ? (dt, effectiveDate) : ((DateTime?)null, effectiveDate);
    }

    // powercfg's "mw" cells carry a trailing unit suffix ("88,966 mWh", not just "88,966") that MwCell's
    // own regex doesn't strip (it only stops at "<"), and a straight TryParse on "88966 mWh" fails
    // outright; silently nulling every real Capacity Remaining/Energy Drained value in the generated
    // report. Extracts the leading numeric run instead of requiring the whole (comma-stripped) string to
    // parse cleanly.
    private static readonly Regex LeadingNumber = new(@"[-+]?[0-9,]*\.?[0-9]+", RegexOptions.Compiled);

    private static double? ParseDouble(string s)
    {
        var m = LeadingNumber.Match(s);
        if (!m.Success) return null;
        return double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    private static string CleanText(string s) => WebUtility.HtmlDecode(s).Trim();
}
