using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinTester.Tools.DiskInfo;

// TRIM state of Windows. DisableDeleteNotify = 0 means Windows sends TRIM to SSDs. The setting belongs to the
// operating system and the file system type, not to one disk, so the Disk Info window labels it that way.
public static class TrimReader
{
    private static readonly Regex Value = new(@"DisableDeleteNotify\s*=\s*(\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Parses the output of "fsutil behavior query DisableDeleteNotify". Newer Windows prints one line per file
    // system (NTFS, ReFS); the NTFS line decides. Older Windows prints a single line. Returns true when TRIM is
    // enabled, false when disabled, null when the text could not be understood.
    public static bool? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? fallback = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var match = Value.Match(line);
            if (match.Success)
            {
                var enabled = match.Groups[1].Value == "0";
                if (line.StartsWith("NTFS", StringComparison.OrdinalIgnoreCase)) return enabled;
                fallback ??= enabled ? "on" : "off";
            }
            else if (line.StartsWith("NTFS", StringComparison.OrdinalIgnoreCase) &&
                     line.Contains("not currently set", StringComparison.OrdinalIgnoreCase))
            {
                // The setting has never been written, so the Windows default applies: TRIM on.
                return true;
            }
        }

        return fallback is null ? null : fallback == "on";
    }

    public static bool? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var info = new ProcessStartInfo("fsutil.exe", "behavior query DisableDeleteNotify")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(info);
            if (process is null) return null;

            var text = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return Parse(text.GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
