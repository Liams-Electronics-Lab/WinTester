namespace WinTester.Tests.Cpu;

// CPU vendor as detected via WMI. Kept as informational metadata surfaced in KeyMetrics/the report's machine info only; it does not branch which stress engine runs, since the same IPDT compute modules run on both Intel and AMD (the underlying algorithms are standard x86 instructions with no Intel-specific gating in the compute loop).
public enum CpuVendor
{
    Unknown,
    GenuineIntel,
    AuthenticAmd,
    Other
}
