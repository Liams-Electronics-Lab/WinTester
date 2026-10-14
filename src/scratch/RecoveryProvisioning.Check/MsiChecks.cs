using WinTester.App.Deployment.RecoveryProvisioning;

// Windows Installer registration rules (S121). Called from Program.cs.
public static class MsiChecks
{
    private const string Unin = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UD = MsiIndex_UserData;
    private const string MsiIndex_UserData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18";
    private const string CI = @"SOFTWARE\Classes\Installer";

    // Chrome's pair from the EaseUS rule file: Uninstall key {09D53CC6-...} <-> Installer key 6CC35D90...
    private const string GChrome = "{09D53CC6-0A7A-3BE2-B558-542159936402}";
    private const string PChrome = "6CC35D90A7A02EB35B85451295394620";

    public static void Run(Action<bool, string> check)
    {
        check(MsiCodes.Pack(GChrome) == PChrome, "msi: packed code of Chrome's product matches the EaseUS key");
        check(MsiCodes.Pack("{00000000-0000-0000-0000-000000000000}") == new string('0', 32), "msi: all-zero code packs to zeros");
        check(MsiCodes.Pack("Google Chrome") is null && MsiCodes.Pack("") is null && MsiCodes.Pack(null) is null, "msi: non-GUID names do not pack");
        check(MsiCodes.IsPacked(PChrome) && !MsiCodes.IsPacked("xyz"), "msi: packed-code shape");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        void V(string p, string n, object v) { K(p); vals[p + "|" + n] = v; }

        // An MSI app: Uninstall key is the product code, product + features + 3 components + 2 upgrade-code keys + cached msi
        var app = Unin + "\\" + GChrome;
        V(app, "DisplayName", "Chrome MSI App"); V(app, "WindowsInstaller", 1); V(app, "InstallLocation", @"C:\Program Files\Chrome MSI");
        K(CI + @"\Products\" + PChrome + @"\SourceList");
        V(CI + @"\Products\" + PChrome, "ProductName", "Chrome MSI App");
        K(CI + @"\Features\" + PChrome);
        V(UD + @"\Products\" + PChrome + @"\InstallProperties", "DisplayName", "Chrome MSI App");
        V(UD + @"\Products\" + PChrome + @"\InstallProperties", "InstallLocation", @"C:\Program Files\Chrome MSI\");
        V(UD + @"\Products\" + PChrome + @"\InstallProperties", "LocalPackage", @"C:\Windows\Installer\1a2b3c.msi");
        var comps = new[] { "AAAA0000AAAA0000AAAA0000AAAA0000", "BBBB0000BBBB0000BBBB0000BBBB0000", "CCCC0000CCCC0000CCCC0000CCCC0000" };
        foreach (var c in comps) V(UD + @"\Components\" + c, PChrome, @"C:\Program Files\Chrome MSI\x.dll");
        V(UD + @"\Components\" + comps[0], "FFFF0000FFFF0000FFFF0000FFFF0000", "shared with another product");
        V(CI + @"\UpgradeCodes\96FDFD1C54952F233AE5EE499CC9C74F", PChrome, "");
        V(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes\96FDFD1C54952F233AE5EE499CC9C74F", PChrome, "");
        V(UD + @"\Components\" + comps[1], "EEEE0000EEEE0000EEEE0000EEEE0000", "another product only");

        // An unrelated product that must not be picked up
        const string POther = "11112222333344445555666677778888";
        V(UD + @"\Products\" + POther + @"\InstallProperties", "DisplayName", "Other");
        V(UD + @"\Products\" + POther + @"\InstallProperties", "InstallLocation", @"C:\Program Files\Other");
        K(CI + @"\Products\" + POther);

        var reg = new AppDiscoveryChecks.Fake(keys, vals);
        var idx = SystemRegistryIndex.Build(reg, CancellationToken.None);
        var inst = new InstalledApp(GChrome, "HKLM", app, "Chrome MSI App", "Vendor", "1.0", @"C:\Program Files\Chrome MSI",
            "InstallLocation", 0, false, true, false, false, null);
        var plan = AppFootprint.Build(inst, idx, Array.Empty<ShortcutInfo>(), reg, "C:");

        bool Reg(string s) => plan.RegistryPatterns.Contains(s);
        check(Reg(@"HKLM\" + CI + @"\Products\" + PChrome + " [*]") && Reg(@"HKLM\" + CI + @"\Products\" + PChrome + @"\* [*]"), "msi: Classes\\Installer\\Products key and sub-keys");
        check(Reg(@"HKLM\" + CI + @"\Features\" + PChrome + " [*]") && !Reg(@"HKLM\" + CI + @"\Features\" + PChrome + @"\* [*]"), "msi: Features key, no sub-key rule when it has none");
        check(Reg(@"HKLM\" + UD + @"\Products\" + PChrome + " [*]") && Reg(@"HKLM\" + UD + @"\Products\" + PChrome + @"\* [*]"), "msi: UserData product key and sub-keys");
        foreach (var c in comps)
            check(Reg(@"HKLM\" + UD + @"\Components\" + c + " [" + PChrome + "]"), "msi: component " + c[..4] + " carries only this product's value");
        check(!plan.RegistryPatterns.Any(p => p.Contains("EEEE0000") || p.Contains("FFFF0000")), "msi: other products' values are not carried");
        check(!plan.RegistryPatterns.Any(p => p.Contains(@"Components\" + comps[0] + " [*]")), "msi: shared component key is not taken whole");
        check(Reg(@"HKLM\" + CI + @"\UpgradeCodes\96FDFD1C54952F233AE5EE499CC9C74F [" + PChrome + "]")
              && Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes\96FDFD1C54952F233AE5EE499CC9C74F [" + PChrome + "]"), "msi: upgrade-code value in both places");
        check(plan.FilePatterns.Contains(@"C:\Windows\Installer [1a2b3c.msi]"), "msi: cached .msi carried");
        check(!plan.RegistryPatterns.Any(p => p.Contains(POther)), "msi: unrelated product not carried");
        check(plan.MsiItems.Count == 1 && plan.MsiItems[0].Contains("3 components"), "msi: one item with the component count");
        check(!plan.Notes.Any(n => n.Contains("no product registration")), "msi: no 'not found' note when the product was found");
        check(plan.RegistryPatterns.Count == plan.RegistryPatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "msi: no duplicate patterns");

        // A product found only by its install folder (the app's own Uninstall key is not a GUID)
        var inst2 = inst with { KeyName = "Chrome MSI App", UninstallRel = Unin + @"\Chrome MSI App" };
        var plan2 = AppFootprint.Build(inst2, idx, Array.Empty<ShortcutInfo>(), reg, "C:");
        check(plan2.MsiItems.Count == 1, "msi: product matched by InstallLocation inside the app folder");

        // A cached package outside Windows\Installer is reported, not captured
        var keys3 = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var vals3 = new Dictionary<string, object?>(vals, StringComparer.OrdinalIgnoreCase);
        vals3[UD + @"\Products\" + PChrome + @"\InstallProperties|LocalPackage"] = @"D:\cache\x.msi";
        var reg3 = new AppDiscoveryChecks.Fake(keys3, vals3);
        var plan3 = AppFootprint.Build(inst, SystemRegistryIndex.Build(reg3, CancellationToken.None), Array.Empty<ShortcutInfo>(), reg3, "C:");
        check(!plan3.FilePatterns.Any(p => p.Contains(".msi")) && plan3.Notes.Any(n => n.Contains("cached installer")), "msi: cached package outside Windows\\Installer is reported");

        // A Burn bundle (the .NET Desktop Runtime): the bundle's own key is not an MSI; its packages are found through
        // ParentKeyName and through the same-version product names
        const string GBundle = "{b5d2e9a1-5c44-4d0e-9d56-1f3a8a7c1111}";
        const string GKid = "{AABBCCDD-1122-3344-5566-778899AABBCC}";
        const string GKid2 = "{DDCCBBAA-1122-3344-5566-778899AABBCC}";
        var kid = MsiCodes.Pack(GKid)!; var kid2 = MsiCodes.Pack(GKid2)!;
        var k2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var v2 = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K2(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) k2.Add(string.Join("\\", parts[..i])); }
        void V2(string p, string n, object v) { K2(p); v2[p + "|" + n] = v; }
        V2(Unin + "\\" + GBundle, "DisplayName", "Windows Desktop Runtime - 8.0.31 (x64)");
        V2(Unin + "\\" + GBundle, "UninstallString", @"""C:\ProgramData\Package Cache\{b5d2e9a1}\windowsdesktop-runtime-8.0.31-win-x64.exe"" /uninstall");
        V2(Unin + "\\" + GKid, "ParentKeyName", GBundle);
        V2(UD + @"\Products\" + kid + @"\InstallProperties", "DisplayName", "Microsoft .NET Host - 8.0.31 (x64)");
        K2(CI + @"\Products\" + kid);
        V2(UD + @"\Products\" + kid2 + @"\InstallProperties", "DisplayName", "Microsoft Windows Desktop Runtime - 8.0.31 (x64)");
        K2(CI + @"\Products\" + kid2);
        // same family, other version: must not match
        const string kid3 = "99998888777766665555444433332222";
        V2(UD + @"\Products\" + kid3 + @"\InstallProperties", "DisplayName", "Microsoft Windows Desktop Runtime - 6.0.5 (x64)");
        K2(CI + @"\Products\" + kid3);
        K2(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost");
        v2[@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost|Path"] = @"C:\Program Files\dotnet\";
        var regB = new AppDiscoveryChecks.Fake(k2, v2);
        var idxB = SystemRegistryIndex.Build(regB, CancellationToken.None);
        var bundle = new InstalledApp(GBundle, "HKLM", Unin + "\\" + GBundle, "Windows Desktop Runtime - 8.0.31 (x64)", "Microsoft Corporation", "8.0.31.34056",
            @"C:\ProgramData\Package Cache\{b5d2e9a1}", "UninstallString", 0, false, false, true, false, null);
        var planB = AppFootprint.Build(bundle, idxB, Array.Empty<ShortcutInfo>(), regB, "C:", null, null, _ => true);
        check(planB.MsiItems.Count == 2, "msi: bundle packages found by ParentKeyName and by runtime product name (" + planB.MsiItems.Count + ")");
        check(planB.RegistryPatterns.Any(p => p.Contains(@"UserData\S-1-5-18\Products\" + kid + " [*]")), "msi: bundle child (ParentKeyName) carried");
        check(planB.RegistryPatterns.Any(p => p.Contains(@"UserData\S-1-5-18\Products\" + kid2 + " [*]")), "msi: same-version runtime package carried");
        check(!planB.RegistryPatterns.Any(p => p.Contains(kid3)), "msi: other runtime version not carried");


        // Real data from the operator's VM after a Repair of the Desktop Runtime (S121, msi1/msi2): four products named like
        // this, no ParentKeyName on any Uninstall entry (msi3/msi4), bundle {208fff39-...} in Package Cache.
        {
            var kR = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vR = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            void VR(string p, string n, object v) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) kR.Add(string.Join("\\", parts[..i])); vR[p + "|" + n] = v; }
            var real = new (string Packed, string Name)[]
            {
                ("34D39D2FC6A8C294E87370D04B990C47", "Microsoft Windows Desktop Runtime - 8.0.31 (x64)"),
                ("615DFE5D517EF654D9F3C46A56C353E3", "Microsoft .NET Runtime - 8.0.31 (x64)"),
                ("AA248A653B5BB9148AE06326D3211F86", "Microsoft .NET Host - 8.0.31 (x64)"),
                ("B5E0B741619D6F446AB661C30F7F329E", "Microsoft .NET Host FX Resolver - 8.0.31 (x64)"),
            };
            foreach (var (pk, nm) in real) VR(UD + @"\Products\" + pk + @"\InstallProperties", "DisplayName", nm);
            const string GB = "{208fff39-3440-44c6-b473-38136ba594bf}";
            VR(Unin + "\\" + GB, "DisplayName", "Windows Desktop Runtime - 8.0.31 (x64)");
            VR(Unin + "\\" + GB, "UninstallString", @"""C:\ProgramData\Package Cache\" + GB + @"\windowsdesktop-runtime-8.0.31-win-x64.exe"" /uninstall");
            // an unrelated product of another version and a 32-bit one must stay out
            VR(UD + @"\Products\11112222333344445555666677778888\InstallProperties", "DisplayName", "Microsoft .NET Runtime - 6.0.5 (x64)");
            VR(UD + @"\Products\88887777666655554444333322221111\InstallProperties", "DisplayName", "Microsoft .NET Runtime - 8.0.31 (x86)");
            var regR = new AppDiscoveryChecks.Fake(kR, vR);
            var idxR = SystemRegistryIndex.Build(regR, CancellationToken.None);
            var appR = new InstalledApp(GB, "HKLM", Unin + "\\" + GB, "Windows Desktop Runtime - 8.0.31 (x64)", "Microsoft Corporation", "8.0.31.36421",
                @"C:\ProgramData\Package Cache\" + GB, "UninstallString", 0, false, false, true, false, null);
            var planR = AppFootprint.Build(appR, idxR, Array.Empty<ShortcutInfo>(), regR, "C:", null, null, _ => true);
            check(planR.MsiItems.Count == 4, "msi: the four real runtime package names all match (" + planR.MsiItems.Count + ")");
            foreach (var (pk, _) in real)
                check(planR.RegistryPatterns.Any(p => p.EndsWith(@"UserData\S-1-5-18\Products\" + pk + " [*]")), "msi: real package carried " + pk[..6]);
            check(!planR.RegistryPatterns.Any(p => p.Contains("11112222") || p.Contains("88887777")), "msi: other version and the x86 package are not carried");
        }

        // MSI apps whose Uninstall entry names no folder (S121: PuTTY and Notepad++ MSIs). The folder comes from the product's components.
        {
            var kM = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vM = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            void KM(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) kM.Add(string.Join("\\", parts[..i])); }
            void VM(string p, string n, object v) { KM(p); vM[p + "|" + n] = v; }
            const string GPutty = "{11AA22BB-33CC-44DD-55EE-66FF77889900}";
            const string GNpp = "{99887766-5544-3322-1100-FFEEDDCCBBAA}";
            var pPutty = MsiCodes.Pack(GPutty)!; var pNpp = MsiCodes.Pack(GNpp)!;
            foreach (var (g, name) in new[] { (GPutty, "PuTTY release 0.83 (64-bit)"), (GNpp, "Notepad++ (64-bit x64)") })
            {
                VM(Unin + "\\" + g, "DisplayName", name);
                VM(Unin + "\\" + g, "WindowsInstaller", 1);
                VM(Unin + "\\" + g, "UninstallString", "MsiExec.exe /X" + g);
                var pk = MsiCodes.Pack(g)!;
                KM(CI + @"\Products\" + pk);
                VM(UD + @"\Products\" + pk + @"\InstallProperties", "DisplayName", name);   // no InstallLocation, no LocalPackage
            }
            VM(UD + @"\Components\C0000000000000000000000000000001", pPutty, @"C:\Program Files\PuTTY\putty.exe");
            VM(UD + @"\Components\C0000000000000000000000000000002", pPutty, @"C:\Program Files\PuTTY\pscp.exe");
            VM(UD + @"\Components\C0000000000000000000000000000003", pPutty, @"C:\Program Files\PuTTY\doc\putty.chm");
            VM(UD + @"\Components\C0000000000000000000000000000004", pPutty, @"01:\Software\SimonTatham\PuTTY\Installed");
            VM(UD + @"\Components\C0000000000000000000000000000005", pPutty, @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PuTTY\PuTTY.lnk");
            VM(UD + @"\Components\D0000000000000000000000000000001", pNpp, @"C:\Program Files\Notepad++\notepad++.exe");
            VM(UD + @"\Components\D0000000000000000000000000000002", pNpp, @"C:\Program Files\Notepad++\plugins\x.dll");
            VM(UD + @"\Components\D0000000000000000000000000000003", pNpp, @"C:\Program Files\Common Files\Notepad++\shared.dll");
            VM(UD + @"\Components\D0000000000000000000000000000004", pNpp, @"C:\Program Files\Common Files\Other\a.dll");
            VM(UD + @"\Components\D0000000000000000000000000000005", pNpp, @"C:\Program Files\Common Files\Other\b.dll");
            var regM = new AppDiscoveryChecks.Fake(kM, vM);
            var apps = AppDiscovery.Discover(regM, "C:", d => d is @"C:\Program Files\PuTTY" or @"C:\Program Files\Notepad++", null, CancellationToken.None);
            var putty = apps.First(a => a.DisplayName.StartsWith("PuTTY"));
            var npp = apps.First(a => a.DisplayName.StartsWith("Notepad++"));
            check(putty.Capturable && putty.Folder == @"C:\Program Files\PuTTY", "msi folder: PuTTY found from its components (" + putty.Folder + " / " + putty.BlockReason + ")");
            check(npp.Capturable && npp.Folder == @"C:\Program Files\Notepad++", "msi folder: Notepad++ found, Common Files ignored (" + npp.Folder + " / " + npp.BlockReason + ")");
            check(putty.FolderSource == "Windows Installer" && putty.IsMsi, "msi folder: source is named");
            var idxM = SystemRegistryIndex.Build(regM, CancellationToken.None);
            var planPutty = AppFootprint.Build(putty, idxM, Array.Empty<ShortcutInfo>(), regM, "C:");
            check(planPutty.FilePatterns.Contains(@"C:\Program Files\PuTTY\* [*]"), "msi folder: PuTTY plan carries its folder");
            check(planPutty.MsiItems.Count == 1 && planPutty.MsiItems[0].Contains("5 components"), "msi folder: PuTTY plan carries its Installer registration (" + string.Join("|", planPutty.MsiItems) + ")");

            // The product's own InstallLocation wins over the component paths
            vM[UD + @"\Products\" + pPutty + @"\InstallProperties|InstallLocation"] = @"C:\Program Files\PuTTY\";
            var apps2 = AppDiscovery.Discover(new AppDiscoveryChecks.Fake(kM, vM), "C:", d => true, null, CancellationToken.None);
            check(apps2.First(a => a.DisplayName.StartsWith("PuTTY")).Folder == @"C:\Program Files\PuTTY", "msi folder: InstallLocation of the product is used when present");

            // Only registry or system-folder components: no folder, same message as before
            const string GBad = "{AAAA1111-BBBB-2222-CCCC-3333DDDD4444}";
            var pBad = MsiCodes.Pack(GBad)!;
            VM(Unin + "\\" + GBad, "DisplayName", "Registry Only App"); VM(Unin + "\\" + GBad, "UninstallString", "MsiExec.exe /X" + GBad);
            VM(UD + @"\Components\E0000000000000000000000000000001", pBad, @"01:\Software\X");
            VM(UD + @"\Components\E0000000000000000000000000000002", pBad, @"C:\Windows\System32\x.dll");
            VM(UD + @"\Products\" + pBad + @"\InstallProperties", "DisplayName", "Registry Only App");
            KM(CI + @"\Products\" + pBad);
            var apps3 = AppDiscovery.Discover(new AppDiscoveryChecks.Fake(kM, vM), "C:", d => true, null, CancellationToken.None);
            var bad = apps3.First(a => a.DisplayName == "Registry Only App");
            check(!bad.Capturable && bad.BlockReason!.Contains("no install folder"), "msi folder: an MSI app with no usable component path stays blocked with the old reason");
        }

        // No Windows Installer data at all: an MSI-flagged app gets a note, other apps nothing
        var empty = new AppDiscoveryChecks.Fake(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, object?>());
        var idxE = SystemRegistryIndex.Build(empty, CancellationToken.None);
        var planE = AppFootprint.Build(inst2, idxE, Array.Empty<ShortcutInfo>(), empty, "C:");
        check(planE.MsiItems.Count == 0 && planE.Notes.Any(n => n.Contains("no product registration")), "msi: MSI app with no registration gets a note");
        var non = inst2 with { IsMsi = false };
        check(!AppFootprint.Build(non, idxE, Array.Empty<ShortcutInfo>(), empty, "C:").Notes.Any(n => n.Contains("Windows Installer")), "msi: non-MSI app gets no Windows Installer note");

        SourceChecks(check);
    }

    // 3.16 (S123, C47): the install source folder named by SourceList, from the Desktop Runtime values seen on the VM.
    private static void SourceChecks(Action<bool, string> check)
    {
        const string GRt = "{F2D93D43-8A6C-492C-8E37-070DB499C074}";
        const string PRt = "34D39D2FC6A8C294E87370D04B990C47";
        const string Src = @"C:\ProgramData\Package Cache\{F2D93D43-8A6C-492C-8E37-070DB499C074}v64.124.58455";
        check(MsiCodes.Pack(GRt) == PRt, "msi source: the VM's Desktop Runtime code packs to the key seen on the VM");

        AppCapturePlan Plan(Action<Action<string>, Action<string, string, object>> setup, Func<string, bool>? exists = null)
        {
            var k = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var v = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) k.Add(string.Join("\\", parts[..i])); }
            void V(string p, string n, object val) { K(p); v[p + "|" + n] = val; }
            var app = Unin + "\\" + GRt;
            V(app, "DisplayName", "Runtime MSI"); V(app, "WindowsInstaller", 1); V(app, "InstallLocation", @"C:\Program Files\dotnet");
            V(UD + @"\Products\" + PRt + @"\InstallProperties", "DisplayName", "Runtime MSI");
            V(UD + @"\Products\" + PRt + @"\InstallProperties", "LocalPackage", @"C:\Windows\Installer\10881d.msi");
            K(CI + @"\Products\" + PRt);
            setup(K, (p, n, val) => V(p, n, val));
            var reg = new AppDiscoveryChecks.Fake(k, v);
            var idx = SystemRegistryIndex.Build(reg, CancellationToken.None);
            var inst = new InstalledApp(GRt, "HKLM", app, "Runtime MSI", "Vendor", "8.0.31", @"C:\Program Files\dotnet",
                "InstallLocation", 0, false, true, false, false, null);
            return AppFootprint.Build(inst, idx, Array.Empty<ShortcutInfo>(), reg, "C:", dirExists: exists);
        }
        string SL(string tail = "") => CI + @"\Products\" + PRt + @"\SourceList" + tail;
        bool HasSrc(AppCapturePlan p, string folder) => p.FilePatterns.Contains(folder + @"\* [*]");

        // The values as seen on the VM: LastUsedSource "n;1;<path>\", Net\1 the same path, Media\1 ";"
        var plan = Plan((K, V) =>
        {
            V(SL(), "LastUsedSource", "n;1;" + Src + "\\"); V(SL(), "PackageName", "windowsdesktop-runtime-8.0.31-win-x64.msi");
            V(SL(@"\Net"), "1", Src + "\\"); V(SL(@"\Media"), "1", ";");
        });
        check(HasSrc(plan, Src), "msi source: the Package Cache folder of the product is carried as a folder rule");
        check(plan.FilePatterns.Count(p => p.Contains("Package Cache")) == 1, "msi source: LastUsedSource and Net\\1 naming one folder give one rule");
        check(plan.Notes.Any(n => n.Contains("Windows Installer source carried") && n.Contains("v64.124.58455")), "msi source: a note names the carried folder");
        check(plan.MsiItems.Count == 1, "msi source: the source folder does not add a product item");

        // Not taken: the Package Cache folder itself, other places, a share, a CD, a relative path, nothing at all
        var bad = Plan((K, V) =>
        {
            V(SL(), "LastUsedSource", @"n;1;C:\ProgramData\Package Cache\");
            V(SL(@"\Net"), "1", @"D:\Share\Setup\"); V(SL(@"\Net"), "2", @"\\server\share\app\"); V(SL(@"\Net"), "3", @"C:\Temp\App");
            V(SL(@"\Net"), "4", @"C:\ProgramData\Package Cache\{AAAA}\deeper"); V(SL(@"\Net"), "5", @"Package Cache\{AAAA}v1"); V(SL(@"\Net"), "6", "");
            V(SL(@"\Media"), "1", ";");
        });
        check(!bad.FilePatterns.Any(p => p.Contains("Package Cache") || p.Contains("Share") || p.Contains(@"C:\Temp")), "msi source: the cache root, other folders, shares and relative paths are not captured");
        check(!Plan((K, V) => K(SL())).FilePatterns.Any(p => p.Contains("Package Cache")), "msi source: a SourceList with no values adds nothing");
        check(!Plan((K, V) => { }).FilePatterns.Any(p => p.Contains("Package Cache")), "msi source: a product with no SourceList adds nothing");

        // Environment text and a second Net entry
        var two = Plan((K, V) =>
        {
            V(SL(@"\Net"), "1", Src + "\\"); V(SL(@"\Net"), "2", @"C:\ProgramData\Package Cache\{BBBB0000-0000-0000-0000-000000000000}v1.0");
        });
        check(HasSrc(two, Src) && HasSrc(two, @"C:\ProgramData\Package Cache\{BBBB0000-0000-0000-0000-000000000000}v1.0"), "msi source: two Package Cache entries both carried");

        // Existence and special characters
        var gone = Plan((K, V) => V(SL(@"\Net"), "1", Src + "\\"), d => !d.Contains("Package Cache"));
        check(!HasSrc(gone, Src) && gone.Notes.Any(n => n.Contains("does not exist on this machine")), "msi source: a missing source folder is not a rule and is reported");
        var here = Plan((K, V) => V(SL(@"\Net"), "1", Src + "\\"), d => true);
        check(HasSrc(here, Src), "msi source: an existing source folder is carried when existence is checked");
        var odd = Plan((K, V) => V(SL(@"\Net"), "1", @"C:\ProgramData\Package Cache\{AAAA}v1^2"));
        check(!odd.FilePatterns.Any(p => p.Contains("Package Cache")) && odd.Notes.Any(n => n.Contains("special characters")), "msi source: a folder name with a pattern character is not a rule and is reported");
        check(plan.RegistryPatterns.Count == plan.RegistryPatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count()
              && plan.FilePatterns.Count == plan.FilePatterns.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "msi source: no duplicate patterns");
    }
}
