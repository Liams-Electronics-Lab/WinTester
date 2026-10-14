using System;
using System.IO;
using System.Net;
using System.Text;
using WinTester.Branding;
using WinTester.Core.Activation;

namespace WinTester.Reporting;

// Shared building blocks for the Battery Charge and Battery Drain HTML report builders, pulled out here
// (rather than duplicated in WinTester.Tools.BatteryCharge.BatteryChargeReportBuilder and
// WinTester.Tools.BatteryDrain.BatteryDrainReportBuilder) so print behavior, brand plumbing, and the
// chemistry-code display mapping stay in one place as both reports evolve.
//
// Print/PDF is a first-class requirement here, not an afterthought: shops print or export these reports
// to PDF for the customer, so PrintCss overrides colors/backgrounds/layout for @media print instead of
// relying on the browser's "print background graphics" toggle (default off in most browsers, which would
// silently discard every dark-theme color). Chart.js print gotchas handled: (1) a canvas has no intrinsic
// print size [every chart canvas already carries an explicit height attribute, and this stylesheet adds
// a max-width/height constraint so it never overflows the page; (2) Chart.js renders to a 2D bitmap,
// which prints fine as long as it isn't hidden/zero-sized] this stylesheet never sets display: none on
// chart sections for print, only color/background overrides.
public static class BatteryReportSharedStyle
{
    // ===================== Shared chart runtime helpers =====================
    //
    // Shared chart plumbing (elapsed-time tick formatting, a mouse-following crosshair, fixed vertical
    // section markers) for both report builders' charts. Any future chart in either report gets all three
    // for free just by calling AppendChartRuntimeScripts once.

    // H:MM:SS, always showing the hour digit (unpadded; "0", "1", "12", never "00"/"01") with minutes/
    // seconds zero-padded.
    public static string FormatElapsedHms(TimeSpan d) =>
        $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}";

    // Emits, once per report: (1) the JS-side equivalent of FormatElapsedHms for charts with a real numeric
    // (elapsed-seconds) x-axis rather than pre-formatted string labels, (2) a global Chart.js plugin that
    // draws a faint dashed vertical line at the mouse position on whichever chart is being hovered, and (3)
    // the shared boundary-region plugin (BoundaryRegionPluginJs below); registered here too so any chart
    // built via this method (currently both battery report builders) gets shaded-region/flyout markers for
    // free just by adding boundary-marker datasets, with no separate registration step. Chart.register with
    // no chart-type key applies globally to every Chart instance created afterward in this page, so this
    // only needs to run once per report, right after the vendored Chart.js <script> tag is emitted (see
    // both Render() methods).
    public static void AppendChartRuntimeScripts(StringBuilder sb)
    {
        sb.Append("<script>\n");
        sb.Append("function wtFormatElapsedHms(value) { " +
                   "var s = Math.max(0, Math.round(value)); var h = Math.floor(s/3600), m = Math.floor((s%3600)/60), sec = s%60; " +
                   "return h + ':' + String(m).padStart(2,'0') + ':' + String(sec).padStart(2,'0'); }\n");
        sb.Append("Chart.register({ id: 'wtCrosshair', afterDraw: function(chart) {\n");
        sb.Append("  var active = chart.tooltip && chart.tooltip._active;\n");
        sb.Append("  if (!active || !active.length) return;\n");
        sb.Append("  var x = active[0].element.x, area = chart.chartArea, ctx = chart.ctx;\n");
        sb.Append("  ctx.save(); ctx.beginPath(); ctx.moveTo(x, area.top); ctx.lineTo(x, area.bottom);\n");
        sb.Append("  ctx.lineWidth = 1; ctx.strokeStyle = 'rgba(110,110,120,0.45)'; ctx.setLineDash([4,4]); ctx.stroke(); ctx.restore();\n");
        sb.Append("} });\n");
        sb.Append("</script>\n");
        sb.Append("<script>\n").Append(BoundaryRegionPluginJs).Append("\n</script>\n");
    }

    // ===================== Shared boundary-region plugin =====================
    //
    // Generalized from HtmlReportGenerator's own 'testBoundaryPlugin' (formerly private to that file,
    // formerly keyed off test-specific dataset fields; testStart/testEnd/testFailed/testDisplayName) so
    // both the main diagnostic report's per-test shading AND the Battery Charge report's CC/CV phase
    // shading share one implementation instead of two diverging ones. Draws a colored shaded region
    // between a start/end x position with dashed boundary lines, then small flyout labels reading
    // "<name> start H:MM:SS" / "<name> end H:MM:SS" (or "<name> FAILED H:MM:SS" when rangeFailed): same
    // visual as the original, just reading generic field names off any dataset that opts in.
    //
    // Field shape a caller's dataset needs (all read off the Chart.js dataset object itself, alongside a
    // real `data` array, empty for a pure marker, same as before): isBoundaryMarker (true to opt in),
    // rangeStart/rangeEnd (numeric x-axis positions; whatever units that chart's own x-axis already uses),
    // boundaryColor, rangeLabel (bare name shown in the flyout, e.g. "CC" or "GPU Stress"), and optionally
    // rangeFailed (colors the end flyout red and swaps its wording to "FAILED").
    //
    // rangeUnit: the one real generalization needed beyond a plain field rename. The main report's charts
    // use epoch-millisecond x values (real Date.now()-style timestamps), formatted for the flyout via
    // toLocaleTimeString(); the Battery Charge report's charts use normalized elapsed-seconds x values
    // (see BatteryChargeReportBuilder), formatted via wtFormatElapsedHms (emitted above by
    // AppendChartRuntimeScripts). A dataset with rangeUnit: 'elapsedSeconds' gets the latter; anything else
    // (including datasets that omit the field entirely, e.g. the main report's) keeps the original epoch-ms
    // behavior; so this plugin still renders identically for the main report's existing per-test markers.
    public const string BoundaryRegionPluginJs = """
        (function(){
          window.__wtCharts = window.__wtCharts || {};
          window.wtToggleBoundary = function(canvasId, datasetIndex, checked) {
            var chart = window.__wtCharts[canvasId];
            if (!chart) return;
            chart.getDatasetMeta(datasetIndex).hidden = !checked;
            chart.update();
          };

          // Caps a linear axis's drawn ticks at trueTotal even when scales.y.max is set higher to leave
          // headroom above the line so a maxed-out reading stays visible instead of clipping at the top.
          // Without this, Chart.js would also draw + label a tick at the headroom-inflated max, which reads
          // as a wrong/confusing capacity. Used by the RAM and VRAM charts (see HtmlReportGenerator).
          window.wtCapAxisTicks = function(trueTotal) {
            return function(axis) {
              axis.ticks = (axis.ticks || []).filter(function(t) { return t.value <= trueTotal + 1e-6; });
            };
          };

          function fmtRangeTime(ds, value) {
            if (ds.rangeUnit === 'elapsedSeconds') {
              return (typeof wtFormatElapsedHms === 'function') ? wtFormatElapsedHms(value) : String(value);
            }
            return new Date(value).toLocaleTimeString();
          }

          // Draws one small pill-shaped flyout label anchored to (x, y), horizontally centered on x and
          // vertically stacked downward by `row` slots; used so a start and end flyout close together
          // (or two different ranges' flyouts) don't just overlap into an unreadable smear.
          function drawFlyout(ctx, x, top, row, text, color) {
            var padX = 6, padY = 3, lineH = 14;
            var y = top + 4 + row * (lineH + 4);
            ctx.font = '11px "Segoe UI", Arial, sans-serif';
            var w = ctx.measureText(text).width + padX * 2;
            var left = Math.min(Math.max(x - w / 2, 2), ctx.canvas.width / (window.devicePixelRatio || 1) - w - 2);
            ctx.save();
            ctx.fillStyle = color;
            ctx.strokeStyle = 'rgba(0,0,0,0.15)';
            ctx.lineWidth = 1;
            if (ctx.roundRect) { ctx.beginPath(); ctx.roundRect(left, y, w, lineH + padY, 3); ctx.fill(); ctx.stroke(); }
            else { ctx.fillRect(left, y, w, lineH + padY); ctx.strokeRect(left, y, w, lineH + padY); }
            ctx.fillStyle = '#FFFFFF';
            ctx.textBaseline = 'middle';
            ctx.fillText(text, left + padX, y + (lineH + padY) / 2);
            ctx.restore();
          }

          var wtBoundaryRegionPlugin = {
            id: 'wtBoundaryRegionPlugin',
            beforeDatasetsDraw: function(chart) {
              var xScale = chart.scales.x;
              if (!xScale) return;
              var ctx = chart.ctx;
              var top = chart.chartArea.top, bottom = chart.chartArea.bottom;
              chart.data.datasets.forEach(function(ds, i) {
                if (!ds.isBoundaryMarker || ds.rangeStart == null || ds.rangeEnd == null) return;
                var meta = chart.getDatasetMeta(i);
                if (meta.hidden) return;
                var xStart = xScale.getPixelForValue(ds.rangeStart);
                var xEnd = xScale.getPixelForValue(ds.rangeEnd);
                var left = Math.min(xStart, xEnd), width = Math.max(1, Math.abs(xEnd - xStart));
                ctx.save();
                ctx.fillStyle = (ds.boundaryColor || '#6E6E6E') + '22';
                ctx.fillRect(left, top, width, bottom - top);
                ctx.strokeStyle = ds.boundaryColor || '#6E6E6E';
                ctx.lineWidth = 1.5;
                ctx.setLineDash([4, 3]);
                [xStart, xEnd].forEach(function(x) {
                  ctx.beginPath();
                  ctx.moveTo(x, top);
                  ctx.lineTo(x, bottom);
                  ctx.stroke();
                });
                ctx.restore();
              });
            },
            afterDatasetsDraw: function(chart) {
              var xScale = chart.scales.x;
              if (!xScale) return;
              var ctx = chart.ctx;
              var top = chart.chartArea.top;
              var row = 0;
              chart.data.datasets.forEach(function(ds, i) {
                if (!ds.isBoundaryMarker || ds.rangeStart == null || ds.rangeEnd == null) return;
                var meta = chart.getDatasetMeta(i);
                if (meta.hidden) return;
                var color = ds.boundaryColor || '#6E6E6E';
                var name = ds.rangeLabel || ds.label || 'Range';
                var xStart = xScale.getPixelForValue(ds.rangeStart);
                var xEnd = xScale.getPixelForValue(ds.rangeEnd);
                drawFlyout(ctx, xStart, top, row, name + ' start ' + fmtRangeTime(ds, ds.rangeStart), color);
                var endLabel = ds.rangeFailed ? (name + ' FAILED ' + fmtRangeTime(ds, ds.rangeEnd)) : (name + ' end ' + fmtRangeTime(ds, ds.rangeEnd));
                drawFlyout(ctx, xEnd, top, row, endLabel, ds.rangeFailed ? '#B23A32' : color);
                row++;
              });
            }
          };
          Chart.register(wtBoundaryRegionPlugin);
        })();
        """;

    // Appended inside the report's single <style> block, after the screen-theme CSS, so @media print
    // rules win by cascade order without needing !important on every property.
    public const string PrintCss = @"
@media print {
  @page { margin: 14mm 12mm; }
  html, body { background: #FFFFFF !important; color: #101012 !important; padding: 0 !important; margin: 0 !important; font-size: 11pt; }
  /* Most browsers default 'print background graphics' off, which would make dark-themed cards render
     white-on-white (invisible text); forcing a light background keeps the report legible either way. */
  .report-header, .report-footer, .chart-section, .data-table, .battery-info, .cross-validation,
  .summary-blocks, .block, .rating-basis, .condition-banner { background: #FFFFFF !important; }
  .block, .chart-section, .data-table, .battery-info, .cross-validation, .rating-basis {
    border: 1px solid #B9B9C0 !important; box-shadow: none !important;
    break-inside: avoid; page-break-inside: avoid;
  }
  /* Condition banner colors kept but darkened/desaturated, with a border, so GOOD/OK/BAD/INCONCLUSIVE
     still reads on a grayscale printer, where hue alone collapses to similar mid-gray tones, border +
     bold label carries the meaning in grayscale, color is a bonus on a color printer. */
  .condition-banner { border: 2px solid #101012 !important; break-inside: avoid; page-break-inside: avoid; }
  .cond-green h1 { color: #1B6E1B !important; } .cond-amber h1 { color: #8A5A00 !important; }
  .cond-red h1 { color: #A31414 !important; } .cond-neutral h1 { color: #4A4A52 !important; }
  h1, h2, h3 { color: #101012 !important; break-after: avoid; page-break-after: avoid; }
  .subtitle, .section-sub, .info-label, .block-label { color: #4A4A52 !important; }
  table { break-inside: auto; }
  tr, .block, canvas { break-inside: avoid; page-break-inside: avoid; }
  thead, tr:first-child { break-after: avoid; page-break-after: avoid; }
  th, td { border-color: #B9B9C0 !important; color: #101012 !important; font-family: 'Consolas','Courier New',monospace; }
  th { background: #EEEEF1 !important; }
  .shade-green { background: #DCEFDC !important; } .shade-amber { background: #FBEBCF !important; }
  .shade-red { background: #F6D6D6 !important; }
  .block-green .block-value { color: #1B6E1B !important; } .block-amber .block-value { color: #8A5A00 !important; }
  .block-red .block-value { color: #A31414 !important; } .block-neutral .block-value { color: #101012 !important; }
  .na, .info-value.na, .na-block { color: #6A6A72 !important; }
  canvas { max-width: 100% !important; height: 240px !important; }
  .chart-legend, details > summary { color: #4A4A52 !important; }
  /* 'Full logged data' is collapsed by default (a <details> element); print doesn't force it open, so
     it contributes only its closed summary line to the page count, not every logged sample. */
  .report-brand-footer { color: #6A6A72 !important; border-top: 1px solid #B9B9C0 !important; }
  a { color: #101012 !important; text-decoration: underline; }
}
";

    // Chemistry FourCC codes mapped to a friendly display label; cosmetic only. The raw code still drives
    // BatteryChargeHealthAlgorithm's chemistry-family branch; this mapping is display-only and never feeds
    // back into that classification.
    public static string FriendlyChemistryLabel(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode)) return "Unknown";
        var code = rawCode.Trim().ToUpperInvariant();
        return code switch
        {
            "LION" or "LI-I" or "LIP" or "LI" => "Li-Ion",
            "LIFE" => "Lithium Iron Phosphate",
            "NIMH" => "Nickel-Metal Hydride",
            "NICD" => "Nickel-Cadmium",
            "PBAC" => "Lead Acid",
            _ => rawCode // unknown code: show verbatim rather than guess
        };
    }

    // Header brand strip; logo (if resolvable) plus company name, rendered above the report's title.
    // brand is optional: call sites that predate brand plumbing keep rendering with no strip at all,
    // rather than a broken image reference or empty bar.
    public static void AppendBrandHeader(StringBuilder sb, BrandProfile? brand)
    {
        if (brand is null) return;

        sb.Append("<div class=\"report-brand-header\">");
        var logoDataUri = TryLoadLogoDataUri(brand.LogoPath);
        if (logoDataUri is not null)
            sb.Append($"<img class=\"report-brand-logo\" src=\"{logoDataUri}\" alt=\"\" />");
        sb.Append($"<span class=\"report-brand-name\">{Enc(brand.CompanyName)}</span>");
        sb.Append("</div>\n");

        // Optional business-details block, shared by every report built on this header:
        // Battery Charge, Battery Drain and Keyboard Tester.
        BusinessDetailsRenderer.Append(sb, brand.BusinessDetails);
    }

    // Footer disclaimer/attribution line; BrandProfile.ReportFooterText, per the requirement that
    // footer/disclaimer text be brand-driven, never hardcoded. Falls back to a generic WinTester line when
    // no brand was supplied, matching each report builder's prior hardcoded footer exactly so behavior is
    // unchanged for callers not yet passing a brand.
    public static void AppendBrandFooter(StringBuilder sb, BrandProfile? brand, string fallbackText)
    {
        var text = brand?.ReportFooterText;
        sb.Append("<footer class=\"report-footer report-brand-footer\"><p>")
          .Append(Enc(string.IsNullOrWhiteSpace(text) ? fallbackText : text))
          .Append("</p></footer>\n");
    }

    // Reads the brand's logo file into an inline data: URI so the report stays a single self-contained
    // file rather than referencing an external path that may not travel with an emailed/copied report.
    // Silently omits the logo (null) on any read failure, missing file, or unrecognized extension, a
    // missing logo must never break report generation.
    private static string? TryLoadLogoDataUri(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath)) return null;
        try
        {
            var fullPath = System.IO.Path.IsPathRooted(logoPath)
                ? logoPath
                : System.IO.Path.Combine(System.AppContext.BaseDirectory, logoPath);
            if (!System.IO.File.Exists(fullPath)) return null;

            var mime = System.IO.Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                _ => null
            };
            if (mime is null) return null;

            var bytes = System.IO.File.ReadAllBytes(fullPath);
            return $"data:{mime};base64,{System.Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    // Shared screen-CSS for the brand header/footer strip: appended alongside each report's own screen
    // CSS (small enough not to warrant its own file).
    public const string BrandScreenCss = @"
.report-brand-header { display:flex; align-items:center; gap:10px; margin-bottom:10px; }
.report-brand-logo { height:28px; width:auto; max-width:160px; object-fit:contain; }
.report-brand-name { font-size:13px; color:#9A9AA2; letter-spacing:.02em; }
.report-brand-footer { margin-top:6px; }
" + BusinessDetailsRenderer.Css + @"
";

    // Shared "System Information" section [one card with two subsections (System Identity, Windows
    // Activation)] used by all three report builders (regular diagnostic report, Battery Drain, Battery
    // Charge) so a technician sees the same information in the same place regardless of which report
    // they're looking at. Renders nothing at all if neither subsection has anything to show, rather than
    // an empty card. The System Identity subsection is intentionally system-level only (manufacturer,
    // product name, serial) [never battery specs] since each report's own battery-specific tables
    // (Battery Info / Quick Stats / Other Battery Data) already cover those.
    public static void AppendSystemInformation(StringBuilder sb, string? sysManufacturer, string? sysModel,
        string? sysSerial, ActivationSnapshot? activation, string? activationImageDataUri)
    {
        var hasIdentity = !string.IsNullOrWhiteSpace(sysManufacturer) || !string.IsNullOrWhiteSpace(sysModel)
            || !string.IsNullOrWhiteSpace(sysSerial);
        if (!hasIdentity && activation is null) return;

        sb.Append("<section class=\"card\">\n<h2>System Information</h2>\n");

        if (hasIdentity)
        {
            sb.Append("<div class=\"sysinfo-sub\">\n<h3>System Identity</h3>\n<table class=\"kv-table\">\n");
            AppendKvRow(sb, "Manufacturer", sysManufacturer);
            AppendKvRow(sb, "Product Name", sysModel);
            AppendKvRow(sb, "Serial Number", sysSerial);
            sb.Append("</table>\n</div>\n");
        }

        if (activation is not null)
        {
            var statusClass = activation.IsPermanentlyActivated ? "activation-genuine" : "activation-nongenuine";
            sb.Append($"<div class=\"sysinfo-sub activation-card {statusClass}\">\n<h3>Windows Activation</h3>\n");
            sb.Append("<div class=\"activation-row\">\n");
            if (activationImageDataUri is { Length: > 0 })
                sb.Append($"<img class=\"activation-badge\" src=\"{Enc(activationImageDataUri)}\" alt=\"Activation status\" />\n");
            sb.Append("<table class=\"kv-table activation-table\">\n");
            sb.Append($"<tr><td>Operating System</td><td>{Enc(activation.OsName)} (Build {Enc(activation.BuildNumber)})</td></tr>\n");
            sb.Append($"<tr><td>Activation Status</td><td>{Enc(activation.LicenseStatus)}</td></tr>\n");
            sb.Append($"<tr><td>Activation Method</td><td>{Enc(activation.ActivationMethod)}</td></tr>\n");
            if (!string.IsNullOrEmpty(activation.KmsServer))
                sb.Append($"<tr><td>KMS Server</td><td>{Enc(activation.KmsServer)}{(activation.KmsPort is { Length: > 0 } p ? $" : {Enc(p)}" : "")}</td></tr>\n");
            sb.Append($"<tr><td>Product Key</td><td>{Enc(ObfuscateProductKey(activation.ProductKey))}</td></tr>\n");
            sb.Append($"<tr><td>Expiry</td><td>{Enc(activation.ExpiryText)}</td></tr>\n");
            sb.Append("</table>\n</div>\n");
            if (activation.ErrorMessage is { Length: > 0 } err)
                sb.Append($"<p class=\"activation-error\">{Enc(err)}</p>\n");
            sb.Append("</div>\n");
        }

        sb.Append("</section>\n");
    }

    private static void AppendKvRow(StringBuilder sb, string label, string? value)
    {
        var known = !string.IsNullOrWhiteSpace(value);
        sb.Append("<tr><td>").Append(Enc(label)).Append("</td><td>")
          .Append(known ? Enc(value!) : "<span class=\"na\">N/A</span>").Append("</td></tr>\n");
    }

    // Product Key display in every report is obfuscated to the last 5 characters; a report is a
    // deliverable that can be emailed/handed to a client, so the full key shouldn't be printed in
    // plaintext there. Only masks alphanumeric key characters; dashes/asterisks already present pass
    // through unchanged, and a non-key placeholder ("Not available (no key stored locally)") is left as-is.
    public static string ObfuscateProductKey(string? productKey)
    {
        if (string.IsNullOrWhiteSpace(productKey)) return productKey ?? "";

        var chars = productKey.ToCharArray();
        var alnumIndices = new System.Collections.Generic.List<int>();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetterOrDigit(chars[i])) alnumIndices.Add(i);
        }

        if (alnumIndices.Count == 0 || alnumIndices.Count > 25) return productKey;

        var maskCount = Math.Max(0, alnumIndices.Count - 5);
        for (var i = 0; i < maskCount; i++)
            chars[alnumIndices[i]] = '*';

        return new string(chars);
    }

    // Resolves the genuine/non-genuine activation badge to an inline data: URI, same lookup MainViewModel
    // used inline before this was shared; battery runners now use the same lookup so their reports can
    // show the same badge as the regular diagnostic report. Missing file/any read failure -> null (report
    // simply omits the image), never throws.
    public static string? TryBuildActivationImageDataUri(bool isPermanentlyActivated)
    {
        try
        {
            var fileName = isPermanentlyActivated ? "genuine.png" : "non-genuine.png";
            var path = Path.Combine(AppContext.BaseDirectory, "vendor", "images", fileName);
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    // Shared CSS for the AppendSystemInformation card; used by all three report builders. Kept here
    // rather than duplicated in HtmlReportGenerator/BatteryDrainReportBuilder/BatteryChargeReportBuilder's
    // own Css consts so the section looks identical everywhere it appears.
    public const string SystemInfoCss = @"
.card { background:#FFFFFF; border:1px solid #E1E3E8; border-radius:8px; padding:20px; margin-bottom:20px; }
.card h2 { margin-top:0; font-size:17px; }
.sysinfo-sub h3 { font-size:12px; margin:0 0 8px 0; color:#6B6B72; text-transform:uppercase; letter-spacing:.04em; }
.sysinfo-sub + .sysinfo-sub { margin-top:18px; padding-top:18px; border-top:1px solid #E1E3E8; }
.kv-table td { padding:4px 10px 4px 0; font-size:13px; }
.kv-table td:first-child { opacity:0.6; width:200px; }
.na { color:#9A9AA2; font-style:italic; }
.activation-card { border-left:5px solid #6E6E6E; padding-left:14px; margin-left:-14px; }
.activation-genuine { border-left-color:#1E7A46; }
.activation-nongenuine { border-left-color:#B23A32; }
.activation-row { display:flex; align-items:flex-start; justify-content:flex-start; gap:20px; flex-wrap:wrap; }
.activation-badge { width:140px; height:auto; flex-shrink:0; }
.activation-table { flex:0 0 auto; width:auto; max-width:420px; }
.activation-error { color:#B23A32; font-size:12px; margin:10px 0 0 0; }
";

    // ===================== Live "test in progress" overlay =====================
    //
    // A fixed, translucent, bold banner shown only while a Charge/Drain report is still live-updating
    // (isFinal: false) [a plain visual disclaimer so a technician glancing at the page mid-run is never
    // confused into reading a still-refreshing report as a finished result, rather than a guarantee about
    // data completeness. pointer-events: none keeps the page underneath fully clickable/scrollable] it
    // never intercepts a click, a scroll, or a text-selection drag, so the existing state-preservation
    // script's scroll position and open <details> section (BatteryChargeReportBuilder) keep working
    // exactly as before underneath it.
    //
    // HtmlReportGenerator (the main diagnostic report) has no live/in-progress mode to begin with [confirmed by inspection: no isFinal parameter anywhere in that file, no meta-refresh tag either] so
    // there's nothing to wire this into there.
    public static void AppendInProgressOverlay(StringBuilder sb)
    {
        sb.Append("<div class=\"wt-in-progress-overlay\" aria-hidden=\"true\">TEST IN PROGRESS</div>\n");
    }

    // Fixed to the top of the viewport, full width, dark/translucent to match the report headers' own
    // dark strip (#1C1C1F) rather than introducing a new color. z-index set high enough to sit above
    // every other fixed/positioned element currently in either report (neither uses position: fixed or a
    // z-index today, so this is a clean high-water mark, not tuned against anything specific). Hidden
    // outright for print/PDF export; a printed page is a snapshot of one moment, not something that
    // benefits from a "still updating" disclaimer the way an on-screen live view does.
    public const string InProgressOverlayCss = @"
.wt-in-progress-overlay {
  position: fixed; top: 0; left: 0; right: 0; z-index: 9999; pointer-events: none; user-select: none;
  text-align: center; padding: 8px 12px; font-weight: 700; font-size: 14px; letter-spacing: .08em;
  color: #FFFFFF; background: rgba(28,28,31,0.72);
}
@media print { .wt-in-progress-overlay { display: none !important; } }
";

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
