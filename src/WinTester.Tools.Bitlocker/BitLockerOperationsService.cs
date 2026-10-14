using System;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using WinTester.Core.Bitlocker;

namespace WinTester.Tools.Bitlocker;

public readonly record struct BitLockerActionResult(bool Success, string Output);

// Mutating operations beyond BitLockerInfoService's read-only snapshot: enabling BitLocker on the boot
// drive (TPM + recovery-password protector, mirrors bitlocker.ps1's Enable-BitLockerBootDrive) and
// disabling it (Disable-BitLockerBootDrive). Also builds the recovery-key backup report text for the
// "Backup key" flow, returned as plain text for the caller's Save As dialog; never writes to disk
// itself, same posture as ProductKeyOperationsService's Activation-tool equivalent.
public static class BitLockerOperationsService
{
    // Mirrors bitlocker.ps1's Enable-BitLockerBootDrive: TPM + recovery-password protector,
    // -SkipHardwareTest to avoid a forced restart before encryption starts.
    private const string EnableScript = @"
$ErrorActionPreference = 'Stop'
try {
    Enable-BitLocker -MountPoint $env:SystemDrive -TpmProtector -RecoveryPasswordProtector -SkipHardwareTest -ErrorAction Stop | Out-Null
    Write-Output 'OK'
} catch {
    Write-Error $_.Exception.Message
}
";

    // Mirrors bitlocker.ps1's Disable-BitLockerBootDrive.
    private const string DisableScript = @"
$ErrorActionPreference = 'Stop'
try {
    Disable-BitLocker -MountPoint $env:SystemDrive -ErrorAction Stop | Out-Null
    Write-Output 'OK'
} catch {
    Write-Error $_.Exception.Message
}
";

    [SupportedOSPlatform("windows")]
    public static async Task<BitLockerActionResult> EnableAsync()
    {
        var result = await PowerShellRunner.RunAsync(EnableScript).ConfigureAwait(false);
        return ToActionResult(result);
    }

    [SupportedOSPlatform("windows")]
    public static async Task<BitLockerActionResult> DisableAsync()
    {
        var result = await PowerShellRunner.RunAsync(DisableScript).ConfigureAwait(false);
        return ToActionResult(result);
    }

    private static BitLockerActionResult ToActionResult(PowerShellResult result)
    {
        if (result.Success) return new BitLockerActionResult(true, result.Output.Trim());

        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        return new BitLockerActionResult(false, message);
    }

    // Plain-text recovery-key backup report for a SaveFileDialog-backed writer. Same header/timestamp
    // style as ProductKeyOperationsService.BuildBackupReportAsync (Activation tool); kept simple since
    // there's one value worth backing up here: the numerical recovery password.
    public static string BuildKeyBackupReport(BitLockerSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        sb.AppendLine("========================================");
        sb.AppendLine("  BitLocker Recovery Key Backup");
        sb.AppendLine($"  Generated: {timestamp}");
        sb.AppendLine("========================================");
        sb.AppendLine();
        sb.AppendLine($"Drive              : {snapshot.MountPoint}");
        sb.AppendLine($"Volume Status      : {snapshot.VolumeStatus}");
        sb.AppendLine($"Protection Status  : {snapshot.ProtectionStatus}");
        sb.AppendLine($"Encryption Method  : {snapshot.EncryptionMethod}");
        sb.AppendLine();
        sb.AppendLine("RECOVERY PASSWORD");
        sb.AppendLine("--------------------------------------");
        sb.AppendLine(snapshot.HasRecoveryKey ? snapshot.RecoveryPassword : "Not available");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Store this file somewhere safe. Anyone with this recovery password can");
        sb.AppendLine("unlock this drive.");

        return sb.ToString();
    }
}
