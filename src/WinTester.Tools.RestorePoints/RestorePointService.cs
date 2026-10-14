using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WinTester.Tools.RestorePoints;

public sealed record OperationResult(bool Success, string Message);

public enum CreateOutcome
{
    Created,
    Skipped,
    Disabled,
    Failed,
}

public sealed record CreateResult(CreateOutcome Outcome, string Message, uint? Sequence = null);

// State of the manual creation limit before it was lifted. Changed is false when nothing was written.
internal readonly record struct LimitState(bool Changed, uint? Previous);

// List, create, delete and restore System Restore points through WMI (root\default, class SystemRestore)
// and srclient.dll. Needs administrator rights. Calls block: run them off the UI thread.
[SupportedOSPlatform("windows")]
public static class RestorePointService
{
    public const string DefaultName = "WinTester restore point";

    // Conservative cap on the description length.
    public const int MaxNameLength = 64;

    private const uint BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;
    private const int ConfirmWaitMs = 45_000;
    private const int ConfirmPollMs = 1_500;
    private const int PostScriptWaitMs = 15_000;
    private const int ScriptTimeoutMs = 180_000;
    private const string NameVariable = "WT_RESTORE_POINT_NAME";

    // Runs Checkpoint-Computer in a background job and prints its progress as plain lines:
    // "PROGRESS n", "WARN text" and "ERROR text". The name arrives in an environment variable,
    // so no user text is ever part of the command line.
    private const string CheckpointScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        function Emit([string]$text) { [Console]::Out.WriteLine($text); [Console]::Out.Flush() }
        $job = Start-Job -ScriptBlock {
            param($name)
            Checkpoint-Computer -Description $name -RestorePointType 'MODIFY_SETTINGS'
        } -ArgumentList $env:WT_RESTORE_POINT_NAME
        $child = $job.ChildJobs[0]
        $last = -1
        while ($job.State -eq 'Running' -or $job.State -eq 'NotStarted') {
            $record = $child.Progress | Select-Object -Last 1
            if ($record -and $record.PercentComplete -ge 0 -and $record.PercentComplete -ne $last) {
                $last = $record.PercentComplete
                Emit "PROGRESS $last"
            }
            Start-Sleep -Milliseconds 400
        }
        foreach ($warning in $child.Warning) { Emit ("WARN " + $warning.Message) }
        try { Receive-Job $job -ErrorAction Stop | Out-Null }
        catch {
            Emit ("ERROR " + $_.Exception.Message)
            Remove-Job $job -Force
            exit 1
        }
        Remove-Job $job -Force
        Emit 'PROGRESS 100'
        exit 0
        """;

    [DllImport("srclient.dll", ExactSpelling = true)]
    private static extern uint SRRemoveRestorePoint(uint restorePointNumber);

    // System Restore methods need backup and restore privileges, which are present but disabled in an
    // administrator token until the WMI connection asks for them.
    private static ManagementClass OpenClass()
    {
        var scope = new ManagementScope(@"\\.\root\default", new ConnectionOptions { EnablePrivileges = true });
        return new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
    }

    public static IReadOnlyList<RestorePointInfo> List()
    {
        var points = new List<RestorePointInfo>();
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using var mo = (ManagementObject)item;
            points.Add(new RestorePointInfo(
                Convert.ToUInt32(mo["SequenceNumber"]),
                RestorePointInfo.ParseWmiDate(mo["CreationTime"] as string),
                mo["Description"] as string ?? string.Empty,
                Convert.ToUInt32(mo["RestorePointType"])));
        }

        points.Sort((a, b) => b.Sequence.CompareTo(a.Sequence));
        return points;
    }

    // Creates a manual restore point with Checkpoint-Computer, which also reports progress (0 to 100).
    // Windows 8 and later skip the request when one was made in the last 24 hours, so the creation
    // limit is lifted for the call and a new sequence number is required. If Windows PowerShell
    // cannot be started, the WMI method is used instead.
    public static CreateResult Create(string? name, IProgress<int>? progress = null)
    {
        var description = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        if (description.Length > MaxNameLength) description = description[..MaxNameLength];

        HashSet<uint> before;
        try
        {
            before = List().Select(p => p.Sequence).ToHashSet();
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return new CreateResult(CreateOutcome.Failed, $"Could not read restore points. {ex.Message}");
        }

        // The limit stays lifted until the new point is visible.
        var previous = LiftCreationLimit();
        try
        {
            var run = RunCheckpointScript(description, progress);
            return run is null
                ? CreateViaWmi(description, before, previous.Changed)
                : InterpretScript(run, before, previous.Changed);
        }
        finally
        {
            RestoreCreationLimit(previous);
        }
    }

    private sealed record ScriptRun(int ExitCode, string? Error, string? Warning, string Stderr);

    // Null when Windows PowerShell is not available.
    private static ScriptRun? RunCheckpointScript(string description, IProgress<int>? progress)
    {
        var exe = PowerShellHost.Exe();
        if (exe is null) return null;

        var encoded = PowerShellHost.Encode(CheckpointScript);
        var info = new ProcessStartInfo(exe,
            $"{PowerShellHost.BaseArguments} -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment[NameVariable] = description;

        string? error = null;
        string? warning = null;
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data?.Trim();
            if (string.IsNullOrEmpty(line)) return;

            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(9), out var percent))
                    progress?.Report(Math.Clamp(percent, 0, 100));
            }
            else if (line.StartsWith("WARN ", StringComparison.Ordinal))
            {
                warning = line[5..];
            }
            else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
            {
                error = line[6..];
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(ScriptTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new ScriptRun(-1, "Windows PowerShell did not finish in time.", warning, string.Empty);
        }

        process.WaitForExit(); // lets the output handlers finish
        return new ScriptRun(process.ExitCode, error, warning, stderr.ToString());
    }

    private static CreateResult InterpretScript(ScriptRun run, HashSet<uint> before, bool limitLifted)
    {
        if (run.ExitCode != 0)
        {
            var detail = run.Error ?? LastTextLine(run.Stderr) ?? $"Exit code {run.ExitCode}.";
            return detail.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                ? new CreateResult(CreateOutcome.Disabled, "System Restore is disabled.")
                : new CreateResult(CreateOutcome.Failed, $"Windows did not create a restore point. {detail}");
        }

        var result = WaitForNewPoint(before, limitLifted, PostScriptWaitMs);

        // Checkpoint-Computer explains a skipped request in a warning. Show that text.
        return result.Outcome == CreateOutcome.Skipped && run.Warning is not null
            ? new CreateResult(CreateOutcome.Skipped, run.Warning)
            : result;
    }

    // Last line that is not PowerShell's serialized progress or error markup.
    private static string? LastTextLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => !l.StartsWith('<') && !l.StartsWith("#<", StringComparison.Ordinal));

    // Fallback when Windows PowerShell is missing.
    private static CreateResult CreateViaWmi(string description, HashSet<uint> before, bool limitLifted)
    {
        uint code;
        try
        {
            using var cls = OpenClass();
            code = Convert.ToUInt32(cls.InvokeMethod("CreateRestorePoint",
                new object[] { description, RestorePointTypes.ModifySettings, BeginSystemChange }));
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return new CreateResult(CreateOutcome.Failed, $"Windows did not create a restore point. {ex.Message}");
        }

        if (code == ErrorServiceDisabled)
            return new CreateResult(CreateOutcome.Disabled, "System Restore is disabled.");

        if (code != 0)
            return new CreateResult(CreateOutcome.Failed,
                $"Windows did not create a restore point (code {code}). Check the settings and try again.");

        return WaitForNewPoint(before, limitLifted, ConfirmWaitMs);
    }

    // Polls until a sequence number that was not there before appears, or the wait runs out.
    // When nothing appears, System Protection being off for the system drive is the usual cause:
    // Windows then reports success without creating anything.
    private static CreateResult WaitForNewPoint(HashSet<uint> before, bool limitLifted, int waitMs)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var added = List().FirstOrDefault(p => !before.Contains(p.Sequence));
                if (added is not null)
                    return new CreateResult(CreateOutcome.Created, "Restore point created.", added.Sequence);
            }
            catch (Exception ex) when (IsWmiFailure(ex))
            {
                return new CreateResult(CreateOutcome.Failed, $"Could not confirm the restore point. {ex.Message}");
            }

            if (timer.ElapsedMilliseconds >= waitMs) break;
            Thread.Sleep(ConfirmPollMs);
        }

        if (IsProtectionOff(RestoreConfigService.SystemDrive()))
            return new CreateResult(CreateOutcome.Disabled, "System Protection is off for the system drive.");

        return new CreateResult(CreateOutcome.Skipped, limitLifted
            ? "Windows accepted the request but no restore point appeared. "
              + "Check the space limit for the system drive and the Application event log for VSS errors."
            : "Windows accepted the request but no restore point appeared. Windows allows one per 24 hours "
              + "and the limit could not be lifted. Run WinTester as administrator.");
    }

    // True when the drive has no shadow storage, which is what System Protection off looks like.
    // A failed query counts as unknown, not off.
    private static bool IsProtectionOff(string drive)
    {
        try
        {
            return RestoreConfigService.ReadSpace(drive) is null;
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return false;
        }
    }

    // Turns System Restore on for a drive such as "C:", and makes sure the drive has a space limit.
    public static OperationResult Enable(string drive)
    {
        try
        {
            using var cls = OpenClass();
            var code = Convert.ToUInt32(cls.InvokeMethod("Enable", new object[] { NormalizeDrive(drive), true }));
            if (code != 0)
                return new OperationResult(false, $"Could not enable System Restore (code {code}).");
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return new OperationResult(false, $"Could not enable System Restore. {ex.Message}");
        }

        var storage = RestoreConfigService.EnsureShadowStorage(drive);
        return storage.Success
            ? new OperationResult(true, "System Restore enabled.")
            : new OperationResult(true, $"System Restore enabled. {storage.Message}");
    }

    // Turns System Restore off for a drive. Windows deletes the restore points on that drive.
    public static OperationResult Disable(string drive)
    {
        try
        {
            using var cls = OpenClass();
            var code = Convert.ToUInt32(cls.InvokeMethod("Disable", new object[] { NormalizeDrive(drive) }));
            if (code != 0)
                return new OperationResult(false, $"Could not disable System Restore (code {code}).");
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return new OperationResult(false, $"Could not disable System Restore. {ex.Message}");
        }

        // Best effort: an association left behind would still read as protection on.
        RestoreConfigService.RemoveShadowStorage(drive);
        return new OperationResult(true, "System Restore disabled.");
    }

    public static OperationResult Delete(uint sequence)
    {
        try
        {
            var code = SRRemoveRestorePoint(sequence);
            return code == 0
                ? new OperationResult(true, "Restore point deleted.")
                : new OperationResult(false, $"Windows could not delete the restore point (code {code}).");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new OperationResult(false, "System Restore is not available on this computer.");
        }
    }

    // Schedules the restore, then restarts the computer to run it.
    public static OperationResult RestoreAndRestart(uint sequence)
    {
        try
        {
            using var cls = OpenClass();
            var code = Convert.ToUInt32(cls.InvokeMethod("Restore", new object[] { sequence }));
            if (code != 0)
                return new OperationResult(false, $"Windows could not start the restore (code {code}).");
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return new OperationResult(false, $"Windows could not start the restore. {ex.Message}");
        }

        return StartRestart()
            ? new OperationResult(true, "Restarting to run the restore.")
            : new OperationResult(false, "The restore is scheduled. Restart the computer to continue.");
    }

    public static string NormalizeDrive(string drive)
    {
        var letter = drive.Trim().TrimEnd('\\', ':');
        return letter.Length == 0 ? drive : letter + @":\";
    }

    private static bool StartRestart()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Best effort. Previous is null when the value did not exist.
    private static LimitState LiftCreationLimit()
    {
        try
        {
            var previous = SrRegistry.ReadDword(SrRegistry.FrequencyValue);
            SrRegistry.WriteDword(SrRegistry.FrequencyValue, 0);
            return new LimitState(true, previous);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new LimitState(false, null);
        }
    }

    private static void RestoreCreationLimit(LimitState state)
    {
        if (!state.Changed) return;
        try
        {
            if (state.Previous is uint value) SrRegistry.WriteDword(SrRegistry.FrequencyValue, value);
            else SrRegistry.DeleteValue(SrRegistry.FrequencyValue);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Best effort: the limit stays lifted.
        }
    }

    private static bool IsWmiFailure(Exception ex) =>
        ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException;
}
