using System;
using System.Collections.Generic;
using System.Linq;
using WinTester.Core;
using WinTester.Tests.Gpu;

namespace WinTester.Tests.Power;

// Desktop vs. laptop, from MachineClassifier's broadened determination; drives which fixed-wattage component lines apply (screen, webcam, battery charging are laptop-only; PCI cards are desktop-only) and which Motherboard/RAM/optical-drive wattage figures are used.
public enum MachineKind
{
    Desktop,
    Laptop
}

// Where a PeakPowerResult's wattage came from; surfaced in KeyMetrics/the raw log so the report distinguishes real this-run telemetry from a calibration-pass stand-in.
public enum PowerSampleSource
{
    // Read back from this session's own CPU/GPU test telemetry window, the preferred source.
    SessionTelemetry,

    // This session didn't run the engine or produce a usable window, so PowerCalibrationRunner ran a short, discard-after-peak-capture load pass instead.
    CalibrationPass,

    // Neither source produced a reading (e.g. no matching power sensor on this board); Watts is null and Detail explains why, per SensorSnapshot's "not found vs. genuinely zero" convention.
    Unavailable
}

// One measured peak-power figure (CPU package power or summed GPU power) with its source and a human-readable explanation, for KeyMetrics and the raw-log component breakdown.
public sealed record PeakPowerResult(double? Watts, PowerSampleSource Source, string Detail);

// One line of the fixed-wattage component table; a detected or assumed-absent hardware component, its wattage, and how it was detected. Watts of 0 means not-detected/not-applicable, not an error.
public sealed record ComponentLine(string Name, double Watts, string? Detail = null);

// One-shot Battery Health pull, design vs. current full-charge capacity in Wh, from both root\WMI and SetupAPI/IOCTL (see BatteryHealthReader, which queries both and reports the lower HealthPercent). HasBatteryInterface is false with no battery instance (most desktops), rendering an explicit N/A rather than a misleading 0%.
// CycleCount/SerialNumber/Chemistry/ManufacturerName/DeviceName are IOCTL-only; each is independently null, never a fabricated placeholder, when the battery/driver doesn't report it; render as N/A, matching the record's capacity-field convention.
public sealed record BatteryHealthResult(
    bool HasBatteryInterface,
    double? DesignCapacityWh,
    double? FullChargeCapacityWh,
    double? HealthPercent,
    string Detail,
    int? CycleCount = null,
    string? SerialNumber = null,
    string? Chemistry = null,
    string? ManufacturerName = null,
    string? DeviceName = null);

// Once-per-run system-identity pull (Win32_ComputerSystem/Win32_Bios); the fields powercfg's header table shows, used by BatteryDrainRunner to fill Device Name/Manufacturer (Serial Number as fallback-only) when the battery/driver doesn't report them. Every field independently null on WMI failure or non-Windows host, same convention as BatteryHealthResult.
public sealed record SystemIdentityResult(string? Manufacturer, string? Model, string? SerialNumber);

// Battery voltage chart-axis range; computed once per run from Win32_Battery.DesignVoltage (root\cimv2, a
// different WMI class/namespace than BatteryHealthReader's root\WMI capacity classes) rather than derived
// from live samples, so the axis is a fixed physical range instead of auto-scaling to whatever the run
// happened to observe. MaxVoltageV is the pack's rated full-charge voltage (DesignVoltage as read, no
// adjustment); MinVoltageV is a calculated "0%-under-load" cutoff voltage, see
// BatteryVoltageRangeReader's own comment for the formula. Both null when DesignVoltage couldn't be read
// (no battery, non-Windows host, WMI failure), in which case the report charts fall back to auto-scaling.
public sealed record BatteryVoltageRangeResult(double? MinVoltageV, double? MaxVoltageV, double? DesignVoltageV);

// Sensor-service (LibreHardwareMonitorLib) inputs BatteryHealthReader needs but can't read itself (no ISensorService access, by design; it stays a pure WMI/IOCTL reader). All three independently omit-able; a caller without a live SensorSnapshot just skips this and Read falls back to its two-source behavior.
public sealed record LhmBatteryHint(
    double? DegradationPercent,
    double? RemainingCapacityMwh,
    double? CurrentPercent);

// Final PSU/adapter power budget; every component line, the summed total, the 25%-headroom recommended figure, and the tier-rounded PSU size to recommend. Carries CpuPeak/GpuPeak separately from Components' copies of the same lines so the report can show each peak's PowerSampleSource distinctly.
public sealed record PowerBudgetResult(
    MachineKind MachineKind,
    IReadOnlyList<ComponentLine> Components,
    double TotalWatts,
    double RecommendedWatts,
    // Null when ExceedsTierList is true (budget above the 2000W top tier); see ComponentInventory.RoundUpToNearest50 for that case's figure instead.
    int? RecommendedTierWatts,
    bool ExceedsTierList,
    PeakPowerResult CpuPeak,
    PeakPowerResult GpuPeak,
    // Plain-language description of how the total was obtained (measured platform power vs. summed components); null for the desktop path.
    string? MeasurementBasis = null);

// Power figures observed across one stretch of sensor snapshots (an idle poll, a session test window, or a calibration pass).
// Discrete and integrated GPUs are kept apart: an integrated GPU sits inside the CPU package, so its reading is informational
// and is never added to the package figure. Every field is null when no snapshot carried the reading.
public sealed record PowerWindowStats(
    int SampleCount,
    double? PlatformPeakW,
    double? PackagePeakW,
    double? PackageAvgW,
    double? DiscreteGpuPeakW,
    double? DiscreteGpuAvgW,
    double? IntegratedGpuPeakW)
{
    public static PowerWindowStats FromSnapshots(IEnumerable<SensorSnapshot> snapshots)
    {
        var count = 0;
        var platform = new List<double>();
        var package = new List<double>();
        var discrete = new List<double>();
        var integrated = new List<double>();

        foreach (var snap in snapshots)
        {
            count++;
            if (snap.Cpu?.PlatformPowerW is { } pl) platform.Add(pl);
            if (snap.Cpu?.PackagePowerW is { } pk) package.Add(pk);

            var discretePower = snap.Gpus.Where(g => !GpuNameClassifier.IsLikelyIntegratedGpuName(g.Name)).Select(g => g.PowerW).ToList();
            if (discretePower.Count > 0 && discretePower.All(w => w is not null))
                discrete.Add(discretePower.Sum(w => w!.Value));

            var integratedPower = snap.Gpus.Where(g => GpuNameClassifier.IsLikelyIntegratedGpuName(g.Name)).Select(g => g.PowerW).ToList();
            if (integratedPower.Count > 0 && integratedPower.All(w => w is not null))
                integrated.Add(integratedPower.Sum(w => w!.Value));
        }

        static double? Max(List<double> v) => v.Count > 0 ? v.Max() : null;
        static double? Avg(List<double> v) => v.Count > 0 ? v.Average() : null;

        return new PowerWindowStats(count, Max(platform), Max(package), Avg(package), Max(discrete), Avg(discrete), Max(integrated));
    }
}

// One laptop load pass: the stats observed during it, and the engine launch error when a required engine never started (the stats then describe an idle machine and must not be used as load data).
public sealed record PowerPassResult(PowerWindowStats Stats, string? LaunchError);
