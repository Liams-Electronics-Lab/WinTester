using System.Runtime.InteropServices;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Shared state for the Recovery Provisioning wizard (Tab 2, Deployment). Phase 1 = UI only: these types carry what
// the wizard shows and collects; the real capture logic (UsmtLocator, RulesXmlBuilder, ScanStateRunner) arrives in
// Phases 2-3 and will plug into IProvisioningWorker without changing the UI.

public enum CaptureMode { AppsAndSettings, AppsOnly, SettingsOnly }

public enum WizardOperation { Capture, Backup, Revert, Restore }

public enum WinReState { NotChecked, Enabled, Disabled, Unknown }

public sealed class CaptureOptions
{
    public CaptureMode Mode { get; set; } = CaptureMode.AppsAndSettings;
    public bool IncludeOemInfo { get; set; } = true;
    public bool ExcludeTemp { get; set; } = true;
    public bool ExcludeBrowserCaches { get; set; } = true;
    public bool ExcludeUpdateCache { get; set; } = true;
    public List<string> CustomExclusions { get; } = new();

    // Copy the running user's desktop shortcuts to <drive>\Users\Public\Desktop and add that folder to the package, so
    // they show on every account after a reset. Off by default: it writes to this PC before the capture starts.
    public bool CopyDesktopShortcuts { get; set; }

    // 5.5: customer files and folders put back at a fixed place after a reset (see CustomerFileSet). Empty by default: nothing is
    // copied or added unless the operator lists something on the Configure step.
    public List<CustomerFileItem> CustomerFiles { get; } = new();

    // What to do with a file that already exists at a destination and differs (decided on the gate dialog before the Review step; the safe default if it was never asked).
    // Choices (policy Choose) is keyed by the full target path.
    public CustomerConflictPolicy CustomerPolicy { get; set; } = CustomerConflictPolicy.ReplaceIfNewer;
    public Dictionary<string, ConflictChoice> CustomerChoices { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Pass /drivers to ScanState so the third-party (INF) drivers installed now go into the package. On by default:
    // a driver still installed at reset time comes back anyway, so this matters for drivers removed after the capture.
    // Windows' own (inbox) drivers are not carried.
    public bool IncludeDrivers { get; set; } = true;

    // Per-app extras, switched on the Configure step without a rescan (3.11, C40). Both on by default. Services: only
    // the safe subset (ServiceRules). Startup: all-users Run values and boot/logon tasks that launch the app.
    public bool IncludeServices { get; set; } = true;
    public bool IncludeStartup { get; set; } = true;

    // 3.20: the wider search (registry strings elsewhere under SOFTWARE, firewall rules, ODBC, event-log sources, environment
    // variables, tasks with other triggers). On by default since S135: measured on a real PC (S134 run), it adds one background pass on
    // the Configure step and nothing to the ScanState time. Switched off on the Configure step.
    public bool IncludeSweep { get; set; } = true;

    // 3.20: "(index: N keys, X s)" of the search that ran on the Configure step, set by the window so the capture log carries the
    // scan time (the status line is not saved anywhere else). Empty when the option is off or the search did not run.
    public string SweepInfo { get; set; } = "";

    // Apps chosen on the Configure step (3.7/3.8). Ignored in System-settings-only mode.
    public List<AppCapturePlan> Apps { get; } = new();
    public IReadOnlyList<AppCapturePlan> ActiveApps => Mode == CaptureMode.SettingsOnly ? Array.Empty<AppCapturePlan>() : Apps;

    // ADK default configuration file that goes to ScanState /config: (required since Windows 10 1703).
    public string ConfigFileName => Mode switch
    {
        CaptureMode.AppsOnly => "Config_AppsOnly.xml",
        CaptureMode.SettingsOnly => "Config_SettingsOnly.xml",
        _ => "Config_AppsAndSettings.xml"
    };

    public string ModeText => Mode switch
    {
        CaptureMode.AppsOnly => "Desktop apps only",
        CaptureMode.SettingsOnly => "System settings only",
        _ => "Desktop apps and system settings"
    };

    public IReadOnlyList<string> ExclusionLabels()
    {
        var list = new List<string>();
        if (ExcludeTemp) list.Add("Temp folders");
        if (ExcludeBrowserCaches) list.Add("Browser caches");
        if (ExcludeUpdateCache) list.Add("Windows Update download cache");
        return list;
    }
}

public sealed record PackageInfo(string Path, long SizeBytes, DateTime LastWriteLocal);

public sealed record DeploymentSnapshot(
    bool IsPreview,
    bool IsElevated,
    Architecture OsArchitecture,
    string? UsmtDirectory,          // resolved vendor\usmt\<arch> folder, null if scanstate.exe wasn't found
    string SystemDrive,             // e.g. "C:"
    long FreeBytes,
    string PackagePath,             // <drive>\Recovery\Customizations\usmt.ppkg
    PackageInfo? Package,           // null = no deployment from this tool
    IReadOnlyList<string> OtherPackages,
    bool OemHookPresent,            // C:\Recovery\OEM\ResetConfig.xml (staged by the OEM Branding tool)
    bool AutoApplyPresent,          // C:\Recovery\AutoApply
    bool OemInfoSet,
    WinReState WinRe,
    DeploymentManifest? Manifest,        // sidecar next to usmt.ppkg, null if absent/unreadable
    string? ManifestNote,                // why Manifest is null (or a version warning)
    string CurrentOsBuild,               // "<build>.<revision>" of this PC, for the stale-capture row
    IReadOnlyList<string> UsmtProblems)  // missing USMT files/folders from the pre-flight (empty = complete)
{
    public const long MinFreeBytes = 5L * 1024 * 1024 * 1024;

    public bool HasDeployment => Package is not null;
    public bool ArchitectureSupported => OsArchitecture is Architecture.X64 or Architecture.X86;
    public bool UsmtFound => UsmtDirectory is not null;
    public bool EnoughFreeSpace => FreeBytes >= MinFreeBytes;
}

public sealed class WizardContext
{
    public DeploymentSnapshot Snapshot { get; set; } = null!;
    public WizardOperation Operation { get; set; } = WizardOperation.Capture;
    public CaptureOptions Options { get; } = new();
    public string? BackupFirstPath { get; set; }   // Overwrite: back the current package up here before replacing it
    public string? BackupPath { get; set; }        // Backup operation: zip destination
    public string? RestorePath { get; set; }       // 5.1 Restore operation: the backup zip to read
}

public sealed record WorkerUpdate(string? LogLine, int? Percent, string? Status);

// Warnings = the operation succeeded but the log needs a look (amber banner). LogFolder = where the run logs were kept.
public sealed record WorkerResult(bool Success, bool Cancelled, string Summary, bool Warnings = false, string? LogFolder = null);

// The wizard's Progress step runs one of these. Phase 1 ships SimulatedProvisioningWorker only; Phase 3 replaces it
// with the real ScanState-backed worker.
public interface IProvisioningWorker
{
    bool IsSimulation { get; }
    Task<WorkerResult> RunAsync(WizardContext context, IProgress<WorkerUpdate> progress, CancellationToken ct);
}

public static class RecoveryFormat
{
    public static string Bytes(long bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        const double mb = 1024d * 1024;
        if (bytes >= gb) return $"{bytes / gb:0.0} GB";
        if (bytes >= mb) return $"{bytes / mb:0.0} MB";
        return $"{Math.Max(0, bytes) / 1024d:0} KB";
    }
}
