using System.IO;
using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// What one app contributes to the generated /i: file. Patterns are in USMT form ("<folder>\* [*]", "<key> [*]").
public sealed record AppCapturePlan(
    string Name,
    string Version,
    string Publisher,
    string Folder,
    long SizeBytes,
    IReadOnlyList<string> FilePatterns,
    IReadOnlyList<string> RegistryPatterns,
    IReadOnlyList<string> Notes)
{
    // Services and "starts with Windows" entries (3.11/C40). Kept apart from the base rules so the Configure step can
    // switch them without a rescan; RulesXmlBuilder, the manifest and the worker go through FilesFor/RegistryFor.
    public IReadOnlyList<string> ServiceRegistryPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StartupFilePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StartupRegistryPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ServiceItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> StartupItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShellItems { get; init; } = Array.Empty<string>();      // context-menu and similar handlers, always carried
    public IReadOnlyList<string> MsiItems { get; init; } = Array.Empty<string>();        // Windows Installer products, always carried
    public IReadOnlyList<string> ComItems { get; init; } = Array.Empty<string>();        // COM registration summary, always carried

    // From the app profiles (3.14): exclusion patterns inside the app folder (game libraries and profile rules), their short
    // labels for the list, log and manifest, the profiles that matched, and the advice text.
    public IReadOnlyList<string> ExcludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ProfileNames { get; init; } = Array.Empty<string>();

    // Vendor folders outside the app folder that a profile adds (S127), for the log. They are part of FilePatterns already.
    public IReadOnlyList<string> ExtraFolders { get; init; } = Array.Empty<string>();

    // 3.18: added by folder, no Uninstall entry.
    public bool IsCustom { get; init; }

    // 3.19: labels of the suggestions the operator ticked for this app (their patterns are already part of the plan), for the log.
    public IReadOnlyList<string> SuggestedItems { get; init; } = Array.Empty<string>();

    public int RuleCount => FilePatterns.Count + RegistryPatterns.Count;

    // 3.20: what the wider search found, carried only when the option is on. Filled in after the scan, once the search has run.
    public IReadOnlyList<string> SweepFilePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SweepRegistryPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SweepItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SweepNotes { get; init; } = Array.Empty<string>();

    public AppCapturePlan WithSweep(SweepResult r) => this with
    {
        SweepFilePatterns = r.FilePatterns, SweepRegistryPatterns = r.RegistryPatterns, SweepItems = r.Items, SweepNotes = r.Notes
    };

    public IReadOnlyList<string> FilesFor(CaptureOptions o)
    {
        if (!o.IncludeStartup && !o.IncludeSweep) return FilePatterns;
        IEnumerable<string> all = FilePatterns;
        if (o.IncludeStartup) all = all.Concat(StartupFilePatterns);
        if (o.IncludeSweep) all = all.Concat(SweepFilePatterns);
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> RegistryFor(CaptureOptions o)
    {
        IEnumerable<string> all = RegistryPatterns;
        if (o.IncludeServices) all = all.Concat(ServiceRegistryPatterns);
        if (o.IncludeStartup) all = all.Concat(StartupRegistryPatterns);
        if (o.IncludeSweep) all = all.Concat(SweepRegistryPatterns);
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> SweepItemsFor(CaptureOptions o) => o.IncludeSweep ? SweepItems : Array.Empty<string>();

    // What the options add on top of the base rules, for the manifest, the log and the Configure list.
    public IReadOnlyList<string> ServiceItemsFor(CaptureOptions o) => o.IncludeServices ? ServiceItems : Array.Empty<string>();
    public IReadOnlyList<string> StartupItemsFor(CaptureOptions o) => o.IncludeStartup ? StartupItems : Array.Empty<string>();
}

public static class AppFootprint
{
    // Vendor/registry names that are never taken as an app's own key, whatever the publisher text says.
    internal static readonly string[] NeverVendorKeys =
    {
        "Microsoft", "Classes", "Clients", "Policies", "Windows", "WOW6432Node", "RegisteredApplications", "ODBC", "Setup",
        "Intel", "NVIDIA Corporation", "AMD", "Realtek", "Qualcomm", "Wow6432Node"
    };

    internal static readonly string[] GenericParents =
        { "Program Files", "Program Files (x86)", "ProgramData", "Common Files", "Apps", "Applications", "Software", "Tools", "Programs", "Bin" };

    private static readonly Regex Suffix = new(
        @"[,\s]+(inc\.?|llc|ltd\.?|limited|corp\.?|corporation|gmbh|co\.?|s\.?a\.?|software|foundation|team|project)\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex TrailingParen = new(@"\s*\([^)]*\)\s*$");
    private static readonly Regex TrailingVersion = new(@"[\s_-]+v?\d[\d.\-_a-z]*\s*$", RegexOptions.IgnoreCase);

    // Candidate names of the app's own HKLM\SOFTWARE\<name> key: cleaned publisher, display name, display name
    // without version/architecture. A candidate only becomes a rule when that key actually exists.
    internal static IReadOnlyList<string> VendorNames(InstalledApp app)
    {
        var names = new List<string>();
        void Add(string? n)
        {
            n = (n ?? "").Trim().Trim('.');
            if (n.Length < 2) return;
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }

        var pub = app.Publisher;
        for (var i = 0; i < 3; i++) pub = Suffix.Replace(pub, "").Trim();
        Add(pub);
        Add(app.DisplayName);

        // 3.18: an app added by folder has no publisher, so its folder name and (unless it is a generic place) its parent folder's
        // name are the candidates: C:\Program Files\Acme Corp\Billing may keep its settings under HKLM\SOFTWARE\Acme Corp.
        if (app.IsCustom && app.Folder is not null)
        {
            string Leaf(string p) => p[(p.LastIndexOf('\\') + 1)..];
            Add(Leaf(app.Folder));
            var parent = WinPath.Parent(app.Folder);
            if (parent is not null && parent.Length > 3 && !GenericParents.Contains(Leaf(parent), StringComparer.OrdinalIgnoreCase))
                Add(Leaf(parent));
        }

        var d = app.DisplayName;
        for (var i = 0; i < 3; i++)
        {
            var before = d;
            d = TrailingParen.Replace(d, "").Trim();
            d = TrailingVersion.Replace(d, "").Trim();
            if (d == before) break;
        }
        Add(d);
        return names;
    }

    public static AppCapturePlan Build(InstalledApp app, SystemRegistryIndex index, IReadOnlyList<ShortcutInfo> shortcuts,
        IRegistryReader reg, string systemDrive, IReadOnlyList<TaskInfo>? tasks = null,
        IReadOnlyList<ShortcutInfo>? userStartupShortcuts = null, Func<string, bool>? dirExists = null,
        AppProfileEffects? profile = null)
    {
        if (app.Folder is null || (app.UninstallRel is null && !app.IsCustom))
            throw new InvalidOperationException("Only a capturable HKLM app (or an app added by folder) has a plan.");

        var sd = WinPath.Clean(systemDrive);
        var files = new List<string>();
        var regs = new List<string>();
        var notes = new List<string>();
        var folder = app.Folder;

        // The app's own folder plus the vendor folders an app profile added (S127). A registration that points into any of them
        // belongs to the app: Google Update's service and tasks sit beside Chrome's folder, not inside it.
        var fx = profile ?? AppProfiles.None;
        var roots = new List<string> { folder };
        roots.AddRange(fx.ExtraRoots);
        bool InRoots(string data) => roots.Any(r => Mentions(data, r));

        // 1. Files: the install folder
        files.Add(FolderPattern(folder, sd));

        // 2. The Uninstall key itself (this is what makes Windows list the app, C31/4.12)
        if (app.UninstallRel is null)
        {
            // 3.18: added by folder, nothing to list in Settings > Apps (the Configure list and the log say so; not a "not carried" note)
        }
        else
        {
            var uninstall = @"HKLM\" + app.UninstallRel;
            if (WinPath.PatternSafe(app.UninstallRel))
            {
                regs.Add(uninstall + " [*]");
                if (reg.SubKeyNames(RegHive.Hklm, app.UninstallRel).Count > 0) regs.Add(uninstall + @"\* [*]");
            }
            else notes.Add("Uninstall key name has characters a rule cannot express, so the app will not be listed as installed.");
        }

        // 3. The app's own vendor key under SOFTWARE and its WOW6432Node twin (only if present)
        var capturedRel = new List<string>();      // key paths below HKLM, for the RegisteredApplications match
        foreach (var name in VendorNames(app))
        {
            if (NeverVendorKeys.Contains(name, StringComparer.OrdinalIgnoreCase) || !WinPath.PatternSafe(name)) continue;
            foreach (var rel in new[] { @"SOFTWARE\" + name, @"SOFTWARE\WOW6432Node\" + name })
            {
                if (!reg.KeyExists(RegHive.Hklm, rel)) continue;
                AddTree(regs, @"HKLM\" + rel);
                capturedRel.Add(rel);
            }
        }

        // 4. Registrations that point into the install folder
        var services = new List<string>();
        var shellItems = new List<string>();
        var comRefs = new List<RegRef>();
        foreach (var r in index.Refs)
        {
            if (r.Kind == RefKind.RegisteredApp) continue;
            if (!r.Data.Any(InRoots)) continue;
            switch (r.Kind)
            {
                case RefKind.AppPath:
                    if (!WinPath.PatternSafe(r.RulePath)) { notes.Add("App Paths entry skipped (special characters): " + r.RulePath); break; }
                    regs.Add(r.RulePath + " [*]");
                    if (r.HasSubKeys) regs.Add(r.RulePath + @"\* [*]");
                    break;
                case RefKind.Client:
                case RefKind.Class:
                    if (!WinPath.PatternSafe(r.RulePath)) { notes.Add("Registration skipped (special characters): " + r.RulePath); break; }
                    AddTree(regs, r.RulePath);
                    capturedRel.Add(r.RulePath[5..]);   // strip "HKLM\"
                    break;
                case RefKind.Service:
                    services.Add(r.ValueName ?? r.RulePath);
                    break;
                case RefKind.ShellExt:
                    regs.AddRange(r.Patterns);
                    shellItems.Add("Shell extension: " + r.Label);
                    break;
                case RefKind.Com:
                    comRefs.Add(r);
                    break;
            }
        }

        // 4b. COM classes whose server is inside the folder (S123, C48): an app with hundreds of classes is cut at the cap
        var comItems = new List<string>();
        if (comRefs.Count > 0)
        {
            foreach (var r in comRefs.Take(MaxComClassesPerApp)) regs.AddRange(r.Patterns);
            var names = comRefs.Take(3).Select(r => r.Label);
            comItems.Add("COM registration: " + Math.Min(comRefs.Count, MaxComClassesPerApp) + " class(es) (" + string.Join(", ", names)
                         + (comRefs.Count > 3 ? ", ..." : "") + ")");
            if (comRefs.Count > MaxComClassesPerApp)
                notes.Add("COM registration cut at " + MaxComClassesPerApp + " of " + comRefs.Count + " classes; the rest are not registered after a reset.");
        }

        // 5. RegisteredApplications values that point at a key captured above (default-apps registration)
        foreach (var r in index.Refs.Where(x => x.Kind == RefKind.RegisteredApp))
        {
            var target = r.Data[0].Trim().TrimStart('\\');
            if (!capturedRel.Any(c => target.StartsWith(c, StringComparison.OrdinalIgnoreCase)
                                      || target.StartsWith(c.Replace(@"SOFTWARE\", @"Software\"), StringComparison.OrdinalIgnoreCase))) continue;
            if (!WinPath.PatternSafe(r.ValueName!)) continue;
            regs.Add(r.RulePath + " [" + r.ValueName + "]");
        }

        // 6. All-users Start-menu shortcuts: by target inside the folder, or named like the app
        foreach (var s in shortcuts)
        {
            var byTarget = s.Target is not null && InRoots(s.Target);
            var byName = Path.GetFileNameWithoutExtension(s.FileName).Equals(app.DisplayName, StringComparison.OrdinalIgnoreCase);
            if (!byTarget && !byName) continue;
            if (!WinPath.PatternSafeAfterBracketEscape(s.Dir) || !WinPath.PatternSafeAfterBracketEscape(s.FileName))
            {
                notes.Add("Start-menu shortcut skipped (special characters): " + s.FileName);
                continue;
            }
            if (WinPath.HasBrackets(s.Dir) || WinPath.HasBrackets(s.FileName))
                notes.Add("Start-menu shortcut path has square brackets and is written with escaped brackets (^[ and ^]); check the capture log: " + s.FileName);
            files.Add(WinPath.EscapeBrackets(s.Dir) + " [" + WinPath.EscapeBrackets(s.FileName) + "]");
        }

        // 7. Services whose image path is inside the folder: only the safe subset is carried (ServiceRules, C40)
        var servicePatterns = new List<string>();
        var serviceItems = new List<string>();
        foreach (var name in services.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var verdict = ServiceRules.Evaluate(reg, name);
            if (verdict.Capture)
            {
                servicePatterns.AddRange(ServiceRules.Patterns(reg, name));
                serviceItems.Add("Service: " + name);
            }
            else notes.Add("The service \"" + name + "\" is not captured: " + verdict.Reason + ".");
        }

        // 8. What it takes to start with Windows: all-users Run values and boot/logon tasks that launch the app
        var startup = StartupRules.Derive(folder, reg, tasks ?? Array.Empty<TaskInfo>(),
            userStartupShortcuts ?? Array.Empty<ShortcutInfo>(), sd);
        foreach (var extra in fx.ExtraRoots)
            startup = MergeStartup(startup, StartupRules.Derive(extra, reg, tasks ?? Array.Empty<TaskInfo>(),
                userStartupShortcuts ?? Array.Empty<ShortcutInfo>(), sd));
        notes.AddRange(startup.Notes);

        // 9. .NET runtimes install outside the folder their Uninstall entry names, so they get their own rules
        var extraRoots = new List<string>();
        if (RuntimeRules.IsDotNetRuntime(app.DisplayName))
        {
            var rt = RuntimeRules.Derive(app, reg, sd, dirExists);
            if (rt.Root is not null) extraRoots.Add(rt.Root);
            files.AddRange(rt.FilePatterns);
            regs.AddRange(rt.RegistryPatterns);
            notes.AddRange(rt.Notes);
            if (rt.Items.Count > 0) notes.Add("Runtime files carried: " + string.Join(", ", rt.Items) + ".");
        }

        // 10. Windows Installer registration (S121): product, features, components, upgrade codes and the cached .msi, so that
        // Repair and Uninstall keep working after a reset
        var msi = MsiRules.Derive(app, folder, extraRoots, index.Msi, reg, sd, dirExists);
        files.AddRange(msi.FilePatterns);
        regs.AddRange(msi.RegistryPatterns);
        notes.AddRange(msi.Notes);
        if (app.IsMsi && msi.Items.Count == 0)
            notes.Add("Installed with Windows Installer, but no product registration was found for it, so Repair/Uninstall may fail after a reset.");

        // 11. App profile extras (3.14): folders and registry keys a profile adds; exclusions travel as ExcludePatterns
        files.AddRange(fx.ExtraFilePatterns);
        regs.AddRange(fx.ExtraRegistryPatterns);
        notes.AddRange(fx.Notes);

        return new AppCapturePlan(app.DisplayName, app.Version, app.Publisher, folder, app.SizeBytes,
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            regs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            notes)
        {
            ServiceRegistryPatterns = servicePatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StartupFilePatterns = startup.FilePatterns,
            StartupRegistryPatterns = startup.RegistryPatterns,
            ServiceItems = serviceItems,
            StartupItems = startup.Items,
            ShellItems = shellItems.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            MsiItems = msi.Items,
            ComItems = comItems,
            ExcludePatterns = fx.ExcludePatterns,
            ExcludedItems = fx.ExcludedItems,
            ProfileNames = app.ProfileNames,
            ExtraFolders = fx.ExtraRoots,
            IsCustom = app.IsCustom
        };
    }

    // Two startup results for the same app (its own folder and a vendor folder), without repeats.
    private static StartupResult MergeStartup(StartupResult a, StartupResult b)
    {
        static List<string> Join(IReadOnlyList<string> x, IReadOnlyList<string> y) =>
            x.Concat(y).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new StartupResult(Join(a.FilePatterns, b.FilePatterns), Join(a.RegistryPatterns, b.RegistryPatterns),
            Join(a.Items, b.Items), Join(a.Notes, b.Notes));
    }

    // COM classes carried per app (each can bring its interface, type library and ProgID keys).
    internal const int MaxComClassesPerApp = 300;

    private static void AddTree(List<string> into, string keyPath)
    {
        into.Add(keyPath + " [*]");
        into.Add(keyPath + @"\* [*]");
    }

    // "<folder>\* [*]". Folders under Program Files (x86) use the variable so the parentheses are not written literally
    // (the binary's own sysdrive.xml uses %programfiles(x86)%; this exact form is untested, see the post-run check).
    internal static string FolderPattern(string folder, string systemDrive)
    {
        var x86 = systemDrive + @"\Program Files (x86)";
        var path = WinPath.SameOrUnder(folder, x86) ? "%ProgramFiles(x86)%" + folder[x86.Length..] : folder;
        return path + @"\* [*]";
    }

    // True when `data` (a command line, icon path or similar) names something inside `folder`.
    internal static bool Mentions(string data, string folder)
    {
        if (string.IsNullOrWhiteSpace(data)) return false;
        var d = Environment.ExpandEnvironmentVariables(data).Replace('/', '\\');
        var f = folder.TrimEnd('\\') + "\\";
        return d.Contains(f, StringComparison.OrdinalIgnoreCase)
               || d.Trim().Trim('"').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ScannedApp(InstalledApp App, AppCapturePlan? Plan);

// What one scan needs to plan more apps later (3.18 adds an app by folder without scanning again). The system registry index
// (the slow Classes pass) and the all-users Start-menu list are built once, on first use, and kept.
public sealed class ScanContext
{
    public IRegistryReader Reg { get; }
    public string SystemDrive { get; }
    public Func<string, bool> DirExists { get; }
    public Func<string, long>? Measure { get; }
    public string StartMenuRoot { get; }
    public Func<string, string?> ResolveShortcut { get; }
    public IReadOnlyList<TaskInfo>? Tasks { get; }
    public IReadOnlyList<ShortcutInfo>? UserStartup { get; }
    public AppProfileSet? Profiles { get; }

    private readonly object _gate = new();
    private SystemRegistryIndex? _index;
    private IReadOnlyList<ShortcutInfo>? _shortcuts;
    private IReadOnlyList<FirewallRuleInfo>? _firewall;
    private SweepIndex? _sweep;
    private readonly object _sweepGate = new();

    internal ScanContext(IRegistryReader reg, string systemDrive, Func<string, bool> dirExists, Func<string, long>? measure,
        string startMenuRoot, Func<string, string?> resolveShortcut, IReadOnlyList<TaskInfo>? tasks,
        IReadOnlyList<ShortcutInfo>? userStartup, AppProfileSet? profiles)
    {
        Reg = reg; SystemDrive = systemDrive; DirExists = dirExists; Measure = measure; StartMenuRoot = startMenuRoot;
        ResolveShortcut = resolveShortcut; Tasks = tasks; UserStartup = userStartup; Profiles = profiles;
    }

    public SystemRegistryIndex Index(CancellationToken ct) { lock (_gate) return _index ??= SystemRegistryIndex.Build(Reg, ct); }
    public IReadOnlyList<ShortcutInfo> Shortcuts(CancellationToken ct)
    { lock (_gate) return _shortcuts ??= StartMenuScanner.Scan(StartMenuRoot, ResolveShortcut, ct); }

    // 3.20: the wider-search index, built once on first use (own lock, so adding an app by folder is not held up by it).
    public SweepIndex Sweep(CancellationToken ct) { lock (_sweepGate) return _sweep ??= SweepIndex.Build(Reg, ct); }

    // 3.19: the firewall rules, read once, for the suggestions.
    public IReadOnlyList<FirewallRuleInfo> FirewallRules(CancellationToken ct)
    { lock (_gate) return _firewall ??= AppSuggestions.ReadFirewall(Reg, ct); }
}

public sealed record AppScanResult(IReadOnlyList<ScannedApp> Apps)
{
    public int CapturableCount => Apps.Count(a => a.Plan is not null);

    // Set by AppScanner.Scan; null for results built by hand (preview mode).
    public ScanContext? Context { get; init; }
}

// Outcome of adding an app by folder: the new entry, or the reason it was refused (exactly one is set).
public sealed record CustomAppResult(ScannedApp? Added, string? Refusal);

public static class AppScanner
{
    // The real scan: registry, file system and the all-users Start menu of this PC. Slow part is the Classes pass and
    // measuring folders without a reported size, so the wizard calls it on a worker thread.
    public static AppScanResult Scan(string systemDrive, CancellationToken ct)
    {
        var lnk = new LnkResolver();
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        var tasks = TaskScanner.Scan(Path.Combine(WinPath.Clean(systemDrive) + "\\", @"Windows\System32\Tasks"), new WinRegistryReader(), ct);
        var userStartup = StartMenuScanner.Scan(Environment.GetFolderPath(Environment.SpecialFolder.Startup), lnk.Resolve, ct);
        return Scan(new WinRegistryReader(), systemDrive, Directory.Exists, FolderSize.Measure, startMenu, lnk.Resolve, ct,
            tasks, userStartup, AppProfileSet.LoadDefault());
    }

    public static AppScanResult Scan(IRegistryReader reg, string systemDrive, Func<string, bool> dirExists,
        Func<string, long>? measure, string startMenuRoot, Func<string, string?> resolveShortcut, CancellationToken ct,
        IReadOnlyList<TaskInfo>? tasks = null, IReadOnlyList<ShortcutInfo>? userStartupShortcuts = null,
        AppProfileSet? profiles = null)
    {
        var ctx = new ScanContext(reg, systemDrive, dirExists, measure, startMenuRoot, resolveShortcut, tasks, userStartupShortcuts, profiles);
        var apps = AppDiscovery.Discover(reg, systemDrive, dirExists, measure, ct);

        // 3.14: profiles and game-library policy (no profile set = the plain 3.7 behaviour, used by older checks)
        var effects = new Dictionary<string, AppProfileEffects>(StringComparer.OrdinalIgnoreCase);
        if (profiles is not null)
        {
            var profiled = AppProfiles.Apply(apps, profiles, reg, systemDrive, dirExists);
            apps = profiled.Select(p => p.App).ToList();
            foreach (var p in profiled) effects[p.App.Source + "|" + p.App.KeyName] = p.Effects;
        }
        if (!apps.Any(a => a.Capturable)) return new AppScanResult(apps.Select(a => new ScannedApp(a, null)).ToList()) { Context = ctx };

        var scanned = new List<ScannedApp>();
        foreach (var a in apps)
        {
            ct.ThrowIfCancellationRequested();
            effects.TryGetValue(a.Source + "|" + a.KeyName, out var fx);
            scanned.Add(PlanFor(ctx, a, fx, ct));
        }
        return new AppScanResult(scanned) { Context = ctx };
    }

    // The plan for one app (null plan, and the app marked "cannot be captured", when rules could not be derived).
    private static ScannedApp PlanFor(ScanContext c, InstalledApp a, AppProfileEffects? fx, CancellationToken ct)
    {
        AppCapturePlan? plan = null;
        if (a.Capturable)
        {
            try
            {
                plan = AppFootprint.Build(a, c.Index(ct), c.Shortcuts(ct), c.Reg, c.SystemDrive, c.Tasks, c.UserStartup, c.DirExists, fx);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* a plan that cannot be built leaves the app listed as not capturable */ }
        }
        return new ScannedApp(plan is null && a.Capturable
            ? a with { BlockReason = "Rules for this app could not be derived." } : a, plan);
    }

    // 3.18: adds an app by its folder to a finished scan, with no Uninstall entry needed. Everything that points into the folder
    // (services, boot/logon tasks, Run values, COM classes, shell extensions, App Paths, shortcuts, MSI records) is found by the same
    // matching as for a scanned app. A folder is refused (nothing is added) when it is a system folder, outside the system drive, a
    // game library, already in the list, inside an app that is in the list, or when it holds or sits in an app that cannot be
    // captured (that would walk around the block).
    public static CustomAppResult AddCustom(AppScanResult current, string? folder, string? name, CancellationToken ct)
    {
        var c = current.Context;
        if (c is null) return new CustomAppResult(null, "Run the app scan first.");
        var sd = WinPath.Clean(c.SystemDrive);

        var (app, refusal) = AppDiscovery.FromFolder(folder, name, sd, c.DirExists, c.Measure);
        if (app is null) return new CustomAppResult(null, refusal);
        var f = app.Folder!;

        foreach (var sa in current.Apps)
        {
            var other = sa.App.Folder;
            if (other is null) continue;
            var listed = sa.Plan is not null;
            if (other.Equals(f, StringComparison.OrdinalIgnoreCase))
                return new CustomAppResult(null, listed
                    ? $"This folder is already in the list as \"{sa.App.DisplayName}\"."
                    : $"This folder belongs to \"{sa.App.DisplayName}\", which cannot be captured: {sa.App.BlockReason}");
            if (WinPath.SameOrUnder(f, other))
                return new CustomAppResult(null, listed
                    ? $"This folder is inside \"{sa.App.DisplayName}\", which is already in the list: tick that app to capture it."
                    : $"This folder is inside \"{sa.App.DisplayName}\", which cannot be captured: {sa.App.BlockReason}");
            if (!listed && WinPath.SameOrUnder(other, f) && c.DirExists(other))
                return new CustomAppResult(null, $"This folder holds \"{sa.App.DisplayName}\", which cannot be captured: {sa.App.BlockReason}");
        }

        AppProfileEffects? fx = null;
        if (c.Profiles is not null)
        {
            var profiled = AppProfiles.Apply(new[] { app }, c.Profiles, c.Reg, sd, c.DirExists)[0];
            app = profiled.App;
            fx = profiled.Effects;
        }
        if (!app.Capturable) return new CustomAppResult(null, app.BlockReason);

        var result = PlanFor(c, app, fx, ct);
        return result.Plan is null ? new CustomAppResult(null, result.App.BlockReason) : new CustomAppResult(result, null);
    }
}
