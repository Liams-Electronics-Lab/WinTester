using System.Xml.Linq;
using WinTester.App.Deployment.RecoveryProvisioning;

// Item 5.3: the OEM branding include rule (RulesXmlBuilder.OemRuleApplies / Build(..., oemBrandingRule)). Pure logic, no machine access.
public static class OemRuleChecks
{
    public static void Run(Action<bool, string> check)
    {
        var o = new CaptureOptions();                       // IncludeOemInfo is on by default
        check(o.IncludeOemInfo, "oem rule: fixture, OEM info is on by default");

        // ---- when the rule applies
        check(RulesXmlBuilder.OemRuleApplies(o, oemInfoSet: true, oemHookPresent: false), "oem rule: applies when info is set and no hook is staged");
        check(!RulesXmlBuilder.OemRuleApplies(o, true, true), "oem rule: not written when the OEM Branding hook is staged (the hook is the source of truth)");
        check(!RulesXmlBuilder.OemRuleApplies(o, false, false), "oem rule: not written when no OEM information is set");
        check(!RulesXmlBuilder.OemRuleApplies(new CaptureOptions { IncludeOemInfo = false }, true, false), "oem rule: not written when the operator unticked OEM info");

        // ---- off: the rules file is what it was before 5.3
        var off = RulesXmlBuilder.Build(o, "C:");
        check(off.OemPatterns.Count == 0 && !off.IncludePatterns.Any(p => p.Contains("OEMInformation") || p.Contains("Oobe"))
              && !off.Document.Descendants("displayName").Any(e => e.Value == RulesXmlBuilder.OemComponentName),
              "oem rule: off by default, no component and no pattern");

        // ---- on
        var on = RulesXmlBuilder.Build(o, "C:", null, oemBrandingRule: true);
        var comp = on.Document.Descendants("component").FirstOrDefault(c => c.Element("displayName")?.Value == RulesXmlBuilder.OemComponentName);
        var pats = comp?.Descendants("pattern").ToList() ?? new List<XElement>();
        var reg = pats.Where(p => (string?)p.Attribute("type") == "Registry").Select(p => p.Value).ToList();
        var files = pats.Where(p => (string?)p.Attribute("type") == "File").Select(p => p.Value).ToList();
        check(comp is not null && comp.Descendants("include").Any() && !comp.Descendants("unconditionalExclude").Any(), "oem rule: one include component is written");
        check(reg.Count == 2 && reg.All(r => r.StartsWith(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", StringComparison.Ordinal))
              && reg.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation [*]")
              && reg.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation\* [*]"),
              "oem rule: registry patterns cover the key's own values and its subkeys");
        check(files.Count == 1 && files[0] == @"C:\Windows\System32\Oobe\Info\* [*]", "oem rule: the logo folder is included (" + string.Join(";", files) + ")");
        check(on.OemPatterns.Count == 3 && on.OemPatterns.All(p => on.IncludePatterns.Contains(p)), "oem rule: the patterns are also in IncludePatterns, so a skipped rule is reported");
        check(on.Patterns.SequenceEqual(off.Patterns), "oem rule: the exclusion list is unchanged by the rule");
        check(on.Document.Descendants("displayName").Select(e => e.Value).Distinct().Count() == on.Document.Descendants("displayName").Count(),
              "oem rule: component display names stay unique (ScanState refuses repeats)");
        check(RulesXmlBuilder.Build(o, "D:", null, true).OemPatterns.Contains(@"D:\Windows\System32\Oobe\Info\* [*]"), "oem rule: follows the system drive letter");
    }
}
