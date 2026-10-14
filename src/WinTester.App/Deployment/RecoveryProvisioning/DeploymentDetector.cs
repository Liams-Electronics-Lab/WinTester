using System.IO;
namespace WinTester.App.Deployment.RecoveryProvisioning;

public sealed record DetectionResult(
    string PackagePath,
    PackageInfo? Package,
    IReadOnlyList<string> OtherPackages,
    DeploymentManifest? Manifest,
    string? ManifestNote,
    bool OemHookPresent,
    bool AutoApplyPresent);

// Looks at <drive>\Recovery for an existing deployment (task 2.4). Read-only; feeds the wizard's Detect step.
public static class DeploymentDetector
{
    public static string RecoveryDir(string systemDrive) =>
        Path.Combine(systemDrive + Path.DirectorySeparatorChar, "Recovery");

    public static string CustomizationsDir(string systemDrive) =>
        Path.Combine(RecoveryDir(systemDrive), "Customizations");

    public static string PackagePathFor(string systemDrive) =>
        Path.Combine(CustomizationsDir(systemDrive), "usmt.ppkg");

    public static DetectionResult Detect(string systemDrive)
    {
        var recovery = RecoveryDir(systemDrive);
        var custDir = CustomizationsDir(systemDrive);
        var pkgPath = PackagePathFor(systemDrive);

        PackageInfo? pkg = null;
        DeploymentManifest? manifest = null;
        string? manifestNote = null;
        var others = new List<string>();

        try
        {
            if (File.Exists(pkgPath))
            {
                var fi = new FileInfo(pkgPath);
                pkg = new PackageInfo(pkgPath, fi.Length, fi.LastWriteTime);
                manifest = DeploymentManifest.TryRead(pkgPath, out manifestNote);
            }

            if (Directory.Exists(custDir))
            {
                foreach (var f in Directory.EnumerateFiles(custDir))
                {
                    var ext = Path.GetExtension(f);
                    var isPackage = ext.Equals(".ppkg", StringComparison.OrdinalIgnoreCase)
                                    || ext.Equals(".spp", StringComparison.OrdinalIgnoreCase);
                    if (isPackage && !string.Equals(f, pkgPath, StringComparison.OrdinalIgnoreCase))
                        others.Add(Path.GetFileName(f));
                }
            }
        }
        catch { /* unreadable folder: report as not found */ }

        var hook = false;
        var autoApply = false;
        try
        {
            hook = File.Exists(Path.Combine(recovery, "OEM", "ResetConfig.xml"));
            autoApply = Directory.Exists(Path.Combine(recovery, "AutoApply"));
        }
        catch { }

        return new DetectionResult(pkgPath, pkg, others, manifest, manifestNote, hook, autoApply);
    }
}
