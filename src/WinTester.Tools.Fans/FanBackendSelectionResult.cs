using WinTester.Core.Fans;

namespace WinTester.Tools.Fans;

// What FanBackendSelector decided, plus enough context for the Fan tool window to show something more
// useful than a silent "here are some fans" when a vendor backend wasn't used — e.g. a technician on a
// Gigabyte laptop wondering why every fan reads "Not controllable" should be able to see that a
// Gigabyte-specific backend isn't wired up yet, not just guess.
public sealed record FanBackendSelectionResult(
    FanVendor DetectedVendor,
    bool UsedVendorBackend,
    string BackendDescription,
    IFanControlService ActiveBackend);
