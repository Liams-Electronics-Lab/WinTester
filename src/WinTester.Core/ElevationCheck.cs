using System.Security.Principal;

namespace WinTester.Core;

// Shared "is this process running as Administrator" check. Previously duplicated privately inside
// WinTester.Sensors' LibreHardwareSensorService (which still uses it, via BuildSensorWarning); pulled up
// into Core so every test module's raw log can report it too (see Phase 0.3 in the field-issues tracker)
// without those modules taking a project reference on WinTester.Sensors just for this one check.
public static class ElevationCheck
{
    // Never throws: a failed check degrades to "assume elevated" so a broken check can't spuriously flag
    // an otherwise-fine run.
    public static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return true;
        }
    }
}
