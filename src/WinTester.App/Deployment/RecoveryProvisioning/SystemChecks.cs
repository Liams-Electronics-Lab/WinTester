using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace WinTester.App.Deployment.RecoveryProvisioning;

// Small read-only machine checks for the wizard (task 2.3). No WPF; none of these write anything.

public static class ElevationCheck
{
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

public sealed record DiskSpaceResult(long FreeBytes, bool Enough);

public static class DiskSpaceCheck
{
    // Hard floor (C13). It is a minimum, not an estimate of the package size.
    public static long MinFreeBytes => DeploymentSnapshot.MinFreeBytes;

    public static long ReadFreeBytes(string systemDrive)
    {
        try { return new DriveInfo(systemDrive).AvailableFreeSpace; }
        catch { return 0; }
    }

    public static DiskSpaceResult Check(string systemDrive)
    {
        var free = ReadFreeBytes(systemDrive);
        return new DiskSpaceResult(free, free >= MinFreeBytes);
    }
}

public sealed record WinReResult(WinReState State, string Detail);

public static class WinReCheck
{
    // Runs "reagentc /info" (C10). Output is localized, so the parse does not rely on English words alone.
    // Blocking call of about a second: run it off the UI thread.
    public static WinReResult Read()
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "reagentc.exe");
            if (!File.Exists(exe))
            {
                // A 32-bit process on 64-bit Windows is redirected to SysWOW64; Sysnative reaches the real System32.
                var alt = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Sysnative", "reagentc.exe");
                if (File.Exists(alt)) exe = alt;
                else return new WinReResult(WinReState.Unknown, "reagentc.exe was not found");
            }

            var psi = new ProcessStartInfo(exe, "/info")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return new WinReResult(WinReState.Unknown, "reagentc.exe could not be started");

            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                return new WinReResult(WinReState.Unknown, "reagentc /info timed out");
            }
            return Parse(stdout.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new WinReResult(WinReState.Unknown, ex.Message);
        }
    }

    // Pure parse of the reagentc /info text, kept separate so it can be checked without running the tool.
    // 1) A "Windows RE location" style line whose value points at a real device/path means WinRE is registered and
    //    enabled (reagentc clears the location when it is disabled). This works in any UI language because the value
    //    is a device path, not a word.
    // 2) Otherwise the status line value is matched against English "Enabled"/"Disabled".
    // 3) Anything else is Unknown (shown as a warning, never as a block).
    public static WinReResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new WinReResult(WinReState.Unknown, "no output");

        string? statusValue = null;
        var locationLooksReal = false;
        var seenPairs = 0;

        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = raw.IndexOf(':');
            if (idx <= 0) continue;
            var value = raw[(idx + 1)..].Trim();
            if (value.Length == 0) continue; // header line ("Information:") or a cleared location
            seenPairs++;

            if (seenPairs == 1) statusValue = value; // first "label: value" line is the status line

            if (value.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase)
                || value.Contains(@"\Recovery\WindowsRE", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(@"\\?\", StringComparison.Ordinal))
                locationLooksReal = true;
        }

        if (locationLooksReal) return new WinReResult(WinReState.Enabled, "registered");
        if (statusValue is not null)
        {
            if (statusValue.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                return new WinReResult(WinReState.Enabled, "Enabled");
            if (statusValue.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                return new WinReResult(WinReState.Disabled, "Disabled");
        }
        return new WinReResult(WinReState.Unknown, "status not recognised");
    }
}

public sealed record ProgramsEstimate(int Count, long Bytes);

// Informational only (C13): the "size" installed programs report about themselves. Many entries report none, so this
// is a rough lower bound and is never used to allow or block anything.
public static class InstalledProgramsEstimate
{
    private static readonly string[] UninstallPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    public static ProgramsEstimate Read()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long kb = 0;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var path in UninstallPaths)
            {
                using var root = hklm.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k is null) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") is not null) continue; // update entries
                        var display = k.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display)) continue;
                        if (!seen.Add(display.Trim())) continue;
                        if (k.GetValue("EstimatedSize") is int size && size > 0) kb += size;
                    }
                    catch { /* one unreadable entry must not hide the rest */ }
                }
            }
        }
        catch { /* registry unreadable: report what was counted so far */ }
        return new ProgramsEstimate(seen.Count, kb * 1024);
    }
}
