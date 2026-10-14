using System.Collections.Generic;

namespace WinTester.Core.Fans;

// Contract for a fan-control backend, same "Core defines the shape, one project owns the real
// implementation" split as ISensorService/LibreHardwareSensorService. The generic LibreHardwareMonitor
// backend (EC/SuperIO access) and the vendor-specific backends (Gigabyte WMI, ASUS WMI, Dell, etc.) are
// all implementations of this same interface, selected by a backend picker.
public interface IFanControlService
{
    // Fresh read on every call — RPM and duty both genuinely change tick to tick, deliberately not
    // cached. Order is not guaranteed stable across calls; callers needing stable row identity should
    // key off Id.
    IReadOnlyList<FanControlInfo> GetFans();

    // No-op when id is unknown, already reset, or IsControllable was false for that id in the most
    // recent GetFans(). percent is clamped to [0, 100] by the implementation.
    void SetDuty(string id, double percent);

    // Returns this one fan to automatic/factory control. No-op when id is unknown or never under manual
    // control.
    void Reset(string id);

    // Returns every fan this backend has ever handed out an Id for to automatic/factory control. Called
    // from the fan tool window's close path; must be safe to call unconditionally.
    void ResetAll();
}
