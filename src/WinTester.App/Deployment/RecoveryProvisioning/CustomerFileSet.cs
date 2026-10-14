using System.IO;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// 5.5: customer files and folders chosen by the operator, carried in the package and put back at a fixed place after Reset.
// This is the explicit exception to "user files are not captured" (C14).
//
// Meaning of one item: Source is a file or folder on this PC. Destination is the FOLDER it is put in; the item keeps its own name
// there (file C:\Data\a.txt + destination C:\Acme\In -> C:\Acme\In\a.txt; folder C:\Data\Docs -> C:\Acme\In\Docs\...). FinalPath is
// that result. Bytes and Files are measured when the item is added (0 until then) and drive the free-space gate.
//
// How it reaches the package (approach A, the one that is known to work: same mechanism as the Public Desktop shortcuts, 3.9): the
// source is COPIED to FinalPath on the live machine when the capture starts and FinalPath is added to the package by an include rule.
// The copies stay where they are afterwards (not rolled back if the capture fails or is stopped). Approach B (a USMT locationModify
// on the source, nothing copied) was never probed; see the tracker, 5.5.
public sealed record CustomerFileItem(string Source, string Destination, bool IsFolder, long Bytes = 0, int Files = 0)
{
    public string Leaf => CustomerFileSet.Leaf(Source);
    public string FinalPath => CustomerFileSet.FinalPath(Source, Destination);
}

// One item as it was carried (written to the manifest, shown on the Detect step).
public sealed record CustomerFileEntry(string Source, string Destination, string FinalPath, string Kind, int Files, long Bytes);

public sealed record CustomerCopyResult(bool Success, string Message, IReadOnlyList<CustomerFileEntry> Entries, IReadOnlyList<string> Notes)
{
    // Include patterns for copies saved under a new name by "keep both" for single-file items (folder items are covered by their
    // <folder>\* [*] rule). Passed to RulesXmlBuilder.Build.
    public IReadOnlyList<string> ExtraPatterns { get; init; } = Array.Empty<string>();
    public int Replaced { get; init; }      // existing files overwritten with the new file
    public int Kept { get; init; }          // existing files kept (the package then carries the existing version)
    public int Renamed { get; init; }       // new files saved next to the existing one under a new name
    public int Unchanged { get; init; }     // existing file identical to the new one (same size and time): nothing done
}

// 5.5, name clashes. What to do when a file about to be copied already exists at the destination and differs.
// The package always carries whatever is at the destination after this step (that is what the include rule points at), so
// "keep existing" means the package holds the existing version, not the source one.
public enum CustomerConflictPolicy
{
    Replace,          // the new file overwrites the existing one (the behaviour before this was added; the default)
    ReplaceIfNewer,   // overwrite only when the source file is newer, otherwise keep the existing file
    KeepBoth,         // keep the existing file and save the new one beside it as "name (2).ext"
    KeepExisting,     // never overwrite: keep what is already there
    Choose            // file by file (decisions come from CustomerConflictDecision.Choices; a file without one follows ReplaceIfNewer)
}

public enum ConflictChoice { UseNew, KeepExisting, KeepBoth }

// One file that exists at the destination and differs from the source file.
public sealed record CustomerConflict(string Source, string Target, long SourceBytes, DateTime SourceUtc, long TargetBytes, DateTime TargetUtc)
{
    public bool SourceNewer => SourceUtc > TargetUtc.AddSeconds(CustomerFileSet.TimeToleranceSeconds);
    public bool TargetNewer => TargetUtc > SourceUtc.AddSeconds(CustomerFileSet.TimeToleranceSeconds);
}

// Result of looking at the destinations before copying. Conflicts holds at most maxListed entries; TotalConflicts is the real count.
// Blocked: places where a folder is in the way of a file (or the reverse); nothing can resolve those but the operator.
public sealed record CustomerConflictScan(IReadOnlyList<CustomerConflict> Conflicts, int TotalConflicts, IReadOnlyList<string> Blocked,
    int Identical, int NewFiles);

public sealed record CustomerConflictDecision(CustomerConflictPolicy Policy, IReadOnlyDictionary<string, ConflictChoice> Choices);

public static class CustomerFileSet
{
    public const int MaxItems = 50;

    // Places no customer file may be read from or written to: system files, the package's own folder, USMT's work folder.
    private static readonly string[] Forbidden =
    {
        @"\Windows", @"\Recovery", @"\System Volume Information", @"\$Recycle.Bin", @"\" + RunWorkspace.RootName
    };

    private static string Drive(string? systemDrive)
    {
        var d = (systemDrive ?? "").Trim().TrimEnd('\\', '/');
        return d.Length == 0 ? "C:" : d;
    }

    public static string Leaf(string source)
    {
        var c = WinPath.Clean(source);
        var i = c.LastIndexOf('\\');
        return i < 0 ? c : c[(i + 1)..];
    }

    // Backslash form, as written into rules and the manifest. Destination C:\ gives C:\<leaf>.
    public static string FinalPath(string source, string destination)
    {
        var d = WinPath.Clean(destination).TrimEnd('\\');
        return d + "\\" + Leaf(source);
    }

    // ---- validation

    // Null when the source is usable.
    public static string? ValidateSource(string raw, string systemDrive, Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        var p = WinPath.Clean(raw);
        if (p.Length == 0) return "Choose a file or folder.";
        if (!WinPath.IsRooted(p)) return @"Use a full path such as C:\Data\Customer.";
        if (!WinPath.PatternSafe(p))
            return @"The path has a character that cannot be used in a rule: [ ] , ^ % ? * < > "" |";
        var sd = Drive(systemDrive);
        if (!p.StartsWith(sd + "\\", StringComparison.OrdinalIgnoreCase)) return $"Only files and folders on {sd} can be added.";
        if (p.Length <= 3) return "A whole drive cannot be added.";
        foreach (var f in Forbidden)
            if (WinPath.SameOrUnder(p, sd + f)) return $"Files inside {sd}{f} cannot be added.";
        if (!fileExists(p) && !dirExists(p)) return "The file or folder does not exist.";
        return null;
    }

    // Null when the destination folder is usable. S144: the "parent must exist on a clean Windows" refusal is gone. Evidence: Reset
    // recreated C:\Program Files\Google\Chrome\... (Google does not exist on a clean Windows), so the restore creates the folders
    // of the files it stores. Not yet shown for customer files at depth: 4.26 (e). createdByPackage is no longer used here; the
    // parameter stays so existing callers and checks keep compiling.
    public static string? ValidateDestination(string raw, string systemDrive, IEnumerable<string>? createdByPackage = null)
    {
        var d = WinPath.Clean(raw);
        if (d.Length == 0) return "Choose the destination folder.";
        if (!WinPath.IsRooted(d)) return @"Use a full path such as C:\Acme\Data.";
        if (!WinPath.PatternSafe(d))
            return @"The path has a character that cannot be used in a rule: [ ] , ^ % ? * < > "" |";
        var sd = Drive(systemDrive);
        if (!d.StartsWith(sd + "\\", StringComparison.OrdinalIgnoreCase)) return $"The destination must be on {sd}.";
        foreach (var f in Forbidden)
            if (WinPath.SameOrUnder(d, sd + f)) return $"Nothing can be put inside {sd}{f}.";

        // A user profile does not exist after Remove everything (same reasoning as the desktop shortcuts, 3.9, C38).
        if (WinPath.SameOrUnder(d, sd + @"\Users")
            && !WinPath.SameOrUnder(d, sd + @"\Users\Public") && !WinPath.SameOrUnder(d, sd + @"\Users\Default"))
            return @"A user profile folder will not exist after Remove everything. Use Users\Public, ProgramData or a folder of your own on " + sd + ".";

        return null;   // incl. the drive root and any depth: the restore creates missing folders (see above)
    }

    // One message (or null) per item, same order as `items`.
    public static string?[] ValidateEach(IReadOnlyList<CustomerFileItem> items, string systemDrive,
        Func<string, bool> fileExists, Func<string, bool> dirExists, IEnumerable<string>? appFolders = null)
    {
        var result = new string?[items.Count];
        var finals = items.Select(i => i.FinalPath).ToList();
        var apps = (appFolders ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        for (var n = 0; n < items.Count; n++)
        {
            var it = items[n];
            var msg = ValidateSource(it.Source, systemDrive, fileExists, dirExists);
            if (msg is null)
            {
                var src = WinPath.Clean(it.Source);
                if (it.IsFolder && !dirExists(src)) msg = "This was added as a folder, but it is not a folder any more.";
                else if (!it.IsFolder && !fileExists(src)) msg = "This was added as a file, but it is not a file any more.";
            }
            if (msg is null)
            {
                var created = finals.Where((_, k) => k != n).Concat(apps);
                msg = ValidateDestination(it.Destination, systemDrive, created);
            }
            if (msg is null)
            {
                var src = WinPath.Clean(it.Source);
                var fin = it.FinalPath;
                if (it.IsFolder && !fin.Equals(src, StringComparison.OrdinalIgnoreCase) && WinPath.SameOrUnder(fin, src))
                    msg = "The destination is inside the folder being added.";
                else if (finals.Where((f, k) => k != n && f.Equals(fin, StringComparison.OrdinalIgnoreCase)).Any())
                    msg = "Another item already ends up at " + fin + ".";
            }
            result[n] = msg;
        }
        return result;
    }

    // All problems as "<source>: <message>" lines; empty = all items usable.
    public static IReadOnlyList<string> ValidateAll(IReadOnlyList<CustomerFileItem> items, string systemDrive,
        Func<string, bool> fileExists, Func<string, bool> dirExists, IEnumerable<string>? appFolders = null)
    {
        var problems = new List<string>();
        if (items.Count > MaxItems) problems.Add($"At most {MaxItems} customer files or folders can be added.");
        var each = ValidateEach(items, systemDrive, fileExists, dirExists, appFolders);
        for (var n = 0; n < items.Count; n++)
            if (each[n] is not null) problems.Add($"{items[n].Source}: {each[n]}");
        return problems;
    }

    // ---- rules and space

    // Include patterns for the package: the final path, not the source (the copy is what is captured).
    public static IReadOnlyList<string> IncludePatterns(IEnumerable<CustomerFileItem> items, string systemDrive)
    {
        var list = new List<string>();
        foreach (var it in items)
        {
            var fin = it.FinalPath;
            if (it.IsFolder) list.Add(fin + @"\* [*]");
            else
            {
                var dir = WinPath.Parent(fin);
                if (dir is not null) list.Add(dir + " [" + it.Leaf + "]");
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Free space wanted on the system drive: the usual floor, plus the data twice (the copy at the destination stays on this PC, and
    // the package holds the data again).
    public static long RequiredFreeBytes(IEnumerable<CustomerFileItem> items) =>
        DeploymentSnapshot.MinFreeBytes + 2 * items.Sum(i => i.Bytes);

    // ---- measuring and copying (real paths, so the Linux harness can use temp folders)

    // File count and byte total of a file or folder. Links are not followed. Stops after maxSeconds and says so (Complete = false).
    public static (int Files, long Bytes, bool Complete) Measure(string path, bool isFolder, CancellationToken ct, double maxSeconds = 30)
    {
        try
        {
            if (!isFolder)
                return File.Exists(path) ? (1, new FileInfo(path).Length, true) : (0, 0L, true);

            var started = DateTime.UtcNow;
            var files = 0;
            long bytes = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if ((DateTime.UtcNow - started).TotalSeconds > maxSeconds) return (files, bytes, false);
                var dir = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            files++;
                            bytes += fi.Length;
                        }
                        catch { }
                    }
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d); }
                        catch { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return (files, bytes, true);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (0, 0L, false); }
    }

    // ---- name clashes

    // Times closer than this count as the same (FAT keeps 2-second time stamps).
    public const int TimeToleranceSeconds = 2;

    public static string PolicyText(CustomerConflictPolicy p) => p switch
    {
        CustomerConflictPolicy.ReplaceIfNewer => "replace only when the source file is newer",
        CustomerConflictPolicy.KeepBoth => "keep both (the new copy gets a name like \"file (2).txt\")",
        CustomerConflictPolicy.KeepExisting => "do not replace (keep the files already there)",
        CustomerConflictPolicy.Choose => "chosen file by file",
        _ => "replace with the new files"
    };

    private static bool SameFile(FileInfo s, FileInfo t) =>
        s.Length == t.Length && Math.Abs((s.LastWriteTimeUtc - t.LastWriteTimeUtc).TotalSeconds) <= TimeToleranceSeconds;

    private static bool SourceIsNewer(FileInfo s, FileInfo t) => s.LastWriteTimeUtc > t.LastWriteTimeUtc.AddSeconds(TimeToleranceSeconds);

    // "name (2).ext", "name (3).ext", ... the first one that does not exist (as a file or a folder).
    public static string UniqueName(string target)
    {
        var dir = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var n = 2; n < 10000; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("No free name for the extra copy of " + target);
    }

    // Every file the item would copy, with the path it would be copied to. Links are skipped, as in Measure and Copy.
    private static IEnumerable<(string Src, string Dst)> Pairs(string source, bool isFolder, string target, CancellationToken ct)
    {
        if (!isFolder)
        {
            yield return (source, target);
            yield break;
        }
        var stack = new Stack<(string Dir, string Dst)>();
        stack.Push((source, target));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, dst) = stack.Pop();
            string[] fs, ds;
            try { fs = Directory.GetFiles(dir); ds = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in fs)
            {
                try { if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                yield return (f, Path.Combine(dst, Path.GetFileName(f)));
            }
            foreach (var d in ds)
            {
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                stack.Push((d, Path.Combine(dst, Path.GetFileName(d))));
            }
        }
    }

    // Looks at the destinations without changing anything: which files already exist there and differ (the clashes), which are
    // identical (same size and time, never a clash), which are new. Files that would be copied onto themselves (include in place)
    // are ignored.
    public static CustomerConflictScan FindConflicts(IReadOnlyList<CustomerFileItem> items, CancellationToken ct, int maxListed = 1000)
    {
        var listed = new List<CustomerConflict>();
        var blocked = new List<string>();
        var total = 0;
        var identical = 0;
        var fresh = 0;

        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(it.Destination, CopyLeaf(it.Source));
            try
            {
                if (string.Equals(Path.GetFullPath(it.Source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            if (it.IsFolder && File.Exists(target)) { blocked.Add($"{target} is a file, a folder is needed there ({it.Source})"); continue; }

            foreach (var (src, dst) in Pairs(it.Source, it.IsFolder, target, ct))
            {
                if (Directory.Exists(dst)) { blocked.Add($"{dst} is a folder, a file is needed there"); continue; }
                if (!File.Exists(dst)) { fresh++; continue; }
                FileInfo s, t;
                try { s = new FileInfo(src); t = new FileInfo(dst); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (SameFile(s, t)) { identical++; continue; }
                total++;
                if (listed.Count < maxListed)
                    listed.Add(new CustomerConflict(src, dst, s.Length, s.LastWriteTimeUtc, t.Length, t.LastWriteTimeUtc));
            }
        }
        return new CustomerConflictScan(listed, total, blocked, identical, fresh);
    }

    // ---- copying (real paths, so the Linux harness can use temp folders)

    private static string CopyLeaf(string source) =>
        Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private sealed class CopyContext
    {
        public CustomerConflictPolicy Policy;
        public IReadOnlyDictionary<string, ConflictChoice>? Choices;
        public CancellationToken Ct;
        public Action<string> Log = _ => { };
        public int Logged;                                 // per-file lines written so far (capped)
        public readonly List<string> Notes = new();
        public readonly List<string> Failures = new();
        public readonly List<string> Extra = new();
        public bool FileItem;
        public int Files;                                  // per item: files at the destination that the package will carry
        public long Bytes;
        public int Replaced, Kept, Renamed, Unchanged;     // totals over all items
    }

    private static void LogFile(CopyContext cx, string line)
    {
        if (cx.Logged < 25) cx.Log("  " + line);
        else if (cx.Logged == 25) cx.Log("  (more files follow the same rule; only the first 25 are listed)");
        cx.Logged++;
    }

    // Copies every item to <Destination>\<leaf>. A file that already exists there and differs is handled by `policy`
    // (see CustomerConflictPolicy); links are skipped and reported; read-only files that are replaced are made writable first.
    // Any file that cannot be copied makes the whole run fail (the capture then does not start): a package that silently lacks
    // customer data is worse than no package. Throws OperationCanceledException when stopped.
    public static CustomerCopyResult Copy(IReadOnlyList<CustomerFileItem> items, Action<string> log, CancellationToken ct,
        CustomerConflictPolicy policy = CustomerConflictPolicy.Replace, IReadOnlyDictionary<string, ConflictChoice>? choices = null)
    {
        var entries = new List<CustomerFileEntry>();
        var cx = new CopyContext { Policy = policy, Choices = choices, Ct = ct, Log = log };

        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(it.Destination, CopyLeaf(it.Source));
            var kind = it.IsFolder ? "folder" : "file";
            cx.FileItem = !it.IsFolder;
            cx.Files = 0;
            cx.Bytes = 0;

            try
            {
                var fullSrc = Path.GetFullPath(it.Source);
                var fullDst = Path.GetFullPath(target);

                if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
                {
                    var m = Measure(it.Source, it.IsFolder, ct);
                    cx.Files = m.Files; cx.Bytes = m.Bytes;
                    log($"  {it.Source}: already at its destination, nothing to copy ({cx.Files} file(s))");
                }
                else if (it.IsFolder)
                {
                    var sep = Path.DirectorySeparatorChar.ToString();
                    if (fullDst.StartsWith(fullSrc.TrimEnd(Path.DirectorySeparatorChar) + sep, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("the destination is inside the folder being copied");
                    if (Directory.Exists(target))
                        cx.Notes.Add($"{target} already exists: other files in it that are not part of this item are included in the package too.");
                    CopyFolder(it.Source, target, cx);
                    log($"  {it.Source} -> {target}: {cx.Files} file(s), {RecoveryFormat.Bytes(cx.Bytes)}");
                }
                else
                {
                    Directory.CreateDirectory(it.Destination);
                    CopyFile(it.Source, target, cx);
                    log($"  {it.Source} -> {target}: {RecoveryFormat.Bytes(cx.Bytes)}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cx.Failures.Add($"{it.Source}: {ex.Message}");
            }

            entries.Add(new CustomerFileEntry(it.Source, it.Destination, it.FinalPath, kind, cx.Files, cx.Bytes));
        }

        if (cx.Replaced + cx.Kept + cx.Renamed + cx.Unchanged > 0)
            log($"  Files that already existed at the destinations ({PolicyText(policy)}): {cx.Replaced} replaced, {cx.Kept} kept, " +
                $"{cx.Renamed} saved as extra copies, {cx.Unchanged} identical.");
        if (cx.Kept > 0)
            cx.Notes.Add($"{cx.Kept} existing file(s) at the destinations were kept, so the package carries those versions, not the source files.");

        if (cx.Failures.Count > 0)
        {
            var shown = string.Join(" ", cx.Failures.Take(3));
            return new CustomerCopyResult(false,
                $"{cx.Failures.Count} customer file problem(s), the capture was not started: {shown}" + (cx.Failures.Count > 3 ? " ..." : ""),
                entries, cx.Notes)
            { ExtraPatterns = cx.Extra, Replaced = cx.Replaced, Kept = cx.Kept, Renamed = cx.Renamed, Unchanged = cx.Unchanged };
        }
        return new CustomerCopyResult(true, $"Copied {entries.Sum(e => e.Files)} file(s) for {entries.Count} item(s).", entries, cx.Notes)
        { ExtraPatterns = cx.Extra, Replaced = cx.Replaced, Kept = cx.Kept, Renamed = cx.Renamed, Unchanged = cx.Unchanged };
    }

    private static void CopyOne(string src, string dst)
    {
        if (File.Exists(dst))
        {
            var a = File.GetAttributes(dst);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, a & ~FileAttributes.ReadOnly);
        }
        File.Copy(src, dst, true);
    }

    private static ConflictChoice Decide(string target, FileInfo s, FileInfo t, CopyContext cx)
    {
        var byDate = SourceIsNewer(s, t) ? ConflictChoice.UseNew : ConflictChoice.KeepExisting;
        switch (cx.Policy)
        {
            case CustomerConflictPolicy.ReplaceIfNewer: return byDate;
            case CustomerConflictPolicy.KeepBoth: return ConflictChoice.KeepBoth;
            case CustomerConflictPolicy.KeepExisting: return ConflictChoice.KeepExisting;
            case CustomerConflictPolicy.Choose:
                return cx.Choices is not null && cx.Choices.TryGetValue(target, out var c) ? c : byDate;
            default: return ConflictChoice.UseNew;
        }
    }

    // One file: new -> copy; same file -> nothing; otherwise the policy decides. Counts what ends up at the destination.
    private static void CopyFile(string src, string target, CopyContext cx)
    {
        cx.Ct.ThrowIfCancellationRequested();
        if (Directory.Exists(target)) throw new IOException($"{target} is a folder, a file is needed there");

        if (!File.Exists(target))
        {
            CopyOne(src, target);
            cx.Files++;
            cx.Bytes += new FileInfo(target).Length;
            return;
        }

        var s = new FileInfo(src);
        var t = new FileInfo(target);

        // Same size and time: nothing to decide. Not for "replace all", which means exactly that.
        if (cx.Policy != CustomerConflictPolicy.Replace && SameFile(s, t))
        {
            cx.Unchanged++;
            cx.Files++;
            cx.Bytes += t.Length;
            return;
        }

        switch (Decide(target, s, t, cx))
        {
            case ConflictChoice.UseNew:
                CopyOne(src, target);
                cx.Replaced++;
                cx.Files++;
                cx.Bytes += new FileInfo(target).Length;
                LogFile(cx, $"replaced {target}");
                break;

            case ConflictChoice.KeepExisting:
                cx.Kept++;
                cx.Files++;
                cx.Bytes += t.Length;
                LogFile(cx, $"kept the existing {target}");
                break;

            default:
                var alt = UniqueName(target);
                CopyOne(src, alt);
                cx.Renamed++;
                cx.Files += 2;                                  // the existing file stays and is carried too
                cx.Bytes += t.Length + new FileInfo(alt).Length;
                if (cx.FileItem)
                {
                    var altDir = Path.GetDirectoryName(alt);
                    if (altDir is not null) cx.Extra.Add(WinPath.Clean(altDir) + " [" + Path.GetFileName(alt) + "]");
                }
                LogFile(cx, $"kept {target} and saved the new file as {alt}");
                break;
        }
    }

    private static void CopyFolder(string src, string dst, CopyContext cx)
    {
        try { Directory.CreateDirectory(dst); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cx.Failures.Add($"{dst}: {ex.Message}");
            return;
        }

        foreach (var f in Directory.EnumerateFiles(src))
        {
            cx.Ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) { cx.Notes.Add("Skipped a link: " + f); continue; }
                CopyFile(f, Path.Combine(dst, Path.GetFileName(f)), cx);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cx.Failures.Add($"{f}: {ex.Message}");
            }
        }
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            cx.Ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { cx.Notes.Add("Skipped a link: " + d); continue; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cx.Failures.Add($"{d}: {ex.Message}"); continue; }
            CopyFolder(d, Path.Combine(dst, Path.GetFileName(d)), cx);
        }
    }
}
