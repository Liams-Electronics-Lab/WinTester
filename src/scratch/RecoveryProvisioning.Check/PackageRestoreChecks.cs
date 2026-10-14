using System.IO;
using System.IO.Compression;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 5.1: restore a package from a backup zip (PackageActions.InspectBackup / RestoreFromBackupAsync). File operations only.
public static class PackageRestoreChecks
{
    private const long Plenty = 100L * 1024 * 1024 * 1024;

    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try { await Body(check, root); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static BackupInfo BadStale() => new(false, "x", 0, null, null, false);
    private static byte[] Bytes(int seed, int n) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static string Dir(string root, string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
    private static string Left(string dir) => string.Join(",", Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(x => x));

    private static async Task Body(Action<bool, string> check, string root)
    {
        // A real backup, made by the real backup code, from a package with a real manifest.
        var src = Dir(root, "src");
        var pkgA = Path.Combine(src, "usmt.ppkg");
        var dataA = Bytes(1, 3 * 1024 * 1024 + 123);
        File.WriteAllBytes(pkgA, dataA);
        var opts = new CaptureOptions();
        DeploymentManifest.Write(pkgA, DeploymentManifest.Create(opts, src, "amd64", pkgA, false));
        var zipA = Path.Combine(root, "a.zip");
        var made = await PackageActions.CreateBackupAsync(zipA, pkgA, null, _ => { }, _ => { }, CancellationToken.None);
        check(made.Success, "restore: fixture, the backup zip is made");

        // ---- inspect
        var info = PackageActions.InspectBackup(zipA);
        check(info.Valid && info.PackageBytes == dataA.Length && info.Manifest is not null && info.ManifestNote is null,
            "restore: a backup is read without extracting it (size and manifest)");
        check(!PackageActions.InspectBackup(Path.Combine(root, "nope.zip")).Valid, "restore: a missing file is refused");
        var junk = Path.Combine(root, "junk.zip"); File.WriteAllBytes(junk, Bytes(2, 5000));
        check(!PackageActions.InspectBackup(junk).Valid, "restore: a file that is not a zip is refused");
        var other = Path.Combine(root, "other.zip");
        using (var z = ZipFile.Open(other, ZipArchiveMode.Create)) { using var w = new StreamWriter(z.CreateEntry("readme.txt").Open()); w.Write("hi"); }
        var oi = PackageActions.InspectBackup(other);
        check(!oi.Valid && oi.Problem.Contains("usmt.ppkg"), "restore: a zip without usmt.ppkg is refused with the reason");

        // ---- 5.2: the stale-capture question before a restore
        var mf = info.Manifest!;
        var sameFam = info with { Manifest = mf with { OsBuild = "26100.1000" } };
        var oldFam = info with { Manifest = mf with { OsBuild = "19045.5000", OsDisplayVersion = "22H2" } };
        check(PackageActions.StaleRestoreWarning(sameFam, "26100.4061") is null, "stale: same build family (monthly update only) is not asked about");
        var w1 = PackageActions.StaleRestoreWarning(oldFam, "26100.4061");
        check(w1 is not null && w1.Contains("19045.5000") && w1.Contains("22H2") && w1.Contains("26100.4061") && w1.Contains("anyway?"),
            "stale: a different build family asks, naming both builds");
        check(PackageActions.StaleRestoreWarning(oldFam, "") is null, "stale: an unknown current build is not asked about");
        check(PackageActions.StaleRestoreWarning(info with { Manifest = null }, "26100.4061") is null, "stale: a backup with no manifest is not asked about");
        check(PackageActions.StaleRestoreWarning(BadStale(), "26100.4061") is null, "stale: an invalid backup is not asked about");

        // ---- round trip into an empty folder (the usual case: the package is gone)
        var d1 = Dir(root, "d1");
        var pk1 = Path.Combine(d1, "usmt.ppkg");
        var pcts = new List<int>();
        var r1 = await PackageActions.RestoreFromBackupAsync(zipA, pk1, Plenty, _ => { }, p => pcts.Add(p), CancellationToken.None);
        check(r1.Success && r1.ManifestRestored && File.ReadAllBytes(pk1).SequenceEqual(dataA), "restore: package comes back byte for byte");
        check(DeploymentManifest.TryRead(pk1, out _) is { } m1 && m1.PackageSizeBytes == dataA.Length, "restore: the manifest comes back and reads");
        check(Left(d1) == "usmt.manifest.json,usmt.ppkg", "restore: nothing else is left in the folder (" + Left(d1) + ")");
        check(pcts.Count > 0 && pcts.Max() == 100, "restore: progress reaches 100");

        // ---- replacing an existing package
        var d2 = Dir(root, "d2");
        var pk2 = Path.Combine(d2, "usmt.ppkg");
        File.WriteAllBytes(pk2, Bytes(3, 4096));
        File.WriteAllText(DeploymentManifest.PathFor(pk2), "{\"old\":true}");
        var r2 = await PackageActions.RestoreFromBackupAsync(zipA, pk2, Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(r2.Success && File.ReadAllBytes(pk2).SequenceEqual(dataA) && Left(d2) == "usmt.manifest.json,usmt.ppkg"
              && DeploymentManifest.TryRead(pk2, out _) is not null, "restore: replaces the existing package and manifest and leaves no .prev");

        // ---- refusals leave the existing package alone
        var d3 = Dir(root, "d3");
        var pk3 = Path.Combine(d3, "usmt.ppkg");
        var keep = Bytes(4, 2048);
        File.WriteAllBytes(pk3, keep);
        var r3 = await PackageActions.RestoreFromBackupAsync(junk, pk3, Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(!r3.Success && File.ReadAllBytes(pk3).SequenceEqual(keep) && Left(d3) == "usmt.ppkg", "restore: a bad zip changes nothing");
        var r4 = await PackageActions.RestoreFromBackupAsync(zipA, pk3, dataA.Length, _ => { }, _ => { }, CancellationToken.None);
        check(!r4.Success && r4.Message.Contains("free space") && File.ReadAllBytes(pk3).SequenceEqual(keep) && Left(d3) == "usmt.ppkg",
            "restore: too little free space is refused before anything is written");
        File.WriteAllText(pk3 + ".prev", "x");
        var r5 = await PackageActions.RestoreFromBackupAsync(zipA, pk3, Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(!r5.Success && r5.Message.Contains(".prev") && File.ReadAllBytes(pk3).SequenceEqual(keep), "restore: a leftover .prev from an interrupted run is refused");
        File.Delete(pk3 + ".prev");

        // ---- cancel: the existing package stays, no partial files
        var cts = new CancellationTokenSource(); cts.Cancel();
        var threw = false;
        try { await PackageActions.RestoreFromBackupAsync(zipA, pk3, Plenty, _ => { }, _ => { }, cts.Token); } catch (OperationCanceledException) { threw = true; }
        check(threw && File.ReadAllBytes(pk3).SequenceEqual(keep) && Left(d3) == "usmt.ppkg", "restore: cancelling leaves the existing package and no partial file");

        // ---- failure after the old package was moved aside: it is put back
        var d6 = Dir(root, "d6");
        var pk6 = Path.Combine(d6, "usmt.ppkg");
        var old6 = Bytes(5, 1500);
        File.WriteAllBytes(pk6, old6);
        Directory.CreateDirectory(DeploymentManifest.PathFor(pk6));      // a folder where the manifest must go: the swap cannot finish
        var r6 = await PackageActions.RestoreFromBackupAsync(zipA, pk6, Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(!r6.Success && File.ReadAllBytes(pk6).SequenceEqual(old6) && !File.Exists(pk6 + ".prev") && !File.Exists(pk6 + ".restoring"),
            "restore: a failed swap puts the previous package back (" + r6.Message + ")");

        // ---- backups with something missing or wrong in them
        var noMan = Path.Combine(root, "noman.zip");
        using (var z = ZipFile.Open(noMan, ZipArchiveMode.Create)) { var e = z.CreateEntry("usmt.ppkg"); using var s = e.Open(); s.Write(dataA, 0, dataA.Length); }
        var d7 = Dir(root, "d7");
        var pk7 = Path.Combine(d7, "usmt.ppkg");
        File.WriteAllBytes(pk7, Bytes(6, 100));
        File.WriteAllText(DeploymentManifest.PathFor(pk7), "{\"old\":true}");
        var r7 = await PackageActions.RestoreFromBackupAsync(noMan, pk7, Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(r7.Success && !r7.ManifestRestored && r7.Notes.Count > 0 && File.ReadAllBytes(pk7).SequenceEqual(dataA) && !File.Exists(DeploymentManifest.PathFor(pk7)),
            "restore: a backup without a manifest restores the package, says so, and does not keep the old package's manifest");

        var badMan = Path.Combine(root, "badman.zip");
        using (var z = ZipFile.Open(badMan, ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("usmt.ppkg").Open()) s.Write(dataA, 0, 1000);
            using var w = new StreamWriter(z.CreateEntry("manifest.json").Open()); w.Write("not json {");
        }
        var d8 = Dir(root, "d8");
        var r8 = await PackageActions.RestoreFromBackupAsync(badMan, Path.Combine(d8, "usmt.ppkg"), Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(r8.Success && !r8.ManifestRestored && r8.Notes.Count > 0 && Left(d8) == "usmt.ppkg", "restore: an unreadable manifest in the zip is not written");

        // ---- a hostile zip: entry names are never used as paths
        var evil = Path.Combine(root, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("sub/usmt.ppkg").Open()) s.Write(new byte[] { 2 }, 0, 1);      // a decoy listed first
            using (var s = z.CreateEntry("../escaped.txt").Open()) s.Write(new byte[] { 1 }, 0, 1);
            using (var s = z.CreateEntry("usmt.ppkg").Open()) s.Write(dataA, 0, 500);
        }
        var d9 = Dir(root, "d9");
        var r9 = await PackageActions.RestoreFromBackupAsync(evil, Path.Combine(d9, "usmt.ppkg"), Plenty, _ => { }, _ => { }, CancellationToken.None);
        check(r9.Success && new FileInfo(Path.Combine(d9, "usmt.ppkg")).Length == 500 && !File.Exists(Path.Combine(root, "escaped.txt")) && Left(d9) == "usmt.ppkg",
            "restore: a zip with ../ and sub-folder entries only ever writes the two fixed files");
    }
}
