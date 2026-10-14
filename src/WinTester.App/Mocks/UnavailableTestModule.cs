using System;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.App.Mocks;

// S147: stands in for a Tab 1 module whose assembly is not next to the exe, so its tile still shows (greyed out, not
// selectable) instead of the whole app failing to start. Never started: TestModuleViewModel.IsAvailable is false.
public sealed class UnavailableTestModule : ITestModule
{
    public string Id { get; }
    public string DisplayName { get; }
    public string TileColorKey { get; }
    public bool SupportsAdvancedSettings => false;

    public UnavailableTestModule(string id, string displayName, string tileColorKey)
    {
        Id = id;
        DisplayName = displayName;
        TileColorKey = tileColorKey;
    }

#pragma warning disable CS0067
    public event EventHandler<TestProgressEventArgs>? ProgressChanged;
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;
    public event EventHandler<TestResultEventArgs>? Completed;
#pragma warning restore CS0067

    public Task StartAsync(TestRunContext context, CancellationToken ct) =>
        throw new InvalidOperationException($"{DisplayName} is not available: its module is missing.");

    public Task StopAsync() => Task.CompletedTask;
}
