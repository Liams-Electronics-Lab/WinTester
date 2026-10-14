using System;
using System.Collections.Generic;
using WinTester.Core.Fans;

namespace WinTester.Tools.Fans;

// Backend detection order:
//   1-2. Detect a vendor candidate from manufacturer/chassis strings (FanVendorDetector).
//   3.   Probe the candidate backend before trusting it, rather than trusting the manufacturer string
//        alone.
//   4.   Candidate reports zero fans or fails to activate -> fall back to the generic backend; never
//        silently show "no fans" when a fallback might still find something.
// A vendor backend is wired in by adding an entry to the caller's factory dictionary; nothing in this
// class or in FanControlWindow needs to change when that happens. With no factory registered for the
// detected vendor, every machine falls through to the generic backend, and BackendDescription says so
// explicitly rather than implying a vendor backend was tried and failed.
public static class FanBackendSelector
{
    public static FanBackendSelectionResult Select(
        IFanControlService genericBackend,
        IReadOnlyDictionary<FanVendor, Func<IFanControlService>>? vendorBackendFactories = null)
    {
        var vendor = FanVendorDetector.Detect();

        if (vendor != FanVendor.Generic &&
            vendorBackendFactories is not null &&
            vendorBackendFactories.TryGetValue(vendor, out var factory))
        {
            if (TryActivateVendorBackend(factory) is { } vendorBackend)
            {
                // A vendor backend covers chassis fans only — none of them can see a discrete GPU's fan.
                // Replacing the generic backend outright would silently drop the GPU fan on exactly the
                // machines most likely to have one (gaming laptops). If the generic backend can hand out
                // a GPU-only slice of itself, compose the two instead of discarding it.
                var gpuBackend = TryCreateGpuBackend(genericBackend);

                if (gpuBackend is not null)
                {
                    return new FanBackendSelectionResult(
                        vendor, UsedVendorBackend: true,
                        BackendDescription: $"{vendor} + discrete GPU",
                        ActiveBackend: new CompositeFanBackend(vendorBackend, gpuBackend));
                }

                return new FanBackendSelectionResult(
                    vendor, UsedVendorBackend: true,
                    BackendDescription: vendor.ToString(),
                    ActiveBackend: vendorBackend);
            }
        }

        // Generic path needs no composition: the generic backend already enumerates GPU fans alongside
        // everything else (LHM's Computer is created with IsGpuEnabled), so wrapping it in a composite
        // here would list every GPU fan twice.
        var description = vendor == FanVendor.Generic
            ? "Generic"
            : $"Generic — {vendor} support unavailable on this machine";

        return new FanBackendSelectionResult(
            vendor, UsedVendorBackend: false,
            BackendDescription: description,
            ActiveBackend: genericBackend);
    }

    // Null means "no GPU fan worth showing" — no discrete GPU, a passively-cooled card, or a GPU whose
    // driver exposes no writable control (Intel Arc, per LibreHardwareMonitor's own IntelDiscreteGpu,
    // which creates Fan sensors but no Control sensors at all). All of those degrade to the plain
    // vendor-backend result rather than an empty GPU section.
    private static IFanControlService? TryCreateGpuBackend(IFanControlService genericBackend)
    {
        if (genericBackend is not IGpuFanBackendProvider provider) return null;

        try { return provider.CreateGpuFanBackend(); }
        catch { return null; }
    }

    // Null return means "not usable"; the specific reason (construction threw, probe failed, or it
    // genuinely reported zero fans) is deliberately not distinguished to the caller, since every case
    // leads to the same fallback action.
    private static IFanControlService? TryActivateVendorBackend(Func<IFanControlService> factory)
    {
        IFanControlService candidate;
        try
        {
            candidate = factory();
        }
        catch
        {
            return null;
        }

        if (candidate is IFanBackendProbe probe)
        {
            bool activated;
            try
            {
                activated = probe.TryActivate();
            }
            catch
            {
                activated = false;
            }

            if (!activated) return null;
        }

        try
        {
            return candidate.GetFans().Count > 0 ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
