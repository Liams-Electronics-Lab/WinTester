using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.Tests.Cpu;

// Runs one IPDT compute-module executable as an isolated, hidden child process; never linked or copy-pasted into this binary, regardless of the BSD-3-Clause grant on the source. Every IPDT module already ships as a standalone console EXE with a clean CLI and a standardized exit code contract, so this class is intentionally thin: build args, launch hidden, wait (cancellably), capture output, return.
public sealed class IpdtModuleRunner
{
    // Directory the module EXEs [and their DetectUtils64.dll/libiomp5md.dll runtime dependencies] live in. See CpuStressTestModule's default for where this points.
    public string VendorDirectory { get; }

    public IpdtModuleRunner(string vendorDirectory)
    {
        VendorDirectory = vendorDirectory;
    }

    public sealed class Result
    {
        public required string ModuleName { get; init; }
        public int? ExitCode { get; init; }
        public string Output { get; init; } = "";
        public TimeSpan Duration { get; init; }

        // Set when the module EXE itself couldn't be found or started; a missing/broken vendor-file condition, distinct from the module running and returning a fail exit code. The report should say which of those two happened, not fold both into "exit code 1".
        public string? LaunchError { get; init; }

        // Set only when this run hit STATUS_DLL_NOT_FOUND and a VcRedistBootstrap retry was attempted (see
        // RunAsync); null on every normal run. Surfaced so the raw log can say what happened rather than
        // silently doubling a module's real duration on the rare machine this fires on.
        public string? BootstrapNote { get; init; }
    }

    // Modules whose compute kernel is linked against Intel MKL (confirmed by source inspection; dgemm/Main.cpp calls cblas_dgemm via mkl.h; none of the other compute modules include MKL at all). This matters for AMD/non-Intel CPUs: MKL has a long-documented history of CPUID-based dispatch that silently drops non-"GenuineIntel" CPUs to a slower code path (e.g. SSE-only) even when the CPU supports AVX2/AVX512, regardless of any vendor gate in the surrounding IPDT module code. RunAsync works around this with the documented MKL_ENABLE_INSTRUCTIONS/MKL_DEBUG_CPU_TYPE environment-variable overrides when launching one of these modules on a non-Intel vendor.
    public static readonly IReadOnlyCollection<string> MklLinkedModules = new HashSet<string>(
        new[] { "dgemm" }, StringComparer.OrdinalIgnoreCase);

    // Runs one duration-controlled IPDT module (-s <seconds> -resultName <file>, a custom result file so concurrent/repeated runs in burn-in mode don't clobber each other). Always hidden [no third-party window is ever shown to the operator. vendor is used only to decide whether the MKL non-Intel dispatch workaround (see MklLinkedModules) needs to be applied for this module] it never gates whether the module runs at all. Cancellation kills the child process rather than waiting for it to notice, since these are CPU-bound native compute loops with no cancellation awareness of their own.
    //
    // On STATUS_DLL_NOT_FOUND (0xC0000135, see NtStatusCodes/field-issues tracker 1.1/2.2), attempts
    // VcRedistBootstrap's silent installer fallback once and, if it reports success, retries this same
    // launch exactly once before giving up. A machine that doesn't hit this failure mode never invokes the
    // bootstrap at all (it's purely reactive) and pays no extra cost.
    public async Task<Result> RunAsync(string moduleName, int durationSeconds, string resultFilePath, CancellationToken ct, CpuVendor vendor = CpuVendor.Unknown)
    {
        var result = await RunOnceAsync(moduleName, durationSeconds, resultFilePath, ct, vendor);

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
                ModuleName = result.ModuleName,
                ExitCode = result.ExitCode,
                Output = result.Output,
                Duration = result.Duration,
                LaunchError = result.LaunchError,
                BootstrapNote = bootstrap.Message
            };
        }

        var retryResult = await RunOnceAsync(moduleName, durationSeconds, resultFilePath, ct, vendor);
        return new Result
        {
            ModuleName = retryResult.ModuleName,
            ExitCode = retryResult.ExitCode,
            Output = retryResult.Output,
            Duration = retryResult.Duration,
            LaunchError = retryResult.LaunchError,
            BootstrapNote = $"First attempt failed with STATUS_DLL_NOT_FOUND; {bootstrap.Message} Retried once " +
                             $"(this result is the retry)."
        };
    }

    private async Task<Result> RunOnceAsync(string moduleName, int durationSeconds, string resultFilePath, CancellationToken ct, CpuVendor vendor)
    {
        var exePath = Path.Combine(VendorDirectory, moduleName + ".exe");
        var sw = Stopwatch.StartNew();

        if (!File.Exists(exePath))
        {
            return new Result
            {
                ModuleName = moduleName,
                ExitCode = null,
                Duration = sw.Elapsed,
                LaunchError =
                    $"Module executable not found at \"{exePath}\". Vendor the built IPDT module " +
                    "(with the LoadLibraryA path patched to load DetectUtils64.dll from its own " +
                    "directory, plus libiomp5md.dll alongside it) before running the CPU test."
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = VendorDirectory,
            Arguments = $"-s {durationSeconds} -resultName \"{resultFilePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        // Non-Intel MKL dispatch workaround for MklLinkedModules. Forces AVX2 (not AVX512, unsupported
        // on pre-Zen4 AMD) so MKL doesn't silently fault on non-Intel CPUs. Both env vars are set since
        // different MKL builds honor one or the other.
        if (vendor != CpuVendor.GenuineIntel && MklLinkedModules.Contains(moduleName))
        {
            psi.EnvironmentVariables["MKL_ENABLE_INSTRUCTIONS"] = "AVX2";
            psi.EnvironmentVariables["MKL_DEBUG_CPU_TYPE"] = "5";
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new Result
            {
                ModuleName = moduleName,
                ExitCode = null,
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

        await process.WaitForExitAsync(CancellationToken.None);
        sw.Stop();

        // Kill-on-cancel above lets the process actually exit rather than hanging the run; still propagates cancellation out to the caller once it has, so CpuStressTestModule's cancel path (mirroring MockTestModule's) runs.
        ct.ThrowIfCancellationRequested();

        return new Result
        {
            ModuleName = moduleName,
            ExitCode = process.ExitCode,
            Output = output.ToString(),
            Duration = sw.Elapsed
        };
    }
}
