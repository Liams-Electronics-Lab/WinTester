using System.Text.RegularExpressions;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// COM registration (S123, from the EaseUS rule files, C48): an app with a COM server (Google Update, an updater service,
// a shell or Office-style add-in, an automation exe) registers CLSID, ProgID, AppID, TypeLib and Interface keys under
// HKLM\SOFTWARE\Classes. The Classes pass of the app scan skips all of those (C36), so such an app came back after Reset
// listed and runnable but with its COM classes unregistered.
//
// A class belongs to an app when its server (InprocServer32, LocalServer32, or the CodeBase of a managed server) sits
// inside the app's install folder; AppFootprint judges that with the same folder test as every other registration.
// Servers under a Windows folder are never indexed (thousands of system classes, never an app's own).
// Per class the rules carry, only for keys that exist: the CLSID key (64-bit and WOW6432Node view), its AppID key
// (by GUID, and the key named after the server exe when it points back at the same AppID), its TypeLib key, every
// Interface key that names that type library or has the class as proxy/stub, and its ProgID, version-independent
// ProgID and the ProgID that CurVer points at. Nothing here has run on Windows.
public static class ComRules
{
    private const string ClassesRoot = @"SOFTWARE\Classes";
    private static readonly string[] ClsidRoots = { ClassesRoot + @"\CLSID", ClassesRoot + @"\WOW6432Node\CLSID" };
    private static readonly string[] InterfaceRoots = { ClassesRoot + @"\Interface", ClassesRoot + @"\WOW6432Node\Interface" };
    private static readonly string[] TypeLibRoots = { ClassesRoot + @"\TypeLib", ClassesRoot + @"\WOW6432Node\TypeLib" };
    private static readonly string[] AppIdRoots = { ClassesRoot + @"\AppID", ClassesRoot + @"\WOW6432Node\AppID" };

    // Interface keys carried for one class; a larger set is cut (an automation-heavy app can register hundreds).
    internal const int MaxInterfacesPerClass = 400;

    private static readonly Regex Guid = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
    // A server under any drive's Windows folder, or written with the system-folder variables.
    private static readonly Regex WindowsServer = new(@"^""?(%systemroot%|%windir%|[A-Za-z]:\\windows\\)", RegexOptions.IgnoreCase);

    public static IReadOnlyList<RegRef> Scan(IRegistryReader reg, CancellationToken ct, int maxClsids = 100_000)
    {
        var refs = new List<RegRef>();
        var h = RegHive.Hklm;

        // Built on first use: which Interface keys name a type library, and which use a class as proxy/stub.
        Dictionary<string, List<string>>? byTypeLib = null, byProxy = null;
        void BuildInterfaces()
        {
            byTypeLib = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            byProxy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in InterfaceRoots)
                foreach (var iid in reg.SubKeyNames(h, root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Guid.IsMatch(iid)) continue;
                    try
                    {
                        var path = root + "\\" + iid;
                        var tl = GuidOf(reg.GetValue(h, path + @"\TypeLib", ""));
                        var ps = GuidOf(reg.GetValue(h, path + @"\ProxyStubClsid32", ""));
                        if (tl is not null) Add(byTypeLib, tl, path);
                        if (ps is not null) Add(byProxy, ps, path);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* one unreadable interface must not hide the rest */ }
                }
        }

        foreach (var root in ClsidRoots)
        {
            var count = 0;
            foreach (var g in reg.SubKeyNames(h, root))
            {
                ct.ThrowIfCancellationRequested();
                if (!Guid.IsMatch(g)) continue;
                if (++count > maxClsids) break;
                try
                {
                    var ck = root + "\\" + g;
                    var servers = Servers(reg, ck);
                    if (servers.Count == 0) continue;

                    if (byTypeLib is null) BuildInterfaces();
                    var patterns = new List<string>();
                    void Tree(string rel)
                    {
                        if (!reg.KeyExists(h, rel)) return;
                        patterns.Add(@"HKLM\" + rel + " [*]");
                        if (reg.SubKeyNames(h, rel).Count > 0) patterns.Add(@"HKLM\" + rel + @"\* [*]");
                    }

                    patterns.Add(@"HKLM\" + ck + " [*]");
                    patterns.Add(@"HKLM\" + ck + @"\* [*]");

                    // AppID: by GUID, and the key named after the server exe when it points back at the same AppID
                    var appId = GuidOf(reg.GetValue(h, ck, "AppID"));
                    if (appId is not null)
                    {
                        foreach (var ar in AppIdRoots) Tree(ar + "\\" + appId);
                        foreach (var exe in ExeNames(servers))
                        {
                            if (!WinPath.PatternSafe(exe)) continue;
                            foreach (var ar in AppIdRoots)
                                if (reg.KeyExists(h, ar + "\\" + exe)
                                    && string.Equals(GuidOf(reg.GetValue(h, ar + "\\" + exe, "AppID")), appId, StringComparison.OrdinalIgnoreCase))
                                    Tree(ar + "\\" + exe);
                        }
                    }

                    // TypeLib and the interfaces that use it or this class as proxy/stub
                    var interfaces = new List<string>();
                    var tlb = GuidOf(reg.GetValue(h, ck + @"\TypeLib", ""));
                    if (tlb is not null)
                    {
                        foreach (var tr in TypeLibRoots) Tree(tr + "\\" + tlb);
                        if (byTypeLib!.TryGetValue(tlb, out var il)) interfaces.AddRange(il);
                    }
                    if (byProxy!.TryGetValue(g, out var pl)) interfaces.AddRange(pl);
                    foreach (var ip in interfaces.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxInterfacesPerClass)) Tree(ip);

                    // ProgIDs
                    var progIds = new List<string>();
                    void ProgId(object? v)
                    {
                        var s = (v as string)?.Trim();
                        if (string.IsNullOrEmpty(s) || s.StartsWith('.') || s.Contains('\\') || !WinPath.PatternSafe(s)) return;
                        if (!progIds.Contains(s, StringComparer.OrdinalIgnoreCase)) progIds.Add(s);
                    }
                    ProgId(reg.GetValue(h, ck + @"\ProgID", ""));
                    ProgId(reg.GetValue(h, ck + @"\VersionIndependentProgID", ""));
                    foreach (var vi in progIds.ToList()) ProgId(reg.GetValue(h, ClassesRoot + "\\" + vi + @"\CurVer", ""));
                    foreach (var pid in progIds) Tree(ClassesRoot + "\\" + pid);

                    var name = (reg.GetValue(h, ck, "") as string)?.Trim();
                    refs.Add(new RegRef(RefKind.Com, @"HKLM\" + ck, null, servers, true)
                    {
                        Patterns = patterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        Label = string.IsNullOrEmpty(name) ? g : name,
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch { /* one unreadable class must not hide the rest */ }
            }
        }
        return refs;
    }

    // The server paths of a class that are not under a Windows folder: InprocServer32, LocalServer32, and the CodeBase of a
    // managed server (its default value is just mscoree.dll, which has no path and is dropped). Values are kept as written;
    // AppFootprint expands variables and compares against the app folder.
    private static List<string> Servers(IRegistryReader reg, string ck)
    {
        var h = RegHive.Hklm;
        var list = new List<string>();
        void Add(object? v)
        {
            var s = (v as string)?.Trim();
            if (string.IsNullOrEmpty(s) || WindowsServer.IsMatch(s)) return;
            if (s.IndexOfAny(new[] { '\\', '/' }) < 0) return;     // a bare file name such as mscoree.dll
            list.Add(s);
        }
        foreach (var kind in new[] { "InprocServer32", "LocalServer32" })
        {
            var sk = ck + "\\" + kind;
            if (!reg.KeyExists(h, sk)) continue;
            Add(reg.GetValue(h, sk, ""));
            Add(reg.GetValue(h, sk, "CodeBase"));
            foreach (var ver in reg.SubKeyNames(h, sk)) Add(reg.GetValue(h, sk + "\\" + ver, "CodeBase"));
        }
        return list;
    }

    // File names of the .exe servers (the command line may be quoted and carry arguments).
    private static IEnumerable<string> ExeNames(IEnumerable<string> servers)
    {
        foreach (var raw in servers)
        {
            var s = raw.Trim();
            string path;
            if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); path = end > 0 ? s[1..end] : s[1..]; }
            else
            {
                var i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = i >= 0 ? s[..(i + 4)] : s;
            }
            var slash = path.LastIndexOfAny(new[] { '\\', '/' });
            var file = slash >= 0 ? path[(slash + 1)..] : path;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) yield return file;
        }
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var l)) map[key] = l = new List<string>();
        l.Add(value);
    }

    private static string? GuidOf(object? v)
    {
        var s = (v as string)?.Trim();
        return s is not null && Guid.IsMatch(s) ? s : null;
    }
}
