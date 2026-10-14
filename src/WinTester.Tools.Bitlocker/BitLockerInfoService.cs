using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WinTester.Core.Bitlocker;

namespace WinTester.Tools.Bitlocker;

// Reads current BitLocker status for the boot drive via Get-BitLockerVolume, mirroring bitlocker.ps1's
// Show-BitLockerInfo source and RecoveryPassword key-protector lookup exactly. Read-only; never writes
// or toggles protection; see BitLockerOperationsService for the mutating and backup-text paths.
public static class BitLockerInfoService
{
    // Each field is .ToString()'d in PowerShell before ConvertTo-Json runs, not left to ConvertTo-Json's
    // PowerShell-version-dependent enum handling, guarantees VolumeStatus/ProtectionStatus/
    // EncryptionMethod come back as plain strings regardless of PowerShell build.
    private const string SnapshotScript = @"
$ErrorActionPreference = 'Stop'
try {
    $vol = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop
    $kp = $vol.KeyProtector | Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } | Select-Object -First 1
    [PSCustomObject]@{
        MountPoint        = $vol.MountPoint
        VolumeStatus      = $vol.VolumeStatus.ToString()
        ProtectionStatus  = $vol.ProtectionStatus.ToString()
        EncryptionMethod  = $vol.EncryptionMethod.ToString()
        RecoveryPassword  = if ($kp) { $kp.RecoveryPassword } else { $null }
        ErrorMessage      = $null
    } | ConvertTo-Json -Compress
} catch {
    [PSCustomObject]@{
        MountPoint        = $env:SystemDrive
        VolumeStatus      = 'Unknown'
        ProtectionStatus  = 'Unknown'
        EncryptionMethod  = 'Unknown'
        RecoveryPassword  = $null
        ErrorMessage      = $_.Exception.Message
    } | ConvertTo-Json -Compress
}
";

    public static async Task<BitLockerSnapshot> GetSnapshotAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Placeholder("BitLocker status is only available on Windows.");
        }

        return await CaptureAsync().ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<BitLockerSnapshot> CaptureAsync()
    {
        var result = await PowerShellRunner.RunAsync(SnapshotScript).ConfigureAwait(false);

        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
        {
            var message = string.IsNullOrWhiteSpace(result.Error)
                ? "Failed to read BitLocker status."
                : result.Error.Trim();
            return Placeholder(message);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SnapshotDto>(result.Output.Trim(), JsonOptions);
            if (dto is null) return Placeholder("Failed to parse BitLocker status.");

            return new BitLockerSnapshot
            {
                MountPoint = dto.MountPoint ?? Environment.GetEnvironmentVariable("SystemDrive") ?? "C:",
                VolumeStatus = dto.VolumeStatus ?? "Unknown",
                ProtectionStatus = dto.ProtectionStatus ?? "Unknown",
                EncryptionMethod = dto.EncryptionMethod ?? "Unknown",
                RecoveryPassword = string.IsNullOrWhiteSpace(dto.RecoveryPassword) ? null : dto.RecoveryPassword,
                ErrorMessage = string.IsNullOrWhiteSpace(dto.ErrorMessage) ? null : dto.ErrorMessage
            };
        }
        catch (JsonException ex)
        {
            return Placeholder($"Failed to parse BitLocker status: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static BitLockerSnapshot Placeholder(string errorMessage) => new()
    {
        MountPoint = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:",
        VolumeStatus = "Unknown",
        ProtectionStatus = "Unknown",
        EncryptionMethod = "Unknown",
        RecoveryPassword = null,
        ErrorMessage = errorMessage
    };

    private sealed class SnapshotDto
    {
        [JsonPropertyName("MountPoint")] public string? MountPoint { get; set; }
        [JsonPropertyName("VolumeStatus")] public string? VolumeStatus { get; set; }
        [JsonPropertyName("ProtectionStatus")] public string? ProtectionStatus { get; set; }
        [JsonPropertyName("EncryptionMethod")] public string? EncryptionMethod { get; set; }
        [JsonPropertyName("RecoveryPassword")] public string? RecoveryPassword { get; set; }
        [JsonPropertyName("ErrorMessage")] public string? ErrorMessage { get; set; }
    }
}
