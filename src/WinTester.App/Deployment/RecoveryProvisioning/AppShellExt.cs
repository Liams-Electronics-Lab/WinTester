using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Shell extensions: context-menu, drag-and-drop and property-sheet handlers that an app registers under
// HKLM\SOFTWARE\Classes\<class>\shellex\<kind>\<name>. The handler key only holds a CLSID; the code that draws the menu
// is the in-process server named under HKLM\SOFTWARE\Classes\CLSID\{guid}\InprocServer32. A handler belongs to an app
// when that server sits inside the app's install folder. Found on the 7-Zip test (S120): the app came back after Reset
// but its right-click menu did not, because none of these keys were in the package.
//
// Per matching handler the rules carry: the handler key, the CLSID key (64-bit and WOW6432Node twin, only if present)
// and the CLSID's value under Shell Extensions\Approved (only if present). Nothing here has run on Windows.
public static class ShellExtRules
{
    private const string ClassesRoot = @"SOFTWARE\Classes";
    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    // Class keys whose shellex subkeys are scanned, and the handler kinds read below each. Extension classes (.zip) are not scanned.
    private static readonly string[] ClassKeys = { "*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive" };
    private static readonly string[] Kinds = { "ContextMenuHandlers", "DragDropHandlers", "PropertySheetHandlers" };

    private static readonly Regex Guid = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");

    public static IReadOnlyList<RegRef> Scan(IRegistryReader reg, CancellationToken ct)
    {
        var refs = new List<RegRef>();
        var h = RegHive.Hklm;
        var approved = new HashSet<string>(reg.ValueNames(h, Approved), StringComparer.OrdinalIgnoreCase);

        foreach (var cls in ClassKeys)
            foreach (var kind in Kinds)
            {
                var root = ClassesRoot + "\\" + cls + @"\shellex\" + kind;
                foreach (var name in reg.SubKeyNames(h, root))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var path = root + "\\" + name;
                        // The CLSID is the handler key's default value, or the key's own name when that is a GUID.
                        var clsid = GuidOf(reg.GetValue(h, path, "")) ?? GuidOf(name);
                        if (clsid is null) continue;

                        var servers = new List<string>();
                        var clsidKeys = new List<string>();
                        foreach (var ck in new[] { ClassesRoot + @"\CLSID\" + clsid, ClassesRoot + @"\WOW6432Node\CLSID\" + clsid })
                        {
                            if (!reg.KeyExists(h, ck)) continue;
                            clsidKeys.Add(ck);
                            if (reg.GetValue(h, ck + @"\InprocServer32", "") is string s && s.Length > 0) servers.Add(s);
                        }
                        if (servers.Count == 0) continue;

                        // The class key "*" is written as the USMT wildcard: it matches the key named "*" and any other class that
                        // carries a handler with this same name (that is the same app's own handler). Only the handler name and the
                        // GUID are checked for special characters.
                        if (!WinPath.PatternSafe(name)) continue;
                        var patterns = new List<string> { @"HKLM\" + path + " [*]", @"HKLM\" + path + @"\* [*]" };
                        foreach (var ck in clsidKeys)
                        {
                            patterns.Add(@"HKLM\" + ck + " [*]");
                            patterns.Add(@"HKLM\" + ck + @"\* [*]");
                        }
                        if (approved.Contains(clsid)) patterns.Add(@"HKLM\" + Approved + " [" + clsid + "]");

                        refs.Add(new RegRef(RefKind.ShellExt, @"HKLM\" + path, null, servers, true)
                        {
                            Patterns = patterns,
                            Label = name,
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* one unreadable handler must not hide the rest */ }
                }
            }
        return refs;
    }

    private static string? GuidOf(object? v)
    {
        var s = (v as string)?.Trim();
        return s is not null && Guid.IsMatch(s) ? s : null;
    }
}
