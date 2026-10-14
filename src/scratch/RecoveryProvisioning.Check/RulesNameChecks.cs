using WinTester.App.Deployment.RecoveryProvisioning;

// Two apps with the same name (S131 log: the .NET Desktop Runtime and its Package Cache bundle) must not give two components with
// the same display name: ScanState rejects that file with exit code 26.
public static class RulesNameChecks
{
    public static void Run(Action<bool, string> check)
    {
        AppCapturePlan P(string name, string folder) => new(name, "1.0", "Pub", folder, 0,
            new[] { folder + @"\* [*]" }, Array.Empty<string>(), new List<string>());
        var o = new CaptureOptions();
        o.Apps.Add(P("Same Name", @"C:\Program Files\A"));
        o.Apps.Add(P("Same Name", @"C:\ProgramData\Package Cache\{X}"));
        o.Apps.Add(P("same name", @"C:\Program Files\C"));
        o.Apps.Add(P("Other", @"C:\Program Files\D"));
        var names = RulesXmlBuilder.Build(o, "C:").Document.Descendants("displayName").Select(e => e.Value).ToList();
        check(names.Count == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "rules: component display names are unique");
        check(names.Contains("WinTester app: Same Name") && names.Contains("WinTester app: Same Name (2)") && names.Contains("WinTester app: Other"),
            "rules: the first keeps its name, a repeat gets a number");
    }
}
