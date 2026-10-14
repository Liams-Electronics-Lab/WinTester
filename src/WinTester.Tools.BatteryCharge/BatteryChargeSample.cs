using System;

namespace WinTester.Tools.BatteryCharge;

// One 1-second poll of live telemetry during a Battery Charge Test run: percent, voltage, Charge Current (mA), Charge Rate (W), Remaining Capacity (mWh, on BatteryRuntimeStatusReader's 10s-cadence split; carried forward on ticks between, same convention as BatteryDrainRunner), and Battery Temperature (°C). Every field independently null on the same not-found convention as SensorSnapshot.BatteryTelemetry.
public sealed record BatteryChargeSample(
    DateTime Timestamp,
    double ElapsedSeconds,
    double? BatteryPercent,
    double? VoltageV,
    double? ChargeCurrentMa,
    double? ChargeRateW,
    double? RemainingCapacityMwh,
    double? TemperatureC);

// Captured once at run start; the fixed baseline the end-of-run report compares against. Mirrors BatteryDrainStartSnapshot's shape/sourcing (same BatteryHealthReader/SystemIdentityReader merge via BatteryChargeRunner's ResolveIdentity-equivalent logic), but shows both serial numbers separately rather than merging into one fallback-preferred value like the Drain report does.
public sealed record BatteryChargeStartSnapshot(
    DateTime StartTime,
    double? StartPercent,
    double? StartVoltageV,
    double? DesignCapacityMwh,
    double? FullChargeCapacityMwh,
    double? StartingBatteryHealthPercent,
    double? StartingDegradationPercent,
    double? RemainingCapacityMwh,
    int? CycleCount = null,
    string? BatterySerialNumber = null,
    string? SystemSerialNumber = null,
    string? Chemistry = null,
    string? ManufacturerName = null,
    string? DeviceName = null);

// Captured once at run end; a second read so the report can show peak observed Charge Current/Rate across the run (feeds the Factory-vs-Actual table's Measured row) alongside final live readings. Same dual-serial-number shape as BatteryChargeStartSnapshot.
public sealed record BatteryChargeEndSnapshot(
    DateTime EndTime,
    string StopReason,
    double? EndPercent,
    double? EndVoltageV,
    double? FullChargeCapacityMwh,
    double? EndingBatteryHealthPercent,
    double? EndingDegradationPercent,
    double? RemainingCapacityMwh,
    double? PeakChargeCurrentMa,
    double? PeakChargeRateW,
    int? CycleCount = null,
    string? BatterySerialNumber = null,
    string? SystemSerialNumber = null,
    string? Chemistry = null,
    string? ManufacturerName = null,
    string? DeviceName = null);
