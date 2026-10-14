using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Core;

public sealed record VcRedistBootstrapResult(bool Attempted, bool Succeeded, int? ExitCode, string Message);

// Reactively installs missing runtime dependencies (e.g., VC++ Redistributable) upon detecting a native loader failure.
// Executes at most once per process as a last-resort safety net to recover from startup crashes before retry.
public static class VcRedistBootstrap
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static VcRedistBootstrapResult? _cachedResult;

   // Defines the central staging directory for bundled redistributable installers, decoupled from specific vendor packages.
// Centralizes runtime dependencies to ensure unified access across all execution pathways.
    public static string DefaultRedistPath => Path.Combine(
        AppContext.BaseDirectory, "vendor", "redist", "vc_redist.x64.exe");

    // ct: honored before the gate and before waiting on the child process; not honored mid-install once the
    // installer has actually started (killing a partially-applied system-wide install would leave the
    // machine in a worse state than either finishing or never starting).
    public static async Task<VcRedistBootstrapResult> TryInstallOnceAsync(CancellationToken ct, string? redistExePath = null)
    {
        await Gate.WaitAsync(ct);
        try
        {
            // Second and later callers in the same process (any engine, any subsequent failure) get the
            // first attempt's cached outcome instead of re-running the installer.
            if (_cachedResult is { } cached) return cached;

            var exePath = redistExePath ?? DefaultRedistPath;
            if (!File.Exists(exePath))
            {
                return _cachedResult = new VcRedistBootstrapResult(
                    Attempted: false, Succeeded: false, ExitCode: null,
                    Message: $"vc_redist.x64.exe not found at \"{exePath}\"; no fallback installer available " +
                             "on this build (nothing to run, not a failed run).");
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                // /install is redundant with the bootstrapper's default action but explicit and future-proof;
                // /passive shows a progress UI but takes no interaction (chosen over /quiet: this fires
                // mid-session on a machine a technician is actively watching, so a several-second stretch
                // with zero visible feedback is a worse experience than a plain progress bar); /norestart
                // avoids a forced reboot (a surprise reboot mid test-session would be a far worse failure
                // mode than leaving the loader problem unresolved for this run).
                Arguments = "/install /passive /norestart",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return _cachedResult = new VcRedistBootstrapResult(
                    Attempted: true, Succeeded: false, ExitCode: null,
                    Message: $"Failed to launch \"{exePath}\": {ex.Message}");
            }

            // No cancellation token here on purpose: see the ct doc comment above.
            await process.WaitForExitAsync(CancellationToken.None);

            // 0 = success. 3010 = success, reboot required (Microsoft's documented ERROR_SUCCESS_REBOOT_REQUIRED
            // for this installer family). Treated as success either way for this session's purposes: a
            // reboot-required result still means the DLLs landed on disk, and a hard "failed" verdict here for
            // what's normally a fully-working install would send a technician chasing the wrong problem.
            bool succeeded = process.ExitCode is 0 or 3010;
            var message = process.ExitCode switch
            {
                0 => "vc_redist.x64.exe ran (passive UI) and reported success.",
                3010 => "vc_redist.x64.exe ran (passive UI) and reported success (reboot recommended, not required for this session).",
                _ => $"vc_redist.x64.exe ran (passive UI) and exited with code {process.ExitCode} (not a recognized success code)."
            };

            return _cachedResult = new VcRedistBootstrapResult(
                Attempted: true, Succeeded: succeeded, ExitCode: process.ExitCode, Message: message);
        }
        finally
        {
            Gate.Release();
        }
    }
}
