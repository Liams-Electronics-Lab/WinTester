using System.IO;
using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Reading what ScanState produced: the /progress file, the /l: log, the exit code (tasks 3.2 and 3.4). Pure logic.

public static class ProgressParser
{
    // The binary's /progress log carries estimatePercentageCompleted / totalPercentageCompleted fields (C8). The exact
    // line layout has not been seen yet, so this only looks for one of the two names followed by a number within a
    // few characters. The raw progress file is always kept with the run logs so the layout can be confirmed (3.2).
    private static readonly Regex PercentRx = new(
        @"(?:total|estimate)PercentageCompleted\D{0,16}?(\d{1,3})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string? line, out int percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var m = PercentRx.Match(line);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var p)) return false;
        percent = Math.Clamp(p, 0, 100);
        return true;
    }

    // Last percentage found in the lines, or null when none matched.
    public static int? Latest(IEnumerable<string> lines)
    {
        int? last = null;
        foreach (var l in lines)
            if (TryParse(l, out var p)) last = p;
        return last;
    }

    // Reads a file that another process is still writing.
    public static List<string> ReadLinesShared(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, true);
        string? line;
        while ((line = sr.ReadLine()) is not null) lines.Add(line);
        return lines;
    }
}

// SkippedIncludeRoots: include patterns ScanState dropped because their registry/file root does not exist (the line
// that exposed the wrong Uninstall path in t5, C31). For our generated rules every one of these is a real signal.
//
// ErrorCount does not include KnownNoiseCount: error lines that every healthy capture so far has contained and that say nothing
// about the package (see ScanStateLogAnalyzer.IsKnownNoise). They are counted and kept separately so none is hidden.
public sealed record LogAnalysis(bool LogFound, int ErrorCount, int WarningCount, bool SkippedErrorMarker,
    IReadOnlyList<string> FirstErrors, IReadOnlyList<string>? SkippedIncludeRoots = null,
    int KnownNoiseCount = 0, IReadOnlyList<string>? KnownNoiseLines = null);

public static class ScanStateLogAnalyzer
{
    // C17: text the binary prints when /c let at least one error pass (exit code 3).
    public const string SkippedMarker = "At least one error was skipped as a result of /c";
    public const string SkippedRootMarker = "Include pattern skipped because enumeration root is not present:";

    // Error lines that appear on every healthy capture on this tool's test machines and carry no information about the package
    // (C25): the PnP migration plugin (pnpmig.dll, plugin GUID ff9c714f-...) cannot read a policy key (access denied, 0x5) and
    // reports AllowDenyLayered (0x32). ScanState still ends with exit 0 and "Successful run" with zero ignored errors. Matching is
    // by the plugin's name or GUID in an Error-level line; the exact line text has not been seen by the author, so the "Not counted"
    // line in the run log shows whether the matcher fits. Any other error line still counts, and a pnpmig line in a run that fails
    // is still a failure: this only changes the amber "log contains errors" verdict of an exit-0 run.
    // S146: the second marker is the line ScanState prints when it cannot delete the Windows upgrade scratch key (access denied,
    // gle=0x5). It was in the S134 and the 20261007-202850 captures, both exit 0 with a working package, and is not from our rules.
    private static readonly string[] KnownNoiseMarkers =
        { "pnpmig", "ff9c714f", @"Failed to delete reg tree HKLM\SYSTEM\Setup\Upgrade" };

    public static bool IsKnownNoise(string errorLine)
    {
        if (string.IsNullOrEmpty(errorLine)) return false;
        foreach (var m in KnownNoiseMarkers)
            if (errorLine.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // USMT log lines look like "2017-11-25 02:24:47, Warning [0x0808b0] message".
    private static readonly Regex LevelRx = new(
        @"^\s*\d{4}[-/]\d{2}[-/]\d{2}\s+[\d:]+,\s*(Error|Warning)\b", RegexOptions.Compiled);

    private const int MaxListedErrors = 5;

    // Which of the roots ScanState skipped belong to include patterns this tool generated (pattern form "<path> [*]",
    // log form "<path>[*]"). Unrelated skips from the vendor XML files are left out. A pattern can carry escaped
    // brackets ("^[" and "^]") in its path; the log may print them with or without the escape, so a last comparison
    // ignores carets, brackets and spaces altogether.
    public static IReadOnlyList<string> OursSkipped(IEnumerable<string>? skippedRoots, IEnumerable<string> includePatterns)
    {
        var result = new List<string>();
        if (skippedRoots is null) return result;
        static string Norm(string x) => x.Replace(" [", "[").Trim().TrimEnd('\\');
        static int FirstBracket(string x)
        {
            for (var i = 0; i < x.Length; i++)
                if (x[i] == '[' && (i == 0 || x[i - 1] != '^')) return i;
            return -1;
        }
        static string PathPart(string x) { var i = FirstBracket(x); return (i < 0 ? x : x[..i]).TrimEnd('\\'); }
        static string Squash(string x) => x.Replace("^", "").Replace("[", "").Replace("]", "").Replace(" ", "");
        var ours = includePatterns.Select(Norm).ToList();
        foreach (var root in skippedRoots)
        {
            var r = Norm(root);
            var rPath = PathPart(r);
            var rSquash = Squash(r);
            if (ours.Any(o => o.Equals(r, StringComparison.OrdinalIgnoreCase)
                              || PathPart(o).Equals(rPath, StringComparison.OrdinalIgnoreCase)
                              || Squash(o).Equals(rSquash, StringComparison.OrdinalIgnoreCase)))
                result.Add(root);
        }
        return result;
    }

    // logPath may be missing; extraLines are ScanState's own console output, searched for the marker too.
    public static LogAnalysis Analyze(string? logPath, IEnumerable<string>? extraLines = null)
    {
        var errors = 0;
        var warnings = 0;
        var marker = false;
        var first = new List<string>();
        var skippedRoots = new List<string>();
        var noise = new List<string>();
        var noiseCount = 0;
        var found = false;

        try
        {
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                found = true;
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, true);
                string? line;
                while ((line = sr.ReadLine()) is not null)
                {
                    if (line.Contains(SkippedMarker, StringComparison.OrdinalIgnoreCase)) marker = true;
                    var at = line.IndexOf(SkippedRootMarker, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        var root = line[(at + SkippedRootMarker.Length)..].Trim();
                        if (root.Length > 0 && skippedRoots.Count < 200 && !skippedRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                            skippedRoots.Add(root);
                    }
                    var m = LevelRx.Match(line);
                    if (!m.Success) continue;
                    if (m.Groups[1].Value == "Error")
                    {
                        var shortLine = line.Trim().Length > 220 ? line.Trim()[..220] : line.Trim();
                        if (IsKnownNoise(line))
                        {
                            noiseCount++;
                            if (noise.Count < MaxListedErrors) noise.Add(shortLine);
                            continue;
                        }
                        errors++;
                        if (first.Count < MaxListedErrors) first.Add(shortLine);
                    }
                    else warnings++;
                }
            }
        }
        catch
        {
            // A log that cannot be read is reported as "not found" rather than failing the whole run.
            found = false;
        }

        if (extraLines is not null)
            foreach (var l in extraLines)
                if (l.Contains(SkippedMarker, StringComparison.OrdinalIgnoreCase)) { marker = true; break; }

        return new LogAnalysis(found, errors, warnings, marker, first, skippedRoots, noiseCount, noise);
    }
}

// Exit codes from Microsoft's USMT 10.0 return-code list (only the ones likely with this command line).
public static class UsmtReturnCodes
{
    public const int Success = 0;
    public const int Canceled = 2;
    public const int WouldHaveFailed = 3;   // "At least one error was skipped as a result of /c"

    public static string Describe(int code) => code switch
    {
        0 => "success",
        2 => "USMT_STATUS_CANCELED: ScanState stopped early (for example on an encrypted file)",
        3 => "USMT_WOULD_HAVE_FAILED: at least one error was skipped because of /c",
        11 => "USMT_INVALID_PARAMETERS: the command line was rejected (see the ScanState log)",
        12 => "USMT_ERROR_OPTION_PARAM_TOO_LARGE: the command line is longer than 256 characters",
        13 => "USMT_INIT_LOGFILE_FAILED: the log path was not accepted",
        26 => "USMT_INIT_ERROR: a loaded .xml file is invalid or several Windows installations were found",
        27 => "USMT_INVALID_STORE_LOCATION: the output location was not accepted",
        28 => "USMT_UNABLE_GET_SCRIPTFILES: a migration .xml file is missing or invalid",
        29 => "USMT_FAILED_MIGSTARTUP: the migration engine could not start; read the ScanState log for the real cause (a missing hwcompat.dll was one)",
        31 => "USMT_UNABLE_FINDMIGUNITS: the discovery phase failed",
        32 => "USMT_FAILED_SETMIGRATIONTYPE: the migration system failed to start",
        34 => "USMT_ERROR_INSUFFICIENT_RIGHTS: run as administrator",
        36 => "USMT_ERROR_UNSUPPORTED_PLATFORM: compliance check failed (look for an active temporary profile)",
        39 => "USMT_UNABLE_TO_READ_CONFIG_FILE: the /config: file could not be read",
        40 => "USMT_ERROR_UNABLE_CREATE_PROGRESS_LOG: the progress log could not be created",
        41 => "USMT_PREFLIGHT_FILE_CREATION_FAILED: an output file could not be overwritten",
        61 => "USMT_MIGRATION_STOPPED_NONFATAL: processing stopped due to an I/O error",
        71 => "USMT_INIT_OPERATING_ENVIRONMENT_FAILED: a Win32 error occurred or USMT was not elevated",
        72 => "USMT_UNABLE_DOMIGRATION: error while gathering, or out of disk space",
        // Windows loader failures: scanstate.exe never started (C20).
        -1073741511 => "0xC0000139 entry point not found: this USMT build does not match the Windows build it runs on (for example 10.0.28000 on an older Windows); use the USMT from the ADK 10.1.26100 or one that matches this Windows",
        -1073741515 => "0xC0000135 a required DLL is missing next to scanstate.exe; the USMT folder is incomplete",
        -1073741502 => "0xC0000142 a USMT DLL failed to initialise",
        -1073741701 => "0xC000007B bad image: a USMT file does not match this machine's architecture (amd64 vs x86)",
        _ => "unrecognised exit code"
    };
}

public enum CaptureOutcomeKind { Success, SuccessWithWarnings, Failed, Cancelled }

public sealed record CaptureOutcome(CaptureOutcomeKind Kind, string Summary, IReadOnlyList<string> Details);

// Decides what a finished ScanState run means (C7). A zero exit code is not enough on its own: /c can let errors
// through, so the package must exist and the log is checked as well.
public static class CaptureVerifier
{
    // packageBytes: size of usmt.ppkg after the run, or -1 when the file does not exist.
    public static CaptureOutcome Verify(int exitCode, bool cancelled, long packageBytes, LogAnalysis log)
    {
        var details = new List<string>();
        if (log.LogFound)
            details.Add($"ScanState log: {log.ErrorCount} error line(s), {log.WarningCount} warning line(s).");
        else
            details.Add("The ScanState log could not be read, so it was not checked for errors.");
        details.AddRange(log.FirstErrors);
        if (log.KnownNoiseCount > 0)
        {
            details.Add($"Not counted: {log.KnownNoiseCount} error line(s) known from healthy captures (the PnP migration plugin pnpmig, and the failed delete of HKLM\\SYSTEM\\Setup\\Upgrade). They have appeared on every capture so far; the lines are kept in the saved ScanState log.");
            foreach (var l in (log.KnownNoiseLines ?? Array.Empty<string>()).Take(4)) details.Add("  (not counted) " + l);
        }

        if (cancelled)
            return new CaptureOutcome(CaptureOutcomeKind.Cancelled, "Stopped before finishing. No package was kept.", details);

        var packageOk = packageBytes > 0;
        var exitText = $"exit code {exitCode} ({UsmtReturnCodes.Describe(exitCode)})";

        if (exitCode == UsmtReturnCodes.Success)
        {
            if (!packageOk)
                return new CaptureOutcome(CaptureOutcomeKind.Failed,
                    "ScanState reported success but no package was written.", details);
            if (log.ErrorCount > 0 || log.SkippedErrorMarker)
                return new CaptureOutcome(CaptureOutcomeKind.SuccessWithWarnings,
                    $"Package written ({RecoveryFormat.Bytes(packageBytes)}), but the log contains errors. Review it before relying on the package.",
                    details);
            return new CaptureOutcome(CaptureOutcomeKind.Success, $"Package written ({RecoveryFormat.Bytes(packageBytes)}).", details);
        }

        if (exitCode == UsmtReturnCodes.WouldHaveFailed)
        {
            if (!packageOk)
                return new CaptureOutcome(CaptureOutcomeKind.Failed,
                    $"ScanState finished with {exitText} and wrote no package.", details);
            return new CaptureOutcome(CaptureOutcomeKind.SuccessWithWarnings,
                $"Package written ({RecoveryFormat.Bytes(packageBytes)}), but ScanState skipped at least one error (/c). Review the log.",
                details);
        }

        return new CaptureOutcome(CaptureOutcomeKind.Failed, $"ScanState failed: {exitText}.", details);
    }
}
