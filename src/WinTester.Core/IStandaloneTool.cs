using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Core;

// Contract for a Tab 2 utility that isn't part of the Tab 1 auto-run queue and never contributes to the
// aggregate Pass/Fail popup. Same shape as ITestModule minus the pass/fail/report parts.
public interface IStandaloneTool
{
    string Id { get; }

    string DisplayName { get; }

    Task StartAsync(CancellationToken ct);

    Task StopAsync();

    event EventHandler<TestProgressEventArgs>? ProgressChanged;
}
