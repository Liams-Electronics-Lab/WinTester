using WinTester.Core;

namespace WinTester.Tests.Cpu;

// Exit code contract every IPDT compute module implements; a far simpler result contract than scraping log output. Values per the vendored "Modules return error code description.docx" shipped with the IPDT source.
public enum IpdtExitCode
{
    Pass = 0,
    Fail = 1,
    SoftKilledOrIndeterminate = 2,
    NoCompareDisplayOnly = 3,
    InvalidCliArgument = 4,
    ConfigMismatch = 5
}

public static class IpdtExitCodeExtensions
{
    // Human-readable description for KeyMetrics/failure-reason text, keeping the meaning of a bare exit code visible to whoever reads the report months later.
    public static string Describe(this int exitCode) => exitCode switch
    {
        0 => "Pass",
        1 => "Fail",
        2 => "Soft-killed / indeterminate",
        3 => "No-compare / display-only mode",
        4 => "Invalid CLI argument",
        5 => "Config mismatch",
        _ => NtStatusCodes.Describe(exitCode) is { } known
            ? $"Unrecognized exit code ({exitCode}) \u2014 {known}"
            : $"Unrecognized exit code ({exitCode})"
    };
}
