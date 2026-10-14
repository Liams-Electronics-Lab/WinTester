using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinTester.Tools.RestorePoints;

// DWORD access to the System Restore policy key. Writing needs administrator rights.
[SupportedOSPlatform("windows")]
internal static class SrRegistry
{
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    // Seconds between automatic restore points.
    public const string IntervalValue = "RPGlobalInterval";

    // Seconds a restore point is kept.
    public const string LifetimeValue = "RPLifeInterval";

    // Minutes between manual restore points (Windows 8 and later). 0 removes the limit.
    public const string FrequencyValue = "SystemRestorePointCreationFrequency";

    public static uint? ReadDword(string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(name) is int raw ? unchecked((uint)raw) : null;
    }

    public static void WriteDword(string name, uint value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, unchecked((int)value), RegistryValueKind.DWord);
    }

    public static void DeleteValue(string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
