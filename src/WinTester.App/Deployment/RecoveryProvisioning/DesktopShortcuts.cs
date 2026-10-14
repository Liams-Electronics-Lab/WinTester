using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Result of one copy run. Copied/Failed hold file names (Failed also carries the reason). SkippedPerUser names the
// shortcuts that were left out because their target sits inside a user profile (C:\Users\<name>\...), where they
// would not work for a different user after a reset. FolderError is set when the target folder could not be prepared
// (nothing copied).
public sealed record DesktopCopyResult(
    string SourceFolder,
    string TargetFolder,
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> SkippedPerUser,
    string? FolderError);

// Optional capture step: copy the current user's desktop shortcuts into the Public Desktop so that, once the package
// is restored by Reset this PC, every user (including the one made after a reset) sees them. The Public Desktop folder
// is then added to the package by an include rule in RulesXmlBuilder. Pure logic, no WPF.
public static class DesktopShortcuts
{
    private static readonly string[] Extensions = { ".lnk", ".url" };

    // <drive>\Users\Public\Desktop. The same string feeds the copy target and the include rule, so they cannot drift
    // apart. A relocated Public profile (unattend ProfilesDirectory) is not supported.
    public static string PublicDesktop(string systemDrive)
    {
        var d = (systemDrive ?? "").Trim().TrimEnd('\\', '/');
        if (d.Length == 0) d = "C:";
        return d + @"\Users\Public\Desktop";
    }

    // The desktop of the account WinTester runs as. When the tool was elevated with another admin account's
    // credentials this is that account's desktop, not the signed-in user's (the worker logs the folder it used).
    // Known-folder redirection (OneDrive Desktop) is followed by the OS call.
    public static string UserDesktop() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    // Shortcut files directly on the desktop (.lnk and .url; desktop.ini and ordinary files are left alone).
    public static IReadOnlyList<string> List(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    // Copies every shortcut from `source` into `target`, silently overwriting a file of the same name (a read-only
    // destination is made writable first). One bad file never stops the others. `resolveTarget` (optional) maps a .lnk
    // to its target path; a shortcut whose target is inside a user profile is skipped (not copied) and reported.
    // .url files have no resolvable target here and are always copied.
    public static DesktopCopyResult Copy(string source, string target, Func<string, string?>? resolveTarget = null)
    {
        var copied = new List<string>();
        var failed = new List<string>();
        var perUser = new List<string>();

        if (string.Equals(WinPath.Clean(source), WinPath.Clean(target), StringComparison.OrdinalIgnoreCase))
            return new DesktopCopyResult(source, target, copied, failed, perUser,
                "The desktop folder and the Public Desktop folder are the same folder.");

        try { Directory.CreateDirectory(target); }
        catch (Exception ex)
        {
            return new DesktopCopyResult(source, target, copied, failed, perUser,
                "Could not create or open the Public Desktop folder: " + ex.Message);
        }

        foreach (var file in List(source))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(target, name);
            try
            {
                if (resolveTarget is not null && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    && IsPerUserPath(resolveTarget(file)))
                {
                    perUser.Add(name);
                    continue;
                }

                if (File.Exists(dest))
                {
                    var attrs = File.GetAttributes(dest);
                    if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
                }
                File.Copy(file, dest, true);
                copied.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(name + ": " + ex.Message);
            }
        }

        return new DesktopCopyResult(source, target, copied, failed, perUser, null);
    }

    // True for <drive>:\Users\<something>\... other than the shared Public profile.
    internal static bool IsPerUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = WinPath.Clean(Environment.ExpandEnvironmentVariables(path));
        if (p.Length < 10 || p[1] != ':') return false;
        var rest = p[2..];
        return rest.StartsWith(@"\Users\", StringComparison.OrdinalIgnoreCase)
               && !rest.StartsWith(@"\Users\Public\", StringComparison.OrdinalIgnoreCase)
               && !rest.Equals(@"\Users\Public", StringComparison.OrdinalIgnoreCase);
    }
}
