using System;
using System.Diagnostics;

namespace WinTester.Tools.HardwareErrors;

// Hands work off to the default browser and to Windows' own Event Viewer. Nothing here changes system
// state.
public static class HardwareErrorLauncher
{
    public sealed record LaunchResult(bool Success, string? ErrorMessage)
    {
        public static readonly LaunchResult Ok = new(true, null);
        public static LaunchResult Failed(string message) => new(false, message);
    }

    // Opens a lookup link in the default browser. Only http and https are accepted: the link is built from
    // event text, so a scheme check keeps a malformed one from being handed to the shell as something else.
    public static LaunchResult OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return LaunchResult.Failed("The lookup link is not a valid web address.");

        try
        {
            // UseShellExecute is required on modern .NET for the shell to resolve the default browser.
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return LaunchResult.Ok;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed($"Could not open the browser: {ex.Message}");
        }
    }

    public static LaunchResult OpenEventViewer()
    {
        if (!OperatingSystem.IsWindows())
            return LaunchResult.Failed("Event Viewer is only available on Windows.");

        try
        {
            // mmc.exe with the snap-in named explicitly rather than shell-executing eventvwr.msc, so a
            // broken .msc file association on a repair-bench machine doesn't block it.
            using var process = Process.Start(new ProcessStartInfo("mmc.exe", "eventvwr.msc")
            {
                UseShellExecute = true
            });

            return LaunchResult.Ok;
        }
        catch (Exception ex)
        {
            return LaunchResult.Failed($"Could not open Event Viewer: {ex.Message}");
        }
    }
}
