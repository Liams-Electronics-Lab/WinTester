using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

public enum SuggestionKind { Folder, RegistryKey, FirewallRules }

// One optional extra for a ticked app (3.19). Name matches are guesses, so a suggestion is never applied on its own: the operator
// ticks it on the Configure step and AppSuggestions.Apply adds its patterns to the app's plan.
// SizeBytes: -1 = folder not measured yet, 0 = nothing to measure (registry and firewall entries).
public sealed record AppSuggestion(
    SuggestionKind Kind,
    string Target,                              // folder path, "HKLM\..." key, or "Firewall rules"
    string Reason,
    long SizeBytes,
    string? Warning,                            // licence or machine-identity look-alikes, shown next to the entry
    IReadOnlyList<string> FilePatterns,
    IReadOnlyList<string> RegistryPatterns)
{
    // Rule names behind a firewall suggestion, for the tooltip.
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public string Key => Kind + "|" + Target;

    public string Describe() => Kind switch
    {
        SuggestionKind.Folder => "Folder " + Target,
        SuggestionKind.RegistryKey => "Registry key " + Target,
        _ => "Firewall rules (" + RegistryPatterns.Count + ")"
    };
}

public sealed record FsEntry(string Name, bool IsDir);

public sealed record FirewallRuleInfo(string ValueName, string DisplayName, string ProgramPath);

public static class AppSuggestions
{
    internal const string FirewallKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    private const int MaxFolders = 8;
    private const int MaxKeys = 6;
    private const int MaxFirewallRules = 200;
    private const int MaxFirewallValues = 20000;
    private const int MaxLicenceScan = 1500;

    // Suggestions for one app that has a plan. Folder sizes are left unmeasured (-1) so the list can be built for every app in one
    // quick pass; Measure fills them in when the operator opens the list. A candidate that fails the same guards a profile folder
    // goes through (CheckFolder, game-library check, pattern-safe names) is not offered. Anything the plan already carries, and any
    // folder that contains or lies inside a captured folder, is left out.
    public static IReadOnlyList<AppSuggestion> Find(ScanContext c, InstalledApp app, AppCapturePlan plan, CancellationToken ct,
        Func<string, IReadOnlyList<FsEntry>>? list = null)
    {
        list ??= RealList;
        var result = new List<AppSuggestion>();
        var folder = app.Folder;
        if (folder is null) return result;

        var sd = WinPath.Clean(c.SystemDrive);
        var covered = CoveredDirs(plan, folder, sd);
        var names = CandidateNames(app, folder);

        // Folders with the same name as the publisher, the app or the install folder, in the usual shared places.
        var places = new (string Root, string Place)[]
        {
            (sd + @"\ProgramData", "ProgramData"),
            (sd + @"\Program Files\Common Files", "Common Files"),
            (sd + @"\Program Files (x86)\Common Files", "Common Files (32-bit)"),
            (sd + @"\Program Files", "Program Files"),
            (sd + @"\Program Files (x86)", "Program Files (x86)"),
        };
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderCount = 0;
        foreach (var (name, why) in names)
        {
            foreach (var (root, place) in places)
            {
                ct.ThrowIfCancellationRequested();
                if (folderCount >= MaxFolders) break;
                var path = root + "\\" + name;
                if (!seenFolders.Add(path)) continue;
                if (covered.Any(cv => WinPath.SameOrUnder(path, cv) || WinPath.SameOrUnder(cv, path))) continue;
                if (AppDiscovery.CheckFolder(path, sd, c.DirExists) is not null || GameLibraries.Member(path, sd) is not null) continue;
                result.Add(new AppSuggestion(SuggestionKind.Folder, path, $"In {place}; {why}.", -1, LicenceWarning(path, list),
                    new[] { AppFootprint.FolderPattern(path, sd) }, Array.Empty<string>()));
                folderCount++;
            }
        }

        // HKLM\SOFTWARE keys (both views) with one of those names that the plan does not already carry.
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyCount = 0;
        foreach (var (name, why) in names)
        {
            foreach (var rel in new[] { @"SOFTWARE\" + name, @"SOFTWARE\WOW6432Node\" + name })
            {
                ct.ThrowIfCancellationRequested();
                if (keyCount >= MaxKeys) break;
                if (!seenKeys.Add(rel) || !WinPath.PatternSafe(rel) || !c.Reg.KeyExists(RegHive.Hklm, rel)) continue;
                var key = @"HKLM\" + rel;
                if (plan.RegistryPatterns.Contains(key + " [*]", StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(new AppSuggestion(SuggestionKind.RegistryKey, key, $"Registry key; {why}.", 0, RegistryWarning(c.Reg, rel),
                    Array.Empty<string>(), new[] { key + " [*]", key + @"\* [*]" }));
                keyCount++;
            }
        }

        // Firewall rules whose program is inside a captured folder: the rule stores the exe path, so this is an exact match.
        var rules = c.FirewallRules(ct).Where(r => covered.Any(cv => AppFootprint.Mentions(r.ProgramPath, cv))).ToList();
        if (rules.Count > 0)
        {
            var safe = rules.Where(r => WinPath.PatternSafe(r.ValueName)).Take(MaxFirewallRules).ToList();
            var left = rules.Count - safe.Count;
            if (safe.Count > 0)
                result.Add(new AppSuggestion(SuggestionKind.FirewallRules, "Firewall rules",
                    "The program of each rule is inside a captured folder (an exact match, not a name guess)"
                    + (left > 0 ? $"; {left} more not offered (special characters in the rule name, or over the limit)." : "."),
                    0, null, Array.Empty<string>(), safe.Select(r => @"HKLM\" + FirewallKey + " [" + r.ValueName + "]").ToList())
                {
                    Details = safe.Select(r => r.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList()
                });
        }

        return result;
    }

    // Fills in the folder sizes. One folder can take seconds, so the wizard calls this on a worker thread, when the list is opened.
    public static IReadOnlyList<AppSuggestion> Measure(ScanContext c, IReadOnlyList<AppSuggestion> items, CancellationToken ct)
    {
        var list = new List<AppSuggestion>(items.Count);
        foreach (var s in items)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(s.Kind == SuggestionKind.Folder && s.SizeBytes < 0 ? s with { SizeBytes = c.Measure?.Invoke(s.Target) ?? 0 } : s);
        }
        return list;
    }

    // The plan with the ticked suggestions added to its rules. The size counts toward the free-space check; the labels go to the log.
    public static AppCapturePlan Apply(AppCapturePlan plan, IEnumerable<AppSuggestion> ticked)
    {
        var items = ticked.ToList();
        if (items.Count == 0) return plan;
        return plan with
        {
            FilePatterns = plan.FilePatterns.Concat(items.SelectMany(s => s.FilePatterns)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RegistryPatterns = plan.RegistryPatterns.Concat(items.SelectMany(s => s.RegistryPatterns)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SizeBytes = plan.SizeBytes + items.Where(s => s.SizeBytes > 0).Sum(s => s.SizeBytes),
            SuggestedItems = items.Select(s => s.Describe()).ToList()
        };
    }

    // Firewall rules as stored under FirewallRules: one value per rule, data "v2.31|Action=Allow|...|App=<exe>|Name=<text>|".
    internal static IReadOnlyList<FirewallRuleInfo> ReadFirewall(IRegistryReader reg, CancellationToken ct)
    {
        var rules = new List<FirewallRuleInfo>();
        foreach (var name in reg.ValueNames(RegHive.Hklm, FirewallKey).Take(MaxFirewallValues))
        {
            ct.ThrowIfCancellationRequested();
            var info = ParseRule(name, AppDiscovery.Str(reg.GetValue(RegHive.Hklm, FirewallKey, name)));
            if (info is not null) rules.Add(info);
        }
        return rules;
    }

    internal static FirewallRuleInfo? ParseRule(string valueName, string data)
    {
        string? app = null, display = null;
        foreach (var seg in data.Split('|'))
        {
            if (seg.StartsWith("App=", StringComparison.OrdinalIgnoreCase)) app = seg[4..].Trim();
            else if (seg.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)) display = seg[5..].Trim();
        }
        if (string.IsNullOrWhiteSpace(app)) return null;
        // Names such as "@{Package?ms-resource:...}" are resource references, not text.
        if (string.IsNullOrWhiteSpace(display) || display.StartsWith('@')) display = valueName;
        return new FirewallRuleInfo(valueName, display, app);
    }

    // Folders the plan already captures: the install folder, a profile's extra folders and every "<folder>\* [*]" rule.
    private static List<string> CoveredDirs(AppCapturePlan plan, string folder, string sd)
    {
        const string tail = @"\* [*]";
        var dirs = new List<string> { folder };
        dirs.AddRange(plan.ExtraFolders);
        foreach (var f in plan.FilePatterns)
        {
            if (!f.EndsWith(tail, StringComparison.Ordinal)) continue;
            var d = f[..^tail.Length].Replace("%ProgramFiles(x86)%", sd + @"\Program Files (x86)", StringComparison.OrdinalIgnoreCase);
            if (d.Contains('%') || !WinPath.IsRooted(d)) continue;
            dirs.Add(WinPath.Clean(d));
        }
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Names an app's own data usually carries: cleaned publisher and display name (the registry-key candidates of AppFootprint), the
    // install folder's name, and the name of the folder above it unless that is a generic place.
    private static List<(string Name, string Why)> CandidateNames(InstalledApp app, string folder)
    {
        var found = new List<(string Name, string Why)>();
        var leaf = folder[(folder.LastIndexOf('\\') + 1)..];
        var parent = WinPath.Parent(folder);
        var parentLeaf = parent is { Length: > 3 } ? parent[(parent.LastIndexOf('\\') + 1)..] : "";

        static bool Has(string text, string n) => text.Contains(n, StringComparison.OrdinalIgnoreCase);
        string Why(string n) =>
            Has(app.Publisher, n) ? "matches the publisher name"
            : Has(app.DisplayName, n) ? "matches the app name"
            : n.Equals(leaf, StringComparison.OrdinalIgnoreCase) ? "matches the install folder name"
            : "matches the folder above the install folder";

        void Add(string? raw)
        {
            var n = (raw ?? "").Trim().Trim('.');
            if (n.Length < 3 || !WinPath.PatternSafe(n)) return;
            if (AppFootprint.NeverVendorKeys.Contains(n, StringComparer.OrdinalIgnoreCase)) return;
            if (AppFootprint.GenericParents.Contains(n, StringComparer.OrdinalIgnoreCase)) return;
            if (found.Any(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) return;
            found.Add((n, Why(n)));
        }

        foreach (var n in AppFootprint.VendorNames(app)) Add(n);
        Add(leaf);
        if (parentLeaf.Length > 0) Add(parentLeaf);
        return found;
    }

    // Looks for file names that suggest a licence or a machine binding, two folder levels down, bounded.
    internal static string? LicenceWarning(string folder, Func<string, IReadOnlyList<FsEntry>> list)
    {
        var hits = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((folder, 0));
        var seen = 0;
        while (queue.Count > 0 && seen < MaxLicenceScan && hits.Count < 3)
        {
            var (dir, depth) = queue.Dequeue();
            foreach (var e in list(dir))
            {
                if (++seen > MaxLicenceScan) break;
                if (e.IsDir) { if (depth < 2) queue.Enqueue((dir + "\\" + e.Name, depth + 1)); }
                else if (LooksLikeLicence(e.Name) && hits.Count < 3) hits.Add(e.Name);
            }
        }
        return hits.Count == 0 ? null
            : "Holds files that look like licence or machine data (" + string.Join(", ", hits) + "): carried as they are, and may not stay valid after a reset.";
    }

    private static readonly string[] LicenceExtensions = { ".lic", ".licence", ".license", ".key", ".activation" };
    private static readonly string[] LicenceFragments = { "machineid", "machine_id", "machine-id", "machineguid", "hwid", "activation" };

    internal static bool LooksLikeLicence(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (LicenceExtensions.Any(x => n.EndsWith(x, StringComparison.Ordinal))) return true;
        if (n.StartsWith("license") || n.StartsWith("licence")) return true;
        return LicenceFragments.Any(x => n.Contains(x, StringComparison.Ordinal));
    }

    private static readonly string[] RegistryLicenceFragments =
        { "licen", "serial", "productkey", "product key", "machineid", "machine_id", "machineguid", "guid", "uuid", "hwid", "activation" };

    // A key whose top-level value names look like a licence or a machine identity.
    internal static string? RegistryWarning(IRegistryReader reg, string rel)
    {
        var hits = reg.ValueNames(RegHive.Hklm, rel)
            .Where(v => RegistryLicenceFragments.Any(f => v.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Take(3).ToList();
        return hits.Count == 0 ? null
            : "Holds values that look like licence or machine data (" + string.Join(", ", hits) + "): carried as they are, and may not stay valid after a reset.";
    }

    private static IReadOnlyList<FsEntry> RealList(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Take(2000)
                .Select(i => new FsEntry(i.Name, i is DirectoryInfo && !i.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                .ToList();
        }
        catch { return Array.Empty<FsEntry>(); }
    }
}
