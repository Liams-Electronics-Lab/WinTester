using WinTester.App.Deployment.RecoveryProvisioning;

// Shell-extension rules (context-menu / drag-and-drop handlers): handler key, CLSID key and Approved value for handlers whose
// in-process server sits inside the app folder. Called from Program.cs.
public static class ShellExtChecks
{
    private const string G7 = "{23170F69-40C1-278A-1000-000100020000}";
    private const string GFoo = "{11111111-2222-3333-4444-555555555555}";
    private const string GNamed = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
    private const string Cls = @"SOFTWARE\Classes";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    public static void Run(Action<bool, string> check)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }

        // 7-Zip as the S120 test machine showed it: handlers on *, Directory, Folder and Drag-drop, one CLSID, an Approved value
        foreach (var c in new[] { @"*\shellex\ContextMenuHandlers", @"Directory\shellex\ContextMenuHandlers", @"Folder\shellex\ContextMenuHandlers",
                                  @"Directory\shellex\DragDropHandlers" })
        {
            K(Cls + "\\" + c + @"\7-Zip");
            vals[Cls + "\\" + c + @"\7-Zip|"] = G7;
        }
        K(Cls + @"\CLSID\" + G7 + @"\InprocServer32");
        vals[Cls + @"\CLSID\" + G7 + @"\InprocServer32|"] = @"C:\Program Files\7-Zip\7-zip.dll";
        K(Approved);
        vals[Approved + "|" + G7] = "7-Zip Shell Extension";

        // Another app's handler, server outside the 7-Zip folder
        K(Cls + @"\*\shellex\ContextMenuHandlers\FooMenu");
        vals[Cls + @"\*\shellex\ContextMenuHandlers\FooMenu|"] = GFoo;
        K(Cls + @"\CLSID\" + GFoo + @"\InprocServer32");
        vals[Cls + @"\CLSID\" + GFoo + @"\InprocServer32|"] = @"C:\Program Files\Foo\foo.dll";

        // Handler whose key name is the GUID and whose default value is empty
        K(Cls + @"\Folder\shellex\ContextMenuHandlers\" + GNamed);
        K(Cls + @"\CLSID\" + GNamed + @"\InprocServer32");
        vals[Cls + @"\CLSID\" + GNamed + @"\InprocServer32|"] = @"C:\Program Files\7-Zip\named.dll";

        // Handler whose CLSID key is missing: nothing to judge, skipped
        K(Cls + @"\Drive\shellex\ContextMenuHandlers\Ghost");
        vals[Cls + @"\Drive\shellex\ContextMenuHandlers\Ghost|"] = "{99999999-9999-9999-9999-999999999999}";

        var reg = new AppDiscoveryChecks.Fake(keys, vals);
        var refs = ShellExtRules.Scan(reg, CancellationToken.None);

        check(refs.Count(r => r.Label == "7-Zip") == 4, "shellext: the 7-Zip handler is found on all four class/kind locations");
        check(refs.All(r => r.Label != "Ghost"), "shellext: a handler with no CLSID key is skipped");
        var star = refs.First(r => r.RulePath.Contains(@"\*\shellex\ContextMenuHandlers\7-Zip"));
        check(star.Patterns.Contains(@"HKLM\SOFTWARE\Classes\*\shellex\ContextMenuHandlers\7-Zip [*]"), "shellext: class * is written as the wildcard path");
        check(star.Patterns.Contains(@"HKLM\SOFTWARE\Classes\CLSID\" + G7 + " [*]") && star.Patterns.Contains(@"HKLM\SOFTWARE\Classes\CLSID\" + G7 + @"\* [*]"), "shellext: CLSID key and its sub-keys");
        check(star.Patterns.Contains(@"HKLM\" + Approved + " [" + G7 + "]"), "shellext: Approved value pattern");
        check(!star.Patterns.Any(p => p.Contains("WOW6432Node")), "shellext: no WOW6432Node CLSID rule when that key does not exist");
        check(refs.Any(r => r.Label == GNamed), "shellext: GUID taken from the key name when the default value is empty");

        // Matched to an app through the server path
        var idx = SystemRegistryIndex.Build(reg, CancellationToken.None);
        var app = new InstalledApp("7-Zip", "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip", "7-Zip 26.03 (x64)", "Igor Pavlov",
            "26.03", @"C:\Program Files\7-Zip", "InstallLocation", 0, false, false, false, false, null);
        var plan = AppFootprint.Build(app, idx, Array.Empty<ShortcutInfo>(), reg, "C:");
        check(plan.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Classes\Directory\shellex\DragDropHandlers\7-Zip [*]"), "shellext: plan carries the drag-and-drop handler");
        check(plan.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Classes\CLSID\" + G7 + " [*]"), "shellext: plan carries the CLSID key");
        check(plan.RegistryPatterns.Contains(@"HKLM\" + Approved + " [" + G7 + "]"), "shellext: plan carries the Approved value");
        check(plan.RegistryPatterns.Any(p => p.Contains(GNamed)), "shellext: plan carries the GUID-named handler (server inside the folder)");
        check(!plan.RegistryPatterns.Any(p => p.Contains("FooMenu") || p.Contains(GFoo)), "shellext: another app's handler is not carried");
        check(plan.ShellItems.Contains("Shell extension: 7-Zip"), "shellext: item listed for the log");
        check(plan.RegistryPatterns.Count == plan.RegistryPatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "shellext: no duplicate patterns");

        // An app with no handlers gets nothing extra
        var foo = app with { KeyName = "Foo", UninstallRel = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo", DisplayName = "Other", Folder = @"C:\Program Files\Other" };
        var p2 = AppFootprint.Build(foo, idx, Array.Empty<ShortcutInfo>(), reg, "C:");
        check(p2.ShellItems.Count == 0 && !p2.RegistryPatterns.Any(p => p.Contains("shellex")), "shellext: an app without handlers has no shell rules");
    }
}
