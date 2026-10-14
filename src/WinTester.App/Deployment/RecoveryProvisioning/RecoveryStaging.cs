using System.Diagnostics;
using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Short per-run work folder for ScanState's helper files (task 3.3). It sits at <drive>\WTRP\<4 hex> on purpose:
// USMT rejects command lines over 256 characters, so the paths must stay short. Rules.xml is written here and the
// whole folder is deleted in a finally block. It is also in the always-on exclusion list of the rules file.
public sealed class RunWorkspace
{
    public const string RootName = "WTRP";

    public string Folder { get; }
    public string ConfigFile => Path.Combine(Folder, "c.xml");
    public string RulesFile => Path.Combine(Folder, "r.xml");
    public string LogFile => Path.Combine(Folder, "s.log");
    public string ProgressFile => Path.Combine(Folder, "p.log");

    private RunWorkspace(string folder) => Folder = folder;

    // parentRoot: "C:" in production; a temp folder in the scratch harness.
    public static RunWorkspace Create(string parentRoot)
    {
        var root = parentRoot.EndsWith(':') ? parentRoot + Path.DirectorySeparatorChar : parentRoot;
        var folder = Path.Combine(root, RootName, Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(folder);
        return new RunWorkspace(folder);
    }

    public void Delete()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            var root = Path.GetDirectoryName(Folder);
            // Remove the shared WTRP folder too when nothing else is in it.
            if (root is not null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch { /* leftovers are harmless and excluded from the capture */ }
    }
}

public sealed record StagingResult(string CustomizationsDir, string PackagePath, bool CreatedRecovery, IReadOnlyList<string> Notes);

// Folder preparation and the overwrite safety net around usmt.ppkg (tasks 3.3 and 3.5).
public static class RecoveryStaging
{
    public const string AsideSuffix = ".prev";

    // C6: Customizations is created and hidden. The lockdown (SYSTEM + Administrators only, hidden) only runs on a
    // Recovery folder this call created; an existing Recovery folder is never re-ACL'd.
    // applyLockdown exists so the scratch harness can skip the icacls calls.
    public static StagingResult Prepare(string recoveryDir, bool applyLockdown = true)
    {
        var notes = new List<string>();
        var created = false;

        if (!Directory.Exists(recoveryDir))
        {
            Directory.CreateDirectory(recoveryDir);
            created = true;
            if (applyLockdown) Lockdown(recoveryDir, notes);
            Hide(recoveryDir, notes);
        }

        var cust = Path.Combine(recoveryDir, "Customizations");
        Directory.CreateDirectory(cust);
        Hide(cust, notes);

        return new StagingResult(cust, Path.Combine(cust, "usmt.ppkg"), created, notes);
    }

    private static void Hide(string dir, List<string> notes)
    {
        try
        {
            var attrs = File.GetAttributes(dir);
            if ((attrs & FileAttributes.Hidden) == 0) File.SetAttributes(dir, attrs | FileAttributes.Hidden);
        }
        catch (Exception ex) { notes.Add($"Could not hide {dir}: {ex.Message}"); }
    }

    // Grants first, then removes inheritance, so the account running this never loses access half way through.
    // SIDs instead of names keep it working on any Windows language: S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators.
    private static void Lockdown(string dir, List<string> notes)
    {
        var steps = new[]
        {
            new[] { dir, "/grant:r", "*S-1-5-18:(OI)(CI)F" },
            new[] { dir, "/grant:r", "*S-1-5-32-544:(OI)(CI)F" },
            new[] { dir, "/inheritance:r" }
        };

        foreach (var step in steps)
        {
            var code = RunTool(Path.Combine(Environment.SystemDirectory, "icacls.exe"), step, 20000);
            if (code != 0)
            {
                notes.Add($"icacls {string.Join(" ", step.Skip(1))} exited with {code}; the Recovery folder was left partly locked down.");
                return;
            }
        }
    }

    private static int RunTool(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return -2;
            }
            return p.ExitCode;
        }
        catch { return -3; }
    }

    // ---- Overwrite safety net. ScanState may not accept another output name, so instead of writing to a temp name
    // the current package and manifest are renamed to *.prev (instant, same volume), ScanState writes the new
    // package, and the rename is undone if the run fails or is stopped. Resolves the OPEN item in the tracker.

    private static string AsidePackage(string packagePath) => packagePath + AsideSuffix;
    private static string AsideManifest(string packagePath) => DeploymentManifest.PathFor(packagePath) + AsideSuffix;

    // A *.prev file left by an interrupted run. Returns a message when one is there, null otherwise.
    public static string? CheckLeftover(string packagePath)
    {
        if (File.Exists(AsidePackage(packagePath)) || File.Exists(AsideManifest(packagePath)))
            return $"A leftover {Path.GetFileName(AsidePackage(packagePath))} from an interrupted run is in " +
                   $"{Path.GetDirectoryName(packagePath)}. It holds the previous package: restore it (rename it back) " +
                   "or delete it, then run again.";
        return null;
    }

    // Returns true when a package was moved aside.
    public static bool MoveCurrentAside(string packagePath)
    {
        var moved = false;
        if (File.Exists(packagePath)) { File.Move(packagePath, AsidePackage(packagePath)); moved = true; }
        var manifest = DeploymentManifest.PathFor(packagePath);
        if (File.Exists(manifest)) File.Move(manifest, AsideManifest(packagePath));
        return moved;
    }

    // Run failed or was stopped: drop whatever was written and put the previous package back.
    public static void RestoreAside(string packagePath)
    {
        var manifest = DeploymentManifest.PathFor(packagePath);

        // A package written by the failed run is always removed; the previous one (if any) goes back.
        TryDelete(packagePath);
        if (File.Exists(AsidePackage(packagePath))) File.Move(AsidePackage(packagePath), packagePath);

        // The manifest is only written after a verified success, so a failed run has none of its own; only the
        // previous one is put back, and a current manifest with nothing to replace it is left alone.
        if (File.Exists(AsideManifest(packagePath)))
        {
            TryDelete(manifest);
            File.Move(AsideManifest(packagePath), manifest);
        }
    }

    // Run succeeded: the previous package is no longer needed.
    public static void CommitAside(string packagePath)
    {
        TryDelete(AsidePackage(packagePath));
        TryDelete(AsideManifest(packagePath));
    }

    // First-ever capture failed or was stopped: no previous package exists, just remove partial output.
    public static void DiscardPartial(string packagePath)
    {
        TryDelete(packagePath);
        TryDelete(DeploymentManifest.PathFor(packagePath));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

// Where run logs are kept: next to the app (portable convention, nothing under ProgramData/AppData), in their own
// Logs folder rather than Sessions so the session reader never sees them. Falls back to %TEMP% if not writable.
public static class RecoveryLogs
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Logs", "RecoveryProvisioning");

    public static string CreateRunFolder(string? rootOverride = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            var dir = Path.Combine(rootOverride ?? Root, stamp);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            var dir = Path.Combine(Path.GetTempPath(), "WinTester-RP-Logs", stamp);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // Newest run folder that holds a ScanState log, or null.
    public static string? FindLatestWithLog(string? rootOverride = null)
    {
        try
        {
            var root = rootOverride ?? Root;
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "scanstate.log")));
        }
        catch { return null; }
    }
}
