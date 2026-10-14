using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Core;

// Contract for an auto-run stress/diagnostic test. Every Tab 1 test implements this so the orchestrator,
// UI tiles, and report generator never need test-specific logic.
public interface ITestModule
{
    // Stable key, e.g. "cpu", "gpu", "storage", "memory", "battery".
    string Id { get; }

    string DisplayName { get; }

    // Resolved via the branding theme.
    string TileColorKey { get; }

    bool SupportsAdvancedSettings { get; }

    Task StartAsync(TestRunContext context, CancellationToken ct);

    // Manual abort. A stopped test is treated as cancelled, not paused.
    Task StopAsync();

    event EventHandler<TestProgressEventArgs>? ProgressChanged;

    // Optional, module-specific telemetry beyond the shared sensor poll.
    event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;

    event EventHandler<TestResultEventArgs>? Completed;
}
