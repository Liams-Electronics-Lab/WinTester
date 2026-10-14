namespace WinTester.App.Deployment.RecoveryProvisioning;

// Phase 1 stand-in for the real worker: replays a plausible log and percentage so the Progress step can be reviewed.
// It reads nothing from and writes nothing to the machine. Replaced by the ScanState-backed worker in Phase 3.
public sealed class SimulatedProvisioningWorker : IProvisioningWorker
{
    public bool IsSimulation => true;

    public async Task<WorkerResult> RunAsync(WizardContext context, IProgress<WorkerUpdate> progress, CancellationToken ct)
    {
        try
        {
            progress.Report(new WorkerUpdate("[simulation] Nothing is being read from or written to this PC.", 0, "Starting..."));
            await Pause(ct);

            switch (context.Operation)
            {
                case WizardOperation.Backup:
                    await Steps(progress, ct, "Backing up", new (int, string)[]
                    {
                        (20, "Reading current package details"),
                        (55, "Adding usmt.ppkg to the backup zip"),
                        (80, "Adding manifest and last log"),
                        (100, $"Backup written: {context.BackupPath}")
                    });
                    return new WorkerResult(true, false, "Simulated backup finished.");

                case WizardOperation.Restore:
                    await Steps(progress, ct, "Restoring", new (int, string)[]
                    {
                        (20, "Checking the backup zip"),
                        (60, "Extracting usmt.ppkg"),
                        (85, "Restoring the manifest"),
                        (100, $"Package restored from {context.RestorePath}")
                    });
                    return new WorkerResult(true, false, "Simulated restore finished.");

                case WizardOperation.Revert:
                    await Steps(progress, ct, "Reverting", new (int, string)[]
                    {
                        (30, "Locating usmt.ppkg"),
                        (70, "Removing the custom recovery package"),
                        (100, "Reset this PC would now restore stock Windows")
                    });
                    return new WorkerResult(true, false, "Simulated revert finished.");

                default:
                    var steps = new List<(int, string)>();
                    if (!string.IsNullOrEmpty(context.BackupFirstPath))
                        steps.Add((6, $"Backing up the current package to {context.BackupFirstPath}"));
                    if (context.Options.CustomerFiles.Count > 0)
                        steps.Add((9, $"Copying {context.Options.CustomerFiles.Count} customer item(s) to their destinations"));
                    steps.AddRange(new (int, string)[]
                    {
                        (12, "Building the rules file"),
                        (18, $"Starting ScanState with {context.Options.ConfigFileName}"),
                        (30, "Scanning installed applications"),
                        (45, "Scanning system-wide settings"),
                        (60, "Collecting application files"),
                        (75, "Writing the provisioning package"),
                        (88, "Verifying the package"),
                        (95, "Cleaning up temporary files"),
                        (100, @"Package written: C:\Recovery\Customizations\usmt.ppkg")
                    });
                    await Steps(progress, ct, "Capturing", steps);
                    return new WorkerResult(true, false, "Simulated capture finished.");
            }
        }
        catch (OperationCanceledException)
        {
            progress.Report(new WorkerUpdate("[simulation] Stopped by the operator.", null, "Stopped"));
            return new WorkerResult(false, true, "Stopped before finishing.");
        }
    }

    private static async Task Steps(IProgress<WorkerUpdate> progress, CancellationToken ct, string status,
        IEnumerable<(int Percent, string Line)> steps)
    {
        foreach (var (percent, line) in steps)
        {
            progress.Report(new WorkerUpdate(line, percent, status + "..."));
            await Pause(ct);
        }
    }

    private static Task Pause(CancellationToken ct) => Task.Delay(450, ct);
}
