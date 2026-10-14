using System.Collections.Generic;

namespace WinTester.Core;

// Minimal shared table of Windows NTSTATUS values that show up as a vendored engine's process exit code
// when the OS loader kills the process before it ever reaches main() (or otherwise, before it can print
// anything of its own). A plain "Unrecognized exit code" is technically true but useless to whoever
// reads the report later; these three specifically explain the most common ways a vendored engine dies
// silently on a machine it wasn't built/tested against (see the field-issues tracker's 1.1/1.2: dgemm,
// fma3, and GravityMark on all four render backends all die this way with zero stdout/stderr).
// Consumed by IpdtExitCode.cs (CPU) and by GpuStressTestModule.cs's exit-code messages (GPU); both engine
// families exit through the same OS loader, so the same table applies to either.
public static class NtStatusCodes
{
    // Keyed by the code's *signed* 32-bit int value, matching what Process.ExitCode actually returns.
    private static readonly Dictionary<int, string> Known = new()
    {
        // 0xC0000135 STATUS_DLL_NOT_FOUND
        [unchecked((int)0xC0000135)] = "missing a required system DLL (commonly the VC++ Redistributable)",
        // 0xC0000005 STATUS_ACCESS_VIOLATION
        [unchecked((int)0xC0000005)] = "crashed with an access violation",
        // 0xC000007B STATUS_INVALID_IMAGE_FORMAT (usually a 32/64-bit bitness mismatch)
        [unchecked((int)0xC000007B)] = "bitness/image-format mismatch (32-bit vs 64-bit build)",
    };

    // Null when the code isn't one of the ones above; callers append their own "Unrecognized exit code"
    // (or equivalent) fallback text rather than this returning one, so each caller's existing phrasing
    // for the unknown case stays intact.
    public static string? Describe(int exitCode) => Known.TryGetValue(exitCode, out var text) ? text : null;
}
