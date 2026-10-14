using System.Threading;
using WinTester.Branding;

namespace WinTester.Core;

// Carries session id, brand profile, abort-on-fail flag, and cancellation token into a running test.
public sealed class TestRunContext
{
    public required string SessionId { get; init; }
    public required BrandProfile BrandProfile { get; init; }
    public bool AbortOnFail { get; init; }
    public CancellationToken Ct { get; init; }

    // Absolute path to this run's session folder. Lets a module (e.g. Power) read back another
    // already-completed test's telemetry from the same run via SessionReader.Load, rather than only
    // writing forward. Null for a caller that hasn't supplied it: treat null as "no prior data available".
    public string? SessionDirectory { get; init; }
}
