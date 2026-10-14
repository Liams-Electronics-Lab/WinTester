using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 5.5: customer files (CustomerFileSet, rules, guard, manifest). Validation uses a fake file system and C:\ paths; the copy and
// measure checks use real temp folders.
public static class CustomerFileChecks
{
    public static void Run(Action<bool, string> check)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Data\price list.xlsx", @"C:\Users\amy\Documents\a.docx" };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Data", @"C:\Data\Docs", @"C:\Users\amy\Documents", @"C:\Windows\Temp", @"C:\Recovery", @"C:\Users\Public\Documents\Big"
        };
        bool F(string p) => files.Contains(p);
        bool D(string p) => dirs.Contains(p);

        // ---- final path
        check(CustomerFileSet.FinalPath(@"C:\Data\Docs", @"C:\Acme\In") == @"C:\Acme\In\Docs", "customer: final path is destination + own name");
        check(CustomerFileSet.FinalPath(@"C:\Data\Docs", @"C:\Acme\In\") == @"C:\Acme\In\Docs", "customer: a trailing slash on the destination is ignored");
        check(CustomerFileSet.FinalPath(@"C:\Data\Docs", @"C:\") == @"C:\Docs", "customer: the drive root as destination gives C:\\<name>");

        // ---- source
        check(CustomerFileSet.ValidateSource(@"C:\Data\Docs", "C:", F, D) is null, "customer: an existing folder is a valid source");
        check(CustomerFileSet.ValidateSource(@"C:\Data\price list.xlsx", "C:", F, D) is null, "customer: an existing file (with a space) is a valid source");
        check(CustomerFileSet.ValidateSource(@"C:\Data\nope", "C:", F, D)?.Contains("does not exist") == true, "customer: a missing source is refused");
        check(CustomerFileSet.ValidateSource(@"D:\Data", "C:", F, D) is not null, "customer: another drive is refused");
        check(CustomerFileSet.ValidateSource(@"Data\Docs", "C:", F, D) is not null, "customer: a relative path is refused");
        check(CustomerFileSet.ValidateSource(@"C:\Data\[x]", "C:", F, D) is not null, "customer: a pattern character in the path is refused");
        check(CustomerFileSet.ValidateSource(@"C:\Windows\Temp", "C:", F, D) is not null && CustomerFileSet.ValidateSource(@"C:\Recovery", "C:", F, D) is not null,
            "customer: Windows and Recovery cannot be a source");
        check(CustomerFileSet.ValidateSource(@"C:\", "C:", F, D) is not null, "customer: a whole drive is refused");

        // ---- destination
        check(CustomerFileSet.ValidateDestination(@"C:\", "C:") is null, "customer: the drive root is a valid destination");
        check(CustomerFileSet.ValidateDestination(@"C:\Acme", "C:") is null, "customer: a new folder directly under the root is valid");
        check(CustomerFileSet.ValidateDestination(@"C:\Users\Public\Documents\Acme", "C:") is null, "customer: a folder inside Public Documents is valid");
        check(CustomerFileSet.ValidateDestination(@"C:\ProgramData\Acme", "C:") is null, "customer: a folder directly under ProgramData is valid");
        check(CustomerFileSet.ValidateDestination(@"C:\Acme\Data", "C:") is null,
            "customer: a deeper new folder whose parent nothing creates is valid (S144: the restore creates missing folders)");
        check(CustomerFileSet.ValidateDestination(@"C:\Acme\Deep\Er\Data", "C:", new[] { @"C:\Acme\In\Docs" }) is null,
            "customer: several missing levels are valid, with or without another item creating one");
        check(CustomerFileSet.ValidateDestination(@"C:\Windows\Acme\Deep", "C:") is not null && CustomerFileSet.ValidateDestination(@"C:\Users\amy\Acme\Deep", "C:") is not null,
            "customer: depth does not unlock Windows or a user profile");
        check(CustomerFileSet.ValidateDestination(@"C:\Users\amy\Documents", "C:")?.Contains("user profile") == true, "customer: a user profile destination is refused");
        check(CustomerFileSet.ValidateDestination(@"C:\Users", "C:") is not null, "customer: the Users folder itself is refused");
        check(CustomerFileSet.ValidateDestination(@"C:\Windows\Acme", "C:") is not null && CustomerFileSet.ValidateDestination(@"C:\Recovery\x", "C:") is not null,
            "customer: Windows and Recovery cannot be a destination");
        check(CustomerFileSet.ValidateDestination(@"D:\Acme", "C:") is not null, "customer: another drive is refused as a destination");
        check(CustomerFileSet.ValidateDestination("", "C:") is not null, "customer: an empty destination is refused");

        // ---- whole items
        var ok = new CustomerFileItem(@"C:\Data\Docs", @"C:\Acme\In", true);
        check(CustomerFileSet.ValidateAll(new[] { ok }, "C:", F, D).Count == 0, "customer: a good item has no problem");
        check(CustomerFileSet.ValidateEach(new[] { new CustomerFileItem(@"C:\Data\Docs", @"C:\Data", true) }, "C:", F, D)[0] is null,
            "customer: destination = the source's own parent means 'include where it is' and is valid");
        var inside = new CustomerFileItem(@"C:\Users\Public\Documents\Big", @"C:\Users\Public\Documents\Big", true);
        check(CustomerFileSet.ValidateEach(new[] { inside }, "C:", F, D)[0]?.Contains("inside the folder") == true, "customer: a destination inside the folder being added is refused");
        var dup = CustomerFileSet.ValidateEach(new[] { ok, new CustomerFileItem(@"C:\Data\Docs", @"C:\Acme\In", true) }, "C:", F, D);
        check(dup[0]?.Contains("already ends up") == true && dup[1]?.Contains("already ends up") == true, "customer: two items with the same final path are both refused");
        check(CustomerFileSet.ValidateEach(new[] { new CustomerFileItem(@"C:\Data\price list.xlsx", @"C:\Acme", true) }, "C:", F, D)[0]?.Contains("not a folder") == true,
            "customer: an item whose kind no longer matches is refused");
        var many = Enumerable.Range(0, 51).Select(n => new CustomerFileItem(@"C:\Data\Docs", @"C:\Acme" + n, true)).ToList();
        check(CustomerFileSet.ValidateAll(many, "C:", F, D).Any(m => m.Contains("At most 50")), "customer: more than 50 items is refused");

        // ---- patterns and space
        var pat = CustomerFileSet.IncludePatterns(new[] { ok, new CustomerFileItem(@"C:\Data\price list.xlsx", @"C:\Acme\In", false) }, "C:");
        check(pat.SequenceEqual(new[] { @"C:\Acme\In\Docs\* [*]", @"C:\Acme\In [price list.xlsx]" }), "customer: folder -> <final>\\* [*], file -> <folder> [<name>] (" + string.Join(" | ", pat) + ")");
        check(CustomerFileSet.IncludePatterns(new[] { ok, ok }, "C:").Count == 1, "customer: duplicate patterns are written once");
        var sized = new[] { new CustomerFileItem(@"C:\a", @"C:\b", true, 1000), new CustomerFileItem(@"C:\c", @"C:\d", false, 500) };
        check(CustomerFileSet.RequiredFreeBytes(sized) == DeploymentSnapshot.MinFreeBytes + 3000, "customer: free space = the minimum plus the data twice");

        // ---- rules, guard
        var o = new CaptureOptions();
        var none = RulesXmlBuilder.Build(o, "C:");
        check(none.CustomerPatterns.Count == 0 && !none.Document.Descendants("displayName").Any(e => e.Value == RulesXmlBuilder.CustomerComponentName), "customer: none listed, no component");
        o.CustomerFiles.Add(ok);
        o.CustomerFiles.Add(new CustomerFileItem(@"C:\Data\price list.xlsx", @"C:\Acme\In", false));
        var on = RulesXmlBuilder.Build(o, "C:");
        var comp = on.Document.Descendants("component").FirstOrDefault(c => c.Element("displayName")?.Value == RulesXmlBuilder.CustomerComponentName);
        check(comp is not null && comp.Descendants("pattern").Select(p => p.Value).SequenceEqual(pat) && comp.Descendants("pattern").All(p => (string?)p.Attribute("type") == "File"),
            "customer: one include component with the File patterns");
        check(on.CustomerPatterns.Count == 2 && on.CustomerPatterns.All(p => on.IncludePatterns.Contains(p)), "customer: the patterns are also in IncludePatterns");
        check(on.Patterns.SequenceEqual(none.Patterns), "customer: the exclusion list is unchanged");
        var settingsOnly = new CaptureOptions { Mode = CaptureMode.SettingsOnly };
        settingsOnly.CustomerFiles.Add(ok);
        check(RulesXmlBuilder.Build(settingsOnly, "C:").CustomerPatterns.Count == 1, "customer: works in settings-only mode");
        var prot = ExclusionGuard.ProtectedFor("C:", o);
        check(prot.Any(p => p.Path == @"C:\Acme\In\Docs" && p.Tree), "customer: the final folder is protected from the Extra paths box");

        // ---- measure and copy (real temp folders)
        var root = Path.Combine(Path.GetTempPath(), "cf-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var src = Path.Combine(root, "src", "Docs");
            Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "aaaa");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "bb");
            File.WriteAllText(Path.Combine(src, "sub", "deep", "c.txt"), "c");
            var m = CustomerFileSet.Measure(src, true, CancellationToken.None);
            check(m.Files == 3 && m.Bytes == 7 && m.Complete, "customer: measure counts files and bytes recursively");
            check(CustomerFileSet.Measure(Path.Combine(src, "a.txt"), false, CancellationToken.None) is (1, 4L, true), "customer: measure of one file");

            var dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(dst, "Docs"));
            var old = Path.Combine(dst, "Docs", "a.txt");
            File.WriteAllText(old, "OLD");
            File.SetAttributes(old, FileAttributes.ReadOnly);
            var one = Path.Combine(root, "src", "price.csv");
            File.WriteAllText(one, "p,q");
            var log = new List<string>();
            var r = CustomerFileSet.Copy(new[]
            {
                new CustomerFileItem(src, dst, true), new CustomerFileItem(one, Path.Combine(dst, "files"), false)
            }, log.Add, CancellationToken.None);
            check(r.Success && r.Entries.Count == 2 && r.Entries[0].Files == 3 && r.Entries[0].Bytes == 7 && r.Entries[1].Files == 1,
                "customer: copy succeeds and reports files and bytes per item (" + r.Message + ")");
            check(File.ReadAllText(Path.Combine(dst, "Docs", "a.txt")) == "aaaa" && File.ReadAllText(Path.Combine(dst, "Docs", "sub", "deep", "c.txt")) == "c"
                  && File.ReadAllText(Path.Combine(dst, "files", "price.csv")) == "p,q", "customer: nested files arrive, a read-only same-named file is overwritten");
            check(r.Notes.Any(n => n.Contains("already exists")), "customer: copying into an existing folder is noted");

            var same = CustomerFileSet.Copy(new[] { new CustomerFileItem(src, Path.GetDirectoryName(src)!, true) }, _ => { }, CancellationToken.None);
            check(same.Success && same.Entries[0].Files == 3 && File.ReadAllText(Path.Combine(src, "a.txt")) == "aaaa", "customer: destination = own parent copies nothing and still counts the files");

            var missing = CustomerFileSet.Copy(new[] { new CustomerFileItem(Path.Combine(root, "gone"), dst, true) }, _ => { }, CancellationToken.None);
            check(!missing.Success && missing.Message.Contains("not started"), "customer: a missing source fails the run");

            var into = CustomerFileSet.Copy(new[] { new CustomerFileItem(src, Path.Combine(src, "sub"), true) }, _ => { }, CancellationToken.None);
            check(!into.Success && !Directory.Exists(Path.Combine(src, "sub", "Docs")), "customer: a destination inside the folder is refused and nothing is written");

            var cts = new CancellationTokenSource(); cts.Cancel();
            var threw = false;
            try { CustomerFileSet.Copy(new[] { new CustomerFileItem(src, Path.Combine(root, "dst2"), true) }, _ => { }, cts.Token); } catch (OperationCanceledException) { threw = true; }
            check(threw && !Directory.Exists(Path.Combine(root, "dst2", "Docs")), "customer: a stopped copy throws and copies nothing");

            // ---- name clashes (the gate): scan and every policy
            var csrc = Path.Combine(root, "cs", "src", "Data");
            var cdst = Path.Combine(root, "cs", "dst");
            var ddir = Path.Combine(cdst, "Data");
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            void W(string path, string text, DateTime stamp)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, stamp);
            }
            void Prep()
            {
                if (Directory.Exists(cdst)) Directory.Delete(cdst, true);
                W(Path.Combine(ddir, "same.txt"), "same", t0);                    // identical to the source file
                W(Path.Combine(ddir, "newer.txt"), "dest-old", t0);               // the source file is newer
                W(Path.Combine(ddir, "older.txt"), "DEST-NEW", t0.AddHours(2));   // the destination file is newer
            }
            W(Path.Combine(csrc, "same.txt"), "same", t0);
            W(Path.Combine(csrc, "newer.txt"), "NEWSRC", t0.AddHours(2));
            W(Path.Combine(csrc, "older.txt"), "OLDSRC", t0);
            W(Path.Combine(csrc, "fresh.txt"), "fresh", t0);
            Prep();
            var citem = new CustomerFileItem(csrc, cdst, true);
            string Dst(string name) => File.ReadAllText(Path.Combine(ddir, name));

            var scan = CustomerFileSet.FindConflicts(new[] { citem }, CancellationToken.None);
            check(scan.TotalConflicts == 2 && scan.Identical == 1 && scan.NewFiles == 1 && scan.Blocked.Count == 0,
                $"clash: scan finds 2 clashes, 1 identical, 1 new ({scan.TotalConflicts}/{scan.Identical}/{scan.NewFiles})");
            var cn = scan.Conflicts.FirstOrDefault(c => c.Target.EndsWith("newer.txt"));
            var co = scan.Conflicts.FirstOrDefault(c => c.Target.EndsWith("older.txt"));
            check(cn is { SourceNewer: true, TargetNewer: false, SourceBytes: 6, TargetBytes: 8 } && co is { SourceNewer: false, TargetNewer: true },
                "clash: the scan says which side is newer and both sizes");
            check(CustomerFileSet.FindConflicts(new[] { citem }, CancellationToken.None, maxListed: 1) is { TotalConflicts: 2, Conflicts.Count: 1 },
                "clash: the listed clashes are capped but the real count is kept");
            check(CustomerFileSet.FindConflicts(new[] { new CustomerFileItem(csrc, Path.GetDirectoryName(csrc)!, true) }, CancellationToken.None).TotalConflicts == 0,
                "clash: an item included in place is ignored by the scan");

            Prep();
            var rRep = CustomerFileSet.Copy(new[] { citem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.Replace);
            check(rRep.Success && Dst("newer.txt") == "NEWSRC" && Dst("older.txt") == "OLDSRC" && Dst("fresh.txt") == "fresh" && rRep.Replaced == 3 && rRep.Kept == 0,
                "clash: replace all overwrites every existing file, newer or not");

            Prep();
            var rNew = CustomerFileSet.Copy(new[] { citem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.ReplaceIfNewer);
            check(rNew.Success && Dst("newer.txt") == "NEWSRC" && Dst("older.txt") == "DEST-NEW" && Dst("fresh.txt") == "fresh"
                  && rNew.Replaced == 1 && rNew.Kept == 1 && rNew.Unchanged == 1 && rNew.Notes.Any(n => n.Contains("kept")),
                "clash: replace only if newer keeps the newer destination file, skips the identical one and says what was kept");

            Prep();
            var rKeep = CustomerFileSet.Copy(new[] { citem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.KeepExisting);
            check(rKeep.Success && Dst("newer.txt") == "dest-old" && Dst("older.txt") == "DEST-NEW" && Dst("fresh.txt") == "fresh"
                  && rKeep.Replaced == 0 && rKeep.Kept == 2, "clash: do not replace keeps every existing file and still copies the new ones");

            Prep();
            var rBoth = CustomerFileSet.Copy(new[] { citem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.KeepBoth);
            check(rBoth.Success && Dst("newer.txt") == "dest-old" && Dst("newer (2).txt") == "NEWSRC" && Dst("older.txt") == "DEST-NEW" && Dst("older (2).txt") == "OLDSRC"
                  && rBoth.Renamed == 2 && !File.Exists(Path.Combine(ddir, "same (2).txt")) && rBoth.ExtraPatterns.Count == 0,
                "clash: keep both saves the new file as 'name (2).ext', leaves identical files alone, needs no extra rule for a folder");

            Prep();
            var picks = new Dictionary<string, ConflictChoice>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(ddir, "newer.txt")] = ConflictChoice.KeepExisting,
                [Path.Combine(ddir, "older.txt")] = ConflictChoice.UseNew
            };
            var rPick = CustomerFileSet.Copy(new[] { citem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.Choose, picks);
            check(rPick.Success && Dst("newer.txt") == "dest-old" && Dst("older.txt") == "OLDSRC", "clash: choosing file by file follows each choice");
            Prep();
            var rNoPick = CustomerFileSet.Copy(new[] { citem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.Choose, new Dictionary<string, ConflictChoice>());
            check(rNoPick.Success && Dst("newer.txt") == "NEWSRC" && Dst("older.txt") == "DEST-NEW", "clash: a file with no choice follows 'newer wins'");

            // keep both for a single file: the renamed copy needs its own include pattern
            var oneSrc = Path.Combine(root, "cs", "src", "one.txt");
            var oneDir = Path.Combine(root, "cs", "dst2");
            W(oneSrc, "ONE", t0.AddHours(1));
            W(Path.Combine(oneDir, "one.txt"), "old", t0);
            var oneItem = new CustomerFileItem(oneSrc, oneDir, false);
            var rOne = CustomerFileSet.Copy(new[] { oneItem }, _ => { }, CancellationToken.None, CustomerConflictPolicy.KeepBoth);
            check(rOne.Success && File.ReadAllText(Path.Combine(oneDir, "one (2).txt")) == "ONE" && File.ReadAllText(Path.Combine(oneDir, "one.txt")) == "old"
                  && rOne.ExtraPatterns.Count == 1 && rOne.ExtraPatterns[0].EndsWith("[one (2).txt]"),
                "clash: keep both on a single file returns an include pattern for the renamed copy (" + string.Join(";", rOne.ExtraPatterns) + ")");
            var oOne = new CaptureOptions();
            oOne.CustomerFiles.Add(oneItem);
            var bOne = RulesXmlBuilder.Build(oOne, "C:", null, false, rOne.ExtraPatterns);
            check(bOne.CustomerPatterns.Contains(rOne.ExtraPatterns[0]) && bOne.IncludePatterns.Contains(rOne.ExtraPatterns[0]),
                "clash: the renamed copy's pattern reaches the rules file");

            // a folder where a file is needed
            var blkSrc = Path.Combine(root, "cs", "src", "Blk");
            W(Path.Combine(blkSrc, "x.txt"), "x", t0);
            var blkDst = Path.Combine(root, "cs", "dst3");
            Directory.CreateDirectory(Path.Combine(blkDst, "Blk", "x.txt"));
            var blkItem = new CustomerFileItem(blkSrc, blkDst, true);
            var blkScan = CustomerFileSet.FindConflicts(new[] { blkItem }, CancellationToken.None);
            var blkCopy = CustomerFileSet.Copy(new[] { blkItem }, _ => { }, CancellationToken.None);
            check(blkScan.Blocked.Count == 1 && !blkCopy.Success && blkCopy.Message.Contains("folder"), "clash: a folder where a file is needed is blocked in the scan and fails the copy");

            // names and text
            var un = Path.Combine(root, "cs", "un");
            W(Path.Combine(un, "a.txt"), "1", t0);
            check(CustomerFileSet.UniqueName(Path.Combine(un, "a.txt")) == Path.Combine(un, "a (2).txt"), "clash: the extra copy is named 'a (2).txt'");
            W(Path.Combine(un, "a (2).txt"), "2", t0);
            check(CustomerFileSet.UniqueName(Path.Combine(un, "a.txt")) == Path.Combine(un, "a (3).txt"), "clash: ... and 'a (3).txt' when that is taken");
            check(Enum.GetValues<CustomerConflictPolicy>().All(x => CustomerFileSet.PolicyText(x).Length > 0), "clash: every policy has a text for the Review step and the log");
            check(new CaptureOptions().CustomerPolicy == CustomerConflictPolicy.ReplaceIfNewer, "clash: the default when nobody was asked is the safe one (replace only if newer)");

            // ---- manifest round trip
            var pk = Path.Combine(root, "man", "usmt.ppkg");
            Directory.CreateDirectory(Path.GetDirectoryName(pk)!);
            File.WriteAllBytes(pk, new byte[10]);
            var withCf = new CaptureOptions(); withCf.CustomerFiles.Add(ok);
            DeploymentManifest.Write(pk, DeploymentManifest.Create(withCf, root, "amd64", pk, false, null, r.Entries));
            var back = DeploymentManifest.TryRead(pk, out _);
            check(back?.CustomerFiles is { Length: 2 } cf && cf[0].FinalPath.EndsWith("Docs") && cf[0].Kind == "folder" && cf[1].Kind == "file", "customer: the manifest carries the copied items and reads back");
            DeploymentManifest.Write(pk, DeploymentManifest.Create(new CaptureOptions(), root, "amd64", pk, false));
            check(DeploymentManifest.TryRead(pk, out _)?.CustomerFiles is null, "customer: no customer files, the manifest field is null");
        }
        finally { try { File.SetAttributes(Path.Combine(root, "dst", "Docs", "a.txt"), FileAttributes.Normal); } catch { } try { Directory.Delete(root, true); } catch { } }
    }
}
