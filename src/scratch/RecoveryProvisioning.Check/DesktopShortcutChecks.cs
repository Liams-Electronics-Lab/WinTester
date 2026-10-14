using System.IO;
using WinTester.App.Deployment.RecoveryProvisioning;

// Checks for the "copy desktop shortcuts to Public Desktop" option. Called from Program.cs.
public static class DesktopShortcutChecks
{
    public static void Run(Action<bool, string> check)
    {
        void Check(bool ok, string n) => check(ok, n);

        // ---- paths and the rules component
        Check(DesktopShortcuts.PublicDesktop("C:") == @"C:\Users\Public\Desktop", "public desktop path");
        Check(DesktopShortcuts.PublicDesktop("") == @"C:\Users\Public\Desktop", "empty drive falls back to C:");
        Check(DesktopShortcuts.PublicDesktop(@"D:\") == @"D:\Users\Public\Desktop", "trailing slash trimmed");

        var off = RulesXmlBuilder.Build(new CaptureOptions(), "C:");
        Check(off.IncludePatterns.Count == 0, "option off: no include patterns");
        Check(!off.Document.Descendants("displayName").Any(d => d.Value.Contains("public desktop")), "option off: no desktop component");

        var on = RulesXmlBuilder.Build(new CaptureOptions { CopyDesktopShortcuts = true }, "C:");
        Check(on.IncludePatterns.SequenceEqual(new[] { @"C:\Users\Public\Desktop\* [*]" }), "option on: one include pattern");
        var comp = on.Document.Descendants("component").Single(c => (string?)c.Element("displayName") == "WinTester public desktop shortcuts");
        Check(comp.Descendants("include").Count() == 1 && comp.Descendants("pattern").Single().Value == @"C:\Users\Public\Desktop\* [*]", "option on: include component shape");
        Check((string?)comp.Descendants("pattern").Single().Attribute("type") == "File", "pattern is type File");
        Check(on.Patterns.Count == off.Patterns.Count, "exclusion set unchanged by the option");
        var settingsOnly = RulesXmlBuilder.Build(new CaptureOptions { CopyDesktopShortcuts = true, Mode = CaptureMode.SettingsOnly }, "C:");
        Check(settingsOnly.IncludePatterns.Count == 1, "option works in settings-only mode");

        // ---- per-user path detection
        Check(DesktopShortcuts.IsPerUserPath(@"C:\Users\leham\AppData\Local\App\app.exe"), "profile path is per-user");
        Check(!DesktopShortcuts.IsPerUserPath(@"C:\Users\Public\Desktop\x.exe"), "Public profile is not per-user");
        Check(!DesktopShortcuts.IsPerUserPath(@"C:\Program Files\Google\Chrome\Application\chrome.exe"), "Program Files is not per-user");
        Check(!DesktopShortcuts.IsPerUserPath(null) && !DesktopShortcuts.IsPerUserPath(""), "empty target is not per-user");

        // ---- copy (real files in a temp folder)
        var root = Path.Combine(Path.GetTempPath(), "wt-dsk-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var src = Path.Combine(root, "user"); var dst = Path.Combine(root, "public");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "Chrome.lnk"), "new-chrome");
            File.WriteAllText(Path.Combine(src, "Site.url"), "url");
            File.WriteAllText(Path.Combine(src, "notes.txt"), "not a shortcut");
            File.WriteAllText(Path.Combine(src, "desktop.ini"), "ini");
            File.WriteAllText(Path.Combine(src, "UPPER.LNK"), "upper");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "Nested.lnk"), "nested");

            Check(DesktopShortcuts.List(src).Count == 3, "list finds .lnk/.url only, top level, any case");
            Check(DesktopShortcuts.List(Path.Combine(root, "missing")).Count == 0, "missing folder lists nothing");

            // pre-existing destination: one read-only file to overwrite, one unrelated file to keep
            Directory.CreateDirectory(dst);
            var old = Path.Combine(dst, "Chrome.lnk");
            File.WriteAllText(old, "old-chrome");
            File.SetAttributes(old, FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(dst, "Keep.lnk"), "keep");

            var r = DesktopShortcuts.Copy(src, dst, f => Path.GetFileName(f) == "UPPER.LNK" ? @"C:\Users\bob\x.exe" : @"C:\Program Files\x.exe");
            Check(r.FolderError is null && r.Failed.Count == 0, "copy: no errors");
            Check(r.Copied.Count == 2 && r.Copied.Contains("Chrome.lnk") && r.Copied.Contains("Site.url"), "copy: two shortcuts copied");
            Check(File.ReadAllText(old) == "new-chrome", "copy: existing (read-only) file overwritten silently");
            Check(File.ReadAllText(Path.Combine(dst, "Keep.lnk")) == "keep", "copy: unrelated destination file untouched");
            Check(!File.Exists(Path.Combine(dst, "notes.txt")) && !File.Exists(Path.Combine(dst, "desktop.ini")), "copy: non-shortcuts not copied");
            Check(!File.Exists(Path.Combine(dst, "Nested.lnk")), "copy: subfolders not copied");
            Check(r.SkippedPerUser.SequenceEqual(new[] { "UPPER.LNK" }), "copy: per-user shortcut reported as skipped");
            Check(!File.Exists(Path.Combine(dst, "UPPER.LNK")), "copy: per-user shortcut not copied");
            Check(DesktopShortcuts.Copy(src, Path.Combine(root, "p2"), null).Copied.Count == 3, "copy: without a resolver nothing is skipped");

            var same = DesktopShortcuts.Copy(src, src + @"\", null);
            Check(same.FolderError is not null && same.Copied.Count == 0, "copy: same folder refused");

            // destination folder cannot be created (a file is in the way)
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            var bad = DesktopShortcuts.Copy(src, Path.Combine(blocker, "Desktop"), null);
            Check(bad.FolderError is not null, "copy: unusable target folder reported");

            // manifest round trip
            var o = new CaptureOptions { CopyDesktopShortcuts = true };
            var mf = DeploymentManifest.Create(o, "/nonexistent", "amd64", "/nonexistent/x.ppkg", false, r.Copied);
            Check(mf.DesktopShortcuts is { Length: 2 }, "manifest lists the copied shortcuts");
            var offMf = DeploymentManifest.Create(new CaptureOptions(), "/nonexistent", "amd64", "/nonexistent/x.ppkg", false, r.Copied);
            Check(offMf.DesktopShortcuts is null, "manifest: option off records null");
            var json = System.Text.Json.JsonSerializer.Serialize(mf);
            var back = System.Text.Json.JsonSerializer.Deserialize<DeploymentManifest>(json);
            Check(back?.DesktopShortcuts?.Length == 2, "manifest round trip");
            var older = System.Text.Json.JsonSerializer.Deserialize<DeploymentManifest>(
                json.Replace(",\"DesktopShortcuts\":[\"Chrome.lnk\",\"Site.url\"]", ""));
            Check(older is not null && older.DesktopShortcuts is null, "manifest without the field still reads");
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch { }
        }
    }
}
