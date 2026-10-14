using System.IO;
using System.Xml;
using System.Xml.Linq;
using WinTester.App.Deployment.RecoveryProvisioning;

// Registry strings with stray NUL characters must never reach a rule pattern, and a bad pattern must not stop the
// rules file from being written. Called from Program.cs.
public static class NulChecks
{
    private const string Cache = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    private const string Folder = @"C:\Program Files\Acme";

    public static void Run(Action<bool, string> check, string tempRoot)
    {
        // ---- The reader's NUL handling
        check((string?)WinRegistryReader.StripNuls("{abc}\0\0") == "{abc}", "nul: trailing NULs cut from a string value");
        check((string?)WinRegistryReader.StripNuls("a\0b") == "a", "nul: a string is cut at its first NUL");
        var multi = (string[]?)WinRegistryReader.StripNuls(new[] { "one\0", "two" });
        check(multi is not null && multi.Length == 2 && multi[0] == "one" && multi[1] == "two", "nul: every element of a multi-string is cut");
        check(WinRegistryReader.StripNuls(5) is 5 && WinRegistryReader.StripNuls(null) is null, "nul: other value types pass through");

        // ---- Pattern checks
        check(!WinPath.PatternSafe("C:\\Program Files\\Acme\0"), "nul: PatternSafe refuses a NUL");
        check(!WinPath.PatternSafe("C:\\Program Files\\Ac\u0001me"), "nul: PatternSafe refuses any control character");
        check(!WinPath.PatternSafeAfterBracketEscape("C:\\Start Menu\\Acme\0"), "nul: bracket-escape check refuses a NUL");
        check(WinPath.PatternSafe("C:\\Program Files\\百度网盘"), "nul: ordinary non-ASCII names still pass");

        // ---- A task whose cache id carries a NUL is reported, not written
        var vals = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void K(string p) { var parts = p.Split('\\'); for (var i = 1; i <= parts.Length; i++) keys.Add(string.Join("\\", parts[..i])); }
        K(Cache + @"\Tree\Acme\Updater");
        var reg = new AppDiscoveryChecks.Fake(keys, vals);
        var badId = "{11111111-1111-1111-1111-111111111111}\0";
        var tasks = new List<TaskInfo>
        {
            new(@"Acme\Updater", new[] { @"C:\Program Files\Acme\upd.exe" }, true, badId),
        };
        var st = StartupRules.Derive(Folder, reg, tasks, new List<ShortcutInfo>(), "C:");
        check(!st.RegistryPatterns.Any(x => x.Contains('\0')) && !st.FilePatterns.Any(x => x.Contains('\0')), "nul: no pattern from a task with a NUL id carries a NUL");
        check(st.Notes.Any(n => n.Contains("Acme\\Updater") && n.Contains("cache entry")), "nul: the task is reported as not captured");

        // ---- The rules file is still written when a plan holds a bad pattern
        var plan = new AppCapturePlan("Acme", "1", "Acme Inc", Folder, 0,
            new[] { Folder + @"\* [*]", Folder + "\0\\bad [*]" },
            new[] { @"HKLM\SOFTWARE\Acme [*]", "HKLM\\SOFTWARE\\Acme\0 [*]" },
            new List<string>());
        var options = new CaptureOptions();
        options.Apps.Add(plan);
        var rules = RulesXmlBuilder.Build(options, "C:");
        var xml = rules.Document.ToString();
        check(xml.Contains(@"HKLM\SOFTWARE\Acme [*]") && xml.Contains(@"C:\Program Files\Acme\* [*]"), "nul: good patterns of the same app are kept");
        var file = Path.Combine(tempRoot, "nul-rules.xml");
        string? err = null;
        try { RulesXmlBuilder.WriteToFile(rules.Document, file); } catch (Exception ex) { err = ex.Message; }
        check(err is null, "nul: the rules file is written (" + err + ")");
        var back = err is null ? File.ReadAllText(file) : "";
        check(back.Length > 0 && !back.Contains('\0') && XDocument.Load(file).Root is not null, "nul: the written file is complete, valid XML with no NUL");
    }
}
