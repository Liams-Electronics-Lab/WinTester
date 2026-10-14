using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 3.19: unticked suggestions for a ticked app (same-name folders and keys, firewall rules by program path) with a reason, a
// licence warning where a name looks like one, and Apply adding only what the operator ticked. Called from Program.cs.
public static class SuggestionChecks
{
    private const string U64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

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
        // Publisher "Acme", app "Acme Studio", installed in a folder with a third name: three candidate names.
        App("AcmeStudio", "Acme Studio", "Acme", @"C:\Program Files\AcmeSuite");
        App("OtherTool", "Other Tool", "OtherCo", @"C:\Program Files\Other");
        App("ZedTool", "Zed Tool", "Intel", @"C:\Program Files\Zed");

        V(@"SOFTWARE\Acme", "Setting", "1");                              // carried by the plan already: not a suggestion
        V(@"SOFTWARE\AcmeSuite", "LicenseKey", "x");                      // install folder name, with a licence-looking value
        V(@"SOFTWARE\WOW6432Node\AcmeSuite", "Setting", "1");
        V(@"SOFTWARE\Intel", "Setting", "1");                             // never taken as a vendor key

        var fw = AppSuggestions.FirewallKey;
        V(fw, "{R1}", @"v2.31|Action=Allow|Active=TRUE|Dir=In|App=C:\Program Files\AcmeSuite\acme.exe|Name=Acme Studio Inbound|");
        V(fw, "{R2}", @"v2.31|Action=Allow|Active=TRUE|Dir=In|App=C:\Windows\notepad.exe|Name=Notepad|");
        V(fw, "{R3}", @"v2.31|Action=Allow|Active=TRUE|Dir=Out|App=C:\Program Files\AcmeSuite\sub\x.exe|Name=@{Pkg?ms-resource:x}|");
        V(fw, "Bad[1]", @"v2.31|Action=Allow|App=C:\Program Files\AcmeSuite\bad.exe|Name=Bad|");
        V(fw, "{R5}", @"v2.31|Action=Allow|Svc=foo|Name=NoProgram|");

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\AcmeSuite", @"C:\Program Files\Other", @"C:\Program Files\Zed",
            @"C:\ProgramData\Acme", @"C:\ProgramData\Acme Studio", @"C:\ProgramData\Other2",
            @"C:\Program Files\Common Files\Acme", @"C:\Program Files (x86)\Acme", @"C:\ProgramData\Intel"
        };

        IReadOnlyList<FsEntry> List(string d) => d switch
        {
            @"C:\ProgramData\Acme" => new[] { new FsEntry("settings.ini", false), new FsEntry("Data", true) },
            @"C:\ProgramData\Acme\Data" => new[] { new FsEntry("License.lic", false) },
            @"C:\ProgramData\Acme Studio" => new[] { new FsEntry("notes.txt", false) },
            _ => Array.Empty<FsEntry>()
        };

        var tmp = Path.Combine(Path.GetTempPath(), "sg-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        var res = AppScanner.Scan(reg, "C:", p => dirs.Contains(p), p => 4096, tmp, _ => null, CancellationToken.None,
            null, null, AppProfileSet.BuiltIn);
        var ctx = res.Context!;
        var acme = res.Apps.First(a => a.App.DisplayName == "Acme Studio");
        var plan = acme.Plan!;
        check(plan.RegistryPatterns.Contains(@"HKLM\SOFTWARE\Acme [*]"), "suggest: fixture, the plan already carries HKLM\\SOFTWARE\\Acme");

        var found = AppSuggestions.Find(ctx, acme.App, plan, CancellationToken.None, List);
        var folders = found.Where(s => s.Kind == SuggestionKind.Folder).ToList();
        var keysFound = found.Where(s => s.Kind == SuggestionKind.RegistryKey).ToList();
        var fwSug = found.FirstOrDefault(s => s.Kind == SuggestionKind.FirewallRules);

        // ---- folders
        check(folders.Select(f => f.Target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(new[]
            { @"C:\Program Files (x86)\Acme", @"C:\Program Files\Common Files\Acme", @"C:\ProgramData\Acme", @"C:\ProgramData\Acme Studio" }, StringComparer.OrdinalIgnoreCase),
            "suggest: same-name folders that exist are offered (ProgramData, Common Files, the other Program Files), and nothing else");
        check(!folders.Any(f => f.Target.Equals(@"C:\Program Files\AcmeSuite", StringComparison.OrdinalIgnoreCase)), "suggest: the captured folder itself is not offered");
        var pd = folders.First(f => f.Target == @"C:\ProgramData\Acme");
        check(pd.Reason.Contains("ProgramData") && pd.Reason.Contains("publisher"), "suggest: the reason names the place and the match (publisher)");
        check(folders.First(f => f.Target == @"C:\ProgramData\Acme Studio").Reason.Contains("app name"), "suggest: the reason says app name for the display-name match");
        check(pd.FilePatterns.SequenceEqual(new[] { @"C:\ProgramData\Acme\* [*]" }) && pd.RegistryPatterns.Count == 0, "suggest: a folder suggestion is one include rule");
        check(folders.First(f => f.Target == @"C:\Program Files (x86)\Acme").FilePatterns[0] == @"%ProgramFiles(x86)%\Acme\* [*]", "suggest: a Program Files (x86) folder uses the variable form");
        check(pd.SizeBytes == -1, "suggest: folder sizes are not measured by Find");

        // ---- licence warnings
        check(pd.Warning is { } w && w.Contains("License.lic"), "suggest: a licence-looking file two levels down gives a warning");
        check(folders.First(f => f.Target == @"C:\ProgramData\Acme Studio").Warning is null, "suggest: a folder without such names has no warning");
        check(AppSuggestions.LooksLikeLicence("App.LIC") && AppSuggestions.LooksLikeLicence("licence.txt") && AppSuggestions.LooksLikeLicence("machineid.dat")
              && AppSuggestions.LooksLikeLicence("x.key") && !AppSuggestions.LooksLikeLicence("settings.ini") && !AppSuggestions.LooksLikeLicence("monkey.txt"),
            "suggest: licence name matcher");

        // ---- registry keys
        check(keysFound.Select(k => k.Target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                  .SequenceEqual(new[] { @"HKLM\SOFTWARE\AcmeSuite", @"HKLM\SOFTWARE\WOW6432Node\AcmeSuite" }, StringComparer.OrdinalIgnoreCase),
            "suggest: keys named like the install folder are offered in both views; the key the plan carries is not");
        var ks = keysFound.First(k => k.Target == @"HKLM\SOFTWARE\AcmeSuite");
        check(ks.RegistryPatterns.SequenceEqual(new[] { @"HKLM\SOFTWARE\AcmeSuite [*]", @"HKLM\SOFTWARE\AcmeSuite\* [*]" }) && ks.Reason.Contains("install folder"),
            "suggest: a key suggestion is the key and its sub-keys, with the reason");
        check(ks.Warning is { } kw && kw.Contains("LicenseKey") && keysFound.First(k => k.Target.Contains("WOW6432Node")).Warning is null,
            "suggest: a licence-looking value name gives a registry warning, other keys none");

        // ---- firewall rules
        check(fwSug is not null && fwSug.RegistryPatterns.Count == 2
              && fwSug.RegistryPatterns.Contains(@"HKLM\" + fw + " [{R1}]") && fwSug.RegistryPatterns.Contains(@"HKLM\" + fw + " [{R3}]"),
            "suggest: firewall rules whose program is inside the folder are offered, one value pattern each");
        check(fwSug is not null && fwSug.Reason.Contains("1 more not offered"), "suggest: a rule name with special characters is counted, not offered");
        check(fwSug is not null && fwSug.Details.Contains("Acme Studio Inbound") && fwSug.Details.Contains("{R3}"), "suggest: rule names for the tooltip (resource-style names fall back to the value name)");
        check(AppSuggestions.ParseRule("{x}", "v2.31|Action=Allow|Svc=foo|") is null, "suggest: a rule without a program path is ignored");

        // ---- nothing to offer, never-keys
        var other = res.Apps.First(a => a.App.DisplayName == "Other Tool");
        check(AppSuggestions.Find(ctx, other.App, other.Plan!, CancellationToken.None, List).Count == 0, "suggest: an app with no matching folder, key or rule gets no suggestions");
        var zed = res.Apps.First(a => a.App.DisplayName == "Zed Tool");
        var zs = AppSuggestions.Find(ctx, zed.App, zed.Plan!, CancellationToken.None, List);
        check(!zs.Any(s => s.Target.Contains("Intel")), "suggest: a vendor name on the never list (Intel) is not used for folders or keys");

        // ---- measure
        var measured = AppSuggestions.Measure(ctx, found, CancellationToken.None);
        check(measured.Where(s => s.Kind == SuggestionKind.Folder).All(s => s.SizeBytes == 4096)
              && measured.Where(s => s.Kind != SuggestionKind.Folder).All(s => s.SizeBytes == 0), "suggest: Measure fills folder sizes and leaves the others");

        // ---- apply
        check(ReferenceEquals(AppSuggestions.Apply(plan, Array.Empty<AppSuggestion>()), plan), "apply: nothing ticked leaves the plan as it was");
        var ticked = measured.Where(s => s.Target == @"C:\ProgramData\Acme" || s.Target == @"HKLM\SOFTWARE\AcmeSuite" || s.Kind == SuggestionKind.FirewallRules).ToList();
        var plan2 = AppSuggestions.Apply(plan, ticked);
        check(plan2.FilePatterns.Contains(@"C:\ProgramData\Acme\* [*]") && !plan2.FilePatterns.Contains(@"C:\ProgramData\Acme Studio\* [*]"), "apply: only the ticked folder is added");
        check(plan2.RegistryPatterns.Contains(@"HKLM\SOFTWARE\AcmeSuite [*]") && plan2.RegistryPatterns.Contains(@"HKLM\SOFTWARE\AcmeSuite\* [*]")
              && plan2.RegistryPatterns.Contains(@"HKLM\" + fw + " [{R1}]") && !plan2.RegistryPatterns.Contains(@"HKLM\SOFTWARE\WOW6432Node\AcmeSuite [*]"),
            "apply: ticked key and firewall rules are added, the unticked key is not");
        check(plan2.SizeBytes == plan.SizeBytes + 4096, "apply: only measured folder sizes of ticked suggestions count");
        check(plan2.SuggestedItems.Count == 3 && plan2.SuggestedItems.Contains(@"Folder C:\ProgramData\Acme") && plan2.SuggestedItems.Contains("Firewall rules (2)"), "apply: labels for the log");
        check(!plan.FilePatterns.Contains(@"C:\ProgramData\Acme\* [*]"), "apply: the original plan is not changed");

        var o = new CaptureOptions();
        o.Apps.Add(plan2);
        var rules = RulesXmlBuilder.Build(o, "C:");
        check(rules.IncludePatterns.Contains(@"C:\ProgramData\Acme\* [*]") && rules.IncludePatterns.Contains(@"HKLM\SOFTWARE\AcmeSuite [*]")
              && rules.IncludePatterns.Contains(@"HKLM\" + fw + " [{R1}]"), "apply: the ticked suggestions reach the rules file");
        var guard = ExclusionGuard.ProtectedFor("C:", false, false, new[] { plan2 });
        check(ExclusionGuard.Check(@"C:\ProgramData\Acme\", "C:", guard) is not null, "apply: an extra-path line that would exclude a ticked suggestion folder is refused");

        try { Directory.Delete(tmp, true); } catch { }
    }
}
