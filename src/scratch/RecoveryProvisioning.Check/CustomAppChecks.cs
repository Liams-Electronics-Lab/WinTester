using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 3.18: an app added by picking its folder, with no Uninstall entry. Everything that points into the folder is found by the
// same matching as for a scanned app; unsafe, listed or blocked folders are refused. Called from Program.cs.
public static class CustomAppChecks
{
    private const string Cls = @"SOFTWARE\Classes";
    private const string Cache = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    private const string Run64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string U64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Svc = @"SYSTEM\CurrentControlSet\Services";
    private const string G1 = "{AAAAAAAA-0000-0000-0000-000000000001}";

    public static void Run(Action<bool, string> check)
    {
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        void V(string p, string n, object? v) { K(p); vals[p + "|" + n] = v; }
        var reg = new AppDiscoveryChecks.Fake(keys, vals);

        void App(string key, string name, string pub, string folder)
        {
            V(U64 + "\\" + key, "DisplayName", name); V(U64 + "\\" + key, "Publisher", pub);
            V(U64 + "\\" + key, "InstallLocation", folder); V(U64 + "\\" + key, "DisplayVersion", "1.0");
        }
        App("Listed", "Listed App", "Someone", @"C:\Program Files\Listed");
        App("M365", "Microsoft 365 Apps for enterprise - en-us", "Microsoft Corporation", @"C:\Program Files\Microsoft Office");
        App("Photoshop", "Adobe Photoshop 2026", "Adobe Inc.", @"C:\Program Files\Adobe\Photoshop");
        V(@"SOFTWARE\Microsoft\Office\ClickToRun", "x", "1");

        // The custom app: no Uninstall entry at all. A service, a logon task, a Run value, a COM class and a vendor key point at it.
        V(Svc + @"\AcmeSvc", "ImagePath", "\"C:\\Acme\\svc.exe\" -k");
        V(Svc + @"\AcmeSvc", "Type", 0x10); V(Svc + @"\AcmeSvc", "Start", 2); V(Svc + @"\AcmeSvc", "ObjectName", "LocalSystem");
        V(Run64, "AcmeTray", "\"C:\\Acme\\tray.exe\" /min");
        V(Cache + @"\Tree\Acme\Updater", "Id", "{11111111-1111-1111-1111-111111111111}");
        V(Cache + @"\Tasks\{11111111-1111-1111-1111-111111111111}", "Path", @"\Acme\Updater");
        V(Cls + @"\CLSID\" + G1, "", "Acme Class");
        V(Cls + @"\CLSID\" + G1 + @"\InprocServer32", "", @"C:\Acme\acme.dll");
        V(@"SOFTWARE\Acme", "Setting", "1");
        V(@"SOFTWARE\Acme Corp", "Setting", "1");                 // parent folder name of the second custom app
        V(@"SOFTWARE\Program Files", "Setting", "1");             // a generic parent name must not be taken
        V(@"SOFTWARE\Tool", "Setting", "1");                      // a key named like the folder, outside any generic name

        var tasks = new List<TaskInfo> { new(@"Acme\Updater", new[] { "\"C:\\Acme\\upd.exe\" /c" }, true, "{11111111-1111-1111-1111-111111111111}") };

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Acme", @"C:\Program Files\Acme Corp\Billing", @"C:\Program Files\Tool",
            @"C:\Program Files\Listed", @"C:\Program Files\Listed\Sub",
            @"C:\Program Files\Microsoft Office", @"C:\Program Files\Microsoft Office\Root",
            @"C:\Program Files\Adobe", @"C:\Program Files\Adobe\Photoshop",
            @"C:\Windows", @"C:\Users\x\Tool", @"D:\Tools",
            @"C:\Program Files (x86)\Steam", @"C:\Program Files (x86)\Steam\steamapps", @"C:\XboxGames"
        };

        var tmp = Path.Combine(Path.GetTempPath(), "ca-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "Acme.lnk"), "x");
        string? Resolve(string lnk) => Path.GetFileName(lnk) == "Acme.lnk" ? @"C:\Acme\acme.exe" : null;

        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, Resolve, CancellationToken.None,
            tasks, null, AppProfileSet.BuiltIn);
        check(res.Context is not null && !res.Apps.Any(a => a.App.IsCustom), "custom: a scan has a context and lists no custom app");
        check(res.Apps.First(a => a.App.DisplayName == "Listed App").Plan is not null
              && res.Apps.First(a => a.App.DisplayName.StartsWith("Microsoft 365")).Plan is null
              && res.Apps.First(a => a.App.DisplayName.StartsWith("Adobe Photoshop")).Plan is null, "custom: fixture has one listed app and two blocked");

        // ---- the add itself
        var add = AppScanner.AddCustom(res, @"C:\Acme\", null, CancellationToken.None);
        check(add.Added is not null && add.Refusal is null, "custom: a plain folder is added (trailing backslash tolerated)");
        var sa = add.Added!;
        var plan = sa.Plan!;
        check(sa.App.IsCustom && sa.App.UninstallRel is null && sa.App.Source == "custom" && sa.App.DisplayName == "Acme" && plan.IsCustom, "custom: entry has source custom, no Uninstall key, the folder's name");
        check(sa.App.SizeBytes == 4096 && sa.App.SizeMeasured && sa.App.DefaultSelected, "custom: size measured, ticked by default");
        check(plan.FilePatterns.Contains(@"C:\Acme\* [*]"), "custom: the folder itself is an include rule");
        check(!plan.RegistryPatterns.Any(r => r.Contains("Uninstall")) && !plan.Notes.Any(n => n.Contains("Uninstall")), "custom: no Uninstall rule and no note about one");
        check(plan.ServiceItems.Contains("Service: AcmeSvc") && plan.ServiceRegistryPatterns.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSvc [*]"), "custom: a service whose image is in the folder is found");
        check(plan.StartupItems.Contains("Run entry: AcmeTray") && plan.StartupItems.Contains(@"Scheduled task: \Acme\Updater"), "custom: the Run value and the logon task are found");
        check(plan.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Acme [*]"), "custom: HKLM\\SOFTWARE\\<folder name> is carried because it exists");
        check(plan.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Classes\CLSID\" + G1 + " [*]"), "custom: a COM class whose server is in the folder is found");
        check(plan.FilePatterns.Contains(tmp + " [Acme.lnk]"), "custom: an all-users Start-menu shortcut that targets the folder is found");

        // ---- it goes into the rules, the log lists and the manifest like any other app
        var o = new CaptureOptions();
        o.Apps.Add(plan);
        var rules = RulesXmlBuilder.Build(o, "C:");
        check(rules.IncludePatterns.Contains(@"C:\Acme\* [*]") && !rules.IncludePatterns.Any(r => r.Contains("Uninstall"))
              && rules.Document.ToString().Contains("WinTester app: Acme"), "custom: rules carry the folder and a component named after it");
        var man = DeploymentManifest.Create(o, tmp, "amd64", Path.Combine(tmp, "x.ppkg"), false);
        check(man.Apps is { Length: 1 } && man.Apps[0].Custom == true, "custom: the manifest marks the app as added by folder");
        var o2 = new CaptureOptions();
        o2.Apps.Add(res.Apps.First(a => a.App.DisplayName == "Listed App").Plan!);
        check(DeploymentManifest.Create(o2, tmp, "amd64", Path.Combine(tmp, "x.ppkg"), false).Apps![0].Custom is null, "custom: a scanned app is not marked");

        // ---- the result keeps its context, so a second add works on the first add's list; the same folder twice is refused
        var res2 = res with { Apps = res.Apps.Append(sa).ToList() };
        check(res2.Context is not null, "custom: a result copied with a new app list keeps its context");
        var again = AppScanner.AddCustom(res2, @"C:\Acme", null, CancellationToken.None);
        check(again.Added is null && again.Refusal!.Contains("already in the list as \"Acme\""), "custom: the same folder twice is refused");

        // ---- name, parent folder key, generic parent
        var named = AppScanner.AddCustom(res, @"C:\Acme", "My\0 Acme", CancellationToken.None);
        check(named.Added?.App.DisplayName == "My Acme", "custom: a given name is used with control characters removed");
        var billing = AppScanner.AddCustom(res, @"C:\Program Files\Acme Corp\Billing", null, CancellationToken.None).Added?.Plan;
        check(billing is not null && billing.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Acme Corp [*]"), "custom: the parent folder's name is a vendor key candidate");
        var tool = AppScanner.AddCustom(res, @"C:\Program Files\Tool", null, CancellationToken.None).Added?.Plan;
        check(tool is not null && tool.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Tool [*]") && !tool.RegistryPatterns.Any(r => r.Contains(@"SOFTWARE\Program Files")),
            "custom: a generic parent (Program Files) is never taken as a vendor key");

        // ---- profiles still apply: a Steam launcher folder added by hand excludes its library
        var steam = AppScanner.AddCustom(res, @"C:\Program Files (x86)\Steam", null, CancellationToken.None).Added?.Plan;
        check(steam is not null && steam.ExcludePatterns.Contains(@"%ProgramFiles(x86)%\Steam\steamapps\* [*]"), "custom: profiles and library exclusions apply to an added folder");

        // ---- refusals: nothing is added, the reason is given
        string? Why(string? folder) => AppScanner.AddCustom(res, folder, null, CancellationToken.None) is { Added: null } r ? r.Refusal : null;
        check(Why(@"C:\Windows") is { Length: > 0 }, "refuse: the Windows folder");
        check(Why(@"C:\Users\x\Tool") is { } u && u.Contains("user profile"), "refuse: a folder in a user profile");
        check(Why(@"C:\") is { } d && d.Contains("whole drive"), "refuse: a whole drive");
        check(Why(@"D:\Tools") is { } dd && dd.Contains("outside the system drive"), "refuse: another drive");
        check(Why(@"C:\Nope") is { } n && n.Contains("does not exist"), "refuse: a folder that does not exist");
        check(Why(@"C:\Program Files (x86)\Steam\steamapps") is { } l && l.Contains("Game library"), "refuse: a game library folder");
        check(Why(@"C:\XboxGames") is { } x && x.Contains("Game library"), "refuse: the Xbox library");
        check(Why(@"C:\Program Files\Listed") is { } li && li.Contains("already in the list as \"Listed App\""), "refuse: the folder of an app that is already listed");
        check(Why(@"C:\Program Files\Listed\Sub") is { } ins && ins.Contains("inside \"Listed App\"") && ins.Contains("tick that app"), "refuse: a folder inside a listed app");
        check(Why(@"C:\Program Files\Microsoft Office\Root") is { } blk && blk.Contains("cannot be captured") && blk.Contains("Microsoft 365"), "refuse: a folder inside an app that cannot be captured (no way around the block)");
        check(Why(@"C:\Program Files\Adobe") is { } hold && hold.Contains("holds \"Adobe Photoshop 2026\"") && hold.Contains("cannot be captured"), "refuse: a folder that holds an app that cannot be captured");
        check(Why("") is { } e0 && e0.Contains("No folder") && Why(null) is not null, "refuse: no folder");
        check(AppScanner.AddCustom(new AppScanResult(Array.Empty<ScannedApp>()), @"C:\Acme", null, CancellationToken.None).Refusal!.Contains("scan first"), "refuse: a result without a scan context");

        try { Directory.Delete(tmp, true); } catch { }
    }
}
