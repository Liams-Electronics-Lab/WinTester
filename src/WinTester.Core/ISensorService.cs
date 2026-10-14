using System;

namespace WinTester.Core;

// Wraps the hardware-sensor backend (LibreHardwareMonitorLib) so nothing above this layer talks to
// hardware sensors directly. Implementation lives in WinTester.Sensors.
public interface ISensorService : IDisposable
{
    // Opens the sensor backend. Safe to call more than once; degrades gracefully rather than throwing
    // when not running elevated.
    void Initialize();

    // Polls every available sensor and returns a structured snapshot.
    SensorSnapshot GetSnapshot();

    // Every raw sensor reported under the app's enabled hardware categories (Cpu/Gpu*/Memory/Storage/
    // Motherboard/Battery/Controller, including SubHardware like Super I/O), unfiltered, with the
    // backend's own running Min/Max. Used by the standalone Sensors diagnostic tool. HWiNFO rows are not
    // part of this contract — see WinTester.Sensors.HwInfo.HwInfoRawSensorRowSource.
    IReadOnlyList<RawSensorRow> GetAllSensorRows();
}
