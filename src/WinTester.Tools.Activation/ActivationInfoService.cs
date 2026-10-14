using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinTester.Core.Activation;

namespace WinTester.Tools.Activation;

// Reads current Windows activation/licensing status, mirroring get_activation.ps1's data sources and
// fallback order exactly (that script can't run unsigned on an untrusted machine; this reimplements
// the same WMI/registry/cscript scaffold as a compiled, signed assembly). Read-only; see
// ProductKeyOperationsService for the mutating (re-key) and backup-extraction paths.
public static class ActivationInfoService
{
    private const string SlpApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";
    private const string SppRegPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform";
    private const string CurrentVersionRegPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // License status code -> text, from get_activation.ps1's $licenseStatusText map.
    private static readonly string[] LicenseStatusText =
    {
        "Unlicensed", "Licensed", "OOB Grace", "OOT Grace", "Non-Genuine Grace", "Notification", "Extended Grace"
    };

    public static async Task<ActivationSnapshot> GetSnapshotAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Placeholder("Activation status is only available on Windows.");
        }

        // Local info and the expiry check are independent; run concurrently. This is the non-progressive
        // combined caller used by report generation; see GetLocalSnapshotAsync/GetExpiryAsync for
        // ActivationWindow's progressive two-stage version.
        var localTask = GetLocalSnapshotAsync();
        var expiryTask = GetExpiryAsync();
        await Task.WhenAll(localTask, expiryTask).ConfigureAwait(false);

        var local = localTask.Result;
        if (local.ErrorMessage is not null) return local; // hard failure reading local info, nothing to merge

        var (expiryText, isPermanent) = expiryTask.Result;
        return local with { ExpiryText = expiryText, IsPermanentlyActivated = isPermanent };
    }

    // Fast half of a snapshot: OS info, license status, activation method, KMS server, product key, all
    // local WMI/registry reads, typically under a second. Excludes the expiry check (see GetExpiryAsync),
    // the one slow part of the original single-call implementation: cscript.exe running slmgr.vbs /xpr can
    // take seconds to 20-30+ on real hardware (WSH + Software Protection Platform startup cost). Splitting
    // these lets a UI caller (ActivationWindow) show real data immediately instead of blocking on the slow
    // half. Returned ExpiryText/IsPermanentlyActivated are placeholders: merge in GetExpiryAsync's result
    // (e.g. via `with`), or just call GetSnapshotAsync.
    public static async Task<ActivationSnapshot> GetLocalSnapshotAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Placeholder("Activation status is only available on Windows.");
        }

        try
        {
            return await Task.Run(CaptureLocalWindows).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Placeholder($"Failed to read activation status: {ex.Message}");
        }
    }

    // Slow half of a snapshot: runs `slmgr /xpr`, derives IsPermanentlyActivated from the expiry text.
    // Bounded by SlmgrRunner's own 25s timeout so a stuck cscript.exe can't block the caller indefinitely;
    // a timeout is reported like any other failure ("Could not retrieve expiration info", not permanent).
    public static async Task<(string ExpiryText, bool IsPermanentlyActivated)> GetExpiryAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("Could not retrieve expiration info", false);
        }

        var result = await SlmgrRunner.RunAsync("/xpr").ConfigureAwait(false);
        var expiryText = result.Success && !string.IsNullOrWhiteSpace(result.Output)
            ? string.Join(" ", result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)).Trim()
            : "Could not retrieve expiration info";

        var isPermanent = expiryText.Contains("permanently activated", StringComparison.OrdinalIgnoreCase);
        return (expiryText, isPermanent);
    }

    [SupportedOSPlatform("windows")]
    private static ActivationSnapshot CaptureLocalWindows()
    {
        // 1. OS info (Win32_OperatingSystem)
        string osName = "Unknown", osVersion = "Unknown", build = "Unknown";
        using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem"))
        {
            var os = searcher.Get().Cast<System.Management.ManagementBaseObject>().FirstOrDefault();
            if (os is not null)
            {
                osName = os["Caption"]?.ToString()?.Trim() ?? osName;
                osVersion = os["Version"]?.ToString() ?? osVersion;
                build = os["BuildNumber"]?.ToString() ?? build;
            }
        }

        // 2a. Active SoftwareLicensingProduct (the one with a partial key), same filter/fallback as the ps1.
        // This WMI query can occasionally come back with zero rows on a machine that is in fact activated, 
        // a transient SoftwareLicensingProduct provider hiccup, not a real "no key found" state. Retried a
        // few times with a short delay before accepting an empty result as real, same defensive posture as
        // the rest of this file's try/catch-and-degrade reads.
        var products = QueryActiveLicensingProductsWithRetry();
        var activeProduct = products.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p["PartialProductKey"] as string) && Convert.ToInt32(p["LicenseStatus"]) == 1);
        activeProduct ??= products.FirstOrDefault(p => !string.IsNullOrEmpty(p["PartialProductKey"] as string));

        var licenseStatus = activeProduct is not null
            ? LicenseStatusIndexToText(Convert.ToInt32(activeProduct["LicenseStatus"]))
            : "Unknown";

        // 2b. Activation method / KMS server
        var activationMethod = "Unknown";
        string? kmsServer = null;
        string? kmsPort = null;
        string? productKey = null;
        string? keySource = null;

        var kmsReg = ReadRegistryString(SppRegPath, "KeyManagementServiceName");
        var kmsPortReg = ReadRegistryString(SppRegPath, "KeyManagementServicePort");

        if (!string.IsNullOrEmpty(kmsReg))
        {
            activationMethod = "KMS";
            kmsServer = kmsReg;
            kmsPort = string.IsNullOrEmpty(kmsPortReg) ? "1688" : kmsPortReg;
        }
        else if (activeProduct is not null)
        {
            var kmsMachine = activeProduct["KeyManagementServiceMachine"] as string;
            if (!string.IsNullOrEmpty(kmsMachine))
            {
                activationMethod = "KMS";
                kmsServer = kmsMachine;
                kmsPort = activeProduct["KeyManagementServicePort"]?.ToString();
            }
            else
            {
                var oemKey = ReadOa3xOriginalProductKey();
                if (!string.IsNullOrEmpty(oemKey))
                {
                    activationMethod = "OEM (BIOS)";
                    productKey = oemKey;
                    keySource = "BIOS";
                }
                else
                {
                    activationMethod = "Digital License (hardware/microsoft account)";
                }
            }
        }
        else
        {
            var oemKey = ReadOa3xOriginalProductKey();
            if (!string.IsNullOrEmpty(oemKey))
            {
                activationMethod = "OEM (BIOS)";
                productKey = oemKey;
                keySource = "BIOS";
            }
            else
            {
                activationMethod = "Unknown (may be Digital License)";
            }
        }

        // 2c. Product key fallback chain, same order as the ps1 script.
        if (string.IsNullOrEmpty(productKey))
        {
            var backupKey = ReadRegistryString(SppRegPath, "BackupProductKeyDefault");
            if (!string.IsNullOrEmpty(backupKey))
            {
                productKey = backupKey;
                keySource = "Registry (backup)";
            }
            else if (activeProduct is not null && activeProduct["PartialProductKey"] is string partial && !string.IsNullOrEmpty(partial))
            {
                productKey = $"*****-*****-*****-*****-{partial}";
                keySource = "Partial (last 5 chars)";
            }
            else
            {
                productKey = "Not available (no key stored locally)";
                keySource = "";
            }
        }

        // Expiry (slmgr /xpr) is intentionally not read here: see GetExpiryAsync, split out as the slow
        // part. Placeholder values below get overwritten once that call resolves.
        return new ActivationSnapshot
        {
            OsName = osName,
            OsVersion = osVersion,
            BuildNumber = build,
            LicenseStatus = licenseStatus,
            ActivationMethod = activationMethod,
            KmsServer = kmsServer,
            KmsPort = kmsPort,
            ProductKey = productKey ?? "Not available",
            KeySource = keySource,
            ExpiryText = "Checking activation expiry...",
            IsPermanentlyActivated = false
        };
    }

    // Up to 3 attempts, 250ms apart. A real "not activated" machine still returns rows for
    // SoftwareLicensingProduct (just with no PartialProductKey/LicenseStatus!=1): a genuinely empty result
    // set is the transient-provider signature this retries against, not a legitimate unlicensed state, so
    // retrying doesn't risk masking a real problem.
    [SupportedOSPlatform("windows")]
    private static List<System.Management.ManagementBaseObject> QueryActiveLicensingProductsWithRetry()
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(250);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT * FROM SoftwareLicensingProduct WHERE ApplicationID='{SlpApplicationId}'");
                var products = searcher.Get().Cast<System.Management.ManagementBaseObject>().ToList();
                if (products.Count > 0 || attempt == maxAttempts) return products;
            }
            catch when (attempt < maxAttempts)
            {
                // Swallow and retry; a genuine failure on the final attempt should still surface via the
                // outer try/catch in CaptureLocalWindows/GetLocalSnapshotAsync, not be hidden here.
            }

            Thread.Sleep(delay);
        }

        return new List<System.Management.ManagementBaseObject>();
    }

    private static string LicenseStatusIndexToText(int index) =>
        index >= 0 && index < LicenseStatusText.Length ? LicenseStatusText[index] : "Unknown";

    [SupportedOSPlatform("windows")]
    internal static string? ReadOa3xOriginalProductKey()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT OA3xOriginalProductKey FROM SoftwareLicensingService");
            var svc = searcher.Get().Cast<System.Management.ManagementBaseObject>().FirstOrDefault();
            return svc?["OA3xOriginalProductKey"] as string;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static string? ReadRegistryString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static byte[]? ReadRegistryBytes(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static ActivationSnapshot Placeholder(string errorMessage) => new()
    {
        OsName = "Unknown",
        OsVersion = "Unknown",
        BuildNumber = "Unknown",
        LicenseStatus = "Unknown",
        ActivationMethod = "Unknown",
        ProductKey = "Not available",
        KeySource = "",
        ExpiryText = "Could not retrieve expiration info",
        IsPermanentlyActivated = false,
        ErrorMessage = errorMessage
    };
}
