using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using WinTester.Branding;
using WinTester.Reporting;

namespace WinTester.Tools.KeyboardTester;

// Renders one Keyboard Tester session to a single self-contained, offline HTML file — deliberately shaped
// like BatteryDrainReportBuilder/BatteryChargeReportBuilder (plain summary blocks + tables, reusing
// BatteryReportSharedStyle's brand header/footer and print CSS) rather than HtmlReportGenerator's
// Chart.js-driven layout, per the operator's explicit ask: this report needs no graphs, since nothing in a
// keyboard-test session is a value sampled over time — every field here is either a session total or a
// discrete event log. See §7.8/Decision Log (session 62) for that reasoning.
public static class KeyboardTesterReportBuilder
{
    public static string Render(KeyboardTesterSession session, BrandProfile? brand = null)
    {
        var summary = session.BuildSummary();
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"UTF-8\">\n");
        sb.Append("<title>Keyboard Tester Report</title>\n");
        sb.Append("<style>\n").Append(Css).Append('\n').Append(BatteryReportSharedStyle.BrandScreenCss)
          .Append('\n').Append(BatteryReportSharedStyle.PrintCss).Append("\n</style>\n");
        sb.Append("</head>\n<body>\n");

        BatteryReportSharedStyle.AppendBrandHeader(sb, brand);
        AppendHeader(sb, summary);
        AppendSummaryBlocks(sb, summary);
        AppendKeyTables(sb, session);
        AppendEventLog(sb, session);
        BatteryReportSharedStyle.AppendBrandFooter(sb, brand, "WinTester, Keyboard Tester report.");

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, KeyboardTesterSummary summary)
    {
        sb.Append("<header class=\"report-header\">\n<h1>Keyboard Tester</h1>\n");
        sb.Append($"<p class=\"subtitle\">Report generated {Enc(summary.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))}"
            + $" &middot; Layout: {Enc(KeyboardLayouts.DisplayName(summary.Layout))}</p>\n");
        sb.Append("</header>\n");
    }

    private static void AppendSummaryBlocks(StringBuilder sb, KeyboardTesterSummary summary)
    {
        sb.Append("<div class=\"summary-blocks\">\n");
        AppendBlock(sb, "Total Keystrokes", summary.TotalKeystrokes.ToString(CultureInfo.InvariantCulture), "block-neutral");
        var percentClass = summary.PercentTested switch
        {
            >= 100 => "block-green",
            >= 50 => "block-amber",
            _ => "block-neutral"
        };
        AppendBlock(sb, "Keys Tested",
            $"{summary.KeysTestedCount} / {summary.KeysTotalCount}",
            percentClass,
            $"{summary.PercentTested.ToString("0.0", CultureInfo.InvariantCulture)}%");
        AppendBlock(sb, "Peak NKRO", $"{summary.PeakNkro} simultaneous", "block-neutral");
        sb.Append("</div>\n");
    }

    private static void AppendBlock(StringBuilder sb, string label, string value, string cssClass, string? sub = null)
    {
        sb.Append($"<div class=\"block {cssClass}\">\n<div class=\"block-label\">{Enc(label)}</div>\n");
        sb.Append($"<div class=\"block-value\">{Enc(value)}</div>\n");
        if (sub is not null) sb.Append($"<div class=\"block-sub\">{Enc(sub)}</div>\n");
        sb.Append("</div>\n");
    }

    // Tested/untested split, ordered to match the currently-displayed layout's own key order (top-left to
    // bottom-right by row then column), same as the attached reference example's own untested-key list
    // ordering — a technician scanning the report can follow along against the physical board.
    private static void AppendKeyTables(StringBuilder sb, KeyboardTesterSession session)
    {
        var ordered = session.Keys.OrderBy(k => k.Row).ThenBy(k => k.Col).ToList();
        var tested = ordered.Where(k => session.GetState(k.Id) == KeyTestState.Verified).ToList();
        var untested = ordered.Where(k => session.GetState(k.Id) != KeyTestState.Verified).ToList();

        sb.Append("<section class=\"data-table\">\n<h2>Untested Keys</h2>\n");
        sb.Append($"<p class=\"section-sub\">{untested.Count} of {ordered.Count} keys in this layout not yet verified.</p>\n");
        AppendKeyList(sb, untested, isTested: false);
        sb.Append("</section>\n");

        sb.Append("<section class=\"data-table\">\n<h2>Tested Keys</h2>\n");
        sb.Append($"<p class=\"section-sub\">{tested.Count} of {ordered.Count} keys in this layout verified.</p>\n");
        AppendKeyList(sb, tested, isTested: true);
        sb.Append("</section>\n");
    }

    private static void AppendKeyList(StringBuilder sb, System.Collections.Generic.List<KeyboardKeyDefinition> keys, bool isTested)
    {
        if (keys.Count == 0)
        {
            sb.Append("<p class=\"na-block\">None.</p>\n");
            return;
        }

        sb.Append("<table>\n<thead><tr><th>Key</th><th>VK</th><th>Scan</th></tr></thead>\n<tbody>\n");
        foreach (var k in keys)
        {
            sb.Append("<tr>");
            sb.Append($"<td>{(isTested ? "\u2713 " : "")}{Enc(k.FriendlyName)}</td>");
            sb.Append($"<td>0x{k.VirtualKey:X2}</td>");
            sb.Append($"<td>{(k.IsExtendedKey ? "E0 (extended)" : "\u2014")}</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
    }

    // Full event stream as an appendix table rather than the reference's plain log lines — same fields
    // (timestamp, direction, key, VK hex/decimal, scan code, hold duration on Up) as the attached example.
    private static void AppendEventLog(StringBuilder sb, KeyboardTesterSession session)
    {
        sb.Append("<section class=\"data-table\">\n<h2>Event Log</h2>\n");
        if (session.Events.Count == 0)
        {
            sb.Append("<p class=\"na-block\">No key events recorded this session.</p>\n");
            sb.Append("</section>\n");
            return;
        }

        sb.Append("<p class=\"section-sub\">Newest first.</p>\n");
        sb.Append("<table>\n<thead><tr><th>Time</th><th>Event</th><th>Key</th><th>VK</th><th>Scan</th><th>Held</th></tr></thead>\n<tbody>\n");
        foreach (var e in session.Events.AsEnumerable().Reverse())
        {
            var direction = e.Direction switch
            {
                KeyEventDirection.Down => "DOWN",
                KeyEventDirection.Repeat => "RPT",
                KeyEventDirection.Up => "UP",
                _ => e.Direction.ToString()
            };
            sb.Append("<tr>");
            sb.Append($"<td>{Enc(e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))}</td>");
            sb.Append($"<td>{Enc(direction)}</td>");
            sb.Append($"<td>{Enc(e.Label)}</td>");
            sb.Append($"<td>0x{e.VirtualKey:X2} ({e.VirtualKey})</td>");
            sb.Append($"<td>0x{e.ScanCode:X4}</td>");
            sb.Append(e.HeldMilliseconds is { } ms
                ? $"<td>{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms</td>"
                : "<td class=\"na\">&mdash;</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
        sb.Append("</section>\n");
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private const string Css = @"
* { box-sizing:border-box; }
body { background:#F4F5F7; color:#1C1C1F; font-family:Segoe UI,Arial,sans-serif; margin:0; padding:24px; }
.report-header { background:#1C1C1F; color:#FFFFFF; padding:24px; border-radius:8px; margin-bottom:20px; }
.report-header h1 { margin:0 0 4px 0; font-size:24px; }
.subtitle { color:#C7C9CF; margin:0; }
.summary-blocks { display:flex; flex-wrap:wrap; gap:12px; margin-bottom:20px; }
.block { flex:1 1 150px; border-radius:8px; padding:14px; background:#FFFFFF; border:1px solid #E1E3E8; }
.block-label { font-size:11px; color:#6B6B72; text-transform:uppercase; letter-spacing:.03em; margin-bottom:6px; }
.block-value { font-size:20px; font-weight:700; color:#1C1C1F; }
.block-sub { font-size:12px; color:#6B6B72; margin-top:2px; font-weight:400; }
.block-green .block-value { color:#1E7A46; }
.block-amber .block-value { color:#9C6B10; }
.block-neutral .block-value { color:#1C1C1F; }
.data-table { background:#FFFFFF; border:1px solid #E1E3E8; border-radius:8px; padding:20px; margin-bottom:20px; }
.data-table h2 { margin-top:0; font-size:17px; }
.section-sub { color:#6B6B72; font-size:12px; margin:-6px 0 12px 0; }
table { border-collapse:collapse; width:100%; font-size:12px; margin-top:10px; }
th, td { border:1px solid #E1E3E8; padding:5px 9px; text-align:left; }
th { background:#F4F5F7; font-size:11px; text-transform:uppercase; opacity:.7; }
.na { color:#9A9AA2; font-style:italic; }
.na-block { color:#6B6B72; font-size:12px; font-style:italic; }
.report-footer { color:#8A8A93; font-size:11px; margin-top:20px; }
";
}
