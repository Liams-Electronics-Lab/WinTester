using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.Tests.Cpu;

// CPU stress test: replaces the earlier MockTestModule registered under id "cpu". Runs Intel PDT's IPDT compute modules (BSD-3-Clause, process-isolated) as the sole load-generation engine on both Intel and AMD, rather than branching between Prime95 and Intel PDT by vendor: source inspection found no vendor gate in the compute-stress modules themselves, so a single engine works for both. Vendor detection (see CpuVendorDetector) does not gate which engine runs; only the standalone genuineintel module checks CPUID vendor at all, and none of the compute-stress modules (dgemm, fma3, avx, math_primenumber, math_fp) have a vendor gate in their compute loop. Detection is instead used for two narrower things: it's surfaced in KeyMetrics/the report's machine-info section, and it picks between DefaultModulesIntel and DefaultModulesNonIntel and tells IpdtModuleRunner whether to apply the MKL non-Intel-dispatch workaround (dgemm specifically links Intel MKL; nothing else here is IPDT-specific).
public sealed class CpuStressTestModule : ITestModule
{
    // Default concurrent module set on Intel: dgemm for sustained FPU/cache/memory-bus load, paired with fma3 for wide-SIMD power draw; an IPDT-native approximation of Prime95's "stress multiple subsystems at once via large FFTs" reputation.
    public static readonly IReadOnlyList<string> DefaultModulesIntel = new[] { "dgemm", "fma3" };

    // Default concurrent module set on AMD/other non-Intel vendors: math_primenumber in place of dgemm, keeping fma3. dgemm is the only IPDT compute module linked against Intel MKL (cblas_dgemm); MKL's own CPUID dispatch has a well-documented history of downgrading non-"GenuineIntel" CPUs to a slower code path independent of anything in IPDT's own source. IpdtModuleRunner applies an environment-variable workaround for that (see its MklLinkedModules), so dgemm stays available as an explicit Advanced Mode choice later, but it's not the safest default sight-unseen on hardware this hasn't been empirically verified against. math_primenumber has zero MKL dependency and no vendor gate anywhere in its source.
    public static readonly IReadOnlyList<string> DefaultModulesNonIntel = new[] { "math_primenumber", "fma3" };

    // Back-compat alias for DefaultModulesIntel; kept so any existing caller that referenced the old single default set still compiles. New code should pick via SelectDefaultModules instead, since the right default now depends on detected vendor.
    public static readonly IReadOnlyList<string> DefaultModules = DefaultModulesIntel;

    // Picks the default module set for a detected vendor. CpuVendor.Unknown is treated as non-Intel: the safest assumption when detection itself failed.
    public static IReadOnlyList<string> SelectDefaultModules(CpuVendor vendor) =>
        vendor == CpuVendor.GenuineIntel ? DefaultModulesIntel : DefaultModulesNonIntel;

    // Placeholder default run length, not yet signed off on by a QA/warranty owner. Override via the constructor; exposed in the UI via Advanced Mode.
    public const int DefaultDurationSeconds = 300;

    // Null means "not explicitly overridden"; the actual module set is picked in StartAsync via SelectDefaultModules once the vendor is known, rather than baked in at construction time, so the Intel/non-Intel default split above actually applies. This constructor-level override still exists for callers/tests that want a fixed set baked in at construction, but StartAsync now prefers Options.ModuleSet (Advanced Mode) when it's anything other than CpuModuleSet.Auto.
    private readonly IReadOnlyList<string>? _explicitModules;
    private readonly string _vendorDirectory;
    private readonly IpdtModuleRunner _runner;
    private CancellationTokenSource? _internalCts;

    public string Id => "cpu";
    public string DisplayName => "CPU";
    public string TileColorKey => "TileCpu";

    // Advanced Mode landed: test duration override, plus a module-set override. See Options.
    public bool SupportsAdvancedSettings => true;

    // Advanced Mode settings for this module. Mutable and read fresh at the top of every StartAsync call, not frozen at construction; the tile's options dialog edits this object directly (TestOptionsWindow), and this module instance is a long-lived singleton held by MainViewModel's composition root for the app's lifetime, so "reconstruct the module with new constructor args" was never going to be the right shape here.
    public CpuTestOptions Options { get; } = new();

    public event EventHandler<TestProgressEventArgs>? ProgressChanged;

    // This module has no engine-specific extra telemetry beyond the shared sensor poll to raise through this event; the IPDT modules' only per-run signal is their exit code, read once at completion, not a stream of interim samples. Suppressed rather than removed: the event is part of the ITestModule contract, and a future engine swap (e.g. Prime95, which does emit periodic stats) may want to raise it.
#pragma warning disable CS0067
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;
#pragma warning restore CS0067

    public event EventHandler<TestResultEventArgs>? Completed;

    public CpuStressTestModule(
        IReadOnlyList<string>? modules = null,
        int? durationSeconds = null,
        string? vendorDirectory = null)
    {
        _explicitModules = modules;
        if (durationSeconds is { } d) Options.DurationSeconds = d;

        // The suggested /vendor layout lists prime95/furmark2/fio/smartmontools/memtest but omits an Intel PDT entry. "intel-pdt" follows the same /vendor/<tool>/ convention as the others.
        _vendorDirectory = vendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "intel-pdt");
        _runner = new IpdtModuleRunner(_vendorDirectory);
    }

    // Centralizes module-set resolution so StartAsync and ReportElapsedProgressAsync can't drift out of
    // sync on which modules are "actually running". Honors Options.ModuleSet (Advanced Mode) when it's
    // anything other than CpuModuleSet.Auto; otherwise falls back to the pre-Advanced-Mode behavior
    // (constructor-level _explicitModules override, else the detected-vendor default).
    private IReadOnlyList<string> ResolveModules(CpuVendor vendor) => Options.ModuleSet switch
    {
        CpuModuleSet.DgemmFma3 => DefaultModulesIntel,
        CpuModuleSet.MathPrimeNumberFma3 => DefaultModulesNonIntel,
        _ => _explicitModules ?? SelectDefaultModules(vendor)
    };

    public async Task StartAsync(TestRunContext context, CancellationToken ct)
    {

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _internalCts.Token;
        var start = DateTime.Now;
        var durationSeconds = Options.DurationSeconds;

        var vendor = CpuVendorDetector.Detect();
        var modules = ResolveModules(vendor);

        // Per-session temp dir keeps -resultName files (and our own combined log) from clobbering a concurrent or repeated run, including burn-in mode's repeat-in-place case.
        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_cpu_" + context.SessionId);
        Directory.CreateDirectory(tempDir);

        // Stage text for the running-state modal's "current test" bar; names the actual IPDT modules in flight rather than a generic "running".
        var stageText = DescribeStage(modules);

        ProgressChanged?.Invoke(this, new TestProgressEventArgs
        {
            ProgressPercent = 0,
            StatusText = $"{stageText} ({vendor})",
            SecondsRemaining = durationSeconds
        });

        // Tracks early exits (launch failure, or an exit long before the requested duration elapsed) for the live display, so a module dying seconds into a run isn't indistinguishable from one that's simply light on visible CPU load. "Early" is deliberately loose (under half the requested duration, or under 5s for very short durations); this is a display heuristic only; pass/fail always comes from the real exit code below.
        var earlyExitWarnings = new List<string>();
        var earlyExitLock = new object();

        var runTasks = modules
            .Select(async m =>
            {
                var result = await _runner.RunAsync(m, durationSeconds, Path.Combine(tempDir, $"{m}_result.txt"), token, vendor);

                bool exitedEarly = !token.IsCancellationRequested &&
                    (result.LaunchError is not null ||
                     result.Duration.TotalSeconds < Math.Min(5, durationSeconds / 2.0));

                if (exitedEarly)
                {
                    var reason = result.LaunchError ?? $"exited after {result.Duration.TotalSeconds:0.0}s with code {result.ExitCode}";
                    lock (earlyExitLock)

                        earlyExitWarnings.Add($"{m}: {reason}");
                }

                return result;
            })
            .ToList();

        // No per-module progress signal exists (exit-code-only contract); approximate against elapsed wall time instead, purely for the running-state modal's benefit. This is display-only: pass/fail below always comes from the real exit codes, never from this estimate.
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var progressTask = ReportElapsedProgressAsync(start, progressCts.Token, () =>
        {
            lock (earlyExitLock)
                return earlyExitWarnings.Count > 0 ? string.Join("; ", earlyExitWarnings) : null;
        });

        List<IpdtModuleRunner.Result> results;
        try
        {
            results = (await Task.WhenAll(runTasks)).ToList();
        }
        catch (OperationCanceledException)
        {
            progressCts.Cancel();
            await progressTask;

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

        progressCts.Cancel();
        await progressTask;

        var end = DateTime.Now;
        var failures = new List<string>();
        var keyMetrics = new Dictionary<string, object> { ["CPU Vendor"] = vendor.ToString() };

        foreach (var r in results)
        {
            if (r.LaunchError is { } launchErr)
            {
                failures.Add($"{r.ModuleName}: {launchErr}");
                keyMetrics[$"{r.ModuleName} Exit Code"] = "not run";
                continue;
            }

            var code = r.ExitCode!.Value;
            keyMetrics[$"{r.ModuleName} Exit Code"] = $"{code} ({code.Describe()})";
            keyMetrics[$"{r.ModuleName} Duration (s)"] = Math.Round(r.Duration.TotalSeconds, 1);
            if (r.BootstrapNote is { Length: > 0 } note)
                keyMetrics[$"{r.ModuleName} Dependency Bootstrap"] = note;

            if (code != 0)
                failures.Add($"{r.ModuleName} returned exit code {code} ({code.Describe()})");
        }

        bool passed = failures.Count == 0;
        var rawLogPath = Path.Combine(tempDir, "cpu_combined.log");
        File.WriteAllText(rawLogPath, BuildRawLog(vendor, results));

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

    private async Task ReportElapsedProgressAsync(DateTime start, CancellationToken token, Func<string?>? getEarlyExitWarning = null)
    {
        var modules = ResolveModules(CpuVendorDetector.Detect());
        var stageText = DescribeStage(modules);

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                var elapsed = (DateTime.Now - start).TotalSeconds;
                var pct = Math.Min(99.0, elapsed * 100.0 / Options.DurationSeconds);
                var remaining = Math.Max(0, Options.DurationSeconds - elapsed);

                // Surfaces a module dying early (missing DLL, bad CLI arg, etc.) immediately, instead of leaving the operator staring at a status line that only names modules that are no longer actually running.
                var earlyWarning = getEarlyExitWarning?.Invoke();
                var text = earlyWarning is null ? stageText : $"{stageText}  \u26A0 {earlyWarning}";

                ProgressChanged?.Invoke(this, new TestProgressEventArgs
                {
                    ProgressPercent = pct,
                    StatusText = text,
                    SecondsRemaining = remaining
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected the moment the run finishes or is cancelled; this loop's only job is periodic display updates, so there's nothing further to clean up.
        }
    }

    // Turns an IPDT module id list into the plain-language "stage" text the running-state modal's current-test bar shows, e.g. "Prime number search" rather than the bare id "math_primenumber", so an operator without source-level familiarity with IPDT still understands what's actually being exercised.
    private static string DescribeStage(IReadOnlyList<string> modules)
    {
        var friendly = modules.Select(m => m switch
        {
            "math_primenumber" => "Prime number search",
            "dgemm" => "Matrix multiply (DGEMM)",
            "fma3" => "FMA3 SIMD stress",
            "avx" => "AVX/AVX2/AVX512 stress",
            "math_fp" => "Floating point math",
            "mmxsse" => "MMX/SSE check",
            "cache" => "Cache detection",
            _ => m
        });
        return "CPU stress: " + string.Join(" + ", friendly);
    }

    private static string BuildRawLog(CpuVendor vendor, List<IpdtModuleRunner.Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"WinTester CPU Test, vendor detected: {vendor}");
        sb.AppendLine($"Running elevated: {(ElevationCheck.IsRunningElevated() ? "Yes" : "No")}");
        sb.AppendLine();

        foreach (var r in results)
        {
            sb.AppendLine($"=== {r.ModuleName} ===");
            if (r.LaunchError is { } err)
            {
                sb.AppendLine($"LAUNCH ERROR: {err}");
            }
            else
            {
                sb.AppendLine($"Exit code: {r.ExitCode} ({r.ExitCode!.Value.Describe()})");
                sb.AppendLine($"Duration: {r.Duration.TotalSeconds:0.0}s");
                if (r.BootstrapNote is { Length: > 0 } note)
                    sb.AppendLine($"Dependency bootstrap: {note}");
                sb.AppendLine("--- output ---");
                sb.AppendLine(r.Output);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public Task StopAsync()
    {
        _internalCts?.Cancel();
        return Task.CompletedTask;
    }
}
