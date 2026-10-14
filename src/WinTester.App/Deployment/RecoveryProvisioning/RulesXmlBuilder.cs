using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace WinTester.App.Deployment.RecoveryProvisioning;

public sealed record RulesBuildResult(XDocument Document, IReadOnlyList<string> Patterns, IReadOnlyList<string> IncludePatterns)
{
    // 5.3: the OEM branding include patterns (also part of IncludePatterns, so a skipped rule is reported); empty when the rule is off.
    public IReadOnlyList<string> OemPatterns { get; init; } = Array.Empty<string>();

    // 5.5: include patterns of the customer files (also part of IncludePatterns); empty when none are listed.
    public IReadOnlyList<string> CustomerPatterns { get; init; } = Array.Empty<string>();
}

// Builds the generated ScanState /i: file. <unconditionalExclude> rules (they beat every include rule in the vendor
// XML files) plus, since 3.7, one <include> component per app chosen on the Configure step (files, registration and
// Start-menu shortcut, same shape as the probe that worked in t5b). Everything else stays in the unmodified
// Config_*.xml passed to /config:.
// Structure follows Microsoft's "Exclude files and settings" example:
//   migration > component(type=Documents) > role(role=Data) > rules > unconditionalExclude > objectSet > pattern
// Pure logic, no WPF. Patterns use USMT's "<folder>\* [<file>]" form: "\* [*]" = everything below the folder.
public static class RulesXmlBuilder
{
    public const string UrlId = "http://www.microsoft.com/migration/1.0/migxmlext/wintester";

    // ---- 5.3: OEM branding inside the package. The OEM Branding tool keeps what Windows shows under Settings > System > About in
    // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation and the logo in <Windows>\System32\Oobe\Info. This rule
    // carries both in the package, so the branding comes back after Reset even when no reset hook is staged.
    public const string OemComponentName = "WinTester OEM branding";
    public const string CustomerComponentName = "WinTester customer files";   // 5.5
    public const string OemRegistryRoot = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation";   // same key as OemBrandingService

    public static IReadOnlyList<string> OemRegistryPatterns { get; } = new[] { OemRegistryRoot + " [*]", OemRegistryRoot + @"\* [*]" };
    public static string OemLogoPattern(string systemDrive) => NormalizeDrive(systemDrive) + @"\Windows\System32\Oobe\Info\* [*]";

    // The rule is written only when the operator ticked OEM info, there is something set to carry, and the OEM Branding tool's
    // reset hook is NOT staged. With the hook present the hook re-applies the branding itself, and it is rewritten every time the
    // tool is applied; a copy in the package could be older and would put the old values back (the package is applied after the hook).
    public static bool OemRuleApplies(CaptureOptions options, bool oemInfoSet, bool oemHookPresent) =>
        options.IncludeOemInfo && oemInfoSet && !oemHookPresent;

    // Characters that can never appear in a Windows path (except * which is the USMT wildcard) or would break the
    // pattern syntax. '?' is rejected because USMT's wildcard support for it is unverified.
    private static readonly char[] BadChars = { '<', '>', '"', '|', '?' };

    // dirExists (S127, optional): when given, a game-library exclusion is written only if that library folder exists on this PC.
    // Without it every default library folder is listed (the older behaviour, used by the checks).
    // oemBrandingRule (5.3): the caller decides with OemRuleApplies; false (the default) keeps the older behaviour for the checks.
    public static RulesBuildResult Build(CaptureOptions options, string systemDrive, Func<string, bool>? dirExists = null,
        bool oemBrandingRule = false, IReadOnlyList<string>? extraCustomerPatterns = null)
    {
        var sd = NormalizeDrive(systemDrive);
        var patterns = new List<string>();

        void Folder(string path) => patterns.Add(path + @"\* [*]");

        if (options.ExcludeTemp)
        {
            Folder(sd + @"\Users\*\AppData\Local\Temp");
            Folder(sd + @"\Windows\Temp");
        }

        if (options.ExcludeBrowserCaches)
        {
            foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                Folder(sd + @"\Users\*\AppData\Local\Google\Chrome\User Data\*\" + cache);
                Folder(sd + @"\Users\*\AppData\Local\Microsoft\Edge\User Data\*\" + cache);
            }
            Folder(sd + @"\Users\*\AppData\Local\Mozilla\Firefox\Profiles\*\cache2");
            Folder(sd + @"\Users\*\AppData\Local\Microsoft\Windows\INetCache");
        }

        if (options.ExcludeUpdateCache) Folder(sd + @"\Windows\SoftwareDistribution\Download");

        // Always excluded, no toggle: system-managed files that are locked or huge and never part of a deployment.
        Folder(sd + @"\System Volume Information");
        Folder(sd + @"\" + RunWorkspace.RootName);   // ScanState's own per-run files (logs, this rules file)
        foreach (var file in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
            patterns.Add(sd + @"\* [" + file + "]");
        // Windows' cached copies of each local account's picture: stale after a reset, and the capture account's picture
        // should not be shipped to the next user.
        patterns.Add(sd + @"\ProgramData\Microsoft\User Account Pictures [*.dat]");

        // Game libraries are never captured (3.14): the default library folders are always excluded, and so is whatever a
        // ticked app's profile excludes inside its own folder (a Steam library in a custom place, the GOG Games folder).
        patterns.AddRange(GameLibraries.ExclusionPatterns(sd, dirExists));
        foreach (var app in options.ActiveApps) patterns.AddRange(app.ExcludePatterns);

        // The wizard refuses lines that would exclude something the package is built from; a line that still arrives here
        // (a manifest, a test, another caller) is dropped instead of written.
        var guard = ExclusionGuard.ProtectedFor(sd, options);
        foreach (var raw in options.CustomExclusions)
        {
            var p = TryConvertCustom(raw, out _);
            if (p is not null && ExclusionGuard.Check(raw, sd, guard) is null) patterns.Add(p);
        }

        var distinct = patterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var apps = options.ActiveApps;
        var includes = apps.SelectMany(a => a.FilesFor(options).Concat(a.RegistryFor(options)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Optional: the whole Public Desktop folder (the worker copies the user's shortcuts into it first).
        string? publicDesktop = null;
        if (options.CopyDesktopShortcuts)
        {
            publicDesktop = DesktopShortcuts.PublicDesktop(sd) + @"\* [*]";
            if (!includes.Contains(publicDesktop, StringComparer.OrdinalIgnoreCase)) includes.Add(publicDesktop);
        }

        var oem = new List<string>();
        if (oemBrandingRule)
        {
            oem.AddRange(OemRegistryPatterns);
            oem.Add(OemLogoPattern(sd));
            foreach (var p in oem)
                if (!includes.Contains(p, StringComparer.OrdinalIgnoreCase)) includes.Add(p);
        }

        // extraCustomerPatterns: copies the "keep both" choice saved under a new name next to a single-file item's destination file.
        var customer = CustomerFileSet.IncludePatterns(options.CustomerFiles, sd)
            .Concat(extraCustomerPatterns ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var cp in customer)
            if (!includes.Contains(cp, StringComparer.OrdinalIgnoreCase)) includes.Add(cp);
        return new RulesBuildResult(BuildDocument(distinct, apps, options, publicDesktop, oem, customer), distinct, includes)
        {
            OemPatterns = oem,
            CustomerPatterns = customer
        };
    }

    // A pattern with a character XML cannot hold would abort the whole rules file; such a rule is left out instead.
    private static bool XmlSafe(string p)
    {
        try { XmlConvert.VerifyXmlChars(p); return true; }
        catch (XmlException) { return false; }
    }

    private static XDocument BuildDocument(IEnumerable<string> patterns, IReadOnlyList<AppCapturePlan> apps,
        CaptureOptions options, string? publicDesktopPattern = null, IReadOnlyList<string>? oemPatterns = null,
        IReadOnlyList<string>? customerPatterns = null)
    {
        var objectSet = new XElement("objectSet");
        foreach (var p in patterns) objectSet.Add(new XElement("pattern", new XAttribute("type", "File"), p));

        var migration = new XElement("migration", new XAttribute("urlid", UrlId),
            new XElement("component",
                new XAttribute("type", "Documents"),
                new XAttribute("context", "UserAndSystem"),
                new XElement("displayName", "WinTester exclusions"),
                new XElement("role", new XAttribute("role", "Data"),
                    new XElement("rules",
                        new XElement("unconditionalExclude", objectSet)))));

        // One component per app so the ScanState log names what each rule set belongs to. Same component shape as
        // the t5/t5b probes (type Documents, context UserAndSystem, role Data).
        // ScanState refuses a rules file in which two components share a display name (exit 26, "Component with display name ...").
        // Two scanned apps can have the same name (a .NET runtime and its bundle in Package Cache), so repeats get " (2)", " (3)".
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinTester exclusions", "WinTester public desktop shortcuts", OemComponentName, CustomerComponentName };
        foreach (var app in apps)
        {
            var name = "WinTester app: " + app.Name;
            for (var n = 2; !usedNames.Add(name); n++) name = "WinTester app: " + app.Name + " (" + n + ")";
            var set = new XElement("objectSet");
            foreach (var p in app.FilesFor(options).Where(XmlSafe))
                set.Add(new XElement("pattern", new XAttribute("type", "File"), p));
            foreach (var p in app.RegistryFor(options).Where(XmlSafe))
                set.Add(new XElement("pattern", new XAttribute("type", "Registry"), p));

            migration.Add(new XElement("component",
                new XAttribute("type", "Documents"),
                new XAttribute("context", "UserAndSystem"),
                new XElement("displayName", name),
                new XElement("role", new XAttribute("role", "Data"),
                    new XElement("rules", new XElement("include", set)))));
        }

        if (publicDesktopPattern is not null)
        {
            migration.Add(new XElement("component",
                new XAttribute("type", "Documents"),
                new XAttribute("context", "UserAndSystem"),
                new XElement("displayName", "WinTester public desktop shortcuts"),
                new XElement("role", new XAttribute("role", "Data"),
                    new XElement("rules", new XElement("include",
                        new XElement("objectSet", new XElement("pattern", new XAttribute("type", "File"), publicDesktopPattern)))))));
        }

        if (customerPatterns is { Count: > 0 })
        {
            var set = new XElement("objectSet");
            foreach (var p in customerPatterns.Where(XmlSafe))
                set.Add(new XElement("pattern", new XAttribute("type", "File"), p));
            migration.Add(new XElement("component",
                new XAttribute("type", "Documents"),
                new XAttribute("context", "UserAndSystem"),
                new XElement("displayName", CustomerComponentName),
                new XElement("role", new XAttribute("role", "Data"),
                    new XElement("rules", new XElement("include", set)))));
        }

        if (oemPatterns is { Count: > 0 })
        {
            var set = new XElement("objectSet");
            foreach (var p in oemPatterns.Where(XmlSafe))
                set.Add(new XElement("pattern", new XAttribute("type", p.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ? "Registry" : "File"), p));
            migration.Add(new XElement("component",
                new XAttribute("type", "Documents"),
                new XAttribute("context", "UserAndSystem"),
                new XElement("displayName", OemComponentName),
                new XElement("role", new XAttribute("role", "Data"),
                    new XElement("rules", new XElement("include", set)))));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), migration);
    }

    // Checks one operator-typed exclusion line. Returns the USMT pattern, or null with a reason in `error`.
    //   C:\Some\Folder          -> C:\Some\Folder\* [*]
    //   C:\Some\Folder\         -> same
    //   C:\Some\Folder\app.log  -> C:\Some\Folder [app.log]        (last segment has a dot: treated as a file)
    //   C:\Some\Folder\*.log    -> C:\Some\Folder [*.log]
    //   C:\Some\Folder\* [*.tmp]-> used as written                 (already in USMT form)
    // Lines must start with a drive letter or a %VARIABLE%.
    public static string? TryConvertCustom(string raw, out string? error)
    {
        error = null;
        var line = (raw ?? "").Trim();
        if (line.Length == 0) { error = "Empty line."; return null; }

        if (line.IndexOfAny(BadChars) >= 0)
        {
            error = $"\"{line}\": contains a character that is not allowed (use * as the only wildcard).";
            return null;
        }

        var rooted = (line.Length >= 3 && char.IsLetter(line[0]) && line[1] == ':' && line[2] == '\\')
                     || (line.StartsWith('%') && line.IndexOf('%', 1) > 1);
        if (!rooted)
        {
            error = $"\"{line}\": use a full path such as C:\\Tools\\Cache (or start with %VARIABLE%).";
            return null;
        }

        // Already in USMT "folder [file]" form: must have a closing bracket at the very end and a space before '['.
        var open = line.IndexOf('[');
        if (open >= 0)
        {
            if (open == 0 || line[open - 1] != ' ' || !line.EndsWith(']') || line.IndexOf('[', open + 1) >= 0
                || line.IndexOf(']') != line.Length - 1 || line.Length - open < 3)
            {
                error = $"\"{line}\": brackets must look like C:\\Folder\\* [*.ext].";
                return null;
            }
            return line;
        }

        if (line.EndsWith('\\')) return line.TrimEnd('\\') + @"\* [*]";

        var lastSlash = line.LastIndexOf('\\');
        if (lastSlash < 0) return line + @"\* [*]"; // bare %VARIABLE%: treat as a folder
        var dir = line[..lastSlash];
        var leaf = line[(lastSlash + 1)..];
        if (leaf.Length == 0) return line.TrimEnd('\\') + @"\* [*]";

        if (leaf == "*") return dir + @"\* [*]";
        if (leaf.Contains('*') || leaf.Contains('.')) return dir + " [" + leaf + "]";
        return line + @"\* [*]";
    }

    // All problems in the operator's custom lines, for the Configure step. Empty list = all lines usable.
    public static IReadOnlyList<string> ValidateCustom(IEnumerable<string> lines)
    {
        var errors = new List<string>();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            if (TryConvertCustom(l, out var err) is null && err is not null) errors.Add(err);
        }
        return errors;
    }

    // Writes Rules.xml into a fresh per-run folder under %TEMP% (C5). The caller owns the folder and deletes it in a
    // finally block; nothing is ever written to the Recovery folder.
    public static string WriteToRunFolder(XDocument doc, out string runFolder)
    {
        runFolder = Path.Combine(Path.GetTempPath(), "WinTester-RP-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(runFolder);
        var file = Path.Combine(runFolder, "Rules.xml");
        WriteToFile(doc, file);
        return file;
    }

    // UTF-8 without a BOM, indented. Used directly by the real capture (file lives in the short RunWorkspace folder).
    public static void WriteToFile(XDocument doc, string file)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using var w = XmlWriter.Create(file, settings);
        doc.Save(w);
    }

    public static void DeleteRunFolder(string? runFolder)
    {
        if (string.IsNullOrEmpty(runFolder)) return;
        try { if (Directory.Exists(runFolder)) Directory.Delete(runFolder, true); }
        catch { /* temp folder; a leftover is harmless and the OS temp cleanup will get it */ }
    }

    // Advisory check against the vendor MigXML.xsd. The XSD has not been run here, so a failure is reported as a
    // warning for the log and does not block the capture; ScanState itself is the final judge of the file.
    // Returns an empty list when the file validates or when the XSD is missing/unreadable.
    public static IReadOnlyList<string> ValidateAgainstXsd(XDocument doc, string xsdPath)
    {
        var problems = new List<string>();
        try
        {
            if (!File.Exists(xsdPath)) return problems;
            var set = new XmlSchemaSet();
            set.Add(null, xsdPath);
            doc.Validate(set, (_, e) => problems.Add(e.Message));
        }
        catch (Exception ex)
        {
            problems.Add("Schema check could not run: " + ex.Message);
        }
        return problems;
    }

    private static string NormalizeDrive(string drive)
    {
        var d = (drive ?? "").Trim().TrimEnd('\\', '/');
        return d.Length == 0 ? "C:" : d;
    }
}
