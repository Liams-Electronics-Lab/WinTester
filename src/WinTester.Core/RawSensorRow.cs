namespace WinTester.Core;

// One raw sensor reading from either backend, unfiltered — unlike SensorSnapshot's named fields, this
// exposes every sensor a backend reports, plus that backend's own running Min/Max (LibreHardwareMonitor:
// session-lifetime; HWiNFO: its own tracked values, read off ValueMin/ValueMax). Built for the standalone
// Sensors diagnostic tool; the report pipeline does not read this type.
public sealed record RawSensorRow(
    string Source,       // producing backend: "LibreHardwareMonitor" or "HWiNFO"
    string Category,     // LHM hardware type (e.g. "Cpu") or HWiNFO reading type (e.g. "Temperature")
    string HardwareName, // device/sensor-group name
    string SensorName,   // individual sensor/reading name
    double? Value,
    double? Min,
    double? Max,
    string Unit);
