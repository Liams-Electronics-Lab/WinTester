using System;
using System.Collections.Generic;
using WinTester.Core.Fans;

namespace WinTester.Tools.Fans;

// Presents a chassis backend and a GPU backend as one IFanControlService, so the Fan tool window can
// show both sets of fans in a single list without knowing that two different mechanisms produced them.
//
// FanBackendSelector returns exactly one backend, so on a machine where a vendor backend wins (Gigabyte,
// ASUS-laptop, Dell), the generic LibreHardwareMonitor backend — the only thing that can see a discrete
// GPU's fan — would otherwise be dropped entirely. Composing the two avoids that.
//
// Routing is by Id ownership, established from the most recent GetFans() call on each side: every
// backend defines its own Id namespace (LHM uses its globally-unique Identifier string, vendor backends
// use prefixed literals), so there's no collision to arbitrate. An Id from neither side is a no-op,
// matching IFanControlService's own "no-op when id is unknown" contract.
public sealed class CompositeFanBackend : IFanControlService, IFanTierControl, IDisposable
{
    private readonly IFanControlService _chassis;
    private readonly IFanControlService _gpu;

    // Optional tier capability, forwarded from the chassis backend only. A GPU backend never has tiers
    // (LHM exposes GPU fans as plain percentage controls), and mixing two backends' tiers into one
    // button row would make it unclear which hardware a button applies to.
    private readonly IFanTierControl? _chassisTiers;

    private readonly HashSet<string> _gpuIds = new();

    public CompositeFanBackend(IFanControlService chassis, IFanControlService gpu)
    {
        _chassis = chassis;
        _gpu = gpu;
        _chassisTiers = chassis as IFanTierControl;
    }

    // Chassis fans first, GPU fans after — stable order across polls so rows don't reshuffle under a
    // slider drag.
    public IReadOnlyList<FanControlInfo> GetFans()
    {
        var result = new List<FanControlInfo>();

        // A failure on either side must not blank the other.
        try { result.AddRange(_chassis.GetFans()); } catch { }

        try
        {
            var gpuFans = _gpu.GetFans();
            _gpuIds.Clear();
            foreach (var fan in gpuFans) _gpuIds.Add(fan.Id);
            result.AddRange(gpuFans);
        }
        catch
        {
        }

        return result;
    }

    public void SetDuty(string id, double percent)
    {
        if (_gpuIds.Contains(id)) { TryRun(() => _gpu.SetDuty(id, percent)); return; }
        TryRun(() => _chassis.SetDuty(id, percent));
    }

    public void Reset(string id)
    {
        if (_gpuIds.Contains(id)) { TryRun(() => _gpu.Reset(id)); return; }
        TryRun(() => _chassis.Reset(id));
    }

    // Both sides are reset unconditionally and independently — a throw from one must not skip the
    // other. Highest-consequence method in the class: the chassis side may be the Dell backend, which
    // has to re-enable EC thermal management here.
    public void ResetAll()
    {
        TryRun(_chassis.ResetAll);
        TryRun(_gpu.ResetAll);
    }

    // ---- IFanTierControl (forwarded from the chassis backend only) --------------------------------
    //
    // This class always implements IFanTierControl, even when the chassis backend has no tiers.
    // FanControlWindow's check is "implements the interface AND returns a non-empty GetTiers()", so an
    // empty list here correctly produces no tier row.

    public IReadOnlyList<FanTier> GetTiers()
    {
        if (_chassisTiers is null) return Array.Empty<FanTier>();

        try { return _chassisTiers.GetTiers(); }
        catch { return Array.Empty<FanTier>(); }
    }

    public string? ActiveTierId
    {
        get
        {
            if (_chassisTiers is null) return null;

            try { return _chassisTiers.ActiveTierId; }
            catch { return null; }
        }
    }

    public void ApplyTier(string tierId)
    {
        if (_chassisTiers is null) return;
        TryRun(() => _chassisTiers.ApplyTier(tierId));
    }

    // Both sides are disposed, chassis then GPU, and a throw from the first still lets the second run.
    // The chassis backend is the one holding a kernel driver handle or a vendor DLL.
    public void Dispose()
    {
        if (_chassis is IDisposable chassisDisposable) TryRun(chassisDisposable.Dispose);
        if (_gpu is IDisposable gpuDisposable) TryRun(gpuDisposable.Dispose);
    }

    private static void TryRun(Action action)
    {
        try { action(); } catch { }
    }
}
