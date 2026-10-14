using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTester.Tools.BatteryCharge;

// Which reference-curve family a battery's chemistry code maps to. Anything starting "LI" is lithium-family; everything else (including unknown/unparseable codes) falls back to the generalized nickel-family curve; a wrong Li-ion-shaped curve on a NiCd/NiMH pack would misgrade it, while the flatter nickel curve is a safer default on unknown chemistry.
public enum ChemistryFamily { Lithium, Nickel }

// GOOD/OK/BAD/INCONCLUSIVE tiers; INCONCLUSIVE is a distinct grey/neutral state, not a tier on the GOOD-to-BAD scale, and Unknown (no ratio could be computed; no design capacity, or fewer than two samples) is distinct again from both.
public enum BatteryChargeConditionTier { Unknown, Inconclusive, Good, Ok, Bad }

// The "Rating Basis" mini-section shown under the condition banner: starting SoC, duration, which phase reference was used, and whether any part of the reference curve used is still on the cold-start placeholder rather than real accumulated data.
public sealed record RatingBasis(
    ChemistryFamily Chemistry,
    double? StartSocPercent,
    double? DurationMinutes,
    string PhaseReferenceUsed,
    bool UsedProvisionalCurve,
    string? ProvisionalNote,
    bool InconclusiveGateFired);

public sealed record BatteryChargeRating(
    BatteryChargeConditionTier Tier,
    double? Ratio,
    double? ActualMwh,
    double? ExpectedMwh,
    RatingBasis Basis);

// Battery Charge health-rating algorithm: the chemistry branch, the self-calibrating CC/CV-phase lookup tables, and the efficiency-ratio/tier computation with the INCONCLUSIVE gate. Replaces BatteryChargeReportBuilder.ComputeCondition's old flat peak-current-vs-factory comparison, which produced false "degraded" readings on every Quick Test run.
public static class BatteryChargeHealthAlgorithm
{
    // ===================== Chemistry branch =====================

    public static ChemistryFamily ClassifyChemistry(string? chemistryCode)
    {
        if (string.IsNullOrWhiteSpace(chemistryCode)) return ChemistryFamily.Nickel; // Unknown chemistry defaults to the safer flat curve.
        return chemistryCode.Trim().StartsWith("LI", StringComparison.OrdinalIgnoreCase)
            ? ChemistryFamily.Lithium
            : ChemistryFamily.Nickel;
    }

    // The factory_w 0.5C assumption, backed by two real 18650 cell datasheets rather than a pure guess, but still a generic, not per-model, default.
    public const double AssumedFactoryCRate = 0.5;

    public static double? FactoryWatts(double? designCapacityMwh, double nominalVoltage)
    {
        if (designCapacityMwh is not { } d || d <= 0 || nominalVoltage <= 0) return null;
        var designAh = d / 1000.0 / nominalVoltage;
        return AssumedFactoryCRate * designAh * nominalVoltage; // AssumedFactoryCRate * (d/1000) watt-hours-per-hour.
    }

    private const double LithiumCcCvCrossoverSoc = 80.0;
    private const double NickelCcCvCrossoverSoc = 95.0;
    private const double NickelCvFlatFraction = 0.30;

    // ===================== Self-calibrating CC/CV lookup tables =====================

    // Cold-start anchor points for the lithium-family CV-phase placeholder curve: 100% of factory rate at the assumed 80% CC-to-CV crossover, tapering to the one real anchor a prior run derived: ~51% of factory at 94.89% SoC. Interpolated as a smooth exponential (log-linear in fraction-vs-SoC space) between these two points; the same slope continues past the anchor as a documented assumption, since the spec only names behavior up to the anchor.
    private const double ColdStartAnchorSoc = 94.89;
    private const double ColdStartAnchorFraction = 0.506;

    // CC-phase cold-start reference: flat 1.0x factory watts, matching the pre-calibration flat-factory assumption exactly rather than a fitted curve; there's no real anchor point for CC-phase behavior yet, unlike the CV taper above. An upgraded install grades identically to before until real Wide Sweep/Peak Sweep data accumulates and the table starts reflecting this charger's actual CC-phase behavior.
    private const double CcColdStartFraction = 1.0;

    // Rolling-weighted-average cap: once a bucket has this many real samples, a new observation blends in at a fixed 1/(N_MAX+1) weight instead of a shrinking influence. This cap value is a documented, unspecified choice. Shared by both the CC and CV tables.
    private const int BucketSampleCap = 20;

    private static double ColdStartFraction(double socPercent)
    {
        // Exponential through (80, 1.0) and (94.89, 0.506): fraction = exp(a + b*soc).
        var b = Math.Log(ColdStartAnchorFraction) / (ColdStartAnchorSoc - LithiumCcCvCrossoverSoc);
        var a = -b * LithiumCcCvCrossoverSoc;
        return Math.Exp(a + b * socPercent);
    }

    public sealed class CvBucket
    {
        public double Value { get; set; }
        public int SampleCount { get; set; }
    }

    // The self-calibrating SoC lookup tables for the lithium chemistry family, each keyed by 0.5%-SoC bucket: CV band (80-100%) and CC band (below the CC/CV crossover). Persisted as local JSON, local-machine only for now; not synced across stations, not pre-seeded.
    public sealed class ChargeCurveStore
    {
        // On-disk shape predates the CC table below; property name kept as-is so existing per-machine calibration files keep loading without silently dropping accumulated CV data.
        [JsonPropertyName("lithiumBuckets")]
        public Dictionary<string, CvBucket> LithiumBuckets { get; set; } = new();

        [JsonPropertyName("lithiumCcBuckets")]
        public Dictionary<string, CvBucket> LithiumCcBuckets { get; set; } = new();

        [JsonIgnore]
        public bool IsDirty { get; private set; }

        private static string BucketKey(double socPercent) => (Math.Round(socPercent * 2) / 2.0).ToString("0.0");

        // True if this bucket (or the run currently being graded) still relies on the raw cold-start placeholder rather than real accumulated data: feeds the Rating Basis "provisional reference curve" note.
        public bool IsProvisional(double socPercent) => IsProvisionalIn(LithiumBuckets, socPercent);

        // Fraction of factory watts expected at this SoC: real accumulated data if this bucket has any, otherwise the cold-start placeholder curve.
        public double FractionOfFactory(double socPercent) => FractionOfFactoryIn(LithiumBuckets, socPercent, ColdStartFraction(socPercent));

        // Rolling weighted-average update for one observed (SoC%, fraction-of-factory) tick; see BucketSampleCap for the weighting rule.
        public void ObserveTick(double socPercent, double observedFraction) => ObserveTickIn(LithiumBuckets, socPercent, observedFraction, ColdStartFraction(socPercent));

        // CC-band counterparts of the three CV methods above; same bucket shape and rolling-average mechanism, flat cold start (CcColdStartFraction) instead of the CV taper curve.
        public bool IsProvisionalCc(double socPercent) => IsProvisionalIn(LithiumCcBuckets, socPercent);
        public double FractionOfFactoryCc(double socPercent) => FractionOfFactoryIn(LithiumCcBuckets, socPercent, CcColdStartFraction);
        public void ObserveTickCc(double socPercent, double observedFraction) => ObserveTickIn(LithiumCcBuckets, socPercent, observedFraction, CcColdStartFraction);

        private static bool IsProvisionalIn(Dictionary<string, CvBucket> buckets, double socPercent)
        {
            var key = BucketKey(socPercent);
            return !buckets.TryGetValue(key, out var b) || b.SampleCount <= 0;
        }

        private static double FractionOfFactoryIn(Dictionary<string, CvBucket> buckets, double socPercent, double coldStartFraction)
        {
            var key = BucketKey(socPercent);
            if (buckets.TryGetValue(key, out var b) && b.SampleCount > 0)
                return b.Value;
            return coldStartFraction;
        }

        private void ObserveTickIn(Dictionary<string, CvBucket> buckets, double socPercent, double observedFraction, double coldStartFraction)
        {
            if (observedFraction is < 0 or > 5) return; // Guards against a bad sensor tick polluting the table: 5x factory rate isn't plausible.
            var key = BucketKey(socPercent);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new CvBucket { Value = coldStartFraction, SampleCount = 0 };
                buckets[key] = bucket;
            }

            var weight = Math.Min(bucket.SampleCount, BucketSampleCap);
            bucket.Value = (bucket.Value * weight + observedFraction) / (weight + 1);
            bucket.SampleCount++;
            IsDirty = true;
        }

        // --------------------- Persistence ---------------------

        // App-relative calibration\charge_reference_curves.json, resolved against AppContext.BaseDirectory; same portable-path convention as Sessions/brand.json/vendor tools elsewhere in the app. WinTester runs portably from a USB drive on client machines, so no app data may live under %ProgramData%/%AppData% or anywhere keyed to the host machine's user profile.
        public static string DefaultPath()
            => Path.Combine(AppContext.BaseDirectory, "calibration", "charge_reference_curves.json");

        public static ChargeCurveStore Load(string? path = null)
        {
            path ??= DefaultPath();
            try
            {
                if (!File.Exists(path)) return new ChargeCurveStore();
                var json = File.ReadAllText(path);
                var store = JsonSerializer.Deserialize<ChargeCurveStore>(json);
                return store ?? new ChargeCurveStore();
            }
            catch
            {
                // Corrupt or unreadable calibration file; never blocks a run. Starting fresh from the cold-start placeholder is always safe; the file self-heals on the next successful Wide Sweep/Peak Sweep save.
                return new ChargeCurveStore();
            }
        }

        public void Save(string? path = null)
        {
            if (!IsDirty) return;
            path ??= DefaultPath();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                IsDirty = false;
            }
            catch
            {
                // Best-effort; a failed calibration save must never fail or roll back the test run. Whatever this run learned simply isn't persisted for next time; grading already happened against the in-memory store.
            }
        }
    }

    // ===================== Ratio & tiers =====================

    private const double GoodRatioThreshold = 0.85;
    private const double OkRatioThreshold = 0.70;
    private const double InconclusiveStartSocThreshold = 90.0;
    private const double InconclusiveDurationMinutesThreshold = 15.0;

    // One (elapsed-seconds, SoC%, charge-rate-W) tick; deliberately not BatteryChargeSample itself, so this class stays testable/reusable without a hard dependency on that record's full shape.
    public readonly record struct RatingTick(double ElapsedSeconds, double? SocPercent, double? ChargeRateW);

    // Live-tick counterpart to Compute: called instead of Compute for every non-final report write, so a
    // run in progress never gets graded against partial data. Chemistry, starting SoC, and duration-so-far
    // are legitimate to surface while a run is live and are populated here; Tier stays Unknown and no
    // ratio/expected-energy figure is produced until BatteryChargeRunner calls Compute on the final tick.
    public static BatteryChargeRating Pending(string? chemistryCode, IReadOnlyList<RatingTick> ticks)
    {
        var chemistry = ClassifyChemistry(chemistryCode);
        var withSoc = ticks.Where(t => t.SocPercent is not null).ToList();
        double? startSoc = withSoc.Count > 0 ? withSoc[0].SocPercent : null;
        double? durationMin = withSoc.Count >= 2
            ? (withSoc[^1].ElapsedSeconds - withSoc[0].ElapsedSeconds) / 60.0
            : null;

        var basis = new RatingBasis(chemistry, startSoc, durationMin, "N/A", false, null, InconclusiveGateFired: false);
        return new BatteryChargeRating(BatteryChargeConditionTier.Unknown, null, null, null, basis);
    }

    // Computes the full rating for one completed run. store is read-only here [calibration updates happen separately via ObserveCompletedRun, called once by BatteryChargeRunner on final completion. Only ever called with isFinal: true (see BatteryChargeRunner.TryWriteReport)] Pending above covers every mid-run tick.
    public static BatteryChargeRating Compute(
        string? chemistryCode,
        double? capacityDesignMwh,
        double? startMwh,
        double? endMwh,
        double nominalVoltage,
        IReadOnlyList<RatingTick> ticks,
        ChargeCurveStore store)
    {
        var chemistry = ClassifyChemistry(chemistryCode);
        var withSoc = ticks.Where(t => t.SocPercent is not null).ToList();

        if (withSoc.Count < 2 || startMwh is not { } sMwh || endMwh is not { } eMwh)
        {
            return new BatteryChargeRating(BatteryChargeConditionTier.Unknown, null, null, null,
                new RatingBasis(chemistry, withSoc.Count > 0 ? withSoc[0].SocPercent : null, null, "N/A", false,
                    null, InconclusiveGateFired: false));
        }

        var startSoc = withSoc[0].SocPercent!.Value;
        var durationMin = (withSoc[^1].ElapsedSeconds - withSoc[0].ElapsedSeconds) / 60.0;
        var actualMwh = eMwh - sMwh;

        var factoryW = FactoryWatts(capacityDesignMwh, nominalVoltage);

        double expectedMwh = 0;
        var sawCcPhase = false;
        var sawCvPhase = false;
        var usedProvisionalCc = false;
        var usedProvisionalCv = false;

        var crossoverSoc = chemistry == ChemistryFamily.Lithium ? LithiumCcCvCrossoverSoc : NickelCcCvCrossoverSoc;

        for (var i = 1; i < withSoc.Count; i++)
        {
            var prev = withSoc[i - 1];
            var cur = withSoc[i];
            var dtHours = (cur.ElapsedSeconds - prev.ElapsedSeconds) / 3600.0;
            if (dtHours <= 0) continue;

            var socAtTick = cur.SocPercent!.Value;
            double expectedWattsThisTick;

            if (socAtTick < crossoverSoc)
            {
                sawCcPhase = true;
                if (chemistry == ChemistryFamily.Lithium)
                {
                    if (store.IsProvisionalCc(socAtTick)) usedProvisionalCc = true;
                    expectedWattsThisTick = (factoryW ?? 0) * store.FractionOfFactoryCc(socAtTick);
                }
                else
                {
                    expectedWattsThisTick = factoryW ?? 0; // Nickel doesn't self-calibrate on either phase; stays the flat factory reference.
                }
            }
            else
            {
                sawCvPhase = true;
                if (chemistry == ChemistryFamily.Lithium)
                {
                    if (store.IsProvisional(socAtTick)) usedProvisionalCv = true;
                    expectedWattsThisTick = (factoryW ?? 0) * store.FractionOfFactory(socAtTick);
                }
                else
                {
                    usedProvisionalCv = true; // Nickel curve is always a flat, unvalidated placeholder.
                    expectedWattsThisTick = (factoryW ?? 0) * NickelCvFlatFraction;
                }
            }

            expectedMwh += expectedWattsThisTick * dtHours * 1000.0; // Wh to mWh.
        }

        var usedProvisional = usedProvisionalCc || usedProvisionalCv;

        double? ratio = expectedMwh > 0 ? actualMwh / expectedMwh : null;

        var phaseUsed = (sawCcPhase, sawCvPhase) switch
        {
            (true, true) => "CC + CV",
            (true, false) => "CC only",
            (false, true) => "CV only",
            _ => "N/A"
        };

        var inconclusiveGate = startSoc > InconclusiveStartSocThreshold && durationMin < InconclusiveDurationMinutesThreshold;

        string? provisionalNote = chemistry == ChemistryFamily.Nickel
            ? (usedProvisionalCv ? "Nickel-family reference curve is a flat, unvalidated placeholder; no real nickel-chemistry hardware has calibrated it yet." : null)
            : (usedProvisionalCc, usedProvisionalCv) switch
            {
                (true, true) => "Using a provisional reference curve for part of both the CC and CV phase; this improves automatically as more Wide Sweep / Peak Sweep runs complete.",
                (true, false) => "Using a provisional reference curve for part of the CC phase; this improves automatically as more Wide Sweep / Peak Sweep runs complete.",
                (false, true) => "Using a provisional reference curve for part of the CV phase; this improves automatically as more Wide Sweep / Peak Sweep runs complete.",
                _ => null
            };

        var basis = new RatingBasis(chemistry, startSoc, durationMin, phaseUsed, usedProvisional, provisionalNote, inconclusiveGate);

        if (ratio is null)
            return new BatteryChargeRating(BatteryChargeConditionTier.Unknown, null, actualMwh, null, basis);

        if (inconclusiveGate)
            return new BatteryChargeRating(BatteryChargeConditionTier.Inconclusive, ratio, actualMwh, expectedMwh, basis);

        var tier = ratio > GoodRatioThreshold ? BatteryChargeConditionTier.Good
            : ratio >= OkRatioThreshold ? BatteryChargeConditionTier.Ok
            : BatteryChargeConditionTier.Bad;

        return new BatteryChargeRating(tier, ratio, actualMwh, expectedMwh, basis);
    }

    // The self-calibration step; called once, by BatteryChargeRunner, on normal completion of a Wide Sweep or Peak Sweep run only (never Quick Test, and never a run cut short too early to sample anything meaningful). Mutates and saves store in place.
    public static void ObserveCompletedRun(
        string? chemistryCode,
        BatteryChargeSweepMode mode,
        double? capacityDesignMwh,
        double nominalVoltage,
        IReadOnlyList<RatingTick> ticks,
        ChargeCurveStore store,
        string? persistPath = null)
    {
        if (mode == BatteryChargeSweepMode.QuickTest) return; // Quick Test never feeds the table; it only sees a narrow high-SoC slice and would bias the table toward the taper's tail.
        if (ClassifyChemistry(chemistryCode) != ChemistryFamily.Lithium) return; // Only the lithium curve self-calibrates; the nickel branch stays a fixed placeholder.

        var factoryW = FactoryWatts(capacityDesignMwh, nominalVoltage);
        if (factoryW is not { } fw || fw <= 0) return;

        foreach (var t in ticks)
        {
            if (t.SocPercent is not { } soc || t.ChargeRateW is not { } w) continue;
            var fraction = w / fw;
            if (soc < LithiumCcCvCrossoverSoc) store.ObserveTickCc(soc, fraction);
            else store.ObserveTick(soc, fraction);
        }

        store.Save(persistPath);
    }
}
