using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Windows Installer registration (S121, from the EaseUS rule files: Chrome's list carries Installer\UpgradeCodes,
// Installer\UserData\S-1-5-18\Products and \Components, and EaseUS runs the app's own "setup /repair" afterwards).
// A package that only holds an MSI app's Uninstall key, folder and vendor key leaves the app listed in Settings > Apps
// but unknown to Windows Installer, so Repair and Uninstall fail (seen with the Desktop Runtime, C42). Per product this
// carries: Classes\Installer\Products and \Features, UserData\S-1-5-18\Products, the product's own value in every
// UserData\...\Components key and UpgradeCodes key that lists it, and the cached .msi (LocalPackage, normally
// C:\Windows\Installer\<n>.msi). Registry keys are named by the "packed" product code (see MsiCodes.Pack).
// Nothing here has run on Windows; the packing is checked against Chrome's pair in the EaseUS file.

public static class MsiCodes
{
    private static readonly Regex GuidRx = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
    private static readonly Regex PackedRx = new(@"^[0-9A-Fa-f]{32}$");

    public static bool IsGuid(string? s) => s is not null && GuidRx.IsMatch(s.Trim());
    public static bool IsPacked(string? s) => s is not null && PackedRx.IsMatch(s);

    // {09D53CC6-0A7A-3BE2-B558-542159936402} -> 6CC35D90A7A02EB35B85451295394620: the first three groups are reversed
    // character by character, the last two groups have their characters swapped pairwise.
    public static string? Pack(string? guid)
    {
        if (!IsGuid(guid)) return null;
        var h = guid!.Trim().Trim('{', '}').Replace("-", "").ToUpperInvariant();
        static string Rev(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
        static string Swap(string s)
        {
            var sb = new StringBuilder();
            for (var i = 0; i + 1 < s.Length; i += 2) sb.Append(s[i + 1]).Append(s[i]);
            return sb.ToString();
        }
        return Rev(h[..8]) + Rev(h[8..12]) + Rev(h[12..16]) + Swap(h[16..20]) + Swap(h[20..32]);
    }
}

public sealed record MsiProduct(string Packed, string Name, string InstallLocation, string LocalPackage);

// Built once per scan next to SystemRegistryIndex. The product list is cheap; the Components and UpgradeCodes maps
// (thousands of keys on a machine with Office) are only read the first time an app needs them.
public sealed class MsiIndex
{
    internal const string UserData = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18";
    internal const string ClassesInstaller = @"SOFTWARE\Classes\Installer";
    internal static readonly string[] UpgradeRoots =
    {
        @"SOFTWARE\Classes\Installer\UpgradeCodes",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UpgradeCodes",
    };
    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    private readonly IRegistryReader _reg;
    private readonly CancellationToken _ct;
    private Dictionary<string, List<string>>? _components;                         // packed product -> packed components
    private Dictionary<string, List<(string Root, string Upgrade)>>? _upgrades;   // packed product -> upgrade code keys

    public IReadOnlyList<MsiProduct> Products { get; }
    // Uninstall key name of a bundle (ParentKeyName value of its child entries) -> packed codes of the child MSI products
    public IReadOnlyDictionary<string, List<string>> ChildrenOf { get; }

    public static MsiIndex Empty { get; } = new(null!, CancellationToken.None, Array.Empty<MsiProduct>(),
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));

    private MsiIndex(IRegistryReader reg, CancellationToken ct, IReadOnlyList<MsiProduct> products, Dictionary<string, List<string>> children)
    {
        _reg = reg; _ct = ct; Products = products; ChildrenOf = children;
    }

    public static MsiIndex Build(IRegistryReader reg, CancellationToken ct)
    {
        var h = RegHive.Hklm;
        var products = new List<MsiProduct>();
        foreach (var packed in reg.SubKeyNames(h, UserData + @"\Products"))
        {
            ct.ThrowIfCancellationRequested();
            if (!MsiCodes.IsPacked(packed)) continue;
            try
            {
                var ip = UserData + @"\Products\" + packed + @"\InstallProperties";
                var name = AppDiscovery.Str(reg.GetValue(h, ip, "DisplayName")).Trim();
                if (name.Length == 0) name = AppDiscovery.Str(reg.GetValue(h, ClassesInstaller + @"\Products\" + packed, "ProductName")).Trim();
                products.Add(new MsiProduct(packed.ToUpperInvariant(), name,
                    WinPath.Clean(Environment.ExpandEnvironmentVariables(AppDiscovery.Str(reg.GetValue(h, ip, "InstallLocation")))),
                    WinPath.Clean(AppDiscovery.Str(reg.GetValue(h, ip, "LocalPackage")))));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* one unreadable product must not hide the rest */ }
        }

        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in UninstallRoots)
            foreach (var key in reg.SubKeyNames(h, root))
            {
                ct.ThrowIfCancellationRequested();
                var packed = MsiCodes.Pack(key);
                if (packed is null) continue;
                var parent = AppDiscovery.Str(reg.GetValue(h, root + "\\" + key, "ParentKeyName")).Trim();
                if (parent.Length == 0) continue;
                if (!children.TryGetValue(parent, out var l)) children[parent] = l = new List<string>();
                if (!l.Contains(packed)) l.Add(packed);
            }
        return new MsiIndex(reg, ct, products, children);
    }

    public bool ProductExists(string packed) =>
        _reg is not null && (_reg.KeyExists(RegHive.Hklm, UserData + @"\Products\" + packed)
                             || _reg.KeyExists(RegHive.Hklm, ClassesInstaller + @"\Products\" + packed));

    // Packed component codes whose key lists this product as a value name.
    public IReadOnlyList<string> ComponentsFor(string packed)
    {
        if (_reg is null) return Array.Empty<string>();
        if (_components is null)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var comp in _reg.SubKeyNames(RegHive.Hklm, UserData + @"\Components"))
            {
                _ct.ThrowIfCancellationRequested();
                if (!MsiCodes.IsPacked(comp)) continue;
                foreach (var v in _reg.ValueNames(RegHive.Hklm, UserData + @"\Components\" + comp))
                {
                    if (!MsiCodes.IsPacked(v)) continue;
                    if (!map.TryGetValue(v, out var l)) map[v] = l = new List<string>();
                    l.Add(comp);
                }
            }
            _components = map;
        }
        return _components.TryGetValue(packed, out var r) ? r : Array.Empty<string>();
    }

    // Upgrade-code keys (both places Windows keeps them) that list this product as a value name.
    public IReadOnlyList<(string Root, string Upgrade)> UpgradeCodesFor(string packed)
    {
        if (_reg is null) return Array.Empty<(string, string)>();
        if (_upgrades is null)
        {
            var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in UpgradeRoots)
                foreach (var up in _reg.SubKeyNames(RegHive.Hklm, root))
                {
                    _ct.ThrowIfCancellationRequested();
                    if (!MsiCodes.IsPacked(up)) continue;
                    foreach (var v in _reg.ValueNames(RegHive.Hklm, root + "\\" + up))
                    {
                        if (!MsiCodes.IsPacked(v)) continue;
                        if (!map.TryGetValue(v, out var l)) map[v] = l = new List<(string, string)>();
                        l.Add((root, up));
                    }
                }
            _upgrades = map;
        }
        return _upgrades.TryGetValue(packed, out var r) ? r : Array.Empty<(string, string)>();
    }
}

public static class MsiRules
{
    public sealed record Result(IReadOnlyList<string> FilePatterns, IReadOnlyList<string> RegistryPatterns,
        IReadOnlyList<string> Items, IReadOnlyList<string> Notes);

    private const int MaxProducts = 40;
    private const int MaxComponents = 4000;     // per product; a larger set is left out and reported

    // Which Windows Installer products belong to the app: its own Uninstall key when that is a product code; MSI entries
    // that name it as their parent (a bundle's packages); products installed into the app folder or into `extraFolders`
    // (the runtime's dotnet folder); for .NET runtimes also the host/runtime packages of the same version and architecture.
    public static IReadOnlyList<MsiProduct> ProductsFor(InstalledApp app, string folder, IReadOnlyList<string> extraFolders, MsiIndex idx)
    {
        var found = new Dictionary<string, MsiProduct>(StringComparer.OrdinalIgnoreCase);
        MsiProduct Get(string packed) =>
            idx.Products.FirstOrDefault(p => p.Packed.Equals(packed, StringComparison.OrdinalIgnoreCase))
            ?? new MsiProduct(packed.ToUpperInvariant(), "", "", "");
        void Add(string packed) { if (idx.ProductExists(packed) && !found.ContainsKey(packed)) found[packed] = Get(packed); }

        var own = MsiCodes.Pack(app.KeyName);
        if (own is not null) Add(own);
        if (idx.ChildrenOf.TryGetValue(app.KeyName, out var kids)) foreach (var k in kids) Add(k);

        var roots = new List<string> { WinPath.Clean(folder) };
        roots.AddRange(extraFolders.Select(WinPath.Clean));
        foreach (var p in idx.Products)
        {
            if (p.InstallLocation.Length <= 3) continue;
            if (roots.Any(r => r.Length > 3 && WinPath.SameOrUnder(p.InstallLocation, r))) Add(p.Packed);
        }

        if (app.IsDotNetRuntime)
            foreach (var p in idx.Products)
                if (RuntimeRules.MatchesProduct(app, p.Name)) Add(p.Packed);

        return found.Values.Take(MaxProducts).ToList();
    }

    public static Result Derive(InstalledApp app, string folder, IReadOnlyList<string> extraFolders, MsiIndex idx,
        IRegistryReader reg, string systemDrive, Func<string, bool>? dirExists = null)
    {
        var files = new List<string>();
        var regs = new List<string>();
        var items = new List<string>();
        var notes = new List<string>();
        var h = RegHive.Hklm;
        var sd = WinPath.Clean(systemDrive);
        var installerDir = sd + @"\Windows\Installer";

        void Tree(string rel)
        {
            if (!reg.KeyExists(h, rel)) return;
            regs.Add(@"HKLM\" + rel + " [*]");
            if (reg.SubKeyNames(h, rel).Count > 0) regs.Add(@"HKLM\" + rel + @"\* [*]");
        }

        foreach (var p in ProductsFor(app, folder, extraFolders, idx))
        {
            var packed = p.Packed;
            Tree(MsiIndex.ClassesInstaller + @"\Products\" + packed);
            Tree(MsiIndex.ClassesInstaller + @"\Features\" + packed);
            Tree(MsiIndex.UserData + @"\Products\" + packed);

            // Only this product's own value is carried from keys that other products share.
            var comps = idx.ComponentsFor(packed);
            var carried = 0;
            if (comps.Count > MaxComponents)
                notes.Add($"Windows Installer product \"{Label(p)}\" lists {comps.Count} components; they are not captured, so Repair may reinstall more than it needs.");
            else
                foreach (var c in comps)
                {
                    regs.Add(@"HKLM\" + MsiIndex.UserData + @"\Components\" + c + " [" + packed + "]");
                    carried++;
                }
            foreach (var (root, up) in idx.UpgradeCodesFor(packed))
                regs.Add(@"HKLM\" + root + "\\" + up + " [" + packed + "]");

            // The cached package Repair and Uninstall run from
            var pkg = p.LocalPackage;
            if (pkg.Length > 0)
            {
                var dir = WinPath.Parent(pkg);
                var name = pkg[(pkg.LastIndexOf('\\') + 1)..];
                if (dir is not null && dir.Equals(installerDir, StringComparison.OrdinalIgnoreCase)
                    && name.Length > 0 && WinPath.PatternSafe(name) && WinPath.PatternSafe(dir))
                    files.Add(dir + " [" + name + "]");
                else
                    notes.Add($"The cached installer of \"{Label(p)}\" ({pkg}) is outside {installerDir} or has special characters and is not captured.");
            }
            else notes.Add($"No cached installer is recorded for \"{Label(p)}\"; Repair needs its original source.");

            // The install source Windows Installer asks for on a forced reinstall or a Repair (S123, C47)
            foreach (var src in SourceFolders(reg, packed, sd))
            {
                if (!WinPath.PatternSafe(src)) { notes.Add($"The install source of \"{Label(p)}\" ({src}) has special characters and is not captured."); continue; }
                if (dirExists is not null && !dirExists(src)) { notes.Add($"The install source of \"{Label(p)}\" ({src}) does not exist on this machine, so Repair will ask for the installer after a reset."); continue; }
                files.Add(AppFootprint.FolderPattern(src, sd));
                notes.Add($"Windows Installer source carried: {src}.");
            }

            if (reg.SubKeyNames(h, MsiIndex.UserData + @"\Products\" + packed + @"\Patches").Count > 0)
                notes.Add($"\"{Label(p)}\" has Windows Installer patches; the patch files are not captured.");

            items.Add($"Windows Installer: {Label(p)} ({carried} components)");
        }

        return new Result(files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            regs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), items, notes);
    }

    private static readonly Regex SourcePrefixRx = new(@"^[a-z];\d+;(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Install source folders of a product (S123, C47): `SourceList\LastUsedSource` (written \"n;1;<path>\") and the values of
    // `SourceList\Net` (plain paths). Only a folder directly inside <drive>\ProgramData\Package Cache is returned: that is
    // where Burn bundles keep each package (\"{product code}v<version>\"); the Package Cache folder itself, a network
    // share, a CD or any other place is never taken.
    internal static IReadOnlyList<string> SourceFolders(IRegistryReader reg, string packed, string systemDrive)
    {
        var h = RegHive.Hklm;
        var list = MsiIndex.ClassesInstaller + @"\Products\" + packed + @"\SourceList";
        if (!reg.KeyExists(h, list)) return Array.Empty<string>();
        var raw = new List<string>();
        var last = AppDiscovery.Str(reg.GetValue(h, list, "LastUsedSource")).Trim();
        var m = SourcePrefixRx.Match(last);
        raw.Add(m.Success ? m.Groups[1].Value : last);
        foreach (var n in reg.ValueNames(h, list + @"\Net"))
            raw.Add(AppDiscovery.Str(reg.GetValue(h, list + @"\Net", n)));

        var cache = WinPath.Clean(systemDrive) + @"\ProgramData\Package Cache";
        var result = new List<string>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var path = WinPath.Clean(Environment.ExpandEnvironmentVariables(r));
            if (!WinPath.IsRooted(path)) continue;
            var parent = WinPath.Parent(path);
            if (parent is null || !parent.Equals(cache, StringComparison.OrdinalIgnoreCase)) continue;
            if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }
        return result;
    }

    // Top-level folders that are never taken as an app's own folder when read from a component path.
    private static readonly string[] NotAppFolders =
    {
        "Common Files", "WindowsApps", "Windows NT", "Windows", "Windows Defender", "ProgramData", "Users", "Recovery",
        "System Volume Information", "$Recycle.Bin", "PerfLogs", "Program Files", "Program Files (x86)", "WTRP",
    };

    // The install folder of an MSI app whose Uninstall entry names none (S121: PuTTY and Notepad++ MSIs have no InstallLocation
    // or DisplayIcon, and their UninstallString is just "MsiExec.exe /X{code}"). First the product's own InstallLocation, then
    // the key-file paths Windows Installer keeps for each component of the product (UserData\...\Components\<c>, value named
    // by the product): the folder under Program Files (or directly under the drive) holding most of them.
    // AppDiscovery.CheckFolder still judges the result.
    public static string? InstallFolder(IRegistryReader reg, MsiIndex idx, string packed, string systemDrive)
    {
        var sd = WinPath.Clean(systemDrive);
        var p = idx.Products.FirstOrDefault(x => x.Packed.Equals(packed, StringComparison.OrdinalIgnoreCase));
        if (p is not null && p.InstallLocation.Length > 3) return p.InstallLocation;

        var bases = new[] { sd + @"\Program Files", sd + @"\Program Files (x86)" };
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var comp in idx.ComponentsFor(packed))
        {
            var raw = AppDiscovery.Str(reg.GetValue(RegHive.Hklm, MsiIndex.UserData + @"\Components\" + comp, packed)).Trim();
            var path = WinPath.Clean(Environment.ExpandEnvironmentVariables(raw));
            if (!WinPath.IsRooted(path)) continue;            // registry key paths ("01:\Software\...") and empty values

            var under = bases.FirstOrDefault(b => path.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)) ?? sd;
            if (!path.StartsWith(under + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[(under.Length + 1)..];
            var i = rest.IndexOf('\\');
            if (i <= 0) continue;                              // a file directly in the base folder
            var seg = rest[..i];
            if (NotAppFolders.Contains(seg, StringComparer.OrdinalIgnoreCase)) continue;
            var root = under + "\\" + seg;
            counts[root] = counts.GetValueOrDefault(root) + 1;
        }
        return counts.Count == 0 ? null : counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).First().Key;
    }

    private static string Label(MsiProduct p) => p.Name.Length > 0 ? p.Name : p.Packed;
}
