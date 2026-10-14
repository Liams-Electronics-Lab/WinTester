using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tests.Gpu;

// One adapter reported by furmark --gpuinfo. Index is FurMark's/Vulkan's own device numbering, passed straight to --gpu-index; it has no guaranteed relationship to ISensorService's LHM enumeration order, so never cross-wire the two without a name-based cross-check.
public sealed class FurmarkGpuTarget
{
    public required int Index { get; init; }
    public string? Name { get; init; }

    // True when Name matches a known software-rasterizer pattern ("Basic Render Driver", "WARP"); the sweep excludes these unless they're the only targets found, in which case one still gets a low-value, non-crashing pass.
    public bool IsSoftwareRenderer { get; init; }
}

// ProbeAsync's result: the parsed target list plus the verbatim raw stdout/stderr that produced it. See
// ProbeAsync's doc comment for why RawOutput exists — no real sample of --gpuinfo's output has ever been
// captured, which is exactly why field-issues tracker 2.3's name-matching gap can't yet be root-caused.
public sealed record FurmarkGpuProbeResult(IReadOnlyList<FurmarkGpuTarget> Targets, string RawOutput);


// Probes FurMark2's own --gpuinfo output to build the Vulkan per-adapter sweep list, deliberately independent of ISensorService's GPU enumeration: FurMark's/Vulkan's own device numbering has no guaranteed relationship to LHM's enumeration order, so the two lists can't be assumed to agree on indexing. No real sample of --gpuinfo's stdout exists, so parsing is loose and defensive across several candidate line shapes and fails soft: an unrecognized result returns an empty list, which GpuStressTestModule treats as "fall back to one implicit Vulkan pass with no --gpu-index." Tighten GpuLineRegexes once a real sample surfaces.
public static class FurmarkGpuProbe
{
    // Candidate line shapes for one GPU entry in --gpuinfo's stdout, tried in order; written against the "- key : value" / "GPU N: name" conventions seen elsewhere since no direct sample exists for this flag.
    private static readonly Regex[] GpuLineRegexes =
    {
        // "- GPU 0: NVIDIA GeForce RTX 3060 [10DE-2504]" or "- GPU 0 : Intel(R) Iris(R) Xe Graphics"
        new(@"^-?\s*GPU\s*(\d+)\s*:\s*(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // "[0] NVIDIA GeForce RTX 3060" [a bare indexed-list style, seen in some GeeXLab/Vulkan sample
        // tools' --list-style output.
        new(@"^\[(\d+)\]\s*(.+?)\s*$", RegexOptions.Compiled),
        // "0: NVIDIA GeForce RTX 3060"] plainest possible fallback shape.
        new(@"^(\d+)\s*:\s*(.+?)\s*$", RegexOptions.Compiled),
    };

    private static readonly string[] SoftwareRendererNamePatterns =
    {
        "Basic Render Driver",
        "Microsoft Basic Render Driver",
        "WARP"
    };

    // Runs furmark --gpuinfo (a quick print-and-exit, no window/render context) and parses stdout into an ordered target list. Never throws for a missing furmark.exe or unparseable output; both degrade to an empty list, which BuildRunPlanAsync treats as "fall back to one no-index Vulkan pass" rather than failing the GPU test over bonus data.
    //
    // Also returns the raw stdout/stderr verbatim (RawOutput), even on a successful parse: no real sample
    // of --gpuinfo's output has ever been captured (see the class doc comment and field-issues tracker
    // 1.2/2.3's "Intel(R) HD Graphics 5500 detected by sensors but not matched" finding), so the only way
    // to eventually tighten GpuLineRegexes against real hardware is for a field run to actually keep a copy
    // of what --gpuinfo printed. Callers should log this into their raw diagnostic log unconditionally, not
    // only when a mismatch note fires, since a clean-looking parse that's still silently wrong (matches the
    // regex but not the sensor-reported name) is exactly the case a mismatch note won't catch either.
    public static async Task<FurmarkGpuProbeResult> ProbeAsync(
        string vendorDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var exePath = Path.Combine(vendorDirectory, "furmark.exe");
        if (!File.Exists(exePath))
            return new FurmarkGpuProbeResult(Array.Empty<FurmarkGpuTarget>(), "");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = vendorDirectory,
            Arguments = "--gpuinfo",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch
        {
            // Launch failure is as non-fatal as an unparseable result: both mean "no sweep list."
            return new FurmarkGpuProbeResult(Array.Empty<FurmarkGpuTarget>(), "");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested(); // real operator cancel, propagate

            // Otherwise this is our own timeout, not the caller's: best-effort kill, treat as no usable output,
            // but still return whatever partial stdout arrived before the timeout for diagnosis.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return new FurmarkGpuProbeResult(Array.Empty<FurmarkGpuTarget>(), output.ToString());
        }

        var raw = output.ToString();
        return new FurmarkGpuProbeResult(ParseGpuInfoOutput(raw), raw);
    }

    // Parses --gpuinfo's stdout into an ordered, de-duplicated (by index) target list. Internal rather than private so it can be unit-tested against a captured real sample once one exists.
    internal static IReadOnlyList<FurmarkGpuTarget> ParseGpuInfoOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Array.Empty<FurmarkGpuTarget>();

        var seen = new SortedDictionary<int, string>();
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            foreach (var regex in GpuLineRegexes)
            {
                var m = regex.Match(line);
                if (!m.Success) continue;

                if (!int.TryParse(m.Groups[1].Value, out var index)) continue;

                // Strip a trailing "[XXXX-XXXX]" PCI-ID suffix if present, same convention as the Quick Stats GPU header, to keep Name a clean display string.
                var name = Regex.Replace(m.Groups[2].Value, @"\s*\[[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}\]\s*$", "").Trim();
                if (name.Length == 0) continue;

                // First regex to match a line wins; first line to name a given index wins, in case --gpuinfo repeats an index in a later summary section.
                if (!seen.ContainsKey(index))
                    seen[index] = name;
                break;
            }
        }

        return seen
            .Select(kv => new FurmarkGpuTarget
            {
                Index = kv.Key,
                Name = kv.Value,
                IsSoftwareRenderer = SoftwareRendererNamePatterns.Any(p =>
                    kv.Value.Contains(p, StringComparison.OrdinalIgnoreCase))
            })
            .ToList();
    }

    // Excludes software-renderer targets from the sweep unless they're the only ones found; a machine with no real GPU driver still gets one low-value, non-crashing pass rather than the sweep silently doing nothing.
    public static IReadOnlyList<FurmarkGpuTarget> ApplySoftwareRendererFilter(IReadOnlyList<FurmarkGpuTarget> targets)
    {
        if (targets.Count == 0) return targets;
        var real = targets.Where(t => !t.IsSoftwareRenderer).ToList();
        return real.Count > 0 ? real : targets;
    }
}
