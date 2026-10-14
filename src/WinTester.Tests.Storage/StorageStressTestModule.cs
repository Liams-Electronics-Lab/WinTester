using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;
using WinTester.Core.Storage;

namespace WinTester.Tests.Storage;

// Which half of one disk's pass is currently running/being displayed: drives the benchmark overlay's read/write bar selection (StorageBenchmarkControl).
public enum StorageBenchmarkPhase { Write, Read }

// Coarse per-drive test-state driving the grid overlay's per-cell border: pulsing red while a drive's passes run, solid green once every profile/phase for that drive is done. Deliberately just three states, not a sub-stage enum; the 4-bar readout inside each cell (StorageLiveEventArgs.Profile/Phase/SpeedMBs) already carries the finer "which profile/phase, what speed" detail; this only answers whether the cell's border is pulsing, solid green, or at rest.
public enum DriveTestState { Pending, Testing, Complete }

// Extra, module-specific telemetry beyond the shared ITestModule contract, consumed by MainViewModel to
// drive StorageBenchmarkViewModel/StorageBenchmarkControl: the same extra constructor/event-level
// plumbing pattern GpuStressTestModule established for FurMark's window embedding, though this module
// needs a live numeric feed instead (fio has no GUI to reparent). Profile identifies which of the two
// benchmark profiles a sample belongs to, since the overlay shows 4 bars per drive; State drives the
// per-cell border. The initial "every disk exists, none started yet" state is announced separately via
// StorageStressTestModule.DisksAnnounced rather than through this event, since a dedicated one-shot event
// beats N synthetic per-disk instances of this class.
public sealed class StorageLiveEventArgs : EventArgs
{
    public required string DriveLetter { get; init; }
    public required string Model { get; init; }
    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }
    public required FioProfile Profile { get; init; }
    public required StorageBenchmarkPhase Phase { get; init; }
    public required DriveTestState State { get; init; }

    // Null means "no sample yet for this profile/phase"; the overlay shows that bar at rest (empty, no number) rather than a stale figure from a previous phase/profile/disk.
    public double? SpeedMBs { get; init; }
}

// Phase 3.4: pairs each disk with its quick-pass decision so the overlay can show *why* a drive's Rand4K
// bars never move instead of leaving them at "--" indefinitely, which reads as "stuck" rather than
// "skipped" to an operator watching the grid. RandSkipReason is null when the full four-pass suite is
// running for this disk (SSD/NVMe, or an HDD/unknown drive the operator forced via Advanced Mode's
// StorageTestOptions.ForceFullSuitePhysicalDriveIndexes).
public sealed record AnnouncedStorageDisk(PhysicalDiskInfo Disk, string? RandSkipReason);

// Per-disk SMART health and temperature telemetry, fired once per disk per
// StorageStressTestModule.EmitDriveTelemetry call: once during the idle rest, then on a fixed 5s cadence.
// Kept separate from StorageLiveEventArgs rather than folding TempC/HealthPercent onto it: speed events
// are keyed to one profile/phase and fire on their own irregular per-pass cadence, while temperature/health
// apply to the whole disk regardless of active profile/phase and fire on a fixed 5s cadence. Conflating the
// two would mean duplicating temp/health onto every speed event (implying a false per-profile/phase
// granularity neither sensor has) or gating temp/health updates on a speed event happening to land at the
// same moment.
public sealed class StorageDriveTelemetryEventArgs : EventArgs
{
    public required string DriveLetter { get; init; }
    public required string Model { get; init; }

    // Null means "no temperature sensor found for this disk," not "0°C"; same nullable-means-not-found convention SensorSnapshot's fields use throughout this codebase.
    public double? TempC { get; init; }

    // Null means "no SMART health/remaining-life sensor exposed for this disk" (common on older SATA SSDs
    // and most HDDs): sourced from LibreHardwareSensorService.ReadStorage's Life/Health/Percentage Used
    // name matching.
    public double? HealthPercent { get; init; }

    // True only for the single reading taken during the idle rest before the first disk's first pass starts; every later reading (once per StorageStressTestModule.TelemetryPollInterval) is false. The UI doesn't currently distinguish the two visually, but the flag is preserved here and in the session log for future use; cheaper to carry through now than to reconstruct "was this the first sample" from timestamps later.
    public required bool IsIdleReading { get; init; }
}

// Storage test: up to two KDiskMark/CrystalDiskMark-standard profiles per physical disk, one disk at a time; Seq1M Q8T1 (sequential, large I/O, best-case throughput) and Rand4K Q1T1 (random, small I/O, worst-case/everyday-feel IOPS); see FioProfile. Engine is fio's CLI, process-isolated since fio is GPLv2. Replaces the earlier MockTestModule registered under id "storage".
// Scope: the roadmap lists four KDiskMark/CrystalDiskMark-standard profiles (Seq1M Q8T1, Seq1M Q1T1, Rand4K Q32T16, Rand4K Q1T1) plus SMART health/fast-fail; this module implements two of the four, the other two remain open. SMART health is partially covered via LibreHardwareMonitor's "Remaining Life"-style Level sensor through ISensorService (see _sensorService/DriveTelemetryChanged), not via real smartctl integration; finer-grained SMART attributes (reallocated-sector count, a real pass/fail threshold rather than a display number) remain open.
// One disk at a time, four passes each on an SSD/NVMe (two on a mechanical HDD or an undetermined drive type — Phase 3's quick-pass gating, see BuildDiskPlan): DiskInfoProvider resolves every testable physical disk to one lettered volume, benchmarked sequentially, never concurrently (concurrent fio jobs would contend for CPU/PCIe bandwidth and produce numbers reflecting neither disk's real standalone performance). Order per disk: Seq1M Q8T1 write, Seq1M Q8T1 read, then (unless quick-pass gated) Rand4K Q1T1 write, Rand4K Q1T1 read [write always precedes its matching read since it creates/populates the test file the read then reads back. Both profiles deliberately share one test file/size rather than each allocating and deleting their own (Rand4K reuses whatever Seq1M already wrote] a fixed-size region is that size regardless of which profile touches it, so a separate file would only add allocate/delete overhead). The test file is deleted once, after all passes for that disk, before moving to the next disk.
// Phase 3 quick-pass gating: Rand4K Q1T1 is a genuinely multi-hour test on a real spinning disk (see the
// field-issues tracker's 1.4 — a field run's Rand4K Q1T1 write ran for 20+ minutes at sub-1MB/s before being
// operator-aborted, on a 2GiB file it never finished). BuildDiskPlan below skips it by default for any disk
// DriveTypeDetector reports as DriveMediaKind.Hdd or Unknown (an undetermined drive type is treated the same
// as a confirmed HDD — see that enum's own remark), running Seq1M Q8T1 alone instead (a sequential pass on
// the same file completes in on the order of a minute even on a spinning disk, no further reduction needed).
// StorageTestOptions.ForceFullSuitePhysicalDriveIndexes lets a technician override this per disk from
// Advanced Mode when they specifically want the full suite on a spinning disk anyway.
public sealed class StorageStressTestModule : ITestModule
{
    // Test file size for each disk's passes, from FioRunner.DefaultTestFileSizeBytes.
    private readonly long _testFileSizeBytes;

    // Directory fio.exe is vendored in.
    private readonly string _vendorDirectory;
    private readonly FioRunner _runner;
    private readonly Func<IReadOnlyList<PhysicalDiskInfo>>? _enumerateDisks;
    private CancellationTokenSource? _internalCts;

    // Supplies live per-disk temperature/SMART-health readings. Optional, defaulted to null; same "degrade gracefully rather than require a dependency every caller must supply" posture GpuStressTestModule's sensorService param uses for its VRAM cross-check. Null skips idle-temp capture and periodic temp/health logging entirely, with a note added to the report explaining why rather than a silently incomplete report.
    private readonly ISensorService? _sensorService;

    // Cadence for temperature/SMART-health sampling once the idle reading is taken. Independent of
    // MainViewModel's own unrelated 5s shared hardware-snapshot poll: this module keeps its own poll
    // because that shared snapshot has no drive-letter linkage and isn't attributed to the storage test.
    public static readonly TimeSpan TelemetryPollInterval = TimeSpan.FromSeconds(5);

    // The two profiles run against every disk, in this fixed order; Seq before Rand, no dependency between them, just matching CrystalDiskMark's own default top-to-bottom ordering.
    private static readonly FioProfile[] Profiles = { FioProfile.Seq1MQ8T1, FioProfile.Rand4KQ1T1 };

    public string Id => "storage";
    public string DisplayName => "Storage";
    public string TileColorKey => "TileStorage";

    public bool SupportsAdvancedSettings => true;

    // Advanced Mode settings for this module, read fresh in StartAsync: same mutable/long-lived-singleton pattern as other modules' Options properties.
    public StorageTestOptions Options { get; } = new();

    public event EventHandler<TestProgressEventArgs>? ProgressChanged;

    // Carries the same StorageDriveTelemetryEventArgs data as DriveTelemetryChanged, reshaped into
    // SensorSample's flat string-keyed dictionary (keyed "<DriveLetter>[ Idle] TempC/HealthPercent") so it
    // lands in the session's samples.jsonl via MainViewModel's AppendModuleSample wiring.
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;

    public event EventHandler<TestResultEventArgs>? Completed;

    // Fires exactly once per run, right after disks are enumerated and before any pass starts, carrying the full disk list (each paired with its quick-pass decision, Phase 3.4) so the grid overlay can build every cell (drive info, initial Pending border, Rand4K skip messaging) and compute its layout up front rather than growing cell-by-cell as each disk's turn comes up; the grid is meant to show every drive simultaneously from frame one. Same thread-affinity assumption as LiveSpeedChanged below.
    public event EventHandler<IReadOnlyList<AnnouncedStorageDisk>>? DisksAnnounced;

    // Fires once per disk-phase transition (no sample yet) and roughly once per second thereafter while a phase runs, sourced from FioRunner's process-I/O-counter poll rather than fio's own --status-interval stdout (see FioRunner). MainViewModel wires this into StorageBenchmarkViewModel the same way it wires GpuStressTestModule's constructor callbacks into the running-state overlay. Relies on the call chain resuming on the UI thread's SynchronizationContext rather than a backgrounded Task.Run, same assumption every other cross-project event in this codebase makes; not wrapped in Dispatcher.Invoke since nothing else here is either.
    public event EventHandler<StorageLiveEventArgs>? LiveSpeedChanged;

    // Fires once for the pre-test idle reading and once per TelemetryPollInterval thereafter, for every disk in the run; independent of which disk's fio passes happen to be active, covering every disk for the run's whole duration. Null ISensorService (see _sensorService) means this event never fires for that run.
    public event EventHandler<StorageDriveTelemetryEventArgs>? DriveTelemetryChanged;

    // testFileSizeBytes defaults to FioRunner.DefaultTestFileSizeBytes; loopCount defaults to FioRunner.DefaultLoopCount; vendorDirectory defaults to vendor/fio next to the exe.
    // enumerateDisks defaults to DiskInfoProvider.EnumerateTestableDisks, overridable for testing without real WMI/hardware: same delegate-seam pattern GpuStressTestModule's getEmbedRect/getParentHwnd use.
    public StorageStressTestModule(
        long? testFileSizeBytes = null,
        int? loopCount = null,
        string? vendorDirectory = null,
        Func<IReadOnlyList<PhysicalDiskInfo>>? enumerateDisks = null,
        ISensorService? sensorService = null)
    {
        _testFileSizeBytes = testFileSizeBytes ?? FioRunner.DefaultTestFileSizeBytes;
        if (loopCount is { } lc) Options.Passes = lc;
        _vendorDirectory = vendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "fio");
        _runner = new FioRunner(_vendorDirectory);
        _enumerateDisks = enumerateDisks;
        _sensorService = sensorService;
    }

    public async Task StartAsync(TestRunContext context, CancellationToken ct)
    {
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _internalCts.Token;
        var start = DateTime.Now;

        // DiskInfoProvider reports why each disk was skipped, but only on the real default path; the _enumerateDisks test seam's delegate signature has no way to hand diagnostics back, so a caller overriding it just gets the plain generic failure reason below.
        IReadOnlyList<PhysicalDiskInfo> disks;
        IReadOnlyList<string> diskDiagnostics = Array.Empty<string>();
        if (_enumerateDisks is not null)
            disks = _enumerateDisks();
        else
            disks = DiskInfoProvider.EnumerateTestableDisks(out diskDiagnostics);

        // Filtered by Options.SkippedPhysicalDriveIndexes, populated by the options dialog scanning this
        // same DiskInfoProvider.EnumerateTestableDisks() list; the PhysicalDriveIndex key is stable within
        // one Windows session. Applied before the "no testable disk" check below so skipping every disk
        // gets a clear reason instead of the generic enumeration-failure message.
        IReadOnlyList<int> skippedThisRun = Array.Empty<int>();
        if (Options.SkippedPhysicalDriveIndexes.Count > 0)
        {
            var kept = disks.Where(d => !Options.SkippedPhysicalDriveIndexes.Contains(d.PhysicalDriveIndex)).ToList();
            skippedThisRun = disks.Where(d => Options.SkippedPhysicalDriveIndexes.Contains(d.PhysicalDriveIndex))
                .Select(d => d.PhysicalDriveIndex).ToList();
            disks = kept;
        }

        if (disks.Count == 0)
        {
            var reason = skippedThisRun.Count > 0
                ? "No testable disk left after Advanced Mode's drive-skip list excluded every detected disk."
                : "No testable disk found (no physical disk resolved to a lettered volume).";
            if (diskDiagnostics.Count > 0)
                reason += " Details: " + string.Join(" | ", diskDiagnostics);

            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = false,
                    FailureReason = reason,
                    StartTime = start,
                    EndTime = DateTime.Now
                }
            });
            return;
        }

        var keyMetrics = new Dictionary<string, object>();
        var notes = new List<string>();
        var failures = new List<string>();
        var rawLog = new System.Text.StringBuilder();
        rawLog.AppendLine("WinTester Storage Test (fio, Seq1M Q8T1 + Rand4K Q1T1)");
        rawLog.AppendLine();
        if (skippedThisRun.Count > 0)
        {
            notes.Add($"Advanced Mode: {skippedThisRun.Count} disk(s) skipped per operator's drive-skip " +
                $"list (physical drive index {string.Join(", ", skippedThisRun)}); not tested this run.");
        }

        // Phase 3.4: build each disk's plan (which profiles run, and why if Rand4K Q1T1 is being skipped)
        // once, up front, so the same decision drives the DisksAnnounced overlay message, the report's
        // per-disk KeyMetrics/notes, and the actual pass loop below — one source of truth instead of the
        // loop re-deriving "should Rand4K run here" separately from what was announced.
        var diskPlans = disks.Select(BuildDiskPlan).ToList();

        foreach (var plan in diskPlans.Where(p => p.RandSkipReason is not null))
        {
            notes.Add($"{plan.Disk.DriveLetter} ({plan.Disk.Model}): Rand4K Q1T1 skipped — {plan.RandSkipReason}; " +
                      "ran a quick sequential (Seq1M Q8T1) pass only. Override in Advanced Mode to force the " +
                      "full suite on this drive.");
            keyMetrics[$"{plan.Disk.DriveLetter} Rand4K Q1T1 Write Result"] =
                $"skipped ({plan.RandSkipReason}; quick pass only)";
            keyMetrics[$"{plan.Disk.DriveLetter} Rand4K Q1T1 Read Result"] =
                $"skipped ({plan.RandSkipReason}; quick pass only)";
            rawLog.AppendLine($"=== {plan.Disk.DriveLetter} Rand4K Q1T1 (Write+Read) ===");
            rawLog.AppendLine($"SKIPPED: {plan.RandSkipReason}; ran Seq1M Q8T1 only. Override in Advanced Mode " +
                               "to force the full suite on this drive.");
            rawLog.AppendLine();
        }

        // Announce every disk in one shot, before any pass starts, so the grid overlay can build and lay out every cell at once, see DisksAnnounced.
        DisksAnnounced?.Invoke(this, diskPlans.Select(p => new AnnouncedStorageDisk(p.Disk, p.RandSkipReason)).ToList());

        // A 5-second idle rest before any disk is touched, so the first temperature/health reading reflects
        // the drives at rest rather than already mid-write. Only worth doing with a sensor service to read
        // from; otherwise this is a no-op (with a note added to the report).
        using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? telemetryTask = null;
        if (_sensorService is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            EmitDriveTelemetry(disks, isIdleReading: true);
            telemetryTask = RunTelemetryPollLoopAsync(disks, telemetryCts.Token);
        }
        else
        {
            notes.Add("No sensor service supplied to the storage test module: idle drive temperature/" +
                      "SMART health capture and periodic logging were skipped for this run.");
        }

        int totalSlots = diskPlans.Sum(p => p.Profiles.Length * 2); // profiles × (write + read) per disk, per-disk profile count varies with Phase 3's quick-pass gating.
        int slot = 0;

        try
        {
        foreach (var plan in diskPlans)
        {
            var disk = plan.Disk;
            var testFilePath = Path.Combine(disk.DriveLetter + @"\", "wintester_iotest.bin");

            foreach (var profile in plan.Profiles)
            {
                foreach (var phase in new[] { FioPhase.Write, FioPhase.Read })
                {
                    token.ThrowIfCancellationRequested();

                    var stageBase = slot * 100.0 / totalSlots;
                    var stageSpan = 100.0 / totalSlots;
                    var benchPhase = phase == FioPhase.Write ? StorageBenchmarkPhase.Write : StorageBenchmarkPhase.Read;
                    var profileLabel = profile == FioProfile.Seq1MQ8T1 ? "Seq1M Q8T1" : "Rand4K Q1T1";
                    var stageText = $"Storage: {disk.DriveLetter} {profileLabel} {phase} ({Options.Passes}x)";

                    ProgressChanged?.Invoke(this, new TestProgressEventArgs
                    {
                        ProgressPercent = stageBase,
                        StatusText = $"{stageText} (starting…)",
                        SecondsRemaining = null
                    });

                    // Phase transition, no sample yet; the overlay needs this explicit "at rest" signal rather than inferring it from a stale prior value (see StorageLiveEventArgs). State=Testing from the first stage of the first profile through the last stage of the last; see the Completed-marker event below for when this disk's cell flips to Complete.
                    LiveSpeedChanged?.Invoke(this, new StorageLiveEventArgs
                    {
                        DriveLetter = disk.DriveLetter,
                        Model = disk.Model,
                        TotalBytes = disk.TotalBytes,
                        FreeBytes = disk.FreeBytes,
                        Profile = profile,
                        Phase = benchPhase,
                        State = DriveTestState.Testing,
                        SpeedMBs = null
                    });

                    void OnLiveSpeed(double mbs)
                    {
                        ProgressChanged?.Invoke(this, new TestProgressEventArgs
                        {
                            ProgressPercent = stageBase + stageSpan * 0.5, // fio's --size-based job has no fixed duration to interpolate a percent from, so this stage's slot just parks at its midpoint once running.
                            StatusText = $"{stageText}, {mbs:0.0} MB/s",
                            SecondsRemaining = null
                        });

                        LiveSpeedChanged?.Invoke(this, new StorageLiveEventArgs
                        {
                            DriveLetter = disk.DriveLetter,
                            Model = disk.Model,
                            TotalBytes = disk.TotalBytes,
                            FreeBytes = disk.FreeBytes,
                            Profile = profile,
                            Phase = benchPhase,
                            State = DriveTestState.Testing,
                            SpeedMBs = mbs
                        });
                    }

                    FioRunner.Result result;
                    try
                    {
                        result = await _runner.RunPassAsync(testFilePath, profile, phase, _testFileSizeBytes, OnLiveSpeed, token, Options.Passes);
                    }
                    catch (OperationCanceledException)
                    {
                        TryDeleteTestFile(testFilePath);
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

                    var prefix = $"{disk.DriveLetter} {profileLabel} {phase}";
                    rawLog.AppendLine($"=== {prefix} ===");

                    if (!result.LaunchOk)
                    {
                        keyMetrics[$"{prefix} Result"] = "not run";
                        failures.Add($"{prefix}: {result.LaunchError}");
                        rawLog.AppendLine($"LAUNCH ERROR: {result.LaunchError}");
                    }
                    else
                    {
                        if (result.FioErrorCount > 0)
                            failures.Add($"{prefix}: fio reported {result.FioErrorCount} I/O error(s)");

                        keyMetrics[$"{prefix} Speed (MB/s, avg of {result.LoopCount})"] = result.AvgSpeedMBs?.ToString("0.0") ?? "n/a";
                        keyMetrics[$"{prefix} IOPS (avg of {result.LoopCount})"] = result.AvgIops?.ToString("0") ?? "n/a";

                        if (result.AvgSpeedMBs is null)
                            notes.Add($"{prefix}: fio produced no parseable result, see raw log.");

                        rawLog.AppendLine($"Loops: {result.LoopCount}");
                        rawLog.AppendLine($"Speed (avg across all loops): {result.AvgSpeedMBs?.ToString("0.0") ?? "n/a"} MB/s");
                        rawLog.AppendLine($"IOPS (avg across all loops): {result.AvgIops?.ToString("0") ?? "n/a"}");
                        rawLog.AppendLine($"fio error count: {result.FioErrorCount}");
                        rawLog.AppendLine("--- raw stdout/stderr ---");
                        rawLog.AppendLine(result.RawOutput);
                    }

                    rawLog.AppendLine();
                    slot++;
                }
            }

            TryDeleteTestFile(testFilePath);

            // This disk's cell flips from pulsing-red to solid-green here; every profile/phase for this disk has now run (or failed/been skipped), nothing more coming for it. Phase/Profile on this event are arbitrary (Complete state is what the border cares about, not which bar); Rand4K Q1T1 Read is used only for consistency with "last thing that happened to this disk".
            LiveSpeedChanged?.Invoke(this, new StorageLiveEventArgs
            {
                DriveLetter = disk.DriveLetter,
                Model = disk.Model,
                TotalBytes = disk.TotalBytes,
                FreeBytes = disk.FreeBytes,
                Profile = FioProfile.Rand4KQ1T1,
                Phase = StorageBenchmarkPhase.Read,
                State = DriveTestState.Complete,
                SpeedMBs = null
            });
        }
        }
        finally
        {
            // Stops the telemetry poll loop once every disk's passes have finished (or the run was cancelled/threw). It has no natural end condition of its own, so something must tell it to stop rather than leaving it polling after StartAsync has returned.
            telemetryCts.Cancel();
            if (telemetryTask is not null)
            {
                try { await telemetryTask; }
                catch (OperationCanceledException) { /* expected; this is exactly how the loop stops. */ }
            }
        }

        var end = DateTime.Now;
        bool passed = failures.Count == 0;
        if (notes.Count > 0) keyMetrics["Notes"] = string.Join(" | ", notes);

        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_storage_" + context.SessionId);
        Directory.CreateDirectory(tempDir);
        var rawLogPath = Path.Combine(tempDir, "storage_combined.log");
        File.WriteAllText(rawLogPath, rawLog.ToString());

        Completed?.Invoke(this, new TestResultEventArgs
        {
            Result = new TestResult
            {
                Passed = passed,
                FailureReason = passed ? null : string.Join("; ", failures),
                StartTime = start,
                EndTime = end,
                KeyMetrics = keyMetrics,
                RawLogPath = rawLogPath
            }
        });
    }

    // Runs until ct is cancelled, taking one EmitDriveTelemetry reading every TelemetryPollInterval. Has no end condition of its own by design; it runs for the whole storage test regardless of which disk is active, so StartAsync's finally block stops it once every disk's passes are done. The OperationCanceledException Task.Delay throws on cancellation is the loop's only exit path, expected and swallowed by that finally block.
    private async Task RunTelemetryPollLoopAsync(IReadOnlyList<PhysicalDiskInfo> disks, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TelemetryPollInterval, ct);
            EmitDriveTelemetry(disks, isIdleReading: false);
        }
    }

    // Reads one ISensorService.GetSnapshot and, for every disk, raises both DriveTelemetryChanged (for the live UI grid) and SensorSampleCaptured (for the session's samples.jsonl via MainViewModel's AppendModuleSample wiring).
    // Runs its own poll rather than relying solely on MainViewModel's shared 5s hardware-snapshot poll (which already covers StorageTelemetry temperature/health for every disk) because that shared poll's snapshot has no drive-letter linkage [StorageTelemetry.Name is LibreHardwareMonitor's own hardware name, not a lettered volume] and its samples.jsonl entries aren't attributed to the storage test specifically. This poll resolves each disk to a drive letter via NamesLooselyMatch (same loose substring-contains approach GpuStressTestModule uses for the analogous GPU-name-matching problem) and tags every sample with testId "storage", giving both the UI grid and the session log a clean, drive-letter-keyed, storage-test-scoped view. Temperature (not health, which is new here) ends up logged twice during a run as a result; a known, harmless duplication, not worth the coupling it'd take to suppress the shared poll's entries during this run.
    // Best-effort throughout: a GetSnapshot failure or a disk with no matching StorageTelemetry entry (null TempC/HealthPercent, no exception) never stops the test, same degrade-gracefully posture as every other sensor consumer in this codebase.
    private void EmitDriveTelemetry(IReadOnlyList<PhysicalDiskInfo> disks, bool isIdleReading)
    {
        SensorSnapshot? snapshot;
        try
        {
            snapshot = _sensorService?.GetSnapshot();
        }
        catch
        {
            snapshot = null; // Best-effort; a failed read this tick isn't worth failing the storage test.
        }

        if (snapshot is null) return;

        var sampleValues = new Dictionary<string, double>();

        foreach (var disk in disks)
        {
            var match = snapshot.Storages.FirstOrDefault(s => NamesLooselyMatch(disk.Model, s.Name));

            DriveTelemetryChanged?.Invoke(this, new StorageDriveTelemetryEventArgs
            {
                DriveLetter = disk.DriveLetter,
                Model = disk.Model,
                TempC = match?.TempC,
                HealthPercent = match?.HealthPercent,
                IsIdleReading = isIdleReading
            });

            var keyPrefix = isIdleReading ? $"{disk.DriveLetter} Idle" : disk.DriveLetter;
            if (match?.TempC is { } temp) sampleValues[$"{keyPrefix} TempC"] = temp;
            if (match?.HealthPercent is { } health) sampleValues[$"{disk.DriveLetter} HealthPercent"] = health;
        }

        if (sampleValues.Count > 0)
        {
            SensorSampleCaptured?.Invoke(this, new SensorSampleEventArgs
            {
                Sample = new SensorSample { Timestamp = DateTime.Now, Values = sampleValues }
            });
        }
    }

    // Best-effort, case-insensitive name match between a PhysicalDiskInfo.Model (WMI's Win32_DiskDrive.Model) and an ISensorService-reported storage hardware name (LibreHardwareMonitor's): the two backends share no naming convention. Duplicated from GpuStressTestModule's identically-implemented method for the analogous GPU case rather than extracted to a shared helper, since it's a two-line method with no natural shared home between the Gpu and Storage test projects.
    private static bool NamesLooselyMatch(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

    // Phase 3.2/3.3: one disk's resolved test plan — which profiles actually run, and (when Rand4K Q1T1 is
    // being left out) the human-readable reason, reused verbatim for the report's notes/KeyMetrics, the raw
    // log, and the DisksAnnounced overlay message so all three always agree with each other and with what
    // the pass loop actually runs.
    private readonly record struct DiskPlan(PhysicalDiskInfo Disk, FioProfile[] Profiles, string? RandSkipReason);

    // Rand4K Q1T1 is skipped by default for a disk DriveTypeDetector reports as a mechanical HDD or as an
    // undetermined drive type (DriveMediaKind.Hdd/Unknown — Unknown covers SMR drives WMI still reports as
    // ordinary HDDs, and USB-SATA bridges that hide media-type info entirely, same as that enum's own
    // remark), unless the operator has forced the full suite for this specific disk via Advanced Mode's
    // StorageTestOptions.ForceFullSuitePhysicalDriveIndexes. An SSD or NVMe disk always gets the full suite;
    // there's no override needed (or offered — see TestOptionsWindow's per-drive checkbox) in that direction.
    private DiskPlan BuildDiskPlan(PhysicalDiskInfo disk)
    {
        bool forcedFullSuite = Options.ForceFullSuitePhysicalDriveIndexes.Contains(disk.PhysicalDriveIndex);
        bool quickPassOnly = !forcedFullSuite &&
                              disk.MediaKind != DriveMediaKind.Ssd &&
                              disk.MediaKind != DriveMediaKind.Nvme;

        if (!quickPassOnly)
            return new DiskPlan(disk, Profiles, null);

        string reason = disk.MediaKind == DriveMediaKind.Hdd
            ? "mechanical HDD detected"
            : "drive type undetermined, treated as HDD";

        return new DiskPlan(disk, new[] { FioProfile.Seq1MQ8T1 }, reason);
    }

    private static void TryDeleteTestFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; a locked/undeletable leftover test file isn't a reason to fail the storage test, though it leaves used space on that volume until the next successful run or a manual delete clears it.
        }
    }

    public Task StopAsync()
    {
        _internalCts?.Cancel();
        return Task.CompletedTask;
    }
}
