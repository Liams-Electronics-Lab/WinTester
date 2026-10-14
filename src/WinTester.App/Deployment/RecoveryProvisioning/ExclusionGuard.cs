using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Keeps the Extra paths box from excluding something the package is built from. An exclusion beats every include rule, so a
// line that covers the Windows folder, the registry hives, the folder of a ticked app or the Recovery folder would leave a
// package that captures nothing useful (or cannot be applied) while the capture still reports success.
//
// Two kinds of protected location, all on the system drive:
//   * folder: a line may not be that folder or a recursive parent of it (so "C:\Windows\*" and "C:\" are refused, while a
//     sub-folder such as "C:\Windows\Logs" is the operator's choice);
//   * tree:   as above, and nothing inside it may be excluded either (hives, driver store, Start menu, Recovery folder,
//     Public Desktop).
// Whatever the file part says, a line that only names junk (an extension in JunkExtensions, for example "*.tmp") is allowed,
// because removing temporary files from a protected folder does not damage it.
public static class ExclusionGuard
{
    public sealed record ProtectedPath(string Path, bool Tree, string Reason);

    private static readonly string[] JunkExtensions = { "tmp", "log", "bak", "old", "dmp", "etl", "cache" };

    // The protected set for the options the capture will run with. `apps` are the ticked apps (empty in settings-only mode).
    public static IReadOnlyList<ProtectedPath> ProtectedFor(string systemDrive, bool includeDrivers, bool copyDesktopShortcuts,
        IEnumerable<AppCapturePlan> apps, IEnumerable<string>? customerPaths = null)
    {
        var sd = Drive(systemDrive);
        var list = new List<ProtectedPath>
        {
            new(sd, false, "the system drive"),
            new(sd + @"\Windows", false, "Windows system files"),
            new(sd + @"\Windows\System32", false, "Windows system files"),
            new(sd + @"\Windows\System32\config", true, "the registry hives"),
            new(sd + @"\ProgramData", false, "shared program data"),
            new(sd + @"\ProgramData\Microsoft\Windows\Start Menu", true, "the Start menu shortcuts"),
            new(sd + @"\Recovery", true, "the Recovery folder that holds the package"),
        };
        if (includeDrivers) list.Add(new(sd + @"\Windows\System32\DriverStore", true, "the driver store"));
        if (copyDesktopShortcuts) list.Add(new(sd + @"\Users\Public\Desktop", true, "the Public Desktop shortcuts"));

        foreach (var c in customerPaths ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(c)) list.Add(new(c, true, "your customer files"));   // 5.5

        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Folder)) list.Add(new(Expand(app.Folder, sd) ?? app.Folder, false, "the files of " + app.Name));
            foreach (var f in app.FilePatterns)
            {
                const string tail = @"\* [*]";
                if (!f.EndsWith(tail, StringComparison.Ordinal)) continue;
                var dir = Expand(f[..^tail.Length], sd);
                if (dir is not null) list.Add(new(dir, false, "the files of " + app.Name));
            }
        }
        return list;
    }

    public static IReadOnlyList<ProtectedPath> ProtectedFor(string systemDrive, CaptureOptions o) =>
        ProtectedFor(systemDrive, o.IncludeDrivers, o.CopyDesktopShortcuts, o.ActiveApps, o.CustomerFiles.Select(i => i.FinalPath));

    // Null when the line is fine (or is not something this check can judge: a line that does not convert, another drive, an
    // unresolved %VARIABLE%). Otherwise a message that names the line and what it would remove.
    public static string? Check(string rawLine, string systemDrive, IReadOnlyList<ProtectedPath> protectedPaths)
    {
        var converted = RulesXmlBuilder.TryConvertCustom(rawLine, out _);
        if (converted is null) return null;
        if (!Split(converted, out var dirText, out var recursive, out var spec)) return null;
        if (IsJunk(spec)) return null;

        var sd = Drive(systemDrive);
        var dir = Expand(dirText, sd);
        if (dir is null) return null;
        var line = Segments(dir);
        if (line.Count == 0 || !line[0].Equals(sd, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var p in protectedPaths)
        {
            var prot = Segments(p.Path);
            if (prot.Count == 0 || !Covers(line, recursive, prot, p.Tree)) continue;
            return $"\"{rawLine.Trim()}\": this would exclude {p.Reason}, which the package needs. Remove this line or choose something inside a narrower folder.";
        }
        return null;
    }

    public static IReadOnlyList<string> Violations(IEnumerable<string> lines, string systemDrive, CaptureOptions o)
    {
        var prot = ProtectedFor(systemDrive, o);
        var list = new List<string>();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            var v = Check(l, systemDrive, prot);
            if (v is not null) list.Add(v);
        }
        return list;
    }

    // ---- matching ---------------------------------------------------------------------------------------------------

    private static bool Covers(IReadOnlyList<string> line, bool recursive, IReadOnlyList<string> prot, bool tree)
    {
        // The line names the protected folder itself, or a parent of it (a parent only matters when it reaches down).
        if (line.Count <= prot.Count && PrefixMatches(line, prot))
            return line.Count == prot.Count || recursive;
        // The line sits inside a protected tree.
        return tree && line.Count > prot.Count && PrefixMatches(prot, line, patternIsFirst: false);
    }

    // True when every segment of `shorter` matches the segment at the same place in `longer`. A '*' in the line's segment
    // matches any characters of one segment; protected paths are literal.
    private static bool PrefixMatches(IReadOnlyList<string> shorter, IReadOnlyList<string> longer, bool patternIsFirst = true)
    {
        for (var i = 0; i < shorter.Count; i++)
            if (!(patternIsFirst ? SegmentMatches(shorter[i], longer[i]) : SegmentMatches(longer[i], shorter[i]))) return false;
        return true;
    }

    private static bool SegmentMatches(string pattern, string name)
    {
        if (!pattern.Contains('*')) return pattern.Equals(name, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
    }

    // "dir\* [spec]" is recursive, "dir [spec]" is that folder only.
    private static bool Split(string converted, out string dir, out bool recursive, out string spec)
    {
        dir = ""; spec = ""; recursive = false;
        var open = converted.LastIndexOf(" [", StringComparison.Ordinal);
        if (open < 0 || !converted.EndsWith(']')) return false;
        spec = converted[(open + 2)..^1];
        dir = converted[..open];
        if (dir.EndsWith(@"\*", StringComparison.Ordinal)) { recursive = true; dir = dir[..^2]; }
        return true;
    }

    private static bool IsJunk(string spec)
    {
        var s = spec.Trim();
        var dot = s.LastIndexOf('.');
        if (dot < 0 || dot == s.Length - 1) return false;
        var ext = s[(dot + 1)..];
        // "*.tmp" or "name.tmp": the extension alone decides; a wildcard inside the extension is not junk.
        return !ext.Contains('*') && JunkExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> Segments(string path) =>
        path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Drive(string systemDrive)
    {
        var d = (systemDrive ?? "").Trim().TrimEnd('\\', '/');
        return d.Length >= 2 && d[1] == ':' ? char.ToUpperInvariant(d[0]) + ":" : "C:";
    }

    // Folder variables resolved against the given system drive first (so the answer does not depend on the machine the
    // check runs on), then the rest through the environment. Null when a %VARIABLE% is still left.
    private static string? Expand(string path, string sd)
    {
        var p = path.Trim();
        p = Regex.Replace(p, "%ProgramFiles\\(x86\\)%", sd + @"\Program Files (x86)", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%ProgramFiles%", sd + @"\Program Files", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%ProgramData%", sd + @"\ProgramData", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%(SystemRoot|windir)%", sd + @"\Windows", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%SystemDrive%", sd, RegexOptions.IgnoreCase);
        if (p.Contains('%')) p = Environment.ExpandEnvironmentVariables(p);
        return p.Contains('%') ? null : p;
    }
}
