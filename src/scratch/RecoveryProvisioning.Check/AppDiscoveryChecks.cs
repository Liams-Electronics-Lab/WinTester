using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Checks for item 3.7/3.8 (app discovery and include rules) against a fake registry. Called from Program.cs.
public static class AppDiscoveryChecks
{
    public static void Run(Action<bool, string> check)
    {
        void Check(bool ok, string n) => check(ok, n);
        // ---- fake registry
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        void V(string p, string n, object? v) { K(p); vals[p + "|" + n] = v; }
        var reg = new Fake(keys, vals);

        const string U64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string U32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
          { @"C:\Program Files\Google\Chrome\Application", @"D:\Games\DOOM II", @"C:\Games\DOOM II", @"C:\Program Files (x86)\HxD",
            @"C:\Program Files\Microsoft Office", @"C:\Program Files\Weird [x]" };

        // Chrome: ARP only in the 32-bit view (C31), files in Program Files
        V(U32 + @"\Google Chrome", "DisplayName", "Google Chrome");
        V(U32 + @"\Google Chrome", "Publisher", "Google LLC");
        V(U32 + @"\Google Chrome", "DisplayVersion", "154.0.8037.93");
        V(U32 + @"\Google Chrome", "InstallLocation", @"C:\Program Files\Google\Chrome\Application");
        V(U32 + @"\Google Chrome", "EstimatedSize", 515493);
        V(@"SOFTWARE\Google\Update", "x", "1"); V(@"SOFTWARE\WOW6432Node\Google\Update", "x", "1");
        V(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        V(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe", "", @"C:\Windows\system32\notepad.exe");
        V(@"SOFTWARE\Clients\StartMenuInternet\Google Chrome\shell\open\command", "", "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"");
        V(@"SOFTWARE\Clients\StartMenuInternet\IEXPLORE.EXE\shell\open\command", "", "\"C:\\Program Files\\Internet Explorer\\iexplore.exe\"");
        V(@"SOFTWARE\Classes\ChromeHTML\shell\open\command", "", "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1");
        V(@"SOFTWARE\Classes\htmlfile\shell\open\command", "", @"C:\Program Files\Internet Explorer\iexplore.exe");
        V(@"SOFTWARE\Classes\.html", "", "ChromeHTML");
        V(@"SOFTWARE\RegisteredApplications", "Google Chrome", @"SOFTWARE\Clients\StartMenuInternet\Google Chrome\Capabilities");
        V(@"SOFTWARE\RegisteredApplications", "Paint", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Applets\Paint\Capabilities");
        V(@"SYSTEM\CurrentControlSet\Services\GoogleChromeElevationService", "ImagePath", "\"C:\\Program Files\\Google\\Chrome\\Application\\elevation_service.exe\"");

        // DOOM II: outside Program Files, no InstallLocation, DisplayIcon only
        V(U64 + @"\DOOM II_is1", "DisplayName", "DOOM II Enhanced"); V(U64 + @"\DOOM II_is1", "Publisher", "GOG.com");
        V(U64 + @"\DOOM II_is1", "DisplayIcon", @"C:\Games\DOOM II\DOOM2.exe,0");
        V(@"SOFTWARE\WOW6432Node\GOG.com\Games\1", "path", @"C:\Games\DOOM II");
        // HxD in Program Files (x86), MSI
        V(U32 + @"\HxD_is1", "DisplayName", "HxD Hex Editor"); V(U32 + @"\HxD_is1", "UninstallString", "\"C:\\Program Files (x86)\\HxD\\unins000.exe\" /x");
        V(U32 + @"\HxD_is1", "WindowsInstaller", 1);
        // Microsoft, update entry, system component, per-user, other-drive, no folder, weird chars, broad folder
        V(U64 + @"\MSO", "DisplayName", "Microsoft Office 365"); V(U64 + @"\MSO", "Publisher", "Microsoft Corporation"); V(U64 + @"\MSO", "InstallLocation", @"C:\Program Files\Microsoft Office");
        V(U64 + @"\KB1", "DisplayName", "Update for X"); V(U64 + @"\KB1", "ParentKeyName", "OperatingSystem");
        V(U64 + @"\SC", "DisplayName", "Hidden thing"); V(U64 + @"\SC", "SystemComponent", 1);
        V(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Teams", "DisplayName", "Teams user"); V(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Teams", "InstallLocation", @"C:\Users\x\AppData\Local\Teams");
        V(U64 + @"\DDrive", "DisplayName", "On D"); V(U64 + @"\DDrive", "InstallLocation", @"D:\Games\DOOM II");
        V(U64 + @"\NoFolder", "DisplayName", "No folder"); V(U64 + @"\NoFolder", "UninstallString", "MsiExec.exe /X{ABC}");
        V(U64 + @"\Weird", "DisplayName", "Weird"); V(U64 + @"\Weird", "InstallLocation", @"C:\Program Files\Weird [x]");
        V(U64 + @"\Broad", "DisplayName", "Broad"); V(U64 + @"\Broad", "InstallLocation", @"C:\Program Files");
        V(U64 + @"\Nvidia", "DisplayName", "NVIDIA Graphics Driver"); V(U64 + @"\Nvidia", "Publisher", "NVIDIA Corporation"); V(U64 + @"\Nvidia", "InstallLocation", @"C:\Program Files\Google\Chrome\Application");

        var tmp = Path.Combine(Path.GetTempPath(), "sm-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(tmp, "Sub"));
        File.WriteAllText(Path.Combine(tmp, "Google Chrome.lnk"), "x");
        File.WriteAllText(Path.Combine(tmp, "Sub", "HxD Hex Editor.lnk"), "x");
        File.WriteAllText(Path.Combine(tmp, "Other.lnk"), "x");
        File.WriteAllText(Path.Combine(tmp, "Chrome Apps (x).lnk"), "x");
        Directory.CreateDirectory(Path.Combine(tmp, "DOOM II Enhanced [GOG.com]"));
        File.WriteAllText(Path.Combine(tmp, "DOOM II Enhanced [GOG.com]", "DOOM II Enhanced.lnk"), "x");
        File.WriteAllText(Path.Combine(tmp, "DOOM II Enhanced [GOG.com]", "Manual, 1.lnk"), "x");

        string? Resolve(string lnk) => Path.GetFileName(lnk) switch
        {
            "Google Chrome.lnk" => @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "Chrome Apps (x).lnk" => @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
            "Other.lnk" => @"C:\Windows\notepad.exe",
            "DOOM II Enhanced.lnk" => @"C:\Games\DOOM II\DOOM2.exe",
            "Manual, 1.lnk" => @"C:\Games\DOOM II\manual.pdf", _ => null
        };

        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, Resolve, CancellationToken.None);
        ScannedApp Get(string n) => res.Apps.First(a => a.App.DisplayName == n);

        Check(res.Apps.All(a => a.App.DisplayName != "Update for X" && a.App.DisplayName != "Hidden thing"), "updates and system components skipped");
        var chrome = Get("Google Chrome");
        Check(chrome.Plan is not null && chrome.App.DefaultSelected, "chrome capturable + selected by default");
        var p = chrome.Plan!;
        Check(p.FilePatterns.Contains(@"C:\Program Files\Google\Chrome\Application\* [*]"), "chrome folder pattern");
        Check(p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Google Chrome [*]"), "chrome WOW6432Node uninstall key (C31)");
        Check(!p.RegistryPatterns.Any(x => x.StartsWith(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Google Chrome")), "no 64-bit uninstall guess");
        Check(p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Google\* [*]") && p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\WOW6432Node\Google\* [*]"), "vendor key both views, only because they exist");
        Check(p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe [*]"), "app paths chrome");
        Check(!p.RegistryPatterns.Any(x => x.Contains("notepad.exe")), "app paths of other apps not taken");
        Check(p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Clients\StartMenuInternet\Google Chrome\* [*]"), "clients key");
        Check(!p.RegistryPatterns.Any(x => x.Contains("IEXPLORE")), "IE client not taken");
        Check(p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Classes\ChromeHTML\* [*]"), "ChromeHTML class");
        Check(!p.RegistryPatterns.Any(x => x.Contains("htmlfile")), "htmlfile class not taken");
        Check(p.RegistryPatterns.Contains(@"HKLM\SOFTWARE\RegisteredApplications [Google Chrome]"), "RegisteredApplications value");
        Check(!p.RegistryPatterns.Any(x => x.Contains("[Paint]")), "other registered apps not taken");
        Check(p.FilePatterns.Contains(tmp + " [Google Chrome.lnk]"), "start menu shortcut");
        Check(p.FilePatterns.Any(x => x.EndsWith("[Chrome Apps (x).lnk]")), "shortcut by target, parentheses allowed");
        Check(!p.FilePatterns.Any(x => x.Contains("Other.lnk")), "unrelated shortcut not taken");
        Check(p.Notes.Any(n => n.Contains("service")) && !p.RegistryPatterns.Any(x => x.Contains("Services")), "service reported, not captured");
        Check(p.SizeBytes == 515493L * 1024, "size from ARP");

        var doom = Get("DOOM II Enhanced");
        Check(doom.Plan is not null && doom.App.Folder == @"C:\Games\DOOM II" && doom.App.FolderSource == "DisplayIcon", "doom: folder from DisplayIcon outside Program Files");
        Check(doom.Plan!.RegistryPatterns.Contains(@"HKLM\SOFTWARE\WOW6432Node\GOG.com\* [*]") && !doom.Plan.RegistryPatterns.Any(x => x.StartsWith(@"HKLM\SOFTWARE\GOG.com")), "doom: vendor key only where it exists");
        Check(doom.Plan.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DOOM II_is1 [*]"), "doom: 64-bit uninstall key");

        Check(doom.Plan.FilePatterns.Contains(tmp + @"\DOOM II Enhanced ^[GOG.com^] [DOOM II Enhanced.lnk]"), "doom: shortcut in a bracketed folder is written with escaped brackets");
        Check(!doom.Plan.FilePatterns.Any(x => x.Contains("Manual, 1.lnk")) && doom.Plan.Notes.Any(n => n.Contains("special characters") && n.Contains("Manual, 1.lnk")), "doom: comma in a shortcut name still blocks the rule and is reported");
        Check(doom.Plan.Notes.Any(n => n.Contains("escaped brackets") && n.Contains("DOOM II Enhanced.lnk")), "doom: escaped-bracket shortcut is reported in the notes");
        Check(WinPath.EscapeBrackets("a [b] c") == "a ^[b^] c" && WinPath.EscapeBrackets("plain") == "plain", "EscapeBrackets");
        Check(!WinPath.PatternSafe("x [y]") && WinPath.PatternSafeAfterBracketEscape("x [y]") && !WinPath.PatternSafeAfterBracketEscape("x, [y]"), "bracket escape only relaxes brackets");

        var hxd = Get("HxD Hex Editor");
        Check(hxd.Plan is not null && hxd.Plan.FilePatterns.Contains(@"%ProgramFiles(x86)%\HxD\* [*]"), "x86 folder uses %ProgramFiles(x86)%");
        Check(hxd.Plan!.FilePatterns.Any(x => x.EndsWith("[HxD Hex Editor.lnk]")), "hxd shortcut by name");
        Check(hxd.Plan.Notes.Any(n => n.Contains("Windows Installer")), "msi note");
        Check(hxd.App.FolderSource == "UninstallString", "folder from quoted UninstallString");

        Check(!Get("Microsoft Office 365").App.DefaultSelected && Get("Microsoft Office 365").Plan is not null, "microsoft: capturable but off by default");
        Check(!Get("NVIDIA Graphics Driver").App.DefaultSelected, "driver vendor off by default");
        Check(Get("Teams user").Plan is null && Get("Teams user").App.BlockReason!.Contains("Per-user"), "per-user blocked");
        Check(Get("On D").Plan is null && Get("On D").App.BlockReason!.Contains("outside the system drive"), "other drive blocked");
        Check(Get("No folder").Plan is null && Get("No folder").App.BlockReason!.Contains("no install folder"), "no folder blocked");
        Check(Get("Weird").Plan is null && Get("Weird").App.BlockReason!.Contains("characters"), "special chars blocked");
        Check(Get("Broad").Plan is null && Get("Broad").App.BlockReason!.Contains("too broad"), "program files root blocked");
        Check(AppDiscovery.CheckFolder(@"C:\Windows\System32\Foo", "C:", _ => true) is not null, "windows folder blocked");
        Check(AppDiscovery.CheckFolder(@"C:\", "C:", _ => true) is not null, "drive root blocked");
        Check(AppDiscovery.CheckFolder(@"C:\Program Files\WindowsApps\X", "C:", _ => true) is not null, "store folder blocked");
        Check(AppDiscovery.CheckFolder(@"C:\WTRP\ab12", "C:", _ => true) is not null, "workspace folder blocked");
        Check(AppDiscovery.FirstPath("\"C:\\a b\\x.exe\",0") == @"C:\a b\x.exe", "FirstPath quoted + icon index");
        Check(AppDiscovery.FirstPath(@"C:\a\x.exe,0") == @"C:\a\x.exe", "FirstPath icon index");
        Check(AppDiscovery.FirstPath(@"C:\a b\x.exe /S") == @"C:\a b\x.exe", "FirstPath unquoted with args");
        Check(AppDiscovery.FirstPath("MsiExec.exe /X{G}") == "MsiExec.exe", "FirstPath msiexec");

        // ---- rules xml
        var opts = new CaptureOptions();
        opts.Apps.Add(chrome.Plan!); opts.Apps.Add(doom.Plan);
        var rules = RulesXmlBuilder.Build(opts, "C:");
        var comps = rules.Document.Root!.Elements("component").ToList();
        Check(comps.Count == 3, "one exclusion component + one per app");
        Check(comps[1].Element("displayName")!.Value == "WinTester app: Google Chrome", "app component named");
        var incl = comps[1].Descendants("include").Single().Descendants("pattern").ToList();
        Check(incl.Any(x => (string?)x.Attribute("type") == "Registry") && incl.Any(x => (string?)x.Attribute("type") == "File"), "registry and file patterns typed");
        Check(rules.IncludePatterns.Count == rules.IncludePatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count() && rules.IncludePatterns.Count > 10, "include patterns listed, distinct");
        Check(rules.Patterns.Contains(@"C:\WTRP\* [*]"), "workspace still excluded");
        opts.Mode = CaptureMode.SettingsOnly;
        var none = RulesXmlBuilder.Build(opts, "C:");
        Check(none.Document.Root!.Elements("component").Count() == 1 && none.IncludePatterns.Count == 0, "settings-only ignores apps");

        // ---- log analysis of skipped include roots
        var logf = Path.Combine(tmp, "s.log");
        File.WriteAllLines(logf, new[]
        {
          "2026-10-01 12:04:50, Info                  [0x080000] Include pattern skipped because enumeration root is not present: HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Google Chrome[*]",
          "2026-10-01 12:04:51, Info                  [0x080000] Include pattern skipped because enumeration root is not present: HKLM\\SOFTWARE\\Other\\Thing[*]",
          "2026-10-01 12:05:48, Error                 [0x0808fe] Plugin x"
        });
        var an = ScanStateLogAnalyzer.Analyze(logf);
        Check(an.SkippedIncludeRoots!.Count == 2 && an.ErrorCount == 1, "analyzer collects skipped roots");
        var ours = ScanStateLogAnalyzer.OursSkipped(an.SkippedIncludeRoots, new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Google Chrome [*]" });
        Check(ours.Count == 1 && ours[0].Contains("Google Chrome"), "only our skipped roots reported");
        var escPat = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\DOOM II Enhanced ^[GOG.com^] [DOOM II Enhanced.lnk]";
        var logEscaped = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\DOOM II Enhanced ^[GOG.com^][DOOM II Enhanced.lnk]";
        var logPlain = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\DOOM II Enhanced [GOG.com][DOOM II Enhanced.lnk]";
        var unrelated = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Other [Vendor][Other.lnk]";
        Check(ScanStateLogAnalyzer.OursSkipped(new[] { logEscaped, unrelated }, new[] { escPat }).Count == 1, "skipped escaped-bracket shortcut recognised (log keeps the escape)");
        Check(ScanStateLogAnalyzer.OursSkipped(new[] { logPlain, unrelated }, new[] { escPat }).Count == 1, "skipped escaped-bracket shortcut recognised (log drops the escape)");
        Check(ScanStateLogAnalyzer.OursSkipped(new[] { unrelated }, new[] { escPat }).Count == 0, "unrelated bracketed shortcut not reported as ours");

        // ---- manifest
        var mf = DeploymentManifest.Create(new CaptureOptions { }, "/nonexistent", "amd64", "/nonexistent/x.ppkg", false);
        Check(mf.SchemaVersion == 2 && mf.Apps is { Length: 0 }, "manifest schema 2");
        var o2 = new CaptureOptions(); o2.Apps.Add(chrome.Plan!);
        var mf2 = DeploymentManifest.Create(o2, "/nonexistent", "amd64", "/nonexistent/x.ppkg", false);
        Check(mf2.Apps![0].Name == "Google Chrome" && mf2.Apps[0].RegistryRules > 5, "manifest lists apps");
        var back = System.Text.Json.JsonSerializer.Deserialize<DeploymentManifest>(System.Text.Json.JsonSerializer.Serialize(mf2));
        Check(back!.Apps![0].Folder.EndsWith("Application"), "manifest round trip");
        var v1 = System.Text.Json.JsonSerializer.Deserialize<DeploymentManifest>("{\"SchemaVersion\":1,\"PackageFileName\":\"usmt.ppkg\",\"OsBuild\":\"1\",\"OsDisplayVersion\":\"\",\"OsProductName\":\"\",\"CaptureMode\":\"\",\"ConfigFile\":\"\",\"Exclusions\":[],\"CustomExclusions\":[],\"UsmtVersion\":\"\",\"UsmtArch\":\"\",\"WinTesterVersion\":\"\"}");
        Check(v1 is not null && v1.Apps is null, "schema 1 manifest still reads");

        Directory.Delete(tmp, true);

    }

    internal sealed class Fake : IRegistryReader
    {
        private readonly HashSet<string> _k; private readonly Dictionary<string, object?> _v;
        public Fake(HashSet<string> k, Dictionary<string, object?> v) { _k = k; _v = v; }
        static string Key(RegHive h, string p) => (h == RegHive.Hkcu ? "HKCU\\" : "") + p;
        public bool KeyExists(RegHive h, string p) => _k.Contains(Key(h, p));
        public IReadOnlyList<string> SubKeyNames(RegHive h, string p)
        {
            var pre = Key(h, p) + "\\";
            return _k.Where(x => x.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && !x.Substring(pre.Length).Contains('\\')).Select(x => x.Substring(pre.Length)).ToList();
        }
        public IReadOnlyList<string> ValueNames(RegHive h, string p)
        {
            var pre = Key(h, p) + "|";
            return _v.Keys.Where(x => x.StartsWith(pre, StringComparison.OrdinalIgnoreCase)).Select(x => x.Substring(pre.Length)).ToList();
        }
        public object? GetValue(RegHive h, string p, string n) => _v.TryGetValue(Key(h, p) + "|" + n, out var v) ? v : null;
    }

}
