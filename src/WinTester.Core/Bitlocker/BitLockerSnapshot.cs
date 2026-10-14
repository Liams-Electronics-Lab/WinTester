using System;

namespace WinTester.Core.Bitlocker;

// Plain data snapshot of the boot drive's BitLocker state, used by the standalone BitLocker tool. Lives
// in Core (not WinTester.Tools.Bitlocker) so other projects can reference it without a PowerShell dependency.
public sealed record BitLockerSnapshot
{
    public required string MountPoint { get; init; }

    // Raw Get-BitLockerVolume VolumeStatus text: "FullyEncrypted", "FullyDecrypted", "EncryptionInProgress", etc.
    public required string VolumeStatus { get; init; }

    // Raw ProtectionStatus text: "On" or "Off" ("Unknown" on a read failure).
    public required string ProtectionStatus { get; init; }

    public required string EncryptionMethod { get; init; }

    // The 48-digit recovery password if a RecoveryPassword protector exists, otherwise null.
    public string? RecoveryPassword { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // Set only when the snapshot failed outright; every other field still gets a safe placeholder.
    public string? ErrorMessage { get; init; }

    // Drives the enabled/disabled badge. A drive mid-encryption counts as not-yet-enabled.
    public bool IsProtectionOn =>
        string.Equals(ProtectionStatus, "On", StringComparison.OrdinalIgnoreCase);

    // Whether a recovery key exists to show or back up.
    public bool HasRecoveryKey => !string.IsNullOrWhiteSpace(RecoveryPassword);
}
