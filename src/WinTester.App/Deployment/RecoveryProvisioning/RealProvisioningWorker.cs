using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// The real worker behind the wizard's Progress step (Phase 3): Capture runs ScanState, Backup zips the package,
// Revert removes it. Every machine-touching step is undone if the run fails or is stopped, so the previous package
// is never lost. The simulated worker is only used for WINTESTER_RP_PREVIEW.
public sealed class RealProvisioningWorker : IProvisioningWorker
{
    public bool IsSimulation => false;

    // Runs on a worker thread so folder preparation, icacls and file copies never block the wizard window. The
    // IProgress<T> passed in by the window still delivers its updates on the UI thread.
    public Task<WorkerResult> RunAsync(WizardContext context, IProgress<WorkerUpdate> progress, CancellationToken ct) =>
        Task.Run(() => RunCoreAsync(context, progress, ct));

    private static async Task<WorkerResult> RunCoreAsync(WizardContext context, IProgress<WorkerUpdate> progress, CancellationToken ct)
    {
        var job = new Job(context, progress);
        try
        {
            return context.Operation switch
            {
                WizardOperation.Backup => await job.BackupAsync(ct),
                WizardOperation.Revert => job.Revert(),
                WizardOperation.Restore => await job.RestoreAsync(ct),
                _ => await job.CaptureAsync(ct)
            };
        }
        catch (OperationCanceledException)
        {
            job.Log("Stopped by the operator.");
            return new WorkerResult(false, true, "Stopped before finishing.", false, job.LogFolder);
        }
    }

    private sealed class Job
    {
        private const int MaxUiOutputLines = 1500;

        private readonly WizardContext _ctx;
        private readonly IProgress<WorkerUpdate> _progress;
        private readonly List<string> _transcript = new();
        private readonly object _gate = new();
        private int _outputLines;

        public string? LogFolder { get; private set; }

        public Job(WizardContext ctx, IProgress<WorkerUpdate> progress)
        {
            _ctx = ctx;
            _progress = progress;
        }

        // ---- reporting

        public void Log(string line)
        {
            lock (_gate) _transcript.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            _progress.Report(new WorkerUpdate(line, null, null));
        }

        private void Step(int percent, string status, string? line = null)
        {
            if (line is not null) lock (_gate) _transcript.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            _progress.Report(new WorkerUpdate(line, Math.Clamp(percent, 0, 99), status));
        }

        private WorkerResult Fail(string message)
        {
            Log("FAILED: " + message);
            return new WorkerResult(false, false, message, false, LogFolder);
        }

        // ---- Backup

        public async Task<WorkerResult> BackupAsync(CancellationToken ct)
        {
            var s = _ctx.Snapshot;
            if (string.IsNullOrEmpty(_ctx.BackupPath)) return Fail("No backup file was chosen.");

            Step(2, "Backing up...", "Backing up the current package");
            var r = await PackageActions.CreateBackupAsync(_ctx.BackupPath, s.PackagePath,
                RecoveryLogs.FindLatestWithLog(), Log, p => Step(2 + p * 97 / 100, "Backing up..."), ct);

            if (!r.Success) return Fail(r.Message);
            Log(r.Message);
            return new WorkerResult(true, false, $"Backup written to {_ctx.BackupPath} ({RecoveryFormat.Bytes(r.PackageBytes)} package).");
        }

        // ---- Restore (5.1)

        public async Task<WorkerResult> RestoreAsync(CancellationToken ct)
        {
            var s = _ctx.Snapshot;
            if (string.IsNullOrEmpty(_ctx.RestorePath)) return Fail("No backup file was chosen.");

            // Checked before anything on this PC is touched, so a bad zip never creates the Recovery folder.
            Step(2, "Restoring...", "Checking the backup " + Path.GetFileName(_ctx.RestorePath));
            var info = PackageActions.InspectBackup(_ctx.RestorePath);
            if (!info.Valid) return Fail(info.Problem);

            var staging = RecoveryStaging.Prepare(DeploymentDetector.RecoveryDir(s.SystemDrive));
            foreach (var n in staging.Notes) Log("Note: " + n);
            if (staging.CreatedRecovery) Log("Created the Recovery folder and restricted it to SYSTEM and Administrators.");

            Step(5, "Restoring...", "Restoring the package");
            var r = await PackageActions.RestoreFromBackupAsync(_ctx.RestorePath, staging.PackagePath,
                DiskSpaceCheck.ReadFreeBytes(s.SystemDrive), Log, p => Step(5 + p * 94 / 100, "Restoring..."), ct);

            if (!r.Success) return Fail(r.Message);
            Log(r.Message);
            foreach (var n in r.Notes) Log("Note: " + n);
            return new WorkerResult(true, false,
                r.Message + " Reset this PC will now use it." + (r.Notes.Count > 0 ? " See the log for notes." : ""), r.Notes.Count > 0);
        }

        // ---- Revert

        public WorkerResult Revert()
        {
            Step(20, "Reverting...", "Removing the custom recovery package");
            var r = PackageActions.Revert(_ctx.Snapshot.PackagePath);
            if (!r.Success) return Fail(r.Message);

            Log(r.Message);
            if (r.OtherPackages.Count > 0)
            {
                var names = string.Join(", ", r.OtherPackages);
                Log("Other packages are still in the Customizations folder and were not touched: " + names);
                return new WorkerResult(true, false,
                    "Removed usmt.ppkg. These other packages are still there and will still be applied by Reset this PC: " + names,
                    true);
            }
            return new WorkerResult(true, false, "Reverted. Reset this PC now restores stock Windows.");
        }

        // ---- Capture

        private sealed class CaptureState
        {
            public string PackagePath = "";
            public string? UsmtDir;
            public string? ArchFolder;
            public RunWorkspace? Workspace;
            public bool StagedAside;        // a previous package/manifest was renamed to *.prev
            public bool Finished;           // the new package was verified and kept
            public ScanStateRunResult? Run;
            public List<string> DesktopCopied = new();   // shortcuts now in the Public Desktop folder
            public bool DesktopWarn;                      // some shortcut could not be copied
            public List<CustomerFileEntry> CustomerEntries = new();   // 5.5: what was copied for the customer files
            public bool CustomerWarn;                     // 5.5: copied, but something was skipped (links) or already there
            public List<string> CustomerExtra = new();    // 5.5: include patterns for "keep both" copies of single files
        }

        public async Task<WorkerResult> CaptureAsync(CancellationToken ct)
        {
            var s = _ctx.Snapshot;
            var o = _ctx.Options;
            var st = new CaptureState { PackagePath = s.PackagePath };

            Step(1, "Checking...", "Checking prerequisites");
            var problem = CheckPrerequisites(st, s, o);
            if (problem is not null) return Fail(problem);

            // Optional zip of the current package before it is replaced. If this fails nothing has been changed yet.
            if (!string.IsNullOrEmpty(_ctx.BackupFirstPath) && File.Exists(st.PackagePath))
            {
                Step(3, "Backing up...", "Backing up the current package first");
                var b = await PackageActions.CreateBackupAsync(_ctx.BackupFirstPath, st.PackagePath,
                    RecoveryLogs.FindLatestWithLog(), Log, p => Step(3 + p * 12 / 100, "Backing up..."), ct);
                if (!b.Success) return Fail(b.Message + " The existing package was not changed.");
                Log(b.Message);
            }

            WorkerResult result;
            try
            {
                result = await ExecuteAsync(st, s, o, ct);
            }
            catch (OperationCanceledException)
            {
                Cleanup(st);
                throw;
            }
            catch (Exception ex)
            {
                Log("Unexpected error: " + ex.Message);
                result = new WorkerResult(false, false, "Unexpected error: " + ex.Message);
            }

            Cleanup(st);
            return result with { LogFolder = LogFolder };
        }

        private string? CheckPrerequisites(CaptureState st, DeploymentSnapshot s, CaptureOptions o)
        {
            if (!ElevationCheck.IsElevated())
                return "WinTester is not running as administrator, so it cannot write to the recovery folder.";
            if (!s.ArchitectureSupported)
                return $"No USMT is bundled for {s.OsArchitecture} Windows (only x64 and x86).";

            var pre = UsmtLocator.Check(s.OsArchitecture, o.ConfigFileName);
            if (!pre.Ok) return UsmtLocator.Describe(pre);
            st.UsmtDir = pre.Directory;
            st.ArchFolder = pre.ArchFolder;

            var space = DiskSpaceCheck.Check(s.SystemDrive);
            if (!space.Enough)
                return $"Only {RecoveryFormat.Bytes(space.FreeBytes)} is free on {s.SystemDrive}; at least " +
                       $"{RecoveryFormat.Bytes(DiskSpaceCheck.MinFreeBytes)} is needed.";

            if (o.CustomerFiles.Count > 0)
            {
                var problems = CustomerFileSet.ValidateAll(o.CustomerFiles, s.SystemDrive, File.Exists, Directory.Exists,
                    o.ActiveApps.Select(a => a.Folder));
                if (problems.Count > 0)
                    return "Customer files: " + string.Join(" ", problems.Take(3)) + (problems.Count > 3 ? $" (and {problems.Count - 3} more)" : "");
                var need = CustomerFileSet.RequiredFreeBytes(o.CustomerFiles);
                if (space.FreeBytes < need)
                    return $"Only {RecoveryFormat.Bytes(space.FreeBytes)} is free on {s.SystemDrive}; the customer files need " +
                           $"{RecoveryFormat.Bytes(need)} (the usual minimum plus their size twice: the copy that stays and the package).";
            }

            return RecoveryStaging.CheckLeftover(st.PackagePath);
        }

        private async Task<WorkerResult> ExecuteAsync(CaptureState st, DeploymentSnapshot s, CaptureOptions o, CancellationToken ct)
        {
            var recoveryDir = DeploymentDetector.RecoveryDir(s.SystemDrive);

            // 5.5: customer files are copied before anything in the Recovery folder changes; a failed copy stops the capture here.
            if (o.CustomerFiles.Count > 0)
            {
                var failure = CopyCustomerFiles(st, o, ct);
                if (failure is not null) return Fail(failure);
            }

            // Done first: if the Public Desktop folder cannot be used nothing in the Recovery folder has changed yet.
            // The copies are left in place if the capture later fails or is stopped (they are ordinary shortcuts).
            if (o.CopyDesktopShortcuts)
            {
                var failure = CopyDesktopShortcuts(st, s.SystemDrive);
                if (failure is not null) return Fail(failure);
            }

            Step(16, "Preparing...", "Preparing the recovery folder");
            var staging = RecoveryStaging.Prepare(recoveryDir);
            foreach (var n in staging.Notes) Log("Note: " + n);
            if (staging.CreatedRecovery) Log("Created the Recovery folder and restricted it to SYSTEM and Administrators.");

            // Keep the current package aside until the new one is verified (resolves the tracker's OPEN overwrite item).
            if (File.Exists(st.PackagePath) || File.Exists(DeploymentManifest.PathFor(st.PackagePath)))
            {
                st.StagedAside = true;
                RecoveryStaging.MoveCurrentAside(st.PackagePath);
                Log("The current package is kept as usmt.ppkg.prev until the new one is verified.");
            }

            Step(17, "Preparing...", "Writing the rules file");
            st.Workspace = RunWorkspace.Create(s.SystemDrive);
            var ws = st.Workspace;
            File.Copy(Path.Combine(st.UsmtDir!, o.ConfigFileName), ws.ConfigFile, true);

            var oemRule = RulesXmlBuilder.OemRuleApplies(o, s.OemInfoSet, s.OemHookPresent);
            var rules = RulesXmlBuilder.Build(o, s.SystemDrive, Directory.Exists, oemRule, st.CustomerExtra);
            RulesXmlBuilder.WriteToFile(rules.Document, ws.RulesFile);
            Log($"Rules: {rules.Patterns.Count} exclusion patterns");
            if (oemRule)
                Log("OEM branding: the package also carries the OEMInformation key and the Oobe\\Info logo folder (no OEM Branding reset hook is staged on this PC).");
            else if (o.IncludeOemInfo)
                Log(!s.OemInfoSet
                    ? "OEM branding: no OEM information is set on this PC, so there is nothing extra to carry."
                    : "OEM branding: the OEM Branding reset hook is staged and re-applies the branding itself, so the package does not carry a second copy (it could be older than the hook's).");
            var libs = GameLibraries.ExclusionPatterns(s.SystemDrive, Directory.Exists);
            Log(libs.Count == 0
                ? "Game libraries: none of the default library folders exist on this PC, so no library exclusion is written."
                : "Game library exclusions written (folders that exist here): " + libs.Count);
            if (o.ActiveApps.Count > 0)
            {
                Log($"Apps included: {o.ActiveApps.Count} ({rules.IncludePatterns.Count - rules.OemPatterns.Count - rules.CustomerPatterns.Count} include patterns)");
                foreach (var a in o.ActiveApps)
                {
                    Log($"  {a.Name} {a.Version}  {a.Folder}  [{a.FilesFor(o).Count} file, {a.RegistryFor(o).Count} registry rules]");
                    if (a.IsCustom) Log("    Added by folder (no Uninstall entry: runs after a reset, but is not listed in Settings > Apps)");
                    if (a.ProfileNames.Count > 0) Log("    App profile: " + string.Join(", ", a.ProfileNames));
                    foreach (var x in a.ExcludedItems) Log("    Excluded from the app folder: " + x);
                    foreach (var x in a.ExtraFolders) Log("    Also carried (app profile): " + x);
                    foreach (var x in a.SuggestedItems) Log("    Also carried (suggestion ticked): " + x);
                    foreach (var x in a.SweepItemsFor(o)) Log("    Also carried (wider search): " + x);
                    if (o.IncludeSweep) foreach (var x in a.SweepNotes) Log("    Wider search note: " + x);
                    foreach (var item in a.ServiceItemsFor(o).Concat(a.StartupItemsFor(o)).Concat(a.ShellItems).Concat(a.MsiItems).Concat(a.ComItems)) Log("    " + item);
                }
                Log("Wider search (registrations found by path): " + (o.IncludeSweep ? "included" + o.SweepInfo : "not included") + ".");
                Log("Services (safe subset): " + (o.IncludeServices ? "included" : "not included")
                    + ". Starts with Windows (all-users Run entries, boot/logon tasks): " + (o.IncludeStartup ? "included" : "not included") + ".");
            }
            else Log("Apps included: none (this package carries Windows settings and system content only)");
            if (o.CopyDesktopShortcuts) Log("Public Desktop folder included: " + DesktopShortcuts.PublicDesktop(s.SystemDrive));
            Log(o.IncludeDrivers
                ? "Third-party drivers: included (/drivers). Windows' own drivers are not carried."
                : "Third-party drivers: not included.");
            foreach (var w in RulesXmlBuilder.ValidateAgainstXsd(rules.Document, Path.Combine(st.UsmtDir!, "MigXML.xsd")))
                Log("Schema note (advisory): " + w);

            ScanStateCommand cmd;
            try { cmd = ScanStateArgs.Build(o, st.UsmtDir!, ws, st.PackagePath); }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }

            Log("Command: " + cmd.CommandLineText);
            Step(18, "Capturing...", $"Starting ScanState with {o.ConfigFileName}. This can take several minutes.");

            var run = await ScanStateRunner.RunAsync(cmd, OnOutputLine, OnPercent, ct);
            st.Run = run;

            Step(92, "Verifying...", $"ScanState finished after {(int)run.Elapsed.TotalMinutes}:{run.Elapsed.Seconds:00} with exit code {run.ExitCode}.");
            var analysis = ScanStateLogAnalyzer.Analyze(ws.LogFile, run.OutputLines);
            long bytes = -1;
            try { if (File.Exists(st.PackagePath)) bytes = new FileInfo(st.PackagePath).Length; } catch { }

            var outcome = CaptureVerifier.Verify(run.ExitCode, run.Cancelled, bytes, analysis);
            foreach (var d in outcome.Details) Log(d);

            // Include rules ScanState dropped because their root does not exist: the capture then lacks that part of an app.
            var droppedOurs = ScanStateLogAnalyzer.OursSkipped(analysis.SkippedIncludeRoots, rules.IncludePatterns);
            foreach (var d in droppedOurs) Log("Include rule matched nothing: " + d);

            switch (outcome.Kind)
            {
                case CaptureOutcomeKind.Cancelled:
                    Log(outcome.Summary);
                    return new WorkerResult(false, true, outcome.Summary);

                case CaptureOutcomeKind.Failed:
                    return Fail(outcome.Summary);

                default:
                    var warn = outcome.Kind == CaptureOutcomeKind.SuccessWithWarnings || droppedOurs.Count > 0 || st.DesktopWarn || st.CustomerWarn;
                    try
                    {
                        var manifest = DeploymentManifest.Create(o, st.UsmtDir!, st.ArchFolder ?? "", st.PackagePath, warn, st.DesktopCopied, st.CustomerEntries);
                        DeploymentManifest.Write(st.PackagePath, manifest);
                        Log("Manifest written: " + DeploymentManifest.PathFor(st.PackagePath));
                    }
                    catch (Exception ex)
                    {
                        Log("The package is in place but the manifest could not be written: " + ex.Message);
                        warn = true;
                    }

                    st.Finished = true;
                    Log(outcome.Summary);
                    var extra = (droppedOurs.Count > 0
                        ? $" {droppedOurs.Count} include rule(s) matched nothing; see the log for which."
                        : "") +
                        (st.DesktopWarn ? " Some desktop shortcuts could not be copied; see the log." : "");
                    return new WorkerResult(true, false,
                        outcome.Summary + extra + " It is used the next time Reset this PC runs.", warn);
            }
        }

        // Returns an error text when the step cannot go ahead, otherwise null (individual file failures only warn).
        // 5.5. Null on success, otherwise the reason. The copies are left in place on success and failure (like the shortcuts).
        private string? CopyCustomerFiles(CaptureState st, CaptureOptions o, CancellationToken ct)
        {
            Step(14, "Copying customer files...", $"Copying {o.CustomerFiles.Count} customer item(s) to their destinations");
            Log("Files that already exist at a destination and differ: " + CustomerFileSet.PolicyText(o.CustomerPolicy));
            var r = CustomerFileSet.Copy(o.CustomerFiles, Log, ct, o.CustomerPolicy, o.CustomerChoices);
            st.CustomerEntries = r.Entries.ToList();
            st.CustomerExtra = r.ExtraPatterns.ToList();
            foreach (var n in r.Notes) Log("Note: " + n);
            if (r.Notes.Count > 0) st.CustomerWarn = true;
            if (!r.Success) return r.Message;
            Log(r.Message + " The copies stay at their destinations on this PC.");
            return null;
        }

        private string? CopyDesktopShortcuts(CaptureState st, string systemDrive)
        {
            var source = DesktopShortcuts.UserDesktop();
            var target = DesktopShortcuts.PublicDesktop(systemDrive);
            Step(15, "Copying shortcuts...", $"Copying desktop shortcuts from {source} to {target}");

            var lnk = new LnkResolver();
            var r = DesktopShortcuts.Copy(source, target, lnk.Resolve);
            if (r.FolderError is not null) return r.FolderError;

            st.DesktopCopied = r.Copied.ToList();
            if (r.Copied.Count == 0 && r.Failed.Count == 0 && r.SkippedPerUser.Count == 0)
                Log("No shortcuts (.lnk or .url) were found on that desktop. The Public Desktop folder is still added to the package.");
            else
                Log($"Copied {r.Copied.Count} shortcut(s) to the Public Desktop (existing files with the same name were overwritten)" +
                    (r.SkippedPerUser.Count > 0 ? $"; skipped {r.SkippedPerUser.Count} that point into a user profile." : "."));
            foreach (var n in r.Copied) Log("  " + n);
            foreach (var f in r.Failed) Log("  NOT copied: " + f);
            foreach (var n in r.SkippedPerUser)
                Log($"  Skipped: {n} (its target is inside a user profile folder, so it would not work for a different user)");

            if (r.Failed.Count > 0) st.DesktopWarn = true;
            return null;
        }

        private void OnOutputLine(string line)
        {
            var n = Interlocked.Increment(ref _outputLines);
            if (n < MaxUiOutputLines) Log(line);
            else if (n == MaxUiOutputLines) Log("(further ScanState output is saved in output.txt in the log folder)");
        }

        private void OnPercent(int pct) => Step(18 + pct * 72 / 100, $"Capturing... {pct}%");

        // Runs once, whatever happened: undo a failed overwrite, keep the logs, delete the work folder.
        private void Cleanup(CaptureState st)
        {
            try
            {
                if (st.Finished)
                {
                    RecoveryStaging.CommitAside(st.PackagePath);
                }
                else if (st.StagedAside || st.Run is not null || st.Workspace is not null)
                {
                    RecoveryStaging.RestoreAside(st.PackagePath);
                    Log(st.StagedAside ? "The previous package was put back." : "Partial output was removed.");
                }
            }
            catch (Exception ex)
            {
                Log("Cleanup problem: " + ex.Message + " Check usmt.ppkg and usmt.ppkg.prev in the Customizations folder.");
            }

            LogFolder = PersistLogs(st);
            st.Workspace?.Delete();
        }

        private string? PersistLogs(CaptureState st)
        {
            try
            {
                var dir = RecoveryLogs.CreateRunFolder();
                var ws = st.Workspace;
                if (ws is not null)
                {
                    CopyIfExists(ws.LogFile, Path.Combine(dir, "scanstate.log"));
                    CopyIfExists(ws.ProgressFile, Path.Combine(dir, "progress.log"));
                    CopyIfExists(ws.RulesFile, Path.Combine(dir, "rules.xml"));
                }
                if (st.Run is not null) File.WriteAllLines(Path.Combine(dir, "output.txt"), st.Run.OutputLines);
                lock (_gate) File.WriteAllLines(Path.Combine(dir, "wizard.log"), _transcript);
                return dir;
            }
            catch { return null; }
        }

        private static void CopyIfExists(string from, string to)
        {
            try { if (File.Exists(from)) File.Copy(from, to, true); } catch { }
        }
    }
}
