using System.Text;

namespace WinTester.Tools.RestorePoints;

// Locates Windows PowerShell 5.1 and encodes fixed scripts for it. Scripts are constants,
// so no user text reaches the command line.
internal static class PowerShellHost
{
    public const string BaseArguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass";

    // Null when Windows PowerShell is not installed.
    public static string? Exe()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem
            ? Path.Combine(windows, "Sysnative")
            : Environment.SystemDirectory;
        var exe = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
