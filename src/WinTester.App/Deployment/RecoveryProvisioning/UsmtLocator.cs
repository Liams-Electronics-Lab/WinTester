using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Result of the USMT pre-flight. Directory is the resolved vendor\usmt\<arch> folder when scanstate.exe was found
// (even if other files are missing, so the caller can still say where it looked); Problems lists every missing name.
public sealed record UsmtPreflight(string? ArchFolder, string? Directory, IReadOnlyList<string> Problems)
{
    public bool Ok => Directory is not null && Problems.Count == 0;
}

// Finds the bundled USMT for this Windows architecture and checks it is complete (spec correction C2/C3/C4).
// The vendor tree has amd64 and x86 folders only; there is no arm64 USMT, so Arm64 is reported as unsupported.
// Pure file-system logic, no WPF, nothing is written.
public static class UsmtLocator
{
    public const string ScanStateExe = "scanstate.exe";

    // C21 (S109): files the ADK's USMT folder does not contain but ScanState /apps needs next to scanstate.exe.
    // They come from the ADK "Windows Setup\<arch>\Sources" folder (hwcompat.dll and the XML scripts); without
    // hwcompat.dll ScanState exits 29, without upgrade_comp.xml and friends it exits 26. All of these were present
    // in the one tested configuration, so all are required.
    public static readonly string[] SupportFiles =
    {
        "hwcompat.dll", "AppXProvisioning.xml", "offline.xml", "oscomps.xml", "reagent.xml", "uninstall.xml",
        "uninstall_data.xml", "upgradeagent.xml", "upgrade_bulk.xml", "upgrade_comp.xml", "upgrade_data.xml",
        "upgrade_frmwrk.xml", "upgWow_bulk.xml"
    };

    // C4: scanstate.exe plus the four DLLs named in the spec, MigApp.xml and both manifest folders, then the C21
    // support files. The full folder ships (about 210 files per arch plus the support files); these are the ones
    // whose absence we can name up front.
    private static readonly string[] RequiredFiles =
        new[] { ScanStateExe, "migcore.dll", "migstore.dll", "unbcl.dll", "wdscore.dll", "MigApp.xml" }
            .Concat(SupportFiles).ToArray();

    private static readonly string[] RequiredFolders = { "DLManifests", "ReplacementManifests" };

    public static readonly string[] AllConfigFiles =
    {
        "Config_AppsAndSettings.xml", "Config_AppsOnly.xml", "Config_SettingsOnly.xml"
    };

    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "vendor", "usmt");

    // null for anything we have no USMT folder for (Arm64 and unknown architectures).
    public static string? ArchFolderName(Architecture arch) => arch switch
    {
        Architecture.X64 => "amd64",
        Architecture.X86 => "x86",
        _ => null
    };

    // configFileName: the chosen Config_*.xml. Null checks all three (used by the Detect screen, before the
    // capture mode is picked). usmtRoot is only overridden by tests.
    public static UsmtPreflight Check(Architecture arch, string? configFileName = null, string? usmtRoot = null)
    {
        var folder = ArchFolderName(arch);
        if (folder is null)
            return new UsmtPreflight(null, null, new[] { $"No USMT is bundled for {arch} Windows (only x64 and x86)." });

        var root = usmtRoot ?? DefaultRoot;
        var dir = Path.Combine(root, folder);
        var problems = new List<string>();

        try
        {
            if (!Directory.Exists(dir))
                return new UsmtPreflight(folder, null, new[] { $"Folder not found: {dir}" });

            foreach (var name in RequiredFiles)
                if (!File.Exists(Path.Combine(dir, name))) problems.Add(name);

            foreach (var name in RequiredFolders)
                if (!Directory.Exists(Path.Combine(dir, name))) problems.Add(name + "\\");

            var configs = configFileName is null ? AllConfigFiles : new[] { configFileName };
            foreach (var name in configs)
                if (!File.Exists(Path.Combine(dir, name))) problems.Add(name);
        }
        catch (Exception ex)
        {
            problems.Add("Could not read the USMT folder: " + ex.Message);
        }

        var hasExe = File.Exists(Path.Combine(dir, ScanStateExe));
        return new UsmtPreflight(folder, hasExe ? dir : null, problems);
    }

    // "Missing: a, b, c" style sentence for the wizard.
    public static string Describe(UsmtPreflight p)
    {
        if (p.Ok) return p.Directory!;
        if (p.Problems.Count == 0) return "USMT tools not found.";
        if (p.Problems.Count == 1 && p.Problems[0].Contains(' ')) return p.Problems[0]; // already a sentence
        return "USMT folder is incomplete. Missing: " + string.Join(", ", p.Problems);
    }
}
