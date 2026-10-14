using System;

namespace WinTester.Core.Tpm;

// Plain data snapshot of the machine's TPM, as shown by the standalone BitLocker tool. Lives in Core
// (not WinTester.Tools.Bitlocker) so anything else that cares about TPM state — a Windows 11 readiness
// check, say — can reference it without taking a PowerShell dependency.
public sealed record TpmSnapshot
{
    // False both for a machine with no TPM at all and for one where the chip is present but disabled in
    // firmware hard enough that Windows doesn't enumerate it. The two are indistinguishable from the OS
    // side, which is why the UI wording says "no TPM detected" rather than "no TPM installed".
    public required bool IsPresent { get; init; }

    // Raw Win32_Tpm SpecVersion, which is a comma-separated list: spec revision, then the errata/revision
    // numbers, e.g. "2.0, 0, 1.59". Null when no TPM was found.
    public string? SpecVersion { get; init; }

    // Four-character vendor ID from ManufacturerIdTxt ("INTC", "AMD", "STM", "IFX", "NTC"). Older Windows
    // builds expose only the numeric ManufacturerId, in which case this is that number as text.
    public string? ManufacturerId { get; init; }

    // Vendor firmware version, e.g. "403.1.0.0". Distinct from SpecVersion: this is the chip's own
    // firmware revision, not which TPM specification it implements.
    public string? ManufacturerVersion { get; init; }

    public bool IsEnabled { get; init; }
    public bool IsActivated { get; init; }
    public bool IsOwned { get; init; }

    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    // Set only when the TPM read itself failed. A machine that genuinely has no TPM is not an error:
    // IsPresent is false and this stays null.
    public string? ErrorMessage { get; init; }

    // Just the spec revision from SpecVersion — "2.0" out of "2.0, 0, 1.59" — which is the number that
    // actually matters for BitLocker and Windows 11 eligibility.
    public string? SpecRevision
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SpecVersion)) return null;
            var first = SpecVersion.Split(',')[0].Trim();
            return first.Length == 0 ? null : first;
        }
    }

    // "TPM 2.0" / "TPM 1.2" / "No TPM detected". What the tool's headline row shows.
    public string VersionDisplay
    {
        get
        {
            if (!IsPresent) return "No TPM detected";
            return SpecRevision is { Length: > 0 } revision ? $"TPM {revision}" : "TPM (version unknown)";
        }
    }

    // A TPM has to be both enabled and activated before BitLocker can bind a key to it; "owned" only means
    // something already took ownership, which is normal on a machine that's been in use.
    public bool IsUsable => IsPresent && IsEnabled && IsActivated;

    public static TpmSnapshot NotPresent() => new() { IsPresent = false };

    public static TpmSnapshot Failed(string message) => new() { IsPresent = false, ErrorMessage = message };
}
