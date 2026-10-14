using WinTester.App.Deployment.RecoveryProvisioning;

// .NET runtime rules: files under <ProgramFiles>\dotnet and the host registration, derived from the host's own registry
// entries. Called from Program.cs.
public static class RuntimeChecks
{
    private const string Setup = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64";

    public static void Run(Action<bool, string> check)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }

        K(Setup + @"\sharedfx\Microsoft.NETCore.App");
        K(Setup + @"\sharedfx\Microsoft.WindowsDesktop.App");
        K(Setup + @"\hostfxr");
        K(Setup + @"\sharedhost");
        vals[Setup + "|InstallLocation"] = @"C:\Program Files\dotnet\";
        vals[Setup + @"\sharedfx\Microsoft.NETCore.App|8.0.31"] = 1;
        vals[Setup + @"\sharedfx\Microsoft.NETCore.App|6.0.36"] = 1;
        vals[Setup + @"\sharedfx\Microsoft.WindowsDesktop.App|8.0.31"] = 1;
        var reg = new AppDiscoveryChecks.Fake(keys, vals);

        var app = new InstalledApp("{guid}", "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{guid}",
            "Microsoft Windows Desktop Runtime - 8.0.31 (x64)", "Microsoft Corporation", "8.0.31.34056",
            @"C:\ProgramData\Package Cache\{guid}", "UninstallString", 0, false, false, true, false, null);

        check(RuntimeRules.IsDotNetRuntime("Microsoft Windows Desktop Runtime - 8.0.31 (x64)"), "runtime: Desktop Runtime name matches");
        check(RuntimeRules.IsDotNetRuntime("Microsoft .NET Runtime - 8.0.31 (x64)"), "runtime: .NET Runtime name matches");
        check(RuntimeRules.IsDotNetRuntime("Microsoft ASP.NET Core 8.0.31 - Shared Framework (x64)"), "runtime: ASP.NET Core shared framework name matches");
        check(!RuntimeRules.IsDotNetRuntime("Microsoft .NET SDK 8.0.400 (x64)"), "runtime: SDK is not a runtime");
        check(!RuntimeRules.IsDotNetRuntime("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810"), "runtime: VC++ redistributable is not matched");
        check(app.DefaultSelected, "runtime: a .NET runtime is ticked by default although Microsoft publishes it");

        var r = RuntimeRules.Derive(app, reg, "C:");
        check(r.FilePatterns.Contains(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.31\* [*]"), "runtime: NETCore.App folder of this version");
        check(r.FilePatterns.Contains(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.31\* [*]"), "runtime: WindowsDesktop.App folder of this version");
        check(!r.FilePatterns.Any(p => p.Contains("6.0.36")), "runtime: another installed version is not taken");
        check(r.FilePatterns.Contains(@"C:\Program Files\dotnet [*]") && r.FilePatterns.Contains(@"C:\Program Files\dotnet\host\* [*]"), "runtime: dotnet.exe and host folder");
        check(r.RegistryPatterns.Contains(@"HKLM\SOFTWARE\dotnet [*]") && r.RegistryPatterns.Contains(@"HKLM\SOFTWARE\dotnet\* [*]"), "runtime: host registration key tree");

        var noReg = RuntimeRules.Derive(app, new AppDiscoveryChecks.Fake(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, object?>()), "C:");
        check(noReg.FilePatterns.Count == 0 && noReg.Notes.Count == 1, "runtime: no host registration gives no rules and one note");

        // The operator's real registration (S119): only sharedhost (Version, Path); frameworks are found on disk
        var k2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var v2 = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in new[] { @"SOFTWARE\dotnet", @"SOFTWARE\dotnet\Setup", @"SOFTWARE\dotnet\Setup\InstalledVersions", Setup, Setup + @"\sharedhost" }) k2.Add(part);
        v2[Setup + @"\sharedhost|Version"] = "8.0.31";
        v2[Setup + @"\sharedhost|Path"] = @"C:\Program Files\dotnet\";
        var disk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.31",
            @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.31",
            @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.36",
            @"C:\Program Files\dotnet\host",
        };
        var d = RuntimeRules.Derive(app, new AppDiscoveryChecks.Fake(k2, v2), "C:", disk.Contains);
        check(d.FilePatterns.Contains(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.31\* [*]")
              && d.FilePatterns.Contains(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.31\* [*]"), "runtime: frameworks found on disk without sharedfx registry entries");
        check(!d.FilePatterns.Any(p => p.Contains("AspNetCore") || p.Contains("6.0.36")), "runtime: absent framework and other version are not written");
        check(d.FilePatterns.Contains(@"C:\Program Files\dotnet\host\* [*]") && d.RegistryPatterns.Contains(@"HKLM\SOFTWARE\dotnet [*]"), "runtime: host folder and registration with sharedhost only");

        var x86 = app with { DisplayName = "Microsoft Windows Desktop Runtime - 8.0.31 (x86)" };
        check(RuntimeRules.Arch(x86.DisplayName) == "x86" && RuntimeRules.Arch(app.DisplayName) == "x64", "runtime: architecture from the name");
    }
}
