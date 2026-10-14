using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WinTester.Core.Tpm;

namespace WinTester.Tools.Bitlocker;

// Reads the machine's TPM state via Win32_Tpm, for display alongside BitLocker status. Read-only; never
// clears, provisions, or takes ownership of a TPM.
//
// Win32_Tpm rather than the Get-Tpm cmdlet: Get-Tpm reports presence and readiness but not which TPM
// specification the chip implements, and "is this 1.2 or 2.0" is the whole question being answered here.
// Win32_Tpm's SpecVersion carries it. Both require an elevated process; WinTester already runs elevated.
public static class TpmInfoService
{
    // Get-CimInstance returns nothing at all (rather than erroring) on a machine whose TPM namespace
    // exists but holds no instance, so the null case is handled explicitly and reported as a clean
    // not-present rather than falling through to the catch. Every value is cast to a plain string or bool
    // in PowerShell before ConvertTo-Json runs, so nothing depends on ConvertTo-Json's version-dependent
    // handling of CIM types.
    private const string SnapshotScript = @"
$ErrorActionPreference = 'Stop'
try {
    $tpm = Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction Stop
    if ($null -eq $tpm) {
        [PSCustomObject]@{
            IsPresent           = $false
            SpecVersion         = $null
            ManufacturerId      = $null
            ManufacturerVersion = $null
            IsEnabled           = $false
            IsActivated         = $false
            IsOwned             = $false
            ErrorMessage        = $null
        } | ConvertTo-Json -Compress
    } else {
        $mfr = if ($tpm.ManufacturerIdTxt) { [string]$tpm.ManufacturerIdTxt } elseif ($tpm.ManufacturerId) { [string]$tpm.ManufacturerId } else { $null }
        [PSCustomObject]@{
            IsPresent           = $true
            SpecVersion         = [string]$tpm.SpecVersion
            ManufacturerId      = $mfr
            ManufacturerVersion = [string]$tpm.ManufacturerVersion
            IsEnabled           = [bool]$tpm.IsEnabled_InitialValue
            IsActivated         = [bool]$tpm.IsActivated_InitialValue
            IsOwned             = [bool]$tpm.IsOwned_InitialValue
            ErrorMessage        = $null
        } | ConvertTo-Json -Compress
    }
} catch {
    [PSCustomObject]@{
        IsPresent           = $false
        SpecVersion         = $null
        ManufacturerId      = $null
        ManufacturerVersion = $null
        IsEnabled           = $false
        IsActivated         = $false
        IsOwned             = $false
        ErrorMessage        = $_.Exception.Message
    } | ConvertTo-Json -Compress
}
";

    // Well short of PowerShellRunner's 60s default: this is a single CIM read with no hardware round-trip
    // worth waiting a minute for, and the BitLocker window runs it alongside the volume query on every
    // refresh.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<TpmSnapshot> GetSnapshotAsync()
    {
        if (!OperatingSystem.IsWindows())
            return TpmSnapshot.Failed("TPM status is only available on Windows.");

        return await CaptureAsync().ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<TpmSnapshot> CaptureAsync()
    {
        var result = await PowerShellRunner.RunAsync(SnapshotScript, Timeout).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            var message = string.IsNullOrWhiteSpace(result.Error)
                ? "Failed to read TPM status."
                : result.Error.Trim();
            return TpmSnapshot.Failed(message);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<TpmDto>(result.Output.Trim(), JsonOptions);
            if (dto is null) return TpmSnapshot.Failed("Failed to parse TPM status.");

            if (dto.ErrorMessage is { Length: > 0 } error)
            {
                // A machine with no TPM at all reports the namespace or class as missing rather than
                // returning an empty result, so those specific failures are a clean "no TPM" rather than
                // something worth showing the operator as an error.
                return IsMissingTpmError(error) ? TpmSnapshot.NotPresent() : TpmSnapshot.Failed(error);
            }

            if (!dto.IsPresent) return TpmSnapshot.NotPresent();

            return new TpmSnapshot
            {
                IsPresent = true,
                SpecVersion = Clean(dto.SpecVersion),
                ManufacturerId = Clean(dto.ManufacturerId),
                ManufacturerVersion = Clean(dto.ManufacturerVersion),
                IsEnabled = dto.IsEnabled,
                IsActivated = dto.IsActivated,
                IsOwned = dto.IsOwned
            };
        }
        catch (JsonException ex)
        {
            return TpmSnapshot.Failed($"Failed to parse TPM status: {ex.Message}");
        }
    }

    // Matched on message text because WMI surfaces these as generic CIM exceptions through
    // Get-CimInstance, with no distinct error code reaching this far. Both the namespace-missing and
    // class-missing wordings are checked since which one a machine reports depends on its Windows build.
    private static bool IsMissingTpmError(string message) =>
        message.Contains("Invalid namespace", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Invalid class", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Not found", StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class TpmDto
    {
        [JsonPropertyName("IsPresent")] public bool IsPresent { get; set; }
        [JsonPropertyName("SpecVersion")] public string? SpecVersion { get; set; }
        [JsonPropertyName("ManufacturerId")] public string? ManufacturerId { get; set; }
        [JsonPropertyName("ManufacturerVersion")] public string? ManufacturerVersion { get; set; }
        [JsonPropertyName("IsEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("IsActivated")] public bool IsActivated { get; set; }
        [JsonPropertyName("IsOwned")] public bool IsOwned { get; set; }
        [JsonPropertyName("ErrorMessage")] public string? ErrorMessage { get; set; }
    }
}
