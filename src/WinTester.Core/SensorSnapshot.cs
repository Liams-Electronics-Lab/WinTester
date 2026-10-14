using System;
using System.Collections.Generic;

namespace WinTester.Core;

// Structured, per-category telemetry consumed by ISensorService and rendered by the running-state
// modal's telemetry grid. SensorSample stays a free-form event a module can raise for its own metrics;
// SensorSnapshot is the shared hardware read every module and the UI draw from.
public enum BatteryChargeState
{
    Unknown,
    Charging,
    Discharging,
    AcNoBattery
}

// Numeric fields are nullable rather than defaulting to 0, so the UI can distinguish "sensor not found"
// from "sensor genuinely reads 0" and show "N/A" instead of a misleading value.
// HwInfoPackagePowerW is HWiNFO's own "CPU Package Power", filled only by the HWiNFO decorator and only when HWiNFO
// is running. PackagePowerW stays LibreHardwareMonitor-first for the Power test, sampler and reports; the run
// overlay prefers this field so it shows HWiNFO's figure when there is one. Null otherwise, and absent in old sessions.
// PackagePowerW is the CPU package (RAPL) reading only. PlatformPowerW is the whole-platform reading
// (Intel PSYS, HWiNFO "Total System Power"): it includes the CPU, every GPU and the board, so it must
// never be added to a per-component figure. Trailing and optional so sessions recorded without it still load.
public sealed record CpuTelemetry(
    string Name,
    double? LoadPercent,
    double? PackageTempC,
    double? VrmTempC,
    double? ClockMhz,
    double? PackagePowerW,
    double? PlatformPowerW = null,
    double? HwInfoPackagePowerW = null);

// SharedMemUsedGB/SharedMemTotalGB carry an integrated GPU's borrowed system RAM, as opposed to
// MemUsedGB/MemTotalGB (dedicated VRAM only); null when unsupported. TempC is the die/"Core" sensor;
// HotspotTempC is the separate "Hot Spot" sensor most dedicated cards expose, reading meaningfully
// higher under load — the two are distinct sensors and are carried independently.
public sealed record GpuTelemetry(
    string Name,
    double? LoadPercent,
    double? TempC,
    double? MemUsedGB,
    double? MemTotalGB,
    double? ClockMhz,
    double? PowerW,
    double? SharedMemUsedGB = null,
    double? SharedMemTotalGB = null,
    double? HotspotTempC = null);

// SpeedMhz is the DIMM kit's configured speed in MT/s (e.g. 4800 for DDR5-4800), sourced from WMI
// Win32_PhysicalMemory since LibreHardwareMonitor's generic Memory hardware exposes no clock sensor.
public sealed record MemoryTelemetry(double? UsedGB, double? TotalGB, double? TempC, double? SpeedMhz = null);

// HealthPercent is a drive's SMART-derived wear/health reading (0-100); null when unsupported.
public sealed record StorageTelemetry(string Name, double? TempC, double? HealthPercent = null);

// DegradationPercent, RemainingTimeMinutes, RemainingCapacityMwh, ChargeCurrentMa, ChargeRateW, and
// TemperatureC are independently null when unsupported, or (for the charge-only pair) when not currently
// charging. DischargeCurrentMa mirrors ChargeCurrentMa for the discharge case.
public sealed record BatteryTelemetry(
    double? Percent,
    double? VoltageV,
    BatteryChargeState ChargeState,
    double? DegradationPercent = null,
    double? RemainingTimeMinutes = null,
    double? RemainingCapacityMwh = null,
    double? ChargeCurrentMa = null,
    double? ChargeRateW = null,
    double? TemperatureC = null,
    double? DischargeCurrentMa = null);

// One fan's live RPM reading.
public sealed record FanTelemetry(string Name, double Rpm);

// One polled read of every sensor category currently available. Consumers must treat a nullable/empty
// field as "not available," not as an error.
public sealed class SensorSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public CpuTelemetry? Cpu { get; init; }
    public IReadOnlyList<GpuTelemetry> Gpus { get; init; } = Array.Empty<GpuTelemetry>();
    public MemoryTelemetry? Memory { get; init; }
    public IReadOnlyList<StorageTelemetry> Storages { get; init; } = Array.Empty<StorageTelemetry>();
    public BatteryTelemetry? Battery { get; init; }
    public IReadOnlyList<FanTelemetry> Fans { get; init; } = Array.Empty<FanTelemetry>();

    // Human-readable explanation when the sensor backend looks unhealthy in a way that would explain
    // widespread missing data (e.g. not running as Administrator); null when healthy.
    public string? SensorWarning { get; init; }
}
