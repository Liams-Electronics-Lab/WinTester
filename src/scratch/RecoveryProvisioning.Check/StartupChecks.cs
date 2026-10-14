using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Checks for item 3.11 (services and "starts with Windows" rules, AppStartup.cs) against a fake registry, plus the
// per-option rule selection (FilesFor / RegistryFor), the rules file and the manifest. Called from Program.cs.
public static class StartupChecks
{
    private const string Svc = @"SYSTEM\CurrentControlSet\Services";
    private const string Run64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string Cache = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    private const string U64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Folder = @"C:\Program Files\Acme";
    private const string Guid1 = "{11111111-1111-1111-1111-111111111111}";
    private const string Guid6 = "{66666666-6666-6666-6666-666666666666}";

    public static void Run(Action<bool, string> check)
    {
        void Check(bool ok, string n) => check(ok, n);

        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        void V(string p, string n, object? v) { K(p); vals[p + "|" + n] = v; }
        var reg = new AppDiscoveryChecks.Fake(keys, vals);

        void Service(string name, int type, int start, string? account, string? image = null)
        {
            V(Svc + "\\" + name, "Type", type);
            V(Svc + "\\" + name, "Start", start);
            if (account is not null) V(Svc + "\\" + name, "ObjectName", account);
            if (image is not null) V(Svc + "\\" + name, "ImagePath", image);
        }

        // ---- ServiceRules.Evaluate: one case per verdict
        Service("SafeSvc", 0x10, 2, "LocalSystem");
        V(Svc + @"\SafeSvc\Parameters", "x", "1");
        Service("NoAccountSvc", 0x20, 3, null);                       // empty ObjectName = LocalSystem
        Service("InteractiveSvc", 0x110, 2, "LocalSystem");           // 0x100 bit is ignored
        Service("LocalSvc", 0x10, 2, @"NT AUTHORITY\LocalService");
        Service("VirtualSvc", 0x10, 4, @"NT SERVICE\SomeThing");
        Service("DriverSvc", 0x01, 3, null);
        Service("BootSvc", 0x10, 0, "LocalSystem");
        Service("SystemStartSvc", 0x10, 1, "LocalSystem");
        Service("UserAcctSvc", 0x10, 2, @".\svcuser");
        Service("TemplateSvc", 0x50, 2, "LocalSystem");
        Service("OddStartSvc", 0x10, 5, "LocalSystem");
        Service("OtherTypeSvc", 0x200, 2, "LocalSystem");
        V(Svc + @"\NoTypeSvc", "Start", 2);
        Service("Bad[Name]", 0x10, 2, "LocalSystem");

        ServiceVerdict Ev(string n) => ServiceRules.Evaluate(reg, n);
        Check(Ev("SafeSvc").Capture, "service: own-process, automatic, LocalSystem is captured");
        Check(Ev("NoAccountSvc").Capture, "service: shared-process, manual, no ObjectName is captured");
        Check(Ev("InteractiveSvc").Capture, "service: the interact-with-desktop bit does not block");
        Check(Ev("LocalSvc").Capture && Ev("VirtualSvc").Capture, "service: NT AUTHORITY and NT SERVICE accounts, disabled start mode, are captured");
        Check(!Ev("DriverSvc").Capture && Ev("DriverSvc").Reason.Contains("driver"), "service: kernel driver refused");
        Check(!Ev("BootSvc").Capture && Ev("BootSvc").Reason.Contains("boot"), "service: boot start refused");
        Check(!Ev("SystemStartSvc").Capture && Ev("SystemStartSvc").Reason.Contains("boot"), "service: system start refused");
        Check(!Ev("UserAcctSvc").Capture && Ev("UserAcctSvc").Reason.Contains("password"), "service: runs as a user account, refused");
        Check(!Ev("TemplateSvc").Capture && Ev("TemplateSvc").Reason.Contains("per-user"), "service: per-user template refused");
        Check(!Ev("OddStartSvc").Capture && Ev("OddStartSvc").Reason.Contains("start mode"), "service: start mode 5 refused");
        Check(!Ev("OtherTypeSvc").Capture, "service: type that is not own/shared process refused");
        Check(!Ev("NoTypeSvc").Capture && Ev("NoTypeSvc").Reason.Contains("could not be read"), "service: unreadable type refused");
        Check(!Ev("Bad[Name]").Capture && Ev("Bad[Name]").Reason.Contains("characters"), "service: name with a special character refused");
        Check(!Ev("NoSuchSvc").Capture && Ev("NoSuchSvc").Reason.Contains("not found"), "service: missing key refused");
        Check(!Ev("").Capture, "service: empty name refused");

        var withSub = ServiceRules.Patterns(reg, "SafeSvc");
        Check(withSub.Count == 2 && withSub[0] == @"HKLM\SYSTEM\CurrentControlSet\Services\SafeSvc [*]"
              && withSub[1] == @"HKLM\SYSTEM\CurrentControlSet\Services\SafeSvc\* [*]", "service patterns: key plus sub-keys when it has them");
        var noSub = ServiceRules.Patterns(reg, "NoAccountSvc");
        Check(noSub.Count == 1 && noSub[0] == @"HKLM\SYSTEM\CurrentControlSet\Services\NoAccountSvc [*]", "service patterns: key only when it has no sub-keys");

        // ---- Run values, StartupApproved, per-user entries
        V(Run64, "AcmeTray", "\"C:\\Program Files\\Acme\\tray.exe\" /min");
        V(Run64, "OtherApp", @"C:\Windows\x.exe");
        V(Run64, "Bad[1]", @"C:\Program Files\Acme\b.exe");
        V(Approved + @"\Run", "AcmeTray", new byte[] { 2, 0, 0, 0 });
        V(Run32, "AcmeLegacy", @"C:\Program Files\Acme\old.exe");
        V(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "AcmeUser", @"C:\Program Files\Acme\user.exe");

        // ---- Tasks
        V(Cache + @"\Tree\Acme\AcmeUpdater", "Id", Guid1);
        V(Cache + @"\Tasks\" + Guid1, "Path", @"\Acme\AcmeUpdater");
        V(Cache + @"\Logon\" + Guid1, "x", "1");                      // state key exists for this task only
        V(Cache + @"\Tree\AcmeTop", "Id", Guid6);
        V(Cache + @"\Tasks\" + Guid6, "Path", @"\AcmeTop");

        var tasks = new List<TaskInfo>
        {
            new(@"Acme\AcmeUpdater", new[] { "\"C:\\Program Files\\Acme\\upd.exe\" /c" }, true, Guid1),
            new(@"Acme\Elsewhere", new[] { @"C:\Windows\notepad.exe" }, true, "{22222222-2222-2222-2222-222222222222}"),
            new(@"Acme\NoTrigger", new[] { @"C:\Program Files\Acme\nt.exe" }, false, "{33333333-3333-3333-3333-333333333333}"),
            new(@"Acme\NoCacheId", new[] { @"C:\Program Files\Acme\nc.exe" }, true, null),
            new(@"Acme\Bad[1]", new[] { @"C:\Program Files\Acme\bad.exe" }, true, "{44444444-4444-4444-4444-444444444444}"),
            new(@"AcmeTop", new[] { @"C:\Program Files\Acme\top.exe" }, true, Guid6),
        };
        var userStartup = new List<ShortcutInfo>
        {
            new(@"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", "Acme.lnk", @"C:\Program Files\Acme\acme.exe"),
            new(@"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", "Other.lnk", @"C:\Windows\notepad.exe"),
        };

        var st = StartupRules.Derive(Folder, reg, tasks, userStartup, "C:");
        Check(st.RegistryPatterns.Contains(@"HKLM\" + Run64 + " [AcmeTray]"), "startup: all-users Run value of the app");
        Check(st.RegistryPatterns.Contains(@"HKLM\" + Approved + @"\Run [AcmeTray]"), "startup: StartupApproved value follows the Run value");
        Check(st.RegistryPatterns.Contains(@"HKLM\" + Run32 + " [AcmeLegacy]"), "startup: 32-bit view Run value");
        Check(!st.RegistryPatterns.Any(x => x.Contains("AcmeLegacy") && x.Contains("StartupApproved")), "startup: no StartupApproved rule when the value is absent");
        Check(!st.RegistryPatterns.Any(x => x.Contains("OtherApp")), "startup: Run value outside the folder not taken");
        Check(!st.RegistryPatterns.Any(x => x.Contains("Bad[1]") && x.Contains("Run")) && st.Notes.Any(n => n.Contains("Bad[1]") && n.Contains("characters")),
              "startup: Run value with a special character in its name reported, not taken");
        Check(!st.RegistryPatterns.Any(x => x.StartsWith("HKCU")) && st.Notes.Any(n => n.Contains("AcmeUser") && n.Contains("current user only")),
              "startup: HKCU Run value only noted");
        Check(st.Notes.Any(n => n.Contains("Startup folder shortcut") && n.Contains("Acme.lnk")) && !st.Notes.Any(n => n.Contains("Other.lnk")),
              "startup: per-user Startup shortcut noted, unrelated one ignored");

        Check(st.FilePatterns.Contains(@"C:\Windows\System32\Tasks\Acme [AcmeUpdater]"), "task: definition file pattern (folder + leaf)");
        Check(st.FilePatterns.Contains(@"C:\Windows\System32\Tasks [AcmeTop]"), "task: top-level task file pattern");
        Check(st.RegistryPatterns.Contains(@"HKLM\" + Cache + @"\Tree\Acme\AcmeUpdater [*]"), "task: TaskCache Tree key");
        Check(st.RegistryPatterns.Contains(@"HKLM\" + Cache + @"\Tasks\" + Guid1 + " [*]"), "task: TaskCache Tasks key by id");
        Check(st.RegistryPatterns.Contains(@"HKLM\" + Cache + @"\Logon\" + Guid1 + " [*]"), "task: Logon state key, because it exists");
        Check(!st.RegistryPatterns.Any(x => x.Contains(@"\Boot\") || x.Contains(@"\Plain\") || x.Contains(@"\Maintenance\")), "task: state keys that do not exist are not written");
        Check(st.Items.Contains(@"Scheduled task: \Acme\AcmeUpdater") && st.Items.Contains("Run entry: AcmeTray"), "startup: items list for the Configure step and the manifest");
        Check(!st.FilePatterns.Any(x => x.Contains("Elsewhere")), "task: command outside the folder not taken");
        Check(!st.FilePatterns.Any(x => x.Contains("NoTrigger")) && st.Notes.Any(n => n.Contains("NoTrigger") && n.Contains("no boot or logon trigger")),
              "task: no boot/logon trigger reported, not taken");
        Check(!st.FilePatterns.Any(x => x.Contains("NoCacheId")) && st.Notes.Any(n => n.Contains("NoCacheId") && n.Contains("cache entry")),
              "task: unreadable TaskCache id reported, not taken");
        Check(!st.FilePatterns.Any(x => x.Contains("Bad[1]")) && st.Notes.Any(n => n.Contains(@"Acme\Bad[1]") && n.Contains("characters")),
              "task: special character in the name reported, not taken");
        Check(st.RegistryPatterns.Count == st.RegistryPatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "startup: no duplicate registry patterns");

        // ---- Task XML parsing and the folder scan
        const string ns = "xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"";
        string Xml(string triggers, string actions) => "<?xml version=\"1.0\"?><Task " + ns + "><Triggers>" + triggers + "</Triggers><Actions>" + actions + "</Actions></Task>";
        const string execAcme = "<Exec><Command>\"C:\\Program Files\\Acme\\a.exe\"</Command></Exec>";

        var (c1, b1) = TaskScanner.ParseTaskXml(Xml("<LogonTrigger/>", execAcme));
        Check(c1.Count == 1 && c1[0] == "\"C:\\Program Files\\Acme\\a.exe\"" && b1, "task xml: Exec command and logon trigger");
        var (c2, b2) = TaskScanner.ParseTaskXml(Xml("<BootTrigger/>", execAcme));
        Check(c2.Count == 1 && b2, "task xml: boot trigger");
        var (c3, b3) = TaskScanner.ParseTaskXml(Xml("<TimeTrigger/>", execAcme));
        Check(c3.Count == 1 && !b3, "task xml: time trigger alone is not boot/logon");
        var (c4, _) = TaskScanner.ParseTaskXml(Xml("<LogonTrigger/>", "<ComHandler><ClassId>{x}</ClassId></ComHandler>"));
        Check(c4.Count == 0, "task xml: COM-handler task has no command");

        var tmpTasks = Path.Combine(Path.GetTempPath(), "tk-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(tmpTasks, "Acme"));
        Directory.CreateDirectory(Path.Combine(tmpTasks, "Microsoft", "Windows"));
        File.WriteAllText(Path.Combine(tmpTasks, "Acme", "AcmeUpdater"), Xml("<LogonTrigger/>", execAcme));
        File.WriteAllText(Path.Combine(tmpTasks, "Microsoft", "Windows", "Foo"), Xml("<LogonTrigger/>", execAcme));
        File.WriteAllText(Path.Combine(tmpTasks, "Broken"), "this is not xml");
        File.WriteAllText(Path.Combine(tmpTasks, "NoExec"), Xml("<LogonTrigger/>", "<ComHandler><ClassId>{x}</ClassId></ComHandler>"));
        var scanned = TaskScanner.Scan(tmpTasks, reg, CancellationToken.None);
        Check(scanned.Count == 1 && scanned[0].RelPath == @"Acme\AcmeUpdater" && scanned[0].TaskId == Guid1 && scanned[0].BootOrLogon,
              "task scan: reads the folder, skips Microsoft, broken and command-less files, takes the id from TaskCache");
        Check(TaskScanner.Scan(Path.Combine(tmpTasks, "missing"), reg, CancellationToken.None).Count == 0, "task scan: missing folder gives an empty list");
        try { TaskScanner.Scan(tmpTasks, reg, new CancellationToken(true)); Check(false, "task scan: cancellation is not swallowed"); }
        catch (OperationCanceledException) { Check(true, "task scan: cancellation is not swallowed"); }

        // ---- End to end: app scan -> plan -> options -> rules file -> manifest
        V(U64 + @"\AcmeTools", "DisplayName", "Acme Tools");
        V(U64 + @"\AcmeTools", "Publisher", "Acme Inc");
        V(U64 + @"\AcmeTools", "InstallLocation", Folder);
        V(U64 + @"\AcmeTools", "EstimatedSize", 1000);
        Service("AcmeSvc", 0x10, 2, "LocalSystem", "\"C:\\Program Files\\Acme\\svc.exe\" -k");
        Service("AcmeDrv", 0x01, 3, null, @"C:\Program Files\Acme\drv.sys");
        Service("AcmeUserSvc", 0x10, 2, @".\svcuser", @"C:\Program Files\Acme\usersvc.exe");
        Service("ForeignSvc", 0x10, 2, "LocalSystem", @"C:\Windows\System32\foreign.exe");

        var tmpStart = Path.Combine(Path.GetTempPath(), "sm-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmpStart);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Folder };
        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmpStart, _ => null, CancellationToken.None, tasks, userStartup);
        var acme = res.Apps.FirstOrDefault(a => a.App.DisplayName == "Acme Tools");
        Check(acme?.Plan is not null, "scan: the app has a plan");
        var plan = acme!.Plan!;

        Check(plan.ServiceItems.SequenceEqual(new[] { "Service: AcmeSvc" }), "plan: only the safe service is a service item");
        Check(plan.ServiceRegistryPatterns.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSvc [*]"), "plan: service rule written");
        Check(!plan.ServiceRegistryPatterns.Any(x => x.Contains("AcmeDrv") || x.Contains("AcmeUserSvc") || x.Contains("ForeignSvc")), "plan: driver, user-account and foreign services not captured");
        Check(plan.Notes.Any(n => n.Contains("\"AcmeDrv\"") && n.Contains("driver")), "plan: driver service reported with its reason");
        Check(plan.Notes.Any(n => n.Contains("\"AcmeUserSvc\"") && n.Contains("account")), "plan: user-account service reported with its reason");
        Check(!plan.Notes.Any(n => n.Contains("not captured (untested)")), "plan: the old blanket 'services are not captured' note is gone");
        Check(plan.StartupItems.Contains("Run entry: AcmeTray") && plan.StartupItems.Contains(@"Scheduled task: \Acme\AcmeUpdater"), "plan: startup items");
        Check(plan.Notes.Any(n => n.Contains("AcmeUser") && n.Contains("current user only")), "plan: per-user Run value reported in the notes");
        Check(!plan.RegistryPatterns.Any(x => x.Contains("Services") || x.Contains("TaskCache") || x.Contains(@"\Run [")), "plan: base rule lists stay free of service and startup rules");
        Check(!plan.FilePatterns.Any(x => x.Contains("System32")), "plan: base file list stays free of task files");

        var all = new CaptureOptions();
        var none = new CaptureOptions { IncludeServices = false, IncludeStartup = false };
        var svcOnly = new CaptureOptions { IncludeServices = true, IncludeStartup = false };
        var runOnly = new CaptureOptions { IncludeServices = false, IncludeStartup = true };
        Check(all.IncludeServices && all.IncludeStartup, "options: services and startup default to on");
        Check(plan.RegistryFor(all).Any(x => x.Contains("Services\\AcmeSvc")) && plan.RegistryFor(all).Any(x => x.Contains("TaskCache"))
              && plan.FilesFor(all).Any(x => x.Contains(@"System32\Tasks")), "options on: service, startup registry and task file rules present");
        Check(plan.RegistryFor(none).SequenceEqual(plan.RegistryPatterns) && plan.FilesFor(none).SequenceEqual(plan.FilePatterns), "options off: only the base rules");
        Check(plan.RegistryFor(svcOnly).Any(x => x.Contains("Services\\AcmeSvc")) && !plan.RegistryFor(svcOnly).Any(x => x.Contains("TaskCache") || x.Contains(@"\Run [")),
              "services only: no startup rules");
        Check(!plan.RegistryFor(runOnly).Any(x => x.Contains("Services\\AcmeSvc")) && plan.RegistryFor(runOnly).Any(x => x.Contains("TaskCache")),
              "startup only: no service rules");
        Check(plan.ServiceItemsFor(none).Count == 0 && plan.StartupItemsFor(none).Count == 0 && plan.ServiceItemsFor(all).Count == 1, "options: item lists follow the switches");

        all.Apps.Add(plan);
        none.Apps.Add(plan);
        var rulesAll = RulesXmlBuilder.Build(all, "C:");
        var rulesNone = RulesXmlBuilder.Build(none, "C:");
        Check(rulesAll.IncludePatterns.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSvc [*]")
              && rulesAll.IncludePatterns.Contains(@"C:\Windows\System32\Tasks\Acme [AcmeUpdater]"), "rules: include list carries the service and task rules when on");
        Check(!rulesNone.IncludePatterns.Any(x => x.Contains("Services\\AcmeSvc") || x.Contains("System32")), "rules: none of them when off");
        var xmlAll = rulesAll.Document.ToString();
        Check(xmlAll.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeSvc [*]") && xmlAll.Contains(@"C:\Windows\System32\Tasks\Acme [AcmeUpdater]"), "rules xml: patterns written into the app's include component");
        Check(!rulesNone.Document.ToString().Contains("AcmeSvc"), "rules xml: absent when the options are off");
        var typeOf = rulesAll.Document.Descendants("pattern").Where(e => e.Value.Contains("AcmeSvc")).Select(e => (string?)e.Attribute("type")).FirstOrDefault();
        Check(typeOf == "Registry", "rules xml: service pattern has type Registry");
        var typeOfTask = rulesAll.Document.Descendants("pattern").Where(e => e.Value.Contains("AcmeUpdater") && e.Value.StartsWith("C:")).Select(e => (string?)e.Attribute("type")).FirstOrDefault();
        Check(typeOfTask == "File", "rules xml: task definition pattern has type File");

        // ---- Manifest: per-app item lists and the two switches, and a round trip through the file
        var mfDir = Path.Combine(Path.GetTempPath(), "mf-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(mfDir);
        var ppkg = Path.Combine(mfDir, "usmt.ppkg");
        var mf = DeploymentManifest.Create(all, mfDir, "amd64", ppkg, false);
        Check(mf.IncludeServices && mf.IncludeStartup, "manifest: records both switches");
        Check(mf.Apps is { Length: 1 } && mf.Apps[0].Services!.Contains("Service: AcmeSvc") && mf.Apps[0].Startup!.Contains("Run entry: AcmeTray"), "manifest: per-app service and startup items");
        Check(mf.Apps![0].RegistryRules == plan.RegistryFor(all).Count && mf.Apps[0].FileRules == plan.FilesFor(all).Count, "manifest: rule counts follow the options");
        var mfNone = DeploymentManifest.Create(none, mfDir, "amd64", ppkg, false);
        Check(!mfNone.IncludeServices && !mfNone.IncludeStartup && mfNone.Apps![0].Services!.Length == 0 && mfNone.Apps[0].Startup!.Length == 0, "manifest: switches off, no items");
        DeploymentManifest.Write(ppkg, mf);
        var back = DeploymentManifest.TryRead(ppkg, out var problem);
        Check(back is not null && problem is null && back.IncludeServices && back.IncludeStartup
              && back.Apps![0].Services!.Contains("Service: AcmeSvc") && back.Apps[0].Startup!.Length == plan.StartupItems.Count, "manifest: round trip keeps the new fields");
        var oldJson = new AppEntry("x", "1", "p", @"C:\x", 1, 2);
        Check(oldJson.Services is null && oldJson.Startup is null, "manifest: an app entry without the new fields reads as null");

        try { Directory.Delete(tmpTasks, true); Directory.Delete(tmpStart, true); Directory.Delete(mfDir, true); } catch { }
    }
}
