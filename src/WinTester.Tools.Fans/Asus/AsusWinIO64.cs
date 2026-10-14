using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinTester.Tools.Fans.Asus;

// Raw P/Invoke surface of AsusWinIO64.dll, the ASUS System Control Interface's own user-mode shim. Entry
// point names, parameter widths, and the fact that none of these return a status code are fixed by the
// vendor DLL's exports.
//
// Every one of these will throw DllNotFoundException on first call if AsusWinIO64.dll isn't resolvable,
// or EntryPointNotFoundException if an older vendor DLL lacks one of these exports. AsusLaptopFanBackend
// is responsible for catching both — this class stays a bare binding with no error handling of its own.
//
// The odd types are the vendor's, not a transcription slip: SetFanTestMode takes a `char` (the DLL wants
// a single byte 0x00/0x01), SetFanPwmDuty takes a `short` carrying a 0-255 duty, and the temperature
// read returns a ulong.
[SupportedOSPlatform("windows")]
internal static class AsusWinIO64
{
    private const string Dll = "AsusWinIO64.dll";

    // Must be called before any HealthyTable_* call, and paired with ShutdownWinIo.
    [DllImport(Dll)] internal static extern void InitializeWinIo();
    [DllImport(Dll)] internal static extern void ShutdownWinIo();

    [DllImport(Dll)] internal static extern int HealthyTable_FanCounts();

    // The DLL is stateful: SetFanIndex selects which fan the next FanRPM/SetFanPwmDuty call applies to.
    // Every read or write must set the index immediately before it — see the backend's lock.
    [DllImport(Dll)] internal static extern void HealthyTable_SetFanIndex(byte index);

    [DllImport(Dll)] internal static extern int HealthyTable_FanRPM();

    // 0x01 hands the selected fan to manual control; 0x00 returns it to the EC's automatic control.
    // This is the reset mechanism for this backend.
    [DllImport(Dll)] internal static extern void HealthyTable_SetFanTestMode(char mode);

    // Duty is 0-255, not a percentage.
    [DllImport(Dll)] internal static extern void HealthyTable_SetFanPwmDuty(short duty);

    [DllImport(Dll)] internal static extern ulong Thermal_Read_Cpu_Temperature();
}
