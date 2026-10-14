using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinTester.Core.Session;

// The curated HWiNFO data points the report consumes. WHEA (Windows Event Log) is not listed here; it has
// its own record type.
public enum HwInfoDataPoint
{
    CpuPackageThermalThrottling,
    CpuCoreThermalThrottling,
    CpuPackageCriticalTemperature,
    CpuCoreCriticalTemperature,
    CpuPackagePowerLimitExceeded,
    CpuCorePowerLimitExceeded,
    PchTemperature,
    GpuThermalLimit,
    GpuPowerLimit,
    GpuPerformanceLimiter,
    PcieErrorCounter,
    PcieLinkSpeed,
    IGpuThrottleReason,

    // CPU PROCHOT assertion ("IA: PROCHOT" on the Performance Limit Reasons sensor). Appended last; the enum is
    // persisted by name (JsonStringEnumConverter), so position doesn't matter for old sessions, but there's no
    // reason to reshuffle it either.
    CpuProcHot,

    // Integrated GPU core temperature (the iGPU sensor's "GPU Core Temperature" reading). Plotted on the report's
    // Temperature chart. HWiNFO supplies it because the sensor library only exposes an Intel iGPU temperature when
    // Intel's graphics telemetry library is installed on the machine.
    IGpuTemperature,

    // --- Sensor Fallback taxonomy (WINTESTER_SENSOR_FALLBACK_TRACKER.md Phase A) ---
    // The numeric metrics confirmed missing from LibreHardwareMonitor's SensorSnapshot on the Broadwell
    // field machine. Appended here purely so they show up in a session's HwInfoEvents alongside the flags
    // already tracked above, for diagnostic visibility — HwInfoSensorResolver (Phase B) does not read
    // through this mapped channel; it matches the raw shared-memory readings directly, so these dp codes
    // are not load-bearing for the actual SensorSnapshot fallback, only for this diagnostic side-channel.
    CpuPackageTemperature,
    CpuPackagePower,
    CpuClock,
    IGpuPower,
    IGpuClock,
    GpuTemperature,
    GpuPower,
    GpuClock,
    MemoryTemperature,
    BatteryTemperature,

    // Not yet classified by HwInfoLabelMap: HWiNFO's shared-memory ReadingType enum has no distinct
    // percentage/"Level" type (see HwInfoSensorType), and the "Other" bucket that would carry a drive
    // health reading is already used for Yes/No flags, so telling the two apart needs a Unit check
    // ("%" vs. "Yes/No") this classifier doesn't do yet. Reserved for when a real drive-health label is
    // confirmed; HwInfoSensorResolver attempts this metric directly against raw readings regardless.
    StorageHealthPercent
}

// One mapped reading at one tick. Value is 0/1 for event flags, a count for error counters, and the
// reading's own unit otherwise. Short JSON names keep the per-tick line small over long burn-in runs.
public sealed class HwInfoEventReading
{
    [JsonPropertyName("dp")]
    public HwInfoDataPoint DataPoint { get; init; }

    // Display label, unique within a sample (e.g. "Core 3 Thermal Throttling").
    [JsonPropertyName("lb")]
    public string Label { get; init; } = "";

    // Set only for per-core readings; hyperthread siblings share the index and differ in Label.
    [JsonPropertyName("ci")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CoreIndex { get; init; }

    [JsonPropertyName("v")]
    public double Value { get; init; }

    // HWiNFO's own running max since its process started (one process per run), so a flag that pulsed
    // between two ticks still reads 1 here. It also includes whatever HWiNFO saw on its very first sample,
    // which can be a state left over from before the run (a thermal-throttle flag latched by an earlier
    // load reads Max = 1 with Value = 0 on the first poll), so the report treats a sensor's first reading
    // as a baseline rather than a rise.
    [JsonPropertyName("mx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; init; }

    [JsonPropertyName("u")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    // The HWiNFO sensor a GPU reading came from (e.g. "dGPU [#1]: NVIDIA GeForce RTX 3070 Ti Laptop"). Set
    // only for readings whose label repeats per GPU (performance limiters, iGPU throttle reasons), so the
    // Events chart can tell which GPU tripped. Null for everything else, and for sessions recorded before
    // this field existed; the report falls back to a generic "GPU"/"iGPU" row for those.
    [JsonPropertyName("dv")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; init; }
}

// One line of samples.jsonl (Kind == HwInfoEvents): every mapped reading from a single HWiNFO poll.
public sealed class HwInfoEventSample
{
    [JsonPropertyName("t")]
    public DateTime Timestamp { get; init; } = DateTime.Now;

    [JsonPropertyName("r")]
    public List<HwInfoEventReading> Readings { get; init; } = new();
}

// One line of whea.jsonl: a WHEA-Logger entry from the System event log that landed during the run.
public sealed class WheaEventRecord
{
    public DateTime Timestamp { get; init; }
    public int EventId { get; init; }
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
}

// One display-class PCI device's link capability as Windows reports it through the PnP device properties
// (DEVPKEY_PciDevice_*), read once at the end of a run. Speeds are Windows' generation numbers (1 = 2.5 GT/s,
// 2 = 5, 3 = 8, 4 = 16, 5 = 32). Any property the device doesn't publish is null, which is the normal case for
// an integrated GPU: it has no PCIe link to describe.
public sealed class PcieDeviceLinkRecord
{
    public string Name { get; init; } = "";

    // PCI bus the device sits on; integrated graphics is on bus 0, so this gives a stable display order.
    public int? BusNumber { get; init; }

    public int? MaxLinkSpeed { get; init; }
    public int? MaxLinkWidth { get; init; }
    public int? CurrentLinkWidth { get; init; }
}
