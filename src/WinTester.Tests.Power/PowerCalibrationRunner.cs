using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;
using WinTester.Tests.Cpu;
using WinTester.Tests.Gpu;

namespace WinTester.Tests.Power;

// Runs short, discard-after-capture load passes with the vendored CPU/GPU engines while sampling ISensorService,
// for the cases where Power runs standalone or the session did not produce usable CPU/GPU telemetry. Produces no
// pass/fail verdict. Desktop passes track a single peak; the laptop passes return every snapshot taken during the
// pass (PowerWindowStats) so the caller can read platform, package and per-GPU power from the same window. The GPU
// pass positions FurMark2's render window off-screen since there is no modal to embed it into.
public sealed class PowerCalibrationRunner
{
    public const int CalibrationDurationSeconds = 60;

    // Laptop standalone CPU-only pass: the CPU's own draw with the GPU idle.
    public const int CpuOnlyCalibrationDurationSeconds = 30;

    // Laptop standalone GPU-only pass: the GPU's own draw, uncontended by the CPU stress engine.
    public const int GpuOnlyCalibrationDurationSeconds = 30;

    private readonly string _cpuVendorDirectory;
    private readonly string _gpuVendorDirectory;
    private readonly ISensorService _sensorService;

    public PowerCalibrationRunner(ISensorService sensorService, string? cpuVendorDirectory = null, string? gpuVendorDirectory = null)
    {
        _sensorService = sensorService;
        _cpuVendorDirectory = cpuVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "intel-pdt");
        _gpuVendorDirectory = gpuVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "furmark2");
    }

    // Runs a 60s math_primenumber pass (vendor-neutral, no MKL dependency) while sampling ISensorService
    // for peak CPU package power.
    public async Task<PeakPowerResult> RunCpuCalibrationAsync(string sessionId, CancellationToken ct, Action<string>? onStatus = null)
    {
        onStatus?.Invoke("Power: running short CPU calibration pass (no historic CPU telemetry this session)...");

        var runner = new IpdtModuleRunner(_cpuVendorDirectory);
        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_power_cpu_cal_" + sessionId);
        Directory.CreateDirectory(tempDir);
        var resultPath = Path.Combine(tempDir, "math_primenumber_result.txt");

        double? peakWatts = null;
        using var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var samplingTask = SamplePeakAsync(samplingCts.Token, snap => snap.Cpu?.PackagePowerW, w => peakWatts = MaxOf(peakWatts, w));

        IpdtModuleRunner.Result runResult;
        try
        {
            runResult = await runner.RunAsync("math_primenumber", CalibrationDurationSeconds, resultPath, ct);
        }
        finally
        {
            samplingCts.Cancel();
            await samplingTask;
        }

        if (peakWatts is not null)
            return new PeakPowerResult(peakWatts, PowerSampleSource.CalibrationPass, "60s math_primenumber calibration pass.");

        var reason = runResult.LaunchError ?? DescribeCpuPowerUnavailable("no CPU package-power sensor reading was captured during the calibration pass.");
        return new PeakPowerResult(null, PowerSampleSource.Unavailable, $"Calibration pass ran, but {reason}");
    }

    // Runs a 60s FurMark2 OpenGL pass, window positioned off-screen, while sampling ISensorService for
    // peak summed GPU power.
    public async Task<PeakPowerResult> RunGpuCalibrationAsync(string sessionId, CancellationToken ct, Action<string>? onStatus = null)
    {
        onStatus?.Invoke("Power: running short GPU calibration pass (no historic GPU telemetry this session)...");

        var runner = new FurmarkRunner(_gpuVendorDirectory);
        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_power_gpu_cal_" + sessionId);
        Directory.CreateDirectory(tempDir);
        var csvPath = Path.Combine(tempDir, "gpu_data.csv");

        // Off-screen, fixed small size.
        var offscreenRect = new WindowScreenRect(-10000, -10000, 640, 360);

        double? peakWatts = null;
        using var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var samplingTask = SamplePeakAsync(samplingCts.Token,
            snap => SumGpuPowerOrNull(snap),
            w => peakWatts = MaxOf(peakWatts, w));

        FurmarkRunner.Result runResult;
        try
        {
            runResult = await runner.RunAsync(FurmarkEngine.OpenGl, CalibrationDurationSeconds, offscreenRect, csvPath, ct);
        }
        finally
        {
            samplingCts.Cancel();
            await samplingTask;
        }

        if (peakWatts is not null)
            return new PeakPowerResult(peakWatts, PowerSampleSource.CalibrationPass, "60s FurMark2 OpenGL calibration pass (off-screen window).");

        var reason = runResult.LaunchError ?? "no GPU power sensor reading was captured during the calibration pass.";
        return new PeakPowerResult(null, PowerSampleSource.Unavailable, $"Calibration pass ran, but {reason}");
    }

    // Laptop standalone CPU pass: math_primenumber only, GPU left idle.
    public Task<PowerPassResult> RunCpuOnlyStatsAsync(string sessionId, CancellationToken ct, Action<string>? onStatus = null) =>
        RunLoadPassAsync("cpu_only", cpu: true, gpu: false, CpuOnlyCalibrationDurationSeconds, sessionId, ct, onStatus,
            "Power: running 30s standalone CPU pass...");

    // Laptop standalone GPU pass: FurMark2 OpenGL only, CPU left idle. Run on its own because the OpenGL demo is
    // draw-call-bound and is starved of CPU time whenever the all-core CPU pass runs alongside it.
    public Task<PowerPassResult> RunGpuOnlyStatsAsync(string sessionId, CancellationToken ct, Action<string>? onStatus = null) =>
        RunLoadPassAsync("gpu_only", cpu: false, gpu: true, GpuOnlyCalibrationDurationSeconds, sessionId, ct, onStatus,
            "Power: running 30s standalone GPU pass...");

    // Laptop combined pass: CPU and GPU engines together, the closest a short pass gets to the worst-case draw
    // the charger has to supply.
    public Task<PowerPassResult> RunCombinedStatsAsync(string sessionId, CancellationToken ct, Action<string>? onStatus = null) =>
        RunLoadPassAsync("combined", cpu: true, gpu: true, CalibrationDurationSeconds, sessionId, ct, onStatus,
            "Power: running 60s combined CPU+GPU pass...");

    private async Task<PowerPassResult> RunLoadPassAsync(
        string tag, bool cpu, bool gpu, int seconds, string sessionId, CancellationToken ct, Action<string>? onStatus, string status)
    {
        onStatus?.Invoke(status);

        var snapshots = new List<SensorSnapshot>();
        using var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var samplingTask = SampleSnapshotsAsync(samplingCts.Token, snapshots);

        var runs = new List<Task>();
        Task<IpdtModuleRunner.Result>? cpuRun = null;
        Task<FurmarkRunner.Result>? gpuRun = null;

        if (cpu)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"WinTester_power_{tag}_cpu_" + sessionId);
            Directory.CreateDirectory(tempDir);
            cpuRun = new IpdtModuleRunner(_cpuVendorDirectory)
                .RunAsync("math_primenumber", seconds, Path.Combine(tempDir, "math_primenumber_result.txt"), ct);
            runs.Add(cpuRun);
        }

        if (gpu)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"WinTester_power_{tag}_gpu_" + sessionId);
            Directory.CreateDirectory(tempDir);
            gpuRun = new FurmarkRunner(_gpuVendorDirectory)
                .RunAsync(FurmarkEngine.OpenGl, seconds, new WindowScreenRect(-10000, -10000, 640, 360), Path.Combine(tempDir, "gpu_data.csv"), ct);
            runs.Add(gpuRun);
        }

        try
        {
            await Task.WhenAll(runs);
        }
        finally
        {
            samplingCts.Cancel();
            await samplingTask;
        }

        // A pass whose engine never started only sampled an idle machine, so its stats are not usable as load data.
        var launchError = cpuRun?.Result.LaunchError ?? gpuRun?.Result.LaunchError;
        return new PowerPassResult(PowerWindowStats.FromSnapshots(snapshots), launchError);
    }

    private async Task SampleSnapshotsAsync(CancellationToken ct, List<SensorSnapshot> snapshots)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    snapshots.Add(_sensorService.GetSnapshot());
                }
                catch
                {
                    // Best-effort sampling; a transient sensor read failure shouldn't abort the pass, just skip this tick.
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path when the calibration engine finishes or is cancelled.
        }
    }

    private async Task SamplePeakAsync(CancellationToken ct, Func<SensorSnapshot, double?> selector, Action<double> onSample)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var snap = _sensorService.GetSnapshot();
                    if (selector(snap) is { } value)
                        onSample(value);
                }
                catch
                {
                    // Best-effort sampling; a transient sensor read failure shouldn't abort the pass, just skip this tick.
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path when the calibration engine finishes or is cancelled.
        }
    }

    // Sums per-GPU power for the sampling loop. Previously did `g.PowerW ?? 0`, which folded a detected
    // GPU's *unavailable* individual reading into the sum as a real zero (see field-issues tracker 2.2's
    // bonus finding) — indistinguishable downstream from a genuine 0.0W reading, and one that quietly
    // understated the recommended PSU wattage. Null-when-no-GPUs was already correct and is unchanged;
    // the fix is that any one GPU's null reading now makes the whole sample null too (skipped by the
    // sampler, same as any other transient miss) rather than silently substituting zero for it.
    private static double? SumGpuPowerOrNull(SensorSnapshot snap)
    {
        if (snap.Gpus.Count == 0) return null;
        if (snap.Gpus.Any(g => g.PowerW is null)) return null;
        return snap.Gpus.Sum(g => g.PowerW!.Value);
    }

    // Wires ISensorService's existing elevation-aware SensorWarning (LibreHardwareSensorService's
    // IsRunningElevated()/BuildSensorWarning, see field-issues tracker 2.2) into the CPU package-power
    // fallback text, instead of the generic "no reading was captured" that gave no explanation even when
    // the real cause (unelevated process, RAPL/MSR needs admin rights) was already known elsewhere in the
    // codebase. Only the CPU-power fallbacks use this; GPU power sampling has no equivalent elevation
    // dependency, so its own fallback text is left as-is.
    private string DescribeCpuPowerUnavailable(string genericReason)
    {
        try
        {
            if (_sensorService.GetSnapshot().SensorWarning is { Length: > 0 } warning)
                return warning;
        }
        catch
        {
            // Best-effort context only; fall through to the generic reason rather than fail the calibration pass over it.
        }

        return genericReason;
    }

    private static double? MaxOf(double? current, double candidate) =>
        current is null ? candidate : Math.Max(current.Value, candidate);
}
