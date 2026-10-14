using System.Collections.Generic;

namespace WinTester.Core.Fans;

// Optional capability alongside IFanControlService, in the same "backend opts in, UI reacts" shape as
// IFanBackendProbe. A backend implements this only when its hardware exposes discrete fan modes rather
// than (or in addition to) linear duty — e.g. Gigabyte's EC has Auto/Whisper/Step/Fixed/Turbo modes, and
// on at least one confirmed board the proportional speed registers are written but ignored, so a bare
// 0-100% slider silently under-reports what's actually available.
//
// Tiers and sliders coexist: FanControlWindow always shows per-fan sliders, and additionally shows a row
// of tier buttons only when the active backend implements this interface and returns a non-empty
// GetTiers(). Backends that don't implement it produce no tier row.
public interface IFanTierControl
{
    // Empty list means this backend has no discrete tiers on this machine, treated the same as not
    // implementing the interface — no tier row shown. Never throws.
    IReadOnlyList<FanTier> GetTiers();

    // Id of the tier the backend currently believes the hardware is in, or null when unknown. Read on
    // every poll so the button row reflects live state, including a hardware-side change triggered by a
    // slider move (see FanTier.IsImplicit).
    string? ActiveTierId { get; }

    // Applies a tier directly. No-op when tierId is unknown to this backend. Safe to call repeatedly
    // with the same tier.
    void ApplyTier(string tierId);
}

// One discrete hardware fan mode a backend can be put into.
//
// IsImplicit marks a tier that can't be picked directly but that the hardware can still end up in as a
// side effect of a slider position — rendered as a non-clickable state indicator rather than a button.
//
// Description is shown as the button's tooltip: what the tier physically does (approximate RPM, whether
// it overrides the slider).
public sealed record FanTier(
    string Id,
    string Name,
    string Description,
    bool IsImplicit = false);
