using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D12;
using Vortice.D3DCompiler;
using Vortice.DXGI;
// Vortice.DXGI and Vortice.Direct3D11 both declare a type named "Feature" (CS0104 on a real build without
// this) -- alias the one CheckFeatureSupport actually needs.
using D3D11Feature = Vortice.Direct3D11.Feature;

namespace WinTester.Tests.Gpu;

// Field-issues tracker Phase 6. The field machine (Intel HD 5500 / Broadwell) can't run GravityMark on
// Direct3D 11 (driver rejects GravityMark's own compute shader -- "D3D11Shader::compile(): can't create
// Compute shader from OCCLUDER_SCALE_SHADER file") or Vulkan (vulkan-1.dll isn't present at all), and the
// operator confirmed by running GravityMark.exe standalone that this isn't a WinTester-side bug: GravityMark
// itself reports no supported graphics API on this hardware. Phases 5.2/5.4 already stop WinTester from
// misreporting those failures as a pass and stop a doomed Vulkan attempt from hanging the full 300s ceiling --
// this doesn't replace that (a real launch is still the ground truth for what actually happens); it trims the
// ApiAttemptOrder up front so a machine like this one doesn't spend a launch-and-fail cycle on an API that's
// already knowable as hopeless, and so the report can say "skipped: not supported on this hardware" instead of
// "didn't render" for a case that was never going to work.
//
// Only Direct3D 11 and Vulkan are probed:
//  - Direct3D 11 is checked with an actual compute-shader compile+create canary (see CanaryComputeShaderHlsl),
//    not just a feature-level/device-creation check. Device creation on its own would NOT have caught the
//    field failure -- the field machine creates a D3D11 device and reports a normal feature level just fine;
//    the failure is specifically the driver's HLSL compiler rejecting a compute shader afterward. A
//    feature-level check alone would have given a false "compatible" verdict on the exact machine this exists
//    for, so this canary is load-bearing, not a nice-to-have.
//  - Vulkan is checked by attempting to load vulkan-1.dll. The only Vulkan failure actually seen in the field
//    (session 170837) was the loader-level "can't load vulkan-1.dll library" -- not an extension/capability
//    mismatch -- so this matches the observed failure mode exactly without pulling in a Vulkan loader/SDK
//    dependency just to ask a question a DLL-presence check answers just as well.
//
// Phase 7.6: Direct3D 12 is now probed too, promoted out of the "deliberately not probed" list below after
// 7.4 gave it a concrete field failure signature: D3D12Adapter::create(): can't create device -- a plain
// device-creation failure on this same Broadwell machine, not a deep shader-compile failure the way D3D11's
// is. That's exactly the class of failure a cheap device-creation-only probe catches reliably (no canary
// shader needed, unlike D3D11), so the earlier "no evidence against D3D12" reasoning no longer applies --
// there is now evidence, and it's evidence a simple probe can act on directly.
//
// Phase 7.7: reading GravityMark's own vendor binary (Tellusim_x64.dll) directly turned up a second,
// stricter Direct3D 12 requirement beyond plain device creation: a distinct error string,
// "D3D12Adapter::create(): can't get ID3D12Device6", separate from the generic "can't create device" the
// field machine actually hit. GravityMark's D3D12 path needs the ID3D12Device6 interface specifically (added
// to the Windows SDK for variable-rate-shading support -- meaningfully newer than plain FL 11_0/12_0 device
// creation), so in principle a machine that creates a base device but can't be queried up to ID3D12Device6
// would pass 7.6's original probe and still fail for real once GravityMark actually launches.
//
// Phase 7.8 attempted to gate on exactly that (device.QueryInterfaceOrNull<ID3D12Device6>()) and reverted it
// before this ever reached a real build: checking the actual Vortice.Windows source directly
// (amerkoleci/Vortice.Windows) shows ID3D12Device1 through ID3D12Device5 and ID3D12Device7 through
// ID3D12Device11 all exist as wrapped COM types, but ID3D12Device6 specifically does not -- Vortice never
// generated a binding for it, so `ID3D12Device6` isn't a type this project can reference at all, not just an
// unverified guess the way 7.2/7.3's earlier errors were. QueryInterfaceOrNull<T>() itself is real and
// correctly used elsewhere in Vortice's own samples (e.g. querying ID3D12Device up to ID3D12Device8), so the
// pattern was sound; there's just no T to plug in for this specific interface without hand-rolling a raw COM
// QueryInterface via ID3D12Device6's GUID and vtable layout -- a materially bigger, riskier addition (a new
// unverified P/Invoke/COM-interop surface, same class of cost OpenGL's probe was already rejected for below)
// for a gate with zero field data behind it (the field machine fails at the earlier, generic device-creation
// step, so this second gate would never even fire there). Not worth that cost for a speculative gate read off
// a binary rather than confirmed against a real failing machine -- ProbeDirect3D12 stays at 7.6's original,
// build-verified, device-creation-only shape. Revisit only if a real field machine creates a base D3D12
// device but still fails GravityMark's D3D12 path, which would be actual evidence for this gate.
//
// Same 7.7 investigation also worked out Direct3D 11's actual floor (Shader Model 5.0 / Feature Level 11_0,
// confirmed via the vs_5_0/ps_5_0/cs_5_0 profile strings next to Tellusim's shader-compile error) and ruled
// out the "-raster 0" workaround 7.6 had floated (the crash reproduces in Rasterization Mode, -raster's own
// default, so there's no non-compute-shader mode left to fall back to) -- neither changes this file's code,
// so they're not repeated here; see the tracker's 7.7.
//
// OpenGL remains deliberately NOT probed, for now: its field failure (Window::create(): can't create
// context, from GravityMark's own log) is a WGL context-creation failure, not a version/extension query a
// capability bit can answer -- reproducing it would mean a new, unverified Win32/WGL P/Invoke surface (no
// Vortice equivalent exists for OpenGL), on top of guessing GravityMark's actual pixel-format/context
// attributes, which is a materially bigger, riskier addition than D3D12's was. 7.7's binary investigation
// didn't turn up anything to lower that cost either (no GL_ARB_* capability-name strings anywhere in
// Tellusim_x64.dll to check against, unlike Vulkan's explicit extension table) -- still nothing to attach a
// cheap probe to.
//
// Caveat shared with the rest of this file's per-GPU-index handling: this probes DXGI's default-enumeration
// adapter only (first non-software adapter, index order). On a multi-GPU system where a GravityMarkStep's
// GpuIndex points at a different physical adapter than DXGI's own enumeration order would pick, this probe's
// verdict may not describe the adapter that step actually targets -- the same unconfirmed-numbering caveat
// PickApiAttemptOrder/GravityMarkStep.GpuIndex already carries (no -listdevices equivalent exists to confirm
// GravityMark's own device numbering against ISensorService's or DXGI's). Not a regression, but worth knowing
// before trusting a pre-flight skip verdict on a multi-GPU machine specifically.
//
// FIELD-CONFIRMED (Phase 7.5): a real run on the Broadwell field machine returned
// TypedUAVLoadAdditionalFormats=False, correlated with the exact D3D11 compute-shader failure this probe
// exists to catch, on the exact machine where the canaries below gave a false "available" (7.4). Promoted
// from logged-only to a real gate in ProbeDirect3D11 -- see the comment there. One field data point, so
// treat this as the best signal found so far, not a settled law; the canaries stay in place as a second
// line of defense for whatever this single bit doesn't cover.
//
// BUILD-VERIFIED AGAINST REAL ERRORS (two rounds so far): the first real build against the actual
// Vortice.DXGI/Vortice.Direct3D11/Vortice.D3DCompiler package surface came back with 11 errors, fixed as
// follows: 'Feature' was ambiguous between Vortice.DXGI.Feature and Vortice.Direct3D11.Feature (fixed with
// the D3D11Feature alias above); the Result+out-Blob overload of Compiler.Compile this file originally
// guessed didn't match the real 6-argument signature (CS1615 on the two out params) -- replaced with the
// throwing 4-arg convenience overload instead of re-guessing that overload's parameter list; and both
// IDXGIFactory1.EnumAdapters1's index and ID3D11DeviceContext.Dispatch's three thread-group-count arguments
// take uint, not int. A second build then caught what the first didn't reach: the throwing 4-arg
// Compiler.Compile overload used above doesn't return a Blob at all -- it returns a plain
// System.ReadOnlyMemory<byte> -- and the actual Blob type in scope here (via Vortice.Direct3D, not
// Vortice.D3DCompiler as first guessed) has no GetBytes() method. RunCanary below no longer touches Blob at
// all, using the ReadOnlyMemory<byte>'s .Span directly. Both builds succeeded after their respective fixes;
// no further build errors reported since.
public enum GpuApiProbeResult
{
    Available,
    Unavailable
}

public sealed record GpuApiAvailability(GravityMarkApi Api, GpuApiProbeResult Result, string? Reason)
{
    public bool IsAvailable => Result == GpuApiProbeResult.Available;
}

public static class GpuApiCompatibilityChecker
{
    // Minimal HLSL compute shader used purely as a canary -- does this driver's D3D11 compute pipeline work
    // at all -- not a copy of GravityMark's own shader (vendor binary; WinTester doesn't have it and
    // couldn't legally ship it if it did). A single-thread RWStructuredBuffer write is an unremarkable
    // compute-shader operation that exercises the same driver code path (HLSL compile -> driver ISA, UAV
    // binding, shader object creation) that failed on the field machine. A driver that can't compile/create
    // this one can't be assumed to handle GravityMark's; a driver that CAN isn't a guarantee GravityMark's
    // own, likely more complex, shader will also compile -- this narrows false "should work" verdicts, it
    // doesn't eliminate them.
    private const string CanaryComputeShaderHlsl = @"
        RWStructuredBuffer<float> Out : register(u0);
        [numthreads(1,1,1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            Out[0] = 1.0f;
        }";

    private static readonly FeatureLevel[] CandidateFeatureLevels =
    {
        FeatureLevel.Level_12_1,
        FeatureLevel.Level_12_0,
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0
    };

    // Entry point: GpuStressTestModule calls this once per BuildRunPlanAsync (only when the GravityMark sweep
    // is actually enabled -- no reason to spend the DXGI/device-create cost otherwise), and threads the result
    // through PickApiAttemptOrder for every GravityMarkStep it builds.
    public static IReadOnlyList<GpuApiAvailability> Probe()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<GpuApiAvailability>(); // degrade gracefully, same posture as DriveTypeDetector

        var results = new List<GpuApiAvailability>();

        try
        {
            results.Add(ProbeVulkan());
        }
        catch (Exception ex)
        {
            // Never let a probe failure take down the whole test plan -- an unknown probe result is treated
            // as "couldn't determine," which PickApiAttemptOrder's filter leaves in the attempt order rather
            // than risk skipping something that might have worked.
            results.Add(new GpuApiAvailability(GravityMarkApi.Vulkan, GpuApiProbeResult.Available,
                $"probe inconclusive ({ex.Message}); left in the attempt order rather than guessing"));
        }

        try
        {
            results.Add(ProbeDirect3D11());
        }
        catch (Exception ex)
        {
            results.Add(new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Available,
                $"probe inconclusive ({ex.Message}); left in the attempt order rather than guessing"));
        }

        try
        {
            results.Add(ProbeDirect3D12());
        }
        catch (Exception ex)
        {
            results.Add(new GpuApiAvailability(GravityMarkApi.Direct3D12, GpuApiProbeResult.Available,
                $"probe inconclusive ({ex.Message}); left in the attempt order rather than guessing"));
        }

        return results;
    }

    [SupportedOSPlatform("windows")]
    private static GpuApiAvailability ProbeVulkan()
    {
        var handle = NativeLibraryProbe.TryLoad("vulkan-1.dll");
        if (handle == IntPtr.Zero)
        {
            return new GpuApiAvailability(GravityMarkApi.Vulkan, GpuApiProbeResult.Unavailable,
                "vulkan-1.dll not found on this system (Vulkan runtime not installed)");
        }

        NativeLibraryProbe.Free(handle);
        return new GpuApiAvailability(GravityMarkApi.Vulkan, GpuApiProbeResult.Available, null);
    }

    // Second canary, added after the operator reported hitting GravityMark's "can't create RenderManager"
    // error directly (standalone GravityMark.exe, same Broadwell machine) even though the field session's
    // raw log already showed that failure originates in a compute-shader compile ("can't create Compute
    // shader from OCCLUDER_SCALE_SHADER"). "Occluder scale" is a common name for a downsample/mip pass in
    // GPU occlusion-culling pipelines, which typically read/write a UAV *texture* (RWTexture2D, often with a
    // Load), not just a UAV buffer -- a meaningfully different driver code path (typed UAV load/store format
    // support) than CanaryComputeShaderHlsl's plain RWStructuredBuffer exercises. This doesn't reproduce
    // GravityMark's actual shader (still don't have it -- vendor binary), but it's a closer analog than the
    // buffer-only canary alone, and catches a driver that handles UAV buffers fine but rejects typed UAV
    // texture access -- a real, separately-toggleable D3D11 capability (TypedUAVLoadAdditionalFormats,
    // queried below) some older integrated drivers restrict.
    private const string CanaryUavTextureShaderHlsl = @"
        RWTexture2D<float> OccluderScale : register(u0);
        [numthreads(8,8,1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            float existing = OccluderScale[id.xy];
            OccluderScale[id.xy] = existing * 0.5f;
        }";

    // Diagnostic-only D3D11 capability snapshot -- the CheckFeatureSupport-style queries. Originally none of
    // these individually gated the Available/Unavailable verdict (see the class remark), but Phase 7.5
    // promoted TypedUAVLoadAdditionalFormats specifically to a real gate after a field run confirmed it: see
    // ProbeDirect3D11 below for why. The rest (Threading, Doubles, UAV slot count, MaxFeatureLevel) remain
    // logged-only, folded into the Reason string alongside whichever check actually decided
    // Available/Unavailable, so a future field run keeps building correlateable data instead of guesses
    // (same shape as Phase 2.3's FurMark --gpuinfo raw-output capture).
    private static (string Summary, bool? TypedUavLoadAdditionalFormats) DescribeFeatureSupport(
        ID3D11Device device, FeatureLevel maxFeatureLevel)
    {
        var parts = new List<string> { $"MaxFeatureLevel={maxFeatureLevel}" };

        try
        {
            var threading = device.CheckFeatureSupport<FeatureDataThreading>(D3D11Feature.Threading);
            parts.Add($"DriverConcurrentCreates={threading.DriverConcurrentCreates}");
            parts.Add($"DriverCommandLists={threading.DriverCommandLists}");
        }
        catch (Exception ex)
        {
            parts.Add($"Threading=query failed ({ex.Message})");
        }

        try
        {
            var doubles = device.CheckFeatureSupport<FeatureDataDoubles>(D3D11Feature.Doubles);
            parts.Add($"FP64Shaders={doubles.DoublePrecisionFloatShaderOps}");
        }
        catch (Exception ex)
        {
            parts.Add($"Doubles=query failed ({ex.Message})");
        }

        bool? typedUavLoadAdditionalFormats = null;
        try
        {
            var options2 = device.CheckFeatureSupport<FeatureDataD3D11Options2>(D3D11Feature.D3D11Options2);
            typedUavLoadAdditionalFormats = options2.TypedUAVLoadAdditionalFormats;
            parts.Add($"TypedUAVLoadAdditionalFormats={options2.TypedUAVLoadAdditionalFormats}");
        }
        catch (Exception ex)
        {
            parts.Add($"D3D11Options2=query failed or unsupported at this feature level ({ex.Message})");
        }

        int uavSlots = maxFeatureLevel >= FeatureLevel.Level_11_1 ? 64 : 8;
        parts.Add($"UAVSlots={uavSlots}");

        return (string.Join(", ", parts), typedUavLoadAdditionalFormats);
    }

    // Compiles, creates, and actually dispatches a compute shader canary, then flushes and checks
    // DeviceRemovedReason -- not just compile+create. GravityMark's own recorded failure was a compile-time
    // error, so compile/create alone would already have caught it, but a driver bug manifesting only at
    // Dispatch/Flush time (a different class of failure this project hasn't seen yet, just guarded against)
    // wouldn't be visible without actually running the shader once.
    //
    // Second real build (see this file's top-of-file build note) corrected this further: the throwing 4-arg
    // Compiler.Compile(source, entryPoint, sourceName, profile) overload returns a System.ReadOnlyMemory<byte>
    // directly -- not a Blob at all, so there's no separate compile-then-create-then-dispose step the way a
    // Blob-returning API would need; CreateComputeShader takes the bytecode's .Span directly.
    private static (bool Ok, string? Reason) RunCanary(
        ID3D11Device device, ID3D11DeviceContext context, string hlsl, string label)
    {
        ReadOnlyMemory<byte> bytecode;
        try
        {
            bytecode = Compiler.Compile(hlsl, "CSMain", $"wintester_gpu_canary_{label}", "cs_5_0");
        }
        catch (Exception ex)
        {
            return (false, $"{label} canary: compile failed ({ex.Message})");
        }

        ID3D11ComputeShader? computeShader = null;
        try
        {
            computeShader = device.CreateComputeShader(bytecode.Span);
        }
        catch (Exception ex)
        {
            return (false, $"{label} canary: compiled but device rejected creating it ({ex.Message})");
        }

        using (computeShader)
        {
            try
            {
                context.CSSetShader(computeShader);
                context.Dispatch(1u, 1u, 1u);
                context.Flush();
            }
            catch (Exception ex)
            {
                return (false, $"{label} canary: created but dispatch/flush failed ({ex.Message})");
            }

            // A driver that silently no-ops a bad dispatch rather than throwing removes the device
            // instead; Flush() above doesn't surface that on its own, so check explicitly.
            var removedReason = device.DeviceRemovedReason;
            if (removedReason.Failure)
                return (false, $"{label} canary: device removed after dispatch ({removedReason})");
        }

        return (true, null);
    }

    // Shared by ProbeDirect3D11 and ProbeDirect3D12: first non-software adapter in DXGI enumeration order --
    // see this class's top-of-file remark on why that may not be the adapter a given GravityMarkStep
    // actually targets on a multi-GPU machine. Returns null if none found or the factory itself couldn't be
    // created; callers treat null the same way this file treats every inconclusive probe -- as "couldn't
    // determine," not as "unavailable."
    [SupportedOSPlatform("windows")]
    private static (IDXGIFactory1? Factory, IDXGIAdapter1? Adapter, string? FailureReason) OpenFirstNonSoftwareAdapter()
    {
        var factoryResult = DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory);
        if (factoryResult.Failure || factory is null)
            return (null, null, $"couldn't create a DXGI factory to probe ({factoryResult})");

        for (uint i = 0; factory.EnumAdapters1(i, out var candidate).Success; i++)
        {
            var desc = candidate.Description1;
            bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
            if (isSoftware)
            {
                candidate.Dispose();
                continue;
            }

            return (factory, candidate, null);
        }

        factory.Dispose();
        return (null, null, "no non-software DXGI adapter enumerated");
    }

    [SupportedOSPlatform("windows")]
    private static GpuApiAvailability ProbeDirect3D11()
    {
        var (factory, adapter, openFailure) = OpenFirstNonSoftwareAdapter();
        if (factory is null || adapter is null)
            return new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Available,
                $"{openFailure}; left in the attempt order rather than guessing");

        using (factory)
        {
            using (adapter)
            {
                var deviceResult = D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.None,
                    CandidateFeatureLevels,
                    out ID3D11Device? device,
                    out FeatureLevel maxFeatureLevel,
                    out ID3D11DeviceContext? context);

                if (deviceResult.Failure || device is null)
                {
                    return new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Unavailable,
                        $"couldn't create a Direct3D 11 device on this adapter ({deviceResult})");
                }

                using (device)
                using (context)
                {
                    var (featureSummary, typedUavLoadAdditionalFormats) =
                        DescribeFeatureSupport(device, maxFeatureLevel);

                    // Phase 7.5: promoted from logged-only to a real gate after a field run on the
                    // Broadwell machine this whole probe exists for came back TypedUAVLoadAdditionalFormats
                    // = False there -- exactly where D3D11 GravityMark fails with the OCCLUDER_SCALE_SHADER
                    // compute-shader compile error, and exactly where both canaries below gave a false
                    // "available" (7.4). One field data point is correlation, not proof, but it's the
                    // specific capability 7.1 already named as the most plausible match for a UAV *texture*
                    // load of a non-mandatory format, it's far cheaper than either canary, and a wrong gate
                    // here costs no more than today's status quo (D3D11 gets attempted and fails exactly as
                    // it does now), while a right gate saves the ~8s launch-and-fail cycle 7.4 measured.
                    // `null` (query failed/unsupported) and `true` both fall through to the canaries below,
                    // same as before.
                    if (typedUavLoadAdditionalFormats == false)
                    {
                        return new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Unavailable,
                            "TypedUAVLoadAdditionalFormats is false on this driver -- confirmed correlated " +
                            $"with the OCCLUDER_SCALE_SHADER compute-shader failure on the field Broadwell " +
                            $"machine (tracker 7.4/7.5) [{featureSummary}]");
                    }

                    // The field failure happens here, not at device creation: feature level and device
                    // creation both succeed fine on the HD 5500, and it's a compute shader compile step
                    // specifically that the driver rejects. Two canaries: a plain UAV-buffer shader
                    // (CanaryComputeShaderHlsl) and a UAV-texture shader closer to an occluder-scale/
                    // downsample pass (CanaryUavTextureShaderHlsl, added after the operator reproduced the
                    // "can't create RenderManager" error running GravityMark.exe standalone on this same
                    // machine) -- either failing is treated as Direct3D 11 being unavailable, since either
                    // is a plausible match for GravityMark's own, still-unseen, shader. Kept as a second
                    // line of defense even after 7.5's TypedUAVLoadAdditionalFormats gate above, in case a
                    // future driver fails for some other reason that capability bit alone (a single field
                    // data point, not a proven-complete list of every way this can fail) doesn't cover.
                    var bufferCanary = RunCanary(device, context, CanaryComputeShaderHlsl, "buffer");
                    if (!bufferCanary.Ok)
                        return new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Unavailable,
                            $"{bufferCanary.Reason} [{featureSummary}]");

                    var textureCanary = RunCanary(device, context, CanaryUavTextureShaderHlsl, "uav-texture");
                    if (!textureCanary.Ok)
                        return new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Unavailable,
                            $"{textureCanary.Reason} [{featureSummary}]");

                    return new GpuApiAvailability(GravityMarkApi.Direct3D11, GpuApiProbeResult.Available,
                        featureSummary);
                }
            }
        }
    }

    // Phase 7.6. Unlike Direct3D 11, the field failure here (tracker 7.4: "D3D12Adapter::create(): can't
    // create device") is a plain device-creation failure -- no deeper shader-compile step to reach, no
    // canary needed. A minimum-feature-level device-creation attempt is the whole probe.
    //
    // Phase 7.8 looked at extending this per 7.7's binary reading (GravityMark's own error strings show it
    // needs the ID3D12Device6 interface specifically, not just a base ID3D12Device) and reverted that idea
    // before it reached a build: checking the real Vortice.Windows source shows ID3D12Device6 was never
    // wrapped by Vortice at all (ID3D12Device1..5 and 7..11 exist, 6 doesn't) -- see this file's top-of-file
    // remark for the full reasoning. This probe stays at its original, build-verified, device-creation-only
    // shape.
    [SupportedOSPlatform("windows")]
    private static GpuApiAvailability ProbeDirect3D12()
    {
        var (factory, adapter, openFailure) = OpenFirstNonSoftwareAdapter();
        if (factory is null || adapter is null)
            return new GpuApiAvailability(GravityMarkApi.Direct3D12, GpuApiProbeResult.Available,
                $"{openFailure}; left in the attempt order rather than guessing");

        using (factory)
        using (adapter)
        {
            var deviceResult = D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0,
                out ID3D12Device? device);

            if (deviceResult.Failure || device is null)
            {
                return new GpuApiAvailability(GravityMarkApi.Direct3D12, GpuApiProbeResult.Unavailable,
                    $"couldn't create a Direct3D 12 device on this adapter ({deviceResult}) -- matches the " +
                    "field Broadwell machine's D3D12Adapter::create(): can't create device failure (7.4)");
            }

            device.Dispose();
            return new GpuApiAvailability(GravityMarkApi.Direct3D12, GpuApiProbeResult.Available, null);
        }
    }
}

// Thin P/Invoke wrapper so ProbeVulkan doesn't need its own DllImport declarations inline. Loading and
// immediately freeing a system DLL by name is the same technique Windows' own "is this runtime installed"
// checks commonly use; it answers exactly the question the field failure raised ("is vulkan-1.dll even
// present") without a Vulkan loader/SDK dependency.
internal static class NativeLibraryProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hLibModule);

    internal static IntPtr TryLoad(string name) => LoadLibraryW(name);

    internal static void Free(IntPtr handle)
    {
        if (handle != IntPtr.Zero) FreeLibrary(handle);
    }
}
