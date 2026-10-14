using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// S127: Adobe Reader allowed, the "allow" override, built-in vendor-folder profiles (extra roots feed registration matching),
// exclusions and library rules written only for folders that exist. Called from Program.cs.
public static class VendorProfileChecks
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
            @"C:\Program Files\Adobe\Reader", @"C:\Program Files\Adobe\Acrobat Pro", @"C:\Program Files\Adobe\Combined",
            @"C:\Program Files (x86)\Common Files\Adobe\ARM",
            @"C:\Program Files\Google\Chrome", @"C:\Program Files (x86)\Google\Update",
            @"C:\Program Files\Java\jre", @"C:\Program Files\Common Files\Oracle\Java",
            @"C:\Program Files\Git",
            @"C:\Program Files\Plain", @"C:\Program Files\Plain\Cache"
        };

        App(U64, "Reader", "Adobe Acrobat Reader (64-bit)", "Adobe", @"C:\Program Files\Adobe\Reader");
        App(U64, "Pro", "Adobe Acrobat Pro DC", "Adobe", @"C:\Program Files\Adobe\Acrobat Pro");
        App(U64, "Combined", "Adobe Acrobat (64-bit)", "Adobe", @"C:\Program Files\Adobe\Combined");
        App(U32, "Chrome", "Google Chrome", "Google LLC", @"C:\Program Files\Google\Chrome");
        App(U64, "Java", "Java 8 Update 401", "Oracle Corporation", @"C:\Program Files\Java\jre");
        App(U64, "Git", "Git", "The Git Development Community", @"C:\Program Files\Git");
        App(U64, "Plain", "Plain App", "Someone", @"C:\Program Files\Plain");
        V(@"SOFTWARE\GitForWindows", "InstallPath", @"C:\Program Files\Git");
        // Google Update service that lives beside Chrome, not inside it
        V(@"SYSTEM\CurrentControlSet\Services\gupdate", "ImagePath", "\"C:\\Program Files (x86)\\Google\\Update\\GoogleUpdate.exe\" /svc");
        V(@"SYSTEM\CurrentControlSet\Services\gupdate", "Type", 0x10);
        V(@"SYSTEM\CurrentControlSet\Services\gupdate", "Start", 2);

        var tmp = Path.Combine(Path.GetTempPath(), "vp-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);

        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None, null, null, AppProfileSet.BuiltIn);
        ScannedApp Get(string n) => res.Apps.First(a => a.App.DisplayName == n);

        // ---- Reader is allowed, everything else from Adobe stays blocked
        var reader = Get("Adobe Acrobat Reader (64-bit)");
        check(reader.Plan is not null && reader.App.BlockReason is null, "adobe: Acrobat Reader is no longer blocked");
        check(reader.Plan is not null && reader.Plan.FilePatterns.Contains(@"%ProgramFiles(x86)%\Common Files\Adobe\ARM\* [*]"), "adobe: Reader carries its ARM updater folder when present");
        check(Get("Adobe Acrobat Pro DC").Plan is null && Get("Adobe Acrobat Pro DC").App.BlockReason!.Contains("Adobe"), "adobe: Acrobat Pro stays blocked");
        check(Get("Adobe Acrobat (64-bit)").Plan is null && Get("Adobe Acrobat (64-bit)").App.BlockReason!.Contains("Pro and by Reader"), "adobe: the combined entry stays blocked, with a reason that says why");

        // ---- operator "allow" lifts a built-in block, and comes first
        var notes = new List<string>();
        var op = AppProfileSet.Parse("""{ "profiles": [ { "name": "Allow combined", "matchName": "Adobe Acrobat (64-bit)", "allow": true } ] }""", "t.json", notes);
        var set = new AppProfileSet(op.Profiles.Concat(AppProfileSet.BuiltIn.Profiles).ToList(), notes);
        var res2 = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None, null, null, set);
        check(res2.Apps.First(a => a.App.DisplayName == "Adobe Acrobat (64-bit)").Plan is not null, "allow: an operator profile with allow lifts the built-in Adobe block");
        check(res2.Apps.First(a => a.App.DisplayName == "Adobe Acrobat Pro DC").Plan is null, "allow: it does not lift the block for other Adobe apps");

        // ---- Chrome: Google Update folder is carried, and the service that points into it comes along
        var chrome = Get("Google Chrome");
        check(chrome.Plan is not null && chrome.Plan.FilePatterns.Contains(@"%ProgramFiles(x86)%\Google\Update\* [*]"), "vendor: Chrome carries the Google Update folder that exists");
        check(chrome.Plan is not null && !chrome.Plan.FilePatterns.Any(f => f.Contains("GoogleUpdater")) && !chrome.Plan.Notes.Any(n => n.Contains("GoogleUpdater")),
            "vendor: a built-in vendor folder that is absent is skipped without a note");
        check(chrome.Plan is not null && chrome.Plan.ServiceItems.Contains("Service: gupdate"), "vendor: a service whose image is in the vendor folder is carried with the app");
        check(chrome.Plan is not null && chrome.Plan.ExtraFolders.Count == 1, "vendor: the plan lists the extra folder for the log");

        // ---- Java and Git: folders and keys only where present
        var java = Get("Java 8 Update 401");
        check(java.Plan is not null && java.Plan.FilePatterns.Contains(@"C:\Program Files\Common Files\Oracle\Java\* [*]")
              && !java.Plan.FilePatterns.Any(f => f.Contains("ProgramData") || f.Contains("(x86)")), "vendor: Java carries only the Oracle folders that exist");
        check(Get("Git").Plan is not null && Get("Git").Plan!.RegistryPatterns.Contains(@"HKLM\SOFTWARE\GitForWindows [*]"), "vendor: Git carries its registry key");

        // ---- an operator's missing folder is reported (possible typo), a built-in one is not
        var typo = AppProfileSet.Parse("""{ "profiles": [ { "name": "T", "matchName": "Plain*", "extraFolders": [ "%ProgramData%\\Nope" ] } ] }""", "t.json", new List<string>());
        var res3 = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None, null, null, typo);
        check(res3.Apps.First(a => a.App.DisplayName == "Plain App").Plan!.Notes.Any(n => n.Contains("Nope") && n.Contains("skipped")), "vendor: an operator folder that does not exist is reported");

        // ---- exclusions only where the folder exists
        check(AppProfiles.ExcludeTargetExists(@"C:\Program Files\Plain", "Cache", p => dirs.Contains(p)), "exclude: folder rule, folder present");
        check(!AppProfiles.ExcludeTargetExists(@"C:\Program Files\Plain", "Games", p => dirs.Contains(p)), "exclude: folder rule, folder absent");
        check(AppProfiles.ExcludeTargetExists(@"C:\Program Files\Plain", "*.tmp", p => dirs.Contains(p)), "exclude: file rule in the app folder");
        check(!AppProfiles.ExcludeTargetExists(@"C:\Program Files\Plain", @"logs\*.log", p => dirs.Contains(p)), "exclude: file rule, its folder absent");
        check(AppProfiles.ExcludeTargetExists(@"C:\Program Files\Plain", @"*\cache", p => false), "exclude: wildcard folder segment cannot be checked, kept");

        var onlyXbox = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\XboxGames" };
        var libs = GameLibraries.ExclusionPatterns("C:", p => onlyXbox.Contains(p));
        check(libs.Count == 1 && libs[0] == @"C:\XboxGames\* [*]", "library: with dirExists only existing library folders are excluded");
        check(GameLibraries.ExclusionPatterns("C:", _ => false).Count == 0, "library: nothing excluded when no library folder exists");
        check(GameLibraries.ExclusionPatterns("C:").Count >= 6, "library: without dirExists all default folders are listed (older behaviour)");
        var built = RulesXmlBuilder.Build(new CaptureOptions(), "C:", _ => false);
        check(!built.Patterns.Any(p => p.Contains("ProgramFiles(x86)")), "rules: no %ProgramFiles(x86)% exclusion on a PC without those folders");

        try { Directory.Delete(tmp, true); } catch { }
    }
}
