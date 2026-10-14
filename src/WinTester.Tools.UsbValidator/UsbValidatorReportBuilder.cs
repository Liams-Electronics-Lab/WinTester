using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinTester.Tools.UsbValidator;

public enum PortResultStatus
{
    NeverConnected,
    Passed
}

/// <summary>End-of-run summary for one physical port.</summary>
public sealed class PortResult
{
    public required string PortKey { get; init; }
    public required int PortNumber { get; init; }
    public required string HubPath { get; init; }
    public required PortResultStatus Status { get; init; }
    public ushort? LastVendorId { get; init; }
    public ushort? LastProductId { get; init; }
}

/// <summary>
/// Builds the end-of-run report. Self-contained single HTML file (no external assets, no JS) 
/// deliberately simpler than WinTester's Chart.js reports since there's no telemetry-over-time data here,
/// just a per-port pass/fail table. No brand/report header threaded through (unlike Keyboard's report):
/// this tool has no white-label requirement of its own, same posture as Hardware Errors/Devices exports.
/// Ported unchanged (aside from the class/namespace rename) from the standalone USB Validator prototype's
/// ResultsExporter.
/// </summary>
public static class UsbValidatorReportBuilder
{
    public static string BuildHtml(IReadOnlyList<PortResult> results, DateTime runStarted, DateTime runEnded)
    {
        int passCount = results.Count(r => r.Status == PortResultStatus.Passed);
        int totalCount = results.Count;
        bool allPassed = passCount == totalCount && totalCount > 0;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>USB Validator Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:2em;color:#222;}");
        sb.AppendLine("h1{margin-bottom:0.1em;}");
        sb.AppendLine(".subtitle{color:#666;margin-top:0;}");
        sb.AppendLine(".verdict{display:inline-block;padding:0.6em 1.4em;font-size:1.4em;font-weight:bold;");
        sb.AppendLine("border-radius:6px;color:#fff;margin:1em 0;}");
        sb.AppendLine(".verdict.pass{background:#2e9e44;}");
        sb.AppendLine(".verdict.fail{background:#c0392b;}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:1em;}");
        sb.AppendLine("th,td{border:1px solid #ddd;padding:0.5em 0.8em;text-align:left;font-size:0.95em;}");
        sb.AppendLine("th{background:#f4f4f4;}");
        sb.AppendLine("tr.passed{background:#eafaf0;}");
        sb.AppendLine("tr.failed{background:#fdecea;}");
        sb.AppendLine(".status-pass{color:#2e9e44;font-weight:bold;}");
        sb.AppendLine(".status-fail{color:#c0392b;font-weight:bold;}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>USB Validator Report</h1>");
        sb.AppendLine($"<p class=\"subtitle\">Run started {runStarted:yyyy-MM-dd HH:mm:ss} &middot; "
            + $"ended {runEnded:yyyy-MM-dd HH:mm:ss} &middot; duration "
            + $"{(runEnded - runStarted):mm\\:ss}</p>");

        sb.AppendLine(allPassed
            ? "<div class=\"verdict pass\">PASS</div>"
            : "<div class=\"verdict fail\">FAIL</div>");

        sb.AppendLine($"<p>{passCount} of {totalCount} physical port(s) saw a device connect during this run.</p>");

        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Port</th><th>Hub</th><th>Status</th><th>Last device seen (VID/PID)</th></tr>");

        foreach (var r in results.OrderBy(r => r.HubPath).ThenBy(r => r.PortNumber))
        {
            string rowClass = r.Status == PortResultStatus.Passed ? "passed" : "failed";
            string statusText = r.Status == PortResultStatus.Passed
                ? "<span class=\"status-pass\">PASS</span>"
                : "<span class=\"status-fail\">NOT TESTED</span>";
            string device = r.LastVendorId.HasValue
                ? $"VID_{r.LastVendorId:X4} PID_{r.LastProductId:X4}"
                : "&mdash;";

            sb.AppendLine($"<tr class=\"{rowClass}\"><td>{r.PortNumber}</td>"
                + $"<td>{System.Net.WebUtility.HtmlEncode(ShortHubName(r.HubPath))}</td>"
                + $"<td>{statusText}</td><td>{device}</td></tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string ShortHubName(string hubPath)
    {
        int hashIndex = hubPath.IndexOf('#', hubPath.IndexOf('#') + 1);
        return hashIndex > 0 ? hubPath[..hashIndex] : hubPath;
    }
}
