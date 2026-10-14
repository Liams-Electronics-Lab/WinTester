using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WinTester.Tests.Gpu;

// One GPU's entry within a Quick Stats block. FurMark2 lists every detected GPU, not just the one under test; GpuStressTestModule matches Name against the target it asked for, or cross-checks against ISensorService's live load data, to pick the right entry; this type just holds what was parsed.
public sealed class FurmarkQuickStatsGpuEntry
{
    public required int Index { get; init; }
    public string? Name { get; init; }
    public string? PciId { get; init; }
    public double? MaxTemperatureC { get; init; }
    public double? MaxUsagePercent { get; init; }
    public double? MinCoreClockMhz { get; init; }
    public double? MaxCoreClockMhz { get; init; }
}

// Parsed form of FurMark2's end-of-run "[ Demo Quick Stats ]" stdout block. Every field is nullable by design; this is best-effort parsing of an undocumented text format with no JSON output mode, so a future build reordering or renaming a line degrades to a missing field rather than a parse exception.
public sealed class FurmarkQuickStats
{
    public string? Demo { get; init; }
    public string? Renderer { get; init; }
    public string? ApiVersion { get; init; }
    public string? Resolution { get; init; }
    public int? Score { get; init; }
    public double? DurationMs { get; init; }
    public int? FpsMin { get; init; }
    public int? FpsAvg { get; init; }
    public int? FpsMax { get; init; }
    public IReadOnlyList<FurmarkQuickStatsGpuEntry> Gpus { get; init; } = System.Array.Empty<FurmarkQuickStatsGpuEntry>();

    private static readonly Regex HeaderRegex = new(@"\[\s*Demo Quick Stats\s*\]", RegexOptions.Compiled);
    private static readonly Regex FieldRegex = new(@"^-\s*([A-Za-z0-9 /()]+?)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex GpuHeaderRegex = new(@"^-\s*GPU\s+(\d+)\s*:\s*(.+?)\s*(\[([0-9A-Fa-f]{4}-[0-9A-Fa-f]{4})\])?\s*$", RegexOptions.Compiled);
    private static readonly Regex GpuSubFieldRegex = new(@"^\.\s*([A-Za-z0-9 ]+?)\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"-?\d+(\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex FpsRegex = new(@"(\d+)\s*/\s*(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    // Returns null if no "[ Demo Quick Stats ]" header appears in output; FurmarkRunner treats that as "no stats available," same as a launch failure or a crash before the block could print.
    public static FurmarkQuickStats? TryParse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = output.Replace("\r\n", "\n").Split('\n');
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (HeaderRegex.IsMatch(lines[i])) { headerIndex = i; break; }
        }
        if (headerIndex < 0) return null;

        string? demo = null, renderer = null, apiVersion = null, resolution = null;
        int? score = null, fpsMin = null, fpsAvg = null, fpsMax = null;
        double? durationMs = null;
        var gpus = new List<FurmarkQuickStatsGpuEntry>();
        int currentGpuIndex = -1;
        string? currentGpuName = null, currentGpuPci = null;
        double? currentMaxTemp = null, currentMaxUsage = null, currentMinClock = null, currentMaxClock = null;

        void FlushCurrentGpu()
        {
            if (currentGpuIndex < 0) return;
            gpus.Add(new FurmarkQuickStatsGpuEntry
            {
                Index = currentGpuIndex,
                Name = currentGpuName,
                PciId = currentGpuPci,
                MaxTemperatureC = currentMaxTemp,
                MaxUsagePercent = currentMaxUsage,
                MinCoreClockMhz = currentMinClock,
                MaxCoreClockMhz = currentMaxClock
            });
            currentGpuIndex = -1;
            currentGpuName = null;
            currentGpuPci = null;
            currentMaxTemp = null;
            currentMaxUsage = null;
            currentMinClock = null;
            currentMaxClock = null;
        }

        // Cap the scan window; the block itself is only a dozen-ish lines, but stdout keeps flowing after it (shutdown/log-flush chatter), so an unbounded scan risks false-matching something unrelated later in the log.
        int maxLinesToScan = System.Math.Min(lines.Length, headerIndex + 60);
        for (int i = headerIndex + 1; i < maxLinesToScan; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0 && currentGpuIndex < 0 && gpus.Count == 0 && score is null)
                continue; // tolerate a blank line or two right after the header before fields start

            var gpuHeaderMatch = GpuHeaderRegex.Match(line);
            if (gpuHeaderMatch.Success)
            {
                FlushCurrentGpu();
                currentGpuIndex = int.Parse(gpuHeaderMatch.Groups[1].Value);
                currentGpuName = gpuHeaderMatch.Groups[2].Value.Trim();
                currentGpuPci = gpuHeaderMatch.Groups[4].Success ? gpuHeaderMatch.Groups[4].Value : null;
                continue;
            }

            if (currentGpuIndex >= 0)
            {
                var subMatch = GpuSubFieldRegex.Match(line);
                if (subMatch.Success)
                {
                    var key = subMatch.Groups[1].Value.Trim().ToLowerInvariant();
                    var value = subMatch.Groups[2].Value.Trim();
                    var num = NumberRegex.Match(value);
                    double? parsed = num.Success ? double.Parse(num.Value) : null;

                    switch (key)
                    {
                        case "max temperature": currentMaxTemp = parsed; continue;
                        case "max usage": currentMaxUsage = parsed; continue;
                        case "min core clock": currentMinClock = parsed; continue;
                        case "max core clock": currentMaxClock = parsed; continue;
                    }
                    continue;
                }
            }

            var fieldMatch = FieldRegex.Match(line);
            if (fieldMatch.Success)
            {
                FlushCurrentGpu(); // a top-level "- key: value" line ends any GPU sub-block we were building
                var key = fieldMatch.Groups[1].Value.Trim().ToLowerInvariant();
                var value = fieldMatch.Groups[2].Value.Trim();

                switch (key)
                {
                    case "demo": demo = value; break;
                    case "renderer": renderer = value; break;
                    case "3d api": apiVersion = value; break;
                    case "resolution": resolution = value; break;
                    case "score":
                        var scoreMatch = NumberRegex.Match(value);
                        if (scoreMatch.Success) score = int.Parse(scoreMatch.Value);
                        break;
                    case "duration":
                        var durMatch = NumberRegex.Match(value);
                        if (durMatch.Success) durationMs = double.Parse(durMatch.Value);
                        break;
                    case "fps (min/avg/max)":
                        var fpsMatch = FpsRegex.Match(value);
                        if (fpsMatch.Success)
                        {
                            fpsMin = int.Parse(fpsMatch.Groups[1].Value);
                            fpsAvg = int.Parse(fpsMatch.Groups[2].Value);
                            fpsMax = int.Parse(fpsMatch.Groups[3].Value);
                        }
                        break;
                }
                continue;
            }

            // Unmatched line (blank, or block ended): once a score has been parsed, treat this as the end rather than scanning the rest of stdout.
            if (line.Length == 0 && score is not null)
                break;
        }

        FlushCurrentGpu();

        return new FurmarkQuickStats
        {
            Demo = demo,
            Renderer = renderer,
            ApiVersion = apiVersion,
            Resolution = resolution,
            Score = score,
            DurationMs = durationMs,
            FpsMin = fpsMin,
            FpsAvg = fpsAvg,
            FpsMax = fpsMax,
            Gpus = gpus
        };
    }
}
