using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.Tests.Gpu;

// Runs one GravityMark pass as a visible, positioned top-level window (never reparented); the same documented exception to "no window is ever shown" that FurmarkRunner carries, since GravityMark's render window is the workload. Built directly against GravityMark.exe using the bare CLI flags documented in the supplied run_fullscreen_*.bat files' rem headers, not by shelling out to a .bat itself; a direct exe launch keeps argument-building in one place and lets this project add/remove flags (like -device) without maintaining a matching .bat.
//
// Runs as a demo loop, not a single scored pass: no GravityMark stdout sample exists, and no flag bounds how long one benchmark pass runs (-count only controls how many passes execute, not their length). Rather than trust an unconfirmed -close 1 self-exit, no -close flag is passed, -count is set high (GpuStressTestModule.GravityMarkDemoLoopPassCount) so GravityMark shouldn't finish on its own, and GravityMarkPassCeilingSeconds [repurposed from a hang-detection safety net into the actual intended duration] is what always ends the pass, by killing the process. Result therefore only reports whether the process exited cleanly and whether a frame-time log was produced (via -times), never a score.
public sealed class GravityMarkRunner
{
    // Vendor directory GravityMark ships under; GravityMark.exe itself lives one level down in a bin\ subfolder (per the .bat files' cd "%~dp0\bin"), unlike FurMark2 where the exe sits directly in FurmarkRunner.VendorDirectory.
    public string VendorDirectory { get; }

    // Phase 5.4 (field-issues tracker): how long to wait after a fatal "GravityMark::create():" log line
    // before killing the process, rather than an instant kill -- see the wait-loop comment in
    // RunOnceAsync for why this exists at all.
    private static readonly TimeSpan FatalCreateFailureGrace = TimeSpan.FromSeconds(5);

    public GravityMarkRunner(string vendorDirectory)
    {
        VendorDirectory = vendorDirectory;
    }

    public sealed class Result
    {
        public required GravityMarkApi Api { get; init; }
        public int? ExitCode { get; init; }
        public string Output { get; init; } = "";
        public TimeSpan Duration { get; init; }
        public string? LaunchError { get; init; }
        public bool WasForceKilledAfterCeiling { get; init; }
        public bool RenderWindowFound { get; init; }
        public bool WasReparented { get; init; }

        // True when the -times frame-time log existed and was non-empty after exit; the closest available signal to "did this pass actually produce data" without a confirmed stdout score format.
        public bool FrameTimeLogProduced { get; init; }

        // Adapter this pass targeted, if any; null when no -device index was available (e.g. no sensor snapshot to enumerate GPUs from) and this ran against GravityMark's default device. Echoed the same way FurmarkRunner.Result.TargetGpuIndex is, so a pass can be labeled from the result alone.
        public int? TargetGpuIndex { get; init; }
        public string? TargetGpuName { get; init; }

        // Set only when this run hit STATUS_DLL_NOT_FOUND and a VcRedistBootstrap retry was attempted (see
        // RunAsync); null on every normal run.
        public string? BootstrapNote { get; init; }
    }

    // On STATUS_DLL_NOT_FOUND (0xC0000135, see NtStatusCodes/field-issues tracker 1.2/2.2), attempts
    // VcRedistBootstrap's silent installer fallback once and, if it reports success, retries this same
    // launch exactly once before giving up. Purely reactive: a machine that doesn't hit this failure mode
    // never invokes the bootstrap and pays no extra cost.
    public async Task<Result> RunAsync(
        WindowScreenRect embedRect,
        string framesTimeLogPath,
        CancellationToken ct,
        int killAfterSeconds,
        GravityMarkApi api = GravityMarkApi.Vulkan,
        int passCount = 1,
        int? gpuIndex = null,
        string? gpuName = null,
        Action? onRenderWindowReady = null,
        Action? reassertOverlayTopmost = null)
    {
        var result = await RunOnceAsync(embedRect, framesTimeLogPath, ct, killAfterSeconds, api, passCount,
            gpuIndex, gpuName, onRenderWindowReady, reassertOverlayTopmost);

        if (result.ExitCode != unchecked((int)0xC0000135))
            return result;

        VcRedistBootstrapResult bootstrap;
        try
        {
            bootstrap = await VcRedistBootstrap.TryInstallOnceAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (!bootstrap.Attempted || !bootstrap.Succeeded)
        {
            return new Result
            {
                Api = result.Api,
                ExitCode = result.ExitCode,
                Output = result.Output,
                Duration = result.Duration,
                LaunchError = result.LaunchError,
                WasForceKilledAfterCeiling = result.WasForceKilledAfterCeiling,
                RenderWindowFound = result.RenderWindowFound,
                FrameTimeLogProduced = result.FrameTimeLogProduced,
                WasReparented = result.WasReparented,
                TargetGpuIndex = result.TargetGpuIndex,
                TargetGpuName = result.TargetGpuName,
                BootstrapNote = bootstrap.Message
            };
        }

        var retryResult = await RunOnceAsync(embedRect, framesTimeLogPath, ct, killAfterSeconds, api, passCount,
            gpuIndex, gpuName, onRenderWindowReady, reassertOverlayTopmost);
        return new Result
        {
            Api = retryResult.Api,
            ExitCode = retryResult.ExitCode,
            Output = retryResult.Output,
            Duration = retryResult.Duration,
            LaunchError = retryResult.LaunchError,
            WasForceKilledAfterCeiling = retryResult.WasForceKilledAfterCeiling,
            RenderWindowFound = retryResult.RenderWindowFound,
            FrameTimeLogProduced = retryResult.FrameTimeLogProduced,
            WasReparented = retryResult.WasReparented,
            TargetGpuIndex = retryResult.TargetGpuIndex,
            TargetGpuName = retryResult.TargetGpuName,
            BootstrapNote = $"First attempt failed with STATUS_DLL_NOT_FOUND; {bootstrap.Message} Retried once " +
                             $"(this result is the retry)."
        };
    }

    private async Task<Result> RunOnceAsync(
        WindowScreenRect embedRect,
        string framesTimeLogPath,
        CancellationToken ct,
        int killAfterSeconds,
        GravityMarkApi api,
        int passCount,
        int? gpuIndex,
        string? gpuName,
        Action? onRenderWindowReady,
        Action? reassertOverlayTopmost)
    {
        var binDirectory = Path.Combine(VendorDirectory, "bin");
        var exePath = Path.Combine(binDirectory, "GravityMark.exe");
        var sw = Stopwatch.StartNew();

        if (!File.Exists(exePath))
        {
            return new Result
            {
                Api = api,
                Duration = sw.Elapsed,
                LaunchError = $"GravityMark.exe not found at \"{exePath}\". Vendor GravityMark's files " +
                              "under vendor/gravitymark/ (with its own bin/ subfolder intact).",
                TargetGpuIndex = gpuIndex,
                TargetGpuName = gpuName
            };
        }

        // Windowed, not fullscreen, so the render window can be found and positioned over embedRect, same approach as FurmarkRunner. No -close flag: WinTester ends the pass itself once the duration elapses. -info 0 hides GravityMark's on-screen overlay.
        var args =
            $"{api.ToCliFlag()} -fullscreen 0 -fulldesktop 1 -width {embedRect.Width} -height {embedRect.Height} " +
            $"-fps 0 -info 0 -sensors 0 -temporal 1 -benchmark 0 -count {passCount} " +
            $"-times \"{framesTimeLogPath}\"";

        // -device works across every API, unlike FurMark2's --gpu-index (Vulkan/OpenCL/CUDA only); why GravityMark handles the per-GPU sweep here; see GpuStressTestModule for index enumeration.
        if (gpuIndex is { } idx)
            args += $" -device {idx}";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = binDirectory,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true, // suppresses a console window only; the render window is separate.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Opts this process out of Windows Fullscreen Optimizations, independent of the sizing inset in
        // PositionAndLockWindow: a second defense against the same fullscreen-exclusivity contention.
        psi.EnvironmentVariables["__COMPAT_LAYER"] = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

        var output = new StringBuilder();
        // Phase 5.4 (field-issues tracker): a Vulkan-load failure ("can't load vulkan-1.dll") confirmed on
        // real hardware that GravityMark doesn't always exit on its own after logging its top-level
        // "GravityMark::create(): can't ..." failure -- it can just hang until force-killed at the full
        // pass ceiling, wasting minutes per doomed API attempt (worse now that the API fallback ladder
        // retries more cases -- see IsGravityMarkAttemptFailure in GpuStressTestModule). Watching for that
        // line live, rather than only inspecting Output after the fact, lets this method cut the wait
        // short instead of always paying the full killAfterSeconds for an attempt that's already doomed.
        var fatalCreateFailureTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutputLine(string? data)
        {
            if (data is null) return;
            lock (output) output.AppendLine(data);
            if (data.Contains("GravityMark::create():", StringComparison.Ordinal))
                fatalCreateFailureTcs.TrySetResult();
        }
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutputLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutputLine(e.Data);

        // A GravityMark pass launched against an API the iGPU can't run (e.g. Vulkan on an older Intel iGPU)
        // can crash with a blocking "has stopped working" dialog instead of exiting quietly like FurMark
        // does; that dialog has no owner in our UI to dismiss, freezing the whole run. Best-effort
        // mitigation only, via the child-process error-mode flag set below.
        Win32Interop.SuppressChildProcessCrashDialogs();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new Result
            {
                Api = api,
                Duration = sw.Elapsed,
                LaunchError = $"Failed to start \"{exePath}\": {ex.Message}",
                TargetGpuIndex = gpuIndex,
                TargetGpuName = gpuName
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var ctReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort; process may already be exiting on its own */ }
        });

        // Same find-then-place approach as FurmarkRunner; not fatal if it times out, the pass still runs and is just flagged via RenderWindowFound rather than silently swallowed.
        //
        // GravityMark is always positioned as an independent top-level window, never reparented; real-hardware testing confirmed cross-process SetParent reparenting itself (not -fullscreen/WS_DISABLED as earlier suspected) was what killed its Vulkan surface (VK_ERROR_SURFACE_LOST_KHR) once the WinTester GUI's own HWND tore down. FurMark stays on ReparentAsChild since it's a different, unrelated engine (GeeXLab) that has never shown this symptom; a deliberate divergence, not an oversight.
        bool renderWindowFound = false;
        try
        {
            var hwnd = await Win32Interop.WaitForMainWindowHandleAsync(process, TimeSpan.FromSeconds(15), ct);
            if (hwnd != IntPtr.Zero)
            {
                // applyDisabledStyle: false; input lockdown doesn't depend on WS_DISABLED (InputLockdown's global hook is the actual defense), and a Vulkan/D3D engine skipping presentation to a window it sees as disabled remains a live, if secondary, suspect not worth reintroducing.
                Win32Interop.PositionAndLockWindow(hwnd, embedRect, applyDisabledStyle: false);
                renderWindowFound = true;

                // Re-assert WinTester's own always-on-top overlay right after GravityMark's window is positioned, rather than trusting Windows' default topmost-band ordering to hold once a second topmost, monitor-sized window exists; one of three defensive fixes shipped for the GUI-hidden-behind-GravityMark symptom, not confirmed load-bearing on its own.
                reassertOverlayTopmost?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the window ever appeared; process kill above already handles this.
        }

        // Same reasoning as FurmarkRunner.RunAsync's identical callback: the caller's progress countdown starts from here (once the window-find attempt resolves, found or not) instead of from this method's entry, so it tracks GravityMark's actual benchmark pass rather than also counting launch/init latency.
        onRenderWindowReady?.Invoke();

        bool forceKilledAfterCeiling = false;
        try
        {
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var ceilingTask = Task.Delay(TimeSpan.FromSeconds(killAfterSeconds), ct);
            // Grace period after a fatal create() line appears, rather than an instant kill: gives a
            // process that's already mid-shutdown on its own a chance to finish cleanly and lets any
            // trailing output still get captured.
            var fatalGraceTask = fatalCreateFailureTcs.Task.ContinueWith(
                _ => Task.Delay(FatalCreateFailureGrace, ct),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

            var finished = await Task.WhenAny(exitTask, ceilingTask, fatalGraceTask);

            if (finished == fatalGraceTask && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            else if (finished == ceilingTask && !process.HasExited)
            {
                forceKilledAfterCeiling = true;
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // ceilingTask/fatalGraceTask both observe ct; an operator cancel during the wait lands here; the ctReg registration above already terminates the process.
        }

        sw.Stop();
        ct.ThrowIfCancellationRequested();

        bool frameTimeLogProduced;
        try
        {
            frameTimeLogProduced = File.Exists(framesTimeLogPath) && new FileInfo(framesTimeLogPath).Length > 0;
        }
        catch
        {
            frameTimeLogProduced = false;
        }

        return new Result
        {
            Api = api,
            ExitCode = process.HasExited ? process.ExitCode : null,
            Output = output.ToString(),
            Duration = sw.Elapsed,
            WasForceKilledAfterCeiling = forceKilledAfterCeiling,
            RenderWindowFound = renderWindowFound,
            // Always false; GravityMark is never reparented (see RunAsync above). Field kept, rather than removed, so GpuStressTestModule's shared raw-log-building code can read WasReparented off either engine's Result without branching on which engine produced it.
            WasReparented = false,
            FrameTimeLogProduced = frameTimeLogProduced,
            TargetGpuIndex = gpuIndex,
            TargetGpuName = gpuName
        };
    }
}
