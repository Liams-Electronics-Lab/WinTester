using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tools.Activation;

public readonly record struct SlmgrResult(bool Success, string Output, int ExitCode);

// Runs `cscript.exe /nologo C:\Windows\System32\slmgr.vbs <args...>` and captures stdout, the exact
// invocation get_activation.ps1 uses for /xpr, backup_key.ps1 for /dlv, plus /ipk and /ato for "Change
// Product Key". Arguments go through ArgumentList, never a hand-built command-line string, so an
// operator-typed product key can't be interpreted as a second token or break out of quoting. Mutating
// slmgr.vbs calls need an elevated process (read-only /xpr, /dlv work unelevated); WinTester already
// runs elevated for hardware sensor access. Hard timeout (default 25s): the Activation window has no
// other bound on cscript.exe/slmgr.vbs latency (up to 30s is real WSH + Software Protection Platform
// startup cost, not a bug this wrapper can shortcut), so a hung process or broken SPP service could block
// the caller indefinitely; now killed and returned as an ordinary failure on timeout. See
// ActivationInfoService.GetLocalSnapshotAsync/GetExpiryAsync for the caller-side split that lets the
// window show fast local data immediately instead of blocking on this call.
public static class SlmgrRunner
{
    private const string SlmgrPath = @"C:\Windows\System32\slmgr.vbs";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    [SupportedOSPlatform("windows")]
    public static SlmgrResult RunSync(params string[] args) =>
        RunAsync(DefaultTimeout, CancellationToken.None, args).GetAwaiter().GetResult();

    // Cancellable variant: cancelling the token kills the cscript.exe process tree and returns a failed
    // result straight away; callers that care (the backup report) check the token themselves afterwards.
    [SupportedOSPlatform("windows")]
    public static SlmgrResult RunSync(CancellationToken cancellationToken, params string[] args) =>
        RunAsync(DefaultTimeout, cancellationToken, args).GetAwaiter().GetResult();

    [SupportedOSPlatform("windows")]
    public static Task<SlmgrResult> RunAsync(params string[] args) =>
        RunAsync(DefaultTimeout, CancellationToken.None, args);

    [SupportedOSPlatform("windows")]
    public static Task<SlmgrResult> RunAsync(TimeSpan timeout, params string[] args) =>
        RunAsync(timeout, CancellationToken.None, args);

    [SupportedOSPlatform("windows")]
    public static async Task<SlmgrResult> RunAsync(TimeSpan timeout, CancellationToken cancellationToken, params string[] args)
    {
        if (cancellationToken.IsCancellationRequested)
            return new SlmgrResult(false, "slmgr.vbs was cancelled.", -1);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cscript.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/nologo");
            psi.ArgumentList.Add(SlmgrPath);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return new SlmgrResult(false, "Failed to start cscript.exe.", -1);

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            // Linked so either the hard timeout or the caller's token (Cancel button) ends the wait.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort; already exiting is fine */ }
                return cancellationToken.IsCancellationRequested
                    ? new SlmgrResult(false, "slmgr.vbs was cancelled.", -1)
                    : new SlmgrResult(false, $"slmgr.vbs timed out after {timeout.TotalSeconds:0}s.", -1);
            }

            var stdOut = await stdOutTask.ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);

            var combined = new StringBuilder(stdOut);
            if (!string.IsNullOrWhiteSpace(stdErr))
                combined.Append('\n').Append(stdErr);

            return new SlmgrResult(process.ExitCode == 0, combined.ToString(), process.ExitCode);
        }
        catch (Exception ex)
        {
            return new SlmgrResult(false, $"Error running slmgr.vbs: {ex.Message}", -1);
        }
    }
}

