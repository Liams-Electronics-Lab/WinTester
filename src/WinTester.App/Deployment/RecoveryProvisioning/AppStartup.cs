using System.IO;
using System.Xml.Linq;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Item 3.11: what an app needs so that it also starts after Reset this PC. Two parts, both derived from the app's
// install folder and both switchable on the Configure step: its own services, and the things that launch it with
// Windows. Pure logic behind IRegistryReader plus a task list, so the scratch harness checks it with a fake registry.
// None of it has run against a real machine yet.

public sealed record ServiceVerdict(bool Capture, string Reason);

public static class ServiceRules
{
    // Written as CurrentControlSet because that is the path the registry index already reports. It is a link to
    // ControlSet001; if ScanState does not store rules written with it, this one constant is the place to change.
    public const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";

    private const int KernelOrDriverBits = 0x0F;     // kernel, file-system, adapter, recognizer drivers
    private const int UserServiceBits = 0xC0;        // per-user service templates and instances
    private const int OwnOrSharedProcess = 0x30;

    // A service is carried only when losing it cannot hurt the boot and the package can recreate it completely.
    public static ServiceVerdict Evaluate(IRegistryReader reg, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || !WinPath.PatternSafe(serviceName))
            return new(false, "its name has characters a rule cannot express");

        var rel = ServicesRoot + "\\" + serviceName;
        if (!reg.KeyExists(RegHive.Hklm, rel)) return new(false, "its registry key was not found");

        var typeRaw = reg.GetValue(RegHive.Hklm, rel, "Type");
        var startRaw = reg.GetValue(RegHive.Hklm, rel, "Start");
        if (typeRaw is null || startRaw is null) return new(false, "its type or start mode could not be read");

        var type = AppDiscovery.Int(typeRaw) & ~0x100;   // 0x100 = may interact with the desktop, no effect here
        var start = AppDiscovery.Int(startRaw);

        if ((type & KernelOrDriverBits) != 0)
            return new(false, "it is a driver (kernel-mode drivers are never captured; a driver with a driver package is carried by the drivers option)");
        if ((type & UserServiceBits) != 0)
            return new(false, "it is a per-user service template");
        if ((type & OwnOrSharedProcess) == 0)
            return new(false, "it is not an own-process or shared-process service");

        if (start is 0 or 1)
            return new(false, "it starts at boot or system start, and a missing file there could stop Windows from starting");
        if (start is < 2 or > 4)
            return new(false, "its start mode is not one of automatic, manual or disabled");

        var account = AppDiscovery.Str(reg.GetValue(RegHive.Hklm, rel, "ObjectName")).Trim();
        if (account.Length > 0 && !RunsAsBuiltInAccount(account))
            return new(false, "it runs as the account \"" + account + "\" and a package cannot carry that account's password");

        return new(true, "");
    }

    // Empty ObjectName means LocalSystem.
    internal static bool RunsAsBuiltInAccount(string account) =>
        account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
        || account.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
        || account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase);

    // The key and, when it has sub-keys (Parameters, Security, ...), everything below it.
    public static IReadOnlyList<string> Patterns(IRegistryReader reg, string serviceName)
    {
        var key = @"HKLM\" + ServicesRoot + "\\" + serviceName;
        var list = new List<string> { key + " [*]" };
        if (reg.SubKeyNames(RegHive.Hklm, ServicesRoot + "\\" + serviceName).Count > 0) list.Add(key + @"\* [*]");
        return list;
    }
}

// One scheduled task as read from Windows\System32\Tasks, with what the capture needs to know about it.
public sealed record TaskInfo(
    string RelPath,                      // path below the Tasks folder, e.g. "GoogleSystem\GoogleUpdater" (no leading backslash)
    IReadOnlyList<string> Commands,      // the Exec actions' commands
    bool BootOrLogon,                    // has a boot or logon trigger
    string? TaskId);                     // GUID from TaskCache\Tree\<path>\Id, null when it could not be read

public sealed record StartupResult(
    IReadOnlyList<string> FilePatterns,
    IReadOnlyList<string> RegistryPatterns,
    IReadOnlyList<string> Items,         // what will be captured, for the log, the manifest and the Configure list
    IReadOnlyList<string> Notes);        // what was found but is not captured, and why

public static class StartupRules
{
    private const string Run64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string UserRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    internal const string TaskCache = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    private static readonly string[] TaskStateKeys = { "Boot", "Logon", "Maintenance", "Plain" };

    // All-users Run values and boot/logon tasks that start something inside the app's folder. Per-user entries are
    // reported only: the capturing account does not exist after Remove everything.
    public static StartupResult Derive(string folder, IRegistryReader reg, IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<ShortcutInfo> userStartupShortcuts, string systemDrive)
    {
        var files = new List<string>();
        var regs = new List<string>();
        var items = new List<string>();
        var notes = new List<string>();
        var sd = WinPath.Clean(systemDrive);

        // 1. All-users Run values (64-bit and 32-bit view), plus the StartupApproved value that records whether the
        //    entry was switched off in Task Manager.
        foreach (var root in new[] { Run64, Run32 })
        {
            foreach (var name in reg.ValueNames(RegHive.Hklm, root))
            {
                if (name.Length == 0) continue;
                var data = AppDiscovery.Str(reg.GetValue(RegHive.Hklm, root, name));
                if (!AppFootprint.Mentions(data, folder)) continue;
                if (!WinPath.PatternSafe(name))
                {
                    notes.Add("Starts with Windows (Run value \"" + name + "\") is not captured: the value name has characters a rule cannot express.");
                    continue;
                }
                regs.Add(@"HKLM\" + root + " [" + name + "]");
                items.Add("Run entry: " + name);
                foreach (var sub in new[] { "Run", "Run32" })
                {
                    var approvedKey = Approved + "\\" + sub;
                    if (reg.ValueNames(RegHive.Hklm, approvedKey).Contains(name, StringComparer.OrdinalIgnoreCase))
                        regs.Add(@"HKLM\" + approvedKey + " [" + name + "]");
                }
            }
        }

        // 2. Per-user entries: found, not captured.
        foreach (var name in reg.ValueNames(RegHive.Hkcu, UserRun))
        {
            var data = AppDiscovery.Str(reg.GetValue(RegHive.Hkcu, UserRun, name));
            if (AppFootprint.Mentions(data, folder))
                notes.Add("Starts with Windows for the current user only (Run value \"" + name + "\"). Per-user entries are not captured: that account will not exist after a reset.");
        }
        foreach (var s in userStartupShortcuts)
            if (s.Target is not null && AppFootprint.Mentions(s.Target, folder))
                notes.Add("Starts with Windows for the current user only (Startup folder shortcut \"" + s.FileName + "\"). Per-user entries are not captured.");

        // 3. Boot and logon tasks whose action runs something inside the folder.
        foreach (var t in tasks)
        {
            if (!t.Commands.Any(c => AppFootprint.Mentions(c, folder))) continue;
            var rel = t.RelPath.Trim('\\');
            if (rel.Length == 0) continue;

            if (!t.BootOrLogon)
            {
                notes.Add("Scheduled task \\" + rel + " runs this app but has no boot or logon trigger, so it is not captured unless the wider search option is on.");
                continue;
            }
            AddTask(t, rel, reg, sd, files, regs, items, notes, "Scheduled task: \\");
        }

        return new StartupResult(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            regs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            items, notes);
    }

    // The task definition file and the cache entries Task Scheduler keeps for it. False (with a note) when a rule cannot express it.
    private static bool AddTask(TaskInfo t, string rel, IRegistryReader reg, string sd, List<string> files, List<string> regs,
        List<string> items, List<string> notes, string label)
    {
        if (!WinPath.PatternSafe(rel))
        {
            notes.Add("Scheduled task \\" + rel + " is not captured: its name has characters a rule cannot express.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(t.TaskId) || !WinPath.PatternSafe(t.TaskId))
        {
            notes.Add("Scheduled task \\" + rel + " is not captured: Task Scheduler's cache entry for it could not be read.");
            return false;
        }

        var slash = rel.LastIndexOf('\\');
        var dir = slash < 0 ? "" : rel[..slash];
        var leaf = slash < 0 ? rel : rel[(slash + 1)..];
        files.Add(sd + @"\Windows\System32\Tasks" + (dir.Length > 0 ? "\\" + dir : "") + " [" + leaf + "]");

        AddTree(regs, reg, TaskCache + @"\Tree\" + rel);
        AddTree(regs, reg, TaskCache + @"\Tasks\" + t.TaskId);
        foreach (var state in TaskStateKeys)
        {
            var k = TaskCache + "\\" + state + "\\" + t.TaskId;
            if (reg.KeyExists(RegHive.Hklm, k)) AddTree(regs, reg, k);
        }
        items.Add(label + rel);
        return true;
    }

    // 3.20: tasks that start something inside one of the roots but have no boot or logon trigger (daily, time, event, idle...).
    // Carried exactly like the boot/logon tasks, only when the wider search option is on.
    public static StartupResult DeriveOtherTasks(IReadOnlyList<string> roots, IRegistryReader reg, IReadOnlyList<TaskInfo> tasks, string systemDrive)
    {
        var files = new List<string>();
        var regs = new List<string>();
        var items = new List<string>();
        var notes = new List<string>();
        var sd = WinPath.Clean(systemDrive);
        foreach (var t in tasks)
        {
            if (t.BootOrLogon || !t.Commands.Any(c => roots.Any(r => AppFootprint.Mentions(c, r)))) continue;
            var rel = t.RelPath.Trim('\\');
            if (rel.Length == 0) continue;
            AddTask(t, rel, reg, sd, files, regs, items, notes, "Scheduled task (other trigger): \\");
        }
        return new StartupResult(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            regs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            items, notes);
    }

    private static void AddTree(List<string> into, IRegistryReader reg, string rel)
    {
        into.Add(@"HKLM\" + rel + " [*]");
        if (reg.SubKeyNames(RegHive.Hklm, rel).Count > 0) into.Add(@"HKLM\" + rel + @"\* [*]");
    }
}

public static class TaskScanner
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // Reads every task definition under Windows\System32\Tasks. The top-level Microsoft folder holds Windows' own
    // tasks (hundreds of them) and is skipped; Microsoft apps are off by default anyway. A file that cannot be read
    // or parsed is skipped, never fatal.
    public static IReadOnlyList<TaskInfo> Scan(string tasksRoot, IRegistryReader reg, CancellationToken ct)
    {
        var list = new List<TaskInfo>();
        try
        {
            if (!Directory.Exists(tasksRoot)) return list;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var root = tasksRoot.TrimEnd('\\', '/');
            foreach (var f in Directory.EnumerateFiles(tasksRoot, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rel = f[root.Length..].TrimStart('\\', '/').Replace('/', '\\');
                    if (rel.StartsWith(@"Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;

                    var (commands, bootOrLogon) = ParseTaskXml(File.ReadAllText(f));
                    if (commands.Count == 0) continue;

                    var id = AppDiscovery.Str(reg.GetValue(RegHive.Hklm, StartupRules.TaskCache + @"\Tree\" + rel, "Id")).Trim();
                    list.Add(new TaskInfo(rel, commands, bootOrLogon, id.Length == 0 ? null : id));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* one unreadable task must not hide the rest */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* a partial list is fine */ }
        return list;
    }

    // Commands of the Exec actions and whether any trigger is a boot or logon trigger. Matches on element names only,
    // so it does not depend on the task schema version.
    public static (IReadOnlyList<string> Commands, bool BootOrLogon) ParseTaskXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var commands = doc.Descendants()
            .Where(e => e.Name.LocalName == "Exec")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "Command")?.Value.Trim() ?? "")
            .Where(c => c.Length > 0)
            .ToList();
        var bootOrLogon = doc.Descendants().Any(e => e.Name.LocalName is "BootTrigger" or "LogonTrigger");
        return (commands, bootOrLogon);
    }
}