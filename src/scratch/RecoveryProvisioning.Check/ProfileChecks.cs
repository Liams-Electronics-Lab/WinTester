using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 3.14: app profiles and game-library policy, against a fake registry. Called from Program.cs.
public static class ProfileChecks
{
    public static void Run(Action<bool, string> check)
    {
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        void V(string p, string n, object? v) { K(p); vals[p + "|" + n] = v; }
        var reg = new AppDiscoveryChecks.Fake(keys, vals);

        const string U64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string U32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        void App(string root, string key, string name, string pub, string folder)
        {
            V(root + "\\" + key, "DisplayName", name); V(root + "\\" + key, "Publisher", pub);
            V(root + "\\" + key, "InstallLocation", folder); V(root + "\\" + key, "DisplayVersion", "1.0");
        }

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files (x86)\Steam", @"C:\Program Files (x86)\Steam\steamapps", @"C:\Program Files (x86)\Steam\steamapps\common\Half",
            @"D:\Steam",
            @"C:\Program Files (x86)\Epic Games", @"C:\Program Files (x86)\Epic Games\Launcher",
            @"C:\Program Files\Epic Games\Fortnite", @"C:\Program Files (x86)\Epic Games\Epic Online Services",
            @"C:\Program Files (x86)\GOG Galaxy", @"C:\Program Files (x86)\GOG Galaxy\Games", @"C:\Program Files (x86)\GOG Galaxy\Games\Witcher",
            @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\FarCry",
            @"C:\Program Files\EA Games\Battlefield", @"C:\XboxGames\Forza", @"C:\GOG Games\DOOM II", @"C:\Tools\Custom Steam", @"C:\Tools\Custom Steam\steamapps",
            @"C:\Program Files\Microsoft Office", @"C:\Program Files\Adobe\Photoshop", @"C:\Program Files\OfficeLike", @"C:\Program Files\Plain",
            @"C:\ProgramData\Vendor\Data", @"C:\Program Files\Plain\Cache"
        };

        App(U32, "Steam", "Steam", "Valve Corporation", @"C:\Program Files (x86)\Steam");
        App(U32, "Epic", "Epic Games Launcher", "Epic Games, Inc.", @"C:\Program Files (x86)\Epic Games");
        App(U64, "EpicOnline", "Epic Online Services", "Epic Games, Inc.", @"C:\Program Files (x86)\Epic Games\Epic Online Services");
        App(U64, "Fortnite", "Fortnite", "Epic Games, Inc.", @"C:\Program Files\Epic Games\Fortnite");
        App(U32, "Galaxy", "GOG GALAXY", "GOG sp. z o.o.", @"C:\Program Files (x86)\GOG Galaxy");
        App(U32, "Witcher", "The Witcher", "GOG.com", @"C:\Program Files (x86)\GOG Galaxy\Games\Witcher");
        App(U64, "Doom", "DOOM II Enhanced", "GOG.com", @"C:\GOG Games\DOOM II");
        App(U32, "Ubi", "Ubisoft Connect", "Ubisoft", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher");
        App(U32, "FarCry", "Far Cry", "Ubisoft", @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\FarCry");
        App(U64, "BF", "Battlefield", "EA", @"C:\Program Files\EA Games\Battlefield");
        App(U64, "Forza", "Forza", "Microsoft", @"C:\XboxGames\Forza");
        App(U64, "CustomSteam", "Other launcher", "Someone", @"C:\Tools\Custom Steam");
        App(U64, "O365", "Microsoft 365 Apps for enterprise - en-us", "Microsoft Corporation", @"C:\Program Files\Microsoft Office");
        App(U64, "OfficeX", "Office Extras", "Microsoft Corporation", @"C:\Program Files\OfficeLike");
        App(U64, "Photoshop", "Adobe Photoshop 2026", "Adobe Inc.", @"C:\Program Files\Adobe\Photoshop");
        App(U64, "Plain", "Plain App", "Someone", @"C:\Program Files\Plain");
        V(@"SOFTWARE\Microsoft\Office\ClickToRun", "x", "1");
        V(@"SOFTWARE\Vendor\Plain", "k", "1"); V(@"SOFTWARE\Vendor\Plain\Sub", "k", "1");

        var tmp = Path.Combine(Path.GetTempPath(), "pf-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);

        // ---- built-in profiles and library policy through the scanner
        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None,
            null, null, AppProfileSet.BuiltIn);
        ScannedApp Get(string n) => res.Apps.First(a => a.App.DisplayName == n);

        var steam = Get("Steam");
        check(steam.Plan is not null && steam.Plan.ExcludePatterns.Contains(@"%ProgramFiles(x86)%\Steam\steamapps\* [*]"), "profile: Steam excludes steamapps (x86 variable form)");
        check(steam.Plan is not null && steam.Plan.ExcludedItems.Contains("steamapps") && steam.App.Advice is not null && steam.App.ProfileNames.Contains("Steam"),
            "profile: Steam shows its exclusion, advice and profile name");
        check(steam.Plan is not null && steam.Plan.FilePatterns.Contains(@"%ProgramFiles(x86)%\Steam\* [*]"), "profile: the Steam launcher folder is still captured");

        var epic = Get("Epic Games Launcher");
        check(epic.Plan is not null && epic.Plan.Folder == @"C:\Program Files (x86)\Epic Games\Launcher", "profile: Epic is narrowed to its Launcher folder");
        check(epic.Plan is not null && epic.Plan.FilePatterns.Contains(@"%ProgramFiles(x86)%\Epic Games\Launcher\* [*]")
              && !epic.Plan.FilePatterns.Contains(@"%ProgramFiles(x86)%\Epic Games\* [*]"), "profile: Epic does not include the Epic Games root");
        check(Get("Fortnite").Plan is null && Get("Fortnite").App.BlockReason!.Contains("Epic Games"), "library: an Epic game is blocked as library content");
        check(Get("Epic Online Services").Plan is null, "library: other Epic folders besides Launcher are blocked");

        check(Get("GOG GALAXY").Plan is not null && Get("GOG GALAXY").Plan!.ExcludePatterns.Contains(@"%ProgramFiles(x86)%\GOG Galaxy\Games\* [*]"), "profile: GOG Galaxy excludes its Games folder");
        check(Get("The Witcher").Plan is null && Get("The Witcher").App.BlockReason!.Contains("GOG Galaxy"), "library: a game inside GOG Galaxy\\Games is blocked");
        check(Get("DOOM II Enhanced").Plan is not null, "library: a GOG game outside the library folders (C:\\GOG Games) is unaffected");
        check(Get("Ubisoft Connect").Plan is not null && Get("Ubisoft Connect").Plan!.ExcludePatterns.Contains(@"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\games\* [*]"),
            "profile: Ubisoft Connect excludes its games folder");
        check(Get("Far Cry").Plan is null, "library: a Ubisoft game inside the games folder is blocked");
        check(Get("Battlefield").Plan is null && Get("Battlefield").App.BlockReason!.Contains("EA app"), "library: an EA Games title is blocked");
        check(Get("Forza").Plan is null && Get("Forza").App.BlockReason!.Contains("Xbox"), "library: an XboxGames title is blocked");

        var custom = Get("Other launcher");
        check(custom.Plan is not null && custom.Plan.ExcludePatterns.Contains(@"C:\Tools\Custom Steam\steamapps\* [*]"), "profile: any captured folder holding a steamapps directory excludes it");

        check(Get("Microsoft 365 Apps for enterprise - en-us").Plan is null && Get("Microsoft 365 Apps for enterprise - en-us").App.BlockReason!.Contains("reinstall"), "profile: Microsoft 365 is unsupported");
        check(Get("Office Extras").Plan is null, "profile: an Office entry is unsupported when the Click-to-Run key exists");
        check(Get("Adobe Photoshop 2026").Plan is null && Get("Adobe Photoshop 2026").App.BlockReason!.Contains("Adobe"), "profile: Adobe is unsupported");
        check(Get("Plain App").Plan is not null && Get("Plain App").Plan!.ExcludePatterns.Count == 0 && Get("Plain App").App.ProfileNames.Count == 0, "profile: an unmatched app is untouched");

        // the old call (no profile set) keeps the 3.7 behaviour
        var legacy = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None);
        check(legacy.Apps.First(a => a.App.DisplayName == "Fortnite").Plan is not null, "profile: with no profile set nothing is blocked (older checks unchanged)");

        // ---- rules: default libraries always excluded, app exclusions written, Epic root not excluded
        var o = new CaptureOptions();
        o.Apps.Add(steam.Plan!); o.Apps.Add(epic.Plan!);
        var built = RulesXmlBuilder.Build(o, "C:");
        check(built.Patterns.Contains(@"%ProgramFiles(x86)%\Steam\steamapps\* [*]") && built.Patterns.Contains(@"C:\XboxGames\* [*]")
              && built.Patterns.Contains(@"C:\Program Files\EA Games\* [*]") && built.Patterns.Contains(@"%ProgramFiles(x86)%\GOG Galaxy\Games\* [*]")
              && built.Patterns.Contains(@"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\games\* [*]") && built.Patterns.Contains(@"C:\Program Files\WindowsApps\* [*]"),
            "rules: every default game library folder is excluded without any app ticked");
        check(!built.Patterns.Any(p => p.StartsWith(@"%ProgramFiles(x86)%\Epic Games\* ") || p.StartsWith(@"C:\Program Files\Epic Games\* ")), "rules: the Epic Games root is never excluded (the launcher lives in it)");
        var settingsOnly = RulesXmlBuilder.Build(new CaptureOptions { Mode = CaptureMode.SettingsOnly }, "C:");
        check(settingsOnly.Patterns.Contains(@"C:\XboxGames\* [*]"), "rules: the library exclusions also apply in settings-only mode");
        var xml = built.Document.ToString();
        check(xml.Contains("steamapps") && xml.Contains("unconditionalExclude"), "rules: the XML carries the exclusions");

        // ---- GameLibraries
        check(GameLibraries.Member(@"C:\Program Files (x86)\Steam", "C:") is null && GameLibraries.Member(@"C:\Program Files (x86)\Steam\steamapps\common\Half", "C:") is not null,
            "library: Steam itself is not a member, its steamapps content is");
        check(GameLibraries.Member(@"D:\Steam\steamapps\common\X", "C:") is not null, "library: a Steam library on another path is recognised by the folder name");
        check(GameLibraries.Member(@"C:\Program Files (x86)\Epic Games\Launcher\Portal", "C:") is null, "library: the Epic Launcher folder is not a member");
        check(GameLibraries.Describe().Count >= 6, "library: the list is available for display");

        // ---- profile file: operator profiles first, bad file ignored, bad profile skipped
        var notes = new List<string>();
        var good = AppProfileSet.Parse("""
        { // comment
          "profiles": [
            { "name": "Plain tweak", "matchName": "Plain*", "excludePatterns": [ "Cache", "*.tmp", "..\\x", "a[b" ],
              "extraFolders": [ "%ProgramData%\\Vendor\\Data", "C:\\Windows", "%NOPE%\\x" ],
              "extraRegistry": [ "HKLM\\SOFTWARE\\Vendor\\Plain", "HKLM\\SOFTWARE\\Vendor\\Missing", "HKCU\\x" ],
              "defaultSelected": false, "advice": "Check it." },
            { "name": "No match fields" },
            { "name": "Lone star", "matchName": "*" }
          ] }
        """, "test.json", notes);
        check(good.Profiles.Count == 1 && notes.Count == 2, "profile file: invalid profiles are skipped with a note each");
        var set = new AppProfileSet(good.Profiles.Concat(AppProfileSet.BuiltIn.Profiles).ToList(), notes);
        var res2 = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None, null, null, set);
        var plain = res2.Apps.First(a => a.App.DisplayName == "Plain App");
        var pl = plain.Plan!;
        check(pl.ExcludePatterns.Contains(@"C:\Program Files\Plain\Cache\* [*]") && pl.ExcludePatterns.Contains(@"C:\Program Files\Plain [*.tmp]"), "profile file: folder and file-spec exclusions are written");
        check(pl.ExcludePatterns.Count == 2 && pl.Notes.Count(n => n.Contains("skipped")) >= 5, "profile file: unsafe or unusable entries are skipped with a note (relative path escape, brackets, system folder, variable, non-HKLM key)");
        check(pl.FilePatterns.Contains(@"C:\ProgramData\Vendor\Data\* [*]"), "profile file: an extra folder is added after the folder guard");
        check(pl.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Vendor\Plain [*]") && pl.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Vendor\Plain\* [*]")
              && !pl.RegistryPatterns.Any(r => r.Contains("Missing")), "profile file: an extra registry key is added only when it exists");
        check(plain.App.DefaultOverride == false && !plain.App.DefaultSelected && plain.App.Advice == "Check it." && plain.App.OffByDefaultReason.Contains("profile"),
            "profile file: defaultSelected and advice are applied");

        // ---- load: missing file, malformed file
        var bad = Path.Combine(tmp, "bad.json"); File.WriteAllText(bad, "{ not json");
        var loaded = AppProfileSet.Load(bad);
        check(loaded.Notes.Count == 1 && loaded.Profiles.Count == AppProfileSet.BuiltIn.Profiles.Count, "profile file: a malformed file is ignored with a note, built-ins stay");
        check(AppProfileSet.Load(Path.Combine(tmp, "none.json")).Notes.Count == 0, "profile file: a missing file is not an error");
        check(AppProfileSet.BuiltIn.Profiles.Count >= 8, "profile: the built-in set parsed");

        // ---- manifest entry carries the profile names and the exclusions
        var mo = new CaptureOptions(); mo.Apps.Add(steam.Plan!);
        var man = DeploymentManifest.Create(mo, tmp, "amd64", Path.Combine(tmp, "x.ppkg"), false);
        check(man.Apps is { Length: 1 } && man.Apps[0].Profiles!.Contains("Steam") && man.Apps[0].Excluded!.Contains("steamapps"), "manifest: profile names and exclusions are recorded");

        try { Directory.Delete(tmp, true); } catch { }
    }
}
