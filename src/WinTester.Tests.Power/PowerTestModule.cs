using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;
using WinTester.Core.Session;
using WinTester.Tests.Gpu;

namespace WinTester.Tests.Power;

// Orchestrates two halves in one tile: Battery Health (laptop-only, a one-shot BatteryHealthReader
// pull), and PSU/Adapter Power Budget (every machine: MachineClassifier plus ComponentInventory's
// fixed-wattage table, fed by this session's own peak CPU/GPU telemetry where available, falling
// back to PowerCalibrationRunner's calibration pass per engine when it isn't). Always reports
// Passed = true; both halves are informational only.
public sealed class PowerTestModule : ITestModule
{
    private const int IdlePollSamples = 10;
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);

    private readonly ISensorService _sensorService;
    private readonly string? _cpuVendorDirectory;
    private readonly string? _gpuVendorDirectory;

    // Shared instance with GpuStressTestModule's _globalOptions.
    private readonly GlobalTestOptions _globalOptions;
    private CancellationTokenSource? _internalCts;

    public string Id => "power";
    public string DisplayName => "Power";
    public string TileColorKey => "TilePower";

    // Opens the same "(global)" settings the GPU tile's dialog edits (same GlobalTestOptions
    // instance); Power has no module-local settings of its own.
    public bool SupportsAdvancedSettings => true;

    // Read-only window onto the shared _globalOptions instance for TestOptionsWindow to bind against.
    public GlobalTestOptions GlobalOptions => _globalOptions;

    public event EventHandler<TestProgressEventArgs>? ProgressChanged;

#pragma warning disable CS0067
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;
#pragma warning restore CS0067

    public event EventHandler<TestResultEventArgs>? Completed;

    public PowerTestModule(ISensorService sensorService, string? cpuVendorDirectory = null, string? gpuVendorDirectory = null, GlobalTestOptions? globalOptions = null)
    {
        _sensorService = sensorService;
        _cpuVendorDirectory = cpuVendorDirectory;
        _gpuVendorDirectory = gpuVendorDirectory;
        _globalOptions = globalOptions ?? new GlobalTestOptions();
    }

    public async Task StartAsync(TestRunContext context, CancellationToken ct)
    {
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _internalCts.Token;
        var start = DateTime.Now;

        try
        {
            ProgressChanged?.Invoke(this, new TestProgressEventArgs
            {
                ProgressPercent = 0,
                StatusText = "Power: reading battery health and machine info...",
                SecondsRemaining = null
            });

            var machineKind = MachineClassifier.Classify();

            // Short idle poll, laptop-only, taken before any CPU/GPU load starts: the one point in
            // the Power tile's flow where the machine is guaranteed idle. Tracks the maximum
            // Remaining Time observed and a representative Degradation%/Remaining-Capacity/Percent
            // reading to hand BatteryHealthReader as an LhmBatteryHint.
            double? maxIdleRemainingTimeMinutes = null;
            LhmBatteryHint? lhmHint = null;
            var idleSnapshots = new List<SensorSnapshot>();
            if (machineKind == MachineKind.Laptop)
            {
                for (var i = 0; i < IdlePollSamples && !token.IsCancellationRequested; i++)
                {
                    var idleSnap = _sensorService.GetSnapshot();
                    idleSnapshots.Add(idleSnap);
                    var battery = idleSnap.Battery;
                    if (battery is not null)
                    {
                        if (battery.RemainingTimeMinutes is { } rt && (maxIdleRemainingTimeMinutes is null || rt > maxIdleRemainingTimeMinutes))
                            maxIdleRemainingTimeMinutes = rt;

                        // Last-poll-wins for the hint fields; these don't meaningfully change at idle.
                        lhmHint = new LhmBatteryHint(battery.DegradationPercent, battery.RemainingCapacityMwh, battery.Percent);
                    }

                    if (i < IdlePollSamples - 1)
                        await Task.Delay(IdlePollInterval, token).ConfigureAwait(false);
                }
            }

            var batteryHealth = BatteryHealthReader.Read(lhmHint);

            // Reuse this run's own CPU/GPU telemetry if this session already ran those tests.
            SessionRecord? record = null;
            string? sessionLoadFailureReason = null;
            if (context.SessionDirectory is { Length: > 0 } dir && Directory.Exists(dir))
            {
                try { record = SessionReader.Load(dir); }
                catch (Exception ex)
                {
                    // Best-effort; a partially-written or unreadable session shouldn't block Power. The
                    // reason is kept (not discarded) for sessionTelemetryReuseNote below -- see its
                    // comment for why.
                    sessionLoadFailureReason = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
            else if (context.SessionDirectory is { Length: > 0 } missingDir)
            {
                sessionLoadFailureReason = $"SessionDirectory \"{missingDir}\" does not exist.";
            }
            else
            {
                sessionLoadFailureReason = "no SessionDirectory was provided to this test run.";
            }

            IReadOnlyList<FanTelemetry> fans = Array.Empty<FanTelemetry>();
            try { fans = _sensorService.GetSnapshot().Fans; }
            catch { /* best-effort; a fan-count miss just means that line reads 0, not a module failure */ }

            PowerBudgetResult budget;
            string? calibrationLimitationNote = null;
            string? sessionTelemetryReuseNote = null;

            if (machineKind == MachineKind.Laptop)
            {
                (budget, sessionTelemetryReuseNote, calibrationLimitationNote) = await MeasureLaptopBudgetAsync(
                    context, record, PowerWindowStats.FromSnapshots(idleSnapshots), fans, token);
            }
            else
            {
                var cpuPeak = record is not null
                    ? TryGetSessionPeak(record, "cpu", snap => snap.Cpu?.PackagePowerW)
                    : null;
                var gpuPeak = record is not null
                    ? TryGetSessionPeak(record, "gpu", snap => snap.Gpus.Count == 0 ? (double?)null : snap.Gpus.Sum(g => g.PowerW ?? 0))
                    : null;

                // Reported as a KeyMetrics row (not "Notes", which the customer-facing report filters out) so
                // the reason a calibration pass ran despite CPU/GPU tests having run is visible to the technician.
                if (record is null)
                {
                    sessionTelemetryReuseNote =
                        $"Could not reuse this session's own CPU/GPU telemetry: {sessionLoadFailureReason ?? "unknown reason (record was null)."}";
                }
                else if (cpuPeak is null || gpuPeak is null)
                {
                    var missing = new List<string>();
                    if (cpuPeak is null) missing.Add("cpu");
                    if (gpuPeak is null) missing.Add("gpu");
                    sessionTelemetryReuseNote =
                        $"This session's own telemetry for the {string.Join(" and ", missing)} test(s) wasn't " +
                        $"found in the loaded session record ({record.Events.Count} event(s), {record.Samples.Count} " +
                        "sample(s) loaded); no matching TestStarted event was found for it.";
                }

                var calibrator = new PowerCalibrationRunner(_sensorService, _cpuVendorDirectory, _gpuVendorDirectory);

                if (cpuPeak is null)
                {
                    cpuPeak = await calibrator.RunCpuCalibrationAsync(context.SessionId, token, status =>
                        ProgressChanged?.Invoke(this, new TestProgressEventArgs
                        {
                            ProgressPercent = 25,
                            StatusText = status,
                            SecondsRemaining = PowerCalibrationRunner.CalibrationDurationSeconds
                        }));
                }

                if (gpuPeak is null)
                {
                    calibrationLimitationNote = IntegratedGpuToggleNote();

                    gpuPeak = await calibrator.RunGpuCalibrationAsync(context.SessionId, token, status =>
                        ProgressChanged?.Invoke(this, new TestProgressEventArgs
                        {
                            ProgressPercent = 65,
                            StatusText = status,
                            SecondsRemaining = PowerCalibrationRunner.CalibrationDurationSeconds
                        }));
                }

                ProgressChanged?.Invoke(this, new TestProgressEventArgs
                {
                    ProgressPercent = 90,
                    StatusText = "Power: calculating component power budget...",
                    SecondsRemaining = null
                });

                budget = ComponentInventory.Calculate(machineKind, cpuPeak, gpuPeak, fans);
            }

            var end = DateTime.Now;
            var keyMetrics = BuildKeyMetrics(machineKind, batteryHealth, budget, maxIdleRemainingTimeMinutes);
            if (budget.MeasurementBasis is { Length: > 0 } basis)
                keyMetrics["Power Measurement Basis"] = basis;
            if (calibrationLimitationNote is not null)
                keyMetrics["Notes"] = calibrationLimitationNote;
            if (sessionTelemetryReuseNote is not null)
                keyMetrics["Session Telemetry Reuse"] = sessionTelemetryReuseNote;

            var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_power_" + context.SessionId);
            Directory.CreateDirectory(tempDir);
            var rawLogPath = Path.Combine(tempDir, "power_combined.log");
            File.WriteAllText(rawLogPath, BuildRawLog(machineKind, batteryHealth, budget, maxIdleRemainingTimeMinutes));

            ProgressChanged?.Invoke(this, new TestProgressEventArgs
            {
                ProgressPercent = 100,
                StatusText = "Power: done.",
                SecondsRemaining = 0
            });

            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    // Both halves are informational only; this module never hard-fails on its own.
                    Passed = true,
                    FailureReason = null,
                    StartTime = start,
                    EndTime = end,
                    KeyMetrics = keyMetrics,
                    RawLogPath = rawLogPath
                }
            });
        }
        catch (OperationCanceledException)
        {
            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = false,
                    FailureReason = "Cancelled by operator",
                    StartTime = start,
                    EndTime = DateTime.Now
                }
            });
            throw;
        }
    }

    public Task StopAsync()
    {
        _internalCts?.Cancel();
        return Task.CompletedTask;
    }

    // A standalone GPU pass must raise the discrete GPU by at least this much for the scope check below to mean
    // anything, and the CPU power reading must follow at least this fraction of that rise to be treated as a
    // whole-system reading. Both are unvalidated defaults.
    private const double MinGpuRiseForScopeCheckW = 20;
    private const double ScopeFollowFraction = 0.5;

    // Laptop budget. The adapter has to carry the whole machine under combined CPU+GPU load, so the combined pass
    // always runs, even when the session's own CPU/GPU tests already produced telemetry: those ran one at a time.
    //  - With a whole-platform reading (PSYS / "Total System Power") the budget is still the summed components; the
    //    platform peak is a cross-check and a floor (ComponentInventory.WithPlatformCrossCheck).
    //  - Without one, standalone CPU and GPU draws (session windows first, short passes otherwise) are combined
    //    with the combined-pass readings: CPU package peak + discrete-GPU peak + the fixed component table. An
    //    integrated GPU is inside the CPU package, so its reading is never added. If the CPU reading is seen to
    //    rise with the discrete GPU's draw it is really a system-scope reading, and is used as measured instead.
    private async Task<(PowerBudgetResult Budget, string? ReuseNote, string? LimitationNote)> MeasureLaptopBudgetAsync(
        TestRunContext context, SessionRecord? record, PowerWindowStats idle, IReadOnlyList<FanTelemetry> fans, CancellationToken token)
    {
        var calibrator = new PowerCalibrationRunner(_sensorService, _cpuVendorDirectory, _gpuVendorDirectory);
        var cpuWindow = SessionWindowStats(record, "cpu");
        var gpuWindow = SessionWindowStats(record, "gpu");
        var platformReported = idle.PlatformPeakW is not null;
        var notes = new List<string>();

        var plannedSeconds = PowerCalibrationRunner.CalibrationDurationSeconds
            + (platformReported ? 0 : (cpuWindow is null ? PowerCalibrationRunner.CpuOnlyCalibrationDurationSeconds : 0)
                                    + (gpuWindow is null ? PowerCalibrationRunner.GpuOnlyCalibrationDurationSeconds : 0));
        var doneSeconds = 0;

        void Report(string status, int passSeconds) =>
            ProgressChanged?.Invoke(this, new TestProgressEventArgs
            {
                ProgressPercent = Math.Min(89, 10 + (int)(80.0 * doneSeconds / plannedSeconds)),
                StatusText = status,
                SecondsRemaining = passSeconds
            });

        var cpuOnly = cpuWindow;
        var gpuOnly = gpuWindow;

        if (!platformReported)
        {
            if (cpuOnly is null)
            {
                var pass = await calibrator.RunCpuOnlyStatsAsync(context.SessionId, token,
                    status => Report(status, PowerCalibrationRunner.CpuOnlyCalibrationDurationSeconds));
                doneSeconds += PowerCalibrationRunner.CpuOnlyCalibrationDurationSeconds;
                if (pass.LaunchError is null) cpuOnly = pass.Stats;
                else notes.Add($"Standalone CPU pass unavailable: {pass.LaunchError}");
            }

            if (gpuOnly is null)
            {
                var pass = await calibrator.RunGpuOnlyStatsAsync(context.SessionId, token,
                    status => Report(status, PowerCalibrationRunner.GpuOnlyCalibrationDurationSeconds));
                doneSeconds += PowerCalibrationRunner.GpuOnlyCalibrationDurationSeconds;
                if (pass.LaunchError is null) gpuOnly = pass.Stats;
                else notes.Add($"Standalone GPU pass unavailable: {pass.LaunchError}");
            }
        }

        PowerWindowStats? combined = null;
        {
            var pass = await calibrator.RunCombinedStatsAsync(context.SessionId, token,
                status => Report(status, PowerCalibrationRunner.CalibrationDurationSeconds));
            doneSeconds += PowerCalibrationRunner.CalibrationDurationSeconds;
            if (pass.LaunchError is null) combined = pass.Stats;
            else notes.Add($"Combined CPU+GPU pass unavailable: {pass.LaunchError}");
        }

        ProgressChanged?.Invoke(this, new TestProgressEventArgs
        {
            ProgressPercent = 90,
            StatusText = "Power: calculating component power budget...",
            SecondsRemaining = null
        });

        var packagePeak = MaxOrNull(cpuOnly?.PackagePeakW, combined?.PackagePeakW, gpuOnly?.PackagePeakW);
        var discretePeak = MaxOrNull(gpuOnly?.DiscreteGpuPeakW, combined?.DiscreteGpuPeakW, cpuOnly?.DiscreteGpuPeakW);
        var integratedPeak = MaxOrNull(gpuOnly?.IntegratedGpuPeakW, combined?.IntegratedGpuPeakW, cpuOnly?.IntegratedGpuPeakW);
        var platformPeak = MaxOrNull(cpuOnly?.PlatformPeakW, combined?.PlatformPeakW, gpuOnly?.PlatformPeakW);

        bool discreteGpuPresent = false;
        try { discreteGpuPresent = _sensorService.GetSnapshot().Gpus.Any(g => !GpuNameClassifier.IsLikelyIntegratedGpuName(g.Name)); }
        catch { /* best-effort; an unreadable snapshot just leaves the discrete-GPU check false */ }

        var reuseNote =
            $"Session windows reused: cpu {(cpuWindow is null ? "not found" : $"{cpuWindow.SampleCount} sample(s)")}, " +
            $"gpu {(gpuWindow is null ? "not found" : $"{gpuWindow.SampleCount} sample(s)")}. " +
            (platformReported ? "Whole-platform power reading available; standalone passes skipped. " : "No whole-platform power reading. ") +
            string.Join(" ", notes);
        var limitationNote = IntegratedGpuToggleNote();

        PeakPowerResult CpuInfo(string detail) => packagePeak is not null
            ? new PeakPowerResult(packagePeak, PowerSampleSource.CalibrationPass, detail)
            : new PeakPowerResult(null, PowerSampleSource.Unavailable, "no CPU package-power reading was captured.");

        PeakPowerResult DiscreteInfo(string detail) => discretePeak is not null
            ? new PeakPowerResult(discretePeak, PowerSampleSource.CalibrationPass, detail)
            : new PeakPowerResult(null, PowerSampleSource.Unavailable, "no discrete GPU power reading was captured.");

        // Discrete-GPU line for the summed budget (also used with a platform reading, see below).
        PeakPowerResult gpuResult;
        if (discretePeak is not null)
            gpuResult = DiscreteInfo("Discrete GPU only; highest of the standalone and combined readings.");
        else if (!discreteGpuPresent)
            gpuResult = new PeakPowerResult(0, PowerSampleSource.CalibrationPass,
                integratedPeak is { } ip
                    ? $"Integrated GPU only; its reported {ip:0.0}W is inside the CPU package reading and is not added."
                    : "Integrated GPU only; its draw is inside the CPU package reading.");
        else
            gpuResult = DiscreteInfo("");

        if (platformPeak is not null)
        {
            var sum = ComponentInventory.Calculate(MachineKind.Laptop,
                CpuInfo("CPU package only; highest of the standalone and combined readings."), gpuResult, fans);
            var budget = ComponentInventory.WithPlatformCrossCheck(sum,
                new PeakPowerResult(platformPeak, PowerSampleSource.CalibrationPass,
                    "Highest whole-platform reading across the combined pass and this session's CPU/GPU test windows."));
            return (budget, reuseNote, limitationNote);
        }

        var gpuRise = gpuOnly?.DiscreteGpuPeakW is { } gpuPeakOnly && idle.DiscreteGpuAvgW is { } gpuIdle ? gpuPeakOnly - gpuIdle : (double?)null;
        var readingRise = gpuOnly?.PackagePeakW is { } readingPeak && idle.PackageAvgW is { } readingIdle ? readingPeak - readingIdle : (double?)null;
        var readingIncludesGpu = gpuRise is { } gr && gr >= MinGpuRiseForScopeCheckW
                                 && readingRise is { } rr && rr >= ScopeFollowFraction * gr;

        if (readingIncludesGpu && packagePeak is not null)
        {
            var budget = ComponentInventory.CalculateFromMeasuredPlatform(
                new PeakPowerResult(packagePeak, PowerSampleSource.CalibrationPass,
                    "Highest CPU power reading across the standalone and combined passes."),
                CpuInfo("CPU power reading (includes GPU draw)."),
                DiscreteInfo("Discrete GPU peak."),
                $"The CPU power reading rose {readingRise!.Value:0.0}W when the discrete GPU rose {gpuRise!.Value:0.0}W with the CPU idle, " +
                "so it is a whole-system reading. It is used as measured; no component wattages are added to it.");
            return (budget, reuseNote, limitationNote);
        }

        var summed = ComponentInventory.Calculate(MachineKind.Laptop,
            CpuInfo("CPU package only; highest of the standalone and combined readings."), gpuResult, fans);
        return (summed with
        {
            MeasurementBasis = "Summed components: CPU package peak plus discrete-GPU peak (an integrated GPU is inside the CPU package and is not added), " +
                               "plus the fixed component table. No whole-platform power reading was available."
        }, reuseNote, limitationNote);
    }

    private static double? MaxOrNull(params double?[] values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count > 0 ? present.Max() : null;
    }

    // Stats over the hardware snapshots inside a test's TestStarted..TestCompleted window; null when that test did not run this session.
    private static PowerWindowStats? SessionWindowStats(SessionRecord? record, string testId)
    {
        if (record is null) return null;

        var startedEvt = record.Events.FirstOrDefault(e => e.Type == SessionEventType.TestStarted && e.TestId == testId);
        if (startedEvt is null) return null;

        var completedEvt = record.Events.FirstOrDefault(e => e.Type == SessionEventType.TestCompleted && e.TestId == testId);
        var snapshots = record.Samples
            .Where(s => s.Kind == SessionSampleKind.HardwareSnapshot && s.Snapshot is not null
                        && s.Timestamp >= startedEvt.Timestamp
                        && (completedEvt is null || s.Timestamp <= completedEvt.Timestamp))
            .Select(s => s.Snapshot!);

        var stats = PowerWindowStats.FromSnapshots(snapshots);
        return stats.SampleCount > 0 ? stats : null;
    }

    // FurMark2's OpenGL engine has no GPU-selection flag, so Advanced Mode's "run on the integrated GPU if a
    // dedicated one is present" toggle cannot steer the Power test's GPU load; it follows the OS default adapter.
    private string? IntegratedGpuToggleNote()
    {
        try
        {
            var gpuNames = _sensorService.GetSnapshot().Gpus.Select(g => g.Name).ToList();
            var bothPresent = gpuNames.Any(n => !GpuNameClassifier.IsLikelyIntegratedGpuName(n)) &&
                              gpuNames.Any(GpuNameClassifier.IsLikelyIntegratedGpuName);
            if (bothPresent && !_globalOptions.RunGpuTestOnIntegratedIfDedicatedPresent)
            {
                return "Advanced Mode: a dedicated GPU was detected alongside an integrated GPU and \"run GPU test on iGPU if " +
                       "dedicated GPU present\" is off, but the Power test's own GPU load uses FurMark2's OpenGL engine, which has " +
                       "no GPU-selection flag, so this toggle can't steer it; it follows the OS default adapter.";
            }
        }
        catch { /* best-effort; a snapshot failure just omits the note */ }

        return null;
    }

    // Finds testId's TestStarted/TestCompleted window in this session's event log and returns the
    // peak value selector sees across snapshots in that window; returns null when the engine didn't
    // run this session (caller falls back to calibration).
    private static PeakPowerResult? TryGetSessionPeak(SessionRecord record, string testId, Func<SensorSnapshot, double?> selector)
    {
        var startedEvt = record.Events.FirstOrDefault(e => e.Type == SessionEventType.TestStarted && e.TestId == testId);
        if (startedEvt is null) return null;

        var completedEvt = record.Events.FirstOrDefault(e => e.Type == SessionEventType.TestCompleted && e.TestId == testId);
        var windowStart = startedEvt.Timestamp;
        var windowEnd = completedEvt?.Timestamp;

        double? peak = null;
        foreach (var sample in record.Samples)
        {
            if (sample.Kind != SessionSampleKind.HardwareSnapshot || sample.Snapshot is null) continue;
            if (sample.Timestamp < windowStart) continue;
            if (windowEnd is { } we && sample.Timestamp > we) continue;

            if (selector(sample.Snapshot) is { } value && (peak is null || value > peak))
                peak = value;
        }

        return peak is not null
            ? new PeakPowerResult(peak, PowerSampleSource.SessionTelemetry, $"Peak observed during this session's own \"{testId}\" test.")
            : new PeakPowerResult(null, PowerSampleSource.Unavailable, $"\"{testId}\" ran this session, but no matching power sensor reading was captured.");
    }

    private static Dictionary<string, object> BuildKeyMetrics(MachineKind machineKind, BatteryHealthResult batteryHealth, PowerBudgetResult budget, double? maxIdleRemainingTimeMinutes)
    {
        var km = new Dictionary<string, object>
        {
            ["Machine Kind"] = machineKind.ToString(),
            ["Battery Health"] = batteryHealth.HasBatteryInterface
                ? (batteryHealth.HealthPercent is { } hp ? $"{hp:0.0}%" : "N/A [full-charge capacity unavailable")
                : "N/A] no battery interface detected",
            // Everything else BatteryHealthReader could gather (root\WMI + SetupAPI/IOCTL combined).
            // Shown as an explicit N/A when unreported rather than omitted, so the field's absence reads
            // as "checked, unavailable" rather than "never checked".
            ["Battery Cycle Count"] = batteryHealth.HasBatteryInterface
                ? (batteryHealth.CycleCount is { } cyc ? cyc.ToString() : "N/A")
                : "N/A [no battery interface detected",
            ["Battery Serial Number"] = batteryHealth.HasBatteryInterface
                ? (batteryHealth.SerialNumber ?? "N/A")
                : "N/A] no battery interface detected",
            ["Battery Chemistry"] = batteryHealth.HasBatteryInterface
                ? (batteryHealth.Chemistry ?? "N/A")
                : "N/A: no battery interface detected",
            ["Total Component Wattage (W)"] = Math.Round(budget.TotalWatts, 1),
            [RecommendedBudgetLabel(machineKind)] = Math.Round(budget.RecommendedWatts, 1),
            ["Recommended PowerSupply (W)"] = budget.ExceedsTierList
                ? $"{ComponentInventory.RoundUpToNearest50(budget.RecommendedWatts)} (exceeds standard tier list)"
                : budget.RecommendedTierWatts!.Value.ToString(),
            ["CPU Peak Power"] = FormatPeak(budget.CpuPeak),
            ["GPU Peak Power"] = FormatPeak(budget.GpuPeak),
        };

        // Laptop-only: the idle poll producing this only runs for MachineKind.Laptop, so a desktop
        // key here would always read N/A.
        if (machineKind == MachineKind.Laptop)
        {
            km["Maximum Estimated Battery Life (Idle)"] = maxIdleRemainingTimeMinutes is { } mins
                ? FormatMinutes(mins)
                : "N/A";
        }

        // GPU, CPU, storage, optical, fans, then everything else, preserves ComponentInventory's
        // order. Component names aren't unique (e.g. two "NVMe SSD" lines on a dual-drive machine),
        // and a bare "Component: {name}" key would overwrite the first drive with the second.
        // Disambiguate repeats with a "#2", "#3", ... suffix.
        var nameOccurrences = new Dictionary<string, int>();
        foreach (var c in budget.Components)
        {
            int occurrence = nameOccurrences.TryGetValue(c.Name, out var n) ? n + 1 : 1;
            nameOccurrences[c.Name] = occurrence;
            var key = occurrence == 1 ? $"Component: {c.Name}" : $"Component: {c.Name} #{occurrence}";
            km[key] = $"{c.Watts:0.0}W" + (c.Detail is { Length: > 0 } d ? $", {d}" : "");
        }

        return km;
    }

    // Laptops get the plain figure (no headroom percentage; the charger spec is picked separately, see
    // ComponentInventory.Calculate), while the desktop label has to say its 20% headroom is included.
    private static string RecommendedBudgetLabel(MachineKind machineKind) =>
        machineKind == MachineKind.Laptop
            ? "Recommended Budget (W)"
            : "Recommended Budget incl. 20% headroom (W)";

    private static string FormatMinutes(double minutes)
    {
        var span = TimeSpan.FromMinutes(minutes);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";
    }

    // CalibrationPass results omit their source/detail annotation in KeyMetrics. SessionTelemetry
    // and Unavailable keep theirs; Unavailable's Detail is the only place that explains why a
    // reading is missing.
    private static string FormatPeak(PeakPowerResult peak)
    {
        if (peak.Source == PowerSampleSource.CalibrationPass)
            return peak.Watts is { } cw ? $"{cw:0.0}W" : "N/A";

        return peak.Watts is { } w
            ? $"{w:0.0}W ({peak.Source}) [{peak.Detail}"
            : $"N/A ({peak.Source})] {peak.Detail}";
    }

    private static string BuildRawLog(MachineKind machineKind, BatteryHealthResult batteryHealth, PowerBudgetResult budget, double? maxIdleRemainingTimeMinutes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinTester Power Test: Battery Health + PSU/Adapter Power Budget");
        sb.AppendLine($"Running elevated: {(ElevationCheck.IsRunningElevated() ? "Yes" : "No")}");
        sb.AppendLine($"Machine kind: {machineKind}");
        if (machineKind == MachineKind.Laptop)
        {
            sb.AppendLine("Maximum Estimated Battery Life (Idle): " +
                (maxIdleRemainingTimeMinutes is { } mins ? FormatMinutes(mins) : "N/A"));
        }
        sb.AppendLine();

        sb.AppendLine("--- Battery Health ---");
        if (batteryHealth.HasBatteryInterface)
        {
            sb.AppendLine($"Design capacity: {Fmt(batteryHealth.DesignCapacityWh)} Wh");
            sb.AppendLine($"Full-charge capacity: {Fmt(batteryHealth.FullChargeCapacityWh)} Wh");
            sb.AppendLine($"Health: {(batteryHealth.HealthPercent is { } h ? $"{h:0.0}%" : "N/A")}");
            sb.AppendLine($"Cycle count: {(batteryHealth.CycleCount is { } cc ? cc.ToString() : "N/A")}");
            sb.AppendLine($"Chemistry: {batteryHealth.Chemistry ?? "N/A"}");
            sb.AppendLine($"Serial number: {batteryHealth.SerialNumber ?? "N/A"}");
            sb.AppendLine($"Manufacturer: {batteryHealth.ManufacturerName ?? "N/A"}");
            sb.AppendLine($"Device name: {batteryHealth.DeviceName ?? "N/A"}");
        }
        else
        {
            sb.AppendLine("No battery interface detected; section not applicable (N/A, not a fail).");
        }
        sb.AppendLine(batteryHealth.Detail);
        sb.AppendLine();

        sb.AppendLine("--- PSU / Adapter Power Budget ---");
        sb.AppendLine($"CPU peak: {FormatPeak(budget.CpuPeak)}");
        sb.AppendLine($"GPU peak: {FormatPeak(budget.GpuPeak)}");
        if (budget.MeasurementBasis is { Length: > 0 } basis)
            sb.AppendLine($"Basis: {basis}");
        sb.AppendLine();
        sb.AppendLine("Component breakdown:");
        foreach (var c in budget.Components)
            sb.AppendLine($"  {c.Name,-60} {c.Watts,8:0.0} W   {c.Detail}");
        sb.AppendLine();
        sb.AppendLine($"Total: {budget.TotalWatts:0.0} W");
        sb.AppendLine(machineKind == MachineKind.Laptop
            ? $"Recommended (no headroom, laptop): {budget.RecommendedWatts:0.0} W"
            : $"Recommended (Total x 1.20 headroom): {budget.RecommendedWatts:0.0} W");
        sb.AppendLine(budget.ExceedsTierList
            ? $"Recommended PSU tier: {ComponentInventory.RoundUpToNearest50(budget.RecommendedWatts)}W " +
              (machineKind == MachineKind.Laptop
                  ? "(exceeds the laptop charger tier list [rounded to the nearest 50W rather than clamped)"
                  : "(exceeds the standard tier list] rounded to the nearest 50W rather than clamped)")
            : $"Recommended PSU tier: {budget.RecommendedTierWatts}W");

        return sb.ToString();
    }

    private static string Fmt(double? v) => v is { } value ? value.ToString("0.0") : "N/A";
}
