using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tools.Activation;

public readonly record struct ApplyKeyResult(bool Success, string InstallOutput, string ActivateOutput);

// Mutating/extraction operations beyond ActivationInfoService's read-only snapshot: applying a new
// product key (slmgr /ipk + /ato) and building the multi-method backup report text (a C# port of
// backup_key.ps1; same 5 extraction methods, same summary section) for the "Backup License" flow.
// Returned as plain text for the caller's Save As dialog; unlike backup_key.ps1, never writes to the
// Desktop itself.
public static class ProductKeyOperationsService
{
    private static readonly Regex ProductKeyShape = new(
        @"^[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}-[A-Za-z0-9]{5}$",
        RegexOptions.Compiled);

    // Loose format check only; not a guarantee slmgr will accept the key. Catches an obvious typo
    // before spending a round-trip on slmgr.vbs; not a substitute for slmgr's own validation.
    public static bool LooksLikeProductKey(string key) => ProductKeyShape.IsMatch(key.Trim());

    // Runs `slmgr /ipk <key>` then, on success, `slmgr /ato`, mirroring the operator's spec
    // ("cscript.exe ... /ipk YOUR-KEY-HERE /ato"). Stops after /ipk on failure: no point activating a
    // key that wasn't even installed.
    [SupportedOSPlatform("windows")]
    public static async Task<ApplyKeyResult> ApplyProductKeyAsync(string productKey)
    {
        var trimmed = productKey.Trim();
        var installResult = await SlmgrRunner.RunAsync("/ipk", trimmed).ConfigureAwait(false);
        if (!installResult.Success)
            return new ApplyKeyResult(false, installResult.Output, "");

        var activateResult = await SlmgrRunner.RunAsync("/ato").ConfigureAwait(false);
        return new ApplyKeyResult(activateResult.Success, installResult.Output, activateResult.Output);
    }

    // Builds the same report backup_key.ps1 writes to disk, minus the disk write, same 5 extraction
    // methods, summary section, and ordering. Returns plain text for a SaveFileDialog-backed writer.
    [SupportedOSPlatform("windows")]
    // Throws OperationCanceledException if cancellationToken fires; the slow part (slmgr /dlv) is killed
    // immediately rather than left running.
    public static async Task<string> BuildBackupReportAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => BuildBackupReportSync(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private const string NotReported = "Not reported";

    // Win32_ComputerSystem Manufacturer/Model and Win32_Bios SerialNumber: the same sources as the main
    // report's System Identity (WinTester.Tests.Power.SystemIdentityReader), duplicated here because this
    // project doesn't reference the Power test project. Each field is read independently and never throws;
    // OEM placeholder junk counts as not reported.
    [SupportedOSPlatform("windows")]
    private static (string? Manufacturer, string? Model, string? Serial) ReadSystemIdentity()
    {
        string? manufacturer = null, model = null, serial = null;

        try
        {
            using var cs = new System.Management.ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (System.Management.ManagementBaseObject obj in cs.Get())
            {
                manufacturer = CleanIdentity(obj["Manufacturer"] as string);
                model = CleanIdentity(obj["Model"] as string);
                break;
            }
        }
        catch { /* leave null; the BIOS read below is independent */ }

        try
        {
            using var bios = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_Bios");
            foreach (System.Management.ManagementBaseObject obj in bios.Get())
            {
                serial = CleanIdentity(obj["SerialNumber"] as string);
                break;
            }
        }
        catch { /* leave null */ }

        return (manufacturer, model, serial);
    }

    private static string? CleanIdentity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    [SupportedOSPlatform("windows")]
    private static string BuildBackupReportSync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sb = new StringBuilder();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        sb.AppendLine("========================================");
        sb.AppendLine("  Windows Activation Key Backup Report");
        sb.AppendLine($"  Generated: {timestamp}");
        sb.AppendLine("========================================");
        sb.AppendLine();

        // Machine identity, so a saved backup says which machine it belongs to.
        var (sysManufacturer, sysModel, sysSerial) = ReadSystemIdentity();
        sb.AppendLine("System Identity");
        sb.AppendLine($"Manufacturer\t{sysManufacturer ?? NotReported}");
        sb.AppendLine($"Product Name\t{sysModel ?? NotReported}");
        sb.AppendLine($"Serial Number\t{sysSerial ?? NotReported}");
        sb.AppendLine();

        // Method 1: OEM key from BIOS/UEFI
        sb.AppendLine("[Method 1] OEM Key from BIOS/UEFI");
        try
        {
            var oemKey = ActivationInfoService.ReadOa3xOriginalProductKey();
            if (!string.IsNullOrEmpty(oemKey))
            {
                sb.AppendLine($"  Key: {oemKey}");
                sb.AppendLine("  Status: SUCCESS");
            }
            else
            {
                sb.AppendLine("  Key: Not found (no OEM key in BIOS/UEFI)");
                sb.AppendLine("  Status: NOT FOUND");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  Key: Error retrieving");
            sb.AppendLine($"  Status: ERROR - {ex.Message}");
        }
        sb.AppendLine();

        // Method 2: Backup key from registry
        sb.AppendLine("[Method 2] Backup Key from Registry");
        try
        {
            var backupKey = ActivationInfoService.ReadRegistryString(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform", "BackupProductKeyDefault");
            if (!string.IsNullOrEmpty(backupKey))
            {
                sb.AppendLine($"  Key: {backupKey}");
                sb.AppendLine("  Status: SUCCESS");
            }
            else
            {
                sb.AppendLine("  Key: Not found");
                sb.AppendLine("  Status: NOT FOUND");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  Key: Error retrieving");
            sb.AppendLine($"  Status: ERROR - {ex.Message}");
        }
        sb.AppendLine();

        // Method 3: Partial key from active product (last 5 chars)
        sb.AppendLine("[Method 3] Partial Key from Active Product (last 5 chars)");
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'");
            var products = searcher.Get().Cast<System.Management.ManagementBaseObject>().ToList();
            var activeProduct = products.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p["PartialProductKey"] as string) && Convert.ToInt32(p["LicenseStatus"]) == 1);
            activeProduct ??= products.FirstOrDefault(p => !string.IsNullOrEmpty(p["PartialProductKey"] as string));

            if (activeProduct?["PartialProductKey"] is string partial && !string.IsNullOrEmpty(partial))
            {
                sb.AppendLine($"  Key: *****-*****-*****-*****-{partial}");
                sb.AppendLine("  Status: SUCCESS (partial key only)");
            }
            else
            {
                sb.AppendLine("  Key: Not found");
                sb.AppendLine("  Status: NOT FOUND");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  Key: Error retrieving");
            sb.AppendLine($"  Status: ERROR - {ex.Message}");
        }
        sb.AppendLine();

        // Method 4: Activation status via slmgr /dlv
        sb.AppendLine("[Method 4] Activation Status (slmgr /dlv)");
        try
        {
            var result = SlmgrRunner.RunSync(ct, "/dlv");
            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                var keyLines = result.Output.Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Contains("Product Key Channel") || l.Contains("KMS machine name")
                                                                    || l.Contains("Volume activation expiration"))
                    .ToList();
                if (keyLines.Count > 0)
                    sb.AppendLine($"  {string.Join("\n  ", keyLines)}");
                else
                    sb.AppendLine("  No key-related info found in output");
                sb.AppendLine("  Status: SUCCESS");
            }
            else
            {
                sb.AppendLine("  Status: ERROR - slmgr command failed");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Status: ERROR - {ex.Message}");
        }
        sb.AppendLine();

        ct.ThrowIfCancellationRequested(); // after Method 4's try/catch so its catch-all can't swallow it

        // Method 5: Decoded key from DigitalProductId (registry)
        sb.AppendLine("[Method 5] Decoded Key from DigitalProductId (Registry)");
        try
        {
            var digitalProductId = ActivationInfoService.ReadRegistryBytes(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DigitalProductId");
            if (digitalProductId is not null)
            {
                var decodedKey = DigitalProductIdKeyDecoder.Decode(digitalProductId);
                if (!string.IsNullOrEmpty(decodedKey))
                {
                    sb.AppendLine($"  Key: {decodedKey}");
                    sb.AppendLine("  Status: SUCCESS");
                }
                else
                {
                    sb.AppendLine("  Key: Decoding failed");
                    sb.AppendLine("  Status: ERROR");
                }
            }
            else
            {
                sb.AppendLine("  Key: DigitalProductId not found in registry");
                sb.AppendLine("  Status: NOT FOUND");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  Key: Error retrieving");
            sb.AppendLine($"  Status: ERROR - {ex.Message}");
        }
        sb.AppendLine();

        sb.AppendLine("========================================");
        sb.AppendLine("  SUMMARY & RECOMMENDATIONS");
        sb.AppendLine("========================================");
        sb.AppendLine();
        sb.AppendLine("  Notes:");
        sb.AppendLine("  - If Method 1 or 2 shows a key, that is your OEM/backup key.");
        sb.AppendLine("  - If Method 5 shows a key, that is the decoded key from the registry.");
        sb.AppendLine("  - For Digital License systems, no full key may be stored locally.");
        sb.AppendLine("  - KMS-activated systems use a generic key (e.g., VK7JG-NPHTM-C97JM-9MPGT-3V66T).");
        sb.AppendLine();
        sb.AppendLine("========================================");

        return sb.ToString();
    }
}
