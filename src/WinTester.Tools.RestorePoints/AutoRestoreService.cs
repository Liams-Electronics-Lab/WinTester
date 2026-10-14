using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace WinTester.Tools.RestorePoints;

public enum AutoPointStatus
{
    // The task is on and a scheduled restore point exists.
    Enabled,
    Disabled,

    // The task is on, but no scheduled restore point exists. Windows is not making them.
    Idle,
    Unknown,
}

// When the scheduled task last ran, its result code and next run. Null fields mean never or none.
public sealed record TaskRunInfo(DateTime? LastRun, int? LastResult, DateTime? NextRun);

public sealed record AutoPointInfo(AutoPointStatus Status, string Detail,
    TaskRunInfo? Run = null, DateTime? LastScheduledPoint = null)
{
    // Plain text for a tooltip.
    public string Summary()
    {
        var lines = new List<string>();
        if (Detail.Length > 0) lines.Add(Detail);
        if (Run is not null)
        {
            lines.Add($"Task last ran: {When(Run.LastRun)}" + (Run.LastResult is int code ? $" (result {code})" : string.Empty));
            lines.Add($"Task next run: {When(Run.NextRun)}");
        }

        lines.Add($"Last scheduled restore point: {When(LastScheduledPoint)}");
        return string.Join("\n", lines);
    }

    private static string When(DateTime? value) => value is null ? "never" : value.Value.ToString("g");
}

// Automatic restore points are created by a scheduled task. When the task is off, the interval
// has no effect. Needs administrator rights. Calls block: run them off the UI thread.
[SupportedOSPlatform("windows")]
public static class AutoRestoreService
{
    public const string TaskName = @"\Microsoft\Windows\SystemRestore\SR";

    private const int TimeoutMs = 30_000;

    // Task state and run history. Whether scheduled points really exist is decided by Assess.
    public static AutoPointInfo Read() => ReadState() with { Run = ReadTaskRun() };

    // Combines the task state with the restore points that exist. A task that is on while no
    // scheduled (System Checkpoint) point exists is reported as Idle, not Enabled.
    public static AutoPointInfo Assess(AutoPointInfo task, IEnumerable<RestorePointInfo> points)
    {
        DateTime? last = null;
        foreach (var point in points)
        {
            if (point.TypeCode == RestorePointTypes.SystemCheckpoint && (last is null || point.Created > last))
                last = point.Created;
        }

        var info = task with { LastScheduledPoint = last };
        return task.Status == AutoPointStatus.Enabled && last is null
            ? info with
            {
                Status = AutoPointStatus.Idle,
                Detail = "The task is on, but no scheduled (System Checkpoint) restore point exists.",
            }
            : info;
    }

    private static AutoPointInfo ReadState()
    {
        var run = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (run is null)
            return new AutoPointInfo(AutoPointStatus.Unknown, "Could not start the task scheduler tool.");

        if (run.ExitCode != 0)
            return new AutoPointInfo(AutoPointStatus.Unknown, "The scheduled task for automatic restore points was not found.");

        return ParseTaskEnabled(run.Output) switch
        {
            true => new AutoPointInfo(AutoPointStatus.Enabled, string.Empty),
            false => new AutoPointInfo(AutoPointStatus.Disabled,
                "The scheduled task that creates automatic restore points is off."),
            null => new AutoPointInfo(AutoPointStatus.Unknown, "Could not read the scheduled task."),
        };
    }

    public static OperationResult Enable() => Change("/ENABLE", "Automatic restore points enabled.",
        "Could not enable automatic restore points");

    public static OperationResult Disable() => Change("/DISABLE", "Automatic restore points disabled.",
        "Could not disable automatic restore points");

    // Reads the Enabled flag from the task's Settings element. The triggers carry their own Enabled
    // flags, so those are ignored. A task without the flag is enabled. Null when the text is not XML.
    public static bool? ParseTaskEnabled(string? xml)
    {
        var text = xml?.Replace("\0", string.Empty);
        var start = text?.IndexOf('<') ?? -1;
        if (text is null || start < 0) return null;

        try
        {
            var doc = XDocument.Parse(text[start..]);
            var settings = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings");
            var enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled");
            return enabled is null
                || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    // Prints three lines: "LAST <time or none>", "RESULT <code>" and "NEXT <time or none>".
    private const string TaskInfoScript = """
        $ErrorActionPreference = 'Stop'
        function Fmt($d) { if ($d -and $d.Year -gt 2000) { $d.ToString('o') } else { 'none' } }
        try {
            $i = Get-ScheduledTaskInfo -TaskPath '\Microsoft\Windows\SystemRestore\' -TaskName 'SR'
            [Console]::Out.WriteLine('LAST ' + (Fmt $i.LastRunTime))
            [Console]::Out.WriteLine('RESULT ' + $i.LastTaskResult)
            [Console]::Out.WriteLine('NEXT ' + (Fmt $i.NextRunTime))
        } catch { exit 1 }
        """;

    // Null when Windows PowerShell is missing or the task cannot be read.
    public static TaskRunInfo? ReadTaskRun()
    {
        var exe = PowerShellHost.Exe();
        if (exe is null) return null;

        var info = new ProcessStartInfo(exe,
            $"{PowerShellHost.BaseArguments} -EncodedCommand {PowerShellHost.Encode(TaskInfoScript)}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode == 0 ? ParseTaskRun(output.Result) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // Reads the three lines printed by TaskInfoScript. Null when none of them is present.
    public static TaskRunInfo? ParseTaskRun(string? output)
    {
        DateTime? last = null, next = null;
        int? result = null;
        var found = false;

        foreach (var raw in (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("LAST ", StringComparison.Ordinal)) { last = ParseTime(raw[5..]); found = true; }
            else if (raw.StartsWith("NEXT ", StringComparison.Ordinal)) { next = ParseTime(raw[5..]); found = true; }
            else if (raw.StartsWith("RESULT ", StringComparison.Ordinal)
                     && int.TryParse(raw.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                result = code;
                found = true;
            }
        }

        return found ? new TaskRunInfo(last, result, next) : null;
    }

    private static DateTime? ParseTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value.LocalDateTime
            : null;

    private static OperationResult Change(string switchText, string okText, string failText)
    {
        var run = RunSchtasks($"/Change /TN \"{TaskName}\" {switchText}");
        if (run is null)
            return new OperationResult(false, $"{failText}. Could not start the task scheduler tool.");

        if (run.ExitCode == 0)
            return new OperationResult(true, okText);

        var detail = LastLine(run.Error) ?? LastLine(run.Output);
        return new OperationResult(false, detail is null
            ? $"{failText} (code {run.ExitCode})."
            : $"{failText} (code {run.ExitCode}). {detail}");
    }

    private sealed record ToolRun(int ExitCode, string Output, string Error);

    // Only the fixed task name reaches the command line. Null when schtasks cannot be started or times out.
    private static ToolRun? RunSchtasks(string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                return null;
            }

            return new ToolRun(process.ExitCode, output.Result, errors.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
