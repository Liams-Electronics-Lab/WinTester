using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinTester.Core;

namespace WinTester.Tests.Gpu;

// GPU stress test. Engine is FurMark2's CLI, process-isolated. FurMark's render window is the workload
// itself, so it is positioned over the app UI and input-locked rather than hidden.
//
// OpenGL baseline + per-GPU Vulkan sweep: --gpu-index applies only to Vulkan/OpenCL/CUDA. OpenGL gets one
// baseline pass; Vulkan gets one --gpu-index-scoped pass per adapter found by FurmarkGpuProbe.
//
// Only the OpenGL baseline counts toward pass/fail. Every Vulkan sweep pass is bonus data, so a
// Vulkan-incapable GPU or an unparseable --gpuinfo probe still yields a valid result off the baseline
// alone. Sweep passes are evaluated with the launch-and-observe pattern (IsEarlyExit) rather than an
// up-front capability probe.
//
// Software renderers are excluded from the sweep unless they are the only option.
//
// VRAM test pass: one pass appended after the baseline and Vulkan sweep, using
// --furmark-vram-test-gb set to the largest tier in SupportedVramTestTiersGb not exceeding the largest
// VRAM size ISensorService reports. No --gpu-index equivalent, so it lands on the driver's default
// adapter. Bonus data; an unreadable VRAM sensor skips the pass with a note.
//
// Sensor-vs-probe GPU cross-check: compares FurmarkGpuProbe's --gpuinfo results against
// ISensorService's GPU enumeration by loose name match. A physical GPU the sensor service sees but no
// swept Vulkan target resembles is flagged as a note.
//
// GravityMark per-GPU sweep: GravityMark's -device flag works across Vulkan/OpenGL/Direct3D11/Direct3D12
// and handles integrated GPUs. Runs after the FurMark plan: one Vulkan pass per physical GPU
// ISensorService detected, index assumed to match enumeration order (no -listdevices equivalent exists
// to confirm this). Bonus data only.
public sealed class GpuStressTestModule : ITestModule
{
    // Total GPU test duration in seconds, split evenly across slots (baseline = 1 slot, each Vulkan
    // sweep target = 1 slot).
    public const int DefaultDurationSeconds = 300;

    // Minimum seconds any single pass gets regardless of slot count.
    public const int MinPassSeconds = 10;

    // Timeout waiting for furmark --gpuinfo to print and exit.
    public static readonly TimeSpan GpuProbeTimeout = TimeSpan.FromSeconds(15);

    // GB tiers furmark.exe --furmark-vram-test-gb accepts. Largest tier not exceeding the GPU's
    // detected VRAM size is used.
    public static readonly int[] SupportedVramTestTiersGb = { 2, 4, 8, 10, 12, 16, 20, 24, 32 };

    // GravityMark's per-GPU phase runs a demo loop (-count set high, no -close) rather than a single
    // self-exiting pass; this is how long WinTester lets it run before force-killing it.
    public const int GravityMarkPassCeilingSeconds = 300;

    // -count value for GravityMark's demo loop, set high enough that it never finishes on its own
    // within GravityMarkPassCeilingSeconds.
    public const int GravityMarkDemoLoopPassCount = 999_999;

    // Phase 7.8 (field-issues tracker): GravityMark is meant to carry most of this test's actual sustained
    // GPU-load duration -- FurMark's own passes above are deliberately sliced thin (perPassSeconds =
    // _durationSeconds / plan.Count) on that assumption. When GravityMark fails on every API/GPU it tried,
    // the GPU only actually got loaded for FurMark's short slice, well under what a "stress test" is meant
    // to deliver, on exactly the machines that most need a real load-bearing check (the ones GravityMark
    // can't run on at all). This is how long FurMark gets re-run for in that case, to recover the missing
    // load time -- 5 minutes, independent of _durationSeconds/perPassSeconds.
    public const int GravityMarkAllFailedFurmarkReRunSeconds = 300;

    // Advanced Mode toggles, not currently exposed in the UI.
    private readonly bool _runOpenGlBaseline;
    private readonly bool _runVulkanSweep;
    private readonly bool _runVramTest;
    private readonly bool _runGravityMarkSweep;

    // Diagnostic toggle: launches GravityMark without -device (single pass against its default
    // adapter) to isolate whether GpuIndex maps correctly to GravityMark's own device numbering.
    private readonly bool _omitGravityMarkDeviceIndex;

    // Re-asserts RunOverlayWindow's topmost z-order after GravityMark's window is positioned. Null-safe no-op if not supplied.
    private readonly Action? _reassertOverlayTopmost;

    private readonly int _durationSeconds;
    private readonly string _vendorDirectory;

    // Vendor directory for GravityMark, separate from FurMark2's. Defaults to vendor/gravitymark next to the exe.
    private readonly string _gravityMarkVendorDirectory;

    private readonly Func<WindowScreenRect>? _getEmbedRect;

    // Supplies live GPU telemetry for VRAM-test sizing and the sensor-vs-probe cross-check. Null disables both.
    private readonly ISensorService? _sensorService;

    // Returns the HWND of a FurmarkEmbedHost to reparent FurMark's render window into, or null/zero if unavailable.
    private readonly Func<IntPtr>? _getParentHwnd;

    private readonly FurmarkRunner _runner;
    private readonly GravityMarkRunner _gravityMarkRunner;
    private CancellationTokenSource? _internalCts;

    // Shared with the Power tile so both tiles' Advanced Mode dialogs stay in sync.
    private readonly GlobalTestOptions _globalOptions;

    public string Id => "gpu";
    public string DisplayName => "GPU";
    public string TileColorKey => "TileGpu";

    public bool SupportsAdvancedSettings => true;

    public GpuTestOptions Options { get; } = new();

    // Read-only window onto _globalOptions for TestOptionsWindow's shared checkboxes.
    public GlobalTestOptions GlobalOptions => _globalOptions;

    public event EventHandler<TestProgressEventArgs>? ProgressChanged;

    // FurMark reports only a Quick Stats block at completion; no interim samples to raise here. Live
    // overlay telemetry comes from MainViewModel's own sensor polling.
#pragma warning disable CS0067
    public event EventHandler<SensorSampleEventArgs>? SensorSampleCaptured;
#pragma warning restore CS0067

    public event EventHandler<TestResultEventArgs>? Completed;

    // One planned FurMark2 invocation. Built up front by BuildRunPlanAsync so the total duration can be
    // split evenly across however many passes are planned.
    private sealed class RunStep
    {
        public required FurmarkEngine Engine { get; init; }
        public required bool CountsTowardPassFail { get; init; }
        public int? GpuIndex { get; init; }
        public string? GpuName { get; init; }

        // Non-null only for the VRAM-test pass.
        public int? VramTestGb { get; init; }

        public string Label =>
            VramTestGb is { } gb
                ? $"VRAM Test ({gb}GB)"
                : Engine == FurmarkEngine.OpenGl
                    ? "OpenGL"
                    : GpuIndex is { } idx
                        ? $"Vulkan GPU{idx}" + (GpuName is { Length: > 0 } n ? $" ({n})" : "")
                        : "Vulkan";
    }

    // durationSeconds: total seconds across every planned pass, or null for DefaultDurationSeconds.
    // vendorDirectory: defaults to vendor/furmark2 next to the exe.
    // getEmbedRect: screen-pixel rect to position FurMark's window over. Null falls back to WindowScreenRect.FullPrimaryScreen.
    // runOpenGlBaseline / runVulkanSweep / runVramTest / runGravityMarkSweep: Advanced Mode toggles, true by default.
    // getParentHwnd: HWND of a FurmarkEmbedHost to reparent into. Null falls back to the positioned-top-level-window approach.
    // sensorService: live GPU telemetry for VRAM-test sizing and the sensor-vs-probe cross-check. Null disables both.
    // gravityMarkVendorDirectory: defaults to vendor/gravitymark next to the exe.
    // reassertOverlayTopmost: invoked after GravityMark's window is positioned so the caller can re-assert its overlay z-order. Null no-ops.
    // globalOptions: shared iGPU-if-dGPU-present/remote-desktop-check settings, same instance handed to PowerTestModule. Null creates a module-private instance.
    public GpuStressTestModule(
        int? durationSeconds = null,
        string? vendorDirectory = null,
        Func<WindowScreenRect>? getEmbedRect = null,
        bool runOpenGlBaseline = true,
        bool runVulkanSweep = true,
        Func<IntPtr>? getParentHwnd = null,
        bool runVramTest = true,
        ISensorService? sensorService = null,
        bool runGravityMarkSweep = true,
        string? gravityMarkVendorDirectory = null,
        bool omitGravityMarkDeviceIndex = false,
        Action? reassertOverlayTopmost = null,
        GlobalTestOptions? globalOptions = null)
    {
        _durationSeconds = durationSeconds ?? DefaultDurationSeconds;
        _getEmbedRect = getEmbedRect;
        _getParentHwnd = getParentHwnd;
        _runOpenGlBaseline = runOpenGlBaseline;
        _runVulkanSweep = runVulkanSweep;
        _runVramTest = runVramTest;
        _sensorService = sensorService;
        _runGravityMarkSweep = runGravityMarkSweep;
        _omitGravityMarkDeviceIndex = omitGravityMarkDeviceIndex;
        _reassertOverlayTopmost = reassertOverlayTopmost;
        _globalOptions = globalOptions ?? new GlobalTestOptions();


        _vendorDirectory = vendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "furmark2");
        _runner = new FurmarkRunner(_vendorDirectory);

        _gravityMarkVendorDirectory = gravityMarkVendorDirectory ?? Path.Combine(AppContext.BaseDirectory, "vendor", "gravitymark");
        _gravityMarkRunner = new GravityMarkRunner(_gravityMarkVendorDirectory);
    }

    // A pass that ended well before its allocated share of the duration with no usable Quick Stats
    // block; a launch failure, a crash, or the GPU/driver not supporting that engine.
    private static bool IsEarlyExit(FurmarkRunner.Result r, int allocatedSeconds) =>
        r.LaunchError is not null ||
        (r.Stats is null && r.Duration.TotalSeconds < Math.Min(10, allocatedSeconds / 2.0));

    // OpenGL context overrides to retry the baseline pass with when the default attempt produces no
    // Quick Stats block on integrated-only hardware.
    private static readonly (int Major, int Minor, bool Core)[] OpenGlCompatibilityAttempts =
    {
        (3, 3, false),
        (2, 1, false)
    };

    // One planned GravityMark invocation, targeting a GPU index assumed to match ISensorService's
    // enumeration order, or none if no sensor snapshot was available. Kept separate from RunStep since
    // GravityMark passes are not sliced out of _durationSeconds.
    private sealed class GravityMarkStep
    {
        public int? GpuIndex { get; init; }
        public string? GpuName { get; init; }

        // API attempt order for this GPU (see PickApiAttemptOrder). First entry launches; a crash/early
        // failure (IsGravityMarkAttemptFailure) tries the next entry until one works or the list is exhausted.
        public required IReadOnlyList<GravityMarkApi> ApiAttemptOrder { get; init; }

        public string Label =>
            GpuIndex is { } idx
                ? $"GravityMark GPU{idx}" + (GpuName is { Length: > 0 } n ? $" ({n})" : "")
                : "GravityMark";
    }

    // Best-effort name-based classification of an integrated GPU. Forwards to the shared
    // GpuNameClassifier so PowerCalibrationRunner can reuse it.
    private static bool IsLikelyIntegratedGpuName(string? name) => GpuNameClassifier.IsLikelyIntegratedGpuName(name);

    // Picks the per-GPU GravityMark API attempt order. Integrated GPUs try Direct3D 11 first (Vulkan is
    // unreliable on some integrated drivers), then fall back through Vulkan/OpenGL/D3D12. Dedicated/unknown
    // GPUs keep Vulkan first.
    //
    // Phase 6 (field-issues tracker): apiProbe (GpuApiCompatibilityChecker.Probe(), computed once per
    // BuildRunPlanAsync) drops an API from the order entirely when it's already known unavailable on this
    // hardware -- e.g. the field machine's Direct3D 11 compute shader failure and missing vulkan-1.dll --
    // rather than spending a launch-and-fail cycle finding that out again. Phase 7.6 added a Direct3D 12
    // probe alongside the existing D3D11/Vulkan ones (see GpuApiCompatibilityChecker's class remark for
    // which three APIs are probed and why OpenGL still isn't); this method didn't need to change for that
    // -- the filter below is generic over whatever GpuApiAvailability entries apiProbe contains. OpenGL is
    // still never in apiProbe, so it always survives the filter and the returned list can never be empty
    // even if every probed API is unavailable, so no separate "GravityMark entirely unsupported" case is
    // needed here.
    private static IReadOnlyList<GravityMarkApi> PickApiAttemptOrder(
        string? gpuName, IReadOnlyList<GpuApiAvailability> apiProbe)
    {
        IReadOnlyList<GravityMarkApi> order = IsLikelyIntegratedGpuName(gpuName)
            ? new[] { GravityMarkApi.Direct3D11, GravityMarkApi.Vulkan, GravityMarkApi.OpenGl, GravityMarkApi.Direct3D12 }
            : new[] { GravityMarkApi.Vulkan, GravityMarkApi.Direct3D11, GravityMarkApi.Direct3D12, GravityMarkApi.OpenGl };

        if (apiProbe.Count == 0) return order; // no probe data (non-Windows, or Probe() itself degraded) -- unfiltered

        var unavailable = apiProbe.Where(p => !p.IsAvailable).Select(p => p.Api).ToHashSet();
        var filtered = order.Where(a => !unavailable.Contains(a)).ToList();
        return filtered.Count > 0 ? filtered : order; // defensive; see remark above on why this shouldn't fire
    }

    // Distinguishes an API attempt that crashed or failed to render (retry the next API) from one that
    // ran but simply produced no frame-time log (not retried).
    // Phase 5.2 (field-issues tracker 4.2): a render window can appear and then the engine can still fail
    // during renderer/shader setup (e.g. GravityMark's "can't create RenderManager" when a compute shader
    // won't compile on an old iGPU driver) -- RenderWindowFound alone doesn't catch that, so this attempt
    // was previously scored a false success and the API fallback ladder never got to try the next entry.
    // A process that exited on its own (not force-killed once the pass ceiling was reached) with a
    // nonzero code is also a failure worth retrying the next API; only needs fields already on Result.
    //
    // Phase 5.4 (field-issues tracker, real re-test after 5.2 shipped): 5.2's D3D11 case did exit on its
    // own (confirmed: the fallback ladder correctly moved on to Vulkan next). But on the same machine,
    // Vulkan hit its own fatal init failure ("can't load vulkan-1.dll") and then never exited on its own
    // at all -- it hung until WinTester force-killed it at the pass ceiling, which the exit-code check
    // above can't distinguish from a genuine full-length successful pass. GravityMark's own log always
    // marks a fatal top-level init failure with a "GravityMark::create():" line (confirmed identical
    // prefix in both the RenderManager and Vulkan-load failures seen so far) regardless of whether the
    // process goes on to exit or hang, so check the captured output for that line too.
    private static bool IsGravityMarkAttemptFailure(GravityMarkRunner.Result r) =>
        r.LaunchError is not null ||
        (!r.RenderWindowFound && r.Duration < TimeSpan.FromSeconds(15)) ||
        (!r.WasForceKilledAfterCeiling && r.ExitCode is { } code && code != 0) ||
        r.Output.Contains("GravityMark::create():", StringComparison.Ordinal);

    // Phase 5.3 (field-issues tracker 4.2/4.3): folds a recognized exit-code cause into the "{prefix}
    // Result" KeyMetrics value itself, not just `notes`/"Notes" (which HtmlReportGenerator filters out of
    // the customer-facing report) -- same reasoning Phase 3.4 already used for the storage-skip message.
    // Empty when the code isn't one NtStatusCodes recognizes, leaving the caller's existing bare label
    // ("no data", "failed") unchanged.
    private static string DescribeGpuFailureSuffix(int? exitCode, TimeSpan duration)
    {
        if (exitCode is not { } code) return "";
        var known = NtStatusCodes.Describe(code);
        return known is null ? "" : $" ({known}, {duration.TotalSeconds:0.0}s)";
    }

    // Builds the ordered list of FurMark2 invocations: the OpenGL baseline (if enabled) followed by one
    // Vulkan pass per GPU FurmarkGpuProbe found, falling back to a single no-index Vulkan pass if the
    // probe found nothing. Also builds the separate GravityMark plan, reusing the same SensorSnapshot
    // read the VRAM-test sizing and cross-check need.
    private async Task<(IReadOnlyList<RunStep> Steps, IReadOnlyList<GravityMarkStep> GravityMarkSteps, IReadOnlyList<string> Notes, string GpuInfoRawOutput, IReadOnlyList<GpuApiAvailability> ApiProbe)> BuildRunPlanAsync(CancellationToken ct)
    {
        var steps = new List<RunStep>();
        var planNotes = new List<string>();
        var gpuInfoRawOutput = "";

        if (_runOpenGlBaseline)
            steps.Add(new RunStep { Engine = FurmarkEngine.OpenGl, CountsTowardPassFail = true });

        // Read once up front, shared by the cross-check below and the VRAM-test sizing further down.
        SensorSnapshot? snapshot = null;
        try
        {
            snapshot = _sensorService?.GetSnapshot();
        }
        catch
        {
            // Bonus data; a snapshot failure just skips the VRAM test and cross-check.
        }

        // Advanced Mode "run GPU test on iGPU if dedicated GPU present" toggle. Computed once and
        // reused by both the Vulkan sweep and GravityMark sweep so they stay in agreement. Only true
        // when the toggle is off and both an integrated and non-integrated GPU are present. The OpenGL
        // baseline is unaffected; it runs on whatever adapter the driver picks by default.
        bool skipIntegratedGpu = !_globalOptions.RunGpuTestOnIntegratedIfDedicatedPresent &&
            snapshot is not null &&
            snapshot.Gpus.Any(g => !GpuNameClassifier.IsLikelyIntegratedGpuName(g.Name)) &&
            snapshot.Gpus.Any(g => GpuNameClassifier.IsLikelyIntegratedGpuName(g.Name));
        if (skipIntegratedGpu)
        {
            planNotes.Add("Advanced Mode: dedicated GPU detected alongside integrated GPU with \"run GPU " +
                "test on iGPU if dedicated GPU present\" off: integrated GPU's Vulkan sweep/GravityMark " +
                "passes skipped.");
        }

        if (_runVulkanSweep)
        {
            IReadOnlyList<FurmarkGpuTarget> targets;
            try
            {
                var probed = await FurmarkGpuProbe.ProbeAsync(_vendorDirectory, GpuProbeTimeout, ct);
                targets = FurmarkGpuProbe.ApplySoftwareRendererFilter(probed.Targets);
                // Captured unconditionally (not just on a mismatch note below) so a field run that hits the
                // "detected by sensors but not matched" case actually leaves behind the real --gpuinfo sample
                // needed to fix FurmarkGpuProbe's regexes (tracker 1.2/2.3) instead of another guess.
                gpuInfoRawOutput = probed.RawOutput;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Treat as "probe found nothing"; bonus data, not worth failing the run over.
                targets = Array.Empty<FurmarkGpuTarget>();
            }

            if (skipIntegratedGpu)
                targets = targets.Where(t => !GpuNameClassifier.IsLikelyIntegratedGpuName(t.Name)).ToList();

            if (targets.Count == 0)
            {
                // Probe found nothing; fall back to one implicit Vulkan pass with no --gpu-index.
                steps.Add(new RunStep { Engine = FurmarkEngine.Vulkan, CountsTowardPassFail = false });
            }
            else
            {
                foreach (var target in targets)
                {
                    steps.Add(new RunStep
                    {
                        Engine = FurmarkEngine.Vulkan,
                        CountsTowardPassFail = false,
                        GpuIndex = target.Index,
                        GpuName = target.Name
                    });
                }
            }

            // Flags a sensor-detected GPU with no matching swept Vulkan target, so a missed adapter is
            // diagnosable rather than silent. Skipped for an integrated GPU when skipIntegratedGpu is
            // true, since that case is already covered by the note above.
            if (snapshot is not null)
            {
                foreach (var gpu in snapshot.Gpus)
                {
                    if (string.IsNullOrWhiteSpace(gpu.Name)) continue;
                    if (skipIntegratedGpu && GpuNameClassifier.IsLikelyIntegratedGpuName(gpu.Name)) continue;
                    bool matched = targets.Any(t => t.Name is { Length: > 0 } n && NamesLooselyMatch(n, gpu.Name));
                    if (!matched)
                    {
                        planNotes.Add(
                            $"\"{gpu.Name}\" detected by sensors but not matched by FurMark's --gpuinfo " +
                            "sweep; no confirmed --gpu-index, so it received no Vulkan pass.");
                    }
                }
            }
        }

        if (_runVramTest)
        {
            var target = PickVramTestTarget(snapshot);
            if (target is { } t)
            {
                steps.Add(new RunStep
                {
                    Engine = FurmarkEngine.OpenGl,
                    CountsTowardPassFail = false,
                    VramTestGb = t.TierGb,
                    GpuName = t.GpuName
                });
            }
            else
            {
                planNotes.Add(snapshot is null
                    ? "VRAM test skipped: sensor data unavailable (service down or read failed)."
                    : "VRAM test skipped: no detected GPU meets the 2GB minimum test tier.");
            }
        }

        // GravityMark's -device works across every API it supports, but no -listdevices-equivalent flag
        // exists to confirm its numbering lines up with ISensorService's enumeration order, one Vulkan
        // pass per sensor-detected physical GPU, index = enumeration position.
        var gravityMarkSteps = new List<GravityMarkStep>();
        IReadOnlyList<GpuApiAvailability> apiProbe = Array.Empty<GpuApiAvailability>();
        if (_runGravityMarkSweep)
        {
            // Phase 6: probe once per plan (system/default-adapter level, not per-GPU-index -- see
            // GpuApiCompatibilityChecker's remark on the multi-GPU caveat this shares with GpuIndex itself),
            // and fold the result into every planNotes-visible reason a step's ApiAttemptOrder came out
            // shorter than the usual four entries.
            apiProbe = GpuApiCompatibilityChecker.Probe();
            foreach (var p in apiProbe.Where(p => !p.IsAvailable))
            {
                planNotes.Add($"GravityMark pre-flight: {p.Api.ToDisplayName()} not attempted on this " +
                              $"hardware ({p.Reason}).");
            }

            if (_omitGravityMarkDeviceIndex)
            {
                // Diagnostic path: single pass against GravityMark's default adapter, even when
                // snapshot.Gpus has multiple entries; running N passes with no -device would just
                // repeat the same result N times.
                gravityMarkSteps.Add(new GravityMarkStep { ApiAttemptOrder = PickApiAttemptOrder(null, apiProbe) });
                planNotes.Add("GravityMark sweep: diagnostic mode (omitGravityMarkDeviceIndex=true), " +
                              "single pass on GravityMark's default adapter instead of one per detected GPU.");
            }
            else if (snapshot is not null && snapshot.Gpus.Count > 0)
            {
                // idx increments for every GPU in the original enumeration, even a filtered-out one, so
                // skipping the iGPU's step doesn't shift the dedicated GPU's index and point -device at
                // the wrong adapter.
                int idx = 0;
                foreach (var gpu in snapshot.Gpus)
                {
                    if (!(skipIntegratedGpu && GpuNameClassifier.IsLikelyIntegratedGpuName(gpu.Name)))
                    {
                        gravityMarkSteps.Add(new GravityMarkStep
                        {
                            GpuIndex = idx,
                            GpuName = gpu.Name,
                            ApiAttemptOrder = PickApiAttemptOrder(gpu.Name, apiProbe)
                        });
                    }
                    idx++;
                }

                if (gravityMarkSteps.Count == 0)
                {
                    // Every entry filtered out; fall back rather than running GravityMark zero times.
                    gravityMarkSteps.Add(new GravityMarkStep { ApiAttemptOrder = PickApiAttemptOrder(null, apiProbe) });
                }
            }
            else
            {
                gravityMarkSteps.Add(new GravityMarkStep { ApiAttemptOrder = PickApiAttemptOrder(null, apiProbe) });
                planNotes.Add("GravityMark sweep: no sensor data to enumerate GPUs: single pass on " +
                              "GravityMark's default device instead of one per GPU.");
            }
        }

        return (steps, gravityMarkSteps, planNotes, gpuInfoRawOutput, apiProbe);
    }

    // Case-insensitive substring match between a FurMark-probed GPU name and a sensor-reported GPU
    // name. Good enough for a diagnostic note, not precise enough for --gpu-index selection.
    private static bool NamesLooselyMatch(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

    // Picks the largest tier in SupportedVramTestTiersGb not exceeding the largest detected VRAM size.
    // Returns null when no snapshot, no VRAM reading, or the reading is below the smallest tier.
    internal static int? PickVramTestTierGb(SensorSnapshot? snapshot) => PickVramTestTarget(snapshot)?.TierGb;

    // Same pick as PickVramTestTierGb, but also returns which GPU's MemTotalGB decided it, so the run plan
    // can record the VRAM-test pass's target GPU up front. FurMark's own VRAM Test pass produces no Quick
    // Stats block (it force-kills at the end rather than reporting a score), so unlike every other pass its
    // KeyMetrics can't supply a "Renderer" after the fact — the report's GPU-phase ribbon needs this recorded
    // at plan time instead, or the pass shows as targeting no GPU even though it always runs on whichever
    // card has the most dedicated VRAM.
    internal static (int TierGb, string GpuName)? PickVramTestTarget(SensorSnapshot? snapshot)
    {
        if (snapshot is null) return null;

        double maxVramGb = 0;
        string? gpuName = null;
        foreach (var gpu in snapshot.Gpus)
        {
            if (gpu.MemTotalGB is { } v && v > maxVramGb)
            {
                maxVramGb = v;
                gpuName = gpu.Name;
            }
        }

        if (maxVramGb <= 0 || gpuName is null) return null;

        int best = 0;
        foreach (var tier in SupportedVramTestTiersGb)
        {
            if (tier <= maxVramGb && tier > best)
                best = tier;
        }

        return best > 0 ? (best, gpuName) : null;
    }

    public async Task StartAsync(TestRunContext context, CancellationToken ct)
    {
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _internalCts.Token;
        var start = DateTime.Now;

        var tempDir = Path.Combine(Path.GetTempPath(), "WinTester_gpu_" + context.SessionId);
        Directory.CreateDirectory(tempDir);

        // RDP sessions don't get normal GPU pipeline access; launching a full-screen render engine
        // (FurMark2/GravityMark) under Remote Desktop is a known display-driver hang trigger, worse on
        // integrated Intel GPUs. Both engines are skipped outright rather than attempted. Not treated as
        // a failure: gated by GlobalTestOptions.EnableRemoteDesktopCheck (default true).
        if (_globalOptions.EnableRemoteDesktopCheck && Win32Interop.IsRunningUnderRemoteSession())
        {
            var skipNotes = new List<string>
            {
                "GPU stress test skipped: running under Remote Desktop: FurMark2/GravityMark render " +
                "passes over RDP risk a display-driver hang. Re-run from the physical console."
            };
            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = true,
                    FailureReason = null,
                    StartTime = start,
                    EndTime = DateTime.Now,
                    KeyMetrics = new Dictionary<string, object> { ["Notes"] = string.Join(" | ", skipNotes) },
                    Flags = new List<string> { "GPU_SKIPPED_REMOTE_SESSION" }
                }
            });
            return;
        }

        var embedRect = _getEmbedRect?.Invoke() ?? WindowScreenRect.FullPrimaryScreen();

        IReadOnlyList<RunStep> plan;
        IReadOnlyList<GravityMarkStep> gravityMarkSteps;
        IReadOnlyList<string> planNotes;
        string gpuInfoRawOutput;
        IReadOnlyList<GpuApiAvailability> apiProbe;
        try
        {
            (plan, gravityMarkSteps, planNotes, gpuInfoRawOutput, apiProbe) = await BuildRunPlanAsync(token);
        }
        catch (OperationCanceledException)
        {
            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = false,
                    FailureReason = "Cancelled by operator",
                    StartTime = start,
                    EndTime = DateTime.Now
                }
            });
            throw;
        }

        if (plan.Count == 0)
        {
            // Both toggles false; nothing to run. Only reachable via the Advanced-Mode-future constructor
            // params, never via the current default-args path, but handled explicitly rather than dividing
            // by zero below.
            Completed?.Invoke(this, new TestResultEventArgs
            {
                Result = new TestResult
                {
                    Passed = false,
                    FailureReason = "GPU test has no engines enabled (OpenGL baseline and Vulkan sweep both disabled).",
                    StartTime = start,
                    EndTime = DateTime.Now
                }
            });
            return;
        }

        // Even split across every planned FurMark pass, one slot per pass. GravityMark passes are not
        // sliced out of _durationSeconds but still get their own progress-bar slot via totalSlots.
        var perPassSeconds = Math.Max(MinPassSeconds, _durationSeconds / plan.Count);
        var totalSlots = plan.Count + gravityMarkSteps.Count;

        var results = new List<FurmarkRunner.Result>();
        var keyMetrics = new Dictionary<string, object>();
        var failures = new List<string>();
        var notes = new List<string>();
        notes.AddRange(planNotes);

        // Phase 6: same reasoning as Phase 3.4's storage-skip message and Phase 5.3's exit-code suffix --
        // a diagnosed reason belongs in a KeyMetrics row, not just folded into "Notes" (which
        // HtmlReportGenerator filters out of the customer-facing report).
        if (apiProbe.Count > 0)
        {
            keyMetrics["GravityMark API Pre-Flight"] = string.Join("; ", apiProbe.Select(p =>
                p.IsAvailable
                    ? $"{p.Api.ToDisplayName()}: available" + (p.Reason is not null ? $" [{p.Reason}]" : "")
                    : $"{p.Api.ToDisplayName()}: not supported on this hardware ({p.Reason})"));
        }

        for (int i = 0; i < plan.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var step = plan[i];
            var stageBase = i * 100.0 / totalSlots;
            var stageSpan = 100.0 / totalSlots;
            var stageText = $"GPU stress: {step.Label} (FurMark2)";

            // SecondsRemaining stays null until the render-window callback starts the countdown, since
            // FurMark2 can take several seconds to launch before its render window exists.
            ProgressChanged?.Invoke(this, new TestProgressEventArgs
            {
                ProgressPercent = stageBase,
                StatusText = $"{stageText} (starting…)",
                SecondsRemaining = null
            });

            using var stageProgressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task? progressTask = null;
            void OnFurmarkWindowReady() =>
                progressTask = ReportStageProgressAsync(DateTime.Now, perPassSeconds, stageBase, stageSpan, stageText, stageProgressCts.Token, _reassertOverlayTopmost);

            var gpuDataCsv = Path.Combine(tempDir, $"furmark_{SanitizeForFileName(step.Label)}_gpu_data.csv");
            // Options.OpenGlVersion (Advanced Mode) picks the initial context for every OpenGL-engine
            // pass. Auto (default) omits the override. Independent of the compatibility-retry chain
            // below (OpenGlCompatibilityAttempts), which still runs if this attempt yields no Quick Stats block.
            var openGlOverride = step.Engine == FurmarkEngine.OpenGl ? Options.ResolveOpenGlContext() : null;
            FurmarkRunner.Result result;
            try
            {
                result = await _runner.RunAsync(
                    step.Engine, perPassSeconds, embedRect, gpuDataCsv, token,
                    gpuIndex: step.GpuIndex, gpuName: step.GpuName, getParentHwnd: _getParentHwnd,
                    vramTestGb: step.VramTestGb, onRenderWindowReady: OnFurmarkWindowReady,
                    openGlVerMajor: openGlOverride?.Major, openGlVerMinor: openGlOverride?.Minor,
                    openGlCoreProfile: openGlOverride?.Core,
                    reassertOverlayTopmost: _reassertOverlayTopmost);
            }
            catch (OperationCanceledException)
            {
                stageProgressCts.Cancel();
                if (progressTask is not null) await progressTask;

                Completed?.Invoke(this, new TestResultEventArgs
                {
                    Result = new TestResult
                    {
                        Passed = false,
                        FailureReason = "Cancelled by operator",
                        StartTime = start,
                        EndTime = DateTime.Now
                    }
                });
                throw;
            }

            stageProgressCts.Cancel();
            if (progressTask is not null) await progressTask;

            results.Add(result);
            var prefix = step.Label;
            bool earlyExit = IsEarlyExit(result, perPassSeconds);

            if (result.LaunchError is { } launchErr)
            {
                keyMetrics[$"{prefix} Result"] = "not run";
                if (step.CountsTowardPassFail) failures.Add($"{prefix}: {launchErr}");
                else notes.Add($"{prefix} skipped: {launchErr}");
                continue;
            }

            if (earlyExit && !step.CountsTowardPassFail)
            {
                // Non-pass/fail-bearing pass exited early (not a failure, just a note).
                keyMetrics[$"{prefix} Result"] = "unavailable (exited early, likely unsupported on this GPU/driver)";
                notes.Add($"{prefix} exited after {result.Duration.TotalSeconds:0.0}s (allocated {perPassSeconds}s) with no usable data; treated as unsupported, not failed.");
                continue;
            }

            // If the default-context OpenGL baseline produced nothing on integrated-only hardware, retry
            // with progressively more-compatible contexts (OpenGlCompatibilityAttempts) before falling
            // through to the failure/note path. Every attempt is recorded into `results`.
            if (result.Stats is null && step.CountsTowardPassFail && step.Engine == FurmarkEngine.OpenGl &&
                step.VramTestGb is null &&
                gravityMarkSteps.Count > 0 && gravityMarkSteps.All(gs => IsLikelyIntegratedGpuName(gs.GpuName)))
            {
                foreach (var (glMajor, glMinor, glCore) in OpenGlCompatibilityAttempts)
                {
                    token.ThrowIfCancellationRequested();
                    notes.Add($"{prefix}: default OpenGL context produced no data on this integrated GPU, " +
                        $"retrying with --opengl-ver-major {glMajor} --opengl-ver-minor {glMinor} " +
                        $"--opengl-core-profile {(glCore ? 1 : 0)}.");

                    var retryCsv = Path.Combine(tempDir,
                        $"furmark_{SanitizeForFileName(step.Label)}_gl{glMajor}{glMinor}_gpu_data.csv");
                    FurmarkRunner.Result retryResult;
                    try
                    {
                        retryResult = await _runner.RunAsync(
                            step.Engine, perPassSeconds, embedRect, retryCsv, token,
                            gpuIndex: step.GpuIndex, gpuName: step.GpuName, getParentHwnd: _getParentHwnd,
                            vramTestGb: step.VramTestGb, onRenderWindowReady: OnFurmarkWindowReady,
                            openGlVerMajor: glMajor, openGlVerMinor: glMinor, openGlCoreProfile: glCore,
                            reassertOverlayTopmost: _reassertOverlayTopmost);
                    }
                    catch (OperationCanceledException)
                    {
                        stageProgressCts.Cancel();
                        if (progressTask is not null) await progressTask;
                        Completed?.Invoke(this, new TestResultEventArgs
                        {
                            Result = new TestResult
                            {
                                Passed = false,
                                FailureReason = "Cancelled by operator",
                                StartTime = start,
                                EndTime = DateTime.Now
                            }
                        });
                        throw;
                    }

                    results.Add(retryResult);
                    if (retryResult.Stats is not null)
                    {
                        notes.Add($"{prefix}: succeeded on retry with OpenGL {glMajor}.{glMinor} " +
                            $"{(glCore ? "core" : "compatibility")} profile.");
                        result = retryResult;
                        break;
                    }
                }
            }

            if (result.Stats is null)
            {
                // Phase 5.3: fold a recognized exit-code cause straight into this KeyMetric's value (e.g.
                // "no data (crashed with an access violation, 35.6s)") instead of a bare "no data" --
                // previously the same diagnosis only reached `msg`/"Notes" below, which the customer-facing
                // report filters out.
                keyMetrics[$"{prefix} Result"] = "no data" + DescribeGpuFailureSuffix(result.ExitCode, result.Duration);
                // The VRAM Test pass in particular never produces a Quick Stats block (it force-kills once
                // VRAM is full rather than reporting a score), so it has no parsed Renderer to fall back on;
                // its target was already known at plan time (see PickVramTestTarget) and is recorded here so
                // the report's GPU-phase ribbon can still attribute the pass to a GPU.
                if (step.GpuName is { Length: > 0 } plannedGpu)
                    keyMetrics[$"{prefix} Renderer"] = plannedGpu;
                var msg = $"{prefix}: no Quick Stats block captured (exit code {DescribeGpuExitCode(result.ExitCode)}, ran {result.Duration.TotalSeconds:0.0}s)";
                if (step.CountsTowardPassFail) failures.Add(msg);
                else notes.Add(msg);

                if (step.CountsTowardPassFail && step.Engine == FurmarkEngine.OpenGl && step.VramTestGb is null &&
                    gravityMarkSteps.Count > 0 && gravityMarkSteps.All(gs => IsLikelyIntegratedGpuName(gs.GpuName)))
                {
                    notes.Add($"{prefix}: all detected GPU(s) are integrated: every OpenGL context attempted " +
                        "(default, then 3.3 and 2.1 compatibility-profile retries) produced no data. " +
                        "GravityMark's sweep below tries Direct3D 11 first and may still produce a result.");
                }

                continue;
            }

            var s = result.Stats;
            keyMetrics[$"{prefix} Score"] = s.Score?.ToString() ?? "n/a";
            keyMetrics[$"{prefix} Resolution"] = s.Resolution ?? "n/a";
            keyMetrics[$"{prefix} Renderer"] = s.Renderer ?? "n/a";
            keyMetrics[$"{prefix} FPS (min/avg/max)"] = $"{s.FpsMin?.ToString() ?? "?"} / {s.FpsAvg?.ToString() ?? "?"} / {s.FpsMax?.ToString() ?? "?"}";
            keyMetrics[$"{prefix} Duration (ms)"] = s.DurationMs?.ToString("0") ?? "n/a";

            // The Quick Stats block lists every GPU FurMark detected, not just this pass's target, 
            // every detected GPU's sub-block is still recorded so nothing is dropped.
            foreach (var g in s.Gpus.Where(g => g.MaxTemperatureC is not null || g.MaxUsagePercent is not null))
            {
                keyMetrics[$"{prefix} GPU{g.Index} Max Temp (C)"] = g.MaxTemperatureC?.ToString("0") ?? "n/a";
                keyMetrics[$"{prefix} GPU{g.Index} Max Usage (%)"] = g.MaxUsagePercent?.ToString("0") ?? "n/a";
                keyMetrics[$"{prefix} GPU{g.Index} Clock (min/max MHz)"] = $"{g.MinCoreClockMhz?.ToString("0") ?? "?"} / {g.MaxCoreClockMhz?.ToString("0") ?? "?"}";
            }

            if (!result.RenderWindowFound)
                notes.Add($"{prefix}: render window not found for on-screen placement/lock: test ran, but wasn't visually positioned or input-locked.");

            if (result.WasForceKilledAfterGrace)
                notes.Add($"{prefix}: force-stopped; did not exit within {perPassSeconds}s + {FurmarkRunner.SelfExitGracePeriod.TotalSeconds:0}s grace.");

            // FurMark2's exit codes aren't documented; a non-zero exit alongside a valid result is
            // informational only, not a failure.
            if (result.ExitCode is { } code && code != 0)
                notes.Add($"{prefix} exited with code {code} despite producing a valid result, treated as informational only.");
        }

        // GravityMark phase runs after the FurMark plan, always bonus data. Uses
        // GravityMarkPassCeilingSeconds as a force-kill safety net rather than a slice of _durationSeconds.
        var gravityMarkResults = new List<GravityMarkRunner.Result>();
        var flags = new List<string>();
        bool anyGravityMarkSuccess = false;
        bool gravityMarkAttempted = gravityMarkSteps.Count > 0;

        for (int i = 0; i < gravityMarkSteps.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var gmStep = gravityMarkSteps[i];
            var stageBase = (plan.Count + i) * 100.0 / totalSlots;
            var stageSpan = 100.0 / totalSlots;
            var gmPrefix = gmStep.Label;

            // Best-effort loop over gmStep.ApiAttemptOrder: each API is tried until one produces more than
            // an early crash/launch failure (IsGravityMarkAttemptFailure), or the list is exhausted
            // (PickApiAttemptOrder sets the order, which differs on an integrated GPU). The loop stays
            // within one progress-bar slot (stageBase/stageSpan unchanged), a fast-crashing attempt
            // barely moves the bar before the next one starts.
            GravityMarkRunner.Result? gmResult = null;
            var attemptNotes = new List<string>();
            for (int attemptIdx = 0; attemptIdx < gmStep.ApiAttemptOrder.Count; attemptIdx++)
            {
                token.ThrowIfCancellationRequested();
                var api = gmStep.ApiAttemptOrder[attemptIdx];
                var stageText = $"GPU stress: {gmPrefix} (GravityMark, {api.ToDisplayName()})";

                // Countdown starts once the render-window callback fires, not at launch.
                ProgressChanged?.Invoke(this, new TestProgressEventArgs
                {
                    ProgressPercent = stageBase,
                    StatusText = $"{stageText} (starting…)",
                    SecondsRemaining = null
                });

                using var gmStageProgressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task? gmProgressTask = null;
                void OnGravityMarkWindowReady() =>
                    gmProgressTask = ReportStageProgressAsync(DateTime.Now, GravityMarkPassCeilingSeconds, stageBase, stageSpan, stageText, gmStageProgressCts.Token, _reassertOverlayTopmost);

                var timesLogPath = Path.Combine(tempDir, $"gravitymark_{SanitizeForFileName(gmPrefix)}_{api}_times.csv");
                GravityMarkRunner.Result attemptResult;
                try
                {
                    // reassertOverlayTopmost fires from onRenderWindowReady, right after GravityMark's
                    // window is positioned, so the caller can re-assert its overlay's z-order.
                    attemptResult = await _gravityMarkRunner.RunAsync(
                        embedRect, timesLogPath, token, killAfterSeconds: GravityMarkPassCeilingSeconds,
                        api: api, passCount: GravityMarkDemoLoopPassCount,
                        gpuIndex: gmStep.GpuIndex, gpuName: gmStep.GpuName,
                        onRenderWindowReady: OnGravityMarkWindowReady,
                        reassertOverlayTopmost: _reassertOverlayTopmost);
                }
                catch (OperationCanceledException)
                {
                    gmStageProgressCts.Cancel();
                    if (gmProgressTask is not null) await gmProgressTask;

                    Completed?.Invoke(this, new TestResultEventArgs
                    {
                        Result = new TestResult
                        {
                            Passed = false,
                            FailureReason = "Cancelled by operator",
                            StartTime = start,
                            EndTime = DateTime.Now
                        }
                    });
                    throw;
                }

                gmStageProgressCts.Cancel();
                if (gmProgressTask is not null) await gmProgressTask;

                gravityMarkResults.Add(attemptResult);
                gmResult = attemptResult;

                bool isFailure = IsGravityMarkAttemptFailure(attemptResult);
                bool hasMoreAttempts = attemptIdx + 1 < gmStep.ApiAttemptOrder.Count;

                if (isFailure && hasMoreAttempts)
                {
                    // This API didn't work on this GPU: try the next one. Any crash dialog is suppressed
                    // by Win32Interop.SuppressChildProcessCrashDialogs inside GravityMarkRunner.
                    attemptNotes.Add($"{gmPrefix}: {api.ToDisplayName()} didn't render " +
                        $"(exit code {DescribeGpuExitCode(attemptResult.ExitCode)}, " +
                        $"{attemptResult.Duration.TotalSeconds:0.0}s), trying " +
                        $"{gmStep.ApiAttemptOrder[attemptIdx + 1].ToDisplayName()} instead.");
                    continue;
                }

                break; // either this attempt worked, or it's the last one in the list either way.
            }

            if (gmResult is null)
                continue; // ApiAttemptOrder was somehow empty; shouldn't happen, PickApiAttemptOrder always returns 4.

            bool succeeded = !IsGravityMarkAttemptFailure(gmResult);
            if (succeeded) anyGravityMarkSuccess = true;

            if (gmResult.LaunchError is { } gmLaunchErr)
            {
                keyMetrics[$"{gmPrefix} Result"] = "failed" + DescribeGpuFailureSuffix(gmResult.ExitCode, gmResult.Duration);
                notes.AddRange(attemptNotes);
                notes.Add($"{gmPrefix} failed on every API it tried ({string.Join(", ", gmStep.ApiAttemptOrder.Select(a => a.ToDisplayName()))}), last error: {gmLaunchErr}");
                continue;
            }

            if (!succeeded)
            {
                // Every attempt failed; records a failed GravityMark pass for this GPU instead of
                // silently reporting "no data." Still bonus data; never counts toward Passed. Phase 5.3:
                // fold the last attempt's exit-code cause into the value itself, same as the FurMark path
                // above, so e.g. a "can't create RenderManager" crash reads as more than "failed" here too.
                keyMetrics[$"{gmPrefix} Result"] = "failed" + DescribeGpuFailureSuffix(gmResult.ExitCode, gmResult.Duration);
                notes.AddRange(attemptNotes);
                notes.Add($"{gmPrefix}: no API in {string.Join(", ", gmStep.ApiAttemptOrder.Select(a => a.ToDisplayName()))} " +
                    "rendered successfully: every attempt either found no render window or exited on its own with an error.");
                continue;
            }

            // Succeeded; report which API worked, plus a note for any API tried first and failed.
            notes.AddRange(attemptNotes);

            // No confirmed score format exists for GravityMark; the most this reports is whether it
            // exited cleanly and produced frame-time data.
            keyMetrics[$"{gmPrefix} API"] = gmResult.Api.ToDisplayName();
            keyMetrics[$"{gmPrefix} Result"] = gmResult.FrameTimeLogProduced ? "completed" : "completed (no frame-time log produced)";
            keyMetrics[$"{gmPrefix} Duration (s)"] = gmResult.Duration.TotalSeconds.ToString("0.0");

            if (!gmResult.RenderWindowFound)
                notes.Add($"{gmPrefix}: render window not found for on-screen placement/lock: pass ran, but wasn't visually positioned or input-locked.");

            if (gmResult.WasForceKilledAfterCeiling)
                notes.Add($"{gmPrefix}: ran for the full {GravityMarkPassCeilingSeconds}s demo-loop duration and was stopped by WinTester as expected.");
            else
                notes.Add($"{gmPrefix}: exited before the {GravityMarkPassCeilingSeconds}s demo-loop duration elapsed: check raw output for a crash/error.");

            if (gmResult.ExitCode is { } gmCode && gmCode != 0)
                notes.Add($"{gmPrefix} exited with code {gmCode}; GravityMark's exit codes aren't documented, treating this as informational only.");
        }

        // Narrow groundwork for a future report-level flag system: raises the two conditions below so
        // they don't need to be re-derived later. Each flag also gets a matching notes line (TestResult.Flags).
        bool anyFurmarkSuccess = results.Any(r => r.Stats is not null);
        if (gravityMarkAttempted && !anyGravityMarkSuccess)
        {
            flags.Add("GPU_GRAVITYMARK_NO_BENCH_RAN");
            notes.Add("FLAG: GravityMark completed no benchmark pass on any detected GPU/API combination.");
        }
        if (anyFurmarkSuccess != anyGravityMarkSuccess && (results.Count > 0 || gravityMarkAttempted))
        {
            flags.Add("GPU_PARTIAL_BENCH_COVERAGE");
            notes.Add($"FLAG: only one GPU benchmark engine could run on this machine, FurMark: " +
                $"{(anyFurmarkSuccess ? "ran" : "did not run")}, GravityMark: {(anyGravityMarkSuccess ? "ran" : "did not run")}.");
        }

        // Phase 7.8 (field-issues tracker): see GravityMarkAllFailedFurmarkReRunSeconds's own remark for why.
        // Gated on the exact same condition as GPU_GRAVITYMARK_NO_BENCH_RAN above, computed from the
        // original plan/results only -- this pass is purely additive from here on and never feeds back into
        // anyFurmarkSuccess, the flags just raised, or Passed/failures, so those keep meaning exactly what
        // they meant before this block existed.
        if (gravityMarkAttempted && !anyGravityMarkSuccess)
        {
            token.ThrowIfCancellationRequested();

            // Reuse whichever step already produced a valid Quick Stats block (most likely to succeed
            // again) rather than guessing a fresh engine/GPU combination. Falls back to a plain OpenGL pass
            // with no --gpu-index -- this module's own existing default/safest invocation (added first,
            // unconditionally, whenever the OpenGL baseline runs at all) -- if literally nothing succeeded
            // the first time either, since some sustained load is better than none.
            //
            // Field bug (2026-09-27 Broadwell re-test): the first cut of this block reused Engine/
            // TargetGpuIndex/TargetGpuName from bestPriorResult but NOT OpenGlVerMajor/OpenGlVerMinor/
            // OpenGlCoreProfile. On a driver that only exposes OpenGL 3.1 by default (this machine),
            // FurMark's default context request fails immediately ("OpenGL 3.2 required but not
            // supported") -- the exact failure the compatibility-context retry ladder above already
            // steps down through to find a context that actually works (here, 2.1 compatibility). Without
            // forwarding that override, the re-run silently re-ran the one context already known not to
            // work on this GPU, exited in ~1s with no data, and looked like "failed to start." Carrying
            // the three OpenGlVer*/CoreProfile fields through fixes it -- bestPriorResult is by definition
            // the pass that produced Stats, so its context fields are exactly the ones proven to work.
            var bestPriorResult = results.FirstOrDefault(r => r.Stats is not null);
            var reRunEngine = bestPriorResult?.Engine ?? FurmarkEngine.OpenGl;
            var reRunGpuIndex = bestPriorResult?.TargetGpuIndex;
            var reRunGpuName = bestPriorResult?.TargetGpuName;
            var reRunOpenGlVerMajor = bestPriorResult?.OpenGlVerMajor;
            var reRunOpenGlVerMinor = bestPriorResult?.OpenGlVerMinor;
            var reRunOpenGlCoreProfile = bestPriorResult?.OpenGlCoreProfile;
            var reRunLabel = "Extended Stress Re-run" +
                (reRunEngine == FurmarkEngine.OpenGl ? " (OpenGL)" : reRunGpuIndex is { } rrIdx
                    ? $" (Vulkan GPU{rrIdx})" : " (Vulkan)");

            var reRunStageText = $"GPU stress: {reRunLabel} (FurMark2, {GravityMarkAllFailedFurmarkReRunSeconds}s " +
                "-- GravityMark unavailable on this hardware)";
            ProgressChanged?.Invoke(this, new TestProgressEventArgs
            {
                ProgressPercent = 99.0,
                StatusText = $"{reRunStageText} (starting\u2026)",
                SecondsRemaining = null
            });

            // No slot was reserved for this in totalSlots -- it's a reactive, not planned, pass -- so it
            // rides a fixed sliver (99-100%) of the progress bar rather than a proportional share; harmless
            // for a bonus pass that can run several minutes past what totalSlots' math otherwise implies is
            // "done."
            using var reRunProgressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task? reRunProgressTask = null;
            void OnReRunWindowReady() =>
                reRunProgressTask = ReportStageProgressAsync(DateTime.Now, GravityMarkAllFailedFurmarkReRunSeconds,
                    99.0, 1.0, reRunStageText, reRunProgressCts.Token, _reassertOverlayTopmost);

            var reRunCsv = Path.Combine(tempDir, $"furmark_{SanitizeForFileName(reRunLabel)}_gpu_data.csv");
            FurmarkRunner.Result reRunResult;
            try
            {
                reRunResult = await _runner.RunAsync(
                    reRunEngine, GravityMarkAllFailedFurmarkReRunSeconds, embedRect, reRunCsv, token,
                    gpuIndex: reRunGpuIndex, gpuName: reRunGpuName, getParentHwnd: _getParentHwnd,
                    openGlVerMajor: reRunOpenGlVerMajor, openGlVerMinor: reRunOpenGlVerMinor,
                    openGlCoreProfile: reRunOpenGlCoreProfile, onRenderWindowReady: OnReRunWindowReady,
                    reassertOverlayTopmost: _reassertOverlayTopmost);
            }
            catch (OperationCanceledException)
            {
                reRunProgressCts.Cancel();
                if (reRunProgressTask is not null) await reRunProgressTask;

                Completed?.Invoke(this, new TestResultEventArgs
                {
                    Result = new TestResult
                    {
                        Passed = false,
                        FailureReason = "Cancelled by operator",
                        StartTime = start,
                        EndTime = DateTime.Now
                    }
                });
                throw;
            }

            reRunProgressCts.Cancel();
            if (reRunProgressTask is not null) await reRunProgressTask;

            // Added to `results` so BuildRawLog picks it up automatically via its existing Engine/
            // TargetGpuIndex-based labelling. When reRunEngine/reRunGpuIndex match an earlier pass (the
            // common case -- reusing whatever already worked), the raw log ends up with two
            // "=== OpenGL ===" (or "=== Vulkan GPUn ===") sections back to back; not disambiguated by
            // label, only by their differing Duration values (perPassSeconds vs. this pass's fixed
            // GravityMarkAllFailedFurmarkReRunSeconds) -- a known minor raw-log-only readability gap, not
            // worth a Result-schema change to fix for a bonus pass.
            //
            // KeyMetrics below are keyed off `reRunLabel` itself ("Extended Stress Re-run (OpenGL)" etc.),
            // not a separate hardcoded prefix -- this has to match the stage label GpuPhaseBuilder parses
            // out of this pass's progress text (reRunStageText above), because GpuPhaseBuilder.Describe
            // looks up "<label> Renderer"/"<label> Result" to attribute a FurMark pass to a GPU when it has
            // no --gpu-index of its own (see GpuPhases.cs). A previous version hardcoded a fixed
            // "GPU Extended Stress Re-run ..." prefix here instead, chosen only to read distinctly from
            // normal passes' prefixes in the raw log/report table; it never matched the actual parsed label,
            // so this pass's segment on the GPU-under-test ribbon always fell through to "no GPU could be
            // resolved" regardless of the GpuPhases.cs regex bug that shipped alongside it. Field run
            // 2026-09-27 (session `212632`, Broadwell) is what surfaced both together: the ribbon showed
            // "No GPU targeted (pass failed to start)" for this pass's whole ~5 minute window while the GPU
            // was actually at 91-100% load throughout. `reRunLabel` already reads distinctly from every
            // normal pass's label ("OpenGL", "Vulkan GPUn", "VRAM Test (Xgb)"), so it satisfies the original
            // distinctness goal without the mismatch.
            results.Add(reRunResult);
            notes.Add($"GravityMark produced no successful pass on any detected GPU/API combination, so " +
                $"FurMark's {reRunLabel} was re-run for {GravityMarkAllFailedFurmarkReRunSeconds}s (vs. the " +
                $"{perPassSeconds}s slice above) to make sure the GPU(s) still got a real sustained-load " +
                "stress pass. Bonus data: never affects Passed.");

            if (reRunResult.Stats is { } rrs)
            {
                keyMetrics[$"{reRunLabel} Score"] = rrs.Score?.ToString() ?? "n/a";
                keyMetrics[$"{reRunLabel} Renderer"] = rrs.Renderer ?? "n/a";
                keyMetrics[$"{reRunLabel} Duration (ms)"] = rrs.DurationMs?.ToString("0") ?? "n/a";
                foreach (var g in rrs.Gpus.Where(g => g.MaxTemperatureC is not null || g.MaxUsagePercent is not null))
                {
                    keyMetrics[$"{reRunLabel} GPU{g.Index} Max Temp (C)"] = g.MaxTemperatureC?.ToString("0") ?? "n/a";
                    keyMetrics[$"{reRunLabel} GPU{g.Index} Max Usage (%)"] = g.MaxUsagePercent?.ToString("0") ?? "n/a";
                }
            }
            else
            {
                keyMetrics[$"{reRunLabel} Result"] =
                    "no data" + DescribeGpuFailureSuffix(reRunResult.ExitCode, reRunResult.Duration);
                notes.Add($"{reRunLabel}: no Quick Stats block captured (exit code " +
                    $"{DescribeGpuExitCode(reRunResult.ExitCode)}, ran {reRunResult.Duration.TotalSeconds:0.0}s) " +
                    "-- informational only, never affects Passed.");
            }
        }

        var end = DateTime.Now;
        bool passed = failures.Count == 0;
        if (notes.Count > 0) keyMetrics["Notes"] = string.Join(" | ", notes);

        var rawLogPath = Path.Combine(tempDir, "gpu_combined.log");
        File.WriteAllText(rawLogPath, BuildRawLog(results, gravityMarkResults, gpuInfoRawOutput));

        Completed?.Invoke(this, new TestResultEventArgs
        {
            Result = new TestResult
            {
                Passed = passed,
                FailureReason = passed ? null : string.Join("; ", failures),
                StartTime = start,
                EndTime = end,
                KeyMetrics = keyMetrics,
                RawLogPath = rawLogPath,
                Flags = flags
            }
        });
    }

    // Mirrors IpdtExitCode.Describe's shared-table fallback (WinTester.Tests.Cpu) for the same class of
    // loader-level failure, so a launch that died before ever producing output (see field-issues tracker
    // 1.1/1.2: dgemm/fma3/GravityMark all exiting with the same STATUS_DLL_NOT_FOUND) reads the same way
    // here as it does in the CPU test's report, instead of just a bare number.
    private static string DescribeGpuExitCode(int? exitCode)
    {
        if (exitCode is not { } code) return "unknown";
        return NtStatusCodes.Describe(code) is { } known ? $"{code} \u2014 {known}" : code.ToString();
    }

    private static string SanitizeForFileName(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    // Phase 5.1 (field-issues tracker 4.1): the single reassertOverlayTopmost() call FurmarkRunner/
    // GravityMarkRunner already fire right after the render window is found isn't enough on its own --
    // a 158s+ FurMark pass, or a 30s+ GravityMark one, is a long window for DWM/Fullscreen Optimizations/
    // the engine's own window re-activation to reclaim topmost afterward, and the real field run this
    // phase is based on confirmed exactly that. Throttled to every ReassertOverlayEveryNTicks ticks of
    // this same 1s progress timer rather than every tick, since toggling Topmost off/on can visibly
    // flicker if done too often.
    private const int ReassertOverlayEveryNTicks = 3;

    private async Task ReportStageProgressAsync(DateTime stageStart, int stageSeconds, double stageBase, double stageSpan, string stageText, CancellationToken token, Action? reassertOverlayTopmost = null)
    {
        try
        {
            var tick = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                tick++;
                if (reassertOverlayTopmost is not null && tick % ReassertOverlayEveryNTicks == 0)
                    reassertOverlayTopmost();

                var elapsed = (DateTime.Now - stageStart).TotalSeconds;
                var stagePct = Math.Min(99.0, elapsed * 100.0 / stageSeconds);
                var overallPct = stageBase + stagePct * stageSpan / 100.0;
                var remaining = Math.Max(0, stageSeconds - elapsed);

                ProgressChanged?.Invoke(this, new TestProgressEventArgs
                {
                    ProgressPercent = Math.Min(99.0, overallPct),
                    StatusText = stageText,
                    SecondsRemaining = remaining
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the stage finishes or the run is cancelled.
        }
    }

    private static string BuildRawLog(List<FurmarkRunner.Result> results, List<GravityMarkRunner.Result>? gravityMarkResults = null, string? gpuInfoRawOutput = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinTester GPU Test (FurMark2)");
        sb.AppendLine($"Running elevated: {(ElevationCheck.IsRunningElevated() ? "Yes" : "No")}");
        sb.AppendLine();

        // Verbatim furmark --gpuinfo output, whether or not FurmarkGpuProbe parsed anything usable from it.
        // No real sample of this flag's output has ever been captured (see FurmarkGpuProbe's doc comment
        // and field-issues tracker 1.2/2.3); kept here so the next field run that hits a sensor/probe
        // name-matching miss leaves behind the sample actually needed to fix the parser, instead of another
        // unconfirmed guess at the output shape.
        if (!string.IsNullOrWhiteSpace(gpuInfoRawOutput))
        {
            sb.AppendLine("=== furmark --gpuinfo (raw) ===");
            sb.AppendLine(gpuInfoRawOutput);
            sb.AppendLine();
        }

        foreach (var r in results)
        {
            var label = r.VramTestGb is { } gb
                ? $"VRAM Test ({gb}GB)"
                : r.Engine == FurmarkEngine.OpenGl
                    ? "OpenGL"
                    : r.TargetGpuIndex is { } idx
                        ? $"Vulkan GPU{idx}" + (r.TargetGpuName is { Length: > 0 } n ? $" ({n})" : "")
                        : "Vulkan";

            sb.AppendLine($"=== {label} ===");
            if (r.LaunchError is { } err)
            {
                sb.AppendLine($"LAUNCH ERROR: {err}");
            }
            else
            {
                sb.AppendLine($"Exit code: {r.ExitCode?.ToString() ?? "(process still tracked as running: killed after grace period)"}");
                sb.AppendLine($"Duration: {r.Duration.TotalSeconds:0.0}s");
                sb.AppendLine($"Render window found/locked: {r.RenderWindowFound}");
                sb.AppendLine($"Reparented as true child (WS_CHILD): {r.WasReparented}");
                sb.AppendLine($"Force-killed after grace period: {r.WasForceKilledAfterGrace}");
                sb.AppendLine($"Quick Stats parsed: {(r.Stats is not null ? "yes" : "no")}");
                sb.AppendLine("--- raw stdout/stderr ---");
                sb.AppendLine(r.Output);
            }
            sb.AppendLine();
        }

        if (gravityMarkResults is { Count: > 0 })
        {
            sb.AppendLine("WinTester GPU Test (GravityMark; bonus data, not pass/fail-bearing)");
            sb.AppendLine();

            foreach (var r in gravityMarkResults)
            {
                // Includes the API in the header since a single GPU can appear more than once here (one
                // entry per best-effort attempt in GravityMarkStep.ApiAttemptOrder); without it, two
                // attempts against the same GPU would print identical, indistinguishable
                // "=== GravityMark GPU0 (...) ===" headers.
                var label = r.TargetGpuIndex is { } idx
                    ? $"GravityMark GPU{idx}" + (r.TargetGpuName is { Length: > 0 } n ? $" ({n})" : "")
                    : "GravityMark";
                label += $" [{r.Api.ToDisplayName()}]";

                sb.AppendLine($"=== {label} ===");
                if (r.LaunchError is { } err)
                {
                    sb.AppendLine($"LAUNCH ERROR: {err}");
                }
                else
                {
                    sb.AppendLine($"API: {r.Api.ToDisplayName()}");
                    sb.AppendLine($"Exit code: {r.ExitCode?.ToString() ?? "(process still tracked as running, killed after ceiling)"}");
                    sb.AppendLine($"Duration: {r.Duration.TotalSeconds:0.0}s");
                    sb.AppendLine($"Render window found/locked: {r.RenderWindowFound}");
                    sb.AppendLine($"Reparented as true child (WS_CHILD): {r.WasReparented}");
                    sb.AppendLine($"Frame-time log produced: {r.FrameTimeLogProduced}");
                    sb.AppendLine($"Force-killed after ceiling: {r.WasForceKilledAfterCeiling}");
                    if (r.BootstrapNote is { Length: > 0 } note)
                        sb.AppendLine($"Dependency bootstrap: {note}");
                    sb.AppendLine("--- raw stdout/stderr ---");
                    sb.AppendLine(r.Output);
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public Task StopAsync()
    {
        _internalCts?.Cancel();
        return Task.CompletedTask;
    }
}
