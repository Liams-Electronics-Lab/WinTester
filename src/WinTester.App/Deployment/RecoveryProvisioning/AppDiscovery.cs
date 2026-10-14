using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Items 3.7 / 3.8: find the installed desktop apps and derive, per app, the /i: include rules that make a capture carry
// the app (files, registration, Start-menu shortcut). Everything that touches the machine sits behind IRegistryReader,
// a shortcut resolver and a few Func parameters, so the rule logic is checked in the scratch harness with a fake
// registry. Nothing here has run against a real machine yet; the rule shapes copy the probe that worked (t5/t5b, C30).

public enum RegHive { Hklm, Hkcu }

public interface IRegistryReader
{
    bool KeyExists(RegHive hive, string path);
    IReadOnlyList<string> SubKeyNames(RegHive hive, string path);
    IReadOnlyList<string> ValueNames(RegHive hive, string path);
    object? GetValue(RegHive hive, string path, string name);   // name "" = the (Default) value
}

// HKLM is read in the 64-bit view; the 32-bit data is reached as SOFTWARE\WOW6432Node\..., which is also how the
// capture rules name it (C31).
public sealed class WinRegistryReader : IRegistryReader
{
    private static RegistryKey? Open(RegHive hive, string path)
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(hive == RegHive.Hklm ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Registry64);
            return path.Length == 0 ? null : b.OpenSubKey(path);
        }
        catch { return null; }
    }

    public bool KeyExists(RegHive hive, string path) { using var k = Open(hive, path); return k is not null; }

    public IReadOnlyList<string> SubKeyNames(RegHive hive, string path)
    {
        try { using var k = Open(hive, path); return k?.GetSubKeyNames() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> ValueNames(RegHive hive, string path)
    {
        try { using var k = Open(hive, path); return k?.GetValueNames() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public object? GetValue(RegHive hive, string path, string name)
    {
        try { using var k = Open(hive, path); return StripNuls(k?.GetValue(name)); }
        catch { return null; }
    }

    // A REG_SZ / REG_MULTI_SZ value can carry extra NUL characters after the terminator the framework removes
    // (seen on a capture run; the exact value was not recorded). A string is cut at its first NUL so no caller ever sees one; a NUL inside
    // a rule pattern makes the whole rules file unwritable.
    internal static object? StripNuls(object? v)
    {
        static string Cut(string s) { var i = s.IndexOf('\0'); return i < 0 ? s : s[..i]; }
        return v switch
        {
            string s => Cut(s),
            string[] a => a.Select(Cut).ToArray(),
            _ => v,
        };
    }
}

// Windows paths handled as plain strings so the logic gives the same answer on any host (the harness runs on Linux).
internal static class WinPath
{
    public static string Clean(string? p)
    {
        var s = (p ?? "").Trim().Trim('"').Trim().Replace('/', '\\');
        while (s.Length > 3 && s.EndsWith('\\')) s = s[..^1];
        return s;
    }

    public static bool IsRooted(string p) => p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    public static string? Parent(string p)
    {
        var i = p.LastIndexOf('\\');
        if (i < 2) return null;
        return i == 2 ? p[..3] : p[..i];
    }

    public static bool SameOrUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // Characters that have a meaning in a USMT pattern and are not escaped by this tool. A path with one of these is
    // never turned into a rule (it is reported instead). Parentheses are allowed: they are common ("Program Files
    // (x86)", "(x64)" in key names) and an unmatched pattern shows up in the log afterwards (see SkippedIncludeRoots).
    private static readonly char[] Special = { '[', ']', ',', '^', '%', '?', '*', '<', '>', '"', '|' };
    public static bool PatternSafe(string p) => p.IndexOfAny(Special) < 0 && !HasControlChars(p);

    // NUL and the other control characters cannot be written into the rules XML and are never part of a real path.
    public static bool HasControlChars(string p) => p.Any(c => c < ' ');

    // Start-menu shortcut paths may contain '[' and ']' (for example a vendor folder named "Game [Publisher]"). Those two
    // are written with USMT's '^' escape; every other special character still blocks the rule.
    private static readonly char[] SpecialExceptBrackets = { ',', '^', '%', '?', '*', '<', '>', '"', '|' };
    public static bool PatternSafeAfterBracketEscape(string p) => p.IndexOfAny(SpecialExceptBrackets) < 0 && !HasControlChars(p);
    public static bool HasBrackets(string p) => p.IndexOfAny(new[] { '[', ']' }) >= 0;
    public static string EscapeBrackets(string p) => p.Replace("[", "^[").Replace("]", "^]");
}

public sealed record InstalledApp(
    string KeyName,
    string Source,                 // "HKLM", "HKLM (32-bit)" or "HKCU"
    string? UninstallRel,          // key path below HKLM, null for HKCU entries
    string DisplayName,
    string Publisher,
    string Version,
    string? Folder,
    string FolderSource,           // InstallLocation, DisplayIcon, UninstallString or ""
    long SizeBytes,
    bool SizeMeasured,
    bool IsMsi,
    bool IsMicrosoft,
    bool IsDriverVendor,
    string? BlockReason)           // null = can be captured
{
    // Set by the app profiles (3.14): their names, the advice text, and a default tick that replaces the built-in guess.
    public IReadOnlyList<string> ProfileNames { get; init; } = Array.Empty<string>();
    public string? Advice { get; init; }
    public bool? DefaultOverride { get; init; }

    // 3.18: an app the operator added by picking its folder. It has no Uninstall entry (UninstallRel is null), so it is never
    // listed in Settings > Apps after a reset, but everything that points into its folder is carried like for any other app.
    public const string CustomSource = "custom";
    public bool IsCustom => Source == CustomSource;

    public bool Capturable => BlockReason is null;
    public bool IsDotNetRuntime => RuntimeRules.IsDotNetRuntime(DisplayName);
    // .NET runtimes are ticked by default even though Microsoft ships them: apps break without them and Reset does not bring them back.
    public bool DefaultSelected => Capturable && (DefaultOverride ?? (IsDotNetRuntime || (!IsMicrosoft && !IsDriverVendor)));
    public string OffByDefaultReason => DefaultOverride == false ? "set to off by an app profile" : IsDotNetRuntime ? "" : IsMicrosoft ? "Microsoft or Windows component (Windows ships or reinstalls these)"
        : IsDriverVendor ? "driver or OEM component (Windows restores inbox/INF drivers itself)" : "";
}

public static class AppDiscovery
{
    private static readonly (RegHive Hive, string Root, string Source)[] Roots =
    {
        (RegHive.Hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM"),
        (RegHive.Hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM (32-bit)"),
        (RegHive.Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", "HKCU"),
    };

    private static readonly string[] UpdateTypes = { "Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack" };

    // Names of vendors whose entries are drivers/OEM tooling. Heuristic, off by default only (the operator can tick them).
    private static readonly string[] DriverVendors =
        { "nvidia", "intel", "advanced micro devices", "amd", "realtek", "qualcomm", "broadcom", "mediatek", "synaptics" };

    public static IReadOnlyList<InstalledApp> Discover(IRegistryReader reg, string systemDrive, Func<string, bool> dirExists,
        Func<string, long>? measureFolder, CancellationToken ct)
    {
        var sd = WinPath.Clean(systemDrive);
        var list = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MsiIndex? msi = null;

        foreach (var (hive, root, source) in Roots)
        {
            foreach (var key in reg.SubKeyNames(hive, root))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rel = root + "\\" + key;
                    var name = Str(reg.GetValue(hive, rel, "DisplayName")).Trim();
                    if (name.Length == 0) continue;
                    if (Int(reg.GetValue(hive, rel, "SystemComponent")) == 1) continue;
                    if (reg.GetValue(hive, rel, "ParentKeyName") is not null) continue;
                    var release = Str(reg.GetValue(hive, rel, "ReleaseType"));
                    if (UpdateTypes.Contains(release, StringComparer.OrdinalIgnoreCase)) continue;

                    var version = Str(reg.GetValue(hive, rel, "DisplayVersion")).Trim();
                    if (!seen.Add(name + "|" + version)) continue;

                    var publisher = Str(reg.GetValue(hive, rel, "Publisher")).Trim();
                    var isMsi = Int(reg.GetValue(hive, rel, "WindowsInstaller")) == 1;
                    var kb = Int(reg.GetValue(hive, rel, "EstimatedSize"));

                    var (folder, how, reason) = ResolveFolder(reg, hive, rel, sd, dirExists);
                    // MSI apps often record no folder in their Uninstall entry; Windows Installer knows where the files went
                    if (folder is null && hive == RegHive.Hklm && MsiCodes.Pack(key) is { } packedCode)
                    {
                        msi ??= MsiIndex.Build(reg, ct);
                        var alt = MsiRules.InstallFolder(reg, msi, packedCode, sd);
                        if (alt is not null) { folder = WinPath.Clean(alt); how = "Windows Installer"; reason = CheckFolder(folder, sd, dirExists); }
                    }
                    if (hive == RegHive.Hkcu)
                        reason = "Per-user install: it lives in a user profile, which this package type does not carry.";

                    long size = kb > 0 ? kb * 1024L : 0;
                    var measured = false;
                    if (size == 0 && reason is null && folder is not null && measureFolder is not null)
                    {
                        size = measureFolder(folder);
                        measured = true;
                    }

                    var ms = IsMicrosoftEntry(publisher, name);
                    var drv = DriverVendors.Any(v => publisher.StartsWith(v, StringComparison.OrdinalIgnoreCase));

                    list.Add(new InstalledApp(key, source, hive == RegHive.Hklm ? rel : null, name, publisher, version,
                        folder, how, size, measured, isMsi, ms, drv, reason));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* one unreadable entry must not hide the rest */ }
            }
        }

        return list.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 3.18: an app entry for a folder picked by the operator. Returns the entry, or null with the reason the folder is refused
    // (same guards as a discovered app's folder, plus game libraries). `name` is optional: the folder's own name is used.
    public static (InstalledApp? App, string? Refusal) FromFolder(string? folder, string? name, string systemDrive,
        Func<string, bool> dirExists, Func<string, long>? measureFolder)
    {
        var sd = WinPath.Clean(systemDrive);
        var f = WinPath.Clean(folder);
        if (f.Length == 0) return (null, "No folder was chosen.");
        var bad = CheckFolder(f, sd, dirExists) ?? GameLibraries.Member(f, sd);
        if (bad is not null) return (null, bad);

        var leaf = f[(f.LastIndexOf('\\') + 1)..];
        var shown = new string((name ?? "").Where(c => c >= ' ').ToArray()).Trim();
        if (shown.Length == 0) shown = leaf;
        if (shown.Length > 80) shown = shown[..80];

        long size = 0;
        var measured = false;
        if (measureFolder is not null) { size = measureFolder(f); measured = true; }

        var app = new InstalledApp(InstalledApp.CustomSource + ":" + f, InstalledApp.CustomSource, null, shown, "", "", f,
            "chosen folder", size, measured, false, false, false, null);
        return (app, null);
    }

    internal static bool IsMicrosoftEntry(string publisher, string name) =>
        publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase);

    // Install folder comes from the Uninstall entry itself (S111: DOOM II lives outside Program Files, so no fixed
    // %ProgramFiles% assumption): InstallLocation first, then the folder of DisplayIcon, then of UninstallString.
    private static (string? Folder, string How, string? Reason) ResolveFolder(IRegistryReader reg, RegHive hive, string rel,
        string sd, Func<string, bool> dirExists)
    {
        string? firstFolder = null, firstHow = "", firstReason = null;

        foreach (var (label, isDir) in new[] { ("InstallLocation", true), ("DisplayIcon", false), ("UninstallString", false) })
        {
            var raw = Str(reg.GetValue(hive, rel, label));
            string? folder;
            if (isDir) folder = string.IsNullOrWhiteSpace(raw) ? null : WinPath.Clean(Environment.ExpandEnvironmentVariables(raw));
            else
            {
                var exe = FirstPath(raw);
                folder = exe is null ? null : WinPath.Parent(WinPath.Clean(exe));
            }
            if (string.IsNullOrEmpty(folder)) continue;

            var reason = CheckFolder(folder, sd, dirExists);
            if (reason is null) return (folder, label, null);
            if (firstFolder is null) { firstFolder = folder; firstHow = label; firstReason = reason; }
        }

        return firstFolder is null
            ? (null, "", "The installer recorded no install folder.")
            : (firstFolder, firstHow, firstReason);
    }

    // First file path inside a command-like value: "\"C:\\x\\a.exe\" /s", "C:\\x\\a.exe,0", "C:\\x\\a.exe /s".
    internal static string? FirstPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) return s[1..end];
            s = s[1..];
        }
        var m = Regex.Match(s, @"^(.*?\.(exe|ico|dll|msi|cmd|bat))(?=[\s,]|$)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        return s.Trim();
    }

    // Why a folder must not become an include rule, or null when it is fine.
    public static string? CheckFolder(string folder, string sd, Func<string, bool> dirExists)
    {
        var f = WinPath.Clean(folder);
        if (!WinPath.IsRooted(f)) return "The recorded install folder is not a full path.";
        if (f.Length <= 3) return "The recorded install folder is a whole drive.";
        if (!f.StartsWith(sd + "\\", StringComparison.OrdinalIgnoreCase))
            return $"Installed outside the system drive ({f[..2]}); only {sd} is captured.";

        string[] exact =
        {
            sd + @"\Windows", sd + @"\Program Files", sd + @"\Program Files (x86)", sd + @"\ProgramData", sd + @"\Users",
            sd + @"\Program Files\Common Files", sd + @"\Program Files (x86)\Common Files"
        };
        if (exact.Any(e => f.Equals(e, StringComparison.OrdinalIgnoreCase)))
            return "The recorded install folder is a shared system folder, too broad to capture.";

        if (WinPath.SameOrUnder(f, sd + @"\Windows")) return "Lives inside the Windows folder (OS-owned).";
        if (WinPath.SameOrUnder(f, sd + @"\Users")) return "Per-user install: it lives in a user profile, which this package type does not carry.";
        if (WinPath.SameOrUnder(f, sd + @"\Program Files\WindowsApps")) return "Store (modern) app: not captured by this package type.";
        if (WinPath.SameOrUnder(f, sd + @"\Recovery") || WinPath.SameOrUnder(f, sd + @"\System Volume Information")
            || WinPath.SameOrUnder(f, sd + @"\" + RunWorkspace.RootName)) return "System-managed folder.";
        if (!WinPath.PatternSafe(f)) return "The path contains characters that a capture rule cannot express safely.";
        if (!dirExists(f)) return "The recorded install folder does not exist.";
        return null;
    }

    internal static string Str(object? o) => o switch { string s => s, null => "", _ => o.ToString() ?? "" };
    internal static int Int(object? o) => o switch { int i => i, string s when int.TryParse(s, out var j) => j, _ => 0 };
}

// ---------------------------------------------------------------------------------------------------------------------
// One pass over the places an app registers itself outside its own Uninstall key. Built once per scan, matched per app.

public enum RefKind { AppPath, Client, Class, Service, RegisteredApp, ShellExt, Com }

public sealed record RegRef(RefKind Kind, string RulePath, string? ValueName, IReadOnlyList<string> Data, bool HasSubKeys)
{
    // ShellExt and Com only: the ready-made rule patterns (handler or class keys and what hangs off them) and a display name.
    public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();
    public string Label { get; init; } = "";
}

public sealed class SystemRegistryIndex
{
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private static readonly string[] ClassesSkip = { "CLSID", "Interface", "TypeLib", "Installer", "AppID", "Wow6432Node", "MIME", "Local Settings" };

    public IReadOnlyList<RegRef> Refs { get; }
    public MsiIndex Msi { get; }
    private SystemRegistryIndex(IReadOnlyList<RegRef> refs, MsiIndex msi) { Refs = refs; Msi = msi; }

    public static SystemRegistryIndex Build(IRegistryReader reg, CancellationToken ct, int maxClassKeys = 100_000)
    {
        var refs = new List<RegRef>();
        var h = RegHive.Hklm;

        // App Paths (both views)
        foreach (var root in new[] { AppPaths, AppPaths.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\") })
            foreach (var n in reg.SubKeyNames(h, root))
            {
                ct.ThrowIfCancellationRequested();
                var p = root + "\\" + n;
                refs.Add(new RegRef(RefKind.AppPath, @"HKLM\" + p, null,
                    Strings(reg.GetValue(h, p, ""), reg.GetValue(h, p, "Path")), reg.SubKeyNames(h, p).Count > 0));
            }

        // Clients\<category>\<client> (default browser, mail, media...) with every string value below the client key
        foreach (var root in new[] { @"SOFTWARE\Clients", @"SOFTWARE\WOW6432Node\Clients" })
            foreach (var cat in reg.SubKeyNames(h, root))
                foreach (var client in reg.SubKeyNames(h, root + "\\" + cat))
                {
                    ct.ThrowIfCancellationRequested();
                    var p = root + "\\" + cat + "\\" + client;
                    var data = new List<string>();
                    CollectStrings(reg, h, p, 0, data);
                    refs.Add(new RegRef(RefKind.Client, @"HKLM\" + p, null, data, true));
                }

        // Classes: ProgIDs only (top level), judged by their open command and icon; extension keys (.ext) are not scanned
        {
            const string root = @"SOFTWARE\Classes";
            var count = 0;
            foreach (var n in reg.SubKeyNames(h, root))
            {
                ct.ThrowIfCancellationRequested();
                if (n.StartsWith('.') || ClassesSkip.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                if (++count > maxClassKeys) break;
                var p = root + "\\" + n;
                if (n.Equals("Applications", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var exe in reg.SubKeyNames(h, p))
                    {
                        var ep = p + "\\" + exe;
                        var d = Strings(reg.GetValue(h, ep + @"\shell\open\command", ""));
                        if (d.Count > 0) refs.Add(new RegRef(RefKind.Class, @"HKLM\" + ep, null, d, true));
                    }
                    continue;
                }
                var data = Strings(reg.GetValue(h, p + @"\shell\open\command", ""), reg.GetValue(h, p + @"\DefaultIcon", ""));
                if (data.Count > 0) refs.Add(new RegRef(RefKind.Class, @"HKLM\" + p, null, data, true));
            }
        }

        // Services (reported only, never captured: untested and a driver service is a boot risk)
        {
            const string root = @"SYSTEM\CurrentControlSet\Services";
            foreach (var n in reg.SubKeyNames(h, root))
            {
                ct.ThrowIfCancellationRequested();
                var d = Strings(reg.GetValue(h, root + "\\" + n, "ImagePath"));
                if (d.Count > 0) refs.Add(new RegRef(RefKind.Service, @"HKLM\" + root + "\\" + n, n, d, false));
            }
        }

        // RegisteredApplications values point at a Capabilities key under Clients or the vendor key
        foreach (var root in new[] { @"SOFTWARE\RegisteredApplications", @"SOFTWARE\WOW6432Node\RegisteredApplications" })
            foreach (var v in reg.ValueNames(h, root))
            {
                var d = Strings(reg.GetValue(h, root, v));
                if (v.Length > 0 && d.Count > 0) refs.Add(new RegRef(RefKind.RegisteredApp, @"HKLM\" + root, v, d, false));
            }

        // Shell extensions (context-menu / drag-and-drop / property-sheet handlers), judged by their in-process server path
        refs.AddRange(ShellExtRules.Scan(reg, ct));

        // COM classes (CLSID, AppID, TypeLib, Interface, ProgID), judged by their server path
        refs.AddRange(ComRules.Scan(reg, ct));

        return new SystemRegistryIndex(refs, MsiIndex.Build(reg, ct));
    }

    private static void CollectStrings(IRegistryReader reg, RegHive h, string path, int depth, List<string> into)
    {
        foreach (var v in reg.ValueNames(h, path))
            if (reg.GetValue(h, path, v) is string s && s.Length > 0) into.Add(s);
        if (depth >= 4) return;
        foreach (var sub in reg.SubKeyNames(h, path)) CollectStrings(reg, h, path + "\\" + sub, depth + 1, into);
    }

    private static List<string> Strings(params object?[] values)
    {
        var l = new List<string>();
        foreach (var v in values) if (v is string s && s.Length > 0) l.Add(s);
        return l;
    }
}

// ---------------------------------------------------------------------------------------------------------------------

public sealed record ShortcutInfo(string Dir, string FileName, string? Target);

public static class StartMenuScanner
{
    // All-users Start menu only (user profiles are out of scope, C14). resolveTarget returns the .lnk target or null.
    public static IReadOnlyList<ShortcutInfo> Scan(string root, Func<string, string?> resolveTarget, CancellationToken ct)
    {
        var list = new List<ShortcutInfo>();
        try
        {
            if (!Directory.Exists(root)) return list;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(root, "*.lnk", opts))
            {
                ct.ThrowIfCancellationRequested();
                var dir = Path.GetDirectoryName(f);
                if (dir is null) continue;
                list.Add(new ShortcutInfo(dir, Path.GetFileName(f), resolveTarget(f)));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* partial list is fine */ }
        return list;
    }
}

// Reads a .lnk target through the WScript.Shell COM object (no extra package needed).
public sealed class LnkResolver
{
    private object? _shell;
    private bool _failed;

    public string? Resolve(string lnkPath)
    {
        if (_failed) return null;
        try
        {
            _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic sh = _shell!;
            dynamic lnk = sh.CreateShortcut(lnkPath);
            string target = lnk.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            _failed = _shell is null;   // no COM shell at all: stop trying; a single bad .lnk is just skipped
            return null;
        }
    }
}

public static class FolderSize
{
    // Sum of file sizes below `folder`, capped (entries and time) so a huge game folder cannot stall the scan.
    public static long Measure(string folder)
    {
        long total = 0;
        var seen = 0;
        var stop = DateTime.UtcNow.AddSeconds(8);
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
            {
                total += f.Length;
                if (++seen >= 200_000 || DateTime.UtcNow > stop) break;
            }
        }
        catch { }
        return total;
    }
}
