using System;

namespace WinTester.Tools.BatteryDrain;

// One 1-second poll of live battery telemetry driving the live graph and log. RemainingCapacityMwh/EstimatedFullChargeCapacityMwh only refresh on a slower 10s cadence [a heavier WMI query, and mWh doesn't shift second-to-second the way percent/voltage can] so BatteryDrainRunner carries the last value forward on ticks between so no row is sparse. GpuTempC is the GPU's "Core" die sensor; GpuHotspotTempC is the separate "Hot Spot" sensor (see SensorSnapshot.GpuTelemetry); the two are independent readings, not a preferred/fallback pair.
public sealed record BatteryDrainSample(
    DateTime Timestamp,
    double ElapsedSeconds,
    double? BatteryPercent,
    double? VoltageV,
    double? RemainingCapacityMwh,
    double? EstimatedFullChargeCapacityMwh,
    double? CpuLoadPercent,
    double? CpuTempC,
    double? GpuLoadPercent,
    double? GpuTempC,
    double? GpuHotspotTempC = null,
    // Feeds the report's Battery Percent Over Time chart discharge-current line, see
    // BatteryTelemetry.DischargeCurrentMa.
    double? DischargeCurrentMa = null);

// Captured once at run start; feeds the live "Start ..." textboxes and the fixed baseline the end-of-run report compares against. CycleCount/SerialNumber/Chemistry/ManufacturerName/DeviceName come from BatteryHealthReader's IOCTL-only fields and are independently null when unreported, never fabricated. AcLineStatus is a fast GetSystemPowerStatus cross-check, not a capacity source.
public sealed record BatteryDrainStartSnapshot(
    DateTime StartTime,
    double? StartPercent,
    double? StartVoltageV,
    double? DesignCapacityMwh,
    double? FullChargeCapacityMwh,
    double? StartingBatteryHealthPercent,
    double? RemainingCapacityMwh,
    int? CycleCount = null,
    string? SerialNumber = null,
    string? Chemistry = null,
    string? ManufacturerName = null,
    string? DeviceName = null,
    string? AcLineStatus = null,
    // Added for the report's Battery Info table "Degradation Level (BMS)" row [mirrors
    // BatteryChargeStartSnapshot.StartingDegradationPercent exactly: LHM's own live "Degradation Level"
    // sensor reading (SensorSnapshot.BatteryTelemetry.DegradationPercent), independent of
    // StartingBatteryHealthPercent's WMI/IOCTL/LHM-merged figure above] the two sources can and do
    // disagree, which is the same reason both are shown side by side on the Charge report.
    double? StartingDegradationPercent = null);

// Captured once at run end (natural stop, operator Stop, or safety-floor stop); a second BatteryHealthReader pull so the report can show design/full-charge/health delta across the run, plus final live readings. See BatteryDrainStartSnapshot for the shared IOCTL fields.
public sealed record BatteryDrainEndSnapshot(
    DateTime EndTime,
    string StopReason,
    double? EndPercent,
    double? EndVoltageV,
    double? FullChargeCapacityMwh,
    double? EndingBatteryHealthPercent,
    double? RemainingCapacityMwh,
    int? CycleCount = null,
    string? SerialNumber = null,
    string? Chemistry = null,
    string? ManufacturerName = null,
    string? DeviceName = null,
    string? AcLineStatus = null,
    // End-of-run mirror of StartingDegradationPercent above.
    double? EndingDegradationPercent = null);
