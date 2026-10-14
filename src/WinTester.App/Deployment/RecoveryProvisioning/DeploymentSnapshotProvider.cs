using System.Runtime.InteropServices;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Read-only look at the machine for the wizard's first screen. Cheap checks only; the WinRE check is slower (it runs
// reagentc) so the window fills that row in afterwards via WinReCheck.Read. Nothing here writes to the machine.
//
// UI review without a Windows test box: set the environment variable WINTESTER_RP_PREVIEW to
//   none      - synthetic "no deployment yet" state
//   existing  - synthetic "package already deployed" state
//   lowdisk   - synthetic "no deployment, only 3 GB free" state
// The wizard then shows a PREVIEW chip and uses no real machine data.
public static class DeploymentSnapshotProvider
{
    public const string PreviewEnvVar = "WINTESTER_RP_PREVIEW";

    public static DeploymentSnapshot Read()
    {
        var preview = Environment.GetEnvironmentVariable(PreviewEnvVar)?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(preview) ? ReadReal() : ReadPreview(preview);
    }

    // Re-reads free space only (used when entering the Configuration step, in case the disk changed).
    public static long ReadFreeBytes(DeploymentSnapshot snapshot)
    {
        if (snapshot.IsPreview) return snapshot.FreeBytes;
        var free = DiskSpaceCheck.ReadFreeBytes(snapshot.SystemDrive);
        return free > 0 ? free : snapshot.FreeBytes;
    }

    private static DeploymentSnapshot ReadReal()
    {
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var det = DeploymentDetector.Detect(sysDrive);

        var arch = RuntimeInformation.OSArchitecture;
        var pre = UsmtLocator.Check(arch);
        // UsmtDirectory is only set when the whole pre-flight passes; the reasons go in UsmtProblems.
        var usmt = pre.Ok ? pre.Directory : null;

        var oemSet = false;
        try
        {
            var o = OemBrandingService.ReadCurrent();
            oemSet = !string.IsNullOrWhiteSpace(o.Manufacturer) || !string.IsNullOrWhiteSpace(o.Model)
                     || !string.IsNullOrWhiteSpace(o.SupportPhone) || !string.IsNullOrWhiteSpace(o.SupportHours)
                     || !string.IsNullOrWhiteSpace(o.SupportUrl) || !string.IsNullOrWhiteSpace(o.LogoPath);
        }
        catch { }

        return new DeploymentSnapshot(false, ElevationCheck.IsElevated(), arch, usmt, sysDrive,
            DiskSpaceCheck.ReadFreeBytes(sysDrive), det.PackagePath, det.Package, det.OtherPackages,
            det.OemHookPresent, det.AutoApplyPresent, oemSet, WinReState.NotChecked,
            det.Manifest, det.ManifestNote, CurrentOs.Read().Build, pre.Problems);
    }

    private static DeploymentSnapshot ReadPreview(string mode)
    {
        const string pkgPath = @"C:\Recovery\Customizations\usmt.ppkg";
        var existing = mode == "existing";
        var pkg = existing
            ? new PackageInfo(pkgPath, (long)(1.8 * 1024 * 1024 * 1024), DateTime.Now.AddDays(-12))
            : null;
        var free = mode == "lowdisk" ? 3L * 1024 * 1024 * 1024 : 42L * 1024 * 1024 * 1024;

        // Synthetic manifest captured on an older feature update, so the stale-capture row can be reviewed.
        var manifest = existing
            ? new DeploymentManifest(DeploymentManifest.CurrentSchema, DateTime.UtcNow.AddDays(-12), "usmt.ppkg",
                pkg!.SizeBytes, "22631.4317", "23H2", "Windows 11 Pro", "AppsAndSettings", "Config_AppsAndSettings.xml",
                true, new[] { "Temp folders", "Browser caches" }, Array.Empty<string>(), "10.0.28000.1", "amd64",
                "0.0.0", false)
            : null;

        return new DeploymentSnapshot(true, true, Architecture.X64, @"(preview)\vendor\usmt\amd64", "C:", free,
            pkgPath, pkg, existing ? new[] { "vendor-tools.ppkg" } : Array.Empty<string>(),
            existing, false, existing, WinReState.NotChecked, manifest, null, "26100.4061", Array.Empty<string>());
    }
}
