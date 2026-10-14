using System;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.App.Mocks;

// Placeholder ITestModule that simulates a short run with periodic progress ticks, then reports a fixed
// result; validates the full plumbing (start → progress → pass/fail popup → minimal report) before wiring
// a real stress engine. Swap out module-by-module for real engines later; nothing above this class
// (orchestrator, tiles, view-models) needs to change, since they only talk to the ITestModule contract.
public sealed class MockTestModule : ITestModule
{
    private readonly TimeSpan _simulatedDuration;
    private readonly bool _forcedResult;
    private CancellationTokenSource? _internalCts;

    public string Id { get; }
    public string DisplayName { get; }
    public string TileColorKey { get; }
    public bool SupportsAdvancedSettings { get; }

    public event EventHandler<TestProgressEventArgs>? ProgressChanged;

    // No engine-specific extra telemetry to raise beyond the shared progress ticks below; suppressed rather
    // than removed since the event is part of the ITestModule contract.
#pragma warning disable CS0067
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;
#pragma warning restore CS0067

    public event EventHandler<TestResultEventArgs>? Completed;

    // forcedResult gives a deterministic Pass/Fail for this mock run: flip to false on any instance to see
    // the tile's Fail styling without real hardware/engines wired up.
    public MockTestModule(string id, string displayName, string tileColorKey,
        bool forcedResult = true, TimeSpan? simulatedDuration = null)
    {
        Id = id;
        DisplayName = displayName;
        TileColorKey = tileColorKey;
        _forcedResult = forcedResult;
        _simulatedDuration = simulatedDuration ?? TimeSpan.FromSeconds(3);
        SupportsAdvancedSettings = false; // real per-test Advanced Mode dialogs land later
    }

    public async Task StartAsync(TestRunContext context, CancellationToken ct)
    {
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _internalCts.Token;
        var start = DateTime.Now;

        const int steps = 10;
        var stepDelay = _simulatedDuration / steps;

        try
        {
            for (int i = 1; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(stepDelay, token);
                var elapsed = i * stepDelay;
                var remaining = (_simulatedDuration - elapsed).TotalSeconds;
                ProgressChanged?.Invoke(this, new TestProgressEventArgs
                {
                    ProgressPercent = i * 100.0 / steps,
                    StatusText = $"{DisplayName}: simulated stage {i} of {steps}",
                    SecondsRemaining = Math.Max(0, remaining)
                });
            }
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

        Completed?.Invoke(this, new TestResultEventArgs
        {
            Result = new TestResult
            {
                Passed = _forcedResult,
                FailureReason = _forcedResult ? null : "Simulated failure (mock module)",
                StartTime = start,
                EndTime = DateTime.Now,
                KeyMetrics = new() { ["mock"] = true }
            }
        });
    }

    public Task StopAsync()
    {
        _internalCts?.Cancel();
        return Task.CompletedTask;
    }
}
