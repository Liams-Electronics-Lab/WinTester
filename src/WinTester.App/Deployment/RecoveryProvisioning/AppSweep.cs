using System.Diagnostics;

namespace WinTester.App.Deployment.RecoveryProvisioning;

public enum SweepKind { Value, OdbcSource, OdbcDriver, EventSource, Environment }

// One registry string that looks like a path. Key is the path below HKLM. Name is the value name (Value, Environment; "" = the
// default value) or the data source, driver or event source name (the other kinds).
public sealed record SweepHit(SweepKind Kind, string Key, string Name, string Data);

// 3.20: what the wider search carries for one app, in USMT form, switched by CaptureOptions.IncludeSweep (default on).
public sealed record SweepResult(
    IReadOnlyList<string> FilePatterns,
    IReadOnlyList<string> RegistryPatterns,
    IReadOnlyList<string> Items,
    IReadOnlyList<string> Notes)
{
    public static readonly SweepResult Empty = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    public int Count => Items.Count;
}

// Registry strings that mention a folder, in places the normal index skips. Built once per scan, on a worker thread, and only when
// the operator turns the wider search on: HKLM\SOFTWARE holds hundreds of thousands of values and every read opens its key, so the
// walk is bounded in keys, hits and seconds. A bound that is reached sets Truncated and the result says so.
public sealed class SweepIndex
{
    public IReadOnlyList<SweepHit> Hits { get; }
    public bool Truncated { get; }
    public int KeysVisited { get; }
    public TimeSpan Elapsed { get; }

    private SweepIndex(IReadOnlyList<SweepHit> hits, bool truncated, int keys, TimeSpan elapsed)
    { Hits = hits; Truncated = truncated; KeysVisited = keys; Elapsed = elapsed; }

    internal const string OdbcIni = @"SOFTWARE\ODBC\ODBC.INI";
    internal const string OdbcInst = @"SOFTWARE\ODBC\ODBCINST.INI";
    internal const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    internal const string EventLogKey = ServiceRules.ServicesRoot + @"\EventLog";
    internal const int MaxDepth = 6;

    // Top-level names that other rules own (Classes, Clients, Uninstall under Microsoft, ...) or that hold Windows' own state.
    private static readonly string[] SkipTop =
        { "Microsoft", "Classes", "Policies", "Clients", "RegisteredApplications", "ODBC", "Windows", "WOW6432Node" };

    private static readonly string[] EventFileValues = { "EventMessageFile", "CategoryMessageFile", "ParameterMessageFile" };

    public static SweepIndex Build(IRegistryReader reg, CancellationToken ct, int maxKeys = 60_000, int maxHits = 30_000, double maxSeconds = 40)
    {
        var clock = Stopwatch.StartNew();
        var hits = new List<SweepHit>();
        var keys = 0;
        var truncated = false;
        var h = RegHive.Hklm;

        bool Stop()
        {
            if (hits.Count >= maxHits || keys >= maxKeys || clock.Elapsed.TotalSeconds > maxSeconds) { truncated = true; return true; }
            return false;
        }

        // The string values of one key that look like paths, as hits of the given kind.
        void ReadValues(SweepKind kind, string rel, string? nameOverride, IEnumerable<string>? only = null)
        {
            keys++;
            foreach (var v in only ?? reg.ValueNames(h, rel))
            {
                if (hits.Count >= maxHits) { truncated = true; return; }
                foreach (var s in Strings(reg.GetValue(h, rel, v)))
                    if (LooksLikePath(s)) hits.Add(new SweepHit(kind, rel, nameOverride ?? v, Cap(s)));
            }
        }

        // ODBC data sources and drivers: the key is carried whole, so the hit names the source or driver.
        foreach (var root in new[] { OdbcIni, OdbcInst.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\"), OdbcIni.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\"), OdbcInst })
        {
            var kind = root.EndsWith("ODBC.INI", StringComparison.OrdinalIgnoreCase) ? SweepKind.OdbcSource : SweepKind.OdbcDriver;
            foreach (var n in reg.SubKeyNames(h, root))
            {
                ct.ThrowIfCancellationRequested();
                if (n.Equals("ODBC Data Sources", StringComparison.OrdinalIgnoreCase) || n.Equals("ODBC Drivers", StringComparison.OrdinalIgnoreCase)) continue;
                if (Stop()) break;
                ReadValues(kind, root + "\\" + n, n);
            }
        }

        // Event-log sources: the message files are what the event viewer needs to show readable text.
        foreach (var log in reg.SubKeyNames(h, EventLogKey))
            foreach (var src in reg.SubKeyNames(h, EventLogKey + "\\" + log))
            {
                ct.ThrowIfCancellationRequested();
                if (Stop()) break;
                ReadValues(SweepKind.EventSource, EventLogKey + "\\" + log + "\\" + src, src, EventFileValues);
            }

        // System environment variables
        ReadValues(SweepKind.Environment, EnvironmentKey, null);

        // Everything else under SOFTWARE and its WOW6432Node twin, to a bounded depth
        void Walk(string rel, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (Stop()) return;
            ReadValues(SweepKind.Value, rel, null);
            if (depth >= MaxDepth) return;
            foreach (var sub in reg.SubKeyNames(h, rel))
            {
                if (truncated) return;
                Walk(rel + "\\" + sub, depth + 1);
            }
        }
        foreach (var root in new[] { "SOFTWARE", @"SOFTWARE\WOW6432Node" })
            foreach (var top in reg.SubKeyNames(h, root))
            {
                if (SkipTop.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
                if (truncated) break;
                Walk(root + "\\" + top, 1);
            }

        return new SweepIndex(hits, truncated, keys, clock.Elapsed);
    }

    private static string Cap(string s) => s.Length > 4000 ? s[..4000] : s;

    private static IEnumerable<string> Strings(object? v) => v switch
    {
        string s when s.Length > 0 => new[] { s },
        string[] a => a.Where(x => !string.IsNullOrEmpty(x)),
        _ => Array.Empty<string>()
    };

    // "C:\..." anywhere in the text, or an environment variable that expands to a path.
    internal static bool LooksLikePath(string s)
    {
        if (s.Contains('%')) return true;
        var i = s.IndexOf(@":\", StringComparison.Ordinal);
        return i >= 1 && char.IsLetter(s[i - 1]);
    }
}

public static class AppSweep
{
    private const int MaxValues = 200;
    private const int MaxFirewall = 200;
    private const int MaxOther = 100;

    // What the wider search finds for one app: registry strings, firewall rules, ODBC sources and drivers, event-log sources,
    // system environment variables (not PATH) and tasks with a trigger other than boot or logon, all pointing into the app's folder
    // or one of its profile folders. Nothing here is a name guess: every rule rests on a stored path that names the folder.
    public static SweepResult Derive(SweepIndex index, IReadOnlyList<FirewallRuleInfo> firewall, IReadOnlyList<TaskInfo> tasks,
        IRegistryReader reg, string systemDrive, AppCapturePlan plan)
    {
        var sd = WinPath.Clean(systemDrive);
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.Folder)) roots.Add(plan.Folder);
        roots.AddRange(plan.ExtraFolders);
        if (roots.Count == 0) return SweepResult.Empty;
        bool InRoots(string data) => roots.Any(r => AppFootprint.Mentions(data, r));

        var files = new List<string>();
        var regs = new List<string>();
        var items = new List<string>();
        var notes = new List<string>();
        var carried = new HashSet<string>(plan.RegistryPatterns, StringComparer.OrdinalIgnoreCase);

        // 1. Registry values elsewhere under SOFTWARE (both views) whose text names the folder: only that value is carried.
        var values = new List<string>();
        foreach (var hit in index.Hits.Where(x => x.Kind == SweepKind.Value && InRoots(x.Data)))
        {
            if (Covered(carried, hit.Key)) continue;
            if (hit.Name.Length == 0)
            {
                notes.Add(@"The default value of HKLM\" + hit.Key + " points into this app but a rule cannot name a default value, so it is not captured.");
                continue;
            }
            if (!WinPath.PatternSafe(hit.Key) || !WinPath.PatternSafe(hit.Name))
            {
                notes.Add(@"The value """ + hit.Name + @""" in HKLM\" + hit.Key + " is not captured: its name has characters a rule cannot express.");
                continue;
            }
            values.Add(@"HKLM\" + hit.Key + " [" + hit.Name + "]");
        }
        values = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Count > MaxValues)
        {
            notes.Add("Registry values cut at " + MaxValues + " of " + values.Count + "; the rest are not carried.");
            values = values.Take(MaxValues).ToList();
        }
        regs.AddRange(values);
        if (values.Count > 0)
            items.Add("Registry values: " + values.Count + " (" + string.Join(", ", values.Take(3).Select(v => v[5..])) + (values.Count > 3 ? ", ..." : "") + ")");

        // 2. Firewall rules: the rule stores the program path, so this is an exact match.
        var rules = firewall.Where(r => InRoots(r.ProgramPath)).ToList();
        var safeRules = rules.Where(r => WinPath.PatternSafe(r.ValueName)).Take(MaxFirewall).ToList();
        if (safeRules.Count > 0)
        {
            regs.AddRange(safeRules.Select(r => @"HKLM\" + AppSuggestions.FirewallKey + " [" + r.ValueName + "]"));
            items.Add("Firewall rules: " + safeRules.Count);
        }
        if (rules.Count > safeRules.Count)
            notes.Add((rules.Count - safeRules.Count) + " firewall rule(s) are not carried (special characters in the rule name, or over the limit).");

        // 3. ODBC data sources and drivers, with their entry in the list Windows reads them from.
        foreach (var group in index.Hits.Where(x => x.Kind is SweepKind.OdbcSource or SweepKind.OdbcDriver && InRoots(x.Data))
                     .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Take(MaxOther))
        {
            var first = group.First();
            if (!WinPath.PatternSafe(first.Key) || !WinPath.PatternSafe(first.Name))
            {
                notes.Add("ODBC entry " + first.Name + " is not captured: special characters.");
                continue;
            }
            AddTree(regs, reg, first.Key);
            var listKey = first.Key[..first.Key.LastIndexOf('\\')] + (first.Kind == SweepKind.OdbcSource ? @"\ODBC Data Sources" : @"\ODBC Drivers");
            if (reg.ValueNames(RegHive.Hklm, listKey).Contains(first.Name, StringComparer.OrdinalIgnoreCase))
                regs.Add(@"HKLM\" + listKey + " [" + first.Name + "]");
            items.Add((first.Kind == SweepKind.OdbcSource ? "ODBC data source: " : "ODBC driver: ") + first.Name);
        }

        // 4. Event-log sources whose message file is inside the folder.
        foreach (var group in index.Hits.Where(x => x.Kind == SweepKind.EventSource && InRoots(x.Data))
                     .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Take(MaxOther))
        {
            var first = group.First();
            if (!WinPath.PatternSafe(first.Key)) { notes.Add("Event-log source " + first.Name + " is not captured: special characters."); continue; }
            AddTree(regs, reg, first.Key);
            items.Add("Event-log source: " + first.Name);
        }

        // 5. System environment variables. PATH is one value shared by every program: restoring it would overwrite the PATH of the
        //    reset system, so an entry in the folder is reported, not captured.
        foreach (var hit in index.Hits.Where(x => x.Kind == SweepKind.Environment))
        {
            if (hit.Name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                if (hit.Data.Split(';').Any(InRoots))
                    notes.Add("The system PATH has an entry inside this app's folder. PATH is one value shared by every program, so it is not captured; add the entry again after a reset if the app needs it.");
                continue;
            }
            if (!InRoots(hit.Data)) continue;
            if (!WinPath.PatternSafe(hit.Name)) { notes.Add("Environment variable \"" + hit.Name + "\" is not captured: special characters."); continue; }
            regs.Add(@"HKLM\" + hit.Key + " [" + hit.Name + "]");
            items.Add("Environment variable: " + hit.Name);
        }

        // 6. Tasks with a trigger other than boot or logon.
        var other = StartupRules.DeriveOtherTasks(roots, reg, tasks, sd);
        files.AddRange(other.FilePatterns);
        regs.AddRange(other.RegistryPatterns);
        items.AddRange(other.Items);
        notes.AddRange(other.Notes);

        if (index.Truncated)
            notes.Add("The wider search stopped at its limit (" + index.KeysVisited + " keys), so some registrations may be missing.");

        return new SweepResult(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            regs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            items, notes);
    }

    // True when a pattern already carries this key's values: the key itself, or a parent whose "\* [*]" covers its sub-keys.
    internal static bool Covered(HashSet<string> patterns, string rel)
    {
        if (patterns.Contains(@"HKLM\" + rel + " [*]")) return true;
        var parts = rel.Split('\\');
        for (var i = 1; i < parts.Length; i++)
            if (patterns.Contains(@"HKLM\" + string.Join("\\", parts[..i]) + @"\* [*]")) return true;
        return false;
    }

    private static void AddTree(List<string> into, IRegistryReader reg, string rel)
    {
        into.Add(@"HKLM\" + rel + " [*]");
        if (reg.SubKeyNames(RegHive.Hklm, rel).Count > 0) into.Add(@"HKLM\" + rel + @"\* [*]");
    }
}
