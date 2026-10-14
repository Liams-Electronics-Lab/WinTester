using System.IO;
using System.IO.Compression;

namespace WinTester.App.Deployment.RecoveryProvisioning;

public sealed record BackupResult(bool Success, string Message, long PackageBytes);

// 5.1: what a backup zip holds, read without extracting anything. Manifest is null when the zip has none or it cannot be used
// (ManifestNote says why).
public sealed record BackupInfo(bool Valid, string Problem, long PackageBytes, DeploymentManifest? Manifest, string? ManifestNote, bool HasLog);

public sealed record RestoreResult(bool Success, string Message, long PackageBytes, bool ManifestRestored, IReadOnlyList<string> Notes);

// Backup and Revert on the existing package (task 3.5). File operations only; ScanState is not involved.
public static class PackageActions
{
    private const int CopyBufferBytes = 1024 * 1024;
    public const string PackageEntryName = "usmt.ppkg";
    public const string ManifestEntryName = "manifest.json";
    public const string LogEntryName = "scanstate.log";

    // Zip = usmt.ppkg + manifest.json + the newest ScanState log (Decision Log: backup contents). Written to a
    // .partial name and renamed on success, so a stopped or failed backup never leaves a half zip under the real name.
    // percent receives 0..100 for the package copy (the bulk of the time).
    public static async Task<BackupResult> CreateBackupAsync(string zipPath, string packagePath, string? logFolder,
        Action<string> log, Action<int> percent, CancellationToken ct)
    {
        if (!File.Exists(packagePath))
            return new BackupResult(false, "There is no package to back up.", 0);

        var partial = zipPath + ".partial";
        long packageBytes;
        try
        {
            packageBytes = new FileInfo(packagePath).Length;
            var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(partial)) File.Delete(partial);

            log($"Adding {PackageEntryName} ({RecoveryFormat.Bytes(packageBytes)}) to the backup");
            using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = zip.CreateEntry(PackageEntryName, CompressionLevel.Fastest);
                using (var entryStream = entry.Open())
                using (var src = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[CopyBufferBytes];
                    long done = 0;
                    var lastPct = -1;
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await entryStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        var pct = packageBytes == 0 ? 100 : (int)(done * 100 / packageBytes);
                        if (pct != lastPct) { lastPct = pct; percent(pct); }
                    }
                }

                var manifestPath = DeploymentManifest.PathFor(packagePath);
                if (File.Exists(manifestPath))
                {
                    log("Adding the manifest");
                    zip.CreateEntryFromFile(manifestPath, ManifestEntryName, CompressionLevel.Optimal);
                }
                else log("No manifest next to the package; the backup will not include one.");

                var scanLog = logFolder is null ? null : Path.Combine(logFolder, "scanstate.log");
                if (scanLog is not null && File.Exists(scanLog))
                {
                    log("Adding the last ScanState log");
                    zip.CreateEntryFromFile(scanLog, LogEntryName, CompressionLevel.Fastest);
                }
            }

            // Re-open and compare sizes before trusting the file.
            using (var check = ZipFile.OpenRead(partial))
            {
                var e = check.GetEntry(PackageEntryName);
                if (e is null || e.Length != packageBytes)
                    throw new IOException("The backup zip did not verify (package size mismatch).");
            }

            File.Move(partial, zipPath, overwrite: true);
            return new BackupResult(true, $"Backup written: {zipPath}", packageBytes);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            return new BackupResult(false, "Backup failed: " + ex.Message, 0);
        }
    }

    // ---- Restore (5.1): the other half of CreateBackupAsync. The zip is only ever read by the three fixed entry names, so nothing
    // in it can choose where a file is written. Extracts to <name>.restoring first, checks the size, and only then swaps it in;
    // the package it replaces is kept as .prev (RecoveryStaging) until the swap has worked and is put back if anything fails.

    public const string RestoringSuffix = ".restoring";
    public const long RestoreHeadroomBytes = 1024L * 1024 * 1024;     // free space wanted on top of the package itself
    private const long MaxManifestBytes = 4 * 1024 * 1024;

    private static ZipArchiveEntry? Find(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static BackupInfo BadBackup(string problem) => new(false, problem, 0, null, null, false);

    public static BackupInfo InspectBackup(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath)) return BadBackup("The file does not exist.");
            using var zip = ZipFile.OpenRead(zipPath);
            var pkg = Find(zip, PackageEntryName);
            if (pkg is null) return BadBackup("This zip is not a package backup: it has no " + PackageEntryName + " inside.");
            if (pkg.Length == 0) return BadBackup(PackageEntryName + " in this zip is empty.");

            DeploymentManifest? manifest = null;
            string? note = null;
            var me = Find(zip, ManifestEntryName);
            if (me is null) note = "The backup has no manifest, so the capture details are not known.";
            else if (me.Length > MaxManifestBytes) note = "The manifest in the backup is too large to be real.";
            else
            {
                using var reader = new StreamReader(me.Open());
                manifest = DeploymentManifest.TryParse(reader.ReadToEnd(), out note);
            }
            return new BackupInfo(true, "", pkg.Length, manifest, note, Find(zip, LogEntryName) is not null);
        }
        catch (InvalidDataException) { return BadBackup("This file is not a valid zip archive."); }
        catch (Exception ex) { return BadBackup("The backup could not be read: " + ex.Message); }
    }

    // 5.2: text for the "restore anyway?" question when the backup was captured on a different Windows feature build than this PC
    // (same rule as the Detect step's "Capture may be stale" row: DeploymentManifest.IsStaleFor, so monthly updates do not count).
    // Null when the builds match, when either build is unknown, or when the backup has no usable manifest: nothing is known, so
    // there is nothing to warn about (the restore confirmation already says the capture details are unknown).
    public static string? StaleRestoreWarning(BackupInfo info, string currentOsBuild)
    {
        if (!info.Valid || info.Manifest is not { } m || !m.IsStaleFor(currentOsBuild)) return null;
        var captured = m.OsBuild + (string.IsNullOrEmpty(m.OsDisplayVersion) ? "" : $" ({m.OsDisplayVersion})");
        return $"This backup was captured on Windows build {captured}, and this PC is on build {currentOsBuild}.\n\n" +
               "A package from a different Windows version is likely out of date: its apps and settings were captured on that version " +
               "and may not match this PC, and Reset this PC may not give the result you expect.\n\n" +
               "Restore it anyway?";
    }

    // freeBytes: free space on the system drive (a parameter so the harness can test the refusal). The package being replaced stays
    // on disk until the swap, so the whole package size is needed, plus headroom.
    public static async Task<RestoreResult> RestoreFromBackupAsync(string zipPath, string packagePath, long freeBytes,
        Action<string> log, Action<int> percent, CancellationToken ct)
    {
        static RestoreResult Failed(string m) => new(false, m, 0, false, Array.Empty<string>());

        var info = InspectBackup(zipPath);
        if (!info.Valid) return Failed(info.Problem);
        var leftover = RecoveryStaging.CheckLeftover(packagePath);
        if (leftover is not null) return Failed(leftover);
        if (freeBytes < info.PackageBytes + RestoreHeadroomBytes)
            return Failed($"Not enough free space: the package is {RecoveryFormat.Bytes(info.PackageBytes)} and needs " +
                          $"{RecoveryFormat.Bytes(RestoreHeadroomBytes)} of headroom, {RecoveryFormat.Bytes(freeBytes)} is free. Nothing was changed.");

        var notes = new List<string>();
        var manifestPath = DeploymentManifest.PathFor(packagePath);
        var partialPackage = packagePath + RestoringSuffix;
        var partialManifest = manifestPath + RestoringSuffix;
        var manifestOk = false;
        var movedAside = false;
        var placed = false;
        try
        {
            TryDelete(partialPackage);
            TryDelete(partialManifest);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var pe = Find(zip, PackageEntryName) ?? throw new IOException("The backup changed while it was being read.");
                log($"Reading {PackageEntryName} ({RecoveryFormat.Bytes(info.PackageBytes)}) from the backup");
                using (var src = pe.Open())
                using (var dst = new FileStream(partialPackage, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[CopyBufferBytes];
                    long done = 0;
                    var lastPct = -1;
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        var pct = (int)(done * 100 / info.PackageBytes);
                        if (pct != lastPct) { lastPct = pct; percent(Math.Min(pct, 100)); }
                    }
                }

                var me = Find(zip, ManifestEntryName);
                if (me is not null && info.Manifest is not null)
                {
                    using var msrc = me.Open();
                    using var mdst = new FileStream(partialManifest, FileMode.Create, FileAccess.Write, FileShare.None);
                    await msrc.CopyToAsync(mdst, ct);
                    manifestOk = true;
                }
                else notes.Add((info.ManifestNote ?? "The backup has no usable manifest.") + " No manifest was restored; the Detect step will not know what the package contains.");
            }

            if (new FileInfo(partialPackage).Length != info.PackageBytes)
                throw new IOException("The restored package did not verify (size mismatch).");
            if (info.Manifest is { } mf && mf.PackageSizeBytes > 0 && mf.PackageSizeBytes != info.PackageBytes)
                notes.Add($"The manifest records a {RecoveryFormat.Bytes(mf.PackageSizeBytes)} package but the zip holds {RecoveryFormat.Bytes(info.PackageBytes)}; the zip content was used.");
            ct.ThrowIfCancellationRequested();

            // Nothing below can be cancelled: the swap is quick and is either completed or undone.
            if (File.Exists(packagePath) || File.Exists(manifestPath))
            {
                RecoveryStaging.MoveCurrentAside(packagePath);
                movedAside = true;
                log("The current package is kept as usmt.ppkg.prev until the restored one is in place.");
            }
            File.Move(partialPackage, packagePath);
            placed = true;
            if (manifestOk) File.Move(partialManifest, manifestPath);
            if (movedAside) RecoveryStaging.CommitAside(packagePath);
            return new RestoreResult(true, $"Restored the package from {Path.GetFileName(zipPath)} ({RecoveryFormat.Bytes(info.PackageBytes)}).",
                info.PackageBytes, manifestOk, notes);
        }
        catch (OperationCanceledException)
        {
            Undo();
            throw;
        }
        catch (Exception ex)
        {
            Undo();
            return Failed("Restore failed: " + ex.Message + (movedAside ? " The previous package was put back." : " Nothing was changed."));
        }
        finally
        {
            TryDelete(partialPackage);
            TryDelete(partialManifest);
        }

        void Undo()
        {
            try
            {
                if (movedAside) RecoveryStaging.RestoreAside(packagePath);
                else if (placed) RecoveryStaging.DiscardPartial(packagePath);
            }
            catch { /* the .prev file stays and CheckLeftover reports it on the next run */ }
        }
    }

    public sealed record RevertResult(bool Success, string Message, IReadOnlyList<string> OtherPackages);

    // Deletes usmt.ppkg and its manifest only. Other .ppkg/.spp files in Customizations are listed, never removed.
    public static RevertResult Revert(string packagePath)
    {
        try
        {
            var existed = File.Exists(packagePath);
            if (existed) File.Delete(packagePath);
            var manifest = DeploymentManifest.PathFor(packagePath);
            if (File.Exists(manifest)) File.Delete(manifest);

            var others = new List<string>();
            var dir = Path.GetDirectoryName(packagePath);
            if (dir is not null && Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f);
                    if ((ext.Equals(".ppkg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".spp", StringComparison.OrdinalIgnoreCase))
                        && !string.Equals(f, packagePath, StringComparison.OrdinalIgnoreCase))
                        others.Add(Path.GetFileName(f));
                }

            return new RevertResult(true, existed ? "Removed usmt.ppkg." : "usmt.ppkg was already gone.", others);
        }
        catch (Exception ex)
        {
            return new RevertResult(false, "Could not remove the package: " + ex.Message, Array.Empty<string>());
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
