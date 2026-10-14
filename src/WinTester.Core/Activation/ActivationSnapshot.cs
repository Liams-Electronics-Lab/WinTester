using System;

namespace WinTester.Core.Activation;

// Plain data snapshot of a machine's Windows activation state, used by both the standalone Activation
// tool and the report header. Lives in Core (not WinTester.Tools.Activation) so Reporting can reference
// it without pulling in a System.Management dependency.
public sealed record ActivationSnapshot
{
    public required string OsName { get; init; }
    public required string OsVersion { get; init; }
    public required string BuildNumber { get; init; }

    // Decoded SoftwareLicensingProduct.LicenseStatus (e.g. "Licensed", "Unlicensed", "OOB Grace"), or
    // "Unknown" if no active product could be resolved.
    public required string LicenseStatus { get; init; }

    // "KMS", "OEM (BIOS)", "Digital License (hardware/microsoft account)", or "Unknown".
    public required string ActivationMethod { get; init; }

    public string? KmsServer { get; init; }
    public string? KmsPort { get; init; }

    public required string ProductKey { get; init; }

    // Where ProductKey came from; "Registry (backup)", "BIOS", "Partial (last 5 chars)", or empty if none found.
    public string? KeySource { get; init; }

    // Raw `slmgr /xpr` output line, e.g. "The machine is permanently activated." or a grace-period expiry date.
    public required string ExpiryText { get; init; }

    // True only when ExpiryText indicates permanent activation. Drives the genuine/non-genuine banner;
    // anything short of permanently activated counts as non-genuine for display, regardless of LicenseStatus.
    public required bool IsPermanentlyActivated { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // Set only when the snapshot failed outright; every other field still gets a safe placeholder.
    public string? ErrorMessage { get; init; }
}
