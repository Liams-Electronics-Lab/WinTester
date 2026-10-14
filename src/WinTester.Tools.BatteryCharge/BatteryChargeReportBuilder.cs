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

namespace WinTester.Tools.BatteryCharge;

// Renders one Battery Charge Test run to a single self-contained, offline HTML file, reusing
// BatteryDrainReportBuilder's vendored Chart.js copy. Layout, top to bottom:
// 1. Condition banner: GOOD/OK/BAD/INCONCLUSIVE.
// 2. Rating Basis: starting SoC, duration, phase reference used, and a provisional-curve note when
//    grading relied on the cold-start reference curve.
// 3. Charge Curve chart: Battery % on a fixed 0-100 right axis, Voltage as its own auto-scaled line,
//    Charge Current as a dashed line on its own auto-scaled left axis, Temperature added only when
//    logged (shares the % axis). Wide Sweep adds a lighter extrapolated segment for Battery % only.
//    CC/CV phase shading (final report only) is drawn on this chart and Charge Power below via the
//    shared boundary-region plugin.
// 4. Charge Power chart: Charge Rate (W) and Remaining Capacity (Wh) on their own two-axis chart.
// 5. Quick stats: Battery Health %, Energy Put Into Battery, Chemistry, Device Name, both serial numbers.
// 6. Factory-vs-Observed table, cell-shaded; informational only, does not drive the condition banner.
// 7. Other battery data, including BMS-reported vs. Observed Degradation Level/Full-Charge Capacity.
// A translucent "TEST IN PROGRESS" overlay is shown while the report is still live (isFinal: false).
public static class BatteryChargeReportBuilder
{
    // Cell-shading tolerance bands for the informational Factory-vs-Actual table. Assumed default, not
    // operator-confirmed.
    private const double ShadeGreenPercent = 10.0;
    private const double ShadeAmberPercent = 25.0;

    public static string Render(BatteryChargeOptions options, BatteryChargeStartSnapshot start,
        BatteryChargeEndSnapshot? end, IReadOnlyList<BatteryChargeSample> samples,
        BatteryChargeRating rating, bool isFinal, BrandProfile? brand = null,
        ActivationSnapshot? activation = null, string? activationImageDataUri = null,
        BatteryVoltageRangeResult? voltageRange = null)
    {
        var chartJs = HtmlReportGenerator.GetVendoredChartJsSource();
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"UTF-8\">\n");
        if (!isFinal)
            sb.Append("<meta http-equiv=\"refresh\" content=\"5\">\n"); // Live-tailed while running; final write omits this.
        sb.Append("<title>Battery Charge Test Report</title>\n");
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
        // Shown only while this report is still live-updating.
        if (!isFinal)
            BatteryReportSharedStyle.AppendInProgressOverlay(sb);
        AppendStatePreservationScript(sb);

        BatteryReportSharedStyle.AppendBrandHeader(sb, brand);
        AppendHeader(sb, options, isFinal, end);
        // System-identity fields intentionally omitted (null, null, null), Device Name/Manufacturer/
        // System Serial Number are already shown in the Quick Stats and Other Battery Data sections below.
        BatteryReportSharedStyle.AppendSystemInformation(sb, null, null, null, activation, activationImageDataUri);

        var health = end?.EndingBatteryHealthPercent ?? start.StartingBatteryHealthPercent;

        AppendQuickStats(sb, start, end, samples, health, rating, isFinal);

        AppendConditionBanner(sb, rating);
        AppendRatingBasis(sb, rating);
        // Shared time-zero for both charts: the actual test start (first real sample), not Wide Sweep's
        // synthetic extrapolation start, so the same wall-clock moment lines up on both charts. Wide
        // Sweep's extrapolated Battery % segment on the Charge Curve chart draws at negative x (before
        // test start), which is expected.
        var testStartElapsed = samples.Count > 0 ? samples[0].ElapsedSeconds : 0.0;
        // CC/CV markers are final-report-only: a partial curve's voltage climbs toward the pack's own
        // peak regardless of battery health, so a live, incomplete run can't be classified with confidence.
        var phases = isFinal ? BatteryChargePhaseDetector.Detect(samples) : null;
        AppendChargeCurveChart(sb, options, samples, testStartElapsed, phases);
        AppendChargePowerChart(sb, options, samples, testStartElapsed, phases);
        var factory = ComputeFactoryFigures(start);
        var measured = ComputeMeasuredFigures(start, end);
        AppendFactoryVsActualTable(sb, factory, measured);
        AppendOtherBatteryData(sb, options, start, end, samples, voltageRange, rating, isFinal);
        AppendDataTable(sb, samples);

        BatteryReportSharedStyle.AppendBrandFooter(sb, brand, "WinTester: Battery Charge Test report.");
        sb.Append("</div>\n"); // end #wt-normal
        // 80 mm thermal receipt version (chart data as JSON; text is read from the report above). The view
        // hash survives this report's 5 s live refresh. See WINTESTER_THERMAL_REPORT_TRACKER.md Phase 6.
        ThermalBatteryView.AppendScript(sb, BuildThermalData(options, samples, testStartElapsed, phases));
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    // ===================== Condition banner: GOOD/OK/BAD/INCONCLUSIVE tier display =====================

    private static void AppendConditionBanner(StringBuilder sb, BatteryChargeRating rating)
    {
        var (text, cls) = rating.Tier switch
        {
            BatteryChargeConditionTier.Good => ("GOOD", "cond-green"),
            BatteryChargeConditionTier.Ok => ("OK", "cond-amber"),
            BatteryChargeConditionTier.Bad => ("BAD", "cond-red"),
            BatteryChargeConditionTier.Inconclusive => ("INCONCLUSIVE", "cond-neutral"),
            _ => ("PENDING; test not yet complete", "cond-neutral")
        };
        sb.Append($"<section class=\"condition-banner {cls}\"><h1>{Enc(text)}</h1></section>\n");
    }

    // Rating Basis section shown under the condition banner: starting SoC, duration, phase reference used,
    // and [when the INCONCLUSIVE gate fires] a would-be (non-verdict) ratio.
    private static void AppendRatingBasis(StringBuilder sb, BatteryChargeRating rating)
    {
        var basis = rating.Basis;
        sb.Append("<section class=\"rating-basis\">\n<h2>Rating Basis</h2>\n<table class=\"info-table\">\n");
        AppendInfoRow(sb, "Chemistry Family Used", basis.Chemistry.ToString());
        AppendInfoRow(sb, "Starting Charge Level", basis.StartSocPercent is { } s ? $"{s:0.0}%" : null);
        AppendInfoRow(sb, "Test Duration", basis.DurationMinutes is { } d ? $"{d:0.0} min" : null);
        AppendInfoRow(sb, "Charge Phase(s) Covered", basis.PhaseReferenceUsed);

        if (basis.InconclusiveGateFired && rating.Ratio is { } r)
        {
            var wouldBeTier = r > 0.85 ? "GOOD" : r >= 0.70 ? "OK" : "BAD";
            AppendInfoRow(sb, "Would-be Rating (not a verdict)",
                $"{wouldBeTier} (ratio {r:0.00}); window too short/too deep in the taper to be a confident result");
        }

        sb.Append("</table>\n</section>\n");
    }

    // ===================== Factory vs. measured (informational only: no longer drives the banner) =====================

    private readonly record struct FactoryFigures(double? CurrentA, double? WattsW, double? CRating);
    private readonly record struct MeasuredFigures(double? CurrentA, double? WattsW, double? CRating);

    // Factory Charge Current/Watts/C-rating, derived from Design Capacity using the same 0.5C assumption
    // as BatteryChargeHealthAlgorithm.AssumedFactoryCRate, so this table and the grading algorithm agree
    // on what "factory" means.
    private static FactoryFigures ComputeFactoryFigures(BatteryChargeStartSnapshot start)
    {
        var nominalV = start.StartVoltageV is { } v and > 0 ? v : 11.1; // Typical laptop pack nominal voltage fallback.
        if (start.DesignCapacityMwh is not { } designMwh || designMwh <= 0) return new FactoryFigures(null, null, null);

        var designAh = designMwh / 1000.0 / nominalV;
        var currentA = BatteryChargeHealthAlgorithm.AssumedFactoryCRate * designAh;
        var wattsW = currentA * nominalV;
        return new FactoryFigures(currentA, wattsW, BatteryChargeHealthAlgorithm.AssumedFactoryCRate);
    }

    // Measured Charge Current/Watts/C-rating: this run's peak observed current/rate, using actual
    // Full-Charge Capacity (not Design) for the C-rating denominator.
    private static MeasuredFigures ComputeMeasuredFigures(BatteryChargeStartSnapshot start, BatteryChargeEndSnapshot? end)
    {
        if (end is null) return new MeasuredFigures(null, null, null);

        var nominalV = end.EndVoltageV ?? (start.StartVoltageV is { } v && v > 0 ? v : 11.1);
        var fullChargeMwh = end.FullChargeCapacityMwh ?? start.FullChargeCapacityMwh;

        double? currentA = end.PeakChargeCurrentMa is { } ma ? ma / 1000.0 : null;
        double? wattsW = end.PeakChargeRateW ?? (currentA is { } a ? a * nominalV : null);

        double? cRating = null;
        if (currentA is { } curA && fullChargeMwh is { } fcMwh && fcMwh > 0)
        {
            var fullChargeAh = fcMwh / 1000.0 / nominalV;
            if (fullChargeAh > 0) cRating = curA / fullChargeAh;
        }

        return new MeasuredFigures(currentA, wattsW, cRating);
    }

    // ===================== Header =====================

    private static void AppendHeader(StringBuilder sb, BatteryChargeOptions options, bool isFinal, BatteryChargeEndSnapshot? end)
    {
        sb.Append("<header class=\"report-header\">\n<h1>Battery Charge Test</h1>\n");
        // Plain-language subtitle and stop-reason phrase: no internal identifiers (WMI, IOCTL, etc.) in
        // customer-facing copy.
        sb.Append($"<p class=\"subtitle\">{Enc(PlainLanguageModeSummary(options))}</p>\n");
        if (isFinal && end is not null)
            sb.Append($"<p class=\"subtitle\">Test complete: {Enc(PlainLanguageStopReason(end.StopReason))}</p>\n");
        else
            sb.Append("<p class=\"subtitle\">Test in progress; this page updates automatically while the test runs.</p>\n");
        sb.Append("</header>\n");
    }

    // Short customer-facing description of the sweep mode, in place of BatteryChargeOptions.ToString's
    // internal shorthand.
    private static string PlainLanguageModeSummary(BatteryChargeOptions options)
    {
        var modeText = options.SweepMode switch
        {
            BatteryChargeSweepMode.WideSweep => "Wide Sweep (full charge curve, from a low starting charge)",
            BatteryChargeSweepMode.QuickTest => "Quick Test (top-of-charge check)",
            BatteryChargeSweepMode.PeakSweep => "Peak Sweep (mid-to-full charge curve)",
            _ => options.SweepMode.ToString()
        };
        return modeText;
    }

    // Maps the runner's internal stop-reason strings to a short customer-facing phrase, falling back to
    // the raw text for any unrecognized reason rather than silently dropping information.
    private static string PlainLanguageStopReason(string stopReason) => stopReason switch
    {
        "Battery reached 100%." => "battery reached a full charge.",
        "Battery reached 100% and was held there for 60 seconds to capture the float/top-off behavior." =>
            "battery reached a full charge and was held there briefly to capture its float/top-off behavior.",
        "Stopped automatically: charger was disconnected mid-run." => "the charger was disconnected during the test.",
        "Stopped automatically: charge current sat at zero for 20 minutes with no Battery % rise." => "charging stopped and did not resume.",
        "Stopped by operator." => "stopped by the technician.",
        // The stall-timeout minute count is configurable (BatteryChargeOptions.PeakStallStopAfter), so this
        // matches on the stable prefix rather than the full literal string.
        _ when stopReason.StartsWith("Stopped automatically: peak charge current hasn't increased") =>
            "the charge rate leveled off.",
        _ => stopReason
    };

    // ===================== Charge Curve chart (Battery %, Voltage, Charge Current, Temperature) =====================
    //
    // Layout:
    //   - Battery % is the only line on the 0-100 right axis.
    //   - Voltage: a plain, auto-scaled line on its own axis with hidden gridlines. Not pinned to a fixed
    //     range; the pack's design/nameplate voltage runs measurably below the real charge curve, so a
    //     pinned range clips. Not extrapolated: there's no logged voltage under Wide Sweep's un-run
    //     0-start% span; the pack's estimated 0% voltage is shown instead as a plain field under
    //     "Other Battery Data".
    //   - Charge Current: dashed yellow line on its own left axis, auto-scaled with a min: 0 floor (charge
    //     current can never be negative; without the floor an all-null run's single synthetic zero anchor
    //     can auto-scale to a small negative range). Never extrapolated; Wide Sweep's dotted span is a
    //     Battery % gap-fill only.
    //   - Temperature is added only when at least one sample has a TemperatureC reading (most machines
    //     don't expose a battery temp sensor). When present it shares the % axis, since it's also 0-100-ish.
    //   - Charge Rate (W) and Remaining Capacity (Wh) are on their own chart (AppendChargePowerChart); neither fits cleanly on the % or current axis.
    //   - CC/CV phase shading (final report only): boundary-marker datasets consumed by the shared
    //     BatteryReportSharedStyle.BoundaryRegionPluginJs, drawn as shaded regions with flyouts, see
    //     BuildPhaseBoundaryDatasetsJs below.
    //
    // X-axis: real elapsed seconds on a linear scale, not evenly-spaced category labels; keeps point
    // spacing honest against real time and keeps the extrapolated segment's timing honest.
    private static void AppendChargeCurveChart(StringBuilder sb, BatteryChargeOptions options,
        IReadOnlyList<BatteryChargeSample> samples, double testStartElapsed,
        BatteryChargePhaseDetector.PhaseResult? phases)
    {
        var hasExtrapolation = false;
        ExtrapolationResult? extrap = null;
        if (options.SweepMode == BatteryChargeSweepMode.WideSweep && samples.Count > 0)
        {
            extrap = BatteryChargeExtrapolation.Compute(samples);
            hasExtrapolation = !extrap.Omitted && extrap.Points.Count > 0;
        }
        var extraPoints = hasExtrapolation ? extrap!.Points : Array.Empty<ExtrapolatedPoint>();

        var hasTemp = samples.Any(s => s.TemperatureC is not null);

        // Gated on whether any sample logged a voltage reading, not on voltageRange resolving; a run
        // where voltageRange fails to resolve (e.g. WMI doesn't return DesignVoltage) can still have a
        // live voltage log. voltageRange separately drives the "Estimated 0% Voltage" info row elsewhere.
        var hasVoltage = samples.Any(s => s.VoltageV is not null);

        sb.Append("<section class=\"chart-section\">\n<h2>Charge Curve</h2>\n");
        sb.Append("<canvas id=\"chargeChart\" height=\"110\"></canvas>\n");
        sb.Append("<div class=\"chart-legend\">\n");
        sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtToggle('pct',this.checked)\"> Battery %</label>\n");
        if (hasVoltage)
            sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtToggle('volt',this.checked)\"> Voltage (V)</label>\n");
        sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtToggle('current',this.checked)\"> Charge Current (mA)</label>\n");
        // Charge Current defaults to a 60s-bucketed peak line, not a mean, to smooth real charger noise:
        // during part of the CC phase the charger duty-cycles between a high delivered-current level and a
        // much lower one every ~20s, and a mean blends those troughs in with the peaks, reading noisier and
        // lower than what the charger is actually regulating to. This checkbox swaps to the untouched raw
        // series; both variants are built server-side as separate datasets sharing wtGroup 'current', toggled
        // by wtVariant via wtSetChargeCurrentVariant.
        sb.Append("<label><input type=\"checkbox\" onchange=\"wtSetChargeCurrentVariant(this.checked)\"> Show raw, unaveraged data</label>\n");
        if (hasTemp)
            sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtToggle('temp',this.checked)\"> Temperature (°C)</label>\n");
        sb.Append("</div>\n</section>\n");

        // x = real elapsed seconds, shifted so the actual test start sits at 0. Must match
        // AppendChargePowerChart's anchor.
        var minElapsed = testStartElapsed;

        string XyLine(IEnumerable<(double Elapsed, double? Y)> pts) =>
            string.Join(",", pts.Select(p => $"{{x:{(p.Elapsed - minElapsed).ToString(CultureInfo.InvariantCulture)},y:{Num(p.Y)}}}"));

        var percentExtrapPts = extraPoints.Select(p => (p.ElapsedSeconds, (double?)p.Percent))
            .Concat(samples.Count > 0 ? new[] { (samples[0].ElapsedSeconds, samples[0].BatteryPercent) } : Array.Empty<(double, double?)>());
        // Charge Current gets a synthetic (0, 0) anchor point prepended ahead of the real samples. Without
        // it the line's first real value (already well above zero) is the leftmost point Chart.js sees, so
        // the auto-scaled left axis never includes zero and the line appears to start partway up the chart
        // instead of rising from a zero baseline.
        //
        // ChargeCurrentMa reads null (not 0) for however long the charger takes to be detected after the
        // run starts. spanGaps: true would otherwise draw a straight diagonal line from the synthetic
        // (0,0) anchor across that null stretch to the first real reading, when the true value across that
        // span is a flat 0. ZeroFillLeadingNulls turns only the leading run of nulls (before the first real
        // reading) into literal 0.0; a null occurring after the first real reading is a genuine sensor
        // dropout and still spans as a gap.
        var currentZeroFilled = ZeroFillLeadingNulls(samples, s => s.ChargeCurrentMa);
        var currentPtsRaw = new[] { (testStartElapsed, (double?)0.0) }.Concat(currentZeroFilled);
        // BucketPeakBroadcast (not BucketPeak) is used here because Chart.js's 'index' hover mode finds the
        // nearest point on whichever dataset is x-closest to the cursor, then reads the same array position
        // out of every other dataset to build the tooltip; it does not re-match by x-value per dataset.
        // BucketPeak collapses each 60s window to one output point, giving this series a much shorter array
        // than the others on the chart, which misaligns that shared-position lookup. BucketPeakBroadcast
        // keeps one entry per real sample (same length as the other series) but replaces each sample's
        // value with its window's peak, so the visual curve is identical but the point count matches.
        var currentPtsAvg = new[] { (testStartElapsed, (double?)0.0) }.Concat(BucketPeakBroadcast(currentZeroFilled));

        // percentPts/tempPts/voltPts each get a (testStartElapsed, <first sample's own value>) anchor
        // prepended; a harmless duplicate of each series' own first point, added so every dataset on this
        // chart shares the same point count and per-index alignment that 'index' hover mode requires.
        var percentPts = new[] { (testStartElapsed, samples.Count > 0 ? samples[0].BatteryPercent : null) }
            .Concat(samples.Select(s => (s.ElapsedSeconds, s.BatteryPercent)));
        var tempPts = new[] { (testStartElapsed, samples.Count > 0 ? samples[0].TemperatureC : null) }
            .Concat(samples.Select(s => (s.ElapsedSeconds, s.TemperatureC)));

        // Voltage: plotted as real voltage; the auto-scaled yVoltage axis handles scaling.
        var voltPts = new[] { (testStartElapsed, samples.Count > 0 ? samples[0].VoltageV : null) }
            .Concat(samples.Select(s => (s.ElapsedSeconds, s.VoltageV)));

        sb.Append("<script>\n");
        sb.Append("var wtDatasets = [\n");

        // Every dataset carries a wtGroup tag (an extra property Chart.js ignores for rendering, read back
        // by wtToggle below) so each legend checkbox can toggle its whole group [e.g. "volt"] by a plain
        // property match instead of parsing/guessing off the display label text.
        var datasetEntries = new List<string>();

        // Declared first so Chart.js draws it behind the % and current lines (order: 3, higher than
        // pct/current's 1/2), so the % line reads as the primary trace.
        if (hasVoltage)
        {
            datasetEntries.Add("{ type: 'line', wtGroup: 'volt', label: 'Voltage (V)', data: [" + XyLine(voltPts) +
              "], borderColor: '#3CB043', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yVoltage', order: 3 }");
        }

        datasetEntries.Add("{ type: 'line', wtGroup: 'pct', label: 'Battery %', data: [" + XyLine(percentPts) +
          "], borderColor: '#D93B3B', backgroundColor: 'rgba(217,59,59,0.08)', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yPct', order: 1 }");
        if (extraPoints.Count > 0)
            datasetEntries.Add("{ type: 'line', wtGroup: 'pct', label: 'Battery % (extrapolated)', data: [" + XyLine(percentExtrapPts) +
              "], borderColor: '#D93B3B', borderDash: [6,4], pointRadius: 0, borderWidth: 2, tension: 0, spanGaps: false, yAxisID: 'yPct', order: 1 }");

        // wtVariant distinguishes the two Charge Current series sharing wtGroup 'current', see
        // wtSetChargeCurrentVariant below. The raw dataset starts hidden (literal, not toggled at runtime)
        // since the averaged view is the default.
        datasetEntries.Add("{ type: 'line', wtGroup: 'current', wtVariant: 'avg', label: 'Charge Current (mA)', data: [" + XyLine(currentPtsAvg) +
          "], borderColor: '#E0A030', borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yCurrent', order: 2 }");
        datasetEntries.Add("{ type: 'line', wtGroup: 'current', wtVariant: 'raw', hidden: true, label: 'Charge Current (mA): raw', data: [" + XyLine(currentPtsRaw) +
          "], borderColor: '#E0A030', borderWidth: 1, tension: 0.1, spanGaps: true, yAxisID: 'yCurrent', order: 2 }");

        if (hasTemp)
            datasetEntries.Add("{ type: 'line', wtGroup: 'temp', label: 'Temperature (°C)', data: [" + XyLine(tempPts) +
              "], borderColor: '#FF4FC3', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yPct', order: 1 }");

        // CC/CV phase shading (final report only; phases is null on a live-updating run): boundary-marker
        // datasets consumed by the shared BatteryReportSharedStyle.BoundaryRegionPluginJs, registered
        // globally by AppendChartRuntimeScripts.
        datasetEntries.AddRange(BuildPhaseBoundaryDatasetsJs(phases, minElapsed));

        sb.Append(string.Join(",\n", datasetEntries));
        sb.Append("\n];\n");

        // yVoltage: a real (unnormalized), auto-scaled voltage axis with no pinned min/max, gridlines
        // hidden so it doesn't add a second visible grid crossing the chart, axis and numbers shown since
        // Voltage is a first-class plotted line. Third axis alongside yPct (right) and yCurrent (left);
        // Chart.js stacks same-side axes without needing a fourth side. Omitted when no sample logged a
        // voltage reading (hasVoltage false), same as the dataset itself.
        var voltageAxis = hasVoltage
            ? "yVoltage: { position: 'right', grid: { drawOnChartArea: false }, title: { display: true, text: 'Voltage (V)' } }, "
            : "";
        sb.Append("var wtChart = new Chart(document.getElementById('chargeChart'), { type: 'line', data: { datasets: wtDatasets }, " +
                   "options: { responsive: true, interaction: { intersect: false, mode: 'index', axis: 'x' }, elements: { point: { hitRadius: 20 } }, " +
                   "plugins: { legend: { labels: { filter: function(item, data){ return !data.datasets[item.datasetIndex].isBoundaryMarker; } } }, " +
                   "tooltip: { callbacks: { title: function(items) { return items.length ? wtFormatElapsedHms(items[0].parsed.x) : ''; } } } }, " +
                   // Boundary-marker datasets (BuildPhaseBoundaryDatasetsJs) carry an empty data: [] array
                   // and no yAxisID, so Chart.js registers its own default 'y' scale for them, which
                   // auto-ranges to 0-1 and renders as a spurious extra axis. display: false keeps the
                   // scale registered (the markers are pixel-positioned via the x scale only) but hides it.
                   "scales: { y: { display: false }, " +
                   "yPct: { min: 0, max: 100, position: 'right', title: { display: true, text: 'Battery % / Temp (°C)' } }, " +
                   "yCurrent: { min: 0, position: 'left', title: { display: true, text: 'Charge Current (mA)' } }, " +
                   voltageAxis +
                   "x: { type: 'linear', title: { display: true, text: 'Elapsed Time' }, " +
                   "ticks: { autoSkip: true, maxTicksLimit: 12, maxRotation: 0, callback: wtFormatElapsedHms } } } } });\n");
        // wtCurrentGroupVisible/wtCurrentVariant track the 'current' group's checkbox and raw/avg toggle
        // independently, so either control can flip without clobbering the other's state, unchecking
        // "Charge Current" then re-checking it restores whichever variant was selected. Every other group
        // (pct/volt/temp) uses a plain single-dataset toggle.
        sb.Append("var wtCurrentGroupVisible = true;\n");
        sb.Append("var wtCurrentVariant = 'avg';\n");
        sb.Append("function wtApplyCurrentVisibility() { " +
                   "wtChart.data.datasets.forEach(function(d){ if (d.wtGroup === 'current') d.hidden = !wtCurrentGroupVisible || d.wtVariant !== wtCurrentVariant; }); " +
                   "wtChart.update(); }\n");
        sb.Append("function wtToggle(group, visible) { " +
                   "if (group === 'current') { wtCurrentGroupVisible = visible; wtApplyCurrentVisibility(); return; } " +
                   "wtChart.data.datasets.forEach(function(d){ if (d.wtGroup === group) d.hidden = !visible; }); " +
                   "wtChart.update(); }\n");
        sb.Append("function wtSetChargeCurrentVariant(raw) { wtCurrentVariant = raw ? 'raw' : 'avg'; wtApplyCurrentVisibility(); }\n");
        sb.Append("</script>\n");
    }

    // ===================== Charge Power chart (Charge Rate W, Remaining Capacity Wh) =====================
    //
    // Neither series reads cleanly on a 0-100% axis or the Charge Current axis, and both are meaningful
    // mostly as trend lines rather than needing to align visually with Battery %. Two-axis line chart:
    // Charge Rate (W) on the left, Remaining Capacity (Wh) on the right. Charge Rate is auto-scaled with a
    // min: 0 floor (charge wattage can never be negative; without the floor an all-null run can auto-scale
    // negative). Remaining Capacity is fully auto-scaling with no floor, a partially-charged pack's
    // remaining capacity is a legitimately non-zero value, not one expected to touch zero on this chart.
    private static void AppendChargePowerChart(StringBuilder sb, BatteryChargeOptions options,
        IReadOnlyList<BatteryChargeSample> samples, double testStartElapsed, BatteryChargePhaseDetector.PhaseResult? phases)
    {
        sb.Append("<section class=\"chart-section\">\n<h2>Charge Power</h2>\n");
        sb.Append("<canvas id=\"chargePowerChart\" height=\"90\"></canvas>\n");
        sb.Append("<div class=\"chart-legend\">\n");
        // wtGroup tags (matching AppendChargeCurveChart) rather than fixed dataset indices, since Charge
        // Rate carries two datasets (avg/raw); a fixed index would silently point at the wrong dataset if
        // that count changes.
        sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtPowerToggle('rate',this.checked)\"> Charge Rate (W)</label>\n");
        sb.Append("<label><input type=\"checkbox\" checked onchange=\"wtPowerToggle('capacity',this.checked)\"> Remaining Capacity (Wh)</label>\n");
        sb.Append("<label><input type=\"checkbox\" onchange=\"wtSetChargeRateVariant(this.checked)\"> Show raw, unaveraged data</label>\n");
        sb.Append("</div>\n</section>\n");

        // Shares testStartElapsed with AppendChargeCurveChart so both charts anchor to the same wall-clock moment.
        var minElapsed = testStartElapsed;
        string XyLine(Func<BatteryChargeSample, double?> selector) =>
            string.Join(",", samples.Select(s => $"{{x:{(s.ElapsedSeconds - minElapsed).ToString(CultureInfo.InvariantCulture)},y:{Num(selector(s))}}}"));

        // Charge Rate (W) gets the same synthetic (0, 0) anchor as Charge Current on the Charge Curve
        // chart, for the same reason. Remaining Capacity is left alone; its real starting value is
        // meaningful and shouldn't visually originate from 0.
        //
        // Charge Rate defaults to a 60s bucketed average (BucketAverage) to smooth real charger power
        // alternation, same as Charge Current. Same leading-null-vs-genuine-gap handling via
        // ZeroFillLeadingNulls as Charge Current on the Charge Curve chart.
        string XyLinePts(IEnumerable<(double Elapsed, double? Y)> pts) =>
            string.Join(",", pts.Select(p => $"{{x:{(p.Elapsed - minElapsed).ToString(CultureInfo.InvariantCulture)},y:{Num(p.Y)}}}"));
        var rateZeroFilled = ZeroFillLeadingNulls(samples, s => s.ChargeRateW);
        var chargeRateAnchor = "{x:" + (testStartElapsed - minElapsed).ToString(CultureInfo.InvariantCulture) + ",y:0}";
        var chargeRatePtsRaw = chargeRateAnchor + "," + XyLinePts(rateZeroFilled);
        // BucketAverageBroadcast (not BucketAverage) keeps the same point count as every other series on
        // this chart, which 'index' hover mode requires: see the Charge Current comment in
        // AppendChargeCurveChart for the full explanation.
        var chargeRatePtsAvg = chargeRateAnchor + "," + XyLinePts(BucketAverageBroadcast(rateZeroFilled));

        sb.Append("<script>\n");
        sb.Append("var wtPowerChart = new Chart(document.getElementById('chargePowerChart'), { type: 'line', data: { datasets: [\n");
        sb.Append("{ wtGroup: 'rate', wtVariant: 'avg', label: 'Charge Rate (W)', data: [").Append(chargeRatePtsAvg)
          .Append("], borderColor: '#3CB043', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yWatts' },\n");
        sb.Append("{ wtGroup: 'rate', wtVariant: 'raw', hidden: true, label: 'Charge Rate (W) [raw', data: [").Append(chargeRatePtsRaw)
          .Append("], borderColor: '#3CB043', pointRadius: 0, borderWidth: 1, tension: 0.1, spanGaps: true, yAxisID: 'yWatts' },\n");
        // Wh, not mWh] this chart's scale reads more naturally in whole/fractional watt-hours than
        // four-to-five-digit milliwatt-hours. Other mWh figures in this report (Design/Full-Charge Capacity
        // info rows, the data table's "Cap. Remaining (mWh)" column) stay mWh, the more precise/conventional
        // unit for a point-in-time reading.
        // Prepends a (testStartElapsed, <first sample's own value>) anchor, matching the Charge Curve
        // chart's percentPts/tempPts/voltPts, so this series' point count matches the two Charge Rate
        // lines above for 'index' hover mode.
        var capacityAnchor = "{x:" + (testStartElapsed - minElapsed).ToString(CultureInfo.InvariantCulture) + ",y:" +
                              Num(samples.Count > 0 ? samples[0].RemainingCapacityMwh / 1000.0 : (double?)null) + "}";
        sb.Append("{ wtGroup: 'capacity', label: 'Remaining Capacity (Wh)', data: [").Append(capacityAnchor).Append(',')
          .Append(XyLine(s => s.RemainingCapacityMwh / 1000.0))
          .Append("], borderColor: '#B07CFF', pointRadius: 0, borderWidth: 2, tension: 0.15, spanGaps: true, yAxisID: 'yCapacity' }");
        // CC/CV phase shading (final report only): same shared boundary-marker datasets as the Charge
        // Curve chart above, appended after the real datasets. Boundary datasets carry no wtGroup, so
        // wtPowerToggle's group-based forEach (below) skips them automatically.
        foreach (var boundaryDs in BuildPhaseBoundaryDatasetsJs(phases, minElapsed))
            sb.Append(",\n").Append(boundaryDs);
        sb.Append('\n');
        sb.Append("] }, options: { responsive: true, interaction: { intersect: false, mode: 'index', axis: 'x' }, elements: { point: { hitRadius: 20 } }, " +
                   "plugins: { legend: { labels: { filter: function(item, data){ return !data.datasets[item.datasetIndex].isBoundaryMarker; } } }, " +
                   "tooltip: { callbacks: { title: function(items) { return items.length ? wtFormatElapsedHms(items[0].parsed.x) : ''; } } } }, " +
                   // Same hidden ghost 'y' scale as the Charge Curve chart above, for the boundary-marker datasets.
                   "scales: { y: { display: false }, " +
                   "yWatts: { min: 0, position: 'left', title: { display: true, text: 'Charge Rate (W)' } }, " +
                   "yCapacity: { position: 'right', grid: { drawOnChartArea: false }, title: { display: true, text: 'Remaining Capacity (Wh)' } }, " +
                   "x: { type: 'linear', title: { display: true, text: 'Elapsed Time' }, " +
                   "ticks: { autoSkip: true, maxTicksLimit: 12, maxRotation: 0, callback: wtFormatElapsedHms } } } } });\n");
        // Mirrors wtChart's wtCurrentGroupVisible/wtCurrentVariant pair above; 'rate' needs the same
        // independent group-visibility/variant-selection tracking since it carries two datasets;
        // 'capacity' stays a plain single-dataset group toggle.
        sb.Append("var wtRateGroupVisible = true;\n");
        sb.Append("var wtRateVariant = 'avg';\n");
        sb.Append("function wtApplyRateVisibility() { " +
                   "wtPowerChart.data.datasets.forEach(function(d){ if (d.wtGroup === 'rate') d.hidden = !wtRateGroupVisible || d.wtVariant !== wtRateVariant; }); " +
                   "wtPowerChart.update(); }\n");
        sb.Append("function wtPowerToggle(group, visible) { " +
                   "if (group === 'rate') { wtRateGroupVisible = visible; wtApplyRateVisibility(); return; } " +
                   "wtPowerChart.data.datasets.forEach(function(d){ if (d.wtGroup === group) d.hidden = !visible; }); " +
                   "wtPowerChart.update(); }\n");
        sb.Append("function wtSetChargeRateVariant(raw) { wtRateVariant = raw ? 'raw' : 'avg'; wtApplyRateVisibility(); }\n");
        sb.Append("</script>\n");
    }

    // ===================== Thermal (80 mm receipt) view chart data =====================
    //
    // Same series the normal charts plot (Charge Current is the 60 s peak line, Charge Rate the 60 s average,
    // both with the leading-null zero fill), on the same test-start-relative x. Battery % is series 0 in each
    // chart because the thermal script draws series 0 heavy/solid and tells the rest apart by dash pattern.
    // CC/CV become bands under the chart in place of the shaded regions and flyouts; final report only, like
    // the normal chart's shading. Long runs are decimated inside ThermalBatteryData.MakeSeries.
    private static ThermalBatteryData BuildThermalData(BatteryChargeOptions options, IReadOnlyList<BatteryChargeSample> samples,
        double testStartElapsed, BatteryChargePhaseDetector.PhaseResult? phases)
    {
        double X(double elapsed) => elapsed - testStartElapsed;
        var duration = samples.Count > 0 ? Math.Max(0.0, samples[^1].ElapsedSeconds - testStartElapsed) : 0.0;
        var hasTemp = samples.Any(s => s.TemperatureC is not null);
        var hasVoltage = samples.Any(s => s.VoltageV is not null);

        var bands = new List<ThermalBand>();
        if (phases is { } p)
        {
            if (p.CcStartElapsed is { } ccS && p.CcEndElapsed is { } ccE)
                bands.Add(new ThermalBand("Constant current", "CC", X(ccS), X(ccE), 0));
            if (p.CvStartElapsed is { } cvS && p.CvEndElapsed is { } cvE)
                bands.Add(new ThermalBand("Constant voltage", "CV", X(cvS), X(cvE), 0));
        }

        // Charge Curve: Battery % (and Temperature) on the right 0-100 axis, Charge Current on the left, Voltage
        // on a second right axis.
        var curveAxes = new List<ThermalAxis>
        {
            new("current", "left", "mA", Min: 0),
            new("pct", "right", hasTemp ? "% / \u00B0C" : "%", 0, 100)
        };
        if (hasVoltage) curveAxes.Add(new ThermalAxis("volt", "right", "V"));

        var curveSeries = new List<ThermalSeries>
        {
            ThermalBatteryData.MakeSeries("Battery %", "pct", samples.Select(s => (X(s.ElapsedSeconds), s.BatteryPercent)), 1)
        };
        if (options.SweepMode == BatteryChargeSweepMode.WideSweep && samples.Count > 0)
        {
            var extrap = BatteryChargeExtrapolation.Compute(samples);
            if (!extrap.Omitted && extrap.Points.Count > 0)
            {
                var extrapPts = extrap.Points.Select(e => (X(e.ElapsedSeconds), (double?)e.Percent))
                    .Concat(new[] { (X(samples[0].ElapsedSeconds), samples[0].BatteryPercent) });
                curveSeries.Add(ThermalBatteryData.MakeSeries("Battery % (extrapolated)", "pct", extrapPts, 1));
            }
        }
        if (hasVoltage)
            curveSeries.Add(ThermalBatteryData.MakeSeries("Voltage (V)", "volt", samples.Select(s => (X(s.ElapsedSeconds), s.VoltageV)), 3));
        var currentAvg = new[] { (testStartElapsed, (double?)0.0) }
            .Concat(BucketPeakBroadcast(ZeroFillLeadingNulls(samples, s => s.ChargeCurrentMa)));
        curveSeries.Add(ThermalBatteryData.MakeSeries("Charge Current (mA)", "current", currentAvg.Select(q => (X(q.Item1), q.Item2)), 1));
        if (hasTemp)
            curveSeries.Add(ThermalBatteryData.MakeSeries("Temperature (\u00B0C)", "pct", samples.Select(s => (X(s.ElapsedSeconds), s.TemperatureC)), 1));

        // Charge Power: Charge Rate (W) on the left (floor 0), Remaining Capacity (Wh) on the right.
        var rateAvg = new[] { (testStartElapsed, (double?)0.0) }
            .Concat(BucketAverageBroadcast(ZeroFillLeadingNulls(samples, s => s.ChargeRateW)));
        var powerSeries = new List<ThermalSeries>
        {
            ThermalBatteryData.MakeSeries("Charge Rate (W)", "watts", rateAvg.Select(q => (X(q.Item1), q.Item2)), 2),
            ThermalBatteryData.MakeSeries("Remaining Capacity (Wh)", "wh", samples.Select(s => (X(s.ElapsedSeconds), s.RemainingCapacityMwh / 1000.0)), 2)
        };

        var charts = new List<ThermalChart>
        {
            new("Charge Curve", curveAxes, curveSeries, bands),
            new("Charge Power", new List<ThermalAxis> { new("watts", "left", "W", Min: 0), new("wh", "right", "Wh") }, powerSeries, bands)
        };
        return new ThermalBatteryData("charge", duration, charts);
    }

    // ===================== Live-refresh state preservation =====================
    //
    // While a run is in progress, the report meta-refreshes every 5s to re-read the file the runner just
    // rewrote. That's a real page navigation, so scroll position and the "Full logged data" <details>
    // open/closed state reset on every refresh. Fix: stash {scroll position, details-open} to
    // sessionStorage right before the refresh navigates away (pagehide fires on a meta-refresh the same as
    // any other navigation), and restore them on load. sessionStorage is scoped per-tab and per file URL,
    // so it doesn't leak across different reports.
    private static void AppendStatePreservationScript(StringBuilder sb)
    {
        sb.Append("<script>\n(function(){\n");
        sb.Append("try {\n");
        sb.Append("  var open = sessionStorage.getItem('wtChargeDetailsOpen');\n");
        sb.Append("  var scrollY = sessionStorage.getItem('wtChargeScrollY');\n");
        sb.Append("  window.addEventListener('DOMContentLoaded', function() {\n");
        sb.Append("    var details = document.getElementById('wtDataTable');\n");
        sb.Append("    if (details && open === '1') details.open = true;\n");
        sb.Append("    if (scrollY !== null) window.scrollTo(0, parseInt(scrollY, 10) || 0);\n");
        sb.Append("  });\n");
        sb.Append("} catch (e) {}\n");
        sb.Append("window.addEventListener('pagehide', function() {\n");
        sb.Append("  try {\n");
        sb.Append("    var details = document.getElementById('wtDataTable');\n");
        sb.Append("    sessionStorage.setItem('wtChargeDetailsOpen', (details && details.open) ? '1' : '0');\n");
        sb.Append("    sessionStorage.setItem('wtChargeScrollY', String(window.scrollY || window.pageYOffset || 0));\n");
        sb.Append("  } catch (e) {}\n");
        sb.Append("});\n");
        sb.Append("})();\n</script>\n");
    }

    // Builds zero-data Chart.js dataset literals consumed by the shared boundary-region plugin
    // (BatteryReportSharedStyle.BoundaryRegionPluginJs) [one per detected CC/CV phase, drawn as a shaded
    // region with dashed start/end lines and "CC start H:MM:SS"/"CC end H:MM:SS" flyouts. CC and CV are
    // independent] a run can have only one phase detected (see BatteryChargePhaseDetector), in which case
    // the other is simply omitted.
    //
    // rangeUnit: 'elapsedSeconds' on every entry; both charts' x-axis is normalized elapsed seconds, not
    // epoch-millisecond timestamps, so the shared plugin formats these flyouts via wtFormatElapsedHms
    // rather than treating the raw number as a Date.
    private static IEnumerable<string> BuildPhaseBoundaryDatasetsJs(BatteryChargePhaseDetector.PhaseResult? phases, double minElapsed)
    {
        if (phases is not { } p) yield break;

        string? Region(double? start, double? end, string label, string color)
        {
            if (start is not { } s || end is not { } e) return null;
            var x0 = (s - minElapsed).ToString(CultureInfo.InvariantCulture);
            var x1 = (e - minElapsed).ToString(CultureInfo.InvariantCulture);
            return "{ data: [], isBoundaryMarker: true, rangeUnit: 'elapsedSeconds', " +
                   $"rangeStart: {x0}, rangeEnd: {x1}, boundaryColor: {JsStr(color)}, rangeLabel: {JsStr(label)} }}";
        }

        // Distinct from the % (red)/current (yellow)/voltage (green)/temp (pink) line colors already used
        // on these charts, and from each other; blue for CC, orange for CV, an assumed default.
        if (Region(p.CcStartElapsed, p.CcEndElapsed, "CC", "#6E9BFF") is { } cc) yield return cc;
        if (Region(p.CvStartElapsed, p.CvEndElapsed, "CV", "#FF9F43") is { } cv) yield return cv;
    }

    // Escapes a C# string for safe embedding as a single-quoted JS string literal inside an inline
    // <script> block; Enc (WebUtility.HtmlEncode) is for HTML text nodes/attributes, not JS string
    // literals, and would mangle characters like apostrophes into HTML entities instead of escaping them.
    private static string JsStr(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string Num(double? v) => v is { } d ? d.ToString(CultureInfo.InvariantCulture) : "null";

    // Buckets samples into fixed-width windows of ElapsedSeconds (default 60s) and takes the peak (max)
    // of the selected field within each window, rather than its mean: see BucketAverage below for the
    // general bucketing approach this mirrors. During part of the CC phase the charger duty-cycles between
    // a high delivered-current level and a much lower one every ~20s, with a short spike before the
    // CC→CV handoff. A mean blends the low troughs in with the high plateaus, producing a line that reads
    // noisier and lower than what the charger is actually regulating to, whereas the top edge of the raw
    // signal traces a clean, steady curve. Used for Charge Current's default (non-raw) line only; Charge
    // Rate/Watts keeps BucketAverage's true mean.
    private static IReadOnlyList<(double Elapsed, double? Y)> BucketPeak(
        IEnumerable<(double Elapsed, double? Y)> points, double bucketSeconds = 60.0)
    {
        var result = new List<(double, double?)>();
        var groups = points
            .Where(p => p.Y is not null)
            .GroupBy(p => Math.Floor(p.Elapsed / bucketSeconds))
            .OrderBy(g => g.Key);
        foreach (var g in groups)
            result.Add((g.Average(p => p.Elapsed), g.Max(p => p.Y!.Value)));
        return result;
    }

    // Buckets samples into fixed-width windows of ElapsedSeconds (default 60s) and averages the selected
    // field within each window; the noise-reduction pass behind the Charge Curve chart's Charge Current
    // series and the Charge Power chart's Charge Rate series. A real charger can alternate between two
    // delivered-power levels every few seconds rather than holding one steady rate, which reads as noise
    // at full sample resolution. Buckets are keyed by absolute elapsed time (not shifted to the chart's own
    // x=0), so window boundaries land at fixed wall-clock offsets from test start regardless of which chart
    // consumes the result; each caller applies its own x-shift afterward. A window with no non-null
    // samples is omitted (not zero-filled); spanGaps: true on the consuming line handles the gap.
    private static IReadOnlyList<(double Elapsed, double? Y)> BucketAverage(
        IEnumerable<(double Elapsed, double? Y)> points, double bucketSeconds = 60.0)
    {
        var result = new List<(double, double?)>();
        var groups = points
            .Where(p => p.Y is not null)
            .GroupBy(p => Math.Floor(p.Elapsed / bucketSeconds))
            .OrderBy(g => g.Key);
        foreach (var g in groups)
            result.Add((g.Average(p => p.Elapsed), g.Average(p => p.Y!.Value)));
        return result;
    }

    // "Broadcast" variants of BucketPeak/BucketAverage above: same 60s-window peak/mean calculation, but
    // instead of collapsing each window to a single output point, every input point keeps its own slot in
    // the output with its Y value replaced by its window's peak/average. This keeps the array the same
    // length as the chart's other series, which Chart.js's 'index' hover mode requires (see the call sites
    // in AppendChargeCurveChart/AppendChargePowerChart). A point falling in a window with no non-null
    // samples gets Y: null, spanned by spanGaps: true the same as a genuine gap.
    private static IReadOnlyList<(double Elapsed, double? Y)> BucketPeakBroadcast(
        IReadOnlyList<(double Elapsed, double? Y)> points, double bucketSeconds = 60.0)
    {
        var bucketMax = new Dictionary<double, double>();
        foreach (var p in points)
        {
            if (p.Y is not { } v) continue;
            var key = Math.Floor(p.Elapsed / bucketSeconds);
            bucketMax[key] = bucketMax.TryGetValue(key, out var cur) ? Math.Max(cur, v) : v;
        }

        var result = new List<(double, double?)>(points.Count);
        foreach (var p in points)
        {
            var key = Math.Floor(p.Elapsed / bucketSeconds);
            result.Add((p.Elapsed, bucketMax.TryGetValue(key, out var v) ? v : (double?)null));
        }
        return result;
    }

    private static IReadOnlyList<(double Elapsed, double? Y)> BucketAverageBroadcast(
        IReadOnlyList<(double Elapsed, double? Y)> points, double bucketSeconds = 60.0)
    {
        var bucketSums = new Dictionary<double, (double Sum, int Count)>();
        foreach (var p in points)
        {
            if (p.Y is not { } v) continue;
            var key = Math.Floor(p.Elapsed / bucketSeconds);
            bucketSums[key] = bucketSums.TryGetValue(key, out var acc) ? (acc.Sum + v, acc.Count + 1) : (v, 1);
        }

        var result = new List<(double, double?)>(points.Count);
        foreach (var p in points)
        {
            var key = Math.Floor(p.Elapsed / bucketSeconds);
            result.Add((p.Elapsed, bucketSums.TryGetValue(key, out var acc) ? acc.Sum / acc.Count : (double?)null));
        }
        return result;
    }

    // Turns only the leading run of nulls for a selected field (before its first non-null reading) into
    // literal 0.0: see the call sites on Charge Current/Charge Rate. A charger not yet detected as
    // delivering current reads as null, not 0, but that represents a real "nothing flowing yet" 0, not a
    // gap to interpolate across. A null occurring after the first real reading is a genuine sensor dropout
    // and is left untouched, still rendering as a spanned gap.
    private static IReadOnlyList<(double Elapsed, double? Y)> ZeroFillLeadingNulls(
        IReadOnlyList<BatteryChargeSample> samples, Func<BatteryChargeSample, double?> selector)
    {
        var result = new List<(double, double?)>(samples.Count);
        var seenReal = false;
        foreach (var s in samples)
        {
            var y = selector(s);
            if (y is not null) seenReal = true;
            result.Add((s.ElapsedSeconds, seenReal ? y : 0.0));
        }
        return result;
    }

    // ===================== Quick stats =====================

    private static void AppendQuickStats(StringBuilder sb, BatteryChargeStartSnapshot start, BatteryChargeEndSnapshot? end,
        IReadOnlyList<BatteryChargeSample> samples, double? health, BatteryChargeRating rating, bool isFinal)
    {
        var lastCapMwh = end?.RemainingCapacityMwh ?? (samples.Count > 0 ? samples[^1].RemainingCapacityMwh : null) ?? start.RemainingCapacityMwh;
        double? energyInWh = start.RemainingCapacityMwh is { } startMwh && lastCapMwh is { } curMwh
            ? (curMwh - startMwh) / 1000.0 : null;

        var chemistry = end?.Chemistry ?? start.Chemistry;
        var deviceName = end?.DeviceName ?? start.DeviceName;
        var battSerial = end?.BatterySerialNumber ?? start.BatterySerialNumber;
        var sysSerial = end?.SystemSerialNumber ?? start.SystemSerialNumber;

        sb.Append("<section class=\"summary-blocks\">\n");
        // Combined into one label with two stacked value lines rather than two separate blocks, so a
        // disagreement between the two figures is visible instead of hidden behind one verdict. BMS-
        // Reported is the pack's own lifetime capacity-fade figure (BatteryHealthReader: FullChargeCapacity
        // ÷ DesignCapacity, cross-checked across WMI/IOCTL/LHM) [it doesn't change run to run. Observed is
        // this run's own efficiency-ratio result (rating.Ratio) expressed as a percentage so the two
        // numbers sit on a comparable scale; it reads "Pending" while the run is still live (grading only
        // happens on the final tick] see BatteryChargeHealthAlgorithm.Pending) since it measures how this
        // specific charge compared to the expected curve, not lifetime capacity fade; the two can
        // legitimately disagree, which is why both are shown.
        var bmsLine = health is { } h ? $"{h:0.0}% (BMS-Reported)" : "N/A (BMS-Reported)";
        var observedValue = !isFinal
            ? "Pending"
            : rating switch
            {
                { Basis.InconclusiveGateFired: true, Ratio: { } r } => $"{r * 100:0}% (would-be)",
                { Tier: BatteryChargeConditionTier.Unknown or BatteryChargeConditionTier.Inconclusive } => "N/A",
                { Ratio: { } r } => $"{r * 100:0}%",
                _ => "N/A"
            };
        AppendTwoLineBlock(sb, "Battery Health", bmsLine, $"{observedValue} (Observed)", "neutral");
        AppendBlock(sb, "Energy Put Into Battery", energyInWh is { } e ? $"{e:0.00} Wh" : "N/A", "green");
        AppendBlock(sb, "Chemistry", BatteryReportSharedStyle.FriendlyChemistryLabel(chemistry), "neutral");
        AppendBlock(sb, "Device Name", FmtStr(deviceName), "neutral");
        AppendBlock(sb, "Battery Serial Number", FmtStr(battSerial), "neutral");
        AppendBlock(sb, "System Serial Number", FmtStr(sysSerial), "neutral");
        sb.Append("</section>\n");
    }

    // ===================== Factory vs. Actual table (informational reference only) =====================

    private static void AppendFactoryVsActualTable(StringBuilder sb, FactoryFigures factory, MeasuredFigures measured)
    {
        sb.Append("<section class=\"battery-info\">\n<h2>Factory vs. Observed</h2>\n");
        sb.Append("<table class=\"xval-table\">\n<tr><th></th><th>Charge Current</th><th>Charge Watts</th><th>C-rating</th></tr>\n");
        sb.Append("<tr><td>Factory</td>")
          .Append($"<td>{Fmt(factory.CurrentA, "0.00")} A</td>")
          .Append($"<td>{Fmt(factory.WattsW, "0.0")} W</td>")
          .Append($"<td>{Fmt(factory.CRating, "0.00")}C</td></tr>\n");

        sb.Append("<tr><td>Measured (peak)</td>")
          .Append(ShadedCell(measured.CurrentA, factory.CurrentA, "0.00", "A"))
          .Append(ShadedCell(measured.WattsW, factory.WattsW, "0.0", "W"))
          .Append(ShadedCell(measured.CRating, factory.CRating, "0.00", "C", crating: true))
          .Append("</tr>\n");
        sb.Append("</table>\n</section>\n");
    }

    private static string ShadedCell(double? measured, double? factory, string format, string suffix, bool crating = false)
    {
        if (measured is not { } m) return "<td class=\"na\">N/A</td>";
        var valueText = crating ? $"{m.ToString(format, CultureInfo.InvariantCulture)}{suffix}" : $"{m.ToString(format, CultureInfo.InvariantCulture)} {suffix}";
        if (factory is not { } f || f <= 0) return $"<td>{Enc(valueText)}</td>";

        var deviationPercent = Math.Abs(m - f) / f * 100.0;
        var cls = deviationPercent <= ShadeGreenPercent ? "shade-green"
            : deviationPercent <= ShadeAmberPercent ? "shade-amber"
            : "shade-red";
        return $"<td class=\"{cls}\">{Enc(valueText)}</td>";
    }

    // ===================== Any other battery data =====================

    private static void AppendOtherBatteryData(StringBuilder sb, BatteryChargeOptions options, BatteryChargeStartSnapshot start,
        BatteryChargeEndSnapshot? end, IReadOnlyList<BatteryChargeSample> samples, BatteryVoltageRangeResult? voltageRange,
        BatteryChargeRating rating, bool isFinal)
    {
        var cycleCount = end?.CycleCount ?? start.CycleCount;
        var degradationBms = end?.EndingDegradationPercent ?? start.StartingDegradationPercent;
        var designMwh = start.DesignCapacityMwh;
        var fullChargeMwhBms = end?.FullChargeCapacityMwh ?? start.FullChargeCapacityMwh;
        var manufacturer = end?.ManufacturerName ?? start.ManufacturerName;
        var peakCurrentMa = end?.PeakChargeCurrentMa;
        var peakRateW = end?.PeakChargeRateW;

        // Observed Degradation/Full-Charge Capacity: derived from rating.Ratio (this run's own efficiency
        // ratio against the self-calibrating expected curve: the same figure AppendQuickStats' "Battery
        // Health" Observed line already shows), not from the BMS's lifetime capacity-fade figure. Only
        // meaningful once the run is graded (isFinal) and Ratio actually resolved; otherwise both rows
        // render N/A rather than a stale/misleading number.
        var observedHealthPercent = isFinal && rating.Ratio is { } r ? r * 100.0 : (double?)null;
        var degradationObserved = observedHealthPercent is { } oh ? 100.0 - oh : (double?)null;
        var fullChargeMwhObserved = observedHealthPercent is { } oh2 && designMwh is { } dm2
            ? dm2 * (oh2 / 100.0) : (double?)null;

        sb.Append("<section class=\"battery-info\">\n<h2>Other Battery Data</h2>\n<table class=\"info-table\">\n");
        AppendInfoRow(sb, "Manufacturer", manufacturer);
        AppendInfoRow(sb, "Cycle Count", cycleCount?.ToString(CultureInfo.InvariantCulture));
        AppendInfoRow(sb, "Design Capacity", designMwh is { } dm ? $"{dm:0} mWh" : null);
        AppendInfoRow(sb, "Degradation Level (BMS)", degradationBms is { } d ? $"{d:0.0}%" : null);
        AppendInfoRow(sb, "Full-Charge Capacity (BMS)", fullChargeMwhBms is { } fm ? $"{fm:0} mWh" : null);
        AppendInfoRow(sb, "Degradation Level (Observed)", degradationObserved is { } dob ? $"{dob:0.0}%" : null);
        AppendInfoRow(sb, "Full-Charge Capacity (Observed)", fullChargeMwhObserved is { } fob ? $"{fob:0} mWh" : null);
        AppendInfoRow(sb, "Peak Charge Current", peakCurrentMa is { } pc ? $"{pc:0} mA" : null);
        AppendInfoRow(sb, "Peak Charge Rate", peakRateW is { } pr ? $"{pr:0.00} W" : null);
        // The pack's estimated 0% voltage (per-cell-count cutoff: see BatteryVoltageRangeReader), shown as
        // a plain data field: there's no logged voltage under an un-run 0-start% span to justify a chart bar.
        AppendInfoRow(sb, "Estimated 0% Voltage", voltageRange?.MinVoltageV is { } v0 ? $"{v0:0.00} V" : null);
        AppendInfoRow(sb, "Test Type", PlainLanguageModeSummary(options));
        sb.Append("</table>\n</section>\n");
    }

    // ===================== Full logged data =====================

    private static void AppendDataTable(StringBuilder sb, IReadOnlyList<BatteryChargeSample> samples)
    {
        // id="wtDataTable" is what the state-preservation script reads/restores open/closed state for: a
        // running test's report meta-refreshes every 5s, which is a real page navigation and would otherwise
        // reset the "Full logged data" details panel to closed on every refresh.
        sb.Append("<section class=\"data-table\">\n<details id=\"wtDataTable\">\n<summary>Full logged data (")
          .Append(samples.Count).Append(" samples)</summary>\n<table>\n");
        sb.Append("<tr><th>Elapsed</th><th>Time</th><th>Batt %</th><th>Voltage (V)</th>" +
                   "<th>Charge Current (mA)</th><th>Charge Rate (W)</th><th>Cap. Remaining (mWh)</th><th>Temp (C)</th></tr>\n");
        foreach (var s in samples)
        {
            sb.Append("<tr>")
              .Append($"<td>{Enc(FormatDuration(TimeSpan.FromSeconds(s.ElapsedSeconds)))}</td>")
              .Append($"<td>{Enc(s.Timestamp.ToString("HH:mm:ss"))}</td>")
              .Append($"<td>{Fmt(s.BatteryPercent, "0.0")}</td>")
              // 0.000 precision (not 0.00) to show the mV digit LHM/WMI actually report (e.g. 15.899, not
              // 15.90); the same precision the trickle-charge detection above relies on.
              .Append($"<td>{Fmt(s.VoltageV, "0.000")}</td>")
              .Append($"<td>{Fmt(s.ChargeCurrentMa, "0.0")}</td>")
              .Append($"<td>{Fmt(s.ChargeRateW, "0.00")}</td>")
              .Append($"<td>{Fmt(s.RemainingCapacityMwh, "0")}</td>")
              .Append($"<td>{Fmt(s.TemperatureC, "0.0")}</td>")
              .Append("</tr>\n");
        }
        sb.Append("</table>\n</details>\n</section>\n");
    }

    // ===================== Shared helpers =====================

    private static void AppendInfoRow(StringBuilder sb, string label, string? value)
    {
        var known = !string.IsNullOrWhiteSpace(value);
        sb.Append("<tr><td class=\"info-label\">").Append(Enc(label)).Append("</td><td class=\"")
          .Append(known ? "info-value" : "info-value na").Append("\">")
          .Append(known ? Enc(value!) : "N/A").Append("</td></tr>\n");
    }

    // Renders one Quick Stats block (label/value/color, optional sub-line), mirroring BatteryDrainReportBuilder.AppendBlock's signature and markup exactly.
    private static void AppendBlock(StringBuilder sb, string label, string value, string colorClass, string? subLine = null)
    {
        sb.Append($"<div class=\"block block-{colorClass}\"><div class=\"block-label\">{Enc(label)}</div>" +
                  $"<div class=\"block-value\">{Enc(value)}</div>");
        if (subLine is not null)
            sb.Append($"<div class=\"block-sub\">{Enc(subLine)}</div>");
        sb.Append("</div>\n");
    }

    // Two-value-line variant of AppendBlock: one label, two stacked value lines instead of one value plus
    // an optional sub-line; used for Battery Health, where BMS-Reported and Observed are both first-class
    // figures rather than one being a caption on the other.
    private static void AppendTwoLineBlock(StringBuilder sb, string label, string line1, string line2, string colorClass)
    {
        sb.Append($"<div class=\"block block-{colorClass}\"><div class=\"block-label\">{Enc(label)}</div>")
          .Append("<div class=\"block-value-lines\">")
          .Append($"<div class=\"block-value-line\">{Enc(line1)}</div>")
          .Append($"<div class=\"block-value-line\">{Enc(line2)}</div>")
          .Append("</div></div>\n");
    }

    private static string FmtStr(string? v) => string.IsNullOrWhiteSpace(v) ? "N/A" : v;

    private static string Fmt(double? v, string format) => v is { } d ? d.ToString(format, CultureInfo.InvariantCulture) : "N/A";

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}"
            : $"{d.Minutes:00}:{d.Seconds:00}";

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    // Light theme, matching HtmlReportGenerator's regular diagnostic report. .card/.kv-table/.activation-*/
    // .na come from BatteryReportSharedStyle.SystemInfoCss (appended alongside this in Render).
    private const string Css = @"
* { box-sizing:border-box; }
body { background:#F4F5F7; color:#1C1C1F; font-family:Segoe UI,Arial,sans-serif; margin:0; padding:24px; }
.report-header { background:#1C1C1F; color:#FFFFFF; padding:24px; border-radius:8px; margin-bottom:20px; }
.report-header h1 { margin:0 0 4px 0; font-size:24px; }
.subtitle { color:#C7C9CF; margin:0 0 4px 0; }
.condition-banner { text-align:center; padding:16px; border-radius:8px; margin-bottom:20px; border:1px solid #E1E3E8; background:#FFFFFF; }
.condition-banner h1 { margin:0; font-size:26px; letter-spacing:.04em; }
.cond-green { background:#E7F5EC; } .cond-green h1 { color:#1E7A46; }
.cond-amber { background:#FBF1DC; } .cond-amber h1 { color:#9C6B10; }
.cond-red { background:#FBEAE8; } .cond-red h1 { color:#B23A32; }
.cond-neutral { background:#F4F5F7; } .cond-neutral h1 { color:#6B6B72; font-size:18px; }
.rating-basis { background:#FFFFFF; border:1px solid #E1E3E8; border-radius:8px; padding:20px; margin-bottom:20px; }
.rating-basis h2 { margin-top:0; font-size:17px; }
.summary-blocks { display:flex; flex-wrap:wrap; gap:12px; margin-bottom:20px; }
.block { flex:1 1 150px; border-radius:8px; padding:14px; background:#FFFFFF; border:1px solid #E1E3E8; }
.block-label { font-size:11px; color:#6B6B72; text-transform:uppercase; letter-spacing:.03em; margin-bottom:6px; }
.block-value { font-size:18px; font-weight:700; color:#1C1C1F; }
.block-green .block-value { color:#1E7A46; }
.block-neutral .block-value { color:#1C1C1F; }
.block-value-lines { display:flex; flex-direction:column; gap:3px; }
.block-value-line { font-size:15px; font-weight:700; color:#1C1C1F; }
.block-green .block-value-line { color:#1E7A46; }
.block-neutral .block-value-line { color:#1C1C1F; }
.chart-section, .data-table, .battery-info { background:#FFFFFF; border:1px solid #E1E3E8; border-radius:8px; padding:20px; margin-bottom:20px; }
.chart-section h2, .data-table h2, .battery-info h2 { margin-top:0; font-size:17px; }
.chart-legend { display:flex; flex-wrap:wrap; gap:14px; margin-top:10px; font-size:12px; color:#4A4A52; }
.chart-legend label { display:flex; align-items:center; gap:5px; cursor:pointer; }
.section-sub { color:#6B6B72; font-size:12px; margin:-6px 0 12px 0; }
table { border-collapse:collapse; width:100%; font-size:12px; margin-top:10px; }
th, td { border:1px solid #E1E3E8; padding:5px 9px; text-align:right; }
th:first-child, td:first-child { text-align:left; }
th { background:#F4F5F7; font-size:11px; text-transform:uppercase; opacity:.7; }
.info-table { width:auto; min-width:340px; border:none; }
.info-table td { text-align:left; border:none; }
.info-label { color:#6B6B72; width:260px; }
.info-value { font-weight:600; }
.info-value.na { color:#9A9AA2; font-weight:400; font-style:italic; }
.na { color:#9A9AA2; font-style:italic; }
.xval-table { width:100%; }
.shade-green { background:#DCEFDC; }
.shade-amber { background:#FBEBCF; }
.shade-red { background:#F6D6D6; }
.report-footer { color:#8A8A93; font-size:11px; margin-top:20px; }
";
}
