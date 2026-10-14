using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Branding;
using WinTester.Core;
using WinTester.Core.Activation;
using WinTester.Reporting;
using WinTester.Tests.Cpu;
using WinTester.Tests.Gpu;
using WinTester.Tests.Power;
using WinTester.Tools.Activation;
using WinTester.Tools.BatteryDrain;

namespace WinTester.Tools.BatteryCharge;

// Orchestrates one Battery Charge Test run: gates entry to laptops only, automatically runs a discharge-prep routine if the battery starts at or above the selected mode's threshold, waits for an actual AC-charging connection, then polls/logs at idle during the real run (BatteryChargeOptions.PollInterval, 5s by default) (no CPU/GPU load of its own; the discharge-prep step can optionally add a GPU load alongside its always-on CPU load, off by default). Evaluates each mode's stop conditions every tick, and writes a crash-safe JSON-Lines log plus a full self-contained HTML report on every tick, mirroring BatteryDrainRunner's "never lose data mid-run" posture closely (same cancellation approach, same per-tick best-effort report rewrite).
public sealed class BatteryChargeRunner
{
    private static readonly TimeSpan ChargerWaitPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PrepPollInterval = TimeSpan.FromSeconds(1);
    private const int CapacityPollEveryNTicks = 10;

    // 2h upper bound on the discharge-prep stress engine; a "get below the gate" prep step, not a burn-in, so it should never need this long; exists so the engine has some finite duration argument, and is cut short the moment the battery percent drops below the threshold.
    private const int PrepEngineRunSeconds = 2 * 60 * 60;

    // Charging-resume delay after discharge-prep: instrumentation window, in seconds at the charger-wait
    // gate's own 1s poll cadence. 60 is the wide end of the observed 30-60s range, so a slow-to-resolve
    // delay isn't clipped from the log before it actually clears. Evidence-gathering only, no behavior
    // change to the gate itself.
    private const int ChargerWaitDiagnosticSeconds = 60;

    // Instead of stopping the instant the battery reads 100%, keeps logging at the normal PollInterval
    // cadence for this long while it sits at/above 100%: captures the float/top-off behavior
    // (voltage/current tapering to near-zero at full charge) that a run stopped exactly at the 100%
    // instant would otherwise miss entirely. No reset-on-dip: once 100% is first observed the float
    // window runs to completion even if a later tick reads a hair under 100% (sensor noise at full charge
    // is common and shouldn't restart/extend the window indefinitely).
    private static readonly TimeSpan FloatAt100Duration = TimeSpan.FromSeconds(60);

    private readonly ISensorService _sensorService;
    private readonly string _cpuVendorDirectory;
    private readonly string _gpuVendorDirectory;
    private readonly BrandProfile? _brand;

    // The self-calibrating CV lookup table, loaded once per runner instance (effectively once per test-window lifetime, since each Battery Charge Test window owns its own runner) rather than once per run, so consecutive runs in the same session see each other's calibration updates without a redundant disk re-read.
    private readonly BatteryChargeHealthAlgorithm.ChargeCurveStore _curveStore;

    public event Action<string>? StatusChanged;
    public event Action<BatteryChargeStartSnapshot>? Started;
    public event Action<BatteryChargeSample>? SampleCaptured;
    public event Action<BatteryChargeEndSnapshot, string>? Completed;
    public event Action<string>? Failed;

    public BatteryChargeRunner(ISensorService sensorService, string? cpuVendorDirectory = null,
        string? gpuVendorDirectory = null, BrandProfile? brand = null)
    {
        _sensorService = sensorService;
        _cpuVendorDirectory = cpuVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "intel-pdt");
        _gpuVendorDirectory = gpuVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "furmark2");
        _brand = brand;
        _curveStore = BatteryChargeHealthAlgorithm.ChargeCurveStore.Load();
    }

    private CancellationTokenSource? _runCts;
    private string? _stopReason;
    private readonly object _stopLock = new();

    // A running copy of this run's start snapshot and sample list, kept outside the main try/catch's local-variable scope so the catch block can still write an honest final report (rather than leaving the last mid-run "in progress" write as the permanent-looking final state) if RunAsync throws partway through. Reset to null at the top of every RunAsync call.
    private BatteryChargeStartSnapshot? _lastKnownStart;
    private List<BatteryChargeSample>? _lastKnownSamples;

    // Windows Activation status doesn't change mid-run; captured once at the top of RunAsync (after the
    // laptop/gate checks so we don't do it for a run that never starts) and reused for every tick's report
    // write plus the crash-path write, same reasoning as BatteryDrainRunner's identical field.
    private ActivationSnapshot? _activation;
    private string? _activationImageDataUri;

    public bool IsRunning { get; private set; }

    public void RequestStop() => RequestStopInternal("Stopped by operator.");

    private void RequestStopInternal(string reason)
    {
        lock (_stopLock)
        {
            _stopReason ??= reason;
            _runCts?.Cancel();
        }
    }

    public async Task RunAsync(BatteryChargeOptions options, string reportPath, CancellationToken externalCt)
    {
        _stopReason = null;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _runCts.Token;
        _lastKnownStart = null;
        _lastKnownSamples = null;
        _activation = null;
        _activationImageDataUri = null;

        try
        {
            if (MachineClassifier.Classify() != MachineKind.Laptop)
            {
                Failed?.Invoke("Battery Charge Test is laptop/portable only; this machine was classified as a desktop.");
                return;
            }

            var gateSnap = _sensorService.GetSnapshot();
            var gateError = options.ValidateStartGate(gateSnap.Battery?.Percent);
            if (gateError is not null)
            {
                Failed?.Invoke(gateError);
                return;
            }

            // Discharge-prep routine: instead of rejecting the run when the battery starts at or above the selected mode's threshold, walks the operator through getting it below that threshold automatically, then falls through into the normal charger-connect gate below.
            var prepOutcome = await RunDischargePrepIfNeededAsync(options, ct);
            if (!prepOutcome.Proceed) return; // cancelled/stopped mid-prep: RequestStop/Failed already fired as needed.

            // "Connect charger" gate; waits with no logging/loading until actual charging is confirmed. BatteryRuntimeStatusReader's WMI-native BatteryStatus.Charging boolean is the primary signal (a direct Windows-reported boolean, not inferred from a sensor's dynamically-renamed Name), falling back to ChargeState only if that WMI read is unavailable.
            StatusChanged?.Invoke("Waiting for AC power connection...");
            {
                // Charger-wait diagnostic instrumentation: only opened when discharge-prep actually ran
                // real stress engines; this gate's ordinary un-prepped case isn't the case being
                // diagnosed. Logs CPU package power / peak GPU load / the charging boolean at the gate's
                // own 1s poll cadence for the first ChargerWaitDiagnosticSeconds, to give a later run on
                // real hardware evidence of what's actually gating charge start. Best-effort: a failed
                // diagnostic read/write must never affect the actual charger-wait gate below it.
                using var diagWriter = prepOutcome.PrepRan
                    ? new StreamWriter(Path.ChangeExtension(reportPath, null) + "_chargerwait_diag.jsonl", append: false) { AutoFlush = true }
                    : null;
                var waitStart = DateTime.Now;
                var diagTicksWritten = 0;

                while (!ct.IsCancellationRequested)
                {
                    var wmiStatus = BatteryRuntimeStatusReader.Read();
                    var isCharging = wmiStatus?.Charging ?? (_sensorService.GetSnapshot().Battery?.ChargeState == BatteryChargeState.Charging);

                    if (diagWriter is not null && diagTicksWritten < ChargerWaitDiagnosticSeconds)
                    {
                        try
                        {
                            var diagSnap = _sensorService.GetSnapshot();
                            double? maxGpuLoad = null;
                            foreach (var gpu in diagSnap.Gpus)
                            {
                                if (gpu.LoadPercent is { } load && (maxGpuLoad is null || load > maxGpuLoad))
                                    maxGpuLoad = load;
                            }

                            var diagSample = new ChargerWaitDiagnosticSample(
                                ElapsedSeconds: (DateTime.Now - waitStart).TotalSeconds,
                                CpuPackagePowerW: diagSnap.Cpu?.PackagePowerW,
                                MaxGpuLoadPercent: maxGpuLoad,
                                Charging: isCharging);
                            diagWriter.WriteLine(JsonSerializer.Serialize(diagSample));
                        }
                        catch { /* best-effort diagnostic only; must never affect the actual gate. */ }
                        diagTicksWritten++;
                    }

                    if (isCharging) break;

                    try { await Task.Delay(ChargerWaitPollInterval, ct); }
                    catch (OperationCanceledException) { return; } // stopped/cancelled while waiting; no run to fail.
                }
            }
            if (ct.IsCancellationRequested) return;

            StatusChanged?.Invoke("Charging detected, starting test.");

            // Best-effort, same posture as BatteryDrainRunner/MainViewModel: a WMI/slmgr failure here
            // shouldn't take down the charge run, it just means the report's System Information card omits
            // the Windows Activation subsection for this run.
            try
            {
                _activation = await ActivationInfoService.GetSnapshotAsync();
                _activationImageDataUri = BatteryReportSharedStyle.TryBuildActivationImageDataUri(_activation.IsPermanentlyActivated);
            }
            catch { /* report simply omits the Windows Activation subsection */ }

            var jsonlPath = Path.ChangeExtension(reportPath, null) + "_data.jsonl";
            var samples = new List<BatteryChargeSample>();

            var startSnapshot = CaptureStartSnapshot();
            _lastKnownStart = startSnapshot;
            _lastKnownSamples = samples;
            IsRunning = true;
            Started?.Invoke(startSnapshot);

            // Sleep/display-off prevention: this is an unattended run that can sit for many minutes with the operator not touching the machine; exactly the situation Windows' default idle timers are designed to sleep.
            using var powerGuard = PowerRequestGuard.Begin();

            using var jsonlWriter = new StreamWriter(jsonlPath, append: false) { AutoFlush = true };

            var startTime = DateTime.Now;
            var tick = 0;
            BatteryRuntimeStatus? lastCapacity = null;

            double? peakCurrentMa = null;
            double? peakRateAtPeakW = null;
            DateTime lastPeakImprovedAt = startTime;
            // Rolling-average peak tracking for the Peak Sweep stall condition: judged against a rolling
            // average (BatteryChargeOptions.PeakStallRollingWindow) rather than a single-sample all-time-high,
            // since the pack's steady CC current can sit a few percent below a noise spike or its own known
            // two-current-level alternation (BatteryChargeOptions.PeakStallToleranceFraction is the tolerance)
            // without the charge actually stalling. peakCurrentMa/peakRateAtPeakW above stay single-sample
            // and are still what the final report's "Peak Charge Current/Rate" fields show.
            double? peakRollingAvgMa = null;
            var rollingCurrentWindow = new Queue<(DateTime Time, double Ma)>();
            DateTime? zeroCurrentSince = null;
            // The Battery % reading at the moment the current zero-current window started, lets the stop
            // condition below tell a genuine stall apart from a slow-to-update current sensor on an actually-
            // still-charging pack (see BatteryChargeOptions.ZeroCurrentPercentRiseResetThreshold).
            double? zeroCurrentWindowStartPercent = null;
            // Same idea as the percent field above, but for pack voltage; a deep-discharge precharge can
            // step the voltage up a few mV at a time while current still reads ~zero and Battery % hasn't
            // moved (see ZeroCurrentVoltageRiseResetThresholdV).
            double? zeroCurrentWindowStartVoltageV = null;
            // When the battery first read >= 100%, see FloatAt100Duration above. Null until that first tick.
            DateTime? at100Since = null;

            while (!ct.IsCancellationRequested)
            {
                var tickStart = DateTime.Now;
                var elapsed = tickStart - startTime;

                if (tick % CapacityPollEveryNTicks == 0)
                {
                    // A transient WMI/driver hiccup on this slower-cadence capacity poll must not crash the run or poison the log with a stale value; falls back to the last known-good reading and keeps going.
                    try { lastCapacity = BatteryRuntimeStatusReader.Read() ?? lastCapacity; }
                    catch { /* Keeps the previous lastCapacity; one bad tick shouldn't end the run. */ }
                }

                BatteryTelemetry? battery = null;
                try { battery = _sensorService.GetSnapshot().Battery; }
                catch
                {
                    // A LibreHardwareMonitorLib read failure on this tick; logs the tick with every battery field null rather than let the exception propagate and end the run over one bad sample.
                }

                var chargeCurrentMa = battery?.ChargeCurrentMa;
                var chargeRateW = battery?.ChargeRateW;

                var sample = new BatteryChargeSample(
                    Timestamp: tickStart,
                    ElapsedSeconds: elapsed.TotalSeconds,
                    BatteryPercent: battery?.Percent,
                    // lastCapacity only refreshes every CapacityPollEveryNTicks (10) ticks, a cadence chosen
                    // for the WMI RemainingCapacity field specifically (see BatteryRuntimeStatusReader's own
                    // comment: "mWh doesn't shift second-to-second the way percent/voltage can"), but Voltage
                    // rode along on that same slow-cadence struct anyway. Since pack voltage climbs steadily
                    // during CC charge, the stale WMI reading always sat a hair below LHM's fresh per-tick
                    // reading. LHM's live per-tick reading is now primary; WMI's is only a fallback for the
                    // rare tick LHM itself fails to read.
                    VoltageV: battery?.VoltageV ?? lastCapacity?.VoltageV,
                    ChargeCurrentMa: chargeCurrentMa,
                    ChargeRateW: chargeRateW,
                    RemainingCapacityMwh: lastCapacity?.RemainingCapacityMwh ?? battery?.RemainingCapacityMwh,
                    TemperatureC: battery?.TemperatureC);

                samples.Add(sample);
                SampleCaptured?.Invoke(sample);
                jsonlWriter.WriteLine(JsonSerializer.Serialize(sample));

                TryWriteReport(reportPath, options, startSnapshot, null, samples, isFinal: false, _curveStore, _brand,
                    _activation, _activationImageDataUri, VoltageRange);

                // Peak-current tracking (Peak Sweep's stall condition) and zero-current tracking (every mode's "stopped/disconnected" condition) both use a small epsilon rather than an exact zero comparison, since a genuinely-charging sensor can read a few hundredths of a mA of noise at true float-charge.
                const double zeroEpsilonMa = 1.0;
                if (chargeCurrentMa is { } curMa && curMa > zeroEpsilonMa)
                {
                    zeroCurrentSince = null;
                    zeroCurrentWindowStartPercent = null;
                    zeroCurrentWindowStartVoltageV = null;

                    // Single-sample peak; kept only for the final report's "Peak Charge Current"/"Peak
                    // Charge Rate" fields. No longer what the stall clock is judged against; see below.
                    if (peakCurrentMa is null || curMa > peakCurrentMa)
                    {
                        peakCurrentMa = curMa;
                        peakRateAtPeakW = chargeRateW;
                    }

                    // Rolling-average + tolerance-band peak tracking (Peak Sweep's stall condition; combines
                    // fixes for the earlier false-stop issue): average recent samples so a momentary dip
                    // or the pack's own two-current-level CC alternation doesn't get compared against a
                    // single instantaneous reading, then only require the smoothed reading to stay within
                    // PeakStallToleranceFraction of the best smoothed reading seen so far [not literally
                    // beat it] to count as still holding at peak.
                    rollingCurrentWindow.Enqueue((tickStart, curMa));
                    while (rollingCurrentWindow.Count > 0 &&
                           tickStart - rollingCurrentWindow.Peek().Time > BatteryChargeOptions.PeakStallRollingWindow)
                        rollingCurrentWindow.Dequeue();
                    var rollingAvgMa = rollingCurrentWindow.Average(s => s.Ma);
                    if (peakRollingAvgMa is null || rollingAvgMa > peakRollingAvgMa)
                    {
                        peakRollingAvgMa = rollingAvgMa;
                        lastPeakImprovedAt = tickStart;
                    }
                    else if (rollingAvgMa >= peakRollingAvgMa * (1.0 - BatteryChargeOptions.PeakStallToleranceFraction))
                    {
                        // Still within tolerance of the best smoothed reading; holding at peak, not
                        // declining, so the stall clock doesn't advance but doesn't reset to a "new best"
                        // either.
                        lastPeakImprovedAt = tickStart;
                    }
                }
                else if (zeroCurrentSince is null)
                {
                    zeroCurrentSince = tickStart;
                    zeroCurrentWindowStartPercent = battery?.Percent;
                    zeroCurrentWindowStartVoltageV = sample.VoltageV;
                }
                else if ((battery?.Percent is { } curPct && zeroCurrentWindowStartPercent is { } windowStartPct &&
                          curPct - windowStartPct >= BatteryChargeOptions.ZeroCurrentPercentRiseResetThreshold) ||
                         (sample.VoltageV is { } curV && zeroCurrentWindowStartVoltageV is { } windowStartV &&
                          curV - windowStartV >= BatteryChargeOptions.ZeroCurrentVoltageRiseResetThresholdV))
                {
                    // Current reads zero/near-zero, but either Battery % or the pack's own voltage has
                    // visibly risen since the window opened; some machines' current sensor is slow to
                    // update, and during a deep-discharge precharge the BMS can step voltage up a few mV at
                    // a time before Battery % moves at all. Either signal is independent proof the pack is
                    // still charging, so either one restarts the stall window from here instead of tripping
                    // the stop condition on a false read.
                    zeroCurrentSince = tickStart;
                    zeroCurrentWindowStartPercent = battery?.Percent;
                    zeroCurrentWindowStartVoltageV = sample.VoltageV;
                }

                // Reaching 100% no longer stops the run immediately, see FloatAt100Duration above.
                // at100Since is set the first tick Percent reads >= 100 and never reset, so the run
                // keeps polling/logging at the normal cadence (capturing the float/top-off behavior) until
                // that window elapses, then stops. Tracked here (ahead of the stop-condition checks below)
                // so the very same tick that first reaches 100% is still eligible to stop other ways too
                // (e.g. a charger disconnect at the exact moment of reaching full).
                if (battery?.Percent is { } livePct100 && livePct100 >= 100.0 && at100Since is null)
                    at100Since = tickStart;

                // Stop conditions; first reason wins, same race-not-gate posture as BatteryDrainRunner.
                if (at100Since is { } a100 && tickStart - a100 >= FloatAt100Duration)
                    RequestStopInternal("Battery reached 100% and was held there for 60 seconds to capture the float/top-off behavior.");
                else if (battery is not null && battery.ChargeState != BatteryChargeState.Charging &&
                         lastCapacity?.Charging != true)
                    // Cross-checked against the same slower-cadence WMI read used for capacity (not a fresh per-tick query); only treated as a real disconnect once both the sensor-derived ChargeState and the last WMI Charging read agree it's not charging.
                    RequestStopInternal("Stopped automatically: charger was disconnected mid-run.");
                else if (zeroCurrentSince is { } zcs && tickStart - zcs >= BatteryChargeOptions.ZeroCurrentStopAfter)
                    RequestStopInternal("Stopped automatically: charge current sat at zero for 20 minutes with no Battery % rise.");
                else if (options.SweepMode == BatteryChargeSweepMode.PeakSweep && options.EnablePeakStallDetection &&
                         peakCurrentMa is not null &&
                         battery?.Percent is { } stallCheckPct && stallCheckPct >= options.PeakStallMinBatteryPercent &&
                         tickStart - lastPeakImprovedAt >= options.PeakStallStopAfter)
                    // Gated on Battery % (PeakStallMinBatteryPercent) so ordinary CC-phase tapering can no
                    // longer trip this; see BatteryChargeOptions for the false-stop this fixed. Minutes is
                    // embedded from the operator-configurable duration rather than a fixed "5";
                    // PlainLanguageStopReason below matches on the stable prefix, not the full string.
                    RequestStopInternal($"Stopped automatically: peak charge current hasn't increased in {options.PeakStallStopAfter.TotalMinutes:0} minutes.");

                tick++;

                var sleepFor = options.PollInterval - (DateTime.Now - tickStart);
                if (sleepFor > TimeSpan.Zero)
                {
                    try { await Task.Delay(sleepFor, ct); }
                    catch (OperationCanceledException) { /* stop condition fired mid-delay */ }
                }
            }

            StatusChanged?.Invoke("Finishing up...");

            var endSnapshot = CaptureEndSnapshot(_stopReason ?? "Stopped.", peakCurrentMa, peakRateAtPeakW);
            TryWriteReport(reportPath, options, startSnapshot, endSnapshot, samples, isFinal: true, _curveStore, _brand,
                _activation, _activationImageDataUri, VoltageRange);

            // Self-calibration: feeds this run's real ticks back into the CC/CV lookup tables (Wide Sweep/Peak Sweep only, lithium chemistry only: see BatteryChargeHealthAlgorithm.ObserveCompletedRun's own guards). Runs on every completion path that reaches here (natural 100% stop, an automatic stop condition, or an operator Stop), since all of them logged real ticks along the way and are legitimate calibration input.
            try
            {
                var nominalVForCalibration = endSnapshot.EndVoltageV
                    ?? (startSnapshot.StartVoltageV is { } sv && sv > 0 ? sv : 11.1);
                var calibrationTicks = samples
                    .Select(s => new BatteryChargeHealthAlgorithm.RatingTick(s.ElapsedSeconds, s.BatteryPercent, s.ChargeRateW))
                    .ToList();
                BatteryChargeHealthAlgorithm.ObserveCompletedRun(
                    endSnapshot.Chemistry ?? startSnapshot.Chemistry,
                    options.SweepMode,
                    startSnapshot.DesignCapacityMwh,
                    nominalVForCalibration,
                    calibrationTicks,
                    _curveStore);
            }
            catch
            {
                // Calibration is a nice-to-have accuracy improvement, never something a completed run should fail over: same best-effort posture as ChargeCurveStore.Save.
            }

            IsRunning = false;
            Completed?.Invoke(endSnapshot, reportPath);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            // An unhandled exception mid-run must still leave an honest, non-misleading final report behind rather than stranding the operator with the last mid-run "in progress" write. Best-effort: if even this fails (e.g. the exception happened before startSnapshot/samples existed), the JSON Lines log on disk remains the crash-recoverable record.
            try
            {
                if (_lastKnownStart is { } startForCrash)
                {
                    var crashEnd = new BatteryChargeEndSnapshot(
                        EndTime: DateTime.Now,
                        StopReason: $"Test interrupted: {ex.Message}",
                        EndPercent: null, EndVoltageV: null, FullChargeCapacityMwh: null,
                        EndingBatteryHealthPercent: null, EndingDegradationPercent: null,
                        RemainingCapacityMwh: null, PeakChargeCurrentMa: null, PeakChargeRateW: null);
                    TryWriteReport(reportPath, options, startForCrash, crashEnd, _lastKnownSamples ?? new List<BatteryChargeSample>(),
                        isFinal: true, _curveStore, _brand, _activation, _activationImageDataUri, VoltageRange);
                }
            }
            catch { /* best-effort only; must never mask the original exception below. */ }

            Failed?.Invoke($"Battery Charge Test failed: {ex.Message}");
        }
    }

    // Proceed: false if the run was cancelled/stopped during the prep routine, so the caller can return
    // without treating it as a failure. PrepRan: true only once real stress engines were actually started, 
    // distinguishes "already below the gate, nothing ran" from "prep engaged" for the charger-wait
    // instrumentation below, which is only meaningful in the latter case.
    private readonly record struct DischargePrepOutcome(bool Proceed, bool PrepRan);

    // One sample of the charging-resume-delay instrumentation; logged only when discharge-prep actually
    // ran, for the charger-wait gate's first ChargerWaitDiagnosticSeconds. MaxGpuLoadPercent is the
    // highest LoadPercent across every reported GPU (a machine with an idle iGPU and a still-ramping-down
    // dGPU should show the dGPU's number, not an averaged-down one).
    private readonly record struct ChargerWaitDiagnosticSample(
        double ElapsedSeconds, double? CpuPackagePowerW, double? MaxGpuLoadPercent, bool Charging);

    // The discharge-prep routine. If the battery is already below the mode's threshold, returns immediately. Otherwise: tells the operator to unplug the charger, waits for an actual on-battery reading (not just "not charging"), runs a quick CPU-only stress test (plus, only when UseGpuLoadInPrep is enabled, an off-screen FurMark2 OpenGL pass alongside it) to drain the battery below the threshold, then tells the operator to reconnect the charger.
    private async Task<DischargePrepOutcome> RunDischargePrepIfNeededAsync(BatteryChargeOptions options, CancellationToken ct)
    {
        var threshold = options.FixedStartThresholdPercent;
        var startSnap = _sensorService.GetSnapshot();
        if (startSnap.Battery?.Percent is not { } startPct || startPct < threshold)
            return new DischargePrepOutcome(true, false); // already below the gate, no prep needed.

        StatusChanged?.Invoke(
            $"Battery is at {startPct:0.0}%; needs to be below {threshold:0}% to start this test. Please unplug the charger.");

        // Waits for an actual on-battery reading, not just "not charging"; a full battery sitting at 100% on AC also reads as not-charging, but that's not what "unplugged" means here.
        while (!ct.IsCancellationRequested)
        {
            var wmiStatus = BatteryRuntimeStatusReader.Read();
            var onBattery = wmiStatus?.Discharging
                ?? (_sensorService.GetSnapshot().Battery?.ChargeState == BatteryChargeState.Discharging);
            if (onBattery) break;

            try { await Task.Delay(PrepPollInterval, ct); }
            catch (OperationCanceledException) { return new DischargePrepOutcome(false, false); }
        }
        if (ct.IsCancellationRequested) return new DischargePrepOutcome(false, false);

        StatusChanged?.Invoke(options.UseGpuLoadInPrep
            ? $"Unplugged [running a quick CPU+GPU stress test to bring the battery below {threshold:0}%..."
            : $"Unplugged] running a quick CPU stress test to bring the battery below {threshold:0}%...");

        var vendor = CpuVendorDetector.Detect();
        var cpuRunner = new IpdtModuleRunner(_cpuVendorDirectory);
        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_battcharge_prep_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var resultPath = Path.Combine(tempDir, "math_primenumber_result.txt");

        using var prepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var engineTask = cpuRunner.RunAsync("math_primenumber", PrepEngineRunSeconds, resultPath, prepCts.Token, vendor);

        // Off by default; when enabled, runs a GPU load alongside the CPU one purely to speed up draining below the gate. Off-screen window, same recipe PowerCalibrationRunner uses for its own GPU calibration pass.
        Task<FurmarkRunner.Result>? gpuEngineTask = null;
        if (options.UseGpuLoadInPrep)
        {
            var gpuRunner = new FurmarkRunner(_gpuVendorDirectory);
            var gpuTempDir = Path.Combine(Path.GetTempPath(), "WinTester_battcharge_prep_gpu_" + Guid.NewGuid());
            Directory.CreateDirectory(gpuTempDir);
            var gpuCsvPath = Path.Combine(gpuTempDir, "gpu_data.csv");
            var offscreenRect = new WindowScreenRect(-10000, -10000, 640, 360);
            gpuEngineTask = gpuRunner.RunAsync(FurmarkEngine.OpenGl, PrepEngineRunSeconds, offscreenRect, gpuCsvPath, prepCts.Token);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var liveSnap = _sensorService.GetSnapshot();
                var livePct = liveSnap.Battery?.Percent;

                if (livePct is { } lp && lp < threshold)
                {
                    StatusChanged?.Invoke($"Battery below {threshold:0}%: please reconnect the charger.");
                    break;
                }

                // Safety net: if the operator reconnects the charger before reaching the threshold, stops stressing and prompts them to unplug again, rather than silently continuing to stress a system that's no longer discharging.
                var wmiStatus = BatteryRuntimeStatusReader.Read();
                var backOnAc = wmiStatus?.Charging ?? (liveSnap.Battery?.ChargeState == BatteryChargeState.Charging);
                StatusChanged?.Invoke(backOnAc
                    ? $"Charger reconnected before reaching {threshold:0}%; please unplug it again to continue."
                    : $"Discharging to below {threshold:0}%... currently {(livePct is { } p ? $"{p:0.0}%" : "N/A")}.");

                try { await Task.Delay(PrepPollInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            prepCts.Cancel();
            try { await engineTask; }
            catch
            {
                // Expected on every normal stop; threshold reached or run cancelled.
            }

            if (gpuEngineTask is not null)
            {
                try { await gpuEngineTask; }
                catch
                {
                    // Same swallow posture as the CPU engine task above; a cancelled/killed FurMark pass at the end of prep is expected, not a failure worth surfacing.
                }
            }
        }

        return new DischargePrepOutcome(!ct.IsCancellationRequested, PrepRan: true);
    }

    private SystemIdentityResult? _systemIdentity;
    private SystemIdentityResult SystemIdentity => _systemIdentity ??= SystemIdentityReader.Read();

    // Voltage chart-axis range (see BatteryVoltageRangeReader); same once-per-run caching as SystemIdentity
    // above; DesignVoltage doesn't change mid-run.
    private BatteryVoltageRangeResult? _voltageRange;
    private BatteryVoltageRangeResult VoltageRange => _voltageRange ??= BatteryVoltageRangeReader.Read();

    private BatteryChargeStartSnapshot CaptureStartSnapshot()
    {
        var health = BatteryHealthReader.Read();
        var sys = SystemIdentity;
        var snap = _sensorService.GetSnapshot();
        var capacity = BatteryRuntimeStatusReader.Read();

        return new BatteryChargeStartSnapshot(
            StartTime: DateTime.Now,
            StartPercent: snap.Battery?.Percent,
            StartVoltageV: capacity?.VoltageV ?? snap.Battery?.VoltageV,
            DesignCapacityMwh: health.DesignCapacityWh is { } d ? d * 1000.0 : null,
            FullChargeCapacityMwh: health.FullChargeCapacityWh is { } f ? f * 1000.0 : null,
            StartingBatteryHealthPercent: health.HealthPercent,
            StartingDegradationPercent: snap.Battery?.DegradationPercent,
            RemainingCapacityMwh: capacity?.RemainingCapacityMwh,
            CycleCount: health.CycleCount,
            BatterySerialNumber: health.SerialNumber,
            SystemSerialNumber: sys.SerialNumber,
            Chemistry: health.Chemistry,
            ManufacturerName: sys.Manufacturer ?? health.ManufacturerName,
            DeviceName: sys.Model ?? health.DeviceName);
    }

    private BatteryChargeEndSnapshot CaptureEndSnapshot(string stopReason, double? peakCurrentMa, double? peakRateW)
    {
        var health = BatteryHealthReader.Read();
        var sys = SystemIdentity;
        var snap = _sensorService.GetSnapshot();
        var capacity = BatteryRuntimeStatusReader.Read();

        return new BatteryChargeEndSnapshot(
            EndTime: DateTime.Now,
            StopReason: stopReason,
            EndPercent: snap.Battery?.Percent,
            EndVoltageV: capacity?.VoltageV ?? snap.Battery?.VoltageV,
            FullChargeCapacityMwh: health.FullChargeCapacityWh is { } f ? f * 1000.0 : null,
            EndingBatteryHealthPercent: health.HealthPercent,
            EndingDegradationPercent: snap.Battery?.DegradationPercent,
            RemainingCapacityMwh: capacity?.RemainingCapacityMwh,
            PeakChargeCurrentMa: peakCurrentMa,
            PeakChargeRateW: peakRateW,
            CycleCount: health.CycleCount,
            BatterySerialNumber: health.SerialNumber,
            SystemSerialNumber: sys.SerialNumber,
            Chemistry: health.Chemistry,
            ManufacturerName: sys.Manufacturer ?? health.ManufacturerName,
            DeviceName: sys.Model ?? health.DeviceName);
    }

    private static void TryWriteReport(string reportPath, BatteryChargeOptions options,
        BatteryChargeStartSnapshot start, BatteryChargeEndSnapshot? end, List<BatteryChargeSample> samples,
        bool isFinal, BatteryChargeHealthAlgorithm.ChargeCurveStore curveStore, BrandProfile? brand,
        ActivationSnapshot? activation, string? activationImageDataUri, BatteryVoltageRangeResult? voltageRange)
    {
        try
        {
            var nominalV = end?.EndVoltageV ?? (start.StartVoltageV is { } sv && sv > 0 ? sv : 11.1);
            var lastKnownMwh = end?.RemainingCapacityMwh ?? (samples.Count > 0 ? samples[^1].RemainingCapacityMwh : null)
                ?? start.RemainingCapacityMwh;
            var ticks = samples
                .Select(s => new BatteryChargeHealthAlgorithm.RatingTick(s.ElapsedSeconds, s.BatteryPercent, s.ChargeRateW))
                .ToList();
            // Grading only happens once the run is done; Compute() previously ran on every tick, so a
            // live report could show a real GOOD/OK/BAD/percentage verdict against a run that hadn't
            // finished. Every mid-run write gets Pending instead (still carries chemistry/starting SoC/
            // duration-so-far for the live Rating Basis table, just no tier or ratio).
            var rating = isFinal
                ? BatteryChargeHealthAlgorithm.Compute(
                    end?.Chemistry ?? start.Chemistry,
                    start.DesignCapacityMwh,
                    start.RemainingCapacityMwh,
                    lastKnownMwh,
                    nominalV,
                    ticks,
                    curveStore)
                : BatteryChargeHealthAlgorithm.Pending(end?.Chemistry ?? start.Chemistry, ticks);

            var html = BatteryChargeReportBuilder.Render(options, start, end, samples, rating, isFinal, brand,
                activation, activationImageDataUri, voltageRange);
            File.WriteAllText(reportPath, html);
        }
        catch
        {
            if (isFinal) throw;
        }
    }
}
