namespace WinTester.Tools.RestorePoints;

// Maps the numeric RestorePointType reported by Windows to a display name.
// Numbers follow the System Restore API constants. Confirm against a real list (tracker O5).
public static class RestorePointTypes
{
    public const uint ModifySettings = 12;

    // Created by the scheduled System Restore task.
    public const uint SystemCheckpoint = 7;

    private static readonly Dictionary<uint, string> Names = new()
    {
        [0] = "Application Install",
        [1] = "Application Uninstall",
        [2] = "Desktop Setting",
        [3] = "Accessibility Setting",
        [4] = "Outlook Express Setting",
        [5] = "Application Run",
        [6] = "Restore",
        [7] = "System Checkpoint",
        [8] = "Windows Shutdown",
        [9] = "Windows Boot",
        [10] = "Device Driver Install",
        [11] = "Application First Run",
        [12] = "Modify Settings",
        [13] = "Cancelled Operation",
        [14] = "Backup Recovery",
        [15] = "Backup Restore",
        [16] = "Manual",
        [17] = "Windows Update",
        [18] = "Critical Update",
    };

    public static string NameOf(uint code) =>
        Names.TryGetValue(code, out var name) ? name : $"Other ({code})";
}
