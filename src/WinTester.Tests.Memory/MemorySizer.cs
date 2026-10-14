using System;
using System.Runtime.Versioning;

namespace WinTester.Tests.Memory;

// One computed answer for how much RAM the memtest engine can safely claim right now.
public sealed record MemoryTestSizing(
    long TotalPhysicalMb,
    long FreePhysicalMb,
    long RecommendedTestMb,
    // Human-readable derivation (or reason for a 0/clamped result) surfaced in the report/failure text instead of a bare number.
    string Explanation);

// Computes how much RAM the memory test can safely claim: reserve the larger of ReserveFloorMb or ReservePercent of total RAM for the OS/other processes, offer whatever's left of currently free memory as the coverage total for the whole run. Degrades to a zero-size, explained result on any WMI failure or non-Windows host rather than throwing; same posture as DiskInfoProvider and every other real-hardware-facing provider. Reserve values are a placeholder default, not validated against real low-RAM (4-8 GB) hardware, where an 800 MB floor is proportionally far more aggressive than on this project's 32 GB reference machine.
public static class MemorySizer
{
    public const long ReserveFloorMb = 800;
    public const double ReservePercent = 0.02;

    // Floor below which a run isn't meaningful; more likely signals the reserve policy needs revisiting on that machine than that a tiny test should proceed.
    public const long MinimumMeaningfulTestMb = 256;

    // Re-queries free memory fresh (not the ComputeSafeTestSize snapshot from test start) so MemoryTestEngine can check headroom before every window rather than trusting one stale start-of-run figure for a long run. Issues a fresh WMI query per call; fine at once-per-window cadence, not per-byte.
    public static bool HasHeadroomForWindow(int windowMb) => ComputeSafeTestSize().RecommendedTestMb >= windowMb;

    public static MemoryTestSizing ComputeSafeTestSize()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MemoryTestSizing(0, 0, 0,
                "Not running on Windows; WMI memory sizing is skipped entirely on this platform.");
        }

        try
        {
            return ComputeViaWmi();
        }
        catch (Exception ex)
        {
            // Degrades to "nothing safe to test" instead of letting a WMI hiccup crash the memory test outright.
            return new MemoryTestSizing(0, 0, 0,
                $"Win32_OperatingSystem memory query threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static MemoryTestSizing ComputeViaWmi()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            // Win32_OperatingSystem reports both fields in KB, not bytes.
            long totalKb = obj["TotalVisibleMemorySize"] is { } t ? Convert.ToInt64(t) : 0;
            long freeKb = obj["FreePhysicalMemory"] is { } f ? Convert.ToInt64(f) : 0;

            if (totalKb <= 0)
            {
                return new MemoryTestSizing(0, 0, 0,
                    "Win32_OperatingSystem returned a zero/missing TotalVisibleMemorySize: cannot size a test.");
            }

            long totalMb = totalKb / 1024;
            long freeMb = freeKb / 1024;

            long reserveMb = Math.Max(ReserveFloorMb, (long)(totalMb * ReservePercent));
            long candidateMb = freeMb - reserveMb;

            if (candidateMb < MinimumMeaningfulTestMb)
            {
                return new MemoryTestSizing(totalMb, freeMb, 0,
                    $"Only {freeMb} MB free of {totalMb} MB total, and the {reserveMb} MB reserve " +
                    $"(max of {ReserveFloorMb} MB floor / {ReservePercent:P0} of total) leaves less than " +
                    $"the {MinimumMeaningfulTestMb} MB minimum meaningful test size: skipping the memory test " +
                    "rather than running a token-sized pass. Close some applications and retry, or lower " +
                    "the reserve policy for this machine.");
            }

            return new MemoryTestSizing(totalMb, freeMb, candidateMb,
                $"{candidateMb} MB");
        }

        return new MemoryTestSizing(0, 0, 0,
            "Win32_OperatingSystem query returned zero rows: WMI/the Winmgmt service may be unavailable.");
    }
}
