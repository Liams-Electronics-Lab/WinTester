using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.Tests.Memory;

// Memory test module, registered under id "memory". Runs MemoryTestEngine in-process [a direct C# port of aplominski/memtest, not a hidden child process like every other engine here] trading crash isolation for simplicity, plus the GPLv3 obligation that comes with shipping the port. Default mode tests as much RAM as safely possible: MemorySizer computes that figure fresh from current free memory at the start of every run, so a busy machine gets a smaller but still meaningful test instead of failing outright; if nothing safe is available, the module fails immediately rather than launching a misleading 0 MB run. The engine always cycles all 8 patterns every iteration [there's no single pattern to select] so the only exposed Advanced Mode variable is iteration count (DefaultIterations), isolated into its own constructor parameter so wiring it into a settings dialog later is a one-place change; window size is wired the same way but stays internal-only (see MemoryTestOptions).
public sealed class MemoryStressTestModule : ITestModule
{
    // One full pass through all 8 patterns by default; Advanced Mode overrides via Options.Iterations, not this constant.
    public const int DefaultIterations = 1;

    private readonly MemoryTestEngine _engine;

    // Test seam for running without real WMI/hardware, same pattern as StorageStressTestModule's enumerateDisks.
    private readonly Func<MemoryTestSizing>? _computeSizing;

    private CancellationTokenSource? _internalCts;

    public string Id => "memory";
    public string DisplayName => "Memory";
    public string TileColorKey => "TileMemory";

    public bool SupportsAdvancedSettings => true;

    // Advanced Mode settings, read fresh in StartAsync; same mutable long-lived-singleton pattern as the other modules' Options properties.
    public MemoryTestOptions Options { get; } = new();

    public event EventHandler<TestProgressEventArgs>? ProgressChanged;

    // CS0067 fix: this module's only per-run signal beyond the shared 5s sensor poll is the progress stream MemoryTestEngine already surfaces via ProgressChanged/TestResult: nothing distinct to raise here yet.
#pragma warning disable CS0067
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;
#pragma warning restore CS0067

    public event EventHandler<TestResultEventArgs>? Completed;

    // iterations: seeds Options.Iterations, which StartAsync actually reads from. windowMb/chunkMb: internal engine tuning, not exposed in Advanced Mode; windowMb is the composed peak MemoryTestEngine holds resident at once, chunkMb the size of each underlying buffer.
    public MemoryStressTestModule(
        int? iterations = null,
        Func<MemoryTestSizing>? computeSizing = null,
        int? windowMb = null,
        int? chunkMb = null)
    {
        if (iterations is { } it) Options.Iterations = it;
        _computeSizing = computeSizing;
        _engine = new MemoryTestEngine(windowMb, chunkMb);
    }

    public async Task StartAsync(TestRunContext context, CancellationToken ct)
    {
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _internalCts.Token;
        var start = DateTime.Now;

        var sizing = (_computeSizing ?? MemorySizer.ComputeSafeTestSize)();

        // Advanced Mode's MaxTestMb only ever lowers the recommendation, never raises it; a cap above what MemorySizer already deemed safe is a no-op, not license to test more.
        if (Options.MaxTestMb is { } cap && cap > 0 && cap < sizing.RecommendedTestMb)
        {
            sizing = sizing with
            {
                RecommendedTestMb = cap,
                Explanation = sizing.Explanation + $" Capped to {cap} MB by Advanced Mode's test-size limit."
            };
        }

        if (sizing.RecommendedTestMb <= 0)
        {
            var end0 = DateTime.Now;
            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = false,
                    FailureReason = $"No memory test run: {sizing.Explanation}",
                    StartTime = start,
                    EndTime = end0,
                    KeyMetrics = new Dictionary<string, object>
                    {
                        ["Total Physical (MB)"] = sizing.TotalPhysicalMb,
                        ["Free Physical (MB)"] = sizing.FreePhysicalMb,
                        ["Amount Tested (MB)"] = 0
                    }
                }
            });
            return;
        }

        ProgressChanged?.Invoke(this, new TestProgressEventArgs
        {
            ProgressPercent = 0,
            StatusText = $"Testing memory: {sizing.RecommendedTestMb} MB, starting",
            SecondsRemaining = null
        });

        void OnEngineProgress(double percent, string status)
        {
            ProgressChanged?.Invoke(this, new TestProgressEventArgs
            {
                ProgressPercent = percent,
                StatusText = $"Testing memory: {sizing.RecommendedTestMb} MB, {status} ({percent:0.#}%)",
                SecondsRemaining = null
            });
        }

        MemoryTestEngineResult result;
        try
        {
            result = await _engine.RunAsync(
                sizing.RecommendedTestMb, Options.Iterations, OnEngineProgress, token,
                // Checked against the actual (clipped) size of the window about to run, not the fixed
                // _engine.WindowMb constant — see field-issues tracker 1.3. Without this, any machine whose
                // recommended test size is below one full window (common under ~6 GB free) fails the
                // headroom check on window 1 every time and reports a vacuous, zero-duration pass.
                hasHeadroomForNextWindow: nextWindowMb => MemorySizer.HasHeadroomForWindow((int)nextWindowMb));
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
        catch (OutOfMemoryException ex)
        {
            // MemorySizer is meant to prevent this, but free memory can shift between sizing and allocation if another process grabs RAM mid-run; surfaced as a normal failed result rather than crashing the session.
            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = false,
                    FailureReason = $"Ran out of memory allocating the {sizing.RecommendedTestMb} MB test " +
                                     $"buffer ({ex.Message}): another process likely claimed memory between " +
                                     "sizing and allocation.",
                    StartTime = start,
                    EndTime = DateTime.Now
                }
            });
            return;
        }

        var end = DateTime.Now;
        var failures = new List<string>();
        // Coverage is reported against what was actually walked (result.ActualTestedMb), not the
        // original target (sizing.RecommendedTestMb): a run that stopped early for headroom — including
        // stopping on window 1, see MemoryTestEngine.RunAsync's field-2026-09-27 note — otherwise reads
        // as if the full target had been tested even at 0 MB actually covered.
        double coverageOfTotalPct = sizing.TotalPhysicalMb > 0
            ? 100.0 * result.ActualTestedMb / sizing.TotalPhysicalMb : 0;
        double coverageOfFreePct = sizing.FreePhysicalMb > 0
            ? 100.0 * result.ActualTestedMb / sizing.FreePhysicalMb : 0;
        var keyMetrics = new Dictionary<string, object>
        {
            ["Amount Tested (MB)"] = result.ActualTestedMb,
            ["Target Sizing (MB)"] = sizing.RecommendedTestMb,
            ["Total Physical (MB)"] = sizing.TotalPhysicalMb,
            ["Free Physical (MB)"] = sizing.FreePhysicalMb,
            ["Coverage of Total RAM (%)"] = Math.Round(coverageOfTotalPct, 1),
            ["Coverage of Free RAM (%)"] = Math.Round(coverageOfFreePct, 1),
            ["Iterations"] = result.Iterations,
            ["Sizing"] = sizing.Explanation,
            ["Duration (s)"] = Math.Round(result.Duration.TotalSeconds, 1),
            ["Errors Found"] = result.Errors.Count,
            ["Distinct Window Addresses"] = $"{result.DistinctWindowAddresses}/{result.TotalWindowsAllocated}",
            ["Stopped Early (Headroom)"] = result.StoppedEarlyForHeadroom
        };

        // Any reported bit error fails the run; a handful of examples go in KeyMetrics/failure text for at-a-glance triage, the full list stays in the raw log.
        if (result.Errors.Count > 0)
        {
            var sample = result.Errors.Take(3)
                .Select(e => $"offset 0x{(e.WindowStartOffset + e.OffsetWithinWindow):X} " +
                             $"({e.BitFlips} bit(s), pattern {e.PatternName})");
            keyMetrics["Sample Errors"] = string.Join(" | ", sample);
            failures.Add($"{result.Errors.Count} bit error(s) found, first: {sample.First()}");
        }

        bool passed = failures.Count == 0;

        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_memory_" + context.SessionId);
        Directory.CreateDirectory(tempDir);
        var rawLogPath = Path.Combine(tempDir, "memory_combined.log");
        File.WriteAllText(rawLogPath, BuildRawLog(sizing, result));

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

    private static string BuildRawLog(MemoryTestSizing sizing, MemoryTestEngineResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinTester Memory Test");
        sb.AppendLine($"Running elevated: {(ElevationCheck.IsRunningElevated() ? "Yes" : "No")}");
        sb.AppendLine($"Sizing (target): {sizing.Explanation}");
        sb.AppendLine($"Amount tested (actual): {result.ActualTestedMb} MB");
        double coverageOfTotalPct = sizing.TotalPhysicalMb > 0
            ? 100.0 * result.ActualTestedMb / sizing.TotalPhysicalMb : 0;
        double coverageOfFreePct = sizing.FreePhysicalMb > 0
            ? 100.0 * result.ActualTestedMb / sizing.FreePhysicalMb : 0;
        sb.AppendLine($"Coverage: {coverageOfTotalPct:0.0}% of total physical RAM, " +
                      $"{coverageOfFreePct:0.0}% of RAM that was free at test start " +
                      "(some RAM is always excluded: it's actively in use by the OS/other processes and " +
                      "can't be safely claimed without risking system instability, see MemorySizer reserve).");
        sb.AppendLine($"Iterations: {result.Iterations}");
        sb.AppendLine($"Duration: {result.Duration.TotalSeconds:0.0}s");
        sb.AppendLine($"Distinct window (virtual) addresses: {result.DistinctWindowAddresses} of " +
                      $"{result.TotalWindowsAllocated} windows allocated; a virtual-address sanity check " +
                      "only, not proof of distinct physical RAM.");
        if (result.StoppedEarlyForHeadroom)
        {
            sb.AppendLine("NOTE: run stopped before reaching the originally sized amount; a live headroom " +
                          "re-check found the machine no longer had room for another window (something else " +
                          "grew its memory footprint mid-run). Windows already completed above are still " +
                          "valid; this is not a test failure.");
        }
        sb.AppendLine($"Errors found: {result.Errors.Count}");
        foreach (var e in result.Errors)
        {
            sb.AppendLine($"  - offset=0x{(e.WindowStartOffset + e.OffsetWithinWindow):X} " +
                          $"bitFlips={e.BitFlips} pattern={e.PatternName}");
        }
        return sb.ToString();
    }

    public Task StopAsync()
    {
        _internalCts?.Cancel();
        return Task.CompletedTask;
    }
}
