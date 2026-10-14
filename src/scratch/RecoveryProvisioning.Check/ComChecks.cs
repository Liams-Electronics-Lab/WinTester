using WinTester.App.Deployment.RecoveryProvisioning;

// COM registration rules: CLSID, AppID, TypeLib, Interface and ProgID keys for classes whose server sits inside the app
// folder. Called from Program.cs.
public static class ComChecks
{
    private const string Cls = @"SOFTWARE\Classes";
    private const string G1 = "{10000000-0000-0000-0000-000000000001}";   // in-process server in the app folder, full set of keys
    private const string G2 = "{10000000-0000-0000-0000-000000000002}";   // local server (quoted exe with arguments)
    private const string G3 = "{10000000-0000-0000-0000-000000000003}";   // 32-bit view only, another app
    private const string G4 = "{10000000-0000-0000-0000-000000000004}";   // server in the Windows folder
    private const string G5 = "{10000000-0000-0000-0000-000000000005}";   // managed server, CodeBase inside the app folder
    private const string G6 = "{10000000-0000-0000-0000-000000000006}";   // server path written with %SystemRoot%
    private const string G7 = "{10000000-0000-0000-0000-000000000007}";   // server outside any app folder under test
    private const string A1 = "{20000000-0000-0000-0000-000000000001}";
    private const string T1 = "{30000000-0000-0000-0000-000000000001}";
    private const string I1 = "{40000000-0000-0000-0000-000000000001}";   // names T1
    private const string I2 = "{40000000-0000-0000-0000-000000000002}";   // proxy/stub is G1
    private const string I3 = "{40000000-0000-0000-0000-000000000003}";   // unrelated

    public static void Run(Action<bool, string> check)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        void V(string path, string name, string value) { K(path); vals[path + "|" + name] = value; }

        // G1: in-process server, ProgID and version-independent ProgID (CurVer chain), AppID, TypeLib, two interfaces
        V(Cls + @"\CLSID\" + G1, "", "Foo Class");
        V(Cls + @"\CLSID\" + G1 + @"\InprocServer32", "", @"C:\Program Files\FooApp\foo.dll");
        V(Cls + @"\CLSID\" + G1 + @"\ProgID", "", "Foo.Object.1");
        V(Cls + @"\CLSID\" + G1 + @"\VersionIndependentProgID", "", "Foo.Object");
        V(Cls + @"\CLSID\" + G1 + @"\TypeLib", "", T1);
        V(Cls + @"\CLSID\" + G1, "AppID", A1);
        K(Cls + @"\Foo.Object.1");
        V(Cls + @"\Foo.Object\CurVer", "", "Foo.Object.1");
        K(Cls + @"\AppID\" + A1);
        K(Cls + @"\TypeLib\" + T1 + @"\1.0\0\win32");
        V(Cls + @"\Interface\" + I1 + @"\TypeLib", "", T1);
        V(Cls + @"\Interface\" + I2 + @"\ProxyStubClsid32", "", G1);
        V(Cls + @"\Interface\" + I3 + @"\TypeLib", "", "{30000000-0000-0000-0000-0000000000FF}");

        // G2: local server named by a quoted command line; the AppID key named after the exe points back at A1
        V(Cls + @"\CLSID\" + G2, "", "Foo Automation");
        V(Cls + @"\CLSID\" + G2 + @"\LocalServer32", "", "\"C:\\Program Files\\FooApp\\foo.exe\" /Embedding");
        V(Cls + @"\CLSID\" + G2, "AppID", A1);
        V(Cls + @"\AppID\foo.exe", "AppID", A1);

        // G3: 32-bit view only, belongs to another app
        V(Cls + @"\WOW6432Node\CLSID\" + G3, "", "Wow Class");
        V(Cls + @"\WOW6432Node\CLSID\" + G3 + @"\InprocServer32", "", @"C:\Program Files (x86)\Bar\wow.dll");

        // G4, G6: servers in the Windows folder are never indexed
        V(Cls + @"\CLSID\" + G4, "", "WinClass");
        V(Cls + @"\CLSID\" + G4 + @"\InprocServer32", "", @"C:\Windows\System32\win.dll");
        V(Cls + @"\CLSID\" + G6, "", "WinVarClass");
        V(Cls + @"\CLSID\" + G6 + @"\InprocServer32", "", @"%SystemRoot%\system32\winvar.dll");

        // G5: managed server, the default value is a bare mscoree.dll and the path is in CodeBase
        V(Cls + @"\CLSID\" + G5, "", "Managed Class");
        V(Cls + @"\CLSID\" + G5 + @"\InprocServer32", "", "mscoree.dll");
        V(Cls + @"\CLSID\" + G5 + @"\InprocServer32", "CodeBase", "file:///C:/Program Files/FooApp/managed.dll");

        // G7: a class with a server elsewhere and a key a rule cannot express (comma in the ProgID)
        V(Cls + @"\CLSID\" + G7, "", "Elsewhere");
        V(Cls + @"\CLSID\" + G7 + @"\InprocServer32", "", @"C:\Program Files\Other\other.dll");
        V(Cls + @"\CLSID\" + G7 + @"\ProgID", "", "Bad,Progid");

        // A class with no server at all
        V(Cls + @"\CLSID\{10000000-0000-0000-0000-0000000000AA}", "", "NoServer");

        var reg = new AppDiscoveryChecks.Fake(keys, vals);
        var refs = ComRules.Scan(reg, CancellationToken.None);

        check(refs.Any(r => r.Label == "Foo Class") && refs.Any(r => r.Label == "Foo Automation") && refs.Any(r => r.Label == "Managed Class"),
            "com: classes with a server outside the Windows folder are indexed");
        check(refs.All(r => r.Label != "WinClass" && r.Label != "WinVarClass"), "com: servers under the Windows folder are not indexed");
        check(refs.All(r => r.Label != "NoServer"), "com: a class without a server is not indexed");
        check(refs.Any(r => r.Label == "Wow Class" && r.RulePath.Contains("WOW6432Node")), "com: the 32-bit view is scanned");

        var foo = refs.First(r => r.Label == "Foo Class");
        var p1 = foo.Patterns;
        string Root(string rel) => @"HKLM\" + Cls + "\\" + rel;
        check(p1.Contains(Root(@"CLSID\" + G1) + " [*]") && p1.Contains(Root(@"CLSID\" + G1) + @"\* [*]"), "com: CLSID key and its sub-keys");
        check(p1.Contains(Root(@"AppID\" + A1) + " [*]"), "com: AppID key by GUID");
        check(!p1.Contains(Root(@"AppID\" + A1) + @"\* [*]"), "com: no sub-key rule for a key without sub-keys");
        check(p1.Contains(Root(@"TypeLib\" + T1) + " [*]") && p1.Contains(Root(@"TypeLib\" + T1) + @"\* [*]"), "com: TypeLib key and its sub-keys");
        check(p1.Contains(Root(@"Interface\" + I1) + " [*]"), "com: interface that names the type library");
        check(p1.Contains(Root(@"Interface\" + I2) + " [*]"), "com: interface that uses the class as proxy/stub");
        check(!p1.Any(p => p.Contains(I3)), "com: an unrelated interface is not carried");
        check(p1.Contains(Root("Foo.Object.1") + " [*]"), "com: ProgID key");
        check(p1.Contains(Root("Foo.Object") + " [*]") && p1.Contains(Root("Foo.Object") + @"\* [*]"), "com: version-independent ProgID key with its CurVer sub-key");
        check(!p1.Any(p => p.Contains("WOW6432Node")), "com: no WOW6432Node rule when those keys do not exist");

        var auto = refs.First(r => r.Label == "Foo Automation");
        check(auto.Patterns.Contains(Root(@"AppID\foo.exe") + " [*]"), "com: AppID key named after the server exe when it points at the same AppID");
        check(auto.Data.Any(d => d.Contains("foo.exe")), "com: local server command line is kept for the folder match");

        var managed = refs.First(r => r.Label == "Managed Class");
        check(managed.Data.Count == 1 && managed.Data[0].Contains("managed.dll"), "com: managed server judged by its CodeBase, bare mscoree.dll dropped");

        var other = refs.First(r => r.Label == "Elsewhere");
        check(!other.Patterns.Any(p => p.Contains("Bad,Progid")), "com: a ProgID with a special character is left out");

        // Matched to an app through the server path
        var idx = SystemRegistryIndex.Build(reg, CancellationToken.None);
        var app = new InstalledApp("FooApp", "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FooApp", "FooApp", "Foo Inc",
            "1.0", @"C:\Program Files\FooApp", "InstallLocation", 0, false, false, false, false, null);
        var plan = AppFootprint.Build(app, idx, Array.Empty<ShortcutInfo>(), reg, "C:");
        check(plan.RegistryPatterns.Contains(Root(@"CLSID\" + G1) + " [*]") && plan.RegistryPatterns.Contains(Root(@"CLSID\" + G2) + " [*]")
              && plan.RegistryPatterns.Contains(Root(@"CLSID\" + G5) + " [*]"), "com: plan carries the three classes of this app");
        check(plan.RegistryPatterns.Contains(Root(@"TypeLib\" + T1) + " [*]") && plan.RegistryPatterns.Contains(Root(@"Interface\" + I2) + " [*]"),
            "com: plan carries the type library and interfaces");
        check(!plan.RegistryPatterns.Any(p => p.Contains(G3) || p.Contains(G7)), "com: another app's classes are not carried");
        check(plan.ComItems.Count == 1 && plan.ComItems[0].StartsWith("COM registration: 3 class(es)"), "com: one summary item for the log");
        check(plan.RegistryPatterns.Count == plan.RegistryPatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "com: no duplicate patterns");

        var bar = app with { KeyName = "Bar", UninstallRel = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Bar", DisplayName = "Bar",
            Folder = @"C:\Program Files (x86)\Bar" };
        var barPlan = AppFootprint.Build(bar, idx, Array.Empty<ShortcutInfo>(), reg, "C:");
        check(barPlan.RegistryPatterns.Contains(Root(@"WOW6432Node\CLSID\" + G3) + " [*]"), "com: plan carries a 32-bit class");

        var none = app with { KeyName = "Nothing", UninstallRel = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Nothing", DisplayName = "Nothing",
            Folder = @"C:\Program Files\Nothing" };
        var nonePlan = AppFootprint.Build(none, idx, Array.Empty<ShortcutInfo>(), reg, "C:");
        check(nonePlan.ComItems.Count == 0 && !nonePlan.RegistryPatterns.Any(p => p.Contains(@"\CLSID\")), "com: an app without COM classes has no COM rules");

        // Caps: classes per app and interfaces per class
        var k2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var v2 = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K2(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) k2.Add(string.Join("\\", parts[..i])); }
        void V2(string path, string name, string value) { K2(path); v2[path + "|" + name] = value; }
        for (var i = 1; i <= FootprintCaps.Classes + 5; i++)
        {
            var g = "{50000000-0000-0000-0000-" + i.ToString("D12") + "}";
            V2(Cls + @"\CLSID\" + g, "", "Many " + i);
            V2(Cls + @"\CLSID\" + g + @"\InprocServer32", "", @"C:\Program Files\Many\m" + i + ".dll");
        }
        var manyReg = new AppDiscoveryChecks.Fake(k2, v2);
        var manyIdx = SystemRegistryIndex.Build(manyReg, CancellationToken.None);
        var many = app with { KeyName = "Many", UninstallRel = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Many", DisplayName = "Many",
            Folder = @"C:\Program Files\Many" };
        var manyPlan = AppFootprint.Build(many, manyIdx, Array.Empty<ShortcutInfo>(), manyReg, "C:");
        check(manyPlan.RegistryPatterns.Count(p => p.Contains(@"\CLSID\{") && !p.EndsWith(@"\* [*]")) == FootprintCaps.Classes,
            "com: classes per app are cut at the cap");
        check(manyPlan.Notes.Any(n => n.Contains("cut at " + FootprintCaps.Classes + " of " + (FootprintCaps.Classes + 5))), "com: the cut is reported in the notes");

        var k3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var v3 = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K3(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) k3.Add(string.Join("\\", parts[..i])); }
        void V3(string path, string name, string value) { K3(path); v3[path + "|" + name] = value; }
        V3(Cls + @"\CLSID\" + G1, "", "Heavy");
        V3(Cls + @"\CLSID\" + G1 + @"\InprocServer32", "", @"C:\Program Files\Heavy\h.dll");
        V3(Cls + @"\CLSID\" + G1 + @"\TypeLib", "", T1);
        for (var i = 1; i <= ComRules.MaxInterfacesPerClass + 10; i++)
            V3(Cls + @"\Interface\{60000000-0000-0000-0000-" + i.ToString("D12") + @"}\TypeLib", "", T1);
        var heavyRefs = ComRules.Scan(new AppDiscoveryChecks.Fake(k3, v3), CancellationToken.None);
        check(heavyRefs.Count == 1 && heavyRefs[0].Patterns.Count(p => p.Contains(@"\Interface\{") && !p.EndsWith(@"\* [*]")) == ComRules.MaxInterfacesPerClass,
            "com: interfaces per class are cut at the cap");
    }
}

// The per-app class cap lives in AppFootprint; the checks read it from there.
internal static class FootprintCaps
{
    public static int Classes => AppFootprint.MaxComClassesPerApp;
}
