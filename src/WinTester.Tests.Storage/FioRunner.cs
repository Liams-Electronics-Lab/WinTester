using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tests.Storage;

public enum FioPhase { Write, Read }

// Which of the two KDiskMark/CrystalDiskMark-standard benchmark profiles a pass runs; Seq1M Q8T1 and Rand4K Q1T1, the two profiles CrystalDiskMark's own default view leads with. Seq1M Q1T1 and Rand4K Q32T16 remain unimplemented roadmap items.
public enum FioProfile
{
    // Sequential 1MiB blocks, queue depth 8, 1 thread, large-I/O "best case" throughput.
    Seq1MQ8T1,
    // Random 4KiB blocks, queue depth 1, 1 thread; small-I/O "worst case" throughput/IOPS; tracks everyday desktop snappiness better than the larger Seq1M Q8T1 figure does.
    Rand4KQ1T1
}

// Drives fio.exe directly rather than embedding KDiskMark's Qt GUI, process-isolated since fio is GPLv2. Runs either FioProfile; the other two roadmap profiles (Seq1M Q1T1, Rand4K Q32T16) aren't implemented here.
// Targets an ordinary file, not the raw physical device; \\.\PhysicalDriveN needs elevated raw-disk access and risks the partition table on a bad index, the same reasoning DiskInfoProvider applies to resolving lettered volumes only. A single test file (default 2GiB, see DefaultTestFileSizeBytes) is created on the target volume, benchmarked for DefaultLoopCount loops via fio's --loops, then deleted.
// Live speed comes from polling GetProcessIoCounters, not fio's --status-interval: an earlier version trusted --status-interval + --output-format=json for periodic stdout snapshots, but a real multi-loop run showed exactly one stdout update per phase, arriving as the phase finished [matching fio's own documented gap where periodic status dumps are suppressed in some builds/formats] which left the overlay frozen at 0 MB/s for the whole write phase. The brace-balanced stdout JSON reader is kept as the only source for the final per-phase result, but PollProcessIoAsync now drives onLiveSpeedMBs instead, independent of fio's stdout behavior.
public sealed class FioRunner
{
    // Default on-disk test file size for a pass. Large enough that Windows' file-system cache and NVMe SLC-cache "burst" numbers matter less than at a smaller size; 3 passes at 2GiB were found to match 10 passes at 4GiB in much less time. Still narrower than the old 4GiB size, so an SSD with an unusually large SLC cache pool could theoretically still inflate Seq1M Q8T1 numbers (--direct=1 bypasses the OS page cache but not the drive's own onboard cache); not assumed to be a live problem here since 2GiB has already been checked against CrystalDiskMark on real hardware. Shared by both profiles (Seq1MQ8T1 and Rand4KQ1T1 use one test file), so both use one size.
    public const long DefaultTestFileSizeBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

    // Default number of times fio repeats the same job back-to-back in one process invocation, via --loops. fio's final JSON result for a multi-loop run reports one bandwidth/IOPS figure averaged across all loops' combined bytes/time, rather than this codebase launching fio N times and averaging itself; documented fio behavior, not yet confirmed against a captured transcript. 3×2GiB was found to match 10×4GiB in much less wall-clock time (paired with the file-size default above). Loops alone don't defeat drive-cache inflation; re-reading/re-writing the same file can still be served from the drive's own cache every loop; loops only smooth run-to-run variance (thermal throttling, background I/O, momentary contention), and the file-size increase is what addresses caching. Loops also rule out polling the test file's own size as a live-speed signal: fio rewrites the same already-full file in place on every loop after the first, so file size stops changing; PollProcessIoAsync polls the fio process's OS-level I/O counters instead, which keep counting correctly across every loop.
    public const int DefaultLoopCount = 3;

    // How often PollProcessIoAsync samples the fio process's I/O counters for a live MB/s figure. Independent of fio's own unreliable --status-interval; governs only how often this polling loop wakes up, not fio's internal reporting cadence.
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    private readonly string _fioExePath;

    // vendorDirectory defaults to vendor/fio next to the exe, matching the vendor/<tool> layout every other process-isolated engine in this codebase (furmark2, gravitymark, intel-pdt) uses.
    public FioRunner(string? vendorDirectory = null)
    {
        var dir = vendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "fio");
        _fioExePath = Path.Combine(dir, "fio.exe");
    }

    public sealed class Result
    {
        public bool LaunchOk { get; init; }
        public string? LaunchError { get; init; }

        // Average bandwidth for the whole pass in MB/s (bw_bytes / 1024 / 1024, the "consumer MB/s ≈ MiB/s" convention CrystalDiskMark/KDiskMark display, not strict SI decimal megabytes).
        public double? AvgSpeedMBs { get; init; }
        public double? AvgIops { get; init; }

        // Non-zero (per FioJob.Error) means fio itself flagged an I/O error: the storage-test fail condition.
        public int FioErrorCount { get; init; }

        public TimeSpan Duration { get; init; }
        public string RawOutput { get; init; } = "";

        // Loops actually requested for this pass, echoed back from the call rather than parsed from fio's output; lets the report say "averaged over N passes" without StorageStressTestModule tracking it separately.
        public int LoopCount { get; init; } = 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters counters);

    // profile selects --rw/--bs/--iodepth and the job name fio's JSON result is tagged with; everything else about how a pass is driven (loop count, live-speed sourcing, file lifecycle) is identical between profiles.
    // loopCount is fio's --loops; how many times it repeats the job before reporting one already-averaged bandwidth/IOPS figure. Defaults to DefaultLoopCount. 1 omits --loops entirely (fio's own default of running once) rather than passing --loops=1 explicitly, keeping the arg string unchanged for callers that don't want looping.
    // onLiveSpeedMBs fires from a background poll loop, not the UI thread [callers must marshal to the dispatcher themselves, same convention as other ProgressChanged-style callbacks here] roughly once per StatusInterval, sourced from PollProcessIoAsync rather than fio's stdout. Fires consistently across every loop of a multi-loop pass, not just the first.
    public async Task<Result> RunPassAsync(
        string testFilePath, FioProfile profile, FioPhase phase, long testFileSizeBytes,
        Action<double>? onLiveSpeedMBs, CancellationToken ct, int loopCount = DefaultLoopCount)
    {
        if (!File.Exists(_fioExePath))
        {
            return new Result { LaunchOk = false, LaunchError = $"fio.exe not found at {_fioExePath}" };
        }

        // Per-profile rw mode/block size/queue depth; everything else about the launched job (path, size, --direct, --thread, --ioengine, looping, JSON output) is identical between profiles.
        var (rw, blockSize, ioDepth, profileTag) = profile switch
        {
            FioProfile.Seq1MQ8T1 => (phase == FioPhase.Write ? "write" : "read", "1m", 8, "seq1m_q8t1"),
            FioProfile.Rand4KQ1T1 => (phase == FioPhase.Write ? "randwrite" : "randread", "4k", 1, "rand4k_q1t1"),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
        };

        var args =
            $"--name=wintester_{profileTag}_{rw} " +
            $"--filename=\"{testFilePath}\" " +
            $"--rw={rw} " +
            $"--bs={blockSize} " +
            $"--iodepth={ioDepth} " +
            "--numjobs=1 " +
            $"--size={testFileSizeBytes} " +
            "--direct=1 " +
            "--thread " +
            "--ioengine=windowsaio " +
            "--time_based=0 " +
            (loopCount > 1 ? $"--loops={loopCount} " : "") +
            $"--status-interval={(int)StatusInterval.TotalMilliseconds} " +
            "--output-format=json " +
            "--group_reporting";

        var psi = new ProcessStartInfo
        {
            FileName = _fioExePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var start = DateTime.Now;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new Result { LaunchOk = false, LaunchError = $"Failed to launch fio.exe: {ex.Message}" };
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // The reliable live-speed source, replacing --status-interval stdout parsing. Runs for the fio process's whole lifetime, cancelled once WaitForExitAsync returns below. Windows-only (GetProcessIoCounters is a Win32 API), but process.Start() above would already be failing on any other OS, so this is defensive rather than a real behavioral fork.
        using var ioPollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ioPollTask = OperatingSystem.IsWindows()
            ? PollProcessIoAsync(process, phase, onLiveSpeedMBs, ioPollCts.Token)
            : Task.CompletedTask;

        FioJsonResult? finalResult = null;
        var rawOutput = new StringBuilder();

        // Streaming brace-balanced JSON reader, kept as the source of the final per-phase result (fio always emits one JSON object on exit) even though it's no longer trusted for live snapshots. Each completed top-level {...} span is either a periodic --status-interval snapshot (harmless if emitted, see ReportLiveSpeed) or the final result object; the last one parsed always wins, so a single-object stream still ends up correct.
        var stdout = process.StandardOutput;
        var buffer = new char[4096];
        var jsonBuf = new StringBuilder();
        int depth = 0;

        try
        {
            while (true)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;

                rawOutput.Append(buffer, 0, read);

                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c == '{') depth++;
                    if (depth > 0) jsonBuf.Append(c);
                    if (c == '}')
                    {
                        depth = Math.Max(0, depth - 1);
                        if (depth == 0 && jsonBuf.Length > 0)
                        {
                            var parsed = TryParse(jsonBuf.ToString());
                            jsonBuf.Clear();
                            if (parsed is not null)
                            {
                                finalResult = parsed;
                                // Belt-and-suspenders only; a mid-run snapshot isn't ignored if fio emits one, but PollProcessIoAsync above is what the live number actually depends on.
                                ReportLiveSpeed(parsed, phase, onLiveSpeedMBs);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            ioPollCts.Cancel();
            throw;
        }

        await process.WaitForExitAsync(ct);
        ioPollCts.Cancel();
        try { await ioPollTask; } catch (OperationCanceledException) { /* expected on cancel. */ }

        var stderrText = await stderrTask;
        var duration = DateTime.Now - start;

        if (finalResult?.Jobs is { Count: > 0 } jobs)
        {
            var job = jobs[0];
            var stats = phase == FioPhase.Write ? job.Write : job.Read;

            return new Result
            {
                LaunchOk = true,
                AvgSpeedMBs = stats is not null ? stats.BwBytes / 1024.0 / 1024.0 : null,
                AvgIops = stats?.Iops,
                FioErrorCount = job.Error,
                Duration = duration,
                RawOutput = rawOutput.ToString() + (stderrText.Length > 0 ? "\n--- stderr ---\n" + stderrText : ""),
                LoopCount = loopCount
            };
        }

        return new Result
        {
            LaunchOk = true,
            AvgSpeedMBs = null,
            FioErrorCount = process.ExitCode != 0 ? 1 : 0,
            Duration = duration,
            RawOutput = rawOutput.ToString() + (stderrText.Length > 0 ? "\n--- stderr ---\n" + stderrText : ""),
            LoopCount = loopCount
        };
    }

    private static FioJsonResult? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FioJsonResult>(json);
        }
        catch
        {
            // Not every balanced {...} span in fio's stdout is one of our job objects (e.g. a stray informational line); skip anything that doesn't parse rather than failing the whole pass.
            return null;
        }
    }

    private static void ReportLiveSpeed(FioJsonResult parsed, FioPhase phase, Action<double>? onLiveSpeedMBs)
    {
        if (onLiveSpeedMBs is null) return;
        if (parsed.Jobs is not { Count: > 0 } jobs) return;

        var stats = phase == FioPhase.Write ? jobs[0].Write : jobs[0].Read;
        if (stats is null || stats.BwBytes <= 0) return;

        onLiveSpeedMBs(stats.BwBytes / 1024.0 / 1024.0);
    }

    // The real live-speed source: polls the fio child process's cumulative OS-level I/O counters via GetProcessIoCounters roughly every StatusInterval, takes the delta in ReadTransferCount/WriteTransferCount matching phase, and divides by elapsed wall-clock time; the same technique Task Manager's per-process disk-I/O column uses. Independent of fio's own stdout, and unlike polling the test file's own size, keeps working correctly across every loop of a multi-loop pass. Not yet confirmed against real fio/hardware (no Windows host in this environment); the Win32 API itself is well-established, but that's distinct from confirming this specific fio.exe build.
    [SupportedOSPlatform("windows")]
    private static async Task PollProcessIoAsync(
        Process process, FioPhase phase, Action<double>? onLiveSpeedMBs, CancellationToken ct)
    {
        if (onLiveSpeedMBs is null) return;

        ulong lastBytes = 0;
        var lastSampleTime = DateTime.UtcNow;
        bool haveBaseline = false;

        try
        {
            while (true)
            {
                await Task.Delay(StatusInterval, ct);

                if (process.HasExited) return;
                if (!GetProcessIoCounters(process.Handle, out var counters))
                {
                    // Best-effort telemetry only: a failed sample (e.g. the process exited between the HasExited check and this call) is one skipped tick, not a reason to fail the test.
                    continue;
                }

                var bytes = phase == FioPhase.Write ? counters.WriteTransferCount : counters.ReadTransferCount;
                var now = DateTime.UtcNow;

                if (haveBaseline)
                {
                    var deltaBytes = bytes >= lastBytes ? bytes - lastBytes : 0UL; // counters are cumulative/monotonic; guarded anyway.
                    var deltaSeconds = (now - lastSampleTime).TotalSeconds;
                    if (deltaSeconds > 0)
                    {
                        var mbs = deltaBytes / 1024.0 / 1024.0 / deltaSeconds;
                        onLiveSpeedMBs(mbs);
                    }
                }

                lastBytes = bytes;
                lastSampleTime = now;
                haveBaseline = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal; cancelled once the caller's WaitForExitAsync returns (process finished) or the pass was aborted.
        }
    }
}
