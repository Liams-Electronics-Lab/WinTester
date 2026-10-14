using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.Versioning;
using WinTester.Core.Fans;

namespace WinTester.Tools.Fans;

// ASUS desktop motherboards via the ASUSHW WMI class (root\wmi). This backend is deliberately
// read-only: GetFans() always reports IsControllable = false, and SetDuty/Reset/ResetAll are no-ops,
// since no known ASUSHW write/set method exists for fan duty. Every public method swallows its own
// failures rather than throwing, since FanBackendSelector and FanControlWindow both expect that from an
// IFanControlService implementation.
public sealed class AsusDesktopFanBackend : IFanControlService, IFanBackendProbe
{
    // One fan sensor as ASUSHW itself describes it. Source groups sensors that share a
    // sensor_update_buffer refresh; DataType == 3 means the raw value is fixed-point and needs dividing
    // by 1,000,000.
    private sealed record AsusFanSensor(uint Index, uint Source, uint DataType, string Name);

    private ManagementObject? _asusHw;
    private readonly List<AsusFanSensor> _fanSensors = new();

    // Establishes the ASUSHW WMI connection and enumerates its fan sensors once. Called by
    // FanBackendSelector before this backend is trusted. GetFans() does the live value read on every
    // call, matching every IFanControlService's "fresh, not cached" contract, but the sensor list only
    // needs discovering once per window session.
    public bool TryActivate()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return TryActivateOnWindows();
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private bool TryActivateOnWindows()
    {
        _asusHw = GetSingleInstance(new ManagementScope(@"root\wmi"), "ASUSHW");
        if (_asusHw is null) return false;

        return LoadFanSensors();
    }

    [SupportedOSPlatform("windows")]
    private bool LoadFanSensors()
    {
        if (_asusHw is null) return false;

        ManagementBaseObject countResult = _asusHw.InvokeMethod("sensor_get_number", null, null);
        var sensorCount = (uint)countResult["Data"];

        _fanSensors.Clear();

        for (uint i = 0; i < sensorCount; i++)
        {
            var parameters = _asusHw.GetMethodParameters("sensor_get_info");
            parameters["Index"] = i;
            ManagementBaseObject infoResult = _asusHw.InvokeMethod("sensor_get_info", parameters, null);

            // Type 2 == fan (1 == temperature). Only fans are this backend's concern; ASUSHW's
            // temperature sensors belong to the sensor tool, not this one.
            var sensorType = (uint)infoResult["Type"];
            if (sensorType != 2) continue;

            _fanSensors.Add(new AsusFanSensor(
                Index: i,
                Source: (uint)infoResult["Source"],
                DataType: (uint)infoResult["Data_Type"],
                Name: infoResult["Name"]?.ToString() ?? $"Fan {i}"));
        }

        return _fanSensors.Count > 0;
    }

    public IReadOnlyList<FanControlInfo> GetFans()
    {
        if (!OperatingSystem.IsWindows() || _asusHw is null || _fanSensors.Count == 0)
            return Array.Empty<FanControlInfo>();

        try
        {
            return ReadFansOnWindows();
        }
        catch
        {
            return Array.Empty<FanControlInfo>();
        }
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<FanControlInfo> ReadFansOnWindows()
    {
        var result = new List<FanControlInfo>(_fanSensors.Count);

        // Refresh each distinct Source's buffer once before reading any sensor that belongs to it,
        // rather than calling sensor_update_buffer once per sensor.
        var refreshedSources = new HashSet<uint>();

        foreach (var sensor in _fanSensors)
        {
            if (refreshedSources.Add(sensor.Source))
            {
                var sourceParam = _asusHw!.GetMethodParameters("sensor_update_buffer");
                sourceParam["Source"] = sensor.Source;
                _asusHw.InvokeMethod("sensor_update_buffer", sourceParam, null);
            }

            var valueParam = _asusHw!.GetMethodParameters("sensor_get_value");
            valueParam["Index"] = sensor.Index;
            ManagementBaseObject valueResult = _asusHw.InvokeMethod("sensor_get_value", valueParam, null);

            var raw = Convert.ToSingle((uint)valueResult["Data"]);
            var rpm = sensor.DataType == 3 ? raw / 1_000_000f : raw;

            result.Add(new FanControlInfo(
                Id: $"AsusHW{sensor.Index}",
                Name: sensor.Name,
                Rpm: rpm,
                DutyPercent: null,
                IsControllable: false));
        }

        return result;
    }

    // Read-only backend — every write path is a documented no-op, not a failure. A caller that ignores
    // IsControllable and calls these anyway gets exactly the "no-op when IsControllable was false"
    // behavior IFanControlService already promises.
    public void SetDuty(string id, double percent) { }
    public void Reset(string id) { }
    public void ResetAll() { }

    // Uses the `ManagementClass(string path, string className, ObjectGetOptions options)` overload,
    // confirmed to work against ASUSHW.
    [SupportedOSPlatform("windows")]
    private static ManagementObject? GetSingleInstance(ManagementScope scope, string className)
    {
        var wmiClass = new ManagementClass(scope.Path.Path, className, null);
        foreach (ManagementObject instance in wmiClass.GetInstances())
            return instance;

        return null;
    }
}
