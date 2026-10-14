using System;
using System.Runtime.Versioning;

namespace WinTester.Tests.Cpu;

// Reads the CPU vendor string via WMI's Win32_Processor.Manufacturer, chosen over raw CPUID since WMI needs no P/Invoke/unsafe code. Purely informational: the same IPDT compute modules run on both Intel and AMD, since the underlying algorithms are standard x86 instructions with no Intel-specific gating in the compute loop, so vendor detection doesn't branch which engine runs. Degrades gracefully to CpuVendor.Unknown on any failure (WMI service unavailable, insufficient permissions, non-Windows host) rather than throwing, since vendor identity is never something the CPU test itself should fail over.
public static class CpuVendorDetector
{
    public static CpuVendor Detect()
    {
        if (!OperatingSystem.IsWindows())
            return CpuVendor.Unknown;

        try
        {
            return DetectViaWmi();
        }
        catch
        {
            return CpuVendor.Unknown;
        }
    }

    [SupportedOSPlatform("windows")]
    private static CpuVendor DetectViaWmi()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Manufacturer FROM Win32_Processor");

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            var manufacturer = obj["Manufacturer"] as string;
            if (string.IsNullOrWhiteSpace(manufacturer))
                continue;

            if (manufacturer.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase))
                return CpuVendor.GenuineIntel;
            if (manufacturer.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase))
                return CpuVendor.AuthenticAmd;

            return CpuVendor.Other;
        }

        return CpuVendor.Unknown;
    }
}
