using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Item 3.14: app profiles and policy. A profile is a small JSON rule that matches an installed app (by display name and/or
// publisher) and adjusts how it is captured: blocks it, narrows its folder, adds folders or registry keys, excludes parts of
// its folder, sets the default tick or shows advice. Built-in profiles ship in this file; the operator can add to or replace
// them with an AppProfiles.json next to the exe (operator profiles are listed first, so their setting wins), so a wrong rule is
// fixed without a rebuild. Every folder and key a profile names goes through the same guards as generated rules.
//
// Game libraries (GameLibraries below) are separate from profiles: they are policy, never captured, on every machine.
//
// Two profile features worth knowing (S127):
//   * "allow": true lifts a licensing block ("unsupported") that a profile further down the list would set, so an app that a
//     general rule blocks (Adobe) can be allowed by a narrower one (Acrobat Reader), and the operator can do the same from
//     AppProfiles.json. The first matching profile that either sets "unsupported" or "allow" decides.
//   * extraFolders are vendor folders that sit outside the app's own folder (Google Update beside Chrome, Adobe's ARM updater,
//     Oracle's Java folders). Besides being carried, they count as part of the app when registrations are matched, so a
//     service, scheduled task, Run entry, COM class or shortcut that points into such a folder is carried with the app.

public sealed class AppProfile
{
    public string Name { get; set; } = "";
    public string? MatchName { get; set; }            // "*" wildcard, whole display name, case-insensitive
    public string? MatchPublisher { get; set; }       // same, against the publisher
    public string? WhenKeyExists { get; set; }        // optional extra condition: HKLM key path below HKLM must exist
    public string? Unsupported { get; set; }          // reason text: the app is listed but cannot be ticked
    public string? FolderOverride { get; set; }       // "{folder}" stands for the recorded install folder
    public List<string> ExtraFolders { get; set; } = new();     // absolute folders (a few %VARIABLES% allowed)
    public List<string> ExtraRegistry { get; set; } = new();    // "HKLM\SOFTWARE\Vendor\Product"
    public List<string> ExcludePatterns { get; set; } = new();  // relative to the app folder: "steamapps", "cache\*.tmp"
    public bool? DefaultSelected { get; set; }
    public string? Advice { get; set; }
    public bool? Allow { get; set; }                  // true: not unsupported, even if a later (lower) profile says so

    // True for the profiles that ship in this file. Not read from JSON. A built-in vendor folder that is not on this PC is not
    // worth a note (most PCs lack most of them); a folder an operator wrote that is missing is reported, it may be a typo.
    [JsonIgnore] public bool BuiltIn { get; set; }
}

public sealed class AppProfileSet
{
    public IReadOnlyList<AppProfile> Profiles { get; }
    public IReadOnlyList<string> Notes { get; }
    public AppProfileSet(IReadOnlyList<AppProfile> profiles, IReadOnlyList<string> notes) { Profiles = profiles; Notes = notes; }

    public const string FileName = "AppProfiles.json";

    private const string BuiltInJson = """
    {
      "profiles": [
        { "name": "Steam", "matchName": "Steam", "matchPublisher": "Valve*", "excludePatterns": [ "steamapps" ],
          "advice": "The Steam launcher is captured; its game library (steamapps) never is." },
        { "name": "Epic Games Launcher", "matchName": "Epic Games Launcher", "folderOverride": "{folder}\\Launcher",
          "advice": "Only the Epic launcher folder is captured; games are never captured." },
        { "name": "GOG Galaxy", "matchName": "GOG GALAXY*", "excludePatterns": [ "Games" ],
          "advice": "The GOG Galaxy launcher is captured; its Games folder never is." },
        { "name": "Ubisoft Connect", "matchName": "Ubisoft Connect*", "excludePatterns": [ "games" ],
          "advice": "The Ubisoft launcher is captured; its games folder never is." },
        { "name": "Uplay", "matchName": "Uplay*", "excludePatterns": [ "games" ],
          "advice": "The Ubisoft launcher is captured; its games folder never is." },
        { "name": "Microsoft 365", "matchName": "Microsoft 365*", "matchPublisher": "Microsoft*",
          "unsupported": "Licensed or Click-to-Run: reinstall it after the reset." },
        { "name": "Microsoft Office", "matchName": "Microsoft Office*", "matchPublisher": "Microsoft*",
          "unsupported": "Licensed or Click-to-Run: reinstall it after the reset." },
        { "name": "Office Click-to-Run", "matchName": "*Office*", "matchPublisher": "Microsoft*",
          "whenKeyExists": "SOFTWARE\\Microsoft\\Office\\ClickToRun",
          "unsupported": "Licensed or Click-to-Run: reinstall it after the reset." },
        // Adobe's free reader is allowed (it needs no licence); it must come before the general Adobe rule below.
        { "name": "Adobe Acrobat Reader (free)", "matchName": "Adobe Acrobat Reader*", "matchPublisher": "Adobe*", "allow": true,
          "extraFolders": [ "%ProgramFiles(x86)%\\Common Files\\Adobe\\ARM" ],
          "advice": "Adobe Acrobat Reader (the free one) is carried, with its update helper when that is installed. Adobe's paid products are not." },
        // Newer Adobe installers list Pro and Reader under one entry name; it cannot be told apart, so it stays blocked.
        { "name": "Adobe Acrobat (combined entry)", "matchName": "Adobe Acrobat (*", "matchPublisher": "Adobe*",
          "unsupported": "Licensed (Adobe): this entry name is used by Acrobat Pro and by Reader. Add an AppProfiles.json entry with \"allow\": true to carry it." },
        { "name": "Adobe", "matchPublisher": "Adobe*",
          "unsupported": "Licensed (Adobe): reinstall it after the reset." },

        // Vendor folders and keys outside the app's own folder (S127). All unverified on a real machine; a folder that is not on
        // this PC is skipped without a note, and a registry key that is missing is skipped the same way.
        { "name": "Google Chrome updater", "matchName": "Google Chrome*", "matchPublisher": "Google*",
          "extraFolders": [ "%ProgramFiles(x86)%\\Google\\Update", "%ProgramFiles(x86)%\\Google\\GoogleUpdater" ],
          "advice": "Chrome's update helper (Google Update) is carried with it when installed, with its service and tasks." },
        { "name": "Java (Oracle)", "matchName": "Java*", "matchPublisher": "Oracle*",
          "extraFolders": [ "%ProgramFiles%\\Common Files\\Oracle\\Java", "%ProgramFiles(x86)%\\Common Files\\Oracle\\Java", "%ProgramData%\\Oracle\\Java" ],
          "extraRegistry": [ "HKLM\\SOFTWARE\\JavaSoft", "HKLM\\SOFTWARE\\WOW6432Node\\JavaSoft" ],
          "advice": "Java's shared Oracle folders and its JavaSoft registration are carried when present, so programs can find the runtime." },
        { "name": "Git for Windows", "matchName": "Git*", "matchPublisher": "The Git Development Community",
          "extraRegistry": [ "HKLM\\SOFTWARE\\GitForWindows" ] }
      ]
    }
    """;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppProfileSet BuiltIn { get; } = Parse(BuiltInJson, null, new List<string>());

    // Built-ins, preceded by the operator's file when one sits next to the exe. A file that cannot be read is ignored with a note.
    public static AppProfileSet LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, FileName));

    public static AppProfileSet Load(string? userFile)
    {
        var notes = new List<string>();
        string? userJson = null;
        if (!string.IsNullOrEmpty(userFile) && File.Exists(userFile))
        {
            try { userJson = File.ReadAllText(userFile); }
            catch (Exception ex) { notes.Add(FileName + " could not be read and is ignored: " + ex.Message); }
        }
        var user = userJson is null ? new AppProfileSet(Array.Empty<AppProfile>(), notes) : Parse(userJson, FileName, notes);
        var all = user.Profiles.Concat(BuiltIn.Profiles).ToList();
        return new AppProfileSet(all, user.Notes);
    }

    // `label` names the source in notes (null for the built-in text, whose own problems would be a bug).
    internal static AppProfileSet Parse(string json, string? label, List<string> notes)
    {
        var list = new List<AppProfile>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("profiles", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                notes.Add($"{label ?? "Profiles"}: expected an object with a \"profiles\" list; ignored.");
                return new AppProfileSet(list, notes);
            }
            var i = 0;
            foreach (var el in arr.EnumerateArray())
            {
                i++;
                AppProfile? p;
                try { p = el.Deserialize<AppProfile>(Json); }
                catch (JsonException ex) { notes.Add($"{label ?? "Profiles"}: profile {i} skipped ({ex.Message})"); continue; }
                if (p is null) continue;
                var problem = Validate(p);
                if (problem is not null) { notes.Add($"{label ?? "Profiles"}: profile {i} ({p.Name}) skipped: {problem}"); continue; }
                p.BuiltIn = label is null;
                list.Add(p);
            }
        }
        catch (JsonException ex)
        {
            notes.Add($"{label ?? "Profiles"} is not valid JSON and is ignored: {ex.Message}");
            list.Clear();
        }
        return new AppProfileSet(list, notes);
    }

    private static string? Validate(AppProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return "no name";
        if (string.IsNullOrWhiteSpace(p.MatchName) && string.IsNullOrWhiteSpace(p.MatchPublisher))
            return "needs matchName or matchPublisher";
        if (p.MatchName is "*" || p.MatchPublisher is "*") return "a lone * would match every app";
        return null;
    }
}

// What the profiles decided for one app, carried into AppFootprint.Build.
public sealed record AppProfileEffects(
    IReadOnlyList<string> ExcludePatterns,      // USMT patterns, ready for unconditionalExclude
    IReadOnlyList<string> ExcludedItems,        // short labels for the list, log and manifest
    IReadOnlyList<string> ExtraFilePatterns,
    IReadOnlyList<string> ExtraRegistryPatterns,
    IReadOnlyList<string> Notes)
{
    // The extra folders that passed the guards, as paths. AppFootprint treats them as part of the app when it matches services,
    // tasks, Run entries, COM classes and shortcuts.
    public IReadOnlyList<string> ExtraRoots { get; init; } = Array.Empty<string>();
}

public sealed record ProfiledApp(InstalledApp App, AppProfileEffects Effects);

public static class AppProfiles
{
    public static readonly AppProfileEffects None = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<string>());

    // Order of work per app: profile blocks, folder override, game-library membership, then (only if still capturable) the
    // exclusions, extra folders and extra registry keys.
    public static IReadOnlyList<ProfiledApp> Apply(IReadOnlyList<InstalledApp> apps, AppProfileSet set, IRegistryReader reg,
        string systemDrive, Func<string, bool> dirExists)
    {
        var sd = WinPath.Clean(systemDrive);
        var result = new List<ProfiledApp>(apps.Count);
        foreach (var original in apps)
        {
            var app = original;
            var matched = set.Profiles.Where(p => Matches(p, app, reg)).ToList();
            var notes = new List<string>();

            if (matched.Count > 0)
                app = app with { ProfileNames = matched.Select(p => p.Name).ToList() };

            // The first matched profile that says "unsupported" or "allow" decides (operator profiles come first, then the built-ins
            // in file order). "unsupported" wins when one profile sets both.
            string? unsupported = null;
            foreach (var mp in matched)
            {
                if (!string.IsNullOrWhiteSpace(mp.Unsupported)) { unsupported = mp.Unsupported; break; }
                if (mp.Allow == true) break;
            }
            if (unsupported is not null && app.Capturable) app = app with { BlockReason = unsupported };

            if (app.Capturable && app.Folder is not null)
            {
                var ov = matched.Select(p => p.FolderOverride).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (ov is not null)
                {
                    var candidate = WinPath.Clean(ov.Replace("{folder}", app.Folder, StringComparison.OrdinalIgnoreCase));
                    if (!candidate.Equals(app.Folder, StringComparison.OrdinalIgnoreCase)
                        && AppDiscovery.CheckFolder(candidate, sd, dirExists) is null)
                        app = app with { Folder = candidate, FolderSource = "profile" };
                }
            }

            if (app.Capturable && app.Folder is not null && GameLibraries.Member(app.Folder, sd) is { } lib)
                app = app with { BlockReason = lib };

            var fx = None;
            if (app.Capturable && app.Folder is not null)
            {
                fx = Effects(app, app.Folder, matched, reg, sd, dirExists, notes);
                var adv = matched.Select(p => p.Advice).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                var def = matched.Select(p => p.DefaultSelected).FirstOrDefault(b => b is not null);
                app = app with { Advice = adv, DefaultOverride = def };
            }
            else if (matched.Count > 0)
            {
                app = app with { Advice = matched.Select(p => p.Advice).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) };
            }
            result.Add(new ProfiledApp(app, fx));
        }
        return result;
    }

    private static AppProfileEffects Effects(InstalledApp app, string folder, List<AppProfile> matched, IRegistryReader reg,
        string sd, Func<string, bool> dirExists, List<string> notes)
    {
        var excl = new List<string>();
        var labels = new List<string>();
        var files = new List<string>();
        var regs = new List<string>();

        var rels = matched.SelectMany(p => p.ExcludePatterns).ToList();
        // Any captured folder that holds a steamapps directory has a Steam library inside it, whatever the app is called.
        if (!rels.Contains("steamapps", StringComparer.OrdinalIgnoreCase) && dirExists(folder + @"\steamapps")) rels.Add("steamapps");

        foreach (var rel in rels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pattern = ExcludeFor(folder, rel, sd, out var why);
            if (pattern is null) { notes.Add($"Profile exclusion \"{rel}\" skipped: {why}"); continue; }
            // Nothing to exclude when that folder is not on this PC. This also keeps the %ProgramFiles(x86)% form (never yet seen
            // working in an exclusion) out of every capture that has no such folder.
            if (!ExcludeTargetExists(folder, rel, dirExists)) continue;
            excl.Add(pattern);
            labels.Add(rel.TrimEnd('\\'));
        }

        var roots = new List<string>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mp in matched)
            foreach (var raw in mp.ExtraFolders)
            {
                if (!seenFolders.Add(raw.Trim())) continue;
                var path = WinPath.Clean(ExpandVars(raw, sd));
                string? bad;
                if (path.Contains('%')) bad = "contains a variable that is not supported";
                else if (mp.BuiltIn && !dirExists(path)) continue;       // a built-in vendor folder that this PC does not have
                else bad = AppDiscovery.CheckFolder(path, sd, dirExists) ?? GameLibraries.Member(path, sd);
                if (bad is not null) { notes.Add($"Profile folder \"{raw}\" skipped: {bad}"); continue; }
                files.Add(AppFootprint.FolderPattern(path, sd));
                roots.Add(path);
            }

        foreach (var raw in matched.SelectMany(p => p.ExtraRegistry).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = raw.Trim().TrimEnd('\\');
            if (!key.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) || key.Length <= 5)
            { notes.Add($"Profile registry key \"{raw}\" skipped: it must start with HKLM\\"); continue; }
            var rel = key[5..];
            if (!WinPath.PatternSafe(rel)) { notes.Add($"Profile registry key \"{raw}\" skipped: special characters"); continue; }
            if (!reg.KeyExists(RegHive.Hklm, rel)) continue;   // an app that is not here simply has nothing to carry
            regs.Add(key + " [*]");
            if (reg.SubKeyNames(RegHive.Hklm, rel).Count > 0) regs.Add(key + @"\* [*]");
        }

        return new AppProfileEffects(excl, labels, files, regs, notes) { ExtraRoots = roots };
    }

    // True when what an exclusion names exists below the app folder. A folder rule needs the folder; a file rule ("*.tmp",
    // "logs\*.log") needs the folder it sits in; a rule with a wildcard folder segment cannot be checked and counts as present.
    internal static bool ExcludeTargetExists(string folder, string rel, Func<string, bool> dirExists)
    {
        var r = (rel ?? "").Trim().Replace('/', '\\').Trim('\\');
        if (r.Length == 0) return false;
        var last = r.LastIndexOf('\\');
        var leaf = last < 0 ? r : r[(last + 1)..];
        var isFileSpec = leaf.Contains('*') || leaf.Contains('.');
        var dirPart = isFileSpec ? (last < 0 ? "" : r[..last]) : r;
        if (dirPart.Contains('*')) return true;
        return dirExists(dirPart.Length == 0 ? folder : folder + "\\" + dirPart);
    }

    // "<folder>\<rel>" as an exclusion pattern. A relative part with a dot or a wildcard in its last segment is a file spec
    // (same rule as the Extra paths box), anything else is a folder. Folders under Program Files (x86) use the variable form.
    internal static string? ExcludeFor(string folder, string rel, string sd, out string? why)
    {
        why = null;
        var r = (rel ?? "").Trim().Replace('/', '\\').Trim('\\');
        if (r.Length == 0) { why = "empty"; return null; }
        if (WinPath.IsRooted(r) || r.StartsWith('%') || r.Split('\\').Any(s => s == ".." || s == ".")) { why = "must be relative to the app folder"; return null; }
        if (r.IndexOfAny(new[] { '[', ']', ',', '^', '%', '?', '<', '>', '"', '|' }) >= 0 || WinPath.HasControlChars(r))
        { why = "contains characters a rule cannot express"; return null; }
        var full = AppFootprintFolder(folder, sd) + "\\" + r;
        var converted = RulesXmlBuilder.TryConvertCustom(full, out var err);
        if (converted is null) why = err ?? "not a usable path";
        return converted;
    }

    private static string AppFootprintFolder(string folder, string sd)
    {
        var x86 = sd + @"\Program Files (x86)";
        return WinPath.SameOrUnder(folder, x86) ? "%ProgramFiles(x86)%" + folder[x86.Length..] : folder;
    }

    internal static string ExpandVars(string path, string sd)
    {
        var p = path.Trim();
        p = Regex.Replace(p, @"%ProgramFiles\(x86\)%", sd + @"\Program Files (x86)", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%ProgramFiles%", sd + @"\Program Files", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%ProgramData%", sd + @"\ProgramData", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, "%SystemDrive%", sd, RegexOptions.IgnoreCase);
        return p;
    }

    internal static bool Matches(AppProfile p, InstalledApp app, IRegistryReader reg)
    {
        if (string.IsNullOrWhiteSpace(p.MatchName) && string.IsNullOrWhiteSpace(p.MatchPublisher)) return false;
        if (!string.IsNullOrWhiteSpace(p.MatchName) && !Glob(p.MatchName!, app.DisplayName)) return false;
        if (!string.IsNullOrWhiteSpace(p.MatchPublisher) && !Glob(p.MatchPublisher!, app.Publisher)) return false;
        if (!string.IsNullOrWhiteSpace(p.WhenKeyExists) && !reg.KeyExists(RegHive.Hklm, p.WhenKeyExists!.Trim())) return false;
        return true;
    }

    internal static bool Glob(string pattern, string text) =>
        Regex.IsMatch(text ?? "", "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
}

// Game libraries are never captured (operator, S122/S126). Launchers may be; the games under them are not. Two effects:
//   * ExclusionPatterns: always-on exclusions for the default library folders, so a ticked launcher never drags a library in;
//   * Member: an installed entry whose folder lies inside a library is listed as "cannot be captured".
// Epic is the exception to the first: its launcher sits inside the same Epic Games folder as the games, so a folder exclusion
// would remove the launcher too. Epic is handled by the folder override in its profile (only the Launcher folder is captured)
// and by Member (everything else under Epic Games is library content).
public static class GameLibraries
{
    private sealed record Library(string Name, string RelativeRoot, string? KeepSubfolder = null);

    private static readonly Library[] Libraries =
    {
        new("Steam", @"Program Files (x86)\Steam\steamapps"),
        new("Epic Games", @"Program Files\Epic Games", "Launcher"),
        new("Epic Games", @"Program Files (x86)\Epic Games", "Launcher"),
        new("Xbox", "XboxGames"),
        new("Xbox (Store apps)", @"Program Files\WindowsApps"),
        new("EA app", @"Program Files\EA Games"),
        new("Ubisoft Connect", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games"),
        new("GOG Galaxy", @"Program Files (x86)\GOG Galaxy\Games"),
    };

    // The same libraries when the launcher was installed somewhere else: matched by the folder names inside the path.
    private static readonly (string Name, Regex Rx)[] Anywhere =
    {
        ("Steam", new Regex(@"\\steamapps(\\|$)", RegexOptions.IgnoreCase)),
        ("GOG Galaxy", new Regex(@"\\GOG Galaxy\\Games(\\|$)", RegexOptions.IgnoreCase)),
        ("Ubisoft Connect", new Regex(@"\\Ubisoft Game Launcher\\games(\\|$)", RegexOptions.IgnoreCase)),
    };

    // dirExists (S127): when given, only the library folders that exist on this PC are listed. The default folders are excluded as a
    // safety net, and an exclusion for a folder that is not there does nothing, so it is left out of the rules instead of being
    // carried into every capture (the %ProgramFiles(x86)% form is still unproven in an exclusion, 4.19 (e)).
    public static IReadOnlyList<string> ExclusionPatterns(string systemDrive, Func<string, bool>? dirExists = null)
    {
        var sd = WinPath.Clean(systemDrive);
        return Libraries.Where(l => l.KeepSubfolder is null && (dirExists is null || dirExists(sd + "\\" + l.RelativeRoot)))
            .Select(l => AppFootprint.FolderPattern(sd + "\\" + l.RelativeRoot, sd)).ToList();
    }

    public static IReadOnlyList<string> Describe() =>
        Libraries.Select(l => l.Name + ": " + l.RelativeRoot + (l.KeepSubfolder is null ? "" : " (all but " + l.KeepSubfolder + ")")).ToList();

    // The reason text when `folder` lies inside a game library, otherwise null.
    public static string? Member(string folder, string systemDrive)
    {
        var sd = WinPath.Clean(systemDrive);
        var f = WinPath.Clean(folder);
        foreach (var l in Libraries)
        {
            var root = sd + "\\" + l.RelativeRoot;
            if (!WinPath.SameOrUnder(f, root)) continue;
            if (l.KeepSubfolder is not null && WinPath.SameOrUnder(f, root + "\\" + l.KeepSubfolder)) continue;
            return Reason(l.Name);
        }
        foreach (var (name, rx) in Anywhere)
            if (rx.IsMatch(f)) return Reason(name);
        return null;
    }

    private static string Reason(string library) =>
        $"Game library content ({library}): game libraries are never captured, reinstall the game from its launcher.";
}
