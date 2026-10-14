namespace WinTester.App.Deployment.RecoveryProvisioning;

// Lines for the wizard's Extra paths box when the operator browses instead of typing. Kept free of WPF so the harness can
// check it. A picked folder ends in a backslash because RulesXmlBuilder.TryConvertCustom reads a last segment that
// contains a dot as a file ("Tools\App 2.1" would otherwise become a file pattern).
public static class CustomExclusionLine
{
    // Null for a drive root: excluding a whole drive is not something this box should do by accident.
    public static string? FromFolder(string folder)
    {
        var p = (folder ?? "").Trim();
        if (p.Length == 0) return null;
        var trimmed = p.TrimEnd('\\', '/');
        if (trimmed.Length <= 2 && trimmed.EndsWith(':')) return null;
        return trimmed + "\\";
    }

    public static string FromFile(string file) => (file ?? "").Trim();

    // Adds `line` as a new last line of `existing` unless an equal line is already there (case-insensitive, ignoring
    // surrounding blanks). The result never gains a blank line.
    public static string Append(string existing, string line)
    {
        var add = (line ?? "").Trim();
        existing ??= "";
        if (add.Length == 0) return existing;
        var lines = existing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Any(l => string.Equals(l.Trim(), add, StringComparison.OrdinalIgnoreCase))) return existing;
        var body = existing.TrimEnd('\r', '\n');
        return body.Length == 0 ? add : body + "\r\n" + add;
    }

    // True for a line that starts with a drive letter other than the system drive's: only that drive is captured (C36), so
    // an exclusion on another drive does nothing. Lines that start with %VARIABLE% are not judged.
    public static bool OffSystemDrive(string line, string systemDrive)
    {
        var l = (line ?? "").Trim();
        var sd = (systemDrive ?? "").Trim();
        if (l.Length < 2 || l[1] != ':' || !char.IsLetter(l[0])) return false;
        if (sd.Length == 0) sd = "C:";
        return char.ToUpperInvariant(l[0]) != char.ToUpperInvariant(sd[0]);
    }
}
