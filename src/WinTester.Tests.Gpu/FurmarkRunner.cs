using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinTester.Tests.Gpu;

// Runs one FurMark2 engine pass (OpenGL or Vulkan) as a visible, positioned-and-locked child process; a deliberate exception to the "no window is ever shown" rule, since FurMark's render window is the workload itself and, unlike IpdtModuleRunner, can't run hidden. Builds the CLI, locates and locks down the render window, and parses the Quick Stats block from stdout.
public sealed class FurmarkRunner
{
    // Directory furmark.exe and its GeeXLab engine dependencies live in.
    public string VendorDirectory { get; }

    public FurmarkRunner(string vendorDirectory)
    {
        VendorDirectory = vendorDirectory;
    }

    // Extra wait past the requested duration before assuming FurMark hasn't self-exited and force-killing it. Covers the unconfirmed assumption that --max-time ends the process rather than just the benchmark phase; the test completes correctly either way, just ~30s later if a kill is needed.
    public static readonly TimeSpan SelfExitGracePeriod = TimeSpan.FromSeconds(30);

    public sealed class Result
    {
        public required FurmarkEngine Engine { get; init; }
        public int? ExitCode { get; init; }
        public string Output { get; init; } = "";
        public TimeSpan Duration { get; init; }
        public string? LaunchError { get; init; }
        public FurmarkQuickStats? Stats { get; init; }
        public bool WasForceKilledAfterGrace { get; init; }
        public bool RenderWindowFound { get; init; }

        // Adapter this pass targeted, if any: null for the OpenGL baseline (no per-adapter selection) and null for a Vulkan pass that fell back to FurMark's default device because FurmarkGpuProbe found nothing. Echoed from RunAsync's input and kept on the result so BuildRawLog can label a pass from the result alone.
        public int? TargetGpuIndex { get; init; }
        public string? TargetGpuName { get; init; }

        // Non-null only for a VRAM-test pass; echoes RunAsync's vramTestGb param so BuildRawLog can label the pass from the result alone, same reasoning as TargetGpuIndex.
        public int? VramTestGb { get; init; }

        // True if the render window was reparented as a true WS_CHILD rather than falling back to the older positioned-top-level-window approach; useful in the raw log to tell which code path ran on a given machine.
        public bool WasReparented { get; init; }

        // Echoes the --opengl-ver-major/-minor/--opengl-core-profile override requested for this pass, null for the default case; meaningful for OpenGL passes only. Kept on the result, same as TargetGpuIndex, so GpuStressTestModule's compatibility-context retry ladder and raw log can both identify the context an attempt used.
        public int? OpenGlVerMajor { get; init; }
        public int? OpenGlVerMinor { get; init; }
        public bool? OpenGlCoreProfile { get; init; }
    }

    public async Task<Result> RunAsync(
        FurmarkEngine engine,
        int durationSeconds,
        WindowScreenRect embedRect,
        string gpuDataCsvPath,
        CancellationToken ct,
        int? gpuIndex = null,
        string? gpuName = null,
        Func<IntPtr>? getParentHwnd = null,
        int? vramTestGb = null,
        Action? onRenderWindowReady = null,
        int? openGlVerMajor = null,
        int? openGlVerMinor = null,
        bool? openGlCoreProfile = null,
        Action? reassertOverlayTopmost = null)
    {
        var exePath = Path.Combine(VendorDirectory, "furmark.exe");
        var sw = Stopwatch.StartNew();

        if (!File.Exists(exePath))
        {
            return new Result
            {
                Engine = engine,
                Duration = sw.Elapsed,
                LaunchError = $"furmark.exe not found at \"{exePath}\". Vendor FurMark2's engine files " +
                              "under vendor/furmark2/."
            };
        }

        // --title-bar 0 gives a chromeless window (Win32Interop.PositionAndLockWindow strips caption/resize/system-menu bits too, defensively); --no-osi/--disable-demo-options hide the HUD and demo-options panel; --no-score-box suppresses the end-of-run popup (SCORE still prints to stdout/_scores.csv regardless); --no-dpi-scaling keeps FurMark from second-guessing our already-DPI-correct pixel rect.
        var args =
            $"--demo {engine.ToDemoName()} --benchmark --no-score-box --disable-demo-options --no-osi " +
            $"--no-dpi-scaling --title-bar 0 --width {embedRect.Width} --height {embedRect.Height} " +
            $"--max-time {durationSeconds} --log-gpu-data --log-gpu-data-filename \"{gpuDataCsvPath}\"";

        // --gpu-index applies only to Vulkan/OpenCL/CUDA demos per its docs; never pass it for the OpenGL baseline, since there's no OpenGL equivalent and an unsupported flag risks an unknown-argument launch failure on the pass/fail-bearing run.
        if (engine == FurmarkEngine.Vulkan && gpuIndex is { } idx)
            args += $" --gpu-index {idx}";

        // The VRAM test is documented as OpenGL-only with no --gpu-index equivalent, so on a multi-GPU machine it can only exercise whichever adapter the driver picks as default; the caller is responsible for pairing this only with engine == OpenGl, this method doesn't re-validate. No confirmation that --benchmark/--max-time/--log-gpu-data behave identically with this flag added; treat as an untested combination, not a confirmed-safe one.
        if (vramTestGb is { } gb)
            args += $" --furmark-vram-test-gb {gb}";

        // --opengl-ver-major/-minor/--opengl-core-profile control the OpenGL context FurMark2 requests; no separate legacy/compatibility demo name exists, so a lower/non-core context on the same furmark-gl demo is the only lever available for iGPUs that reject the default context. Only appended on an explicit override request (GpuStressTestModule's integrated-GPU compatibility retry ladder); omitted on the first attempt so a normal dedicated-GPU run's negotiated context is unaffected.
        if (openGlVerMajor is { } glMajor)
            args += $" --opengl-ver-major {glMajor}";
        if (openGlVerMinor is { } glMinor)
            args += $" --opengl-ver-minor {glMinor}";
        if (openGlCoreProfile is { } glCore)
            args += $" --opengl-core-profile {(glCore ? 1 : 0)}";

        // On by default for every pass (OpenGL baseline and every Vulkan sweep pass); not yet parsed into KeyMetrics since no confirmed stdout/exit-code contract for a hit exists, but captured in the raw per-pass log regardless. Advanced-Mode-togglable later.
        args += " --artifact-scanner";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = VendorDirectory,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true, // suppresses only the console window; the GL/Vulkan render window is a separate GUI window unaffected by this flag.
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
        catch (Exception ex)
        {
            return new Result
            {
                Engine = engine,
                Duration = sw.Elapsed,
                LaunchError = $"Failed to start \"{exePath}\": {ex.Message}"
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var ctReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort; process may already be exiting on its own */ }
        });

        // Finds the render window once it exists, then reparents it as a true WS_CHILD when a parent HWND is available; this fixes "window exists independently and can be clicked away" by construction, unlike the older positioned-overlay approach that merely asked a still-independent top-level window to behave (confirmed insufficient on real hardware). Falls back to positioning/locking the window as a top-level window if no parent HWND is supplied or it resolves to zero (e.g. a headless caller or a WPF container that hasn't built its native handle yet) [still functional, just without the stronger click-proof guarantee. Not fatal if the window is never found] the test still runs, just without visual placement/input-lock, flagged via RenderWindowFound so the report can surface the gap.
        bool renderWindowFound = false;
        bool reparented = false;
        try
        {
            var hwnd = await Win32Interop.WaitForMainWindowHandleAsync(process, TimeSpan.FromSeconds(15), ct);
            if (hwnd != IntPtr.Zero)
            {
                var parentHwnd = getParentHwnd?.Invoke() ?? IntPtr.Zero;
                if (parentHwnd != IntPtr.Zero)
                {
                    Win32Interop.ReparentAsChild(hwnd, parentHwnd, embedRect.Width, embedRect.Height);
                    reparented = true;
                }
                else
                {
                    Win32Interop.PositionAndLockWindow(hwnd, embedRect);
                }
                renderWindowFound = true;

                // Re-assert WinTester's own always-on-top overlay right after FurMark's window is found/placed,
                // mirroring GravityMarkRunner's identical call. FurMark's window is a genuine top-level window
                // for the duration of this find (up to the same 15s detection window) before reparenting above
                // turns it into a WS_CHILD; during that window its own creation can disturb topmost-band
                // ordering the same way GravityMark's independent window does, and once reparented it simply
                // inherits whatever z-order its host settled into. Unconfirmed on real hardware either way (see
                // field-issues tracker 1.2/2.1/4) but low-risk regardless of the exact cause, and closes a real
                // asymmetry: previously this protected GravityMark's phase and not FurMark's.
                reassertOverlayTopmost?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the window appeared; the process kill above already handles this.
        }

        // FurMark2 can take several real seconds to initialize (worst case: the VRAM test pass) before the
        // render window exists and the benchmark clock starts. This callback fires once, right after the
        // window-find attempt above resolves either way, so the caller can start its stage timer from
        // actual benchmark start rather than process-launch time. Never invoked if LaunchError already
        // returned above.
        onRenderWindowReady?.Invoke();

        bool forceKilledAfterGrace = false;
        try
        {
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var graceTask = Task.Delay(TimeSpan.FromSeconds(durationSeconds) + SelfExitGracePeriod, ct);
            var finished = await Task.WhenAny(exitTask, graceTask);

            if (finished == graceTask && !process.HasExited)
            {
                forceKilledAfterGrace = true;
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // graceTask observes ct too; an operator cancel during the grace wait lands here; the ctReg registration above already terminates the process, so just fall through to normal post-exit handling.
        }

        sw.Stop();
        ct.ThrowIfCancellationRequested();

        var capturedOutput = output.ToString();
        var stats = FurmarkQuickStats.TryParse(capturedOutput);

        return new Result
        {
            Engine = engine,
            ExitCode = process.HasExited ? process.ExitCode : null,
            Output = capturedOutput,
            Duration = sw.Elapsed,
            Stats = stats,
            WasForceKilledAfterGrace = forceKilledAfterGrace,
            RenderWindowFound = renderWindowFound,
            TargetGpuIndex = gpuIndex,
            TargetGpuName = gpuName,
            VramTestGb = vramTestGb,
            WasReparented = reparented,
            OpenGlVerMajor = openGlVerMajor,
            OpenGlVerMinor = openGlVerMinor,
            OpenGlCoreProfile = openGlCoreProfile
        };
    }
}
