using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using WinTester.App.Deployment.RecoveryProvisioning;

// Checks for the pure Recovery Provisioning logic. Prints one line per failure and a summary.
var failures = 0;
var total = 0;

void Check(bool ok, string name)
{
    total++;
    if (!ok) { failures++; Console.WriteLine("FAIL  " + name); }
}

void Eq(string? actual, string? expected, string name)
{
    total++;
    if (actual != expected) { failures++; Console.WriteLine($"FAIL  {name}\n        expected: {expected ?? "<null>"}\n        actual:   {actual ?? "<null>"}"); }
}

bool ThrowsInvalid(Action a)
{
    try { a(); return false; }
    catch (InvalidOperationException) { return true; }
}

var tempRoot = Path.Combine(Path.GetTempPath(), "rp-check-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempRoot);

try
{
    // ---- App discovery and include rules (3.7/3.8), fake registry
    AppDiscoveryChecks.Run(Check);
    DesktopShortcutChecks.Run(Check);
    StartupChecks.Run(Check);
    NulChecks.Run(Check, tempRoot);
    RuntimeChecks.Run(Check);
    ShellExtChecks.Run(Check);
    MsiChecks.Run(Check);
    ComChecks.Run(Check);
    CustomExclusionLineChecks.Run(Check);
    ExclusionGuardChecks.Run(Check);
    ProfileChecks.Run(Check);
    VendorProfileChecks.Run(Check);
    CustomAppChecks.Run(Check);
    SuggestionChecks.Run(Check);
    SweepChecks.Run(Check);
    RulesNameChecks.Run(Check);
    await PackageRestoreChecks.Run(Check);
    OemRuleChecks.Run(Check);
    CustomerFileChecks.Run(Check);
    LogNoiseChecks.Run(Check, tempRoot);

    // ---- Custom exclusion conversion
    Eq(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\Cache", out _), @"C:\Tools\Cache\* [*]", "folder");
    Eq(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\Cache\", out _), @"C:\Tools\Cache\* [*]", "folder trailing slash");
    Eq(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\app.log", out _), @"C:\Tools [app.log]", "file");
    Eq(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\*.log", out _), @"C:\Tools [*.log]", "file wildcard");
    Eq(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\*", out _), @"C:\Tools\* [*]", "star leaf");
    Eq(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\* [*.tmp]", out _), @"C:\Tools\* [*.tmp]", "already usmt form");
    Eq(RulesXmlBuilder.TryConvertCustom(@"%TEMP%\x", out _), @"%TEMP%\x\* [*]", "env var folder");
    Eq(RulesXmlBuilder.TryConvertCustom(@"%TEMP%", out _), @"%TEMP%\* [*]", "bare env var");
    Check(RulesXmlBuilder.TryConvertCustom(@"Tools\Cache", out var e1) is null && e1 is not null, "relative path rejected");
    Check(RulesXmlBuilder.TryConvertCustom(@"C:\a|b", out var e2) is null && e2 is not null, "bad char rejected");
    Check(RulesXmlBuilder.TryConvertCustom(@"C:\Tools\* [*.tmp", out var e3) is null && e3 is not null, "unclosed bracket rejected");
    Check(RulesXmlBuilder.TryConvertCustom("   ", out var e4) is null && e4 is not null, "blank rejected");
    Check(RulesXmlBuilder.ValidateCustom(new[] { @"C:\ok", "", @"nope" }).Count == 1, "ValidateCustom skips blanks, reports bad");

    // ---- Rules builder
    var all = new CaptureOptions();
    all.CustomExclusions.Add(@"D:\Scratch");
    var built = RulesXmlBuilder.Build(all, "C:");
    Check(built.Patterns.Contains(@"C:\Users\*\AppData\Local\Temp\* [*]"), "temp pattern present");
    Check(!built.Patterns.Any(x => x.Contains(@"\Downloads") || x.Contains("Recycle.Bin")), "no Downloads or Recycle Bin pattern (3.15)");
    Check(built.Patterns.Contains(@"C:\Windows\SoftwareDistribution\Download\* [*]"), "update cache pattern present");
    Check(built.Patterns.Contains(@"C:\* [pagefile.sys]"), "pagefile always excluded");
    Check(built.Patterns.Contains(@"D:\Scratch\* [*]"), "custom pattern present");
    Check(built.Patterns.Count == built.Patterns.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no duplicate patterns");

    var root = built.Document.Root!;
    Eq(root.Name.LocalName, "migration", "root element");
    Eq((string?)root.Attribute("urlid"), RulesXmlBuilder.UrlId, "urlid");
    Check(root.Descendants("unconditionalExclude").Count() == 1, "one unconditionalExclude");
    Check(root.Descendants("pattern").Count() == built.Patterns.Count, "pattern elements match list");
    Check(root.Descendants("pattern").All(p => (string?)p.Attribute("type") == "File"), "all patterns are type File");

    var none = new CaptureOptions
    {
        ExcludeTemp = false, ExcludeBrowserCaches = false, ExcludeUpdateCache = false
    };
    Check(RulesXmlBuilder.Build(none, "C:").Patterns.Count == 6 + GameLibraries.ExclusionPatterns("C:").Count, "only the always-on patterns remain (6 system + the game library folders, 3.14)");
    Check(built.Patterns.Contains(@"C:\ProgramData\Microsoft\User Account Pictures [*.dat]"), "account picture .dat cache always excluded");
    Check(built.Patterns.Contains(@"C:\WTRP\* [*]"), "ScanState work folder always excluded");
    Eq(RulesXmlBuilder.Build(none, "").Patterns[0], @"C:\System Volume Information\* [*]", "empty drive falls back to C:");

    Check(RulesXmlBuilder.ValidateAgainstXsd(built.Document, Path.Combine(tempRoot, "missing.xsd")).Count == 0, "missing xsd is not an error");

    var file = RulesXmlBuilder.WriteToRunFolder(built.Document, out var runFolder);
    Check(File.Exists(file) && Path.GetFileName(file) == "Rules.xml", "Rules.xml written to run folder");
    Check(file.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "run folder is under temp");
    var bytes = File.ReadAllBytes(file);
    Check(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "no UTF-8 BOM");
    Check(XDocument.Load(file).Root?.Name.LocalName == "migration", "written file parses back");
    RulesXmlBuilder.DeleteRunFolder(runFolder);
    Check(!Directory.Exists(runFolder), "run folder deleted");

    // ---- WinRE parse
    const string enabledEn = "Windows Recovery Environment (Windows RE) and system reset configuration\nInformation:\n\n" +
        "    Windows RE status:         Enabled\n" +
        "    Windows RE location:       \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\n" +
        "    Boot Configuration Data (BCD) identifier: 11111111-2222-3333-4444-555555555555\n";
    const string disabledEn = "Windows Recovery Environment (Windows RE) and system reset configuration\nInformation:\n\n" +
        "    Windows RE status:         Disabled\n" +
        "    Windows RE location:       \n" +
        "    Boot Configuration Data (BCD) identifier: 00000000-0000-0000-0000-000000000000\n";
    const string enabledJa = "Information:\n\n    Windows RE status:         有効\n" +
        "    Windows RE location:       \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\n";
    const string disabledJa = "Information:\n\n    Windows RE status:         無効\n    Windows RE location:       \n";

    Check(WinReCheck.Parse(enabledEn).State == WinReState.Enabled, "WinRE enabled (English)");
    Check(WinReCheck.Parse(disabledEn).State == WinReState.Disabled, "WinRE disabled (English)");
    Check(WinReCheck.Parse(enabledJa).State == WinReState.Enabled, "WinRE enabled (Japanese, via location)");
    Check(WinReCheck.Parse(disabledJa).State == WinReState.Unknown, "WinRE disabled (Japanese) is Unknown, not a false Enabled");
    Check(WinReCheck.Parse("").State == WinReState.Unknown, "WinRE empty output is Unknown");

    // ---- USMT locator
    var arm = UsmtLocator.Check(Architecture.Arm64, null, tempRoot);
    Check(!arm.Ok && arm.ArchFolder is null, "Arm64 not supported");
    Check(!UsmtLocator.Check(Architecture.X64, null, tempRoot).Ok, "missing amd64 folder fails");

    var amd = Path.Combine(tempRoot, "amd64");
    Directory.CreateDirectory(amd);
    var partial = UsmtLocator.Check(Architecture.X64, "Config_AppsOnly.xml", tempRoot);
    Check(!partial.Ok && partial.Directory is null && partial.Problems.Contains("scanstate.exe"), "empty folder lists scanstate.exe missing");

    // Support files (C21) are required too, so a folder with only the original set must now name them.
    foreach (var f in new[] { "scanstate.exe", "migcore.dll", "migstore.dll", "unbcl.dll", "wdscore.dll", "MigApp.xml", "Config_AppsOnly.xml" })
        File.WriteAllText(Path.Combine(amd, f), "x");
    var noSupport = UsmtLocator.Check(Architecture.X64, "Config_AppsOnly.xml", tempRoot);
    Check(!noSupport.Ok && noSupport.Problems.Contains("hwcompat.dll") && noSupport.Problems.Contains("upgrade_comp.xml"),
          "missing hwcompat.dll and upgrade_comp.xml are named");
    foreach (var f in UsmtLocator.SupportFiles)
        File.WriteAllText(Path.Combine(amd, f), "x");
    var noManifests = UsmtLocator.Check(Architecture.X64, "Config_AppsOnly.xml", tempRoot);
    Check(!noManifests.Ok && noManifests.Directory is not null
          && noManifests.Problems.SequenceEqual(new[] { @"DLManifests\", @"ReplacementManifests\" }), "manifest folders reported");

    Directory.CreateDirectory(Path.Combine(amd, "DLManifests"));
    Directory.CreateDirectory(Path.Combine(amd, "ReplacementManifests"));
    Check(UsmtLocator.Check(Architecture.X64, "Config_AppsOnly.xml", tempRoot).Ok, "complete folder passes for the chosen config");
    var allCfg = UsmtLocator.Check(Architecture.X64, null, tempRoot);
    Check(!allCfg.Ok && allCfg.Problems.Contains("Config_AppsAndSettings.xml") && allCfg.Problems.Contains("Config_SettingsOnly.xml"),
          "checking all configs reports the two that are absent");

    // ---- Manifest round trip, staleness, detector
    var recovery = Path.Combine(tempRoot, "Recovery", "Customizations");
    Directory.CreateDirectory(recovery);
    var ppkg = Path.Combine(recovery, "usmt.ppkg");
    File.WriteAllText(ppkg, "package");

    Check(DeploymentManifest.TryRead(ppkg, out var missingNote) is null && missingNote is not null, "missing manifest reports a note");

    var opts = new CaptureOptions { Mode = CaptureMode.AppsOnly };
    opts.CustomExclusions.Add(@"D:\Scratch");
    var manifest = DeploymentManifest.Create(opts, amd, "amd64", ppkg, false);
    DeploymentManifest.Write(ppkg, manifest);
    Eq(DeploymentManifest.PathFor(ppkg), Path.Combine(recovery, "usmt.manifest.json"), "manifest sits next to the package");

    var back = DeploymentManifest.TryRead(ppkg, out var note);
    Check(back is not null && note is null, "manifest reads back");
    Eq(back?.ConfigFile, "Config_AppsOnly.xml", "config file round trips");
    Eq(back?.CaptureMode, "AppsOnly", "mode round trips");
    Check(back is not null && back.CustomExclusions.SequenceEqual(new[] { @"D:\Scratch" }), "custom exclusions round trip");
    Check(back is not null && back.PackageSizeBytes == "package".Length, "package size recorded");
    Check(back is not null && back.IncludeDrivers, "manifest records the drivers option (default on)");
    var noDrvManifest = DeploymentManifest.Create(new CaptureOptions { IncludeDrivers = false }, amd, "amd64", ppkg, false);
    Check(!noDrvManifest.IncludeDrivers, "manifest records the drivers option off");
    {
        // A manifest written before this option has no IncludeDrivers property: it must still read, as off.
        var oldJson = System.Text.Json.JsonSerializer.Serialize(manifest);
        var node = System.Text.Json.Nodes.JsonNode.Parse(oldJson)!.AsObject();
        node.Remove("IncludeDrivers");
        File.WriteAllText(DeploymentManifest.PathFor(ppkg), node.ToJsonString());
        var old = DeploymentManifest.TryRead(ppkg, out var oldNote);
        Check(old is not null && oldNote is null && !old.IncludeDrivers, "older manifest without the field reads back as drivers off");
        DeploymentManifest.Write(ppkg, manifest);
    }

    var m = manifest with { OsBuild = "22631.4317" };
    Check(m.IsStaleFor("26100.4061"), "different feature build is stale");
    Check(!m.IsStaleFor("22631.9999"), "same feature build is not stale");
    Check(!m.IsStaleFor(""), "unknown current build is not reported stale");
    Eq(DeploymentManifest.MajorBuild("26100.4061"), "26100", "MajorBuild");

    File.WriteAllText(DeploymentManifest.PathFor(ppkg), "{ not json");
    Check(DeploymentManifest.TryRead(ppkg, out var badNote) is null && badNote is not null, "corrupt manifest returns null with a note");
    DeploymentManifest.Write(ppkg, manifest);

    File.WriteAllText(Path.Combine(recovery, "other.ppkg"), "x");
    File.WriteAllText(Path.Combine(recovery, "vendor.spp"), "x");
    var det = DeploymentDetector.Detect(tempRoot);
    Check(det.Package is not null && det.Package.SizeBytes == "package".Length, "detector finds our package");
    Check(det.Manifest is not null, "detector loads the manifest");
    Check(det.OtherPackages.OrderBy(x => x).SequenceEqual(new[] { "other.ppkg", "vendor.spp" }), "other packages listed, json and ours excluded");
    Check(!det.OemHookPresent && !det.AutoApplyPresent, "no OEM hook / AutoApply in the temp tree");
    Directory.CreateDirectory(Path.Combine(tempRoot, "Recovery", "AutoApply"));
    Check(DeploymentDetector.Detect(tempRoot).AutoApplyPresent, "AutoApply folder detected");

    // ================= Phase 3 =================

    // ---- Progress parsing
    Check(ProgressParser.TryParse("2014/08/11 14:24:23, totalPercentageCompleted, 12", out var pct1) && pct1 == 12, "progress: total line");
    Check(ProgressParser.TryParse("estimatePercentageCompleted=7", out var pct2) && pct2 == 7, "progress: estimate line");
    Check(ProgressParser.TryParse("totalPercentageCompleted 250", out var pct3) && pct3 == 100, "progress: clamped to 100");
    Check(!ProgressParser.TryParse("nothing to see here", out _), "progress: no match");
    Check(ProgressParser.Latest(new[] { "totalPercentageCompleted, 3", "noise", "totalPercentageCompleted, 9" }) == 9, "progress: last value wins");
    Check(ProgressParser.Latest(new[] { "noise" }) is null, "progress: none found");

    // ---- Log analysis
    var scanLog = Path.Combine(tempRoot, "scan.log");
    File.WriteAllLines(scanLog, new[]
    {
        "2017-11-25 02:24:47, Info [0x000000] hello",
        "2017-11-25 02:24:48, Warning [0x0808b0] EnumRegistryKeyValues: Failed to open key",
        "2017-11-25 02:24:49, Error [0x000001] something bad",
        "2017-11-25 02:24:50, Info At least one error was skipped as a result of /c"
    });
    var an = ScanStateLogAnalyzer.Analyze(scanLog);
    Check(an.LogFound && an.ErrorCount == 1 && an.WarningCount == 1 && an.SkippedErrorMarker && an.FirstErrors.Count == 1, "log analysis counts and marker");
    Check(!ScanStateLogAnalyzer.Analyze(Path.Combine(tempRoot, "nope.log")).LogFound, "missing log is reported as not found");
    Check(ScanStateLogAnalyzer.Analyze(null, new[] { "x", ScanStateLogAnalyzer.SkippedMarker }).SkippedErrorMarker, "marker found in console output");

    // ---- Return codes and verification
    var clean = new LogAnalysis(true, 0, 3, false, Array.Empty<string>());
    var dirty = new LogAnalysis(true, 2, 3, false, new[] { "err" });
    Check(UsmtReturnCodes.Describe(3).Contains("skipped") && UsmtReturnCodes.Describe(999).Contains("unrecognised"), "return code text");
    Check(UsmtReturnCodes.Describe(-1073741511).Contains("0xC0000139") && UsmtReturnCodes.Describe(-1073741515).Contains("0xC0000135"), "return code text: loader failures");
    Check(UsmtReturnCodes.Describe(29).Contains("log for the real cause") && !UsmtReturnCodes.Describe(29).Contains("250 MB"), "return code text: 29 points at the log (C23)");
    Check(CaptureVerifier.Verify(0, false, 1_000_000, clean).Kind == CaptureOutcomeKind.Success, "verify: clean success");
    Check(CaptureVerifier.Verify(0, false, -1, clean).Kind == CaptureOutcomeKind.Failed, "verify: exit 0 but no package fails");
    Check(CaptureVerifier.Verify(0, false, 0, clean).Kind == CaptureOutcomeKind.Failed, "verify: empty package fails");
    Check(CaptureVerifier.Verify(0, false, 1000, dirty).Kind == CaptureOutcomeKind.SuccessWithWarnings, "verify: log errors give warnings");
    Check(CaptureVerifier.Verify(3, false, 1000, clean).Kind == CaptureOutcomeKind.SuccessWithWarnings, "verify: exit 3 gives warnings");
    Check(CaptureVerifier.Verify(3, false, -1, clean).Kind == CaptureOutcomeKind.Failed, "verify: exit 3 without package fails");
    var fail72 = CaptureVerifier.Verify(72, false, 1000, clean);
    Check(fail72.Kind == CaptureOutcomeKind.Failed && fail72.Summary.Contains("72"), "verify: fatal code fails and names the code");
    Check(CaptureVerifier.Verify(0, true, 1000, clean).Kind == CaptureOutcomeKind.Cancelled, "verify: cancelled");

    // ---- Command line
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", "x.ppkg", "/nocompress" })), "args: /nocompress refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", "x.ppkg", "/p" })), "args: /p refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", "x.ppkg", "/hardlink" })), "args: /hardlink refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", "x.ppkg", "/encrypt" })), "args: /encrypt refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", "x.ppkg", "/targetWindows10" })), "args: /target refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "x.ppkg" })), "args: missing /ppkg refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/ppkg", "x.ppkg" })), "args: missing /apps refused");
    Check(ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", new string('a', 300) })), "args: over-long line refused");
    Check(!ThrowsInvalid(() => ScanStateArgs.Validate(new[] { "/apps", "/ppkg", "x.ppkg", "/progress:p.log", "/o", "/c" })), "args: /progress is not mistaken for /p");

    var shortRoot = Path.GetPathRoot(tempRoot)!;
    try
    {
        var wsShort = RunWorkspace.Create(shortRoot);
        try
        {
            var cmdDefault = ScanStateArgs.Build(new CaptureOptions(), amd, wsShort, @"C:\Recovery\Customizations\usmt.ppkg");
            Eq(cmdDefault.Arguments[0], "/apps", "args: apps with oeminfo on");
            Check(!cmdDefault.Arguments.Any(a => a.StartsWith("/listfiles", StringComparison.OrdinalIgnoreCase)), "args: no /listfiles (C24)");
            Check(cmdDefault.Arguments.Contains("/tel:OFF") && cmdDefault.Arguments.Contains("/o") && cmdDefault.Arguments.Contains("/c") && cmdDefault.Arguments.Contains("/v:13"), "args: fixed switches present");
            var idx = cmdDefault.Arguments.ToList().IndexOf("/ppkg");
            Check(idx >= 0 && cmdDefault.Arguments[idx + 1] == @"C:\Recovery\Customizations\usmt.ppkg", "args: /ppkg followed by the package path");
            Check(cmdDefault.Arguments.Contains("/config:" + wsShort.ConfigFile) && cmdDefault.Arguments.Contains("/i:" + wsShort.RulesFile), "args: config and rules from the work folder");
            Check(cmdDefault.CommandLineText.Length <= 240, "args: command line stays under the 256 limit (" + cmdDefault.CommandLineText.Length + ")");
            Check(cmdDefault.ExePath.EndsWith("scanstate.exe", StringComparison.OrdinalIgnoreCase), "args: exe path");
            var noOem = ScanStateArgs.Build(new CaptureOptions { IncludeOemInfo = false }, amd, wsShort, @"C:\Recovery\Customizations\usmt.ppkg");
            Eq(noOem.Arguments[0], "/apps:-oeminfo", "args: apps with oeminfo off");

            // 3.10: /drivers is on by default, last on the line, absent when the option is off.
            Check(new CaptureOptions().IncludeDrivers, "drivers: option defaults to on");
            Eq(cmdDefault.Arguments[cmdDefault.Arguments.Count - 1], "/drivers", "args: /drivers is the last switch by default");
            Check(cmdDefault.Arguments.Count(a => a.Equals("/drivers", StringComparison.OrdinalIgnoreCase)) == 1, "args: /drivers appears once");
            var noDrv = ScanStateArgs.Build(new CaptureOptions { IncludeDrivers = false }, amd, wsShort, @"C:\Recovery\Customizations\usmt.ppkg");
            Check(!noDrv.Arguments.Any(a => a.StartsWith("/drivers", StringComparison.OrdinalIgnoreCase)), "args: no /drivers when the option is off");
            Check(noDrv.Arguments.SequenceEqual(cmdDefault.Arguments.Take(cmdDefault.Arguments.Count - 1)), "args: option off leaves the rest of the line unchanged");
            Check(cmdDefault.CommandLineText.Length - noDrv.CommandLineText.Length == " /drivers".Length, "args: /drivers adds 9 characters");
            Check(!cmdDefault.Arguments.Any(a => a.Contains(':') && a.StartsWith("/drivers", StringComparison.OrdinalIgnoreCase)), "args: no driver filter is passed");
            try { ScanStateArgs.Validate(cmdDefault.Arguments); Check(true, "args: /drivers passes validation next to /apps and /ppkg"); }
            catch (InvalidOperationException ex) { Check(false, "args: /drivers passes validation: " + ex.Message); }
            // Longest realistic line: /oeminfo default, a 4-hex run folder on a short root, and a package path on a long-ish drive path.
            var longest = ScanStateArgs.Build(new CaptureOptions(), amd, wsShort, @"C:\Recovery\Customizations\usmt.ppkg");
            Check(longest.CommandLineText.Length <= 240, "args: with /drivers the line is " + longest.CommandLineText.Length + " chars, guard is 240");
        }
        finally { wsShort.Delete(); }
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("skip  could not create a work folder at " + shortRoot + " (not writable here)"); }

    var ws1 = RunWorkspace.Create(tempRoot);
    Check(Directory.Exists(ws1.Folder) && Path.GetFileName(Path.GetDirectoryName(ws1.Folder)!) == "WTRP", "workspace created under WTRP");
    ws1.Delete();
    Check(!Directory.Exists(ws1.Folder) && !Directory.Exists(Path.Combine(tempRoot, "WTRP")), "workspace and empty WTRP root removed");

    // ---- Staging and the overwrite safety net
    var rec = Path.Combine(tempRoot, "Recovery2");
    var stg = RecoveryStaging.Prepare(rec, applyLockdown: false);
    Check(stg.CreatedRecovery && Directory.Exists(stg.CustomizationsDir), "staging creates Recovery and Customizations");
    Check((File.GetAttributes(stg.CustomizationsDir) & FileAttributes.Hidden) != 0, "Customizations is hidden");
    Check(!RecoveryStaging.Prepare(rec, applyLockdown: false).CreatedRecovery, "existing Recovery is not reported as created");

    var pk = stg.PackagePath;
    var pkManifest = DeploymentManifest.PathFor(pk);
    File.WriteAllText(pk, "old");
    DeploymentManifest.Write(pk, manifest);
    Check(RecoveryStaging.CheckLeftover(pk) is null, "no leftover before a run");
    Check(RecoveryStaging.MoveCurrentAside(pk), "current package moved aside");
    Check(!File.Exists(pk) && File.Exists(pk + ".prev") && File.Exists(pkManifest + ".prev"), "package and manifest are in *.prev");
    Check(RecoveryStaging.CheckLeftover(pk) is not null, "leftover is reported");
    File.WriteAllText(pk, "partial-new");
    RecoveryStaging.RestoreAside(pk);
    Check(File.ReadAllText(pk) == "old" && !File.Exists(pk + ".prev") && File.Exists(pkManifest) && !File.Exists(pkManifest + ".prev"), "failed run: previous package and manifest put back");

    RecoveryStaging.MoveCurrentAside(pk);
    File.WriteAllText(pk, "new");
    RecoveryStaging.CommitAside(pk);
    Check(File.ReadAllText(pk) == "new" && !File.Exists(pk + ".prev") && !File.Exists(pkManifest + ".prev"), "successful run: *.prev removed, new package kept");

    File.Delete(pk);
    File.WriteAllText(pk, "partial-first-run");
    RecoveryStaging.RestoreAside(pk);
    Check(!File.Exists(pk), "first-ever capture failing leaves no partial package");

    // ---- Backup
    File.WriteAllText(pk, new string('x', 5000));
    DeploymentManifest.Write(pk, manifest);
    var logRoot = Path.Combine(tempRoot, "logs");
    var logDir = Path.Combine(logRoot, "20260101-000000");
    Directory.CreateDirectory(logDir);
    File.WriteAllText(Path.Combine(logDir, "scanstate.log"), "log");
    Directory.CreateDirectory(Path.Combine(logRoot, "20250101-000000"));
    Eq(RecoveryLogs.FindLatestWithLog(logRoot), logDir, "latest run folder with a log is found");

    var zipPath = Path.Combine(tempRoot, "b", "backup.zip");
    var pcts = new List<int>();
    var br = await PackageActions.CreateBackupAsync(zipPath, pk, logDir, _ => { }, p => pcts.Add(p), CancellationToken.None);
    Check(br.Success && File.Exists(zipPath) && !File.Exists(zipPath + ".partial"), "backup written, no .partial left");
    using (var z = ZipFile.OpenRead(zipPath))
    {
        var names = z.Entries.Select(x => x.FullName).OrderBy(x => x).ToArray();
        Check(names.SequenceEqual(new[] { "manifest.json", "scanstate.log", "usmt.ppkg" }), "backup holds package, manifest and log");
        Check(z.GetEntry("usmt.ppkg")!.Length == 5000, "backup package size matches");
    }
    Check(pcts.Count > 0 && pcts[^1] == 100, "backup reports up to 100 percent");
    var noPkg = await PackageActions.CreateBackupAsync(Path.Combine(tempRoot, "b", "x.zip"), Path.Combine(tempRoot, "missing.ppkg"), null, _ => { }, _ => { }, CancellationToken.None);
    Check(!noPkg.Success, "backup of a missing package fails cleanly");

    // ---- Revert
    File.WriteAllText(Path.Combine(stg.CustomizationsDir, "third-party.ppkg"), "x");
    var rv = PackageActions.Revert(pk);
    Check(rv.Success && !File.Exists(pk) && !File.Exists(pkManifest), "revert removes package and manifest");
    Check(rv.OtherPackages.SequenceEqual(new[] { "third-party.ppkg" }) && File.Exists(Path.Combine(stg.CustomizationsDir, "third-party.ppkg")), "revert lists but keeps other packages");
    Check(PackageActions.Revert(pk).Success, "revert twice is harmless");

    // ---- Runner against a stand-in process (cmd.exe), so no real ScanState is needed
    var runDir = Path.Combine(tempRoot, "run");
    Directory.CreateDirectory(runDir);
    var progFile = Path.Combine(runDir, "p.log");
    File.WriteAllText(progFile, "2020/01/01 00:00:00, totalPercentageCompleted, 42" + Environment.NewLine);
    var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    var gotLines = new List<string>();
    var gotPct = new List<int>();
    var echo = new ScanStateCommand(cmdExe, runDir, new[] { "/c", "echo", "hello-from-fake" }, progFile, "x");
    var rr = await ScanStateRunner.RunAsync(echo, l => { lock (gotLines) gotLines.Add(l); }, p => { lock (gotPct) gotPct.Add(p); }, CancellationToken.None);
    Check(rr.ExitCode == 0 && !rr.Cancelled, "runner: clean exit");
    Check(gotLines.Any(l => l.Contains("hello-from-fake")) && rr.OutputLines.Any(l => l.Contains("hello-from-fake")), "runner: output captured");
    Check(gotPct.Contains(42), "runner: progress file percentage reported");

    var exit7 = new ScanStateCommand(cmdExe, runDir, new[] { "/c", "exit", "7" }, progFile, "x");
    Check((await ScanStateRunner.RunAsync(exit7, _ => { }, _ => { }, CancellationToken.None)).ExitCode == 7, "runner: exit code passed through");

    using (var cts = new CancellationTokenSource())
    {
        cts.CancelAfter(1500);
        var slow = new ScanStateCommand(cmdExe, runDir, new[] { "/c", "ping", "-n", "30", "127.0.0.1" }, progFile, "x");
        var killed = await ScanStateRunner.RunAsync(slow, _ => { }, _ => { }, cts.Token);
        Check(killed.Cancelled && killed.Elapsed < TimeSpan.FromSeconds(20), "runner: cancel kills the process tree");
    }

    // ---- Live reads (smoke only: must not throw on any machine)
    var winre = WinReCheck.Read();
    Console.WriteLine($"info  WinRE on this machine: {winre.State} ({winre.Detail})");
    var os = CurrentOs.Read();
    Console.WriteLine($"info  OS: {os.ProductName} {os.DisplayVersion} build {os.Build}");
    var programs = InstalledProgramsEstimate.Read();
    Console.WriteLine($"info  Installed programs: {programs.Count}, about {RecoveryFormat.Bytes(programs.Bytes)}");
    Console.WriteLine($"info  ElevationCheck: {ElevationCheck.IsElevated()}");
    Console.WriteLine($"info  Free on system drive: {RecoveryFormat.Bytes(DiskSpaceCheck.ReadFreeBytes(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"))}");
}
finally
{
    try { Directory.Delete(tempRoot, true); } catch { }
}

Console.WriteLine(failures == 0 ? $"PASS  {total} checks" : $"FAILED  {failures} of {total} checks");
return failures == 0 ? 0 : 1;
