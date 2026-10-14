using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using WinTester.Branding;
using WinTester.Core.Activation;
using WinTester.Reporting;
using WinTester.Tests.Power;

namespace WinTester.Tools.BatteryDrain;

// Renders one Battery Drain run to a single self-contained, offline HTML file, matching
// HtmlReportGenerator's "pure data in, HTML out" posture and reusing its vendored Chart.js copy rather
// than shipping a second one. Called on every 1s tick (isFinal: false) and once more at completion
// (isFinal: true), see BatteryDrainRunner.RunAsync. Summary blocks show final delta figures once end is
// non-null; mid-run they show live-so-far figures, so an in-progress report stays informative.
// Uses the same white-background/clean-professional look as the regular diagnostic report
// (HtmlReportGenerator) [see Css below] and gains the same "System Information" card (Windows
// Activation) via BatteryReportSharedStyle.AppendSystemInformation. The System Identity subsection of
// that card is intentionally left out here: DeviceName/ManufacturerName/SerialNumber (system-level, from
// SystemIdentityReader, see BatteryDrainRunner.ResolveIdentity) are already shown in the Battery Info
// table below, so this report only needs the identity/activation params for the Windows Activation half.
// The Battery Percent chart's y-axis is fixed 0-100; the Battery Info table's Voltage row has no chart of
// its own. Kept voltageRange as an accepted-but-currently-unused param for signature parity with
// BatteryChargeReportBuilder.Render and so a future dedicated voltage chart here doesn't need another
// runner-side wiring pass.
// A fixed, translucent "TEST IN PROGRESS" overlay is shown while the report is still live (isFinal:
// false), see BatteryReportSharedStyle.AppendInProgressOverlay.
public static class BatteryDrainReportBuilder
{
    public static string Render(BatteryDrainOptions options, BatteryDrainStartSnapshot start,
        BatteryDrainEndSnapshot? end, IReadOnlyList<BatteryDrainSample> samples, BatteryHistoryResult? history,
        bool isFinal, BrandProfile? brand = null,
        ActivationSnapshot? activation = null, string? activationImageDataUri = null,
        BatteryVoltageRangeResult? voltageRange = null)
    {
        var chartJs = HtmlReportGenerator.GetVendoredChartJsSource();
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"UTF-8\">\n");
        if (!isFinal)
            sb.Append("<meta http-equiv=\"refresh\" content=\"5\">\n"); // Live-tailed report re-polls every 5s while the run continues; only for mid-run writes, since a finished report reloading itself would silently re-collapse any opened <details> section.
        sb.Append("<title>Battery Drain Report</title>\n");
        sb.Append("<style>\n").Append(Css).Append('\n').Append(BatteryReportSharedStyle.SystemInfoCss)
          .Append('\n').Append(BatteryReportSharedStyle.BrandScreenCss)
          .Append('\n').Append(BatteryReportSharedStyle.InProgressOverlayCss)
          .Append('\n').Append(BatteryReportSharedStyle.PrintCss).Append('\n');
        ThermalBatteryView.AppendStyle(sb);
        sb.Append("</style>\n");
        sb.Append("<script>\n").Append(chartJs).Append("\n</script>\n");
        BatteryReportSharedStyle.AppendChartRuntimeScripts(sb);
        // data-view="normal" is required: the thermal stylesheet hides #wt-normal whenever data-view is anything
        // else (including absent), so a thermal script fault must never be able to leave the report blank.
        sb.Append("</head>\n<body data-view=\"normal\">\n");
        // Everything the normal report shows sits in #wt-normal so the thermal view can swap it out.
        sb.Append("<div id=\"wt-normal\">\n");
        // Shown only while this report is still live-updating: isFinal is the only gate needed since the
        // overlay is a plain visual disclaimer, not a completeness guarantee.
        if (!isFinal)
            BatteryReportSharedStyle.AppendInProgressOverlay(sb);

        BatteryReportSharedStyle.AppendBrandHeader(sb, brand);
        AppendHeader(sb, isFinal, end);
        // System-identity fields intentionally omitted (null, null, null): already shown in the Battery
        // Info table below.
        BatteryReportSharedStyle.AppendSystemInformation(sb, null, null, null, activation, activationImageDataUri);
        AppendSummaryBlocks(sb, options, start, end, samples);
        AppendBatteryInfoTable(sb, start, end, samples);
        AppendChart(sb, samples);
        AppendTemperatureChart(sb, samples);
        if (history is not null) AppendUsageHistoryTable(sb, history);
        if (history is not null) AppendDrainEventsTable(sb, history);
        AppendDataTable(sb, samples);

        BatteryReportSharedStyle.AppendBrandFooter(sb, brand, "WinTester, Battery Drain report.");
        sb.Append("</div>\n"); // end #wt-normal
        // 80 mm thermal receipt version (chart data as JSON; text is read from the report above). The view
        // hash survives this report's 5 s live refresh. See WINTESTER_THERMAL_REPORT_TRACKER.md Phase 6.
        ThermalBatteryView.AppendScript(sb, BuildThermalData(samples));
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, bool isFinal, BatteryDrainEndSnapshot? end)
    {
        var (verdictClass, verdictText) = DetermineVerdict(isFinal, end);

        sb.Append("<header class=\"report-header\">\n<h1>Battery Drain</h1>\n");
        sb.Append($"<div class=\"verdict {verdictClass}\">{verdictText}</div>\n");
        if (isFinal && end is not null)
            sb.Append($"<p class=\"subtitle\">Cycle complete: {Enc(end.StopReason)}</p>\n");
        else
            sb.Append("<p class=\"subtitle\">Run in progress; this file refreshes automatically while the test runs.</p>\n");
        sb.Append("</header>\n");
    }

    // PASS only when the run reached its own configured stop percentage (e.g. "Reached the configured stop
    // percentage (95%)."); any other stop reason [operator Stop, external AC connected, the safety-floor
    // catch, the max-length cap, or a crash] means the test ended before the stop condition it was
    // actually configured for was met. PENDING covers every mid-run write, where there's no verdict yet.
    private static (string CssClass, string Text) DetermineVerdict(bool isFinal, BatteryDrainEndSnapshot? end)
    {
        if (!isFinal || end is null) return ("verdict-pending", "PENDING");
        var reachedTarget = end.StopReason.Contains("configured stop percentage", StringComparison.OrdinalIgnoreCase);
        return reachedTarget ? ("verdict-pass", "PASS") : ("verdict-fail", "FAIL");
    }

    private static void AppendSummaryBlocks(StringBuilder sb, BatteryDrainOptions options,
        BatteryDrainStartSnapshot start, BatteryDrainEndSnapshot? end, IReadOnlyList<BatteryDrainSample> samples)
    {
        var last = samples.Count > 0 ? samples[^1] : null;
        var elapsed = TimeSpan.FromSeconds(last?.ElapsedSeconds ?? 0);
        var currentPercent = end?.EndPercent ?? last?.BatteryPercent ?? start.StartPercent;
        var drained = start.StartPercent is { } sp && currentPercent is { } cp ? sp - cp : (double?)null;

        // "Wh Consumed" is computed directly from mWh capacity readings (start minus current/end remaining), not derived from percent. Uses the same live-vs-final fallback chain as currentPercent above.
        var currentRemainingMwh = end?.RemainingCapacityMwh ?? last?.RemainingCapacityMwh ?? start.RemainingCapacityMwh;
        double? consumedWh = start.RemainingCapacityMwh is { } startMwh && currentRemainingMwh is { } curMwh
            ? (startMwh - curMwh) / 1000.0 : null;

        // The IOCTL-reported Full-Charge Capacity (BatteryHealthReader) is a static figure that doesn't
        // move within a single run, so its start-vs-end delta was always ~0 and told the operator nothing.
        // These two blocks instead show this run's own *calculated* full-charge capacity and health [BatteryDrainRunner's EstimatedFullChargeCapacityMwh, derived from this run's own
        // remaining-capacity/percent readings] at the end of the test, which is actually informative.
        double? finalEstimatedFccMwh = end is not null ? last?.EstimatedFullChargeCapacityMwh : null;
        double? calculatedHealthPercent = finalEstimatedFccMwh is { } fcc && start.DesignCapacityMwh is { } design && design > 0
            ? fcc / design * 100.0 : null;

        sb.Append("<section class=\"summary-blocks\">\n");
        AppendBlock(sb, "Elapsed Time", FormatDuration(elapsed), "neutral");
        AppendBlock(sb, "Battery Drained", drained is { } d ? $"{d:0.0}%" : "N/A", "red",
            consumedWh is { } cw ? $"{cw:0.00} Wh Consumed" : null);
        AppendBlock(sb, "Load Method", MethodText(options.Method), "neutral");
        AppendBlock(sb, "Full-Charge Capacity (Est.)", finalEstimatedFccMwh is { } fe ? $"{fe:0} mWh" : "Pending; run not yet complete", "neutral");
        AppendBlock(sb, "Battery Health (Est.)", calculatedHealthPercent is { } chp ? $"{chp:0.0}%" : "Pending; run not yet complete",
            calculatedHealthPercent is { } chp2 ? (chp2 >= 80 ? "green" : "amber") : "neutral");
        AppendBlock(sb, "Stop Condition", end?.StopReason ?? options.ToString(), "neutral");
        sb.Append("</section>\n");
    }

    // Everything BatteryHealthReader could gather beyond the drain-specific summary numbers above: cycle
    // count, serial number, chemistry, manufacturer/device name, and a fast AC-line cross-check. Prefers
    // the end snapshot's values once complete, falling back to start's otherwise. Every unreported field
    // renders as an explicit greyed "N/A", never fabricated, via the "na" CSS class.
    //
    // Design Capacity, Full-Charge Capacity (BMS), Degradation Level (BMS), Full-Charge Capacity
    // (Observed), and Degradation Level (Observed) use the same BMS/Observed pairing and formulas as
    // BatteryChargeReportBuilder.AppendOtherBatteryData's Other Battery Data section. BMS is the pack's
    // own IOCTL/WMI/LHM-merged lifetime figures (BatteryHealthReader, unchanged run to run); Observed is
    // this run's own calculated Full-Charge Capacity/health [the same EstimatedFullChargeCapacityMwh-
    // derived figures AppendSummaryBlocks already computes and shows as "Full-Charge Capacity (Est.)"/
    // "Battery Health (Est.)"] recomputed here from the same inputs (`end`, `samples`,
    // `start.DesignCapacityMwh`) rather than threaded through as parameters, so this method's signature
    // only grows by the one new `samples` parameter needed to do that.
    private static void AppendBatteryInfoTable(StringBuilder sb, BatteryDrainStartSnapshot start,
        BatteryDrainEndSnapshot? end, IReadOnlyList<BatteryDrainSample> samples)
    {
        string? deviceName = end?.DeviceName ?? start.DeviceName;
        string? manufacturer = end?.ManufacturerName ?? start.ManufacturerName;
        string? serial = end?.SerialNumber ?? start.SerialNumber;
        string? chemistry = end?.Chemistry ?? start.Chemistry;
        int? cycleCount = end?.CycleCount ?? start.CycleCount;
        string? acLine = end?.AcLineStatus ?? start.AcLineStatus;
        var designMwh = start.DesignCapacityMwh;
        var fullChargeMwhBms = end?.FullChargeCapacityMwh ?? start.FullChargeCapacityMwh;
        var degradationBms = end?.EndingDegradationPercent ?? start.StartingDegradationPercent;

        // Same as AppendSummaryBlocks' finalEstimatedFccMwh/calculatedHealthPercent: only meaningful once
        // the run has actually ended (end is not null); a mid-run figure would be based on however few
        // ticks have happened so far and isn't yet a real "this run's own calculated" result.
        var last = samples.Count > 0 ? samples[^1] : null;
        double? finalEstimatedFccMwh = end is not null ? last?.EstimatedFullChargeCapacityMwh : null;
        double? healthObserved = finalEstimatedFccMwh is { } fcc && designMwh is { } dm && dm > 0
            ? fcc / dm * 100.0 : (double?)null;
        var degradationObserved = healthObserved is { } ho ? 100.0 - ho : (double?)null;

        sb.Append("<section class=\"battery-info\">\n<h2>Battery Info</h2>\n<table class=\"info-table\">\n");
        AppendInfoRow(sb, "Device Name", deviceName);
        AppendInfoRow(sb, "Manufacturer", manufacturer);
        AppendInfoRow(sb, "Serial Number", serial);
        AppendInfoRow(sb, "Chemistry", BatteryReportSharedStyle.FriendlyChemistryLabel(chemistry));
        AppendInfoRow(sb, "Cycle Count", cycleCount?.ToString(CultureInfo.InvariantCulture));
        AppendInfoRow(sb, "AC Line Status (at last read)", acLine);
        AppendInfoRow(sb, "Design Capacity", designMwh is { } dm2 ? $"{dm2:0} mWh" : null);
        AppendInfoRow(sb, "Full-Charge Capacity (BMS)", fullChargeMwhBms is { } fm ? $"{fm:0} mWh" : null);
        AppendInfoRow(sb, "Degradation Level (BMS)", degradationBms is { } db ? $"{db:0.0}%" : null);
        AppendInfoRow(sb, "Full-Charge Capacity (Observed)", finalEstimatedFccMwh is { } fob ? $"{fob:0} mWh" : null);
        AppendInfoRow(sb, "Degradation Level (Observed)", degradationObserved is { } dob ? $"{dob:0.0}%" : null);
        sb.Append("</table>\n</section>\n");
    }

    private static void AppendInfoRow(StringBuilder sb, string label, string? value)
    {
        var known = !string.IsNullOrWhiteSpace(value);
        sb.Append("<tr><td class=\"info-label\">").Append(Enc(label)).Append("</td><td class=\"")
          .Append(known ? "info-value" : "info-value na").Append("\">")
          .Append(known ? Enc(value!) : "N/A").Append("</td></tr>\n");
    }

    private static void AppendBlock(StringBuilder sb, string label, string value, string colorClass, string? subLine = null)
    {
        sb.Append($"<div class=\"block block-{colorClass}\"><div class=\"block-label\">{Enc(label)}</div>" +
                  $"<div class=\"block-value\">{Enc(value)}</div>");
        if (subLine is not null)
            sb.Append($"<div class=\"block-sub\">{Enc(subLine)}</div>");
        sb.Append("</div>\n");
    }

    // Shaded with two background bar datasets showing when each stress engine was actually loading the
    // system (CpuLoadPercent/GpuLoadPercent above ActiveLoadThreshold), rather than assuming load starts
    // at elapsed=0; the engines take a few seconds to spin up, so a naive "run start to run end" band
    // would shade a flat lead-in period where nothing was actually stressing the battery yet.
    //
    // The Discharge Current (mA) line follows the same pattern as Charge Current/Rate on the Charge report
    // (BatteryChargeReportBuilder.AppendChargeCurveChart): a 60-second bucket-averaged series shown by
    // default, with the untouched raw series kept as a second, initially-hidden dataset a "Show raw,
    // unaveraged data" checkbox swaps to. This chart keeps its existing categorical (label-per-sample)
    // x-axis rather than switching to the Charge report's linear-elapsed-seconds one, every dataset here
    // (Battery %, both stress-active bands) is already index-aligned to `labels`, so the averaged variant
    // is built the same way: each 60s bucket's average is broadcast back onto every sample index that
    // falls in that bucket (a stepped line), rather than collapsing to one point per bucket the way
    // BatteryChargeReportBuilder.BucketAverage does for its own linear-axis charts; that would leave it
    // with a different point count than the other index-aligned datasets on this chart, which a categorical
    // axis can't reconcile. DischargeCurrentMa mirrors ChargeCurrentMa's null convention (see
    // LibreHardwareSensorService.ReadBattery): null means "not currently resolved as discharging", not
    // "reads as zero": the same leading-null-becomes-0 treatment Charge Current gets (ZeroFillLeadingNulls
    // below) applies here for the same reason, even though in practice the drain runner's own AC-unplug
    // gate means discharging is usually already resolved by the first tick.
    private static void AppendChart(StringBuilder sb, IReadOnlyList<BatteryDrainSample> samples)
    {
        const double ActiveLoadThreshold = 5.0;

        var labels = samples.Select(s => BatteryReportSharedStyle.FormatElapsedHms(TimeSpan.FromSeconds(s.ElapsedSeconds)));
        var values = samples.Select(s => s.BatteryPercent?.ToString(CultureInfo.InvariantCulture) ?? "null");
        var cpuActive = samples.Select(s => s.CpuLoadPercent is { } cl && cl >= ActiveLoadThreshold ? "100" : "null");
        var gpuActive = samples.Select(s => s.GpuLoadPercent is { } gl && gl >= ActiveLoadThreshold ? "100" : "null");

        var hasDischarge = samples.Any(s => s.DischargeCurrentMa is not null);
        var dischargeZeroFilled = ZeroFillLeadingNulls(samples, s => s.DischargeCurrentMa);
        var dischargeAvgBroadcast = BucketAverageBroadcast(samples, dischargeZeroFilled);
        var dischargeRawValues = dischargeZeroFilled.Select(v => v?.ToString(CultureInfo.InvariantCulture) ?? "null");
        var dischargeAvgValues = dischargeAvgBroadcast.Select(v => v?.ToString(CultureInfo.InvariantCulture) ?? "null");

        sb.Append("<section class=\"chart-section\">\n<h2>Battery Percent Over Time</h2>\n");
        if (hasDischarge)
        {
            // Custom checkbox controls (not Chart.js's built-in legend) for the same reason the Charge
            // report's Charge Current uses them: swapping between the averaged/raw variant is a
            // wtGroup+wtVariant match, not something a plain legend click can express. The other three
            // datasets on this chart (Battery %/CPU Active/GPU Active) are left on the default legend.
            // Positioned above the canvas with a color swatch matching the line's own color (#8E6EFF), 
            // being excluded from Chart.js's own legend (see the wtGroup filter below) means this line
            // otherwise has no visible color key, unlike the other three datasets.
            sb.Append("<div class=\"chart-legend\">\n");
            sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtToggle('current',this.checked)\">" +
                      "<span class=\"legend-swatch\" style=\"background:#8E6EFF\"></span> Discharge Current (mA)</label>\n");
            sb.Append("<label><input type=\"checkbox\" onchange=\"wtSetDischargeCurrentVariant(this.checked)\"> Show raw, unaveraged data</label>\n");
            sb.Append("</div>\n");
        }
        sb.Append("<canvas id=\"battChart\" height=\"90\"></canvas>\n");
        sb.Append("</section>\n");
        sb.Append("<script>\n");
        sb.Append("var wtBattChart = new Chart(document.getElementById('battChart'), { type: 'line', data: { labels: [");
        sb.Append(string.Join(",", labels.Select(l => $"\"{l}\"")));
        sb.Append("], datasets: [");
        sb.Append("{ type: 'bar', label: 'CPU Stress Active', data: [").Append(string.Join(",", cpuActive))
          .Append("], backgroundColor: 'rgba(62,182,255,0.25)', borderWidth: 0, barPercentage: 1.0, categoryPercentage: 1.0, order: 3 },");
        sb.Append("{ type: 'bar', label: 'GPU Stress Active', data: [").Append(string.Join(",", gpuActive))
          .Append("], backgroundColor: 'rgba(255,214,0,0.28)', borderWidth: 0, barPercentage: 1.0, categoryPercentage: 1.0, order: 2 },");
        sb.Append("{ label: 'Battery %', data: [");
        sb.Append(string.Join(",", values));
        // Fewer, non-rotated ticks read more cleanly than Chart.js's default autoSkip on this categorical
        // (non-time-scale) axis; a real 'time' scale isn't available in this vendored build. maxRotation: 0
        // avoids diagonal-label clutter once ticks overlap.
        sb.Append("], borderColor: '#D93B3B', backgroundColor: 'rgba(217,59,59,0.08)', pointRadius: 0, " +
                  "borderWidth: 2, tension: 0.15, spanGaps: true, order: 1, yAxisID: 'y' }");
        if (hasDischarge)
        {
            // wtVariant distinguishes the two Discharge Current series sharing wtGroup 'current', see
            // wtSetDischargeCurrentVariant below. The raw dataset starts hidden since the averaged view is
            // the default, same as Charge Current on the Charge report.
            sb.Append(",{ wtGroup: 'current', wtVariant: 'avg', label: 'Discharge Current (mA)', data: [")
              .Append(string.Join(",", dischargeAvgValues))
              .Append("], borderColor: '#8E6EFF', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yCurrent', order: 1 }");
            sb.Append(",{ wtGroup: 'current', wtVariant: 'raw', hidden: true, label: 'Discharge Current (mA), raw', data: [")
              .Append(string.Join(",", dischargeRawValues))
              .Append("], borderColor: '#8E6EFF', pointRadius: 0, borderWidth: 1, tension: 0.1, spanGaps: true, yAxisID: 'yCurrent', order: 1 }");
        }
        sb.Append("] }, options: { responsive: true, " +
                  // This chart had no `interaction` option at all, which left Chart.js on its built-in
                  // default of intersect:true; a tooltip only fires when the cursor is essentially
                  // touching a rendered line, which for pointRadius:0 lines with no visible dots reads as
                  // "must be pixel-perfect". mode:'index' + intersect:false is the same fix the Charge
                  // report's two charts already have; axis:'x' makes explicit what 'index' already
                  // defaults to (match on x-proximity only, regardless of the cursor's vertical position),
                  // and elements.point.hitRadius widens the invisible hit-target every point on this chart
                  // is checked against. Every dataset here (bar/line) is already the same length as
                  // `labels` (Battery %/CPU Active/GPU Active are plain per-sample selects, Discharge
                  // Current goes through BucketAverageBroadcast specifically to stay index-aligned), so
                  // unlike the Charge report's linear-axis charts this one didn't also need a
                  // dataset-alignment fix, just the missing interaction config.
                  "interaction: { intersect: false, mode: 'index', axis: 'x' }, elements: { point: { hitRadius: 20 } }, " +
                  // Discharge Current's two datasets are driven entirely by the custom checkboxes above, not
                  // Chart.js's own legend; same filter approach the Charge report's Charge Curve chart uses
                  // to hide its boundary-marker datasets from the default legend.
                  "plugins: { legend: { labels: { filter: function(item, data){ return data.datasets[item.datasetIndex].wtGroup !== 'current'; } } } }, " +
                  "scales: { y: { min: 0, max: 100, position: 'left', title: { display: true, text: 'Battery %' } }, " +
                  (hasDischarge ? "yCurrent: { min: 0, position: 'right', grid: { drawOnChartArea: false }, title: { display: true, text: 'Discharge Current (mA)' } }, " : "") +
                  "x: { title: { display: true, text: 'Elapsed Time' }, " +
                  "ticks: { autoSkip: true, maxTicksLimit: 10, maxRotation: 0 } } } } });\n");
        if (hasDischarge)
        {
            // Mirrors BatteryChargeReportBuilder's wtCurrentGroupVisible/wtCurrentVariant pair exactly: the
            // group-visibility checkbox and the avg/raw variant checkbox track state independently so
            // toggling one never clobbers the other (e.g. unchecking then re-checking "Discharge Current"
            // must restore whichever variant was selected, not always fall back to avg).
            sb.Append("var wtCurrentGroupVisible = true;\n");
            sb.Append("var wtCurrentVariant = 'avg';\n");
            sb.Append("function wtApplyCurrentVisibility() { " +
                       "wtBattChart.data.datasets.forEach(function(d){ if (d.wtGroup === 'current') d.hidden = !wtCurrentGroupVisible || d.wtVariant !== wtCurrentVariant; }); " +
                       "wtBattChart.update(); }\n");
            sb.Append("function wtToggle(group, visible) { " +
                       "if (group === 'current') { wtCurrentGroupVisible = visible; wtApplyCurrentVisibility(); return; } " +
                       "wtBattChart.data.datasets.forEach(function(d){ if (d.wtGroup === group) d.hidden = !visible; }); " +
                       "wtBattChart.update(); }\n");
            sb.Append("function wtSetDischargeCurrentVariant(raw) { wtCurrentVariant = raw ? 'raw' : 'avg'; wtApplyCurrentVisibility(); }\n");
        }
        sb.Append("</script>\n");
    }

    // ===================== Thermal (80 mm receipt) view chart data =====================
    //
    // Battery Percent chart: Battery % (left, 0-100) with the 60 s averaged Discharge Current on the right axis;
    // Temperature chart: CPU / GPU Core / GPU Hot Spot on one 0-110 axis. x is elapsed seconds from run start.
    // CPU / GPU stress spans (load at or above ActiveLoadThreshold, same rule as the normal chart's background
    // bars) print as two band rows under each chart. Long runs are decimated inside ThermalBatteryData.MakeSeries.
    private static ThermalBatteryData BuildThermalData(IReadOnlyList<BatteryDrainSample> samples)
    {
        const double ActiveLoadThreshold = 5.0;
        var duration = samples.Count > 0 ? samples[^1].ElapsedSeconds : 0.0;

        var bands = new List<ThermalBand>();
        foreach (var (start, end) in ThermalBatteryData.Spans(samples.Select(s => (s.ElapsedSeconds, s.CpuLoadPercent is { } c && c >= ActiveLoadThreshold))))
            bands.Add(new ThermalBand("CPU stress", "CPU", start, end, 0));
        foreach (var (start, end) in ThermalBatteryData.Spans(samples.Select(s => (s.ElapsedSeconds, s.GpuLoadPercent is { } g && g >= ActiveLoadThreshold))))
            bands.Add(new ThermalBand("GPU stress", "GPU", start, end, 1));

        var pctAxes = new List<ThermalAxis> { new("pct", "left", "%", 0, 100) };
        var pctSeries = new List<ThermalSeries>
        {
            ThermalBatteryData.MakeSeries("Battery %", "pct", samples.Select(s => (s.ElapsedSeconds, s.BatteryPercent)), 1)
        };
        if (samples.Any(s => s.DischargeCurrentMa is not null))
        {
            var avg = BucketAverageBroadcast(samples, ZeroFillLeadingNulls(samples, s => s.DischargeCurrentMa));
            pctAxes.Add(new ThermalAxis("current", "right", "mA", Min: 0));
            pctSeries.Add(ThermalBatteryData.MakeSeries("Discharge Current (mA)", "current", samples.Select((s, i) => (s.ElapsedSeconds, avg[i])), 1));
        }

        var charts = new List<ThermalChart> { new("Battery Percent", pctAxes, pctSeries, bands) };

        if (samples.Count > 0)
        {
            var tempSeries = new List<ThermalSeries>();
            void AddTemp(string label, Func<BatteryDrainSample, double?> pick)
            {
                if (samples.Any(s => pick(s) is not null))
                    tempSeries.Add(ThermalBatteryData.MakeSeries(label, "temp", samples.Select(s => (s.ElapsedSeconds, pick(s))), 1));
            }
            AddTemp("CPU Temp (\u00B0C)", s => s.CpuTempC);
            AddTemp("GPU Core (\u00B0C)", s => s.GpuTempC);
            AddTemp("GPU Hot Spot (\u00B0C)", s => s.GpuHotspotTempC);
            charts.Add(new ThermalChart("Temperature", new List<ThermalAxis> { new("temp", "left", "\u00B0C", 0, 110) }, tempSeries, bands));
        }
        return new ThermalBatteryData("drain", duration, charts);
    }

    // Turns only the *leading* run of nulls for a selected field (before its first non-null reading) into
    // literal 0.0: mirrors BatteryChargeReportBuilder.ZeroFillLeadingNulls exactly (same reasoning: a
    // "not yet resolved as discharging" reading is a real "nothing flowing yet" 0, not a gap to be
    // interpolated across). Any null after the first real reading is left untouched and still renders as a
    // spanned gap via spanGaps: true on the consuming line.
    private static IReadOnlyList<double?> ZeroFillLeadingNulls(
        IReadOnlyList<BatteryDrainSample> samples, Func<BatteryDrainSample, double?> selector)
    {
        var result = new List<double?>(samples.Count);
        var seenReal = false;
        foreach (var s in samples)
        {
            var y = selector(s);
            if (y is not null) seenReal = true;
            result.Add(seenReal ? y : 0.0);
        }
        return result;
    }

    // Buckets samples into fixed-width 60s windows of ElapsedSeconds and averages the non-null values in
    // each window, same noise-reduction reasoning as BatteryChargeReportBuilder.BucketAverage (a real
    // discharge/charge current can jump between levels every few seconds as engines ramp [Finding C] which
    // reads as noise more than signal at full sample resolution). Unlike that method, this one returns one
    // entry per *sample* (not per bucket): every sample's value is replaced with its own bucket's average, so
    // the result stays index-aligned with `labels` for this chart's categorical x-axis instead of collapsing
    // to a shorter, differently-spaced point list. A bucket with no non-null samples leaves every sample in
    // it as null, which spanGaps: true on the consuming line spans over exactly like the raw series does.
    private static IReadOnlyList<double?> BucketAverageBroadcast(
        IReadOnlyList<BatteryDrainSample> samples, IReadOnlyList<double?> values, double bucketSeconds = 60.0)
    {
        var bucketSums = new Dictionary<double, (double Sum, int Count)>();
        for (var i = 0; i < samples.Count; i++)
        {
            if (values[i] is not { } v) continue;
            var key = Math.Floor(samples[i].ElapsedSeconds / bucketSeconds);
            bucketSums[key] = bucketSums.TryGetValue(key, out var acc) ? (acc.Sum + v, acc.Count + 1) : (v, 1);
        }

        var result = new List<double?>(samples.Count);
        foreach (var s in samples)
        {
            var key = Math.Floor(s.ElapsedSeconds / bucketSeconds);
            result.Add(bucketSums.TryGetValue(key, out var acc) ? acc.Sum / acc.Count : (double?)null);
        }
        return result;
    }

    // GPU Hot Spot commonly reads meaningfully higher than GPU Core under sustained load (see
    // SensorSnapshot.GpuTelemetry); both are shown as independent lines rather than picking one, styled
    // distinctly (Hot Spot solid bright red, Core dashed orange) so they're easy to tell apart at a glance.
    private static void AppendTemperatureChart(StringBuilder sb, IReadOnlyList<BatteryDrainSample> samples)
    {
        if (samples.Count == 0) return;

        var labels = samples.Select(s => BatteryReportSharedStyle.FormatElapsedHms(TimeSpan.FromSeconds(s.ElapsedSeconds)));
        var cpuVals = samples.Select(s => s.CpuTempC?.ToString(CultureInfo.InvariantCulture) ?? "null");
        var gpuCoreVals = samples.Select(s => s.GpuTempC?.ToString(CultureInfo.InvariantCulture) ?? "null");
        var gpuHotspotVals = samples.Select(s => s.GpuHotspotTempC?.ToString(CultureInfo.InvariantCulture) ?? "null");

        sb.Append("<section class=\"chart-section\">\n<h2>Temperature Over Time</h2>\n");
        sb.Append("<canvas id=\"tempChart\" height=\"90\"></canvas>\n</section>\n");
        sb.Append("<script>\n");
        sb.Append("new Chart(document.getElementById('tempChart'), { type: 'line', data: { labels: [");
        sb.Append(string.Join(",", labels.Select(l => $"\"{l}\"")));
        sb.Append("], datasets: [");
        sb.Append("{ label: 'CPU Temp (C)', data: [").Append(string.Join(",", cpuVals))
          .Append("], borderColor: '#3ED6FF', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true },");
        sb.Append("{ label: 'GPU Core (C)', data: [").Append(string.Join(",", gpuCoreVals))
          .Append("], borderColor: '#E0A030', pointRadius: 0, borderWidth: 2, borderDash: [6,4], tension: 0.15, spanGaps: true },");
        sb.Append("{ label: 'GPU Hot Spot (C)', data: [").Append(string.Join(",", gpuHotspotVals))
          .Append("], borderColor: '#FF1414', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true }");
        sb.Append("] }, options: { responsive: true, " +
                  // Without this, Chart.js defaults to intersect:true, so a tooltip only fires when the
                  // cursor is essentially touching a rendered line: unworkable for pointRadius:0 lines
                  // with no visible dots. mode:'index'+intersect:false matches on x-proximity instead, and
                  // hitRadius widens the invisible per-point hit target.
                  "interaction: { intersect: false, mode: 'index', axis: 'x' }, elements: { point: { hitRadius: 20 } }, " +
                  "scales: { y: { min: 0, max: 110, title: { display: true, text: 'Degrees C' } }, " +
                  "x: { title: { display: true, text: 'Elapsed Time' }, " +
                  "ticks: { autoSkip: true, maxTicksLimit: 10, maxRotation: 0 } } } } });\n");
        sb.Append("</script>\n");
    }

    // Mirrors powercfg /batteryreport's "Battery usage" table layout; START TIME / STATE / DURATION / ENERGY DRAINED. Rows with no energy-drained reading (AC-connected spans) show "-", matching powercfg's own convention for a genuinely not-applicable cell rather than our usual "N/A".
    private static void AppendDrainEventsTable(StringBuilder sb, BatteryHistoryResult history)
    {
        sb.Append("<section class=\"data-table\">\n<h2>Battery Usage</h2>\n");
        sb.Append("<div class=\"section-sub\">Battery drains over the last few days</div>\n");

        if (!history.Success || history.DrainEvents.Count == 0)
        {
            sb.Append($"<p class=\"na-block\">{Enc(history.Detail)}</p>\n</section>\n");
            return;
        }

        sb.Append("<table>\n<tr><th>Start Time</th><th>State</th><th>Duration</th><th>Energy Drained</th></tr>\n");
        foreach (var e in history.DrainEvents)
        {
            var start = e.StartTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "-";
            var energy = e.EnergyDrainedPercent is { } p && e.EnergyDrainedMwh is { } m
                ? $"{p:0}% / {m:#,0} mWh"
                : "-";
            sb.Append("<tr>")
              .Append($"<td>{Enc(start)}</td>")
              .Append($"<td>{Enc(e.State)}</td>")
              .Append($"<td>{Enc(e.DurationText ?? "-")}</td>")
              .Append($"<td>{Enc(energy)}</td>")
              .Append("</tr>\n");
        }
        sb.Append("</table>\n</section>\n");
    }

    // Mirrors powercfg /batteryreport's own "Recent usage" table layout [START TIME / STATE / SOURCE /
    // CAPACITY REMAINING] rather than redrawing it as a chart (retired: the old iOS-Settings-style canvas
    // with AC-shaded bands behind a Battery % line). Same "-" for a genuinely not-applicable cell convention
    // as AppendDrainEventsTable's "Battery usage" table (e.g. "Battery changed" rows, which carry neither a
    // percent nor an mWh reading). Source displays powercfg's own wording ("AC"/"Battery"), not the
    // BatteryUsagePoint.Source field's internal "AC"/"DC" values.
    private static void AppendUsageHistoryTable(StringBuilder sb, BatteryHistoryResult history)
    {
        sb.Append("<section class=\"data-table\">\n<h2>Recent usage</h2>\n");
        sb.Append("<div class=\"section-sub\">Power states over the last 3 days</div>\n");

        if (!history.Success || history.UsagePoints.Count == 0)
        {
            sb.Append($"<p class=\"na-block\">{Enc(history.Detail)}</p>\n</section>\n");
            return;
        }

        sb.Append("<table>\n<tr><th>Start Time</th><th>State</th><th>Source</th><th>Capacity Remaining</th></tr>\n");
        foreach (var p in history.UsagePoints)
        {
            var time = p.Time is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "-";
            var source = p.Source switch
            {
                "AC" => "AC",
                "DC" => "Battery",
                _ => "-"
            };
            var capacity = p.Percent is { } pct && p.CapacityRemainingMwh is { } mwh
                ? $"{pct:0}% / {mwh:#,0} mWh"
                : "-";
            sb.Append("<tr>")
              .Append($"<td>{Enc(time)}</td>")
              .Append($"<td>{Enc(p.State)}</td>")
              .Append($"<td>{Enc(source)}</td>")
              .Append($"<td>{Enc(capacity)}</td>")
              .Append("</tr>\n");
        }
        sb.Append("</table>\n</section>\n");
    }

    private static void AppendDataTable(StringBuilder sb, IReadOnlyList<BatteryDrainSample> samples)
    {
        sb.Append("<section class=\"data-table\">\n<details>\n<summary>Full logged data (")
          .Append(samples.Count).Append(" samples)</summary>\n<table>\n");
        sb.Append("<tr><th>Elapsed</th><th>Time</th><th>Batt %</th><th>Voltage (V)</th>" +
                   "<th>Cap. Remaining (mWh)</th><th>Est. Full Charge Cap. (mWh)</th>" +
                   "<th>CPU Load %</th><th>CPU Temp (C)</th><th>GPU Load %</th><th>GPU Core (C)</th><th>GPU Hot Spot (C)</th></tr>\n");
        foreach (var s in samples)
        {
            sb.Append("<tr>")
              .Append($"<td>{Enc(FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds)))}</td>")
              .Append($"<td>{Enc(s.Timestamp.ToString("HH:mm:ss"))}</td>")
              .Append($"<td>{Fmt(s.BatteryPercent, "0.0")}</td>")
              .Append($"<td>{Fmt(s.VoltageV, "0.00")}</td>")
              .Append($"<td>{Fmt(s.RemainingCapacityMwh, "0")}</td>")
              .Append($"<td>{Fmt(s.EstimatedFullChargeCapacityMwh, "0")}</td>")
              .Append($"<td>{Fmt(s.CpuLoadPercent, "0.0")}</td>")
              .Append($"<td>{Fmt(s.CpuTempC, "0.0")}</td>")
              .Append($"<td>{Fmt(s.GpuLoadPercent, "0.0")}</td>")
              .Append($"<td>{Fmt(s.GpuTempC, "0.0")}</td>")
              .Append($"<td>{Fmt(s.GpuHotspotTempC, "0.0")}</td>")
              .Append("</tr>\n");
        }
        sb.Append("</table>\n</details>\n</section>\n");
    }

    private static string MethodText(BatteryDrainMethod m) => m switch
    {
        BatteryDrainMethod.CpuOnly => "CPU stress",
        BatteryDrainMethod.GpuOnly => "GPU stress (FurMark, off-screen)",
        BatteryDrainMethod.CpuAndGpu => "CPU + GPU stress",
        _ => m.ToString()
    };

    private static string Fmt(double? v, string format) => v is { } d ? d.ToString(format, CultureInfo.InvariantCulture) : "N/A";

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}"
            : $"{d.Minutes:00}:{d.Seconds:00}";

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    // Light theme, matching HtmlReportGenerator's regular diagnostic report (white cards, off-white page
    // background, dark text). .card/.kv-table/.activation-*/.na come from
    // BatteryReportSharedStyle.SystemInfoCss (appended alongside this in Render); everything below is
    // this report's own sections (summary blocks, battery info, charts).
    private const string Css = @"
* { box-sizing:border-box; }
body { background:#F4F5F7; color:#1C1C1F; font-family:Segoe UI,Arial,sans-serif; margin:0; padding:24px; }
.report-header { background:#1C1C1F; color:#FFFFFF; padding:24px; border-radius:8px; margin-bottom:20px; }
.report-header h1 { margin:0 0 4px 0; font-size:24px; }
.verdict { display:inline-block; font-size:20px; font-weight:700; padding:6px 20px; border-radius:6px; margin-bottom:14px; letter-spacing:0.05em; }
.verdict-pass { background:#1E7A46; color:#FFFFFF; }
.verdict-fail { background:#B23A32; color:#FFFFFF; }
.verdict-pending { background:#6E6E6E; color:#FFFFFF; }
.subtitle { color:#C7C9CF; margin:0 0 4px 0; }
.summary-blocks { display:flex; flex-wrap:wrap; gap:12px; margin-bottom:20px; }
.block { flex:1 1 150px; border-radius:8px; padding:14px; background:#FFFFFF; border:1px solid #E1E3E8; }
.block-label { font-size:11px; color:#6B6B72; text-transform:uppercase; letter-spacing:.03em; margin-bottom:6px; }
.block-value { font-size:20px; font-weight:700; color:#1C1C1F; }
.block-sub { font-size:12px; color:#6B6B72; margin-top:2px; font-weight:400; }
.block-red .block-value { color:#B23A32; }
.block-green .block-value { color:#1E7A46; }
.block-amber .block-value { color:#9C6B10; }
.block-neutral .block-value { color:#1C1C1F; }
.chart-section, .data-table, .battery-info { background:#FFFFFF; border:1px solid #E1E3E8; border-radius:8px; padding:20px; margin-bottom:20px; }
.chart-section h2, .data-table h2, .battery-info h2 { margin-top:0; font-size:17px; }
.chart-legend { display:flex; flex-wrap:wrap; gap:14px; margin:2px 0 12px 0; font-size:12px; color:#4A4A52; }
.chart-legend label { display:flex; align-items:center; gap:5px; cursor:pointer; }
.legend-swatch { display:inline-block; width:11px; height:11px; border-radius:2px; }
.section-sub { color:#6B6B72; font-size:12px; margin:-6px 0 12px 0; }
table { border-collapse:collapse; width:100%; font-size:12px; margin-top:10px; }
th, td { border:1px solid #E1E3E8; padding:5px 9px; text-align:right; }
th:first-child, td:first-child { text-align:left; }
th { background:#F4F5F7; font-size:11px; text-transform:uppercase; opacity:.7; }
.info-table { width:auto; min-width:340px; border:none; }
.info-table td { text-align:left; border:none; }
.info-label { color:#6B6B72; width:220px; }
.info-value { font-weight:600; }
.info-value.na { color:#9A9AA2; font-weight:400; font-style:italic; }
.na-block { color:#6B6B72; font-size:12px; font-style:italic; }
.report-footer { color:#8A8A93; font-size:11px; margin-top:20px; }
";
}
