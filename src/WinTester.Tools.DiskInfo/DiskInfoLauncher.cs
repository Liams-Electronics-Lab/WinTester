using System.Diagnostics;

namespace WinTester.Tools.DiskInfo;

// Hands work off to Windows' own Disk Management and to the default browser. Nothing here changes a disk: every
// partition change is left to Disk Management.
public static class DiskInfoLauncher
{
    public sealed record LaunchResult(bool Success, string? ErrorMessage)
    {
        public static readonly LaunchResult Ok = new(true, null);
        public static LaunchResult Failed(string message) => new(false, message);
    }

    public static LaunchResult OpenDiskManagement()
    {
        if (!OperatingSystem.IsWindows())
            return LaunchResult.Failed("Disk Management is only available on Windows.");

        try
        {
            // mmc.exe with the snap-in named explicitly rather than shell-executing diskmgmt.msc, so a broken .msc
            // file association on a repair-bench machine does not block it.
            using var process = Process.Start(new ProcessStartInfo("mmc.exe", "diskmgmt.msc") { UseShellExecute = true });
            return LaunchResult.Ok;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed($"Could not open Disk Management: {ex.Message}");
        }
    }

    // Opens a web search in the default browser. Only http and https are accepted.
    public static LaunchResult OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return LaunchResult.Failed("The search link is not a valid web address.");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return LaunchResult.Ok;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed($"Could not open the browser: {ex.Message}");
        }
    }
}
