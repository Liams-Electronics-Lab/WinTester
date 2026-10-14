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

namespace WinTester.Tools.BatteryDrain;

// Orchestrates one Battery Drain run: starts the requested CPU/GPU stress engine(s) (reusing IpdtModuleRunner/FurmarkRunner the way PowerCalibrationRunner's combined pass does), polls battery+CPU/GPU telemetry every 1s and mWh capacity every 10s (BatteryRuntimeStatusReader), and writes a crash-safe JSON-Lines log plus a full HTML report every tick so no data is lost if the machine dies mid-run. Engines get a large-but-finite duration (EngineRunSeconds), not true infinity; early cancellation via their CancellationToken is what actually stops them, matching PowerCalibrationRunner's "duration is just an upper bound" posture.
public sealed class BatteryDrainRunner
{
    // Upper bound handed to the stress engines; comfortably longer than any realistic drain run since engines are always stopped early via cancellation, never by reaching this duration.
    private const int EngineRunSeconds = 24 * 60 * 60; // 24h

    // The drain-logging loop's own cadence is BatteryDrainOptions.PollInterval (operator-configurable,
    // default 5s, mirroring BatteryChargeOptions.PollInterval). AcUnplugWaitPollInterval stays fixed, 
    // that's the short "unplug the charger" setup gate, not the long-running measurement.
    private static readonly TimeSpan AcUnplugWaitPollInterval = TimeSpan.FromSeconds(1);
    private const int CapacityPollEveryNTicks = 10; // 10s cadence for BatteryRuntimeStatusReader.

    private readonly ISensorService _sensorService;
    private readonly string _cpuVendorDirectory;
    private readonly string _gpuVendorDirectory;
    private readonly BrandProfile? _brand;

    public event Action<BatteryDrainStartSnapshot>? Started;
    public event Action<BatteryDrainSample>? SampleCaptured;
    public event Action<string>? StatusChanged;
    public event Action<BatteryDrainEndSnapshot, string>? Completed; // (end snapshot, report file path)
    public event Action<string>? Failed;

    public BatteryDrainRunner(ISensorService sensorService, string? cpuVendorDirectory = null,
        string? gpuVendorDirectory = null, BrandProfile? brand = null)
    {
        _sensorService = sensorService;
        _cpuVendorDirectory = cpuVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "intel-pdt");
        _gpuVendorDirectory = gpuVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "furmark2");
        _brand = brand;
    }

    private CancellationTokenSource? _runCts;
    private string? _stopReason;
    private readonly object _stopLock = new();

    // Crash/interrupt resilience, mirroring BatteryChargeRunner's fields of the same purpose: kept outside the try/catch's local scope so the catch block can still write an honest final report instead of leaving the last mid-run write looking permanent.
    private BatteryDrainStartSnapshot? _lastKnownStart;
    private List<BatteryDrainSample>? _lastKnownSamples;

    // Windows Activation status doesn't change mid-run, so [same reasoning as SystemIdentity below] it's
    // captured once at the top of RunAsync and reused for every tick's report write plus the crash-path
    // write, rather than re-queried (async, WMI-backed) on every TryWriteReport call.
    private ActivationSnapshot? _activation;
    private string? _activationImageDataUri;

    // True from the Started event firing until Completed/Failed fires.
    public bool IsRunning { get; private set; }

    // Operator-initiated Stop button, distinct from the automatic stop conditions (max length / stop % / safety floor), each of which sets its own reason before cancelling.
    public void RequestStop()
    {
        RequestStopInternal("Stopped by operator.");
    }

    private void RequestStopInternal(string reason)
    {
        lock (_stopLock)
        {
            _stopReason ??= reason; // First reason wins; guards against the UI thread's Stop button racing the poll loop, even though it can't happen in practice.
            _runCts?.Cancel();
        }
    }

    // Runs the whole drain to completion or until stopped. reportPath is the operator-chosen .html save location, picked via SaveFileDialog before this is called; the report is rewritten in place at that path on every tick and again at completion.
    public async Task RunAsync(BatteryDrainOptions options, string reportPath, CancellationToken externalCt)
    {
        _stopReason = null;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _runCts.Token;
        _lastKnownStart = null;
        _lastKnownSamples = null;
        _activation = null;
        _activationImageDataUri = null;

        var jsonlPath = Path.ChangeExtension(reportPath, null) + "_data.jsonl";
        var samples = new List<BatteryDrainSample>();

        try
        {
            // Best-effort, same posture as MainViewModel's report-time activation capture: a WMI/slmgr
            // failure here shouldn't take down the drain run, it just means the report's System Information
            // card omits the Windows Activation subsection for this run.
            try
            {
                _activation = await ActivationInfoService.GetSnapshotAsync();
                _activationImageDataUri = BatteryReportSharedStyle.TryBuildActivationImageDataUri(_activation.IsPermanentlyActivated);
            }
            catch { /* report simply omits the Windows Activation subsection */ }

            // "Unplug charger" gate: the inverse of BatteryChargeRunner's "connect charger" gate. Starting
            // a drain run while still on AC would otherwise begin the run and then stop it on the very
            // first tick (the per-tick AC check further down), silently producing a near-empty run instead
            // of telling the operator up front what to fix.
            StatusChanged?.Invoke("Checking power state...");
            while (!ct.IsCancellationRequested)
            {
                WinTester.Core.SensorSnapshot? gateSnap = null;
                try { gateSnap = _sensorService.GetSnapshot(); }
                catch { /* transient sensor hiccup; retried on the next poll rather than failing the gate. */ }

                var stillOnAc = gateSnap?.Battery?.ChargeState is BatteryChargeState.Charging or BatteryChargeState.AcNoBattery;
                if (stillOnAc != true) break;

                StatusChanged?.Invoke("Please unplug the charger to begin the Battery Drain test.");
                try { await Task.Delay(AcUnplugWaitPollInterval, ct); }
                catch (OperationCanceledException) { return; } // cancelled/stopped while waiting; no run to fail.
            }
            if (ct.IsCancellationRequested) return;

            var startSnapshot = CaptureStartSnapshot();
            _lastKnownStart = startSnapshot;
            _lastKnownSamples = samples;
            IsRunning = true;
            Started?.Invoke(startSnapshot);

            // Sleep/display-off prevention; see PowerRequestGuard; this run can sit unattended for hours in "Infinite" mode.
            using var powerGuard = PowerRequestGuard.Begin();

            using var jsonlWriter = new StreamWriter(jsonlPath, append: false) { AutoFlush = true };

            StatusChanged?.Invoke("Starting drain load...");
            var engineTasks = StartEngines(options, ct, out var engineLog);

            var startTime = DateTime.Now;
            var tick = 0;
            BatteryRuntimeStatus? lastCapacity = null;
            // Held between capacity polls rather than recomputed every tick, since recomputing against a
            // stale mWh reading between polls makes the ratio drift and snap (see the recompute site below).
            double? lastEstimatedFcc = null;

            // The raw remMwh/pct ratio below is a genuine estimate, not a measurement, and on a short run
            // (few capacity polls, coarse WMI percent granularity) it can overshoot the pack's real
            // full-charge capacity outright; a real short run reported 100.3% Battery Health because the
            // estimate landed above Design Capacity. Nothing physically rechargeable can hold more than the
            // BMS's own reported ceiling, so the estimate is clamped to the higher of Design Capacity and
            // BMS Full-Charge Capacity (whichever is actually reported; either can be the larger one
            // depending on the pack's Wear Adjusted Design Capacity behavior).
            double? bmsCapacityCeilingMwh = null;
            if (startSnapshot.DesignCapacityMwh is { } designCeiling and > 0)
                bmsCapacityCeilingMwh = designCeiling;
            if (startSnapshot.FullChargeCapacityMwh is { } bmsFccCeiling and > 0)
                bmsCapacityCeilingMwh = bmsCapacityCeilingMwh is { } existing ? Math.Max(existing, bmsFccCeiling) : bmsFccCeiling;

            while (!ct.IsCancellationRequested)
            {
                var tickStart = DateTime.Now;
                var elapsed = tickStart - startTime;

                var freshCapacityThisTick = false;
                if (tick % CapacityPollEveryNTicks == 0)
                {
                    // Same posture as BatteryChargeRunner: a transient WMI/driver hiccup must not crash the run; falls back to the last known-good reading.
                    try
                    {
                        var read = BatteryRuntimeStatusReader.Read();
                        if (read is not null) { lastCapacity = read; freshCapacityThisTick = true; }
                    }
                    catch { /* Keeps the previous lastCapacity; one bad tick shouldn't end the run. */ }
                }

                WinTester.Core.SensorSnapshot snap;
                try { snap = _sensorService.GetSnapshot(); }
                catch
                {
                    // A full sensor-service failure logs a mostly-null sample instead of ending the run.
                    snap = new WinTester.Core.SensorSnapshot();
                }
                var battery = snap.Battery;
                var gpu = snap.Gpus.Count > 0 ? snap.Gpus[0] : null;

                // Only recomputed on the same tick RemainingCapacityMwh is freshly polled, paired with
                // that tick's own percent reading. Recomputing every tick would mix a capacity reading that
                // only updates every CapacityPollEveryNTicks with a percent reading that updates every tick,
                // so between capacity polls the percent would keep moving while the mWh figure sat still,
                // making the ratio drift upward and then snap back down every time capacity refreshed (the
                // exact sawtooth seen in logged data). Held steady on every other tick instead of recomputed
                // against stale data.
                if (freshCapacityThisTick && lastCapacity?.RemainingCapacityMwh is { } remMwh && battery?.Percent is { } pct && pct > 0)
                {
                    var rawEstimatedFcc = remMwh / (pct / 100.0);
                    lastEstimatedFcc = bmsCapacityCeilingMwh is { } ceiling ? Math.Min(rawEstimatedFcc, ceiling) : rawEstimatedFcc;
                }

                var sample = new BatteryDrainSample(
                    Timestamp: tickStart,
                    ElapsedSeconds: elapsed.TotalSeconds,
                    BatteryPercent: battery?.Percent,
                    // Same underlying bug as BatteryChargeRunner's identical fix: LHM's live per-tick
                    // reading is primary; WMI's slower-cadence reading is only a fallback for the rare
                    // tick LHM itself fails to read.
                    VoltageV: battery?.VoltageV ?? lastCapacity?.VoltageV,
                    RemainingCapacityMwh: lastCapacity?.RemainingCapacityMwh,
                    EstimatedFullChargeCapacityMwh: lastEstimatedFcc,
                    CpuLoadPercent: snap.Cpu?.LoadPercent,
                    CpuTempC: snap.Cpu?.PackageTempC,
                    GpuLoadPercent: gpu?.LoadPercent,
                    GpuTempC: gpu?.TempC,
                    GpuHotspotTempC: gpu?.HotspotTempC,
                    DischargeCurrentMa: battery?.DischargeCurrentMa);

                samples.Add(sample);
                SampleCaptured?.Invoke(sample);
                jsonlWriter.WriteLine(JsonSerializer.Serialize(sample));

                // Rewrites the full report every tick so data survives a mid-run crash; best-effort, a transient write failure shouldn't abort the run. Null history mid-run is expected, not a failure.
                TryWriteReport(reportPath, options, startSnapshot, null, samples, history: null, isFinal: false, _brand, _activation, _activationImageDataUri, VoltageRange);

                // External AC connection mid-run invalidates the drain data; stops immediately rather than logging a run that's no longer discharging.
                if (battery?.ChargeState is BatteryChargeState.Charging or BatteryChargeState.AcNoBattery)
                    RequestStopInternal("Stopped automatically: external power was connected.");
                else if (battery?.Percent is { } liveP && liveP <= BatteryDrainOptions.SafetyFloorPercent)
                    RequestStopInternal($"Stopped automatically: reached the {BatteryDrainOptions.SafetyFloorPercent}% safety floor.");
                else if (options.MaxDuration is { } maxD && elapsed >= maxD)
                    RequestStopInternal("Reached the configured max test length.");
                else if (battery?.Percent is { } liveP2 && liveP2 <= options.StopPercent)
                    // Checked every tick regardless of MaxDuration; whichever condition fires first stops the run. RequestStopInternal's first-reason-wins makes this race-safe.
                    RequestStopInternal($"Reached the configured stop percentage ({options.StopPercent}%).");

                tick++;

                var sleepFor = options.PollInterval - (DateTime.Now - tickStart);
                if (sleepFor > TimeSpan.Zero)
                {
                    try { await Task.Delay(sleepFor, ct); }
                    catch (OperationCanceledException) { /* Stop condition fired mid-delay; falls through. */ }
                }
            }

            StatusChanged?.Invoke("Stopping drain load engines...");
            try
            {
                await Task.WhenAll(engineTasks);
            }
            catch (OperationCanceledException)
            {
                // Expected on every normal stop; RequestStopInternal cancels the same linked token the engines watch, and both IpdtModuleRunner/FurmarkRunner call ct.ThrowIfCancellationRequested() after killing their child process so cancellation propagates rather than being swallowed. A genuine engine crash (not cancellation) still falls through to the outer catch.
            }

            var endSnapshot = CaptureEndSnapshot(_stopReason ?? "Stopped.");

            // Recent-usage/drain-history section: only fetched for the final write; shelling powercfg and parsing HTML is comparatively heavy, and the last-3-days history doesn't meaningfully change tick-to-tick.
            StatusChanged?.Invoke("Reading recent battery usage history (powercfg)...");
            var history = PowerCfgBatteryHistoryReader.Read();

            TryWriteReport(reportPath, options, startSnapshot, endSnapshot, samples, history, isFinal: true, _brand, _activation, _activationImageDataUri, VoltageRange);

            IsRunning = false;
            Completed?.Invoke(endSnapshot, reportPath);
        }
        catch (Exception ex)
        {
            IsRunning = false;

            // Crash/interrupt resilience, matching BatteryChargeRunner's handling: best-effort write of an honest "Test interrupted" final report instead of stranding a stale mid-run write.
            try
            {
                if (_lastKnownStart is { } startForCrash)
                {
                    var crashEnd = new BatteryDrainEndSnapshot(
                        EndTime: DateTime.Now,
                        StopReason: $"Test interrupted: {ex.Message}",
                        EndPercent: null, EndVoltageV: null, FullChargeCapacityMwh: null,
                        EndingBatteryHealthPercent: null, RemainingCapacityMwh: null,
                        CycleCount: null, SerialNumber: null, Chemistry: null,
                        ManufacturerName: null, DeviceName: null, AcLineStatus: null,
                        EndingDegradationPercent: null);
                    TryWriteReport(reportPath, options, startForCrash, crashEnd,
                        _lastKnownSamples ?? new List<BatteryDrainSample>(), history: null, isFinal: true, _brand,
                        _activation, _activationImageDataUri, VoltageRange);
                }
            }
            catch { /* Best-effort only. */ }

            Failed?.Invoke($"Battery Drain failed: {ex.Message}");
        }
    }

    private List<Task> StartEngines(BatteryDrainOptions options, CancellationToken ct, out string engineLog)
    {
        var tasks = new List<Task>();
        engineLog = "";

        if (options.Method is BatteryDrainMethod.CpuOnly or BatteryDrainMethod.CpuAndGpu)
        {
            var vendor = CpuVendorDetector.Detect();
            var cpuRunner = new IpdtModuleRunner(_cpuVendorDirectory);
            var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_battdrain_cpu_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            var resultPath = Path.Combine(tempDir, "math_primenumber_result.txt");
            tasks.Add(cpuRunner.RunAsync("math_primenumber", EngineRunSeconds, resultPath, ct, vendor));
        }

        if (options.Method is BatteryDrainMethod.GpuOnly or BatteryDrainMethod.CpuAndGpu)
        {
            var gpuRunner = new FurmarkRunner(_gpuVendorDirectory);
            var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_battdrain_gpu_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            var csvPath = Path.Combine(tempDir, "gpu_data.csv");
            // Off-screen, the same fixed-size recipe PowerCalibrationRunner uses; this window is never meant to be seen; the drain popup shows its own live battery-% graph instead.
            var offscreenRect = new WindowScreenRect(-10000, -10000, 640, 360);
            tasks.Add(gpuRunner.RunAsync(FurmarkEngine.OpenGl, EngineRunSeconds, offscreenRect, csvPath, ct));
        }

        return tasks;
    }

    // System identity (Win32_ComputerSystem/Win32_Bios) doesn't change mid-run, so it's read once per run rather than once per snapshot like BatteryHealthReader's live capacity/health figures. Cached on the instance rather than threaded through every call site.
    private SystemIdentityResult? _systemIdentity;

    private SystemIdentityResult SystemIdentity => _systemIdentity ??= SystemIdentityReader.Read();

    // Voltage chart-axis range (see BatteryVoltageRangeReader); same once-per-run caching as SystemIdentity
    // above; DesignVoltage doesn't change mid-run.
    private BatteryVoltageRangeResult? _voltageRange;

    private BatteryVoltageRangeResult VoltageRange => _voltageRange ??= BatteryVoltageRangeReader.Read();

    // Merges a BatteryHealthReader read with the once-per-run SystemIdentity pull: Device Name/Manufacturer show the system's identity (Win32_ComputerSystem's Model/Manufacturer, matching powercfg's own header table) rather than battery-device IOCTL fields this hardware doesn't answer. Serial Number prefers the battery's own IOCTL serial when reported, falling back to the system/chassis serial only when it isn't; never the reverse.
    private (string? DeviceName, string? Manufacturer, string? Serial) ResolveIdentity(BatteryHealthResult health)
    {
        var sys = SystemIdentity;
        return (
            DeviceName: sys.Model ?? health.DeviceName,
            Manufacturer: sys.Manufacturer ?? health.ManufacturerName,
            Serial: health.SerialNumber ?? sys.SerialNumber);
    }

    private BatteryDrainStartSnapshot CaptureStartSnapshot()
    {
        var health = BatteryHealthReader.Read();
        var identity = ResolveIdentity(health);
        var snap = _sensorService.GetSnapshot();
        var capacity = BatteryRuntimeStatusReader.Read();
        var power = SystemPowerStatusReader.Read();

        return new BatteryDrainStartSnapshot(
            StartTime: DateTime.Now,
            StartPercent: snap.Battery?.Percent,
            StartVoltageV: capacity?.VoltageV ?? snap.Battery?.VoltageV,
            DesignCapacityMwh: health.DesignCapacityWh is { } d ? d * 1000.0 : null,
            FullChargeCapacityMwh: health.FullChargeCapacityWh is { } f ? f * 1000.0 : null,
            StartingBatteryHealthPercent: health.HealthPercent,
            RemainingCapacityMwh: capacity?.RemainingCapacityMwh,
            CycleCount: health.CycleCount,
            SerialNumber: identity.Serial,
            Chemistry: health.Chemistry,
            ManufacturerName: identity.Manufacturer,
            DeviceName: identity.DeviceName,
            AcLineStatus: power.Success ? power.AcLineStatus : null,
            StartingDegradationPercent: snap.Battery?.DegradationPercent);
    }

    private BatteryDrainEndSnapshot CaptureEndSnapshot(string stopReason)
    {
        var health = BatteryHealthReader.Read();
        var identity = ResolveIdentity(health);
        var snap = _sensorService.GetSnapshot();
        var capacity = BatteryRuntimeStatusReader.Read();
        var power = SystemPowerStatusReader.Read();

        return new BatteryDrainEndSnapshot(
            EndTime: DateTime.Now,
            StopReason: stopReason,
            EndPercent: snap.Battery?.Percent,
            EndVoltageV: capacity?.VoltageV ?? snap.Battery?.VoltageV,
            FullChargeCapacityMwh: health.FullChargeCapacityWh is { } f ? f * 1000.0 : null,
            EndingBatteryHealthPercent: health.HealthPercent,
            RemainingCapacityMwh: capacity?.RemainingCapacityMwh,
            CycleCount: health.CycleCount,
            SerialNumber: identity.Serial,
            Chemistry: health.Chemistry,
            ManufacturerName: identity.Manufacturer,
            DeviceName: identity.DeviceName,
            AcLineStatus: power.Success ? power.AcLineStatus : null,
            EndingDegradationPercent: snap.Battery?.DegradationPercent);
    }

    private static void TryWriteReport(string reportPath, BatteryDrainOptions options,
        BatteryDrainStartSnapshot start, BatteryDrainEndSnapshot? end, List<BatteryDrainSample> samples,
        BatteryHistoryResult? history, bool isFinal, BrandProfile? brand,
        ActivationSnapshot? activation, string? activationImageDataUri, BatteryVoltageRangeResult? voltageRange)
    {
        try
        {
            var html = BatteryDrainReportBuilder.Render(options, start, end, samples, history, isFinal, brand,
                activation, activationImageDataUri, voltageRange);
            File.WriteAllText(reportPath, html);
        }
        catch
        {
            // Best-effort per-tick write; only the final (isFinal: true) write genuinely loses the report on failure, so that one propagates to RunAsync's try/catch; everything else is swallowed deliberately.
            if (isFinal) throw;
        }
    }
}
