using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Sidecar written next to usmt.ppkg (operator decision, session 107): <Recovery>\Customizations\usmt.manifest.json.
// Records what the capture was and what it was made on, so the Detect screen can say "captured on build X, this PC is
// on build Y" (C12). Windows only consumes .ppkg/.spp, so a .json beside the package should be ignored by Reset this
// PC; that is still unverified and is a Phase 4 check.
public sealed record DeploymentManifest(
    int SchemaVersion,
    DateTime CapturedUtc,
    string PackageFileName,
    long PackageSizeBytes,
    string OsBuild,              // "<CurrentBuild>.<UBR>", e.g. 26100.4061
    string OsDisplayVersion,     // e.g. 24H2
    string OsProductName,
    string CaptureMode,
    string ConfigFile,
    bool IncludeOemInfo,
    string[] Exclusions,
    string[] CustomExclusions,
    string UsmtVersion,          // file version of the scanstate.exe that ran
    string UsmtArch,             // amd64 / x86
    string WinTesterVersion,
    bool CompletedWithWarnings,
    AppEntry[]? Apps = null,     // schema 2: apps chosen for the capture (null/empty in schema 1 files)
    string[]? DesktopShortcuts = null,   // shortcuts copied to Public Desktop for this capture; null = option was off
    bool IncludeDrivers = false,         // ScanState /drivers was passed; absent in older files = off
    bool IncludeServices = false,        // 3.11: safe services of the selected apps were carried; absent = off
    bool IncludeStartup = false,         // 3.11: Run values and boot/logon tasks of the selected apps were carried; absent = off
    bool IncludeSweep = false,           // 3.20: the wider search was on; absent = off
    CustomerFileEntry[]? CustomerFiles = null)   // 5.5: customer files carried in the package; null = none (no schema change: additive)
{
    public const int CurrentSchema = 2;
    public const string FileName = "usmt.manifest.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // usmt.ppkg -> usmt.manifest.json in the same folder.
    public static string PathFor(string packagePath) =>
        Path.Combine(Path.GetDirectoryName(packagePath) ?? "", FileName);

    // Feature-update number only ("26100"); monthly updates change the revision but not this.
    public static string MajorBuild(string osBuild)
    {
        var dot = osBuild.IndexOf('.');
        return dot < 0 ? osBuild : osBuild[..dot];
    }

    public bool IsStaleFor(string currentOsBuild) =>
        !string.IsNullOrEmpty(currentOsBuild) && !string.IsNullOrEmpty(OsBuild)
        && MajorBuild(OsBuild) != MajorBuild(currentOsBuild);

    public static void Write(string packagePath, DeploymentManifest manifest)
    {
        File.WriteAllText(PathFor(packagePath), JsonSerializer.Serialize(manifest, Json));
    }

    // Returns null with `problem` set when the file is missing or unreadable; never throws.
    public static DeploymentManifest? TryRead(string packagePath, out string? problem)
    {
        problem = null;
        var path = PathFor(packagePath);
        try
        {
            if (!File.Exists(path))
            {
                problem = "No manifest found next to the package (it was not made by this tool, or the file was removed).";
                return null;
            }
            return TryParse(File.ReadAllText(path), out problem);
        }
        catch (Exception ex)
        {
            problem = "The manifest could not be read: " + ex.Message;
            return null;
        }
    }

    // The same checks for manifest text that did not come from the Customizations folder (5.1: the manifest inside a backup zip).
    public static DeploymentManifest? TryParse(string json, out string? problem)
    {
        problem = null;
        try
        {
            var m = JsonSerializer.Deserialize<DeploymentManifest>(json, Json);
            if (m is null || m.SchemaVersion <= 0)
            {
                problem = "The manifest file is empty or not in a recognised format.";
                return null;
            }
            if (m.SchemaVersion > CurrentSchema)
                problem = "The manifest was written by a newer version of this tool; some details may be missing.";
            return m;
        }
        catch (Exception ex)
        {
            problem = "The manifest could not be read: " + ex.Message;
            return null;
        }
    }

    // Built by the Phase 3 runner after a successful capture; here so the format lives in one place.
    public static DeploymentManifest Create(CaptureOptions options, string usmtDirectory, string usmtArchFolder,
        string packagePath, bool completedWithWarnings, IReadOnlyList<string>? desktopShortcuts = null,
        IReadOnlyList<CustomerFileEntry>? customerFiles = null)
    {
        var appEntries = options.ActiveApps
            .Select(a => new AppEntry(a.Name, a.Version, a.Publisher, a.Folder, a.FilesFor(options).Count, a.RegistryFor(options).Count,
                a.ServiceItemsFor(options).ToArray(), a.StartupItemsFor(options).ToArray(),
                a.ProfileNames.ToArray(), a.ExcludedItems.ToArray(), a.IsCustom ? true : null,
                options.IncludeSweep ? a.SweepItems.ToArray() : null))
            .ToArray();
        var os = CurrentOs.Read();
        long size = 0;
        try { size = new FileInfo(packagePath).Length; } catch { }

        var usmtVersion = "";
        try { usmtVersion = FileVersionInfo.GetVersionInfo(Path.Combine(usmtDirectory, UsmtLocator.ScanStateExe)).FileVersion ?? ""; }
        catch { }

        return new DeploymentManifest(
            CurrentSchema, DateTime.UtcNow, Path.GetFileName(packagePath), size,
            os.Build, os.DisplayVersion, os.ProductName,
            options.Mode.ToString(), options.ConfigFileName, options.IncludeOemInfo,
            options.ExclusionLabels().ToArray(), options.CustomExclusions.ToArray(),
            usmtVersion, usmtArchFolder,
            typeof(DeploymentManifest).Assembly.GetName().Version?.ToString() ?? "",
            completedWithWarnings, appEntries,
            options.CopyDesktopShortcuts ? (desktopShortcuts?.ToArray() ?? Array.Empty<string>()) : null,
            options.IncludeDrivers, options.IncludeServices, options.IncludeStartup, options.IncludeSweep,
            options.CustomerFiles.Count > 0 ? (customerFiles?.ToArray() ?? Array.Empty<CustomerFileEntry>()) : null);
    }
}

// What the manifest remembers per captured app. Rule counts only; the full rule text is in the run's rules.xml.
// Services and Startup list what was carried for the app ("Service: x", "Run entry: x", "Scheduled task: \\x"); null in older files.
public sealed record AppEntry(string Name, string Version, string Publisher, string Folder, int FileRules, int RegistryRules,
    string[]? Services = null, string[]? Startup = null, string[]? Profiles = null, string[]? Excluded = null,
    bool? Custom = null,    // 3.18: added by folder (no Uninstall entry); absent otherwise
    string[]? Sweep = null);   // 3.20: what the wider search carried for the app; null when the option was off

public sealed record OsVersionInfo(string Build, string DisplayVersion, string ProductName);

public static class CurrentOs
{
    public static OsVersionInfo Read()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k is null) return Fallback();

            var build = (k.GetValue("CurrentBuild") as string ?? k.GetValue("CurrentBuildNumber") as string ?? "").Trim();
            var ubr = k.GetValue("UBR") is int u ? u.ToString() : "";
            var full = build.Length == 0 ? Fallback().Build : (ubr.Length > 0 ? build + "." + ubr : build);
            var display = (k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string ?? "").Trim();
            var product = FixProductName((k.GetValue("ProductName") as string ?? "").Trim(), build);
            return new OsVersionInfo(full, display, product);
        }
        catch { return Fallback(); }
    }

    // Windows 11 still writes "Windows 10 ..." into the ProductName registry value (Microsoft never changed it).
    // Build 22000 and up is Windows 11 by definition, so correct the label; activation/KMS does not matter here.
    internal static string FixProductName(string product, string build)
    {
        if (int.TryParse(build, out var b) && b >= 22000 &&
            product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            return "Windows 11" + product.Substring("Windows 10".Length);
        return product;
    }

    private static OsVersionInfo Fallback()
    {
        var v = Environment.OSVersion.Version;
        return new OsVersionInfo(v.Build + "." + v.Revision, "", RuntimeInformation.OSDescription);
    }
}
