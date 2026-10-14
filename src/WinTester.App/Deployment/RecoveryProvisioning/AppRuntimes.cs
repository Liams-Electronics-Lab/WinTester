using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// What a .NET runtime needs on top of its Uninstall entry: the runtime installs into <ProgramFiles>\dotnet, not into the
// folder the Uninstall entry points at (the bundle's Package Cache folder), so the generic rules alone leave the app listed
// as installed with no runtime files behind it.
public static class RuntimeRules
{
    private static readonly Regex NameMatch = new(
        @"(Windows Desktop Runtime|\.NET Desktop Runtime|ASP\.NET Core.*(Runtime|Shared Framework)|\.NET (Core )?Runtime)",
        RegexOptions.IgnoreCase);
    private static readonly Regex Excluded = new(@"\b(SDK|Targeting Pack|AppHost Pack|Templates|Visual C\+\+)\b", RegexOptions.IgnoreCase);
    private static readonly Regex VersionIn = new(@"\d+\.\d+\.\d+");

    public static bool IsDotNetRuntime(string displayName) =>
        NameMatch.IsMatch(displayName) && !Excluded.IsMatch(displayName);

    // Architecture key used by the host registration: x64, x86 or arm64. Read from the display name, x64 when absent.
    internal static string Arch(string displayName) =>
        displayName.Contains("(x86)", StringComparison.OrdinalIgnoreCase) ? "x86"
        : displayName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";

    // Runtime version (8.0.31) from the display name, else from DisplayVersion (8.0.31.34056 is cut to three parts).
    internal static string? VersionOf(InstalledApp app)
    {
        var m = VersionIn.Match(app.DisplayName);
        if (!m.Success) m = VersionIn.Match(app.Version ?? "");
        return m.Success ? m.Value : null;
    }

    public sealed record Result(IReadOnlyList<string> FilePatterns, IReadOnlyList<string> RegistryPatterns,
        IReadOnlyList<string> Items, IReadOnlyList<string> Notes)
    {
        public string? Root { get; init; }     // the dotnet folder the framework folders sit in, for the Windows Installer match
    }

    private static readonly Regex ProductMatch = new(
        @"(\.NET (Core )?(Host|Runtime)|Host FX Resolver|Windows Desktop Runtime|ASP\.NET Core)", RegexOptions.IgnoreCase);

    // A Windows Installer package of the same runtime release (host, host resolver, runtime, desktop runtime): same version
    // and architecture as the bundle's entry. Names like these are what the runtime bundles install; not checked on a real machine.
    public static bool MatchesProduct(InstalledApp app, string productName)
    {
        if (string.IsNullOrWhiteSpace(productName) || !ProductMatch.IsMatch(productName) || Excluded.IsMatch(productName)) return false;
        var v = VersionOf(app);
        return v is not null && productName.Contains(v, StringComparison.Ordinal)
               && Arch(productName) == Arch(app.DisplayName);
    }

    private static readonly string[] KnownFrameworks =
        { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App" };

    // The host finds a runtime by its files (<root>\shared\<framework>\<version>), so the folders are found on disk; the
    // registration under HKLM\SOFTWARE\dotnet\Setup\InstalledVersions only supplies the root and extra framework names.
    // On the operator's machine that key held just sharedhost (Version, Path), with no sharedfx or hostfxr entries (S119),
    // so nothing here may depend on those.
    public static Result Derive(InstalledApp app, IRegistryReader reg, string systemDrive, Func<string, bool>? dirExists = null)
    {
        var files = new List<string>();
        var regs = new List<string>();
        var items = new List<string>();
        var notes = new List<string>();
        var sd = WinPath.Clean(systemDrive);
        var exists = dirExists ?? (_ => false);

        var version = VersionOf(app);
        if (version is null)
        {
            notes.Add("The runtime version could not be read from the entry, so its Program Files\\dotnet files are not captured.");
            return new Result(files, regs, items, notes);
        }

        var arch = Arch(app.DisplayName);
        var wow = arch == "x86";
        var swRoot = wow ? @"SOFTWARE\WOW6432Node\dotnet" : @"SOFTWARE\dotnet";
        var setup = swRoot + @"\Setup\InstalledVersions\" + arch;

        // dotnet root: InstallLocation, else the sharedhost Path, else the default under Program Files
        var root = AppDiscovery.Str(reg.GetValue(RegHive.Hklm, setup, "InstallLocation")).Trim();
        if (root.Length == 0) root = AppDiscovery.Str(reg.GetValue(RegHive.Hklm, setup + @"\sharedhost", "Path")).Trim();
        root = root.TrimEnd('\\');
        if (root.Length == 0)
            root = sd + (wow ? @"\Program Files (x86)\dotnet" : @"\Program Files\dotnet");
        root = WinPath.Clean(root);

        if (!root.StartsWith(sd + "\\", StringComparison.OrdinalIgnoreCase) || !WinPath.PatternSafe(root))
        {
            notes.Add("The registered .NET folder (" + root + ") cannot be captured by a rule.");
            return new Result(files, regs, items, notes);
        }

        var fxRoot = setup + @"\sharedfx";
        var frameworks = KnownFrameworks.Concat(reg.SubKeyNames(RegHive.Hklm, fxRoot)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var fx in frameworks)
        {
            if (!WinPath.PatternSafe(fx)) continue;
            var dir = root + @"\shared\" + fx + "\\" + version;
            var registered = reg.ValueNames(RegHive.Hklm, fxRoot + "\\" + fx).Any(v => v.Equals(version, StringComparison.OrdinalIgnoreCase));
            if (!exists(dir) && !registered) continue;
            files.Add(AppFootprint.FolderPattern(dir, sd));
            items.Add(fx + " " + version);
        }
        if (files.Count == 0)
        {
            notes.Add("No shared framework folder of version " + version + " was found under " + root + ", so no runtime files are captured.");
            return new Result(files, regs, items, notes);
        }

        // host: dotnet.exe and the other top-level files, and the hostfxr resolver folder
        files.Add(root + @" [*]");
        if (exists(root + @"\host")) files.Add(AppFootprint.FolderPattern(root + @"\host", sd));

        if (reg.KeyExists(RegHive.Hklm, swRoot))
        {
            regs.Add(@"HKLM\" + swRoot + " [*]");
            regs.Add(@"HKLM\" + swRoot + @"\* [*]");
        }

        notes.Add("The system PATH entry for dotnet.exe is not carried; applications find the runtime through the registered install folder.");
        return new Result(files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            regs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), items, notes) { Root = root };
    }
}
