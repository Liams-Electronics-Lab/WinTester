using System;
using System.Linq;

namespace WinTester.Tests.Gpu;

// Extracted from GpuStressTestModule (GravityMark API attempt ordering) so PowerCalibrationRunner can share the same heuristic for its iGPU-if-dGPU-present toggle instead of duplicating the name list. Matches on free-text sensor/driver name; good enough to separate "probably integrated" from "probably dedicated" for UI/planning, not a hardware-ID ground truth.
public static class GpuNameClassifier
{
    private static readonly string[] IntegratedGpuNameHints =
    {
        "UHD Graphics", "HD Graphics", "Iris", "Radeon Graphics", "Radeon(TM) Graphics", "Vega"
    };

    public static bool IsLikelyIntegratedGpuName(string? name) =>
        name is { Length: > 0 } n && IntegratedGpuNameHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase));
}
