using System.Management;
using System.Runtime.Versioning;

namespace WinTester.Tools.DiskInfo;

// Small helpers over System.Management so the readers stay short. Every ManagementObject is disposed here.
[SupportedOSPlatform("windows")]
internal static class WmiQuery
{
    public const string StorageScope = @"root\Microsoft\Windows\Storage";
    public const string WmiScope = @"root\wmi";
    public const string CimScope = @"root\cimv2";

    public static void Each(string scope, string wql, Action<ManagementObject> action)
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using var obj = (ManagementObject)item;
            action(obj);
        }
    }

    public static string Str(ManagementBaseObject o, string name)
    {
        try { return o[name]?.ToString()?.Trim() ?? ""; }
        catch (ManagementException) { return ""; }
    }

    public static ulong? U64(ManagementBaseObject o, string name)
    {
        try
        {
            var v = o[name];
            return v is null ? null : Convert.ToUInt64(v);
        }
        catch (Exception ex) when (ex is ManagementException or FormatException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    public static int? Int(ManagementBaseObject o, string name)
    {
        try
        {
            var v = o[name];
            return v is null ? null : Convert.ToInt32(v);
        }
        catch (Exception ex) when (ex is ManagementException or FormatException or OverflowException or InvalidCastException)
        {
            return null;
        }
    }

    public static bool? Bool(ManagementBaseObject o, string name)
    {
        try
        {
            var v = o[name];
            return v is null ? null : Convert.ToBoolean(v);
        }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    public static string[] StrArray(ManagementBaseObject o, string name)
    {
        try { return o[name] is string[] a ? a : Array.Empty<string>(); }
        catch (ManagementException) { return Array.Empty<string>(); }
    }

    public static byte[]? Bytes(ManagementBaseObject o, string name)
    {
        try { return o[name] as byte[]; }
        catch (ManagementException) { return null; }
    }
}
