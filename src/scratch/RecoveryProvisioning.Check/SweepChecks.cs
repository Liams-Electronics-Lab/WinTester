using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 3.20: the wider search. Registry strings elsewhere under SOFTWARE, firewall rules, ODBC sources and drivers, event-log
// sources, system environment variables (PATH only reported) and tasks with other triggers, all found by a path that names the
// app's folder; on by default (S135), switched by CaptureOptions.IncludeSweep. Called from Program.cs.
public static class SweepChecks
{
    private const string U64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Folder = @"C:\Program Files\AcmeSuite";

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
        App("AcmeStudio", "Acme Studio", "Acme", Folder);
        App("OtherTool", "Other Tool", "OtherCo", @"C:\Program Files\Other");

        // The vendor key the plan carries by itself: a path inside it must not be offered a second time.
        V(@"SOFTWARE\Acme\Sub", "Path", Folder + @"\x.dll");

        // Registry values elsewhere under SOFTWARE
        V(@"SOFTWARE\Contoso\Bridge", "Exe", Folder + @"\bridge.exe");
        V(@"SOFTWARE\Contoso\Bridge", "Other", @"C:\Program Files\Else\x.exe");
        V(@"SOFTWARE\WOW6432Node\Contoso\Bridge", "Home", Folder);
        V(@"SOFTWARE\Microsoft\Foo", "Path", Folder + @"\skipped.dll");                 // Microsoft's subtree is not walked
        V(@"SOFTWARE\Contoso\Def", "", Folder + @"\d.exe");                              // default value: reported only
        V(@"SOFTWARE\Contoso\Odd", "a[b", Folder + @"\o.exe");                           // value name a rule cannot express
        V(@"SOFTWARE\Contoso\Deep\a\b\c\d\e\f\g", "TooDeep", Folder + @"\deep.exe");     // beyond the depth bound

        // Environment
        V(SweepIndex.EnvironmentKey, "ACME_HOME", Folder);
        V(SweepIndex.EnvironmentKey, "Path", @"C:\Windows;" + Folder + @"\bin");
        V(SweepIndex.EnvironmentKey, "ELSE_HOME", @"C:\Else");

        // ODBC
        V(SweepIndex.OdbcIni + @"\AcmeDsn", "Driver", Folder + @"\odbc.dll");
        V(SweepIndex.OdbcIni + @"\OtherDsn", "Driver", @"C:\Windows\System32\sqlsrv32.dll");
        V(SweepIndex.OdbcIni + @"\ODBC Data Sources", "AcmeDsn", "Acme ODBC");
        V(SweepIndex.OdbcInst + @"\Acme ODBC", "Driver", Folder + @"\odbc.dll");
        V(SweepIndex.OdbcInst + @"\ODBC Drivers", "Acme ODBC", "Installed");

        // Event-log sources
        V(SweepIndex.EventLogKey + @"\Application\AcmeSvc", "EventMessageFile", Folder + @"\msg.dll");
        V(SweepIndex.EventLogKey + @"\Application\ElseSvc", "EventMessageFile", @"C:\Windows\System32\else.dll");

        // Firewall rules
        var fw = AppSuggestions.FirewallKey;
        V(fw, "{A1}", @"v2.31|Action=Allow|Active=TRUE|Dir=In|App=" + Folder + @"\acme.exe|Name=Acme Studio|");
        V(fw, "{B1}", @"v2.31|Action=Allow|Active=TRUE|Dir=In|App=C:\Windows\notepad.exe|Name=Notepad|");

        var tasks = new List<TaskInfo>
        {
            new(@"Acme\Weekly", new[] { Folder + @"\upd.exe" }, false, "{TID-1}"),
            new(@"Acme\Logon", new[] { Folder + @"\run.exe" }, true, "{TID-2}"),
            new(@"Acme\NoId", new[] { Folder + @"\x.exe" }, false, null),
            new(@"Else\Weekly", new[] { @"C:\Else\x.exe" }, false, "{TID-3}"),
        };

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Folder, @"C:\Program Files\Other" };
        var tmp = Path.Combine(Path.GetTempPath(), "sw-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None, tasks, null, AppProfileSet.BuiltIn);
        var acme = res.Apps.First(a => a.App.DisplayName == "Acme Studio").Plan!;
        check(acme.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Acme\* [*]"), "sweep: fixture, the plan carries the Acme vendor tree");

        var idx = SweepIndex.Build(reg, CancellationToken.None);
        var rules = AppSuggestions.ReadFirewall(reg, CancellationToken.None);
        var r = AppSweep.Derive(idx, rules, tasks, reg, "C:", acme);

        // ---- index
        check(!idx.Truncated && idx.KeysVisited > 0, "sweep: the index of a small registry is complete");
        check(idx.Hits.All(h => h.Data.Length > 0), "sweep: only text that looks like a path is indexed");
        check(!idx.Hits.Any(h => h.Key.StartsWith(@"SOFTWARE\Microsoft", StringComparison.OrdinalIgnoreCase)), "sweep: Microsoft's subtree is not walked");
        check(!idx.Hits.Any(h => h.Data.Contains("deep.exe")), "sweep: keys deeper than the bound are not read");
        check(SweepIndex.LooksLikePath(@"C:\x") && SweepIndex.LooksLikePath(@"%ProgramFiles%\x") && !SweepIndex.LooksLikePath("hello")
              && !SweepIndex.LooksLikePath(@"1:\x") && !SweepIndex.LooksLikePath(""), "sweep: path matcher");

        // ---- registry values
        check(r.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Contoso\Bridge [Exe]") && r.RegistryPatterns.Contains(@"HKLM\SOFTWARE\WOW6432Node\Contoso\Bridge [Home]"),
            "sweep: a value naming the folder is carried, in both registry views, as that value only");
        check(!r.RegistryPatterns.Any(p => p.Contains("[Other]")), "sweep: a value for another folder is not carried");
        check(!r.RegistryPatterns.Any(p => p.Contains(@"SOFTWARE\Acme\Sub")), "sweep: a value inside a key the plan already carries is left alone");
        check(!r.RegistryPatterns.Any(p => p.Contains(@"SOFTWARE\Microsoft\Foo")), "sweep: nothing under Microsoft is carried");
        check(r.Notes.Any(n => n.Contains(@"Contoso\Def") && n.Contains("default value")), "sweep: a default value is reported, not carried");
        check(r.Notes.Any(n => n.Contains("a[b")), "sweep: a value name a rule cannot express is reported");
        check(r.Items.Any(i => i.StartsWith("Registry values: 2")), "sweep: the registry values are summed up in one item");

        // ---- environment
        check(r.RegistryPatterns.Contains(@"HKLM\" + SweepIndex.EnvironmentKey + " [ACME_HOME]") && r.Items.Contains("Environment variable: ACME_HOME"),
            "sweep: an environment variable naming the folder is carried by value");
        check(!r.RegistryPatterns.Any(p => p.Contains("[Path]")) && !r.RegistryPatterns.Any(p => p.Contains("ELSE_HOME")),
            "sweep: PATH is never carried and an unrelated variable is not");
        check(r.Notes.Any(n => n.Contains("system PATH")), "sweep: a PATH entry inside the folder is reported");

        // ---- ODBC
        check(r.RegistryPatterns.Contains(@"HKLM\" + SweepIndex.OdbcIni + @"\AcmeDsn [*]")
              && r.RegistryPatterns.Contains(@"HKLM\" + SweepIndex.OdbcIni + @"\ODBC Data Sources [AcmeDsn]")
              && !r.RegistryPatterns.Any(p => p.Contains("OtherDsn")), "sweep: an ODBC data source is carried with its entry in the source list, another is not");
        check(r.RegistryPatterns.Contains(@"HKLM\" + SweepIndex.OdbcInst + @"\Acme ODBC [*]")
              && r.RegistryPatterns.Contains(@"HKLM\" + SweepIndex.OdbcInst + @"\ODBC Drivers [Acme ODBC]"), "sweep: an ODBC driver is carried with its entry in the driver list");

        // ---- event log
        check(r.RegistryPatterns.Contains(@"HKLM\" + SweepIndex.EventLogKey + @"\Application\AcmeSvc [*]")
              && !r.RegistryPatterns.Any(p => p.Contains("ElseSvc")), "sweep: an event-log source with a message file in the folder is carried");

        // ---- firewall
        check(r.RegistryPatterns.Contains(@"HKLM\" + fw + " [{A1}]") && !r.RegistryPatterns.Any(p => p.Contains("{B1}")) && r.Items.Contains("Firewall rules: 1"),
            "sweep: firewall rules by program path");

        // ---- tasks with other triggers
        check(r.FilePatterns.Contains(@"C:\Windows\System32\Tasks\Acme [Weekly]")
              && r.RegistryPatterns.Any(p => p.EndsWith(@"TaskCache\Tree\Acme\Weekly [*]")) && r.RegistryPatterns.Any(p => p.EndsWith(@"TaskCache\Tasks\{TID-1} [*]"))
              && r.Items.Contains(@"Scheduled task (other trigger): \Acme\Weekly"), "sweep: a task with another trigger is carried like a boot/logon task");
        check(!r.FilePatterns.Any(p => p.Contains("[Logon]")) && !r.FilePatterns.Any(p => p.Contains("Else")), "sweep: the boot/logon task (already in the plan) and an unrelated task are not repeated");
        check(r.Notes.Any(n => n.Contains(@"\Acme\NoId") && n.Contains("cache entry")), "sweep: a task without a cache id is reported");
        check(acme.StartupFilePatterns.Contains(@"C:\Windows\System32\Tasks\Acme [Logon]")
              && acme.Notes.Any(n => n.Contains(@"\Acme\Weekly") && n.Contains("no boot or logon trigger") && n.Contains("wider search")),
            "sweep: the base plan still has the boot/logon task and now says the wider search can carry the other one");

        // ---- an app nothing points to
        var other = res.Apps.First(a => a.App.DisplayName == "Other Tool").Plan!;
        var none = AppSweep.Derive(idx, rules, tasks, reg, "C:", other);
        check(none.Items.Count == 0 && none.RegistryPatterns.Count == 0 && none.FilePatterns.Count == 0, "sweep: an app with no matching path gets nothing");

        // ---- bounds
        var small = SweepIndex.Build(reg, CancellationToken.None, maxKeys: 3);
        check(small.Truncated, "sweep: a key limit stops the walk and sets Truncated");
        check(AppSweep.Derive(small, rules, tasks, reg, "C:", acme).Notes.Any(n => n.Contains("stopped at its limit")), "sweep: a truncated index says so in the result");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var threw = false;
        try { SweepIndex.Build(reg, cancelled.Token); } catch (OperationCanceledException) { threw = true; }
        check(threw, "sweep: cancelling stops the build");

        // ---- Covered
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"HKLM\SOFTWARE\A [*]", @"HKLM\SOFTWARE\B\* [*]" };
        check(AppSweep.Covered(set, @"SOFTWARE\A") && !AppSweep.Covered(set, @"SOFTWARE\A\x") && AppSweep.Covered(set, @"SOFTWARE\B\x\y")
              && !AppSweep.Covered(set, @"SOFTWARE\B") && !AppSweep.Covered(set, @"SOFTWARE\C"), "sweep: Covered follows the [*] and \\* [*] rules");

        // ---- on by default (S135), off when switched off
        var off = new CaptureOptions { IncludeSweep = false };
        check(new CaptureOptions().IncludeSweep, "sweep: the option is on by default");
        var plan2 = acme.WithSweep(r);
        check(!plan2.RegistryFor(off).Any(p => p.Contains("Contoso")) && plan2.FilesFor(off).SequenceEqual(acme.FilesFor(off)) && plan2.SweepItemsFor(off).Count == 0,
            "sweep: with the option off the plan is what it was");
        var on = new CaptureOptions { IncludeSweep = true };
        check(plan2.RegistryFor(on).Contains(@"HKLM\SOFTWARE\Contoso\Bridge [Exe]") && plan2.FilesFor(on).Contains(@"C:\Windows\System32\Tasks\Acme [Weekly]")
              && plan2.SweepItemsFor(on).Count == r.Items.Count, "sweep: with the option on the plan carries the sweep rules and items");
        check(plan2.RegistryFor(new CaptureOptions { IncludeSweep = true, IncludeServices = false, IncludeStartup = false }).Contains(@"HKLM\SOFTWARE\Contoso\Bridge [Exe]"),
            "sweep: independent of the services and startup options");

        // ---- rules file, manifest, suggestions
        on.Apps.Add(plan2);
        var built = RulesXmlBuilder.Build(on, "C:");
        check(built.IncludePatterns.Contains(@"HKLM\SOFTWARE\Contoso\Bridge [Exe]") && built.IncludePatterns.Contains(@"HKLM\" + fw + " [{A1}]")
              && built.IncludePatterns.Contains(@"C:\Windows\System32\Tasks\Acme [Weekly]"), "sweep: the rules file carries the sweep patterns");
        var offOpts = new CaptureOptions { IncludeSweep = false };
        offOpts.Apps.Add(plan2);
        check(!RulesXmlBuilder.Build(offOpts, "C:").IncludePatterns.Any(p => p.Contains("Contoso")), "sweep: the rules file has none of them when the option is off");
        var man = DeploymentManifest.Create(on, tmp, "amd64", Path.Combine(tmp, "x.ppkg"), false);
        check(man.IncludeSweep && man.Apps![0].Sweep is { Length: > 0 } && man.Apps[0].Sweep!.Contains("Firewall rules: 1"), "sweep: the manifest records the option and the items");
        var manOff = DeploymentManifest.Create(offOpts, tmp, "amd64", Path.Combine(tmp, "x.ppkg"), false);
        check(!manOff.IncludeSweep && manOff.Apps![0].Sweep is null, "sweep: the manifest has no sweep list when the option was off");
        var sug = AppSuggestions.Apply(plan2, Array.Empty<AppSuggestion>());
        check(ReferenceEquals(sug, plan2) && AppSuggestions.Apply(plan2, new[] { new AppSuggestion(SuggestionKind.Folder, @"C:\ProgramData\Acme", "r", 0, null,
                new[] { @"C:\ProgramData\Acme\* [*]" }, Array.Empty<string>()) }).SweepItems.Count == plan2.SweepItems.Count, "sweep: applying suggestions keeps the sweep results");

        try { Directory.Delete(tmp, true); } catch { }
    }
}
