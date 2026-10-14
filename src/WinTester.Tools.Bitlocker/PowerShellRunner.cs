using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tools.Bitlocker;

public readonly record struct PowerShellResult(bool Success, string Output, string Error, int ExitCode);

// Runs `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command <script>`, capturing
// stdout/stderr separately: same Get-BitLockerVolume/Enable-BitLocker/Disable-BitLocker cmdlets as the
// operator's bitlocker.ps1, invoked one call at a time instead of via that unsigned interactive script
// (bitlocker.ps1 itself can't run on the operator's machine unsigned; see SlmgrRunner for the same
// constraint on get_activation.ps1). Script text is passed as a single ArgumentList element, never
// concatenated into a command-line string, so nothing embedded in a drive letter or error message can
// break out of it. Requires an elevated process, same as slmgr.vbs's mutating calls [WinTester already
// runs elevated for sensor access. Same hard-timeout posture as SlmgrRunner: Get-BitLockerVolume is fast,
// but Enable/Disable-BitLocker only kick off the encrypt/decrypt pass and return immediately] still
// bounded (default 60s, longer than slmgr's 25s for TPM/key-protector round-trip headroom) so a hung
// powershell.exe can't block the caller indefinitely.
public static class PowerShellRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    [SupportedOSPlatform("windows")]
    public static Task<PowerShellResult> RunAsync(string script) => RunAsync(script, DefaultTimeout);

    [SupportedOSPlatform("windows")]
    public static async Task<PowerShellResult> RunAsync(string script, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null)
                return new PowerShellResult(false, "", "Failed to start powershell.exe.", -1);

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort; already exiting is fine */ }
                return new PowerShellResult(false, "", $"powershell.exe timed out after {timeout.TotalSeconds:0}s.", -1);
            }

            var stdOut = await stdOutTask.ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);

            return new PowerShellResult(process.ExitCode == 0 && string.IsNullOrWhiteSpace(stdErr), stdOut, stdErr, process.ExitCode);
        }
        catch (Exception ex)
        {
            return new PowerShellResult(false, "", $"Error running powershell.exe: {ex.Message}", -1);
        }
    }
}
