using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tests.Memory;

public enum MemoryTestPattern { Aa, Bb, Cc, Dd, Ee, Ff, Pattern55, Random }

// One detected mismatch for a window+iteration+pattern pass. Records only the first differing byte per pass, matching the ported tool's own log granularity; a real bit-flip is expected to be rare and localized, not worth enumerating exhaustively.
public sealed record MemoryTestError(long WindowStartOffset, long OffsetWithinWindow, int BitFlips, string PatternName);

public sealed class MemoryTestEngineResult
{
    public int Iterations { get; init; }
    public long SizeMb { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<MemoryTestError> Errors { get; init; } = Array.Empty<MemoryTestError>();

    // Total windows allocated across the run; compared against DistinctWindowAddresses as a cheap sanity signal.
    public int TotalWindowsAllocated { get; init; }

    // True if a live headroom re-check (not the original start-of-run sizing) cut the run short, e.g. another process grew its footprint mid-run. Not an error: completed windows are still valid, SizeMb just wasn't fully reached.
    public bool StoppedEarlyForHeadroom { get; init; }

    // Count of distinct virtual base addresses seen across all windows (via a brief pin + AddrOfPinnedObject right after allocation). Proves only whether the .NET allocator reused a virtual address between windows; says nothing about physical page frames or DIMM/channel, which aren't observable from user-mode managed code. A value lower than TotalWindowsAllocated is a look-closer signal, not a failure (LOH address reuse after release is legitimate GC behavior).
    public int DistinctWindowAddresses { get; init; }

    // Sum of the actual window sizes that ran to completion, in MB — distinct from SizeMb, which is
    // always the original *target* regardless of how the run actually went. Added after a field run
    // (2026-09-27) showed a 0-window StoppedEarlyForHeadroom run still reporting "Amount tested"/
    // "Coverage %" as if the full target had run: SizeMb alone can't distinguish "tested everything
    // requested" from "tested nothing." Callers that report coverage should use this field, not SizeMb.
    public long ActualTestedMb { get; init; }
}

// In-process C# port of Aleksander Płomiński's memtest (GPLv3); fills memory with 7 fixed byte patterns plus one random pattern and checks for silent corruption. Runs in-process rather than as an isolated child process like every other engine in this codebase, accepting a crash-isolation trade-off plus the GPLv3 obligation that comes with it on distribution.
//
// Windowed, not whole-buffer: an earlier version held the entire requested size resident for the whole run, pinning up to half a 32 GB machine's RAM for minutes and starving everything else. This version walks the total in bounded DefaultWindowMb windows [allocate, run every pattern's fill/settle/verify cycle, release with a forced blocking GC (see ReleaseWindow), move on] so peak extra resident memory is one window's worth regardless of total size.
//
// Windowed, multi-chunk: a .NET byte[] can't exceed Array.MaxLength (~2047.99 MB), so a window above that can't be one array. Each window is composed of several DefaultChunkMb buffers, all allocated up front and held resident together for that window's pattern loop, then released as one group. Progress accounting still counts one step per window-pattern pair, not per chunk.
//
// Off the calling thread: the fill/verify loops are heavy synchronous CPU/memory-bandwidth work, so RunAsync runs each window's work inside Task.Run to keep the UI thread free to pump messages and respond to Cancel.
//
// Correctness fixes vs. the reference source: re-derives expected content only after SettleDelay has elapsed (the reference hashed the same unchanged buffer twice back-to-back, so its mismatch branch could never fire); replaces the reference's out-of-range uniform_int_distribution random fill with Random.NextBytes; never allocates a second full-test-size expected buffer for verification.
public sealed class MemoryTestEngine
{
    // How long a pattern sits in a window before readback. Placeholder pending real-hardware tuning.
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    // Most memory this engine ever holds resident at once, regardless of total requested test size. Restored to 5 GB once the multi-chunk redesign made it achievable without overflow (see MaxSafeChunkMb); composed of several smaller buffers, not one. Exposed as a constructor parameter for a future Advanced Mode UI, same as MemoryStressTestModule's iterations. Not yet validated against real low-RAM (4-8 GB) hardware, where a 5 GB window could exceed the whole recommended test size.
    public const int DefaultWindowMb = 5120;

    // Size of each buffer a window is split into. Chosen well clear of MaxSafeChunkMb for allocator/header margin.
    public const int DefaultChunkMb = 1536;

    // Hard ceiling for any single byte[] chunk; Array.MaxLength is ~2047.99 MB even with gcAllowVeryLargeObjects, and 1900 MB leaves margin below that. Guards against a prior incident where a 5120 MB single-buffer window silently overflowed the int cast used for array length (wrapping to 1 GiB), desyncing the long-math window count from the ~5x-as-many windows actually run and driving progress past 200%. The constructor now throws instead of silently wrapping if a chunk at or above this ceiling is requested.
    public const int MaxSafeChunkMb = 1900;

    private readonly int _windowMb;
    private readonly int _chunkMb;

    // The composed peak (sum of concurrently-held chunks) this instance was constructed with, not a single buffer's size; exposed so callers like MemoryStressTestModule's headroom re-check can ask about this instance's own window size.
    public int WindowMb => _windowMb;

    // windowMb: total simultaneous resident peak per window; may exceed MaxSafeChunkMb since it's composed of multiple chunkMb buffers. chunkMb: size of each buffer a window splits into; must not exceed MaxSafeChunkMb, since a single byte[] can't hold more.
    public MemoryTestEngine(int? windowMb = null, int? chunkMb = null)
    {
        _windowMb = windowMb ?? DefaultWindowMb;
        _chunkMb = chunkMb ?? DefaultChunkMb;
        if (_windowMb <= 0) throw new ArgumentOutOfRangeException(nameof(windowMb));
        if (_chunkMb <= 0) throw new ArgumentOutOfRangeException(nameof(chunkMb));
        if (_chunkMb > MaxSafeChunkMb)
            throw new ArgumentOutOfRangeException(nameof(chunkMb),
                $"{_chunkMb} MB exceeds MaxSafeChunkMb ({MaxSafeChunkMb} MB) [a single .NET byte[] cannot " +
                "hold this much (Array.MaxLength is ~2047.99 MB even with gcAllowVeryLargeObjects). A " +
                $"larger *composed* peak is fine] raise {nameof(windowMb)} instead, which is split across " +
                $"multiple {nameof(chunkMb)}-sized buffers automatically; see {nameof(MaxSafeChunkMb)}'s " +
                "doc comment for the incident this guards against.");
    }

    private static readonly MemoryTestPattern[] AllPatterns =
    {
        MemoryTestPattern.Aa, MemoryTestPattern.Bb, MemoryTestPattern.Cc, MemoryTestPattern.Dd,
        MemoryTestPattern.Ee, MemoryTestPattern.Ff, MemoryTestPattern.Pattern55, MemoryTestPattern.Random
    };

    // sizeMb: total coverage target for the whole run, from MemorySizer; never all resident at once, walked in _windowMb windows, and treated as a ceiling not a guarantee (see hasHeadroomForNextWindow). iterations: full passes over the entire range; hardcoded to 1 by MemoryStressTestModule for now. onProgress: invoked after every window's pattern pass. hasHeadroomForNextWindow: optional live re-check before each window, given the actual (already-clipped-to-remaining-total) size in MB of the window about to run rather than the fixed _windowMb constant — the final window of any run is routinely smaller than a full window, and on a machine whose *entire* recommended test size is below one full window (see field-issues tracker 1.3: low-RAM hardware), every window is. Asking "is there room for a full _windowMb window" in that case demands more headroom than the run will ever need and fails the very first window. Returning false stops the run early rather than pressing on against a start-of-run size that may have gone stale. Null preserves the original always-proceed behavior, relying on the caller's OutOfMemoryException handling as the safety net.
    public async Task<MemoryTestEngineResult> RunAsync(
        long sizeMb, int iterations, Action<double, string>? onProgress, CancellationToken ct,
        Func<long, bool>? hasHeadroomForNextWindow = null)
    {
        var sw = Stopwatch.StartNew();
        long totalBytes = sizeMb * 1024L * 1024L;
        long windowBytes = (long)_windowMb * 1024L * 1024L;
        int windowCount = (int)((totalBytes + windowBytes - 1) / Math.Max(1, windowBytes));

        var errors = new List<MemoryTestError>();
        var seedRng = new Random();
        var windowAddresses = new HashSet<nint>();
        int totalWindowsAllocated = 0;
        long actualBytesCovered = 0;
        bool stoppedEarlyForHeadroom = false;

        int totalSteps = Math.Max(1, iterations * windowCount * AllPatterns.Length);
        int stepIndex = 0;

        try
        {
            for (int iter = 1; iter <= iterations && !stoppedEarlyForHeadroom; iter++)
            {
                long windowStart = 0;
                int windowNum = 0;

                while (windowStart < totalBytes)
                {
                    ct.ThrowIfCancellationRequested();

                    // Kept as `long` throughout; casting a multi-GB window size to `int` here caused the MaxSafeChunkMb overflow incident; only RunWindow's per-chunk sizes (each capped at MaxSafeChunkMb) are cast to int.
                    // Computed before the headroom check (not after, as previously) so the check is asked about the window that will actually run, not the fixed _windowMb constant — see the field-issues tracker 1.3 fix note on RunAsync's hasHeadroomForNextWindow parameter above.
                    long thisWindowBytes = Math.Min(windowBytes, totalBytes - windowStart);
                    long thisWindowMb = (thisWindowBytes + 1024L * 1024L - 1) / (1024L * 1024L);

                    // Skipped for the very first window of the whole run (totalWindowsAllocated == 0):
                    // that window's size came from MemorySizer.ComputeSafeTestSize() moments ago, and
                    // hasHeadroomForNextWindow's own implementation (MemorySizer.HasHeadroomForWindow)
                    // re-queries WMI free memory fresh rather than reusing that snapshot. Gating window 1
                    // on a second, independent WMI read taken milliseconds later has no safety margin
                    // against ordinary measurement noise between two calls — a field run (2026-09-27, the
                    // Broadwell machine, right after a GPU test) hit exactly this: window 1 failed with
                    // "insufficient headroom" and the run reported 0 windows / 0.0s / a vacuous pass,
                    // reproducing field-issues 1.3's symptom through a new path even with that fix's
                    // clipped-window-size logic correctly in place. The live re-check still protects every
                    // window after the first, which is its actual job: catching another process growing
                    // its footprint *during* a long multi-window run, not re-litigating a sizing decision
                    // that was just made.
                    if (totalWindowsAllocated > 0 && hasHeadroomForNextWindow is not null && !hasHeadroomForNextWindow(thisWindowMb))
                    {
                        stoppedEarlyForHeadroom = true;
                        onProgress?.Invoke(100.0 * stepIndex / totalSteps,
                            $"Stopped early; insufficient headroom for another {thisWindowMb} MB window " +
                            $"(iteration {iter}/{iterations}, window {windowNum + 1}/{windowCount})");
                        break;
                    }

                    windowNum++;

                    long capturedWindowStart = windowStart;
                    int capturedWindowNum = windowNum;
                    int capturedIter = iter;

                    // Runs off the calling thread inside Task.Run, allocating the window's buffers inside RunWindow so they're eligible for collection the moment it returns rather than staying referenced across the await. The collection that frees them runs here, after RunWindow has returned; inside RunWindow its own `chunks` local is still live, so a collection there frees nothing.
                    var windowResult = await Task.Run(() =>
                    {
                        var result = RunWindow(thisWindowBytes, capturedWindowStart, seedRng, ct);
                        ReleaseWindow();
                        return result;
                    }, ct);

                    errors.AddRange(windowResult.Errors);
                    lock (windowAddresses) { windowAddresses.Add(windowResult.BaseAddress); }
                    totalWindowsAllocated++;
                    // Only counted once a window actually completes; a window that never ran (headroom
                    // check failed, or a stop before this point) contributes nothing, unlike SizeMb, which
                    // is fixed at the original target regardless of how the run went.
                    if (iter == 1) actualBytesCovered += thisWindowBytes;

                    stepIndex += AllPatterns.Length;
                    onProgress?.Invoke(100.0 * stepIndex / totalSteps,
                        $"Iteration {capturedIter}/{iterations}, window {capturedWindowNum}/{windowCount}");

                    windowStart += thisWindowBytes;
                }
            }
        }
        finally
        {
            // However the run ends (finished, stopped early, cancelled, faulted), give everything back to the OS so the test's multi-GB footprint doesn't sit in the process after it. A plain forced collection frees the managed heap but leaves its pages committed to the process; only an aggressive collection decommits them. Off the calling thread, since returning several GB to the OS isn't instant and this may be the UI thread.
            await Task.Run(ReleaseAllMemory);
        }

        sw.Stop();
        return new MemoryTestEngineResult
        {
            Iterations = iterations,
            SizeMb = sizeMb,
            ActualTestedMb = actualBytesCovered / (1024L * 1024L),
            Duration = sw.Elapsed,
            Errors = errors,
            TotalWindowsAllocated = totalWindowsAllocated,
            DistinctWindowAddresses = windowAddresses.Count,
            StoppedEarlyForHeadroom = stoppedEarlyForHeadroom
        };
    }

    // Runs every pattern's fill-settle-verify cycle against one window, then releases it. Synchronous by design; the caller runs this inside Task.Run so it doesn't block the UI thread; the settle step blocks this pool thread via GetAwaiter().GetResult() rather than await since the whole method is meant to run start-to-finish with no UI affinity. Splits windowSizeBytes into _chunkMb buffers, all allocated up front and held resident together for the pattern loop; each chunk length is computed in long and only cast to int after being clamped to at most MaxSafeChunkMb, so it can't repeat the overflow the constructor's guard prevents.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (List<MemoryTestError> Errors, nint BaseAddress) RunWindow(
        long windowSizeBytes, long windowStartOffset, Random seedRng, CancellationToken ct)
    {
        long chunkBytes = (long)_chunkMb * 1024L * 1024L;
        int chunkCount = (int)((windowSizeBytes + chunkBytes - 1) / Math.Max(1, chunkBytes));

        var chunks = new byte[chunkCount][];
        var chunkOffsets = new long[chunkCount];
        long remaining = windowSizeBytes;
        long offset = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            int size = (int)Math.Min(chunkBytes, remaining); // safe: chunkBytes <= MaxSafeChunkMb worth
            chunks[i] = new byte[size];
            chunkOffsets[i] = offset;
            offset += size;
            remaining -= size;
        }

        var errors = new List<MemoryTestError>();

        // Brief pin to read back a virtual base address for the duplicate-window sanity check (see DistinctWindowAddresses). Only the first chunk is fingerprinted, representative of the window rather than exhaustive; freed immediately after reading the pointer.
        var handle = GCHandle.Alloc(chunks[0], GCHandleType.Pinned);
        nint baseAddress;
        try { baseAddress = handle.AddrOfPinnedObject(); }
        finally { handle.Free(); }

        foreach (var pattern in AllPatterns)
        {
            ct.ThrowIfCancellationRequested();

            // One seed per chunk per pattern so the random pattern's content differs chunk-to-chunk instead of repeating.
            var seeds = new int[chunkCount];
            lock (seedRng)
            {
                for (int i = 0; i < chunkCount; i++) seeds[i] = seedRng.Next();
            }

            for (int i = 0; i < chunkCount; i++) FillWindow(chunks[i], pattern, seeds[i]);

            Task.Delay(SettleDelay, ct).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();

            for (int i = 0; i < chunkCount; i++)
            {
                if (VerifyWindow(chunks[i], pattern, seeds[i]) is { } mismatch)
                {
                    errors.Add(new MemoryTestError(
                        windowStartOffset + chunkOffsets[i], mismatch.Offset, mismatch.BitFlips,
                        PatternName(pattern)));
                }
            }
        }
    

        // No collection here: `chunks` is still live in this frame, so one would free nothing. The caller collects once this method has returned (see RunAsync).
        return (errors, baseAddress);
    }

    // Forces an immediate blocking Gen2/LOH collection so the just-finished window's buffers are freed before the next window allocates, rather than waiting on a GC that might not happen soon. Only frees the managed heap for reuse; it does not hand the pages back to the OS (see ReleaseAllMemory).
    private static void ReleaseWindow() => GC.Collect(2, GCCollectionMode.Forced, blocking: true);

    // End-of-run release: a compacting, aggressive collection that also decommits the freed pages, which a plain forced collection does not.
    private static void ReleaseAllMemory() =>
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    private static void FillWindow(byte[] window, MemoryTestPattern pattern, int seed)
    {
        if (pattern == MemoryTestPattern.Random)
        {
            new Random(seed).NextBytes(window);
            return;
        }

        Array.Fill(window, FillByte(pattern));
    }

    private readonly record struct Mismatch(long Offset, int BitFlips);

    private static Mismatch? VerifyWindow(byte[] window, MemoryTestPattern pattern, int seed)
    {
        if (pattern == MemoryTestPattern.Random)
        {
            var expected = new byte[window.Length];
            new Random(seed).NextBytes(expected);

            if (window.AsSpan().SequenceEqual(expected)) return null;

            for (int i = 0; i < window.Length; i++)
            {
                if (window[i] != expected[i])
                    return new Mismatch(i, BitOperations.PopCount((uint)(window[i] ^ expected[i])));
            }
            return null; // unreachable given SequenceEqual returned false above, kept for safety
        }
        else
        {
            byte fill = FillByte(pattern);
            for (int i = 0; i < window.Length; i++)
            {
                if (window[i] != fill)
                    return new Mismatch(i, BitOperations.PopCount((uint)(window[i] ^ fill)));
            }
            return null;
        }
    }

    private static byte FillByte(MemoryTestPattern p) => p switch
    {
        MemoryTestPattern.Aa => 0xAA,
        MemoryTestPattern.Bb => 0xBB,
        MemoryTestPattern.Cc => 0xCC,
        MemoryTestPattern.Dd => 0xDD,
        MemoryTestPattern.Ee => 0xEE,
        MemoryTestPattern.Ff => 0xFF,
        MemoryTestPattern.Pattern55 => 0x55,
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Random pattern has no constant fill byte.")
    };

    private static string PatternName(MemoryTestPattern p) => p switch
    {
        MemoryTestPattern.Aa => "0xAA",
        MemoryTestPattern.Bb => "0xBB",
        MemoryTestPattern.Cc => "0xCC",
        MemoryTestPattern.Dd => "0xDD",
        MemoryTestPattern.Ee => "0xEE",
        MemoryTestPattern.Ff => "0xFF",
        MemoryTestPattern.Pattern55 => "0x55",
        _ => "RANDOM"
    };
}
