using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace WinTester.Tools.Fans.Gigabyte;

// GCC's own conditional RPM byte-swap (GccWmiTool.GetBytesInt16 in ucNotebook.dll). getRpm1..4 return a
// UInt16 that GAMING/AERO-line boards report as-is and AORUS-line boards report byte-swapped, gated on a
// static ComData.Manufacture flag GCC sets once at startup. This reimplements that gate from the same
// source field GCC itself reads — Win32_ComputerSystem.SystemFamily, exact-matched and uppercased —
// rather than Model, which GCC only consults afterward for sub-variant behaviour within an already-known
// family.
//
// Not gated on RPM-verified-model status the way GigabyteLaptopFanBackend's display is: this class
// always answers "what would GCC's own logic compute," independent of whether that answer is trusted
// enough to show. That trust decision belongs to the caller.
[SupportedOSPlatform("windows")]
internal static class GigabyteRpmByteSwap
{
    // The family strings GCC's NB_Controler.GetModelString exact-matches to set Manufacture = 3 (no
    // swap) — "GIGABYTE AERO" and "AERO" both count as the AERO line. AORUS is the only one of the known
    // SystemFamily values that leaves Manufacture at its static-init default of 0 (swap).
    private static readonly HashSet<string> NoSwapFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "AERO", "GIGABYTE AERO", "GIGABYTE GAMING"
    };

    // Read once and cached: SystemFamily cannot change while the machine is running, and
    // GigabyteFanWorker polls every 350ms, so a WMI round-trip per tick would be wasted work. Lazily
    // computed so a WMI failure at type-load time can't take the whole backend down.
    private static bool? _requiresSwap;

    public static bool RequiresSwap() => _requiresSwap ??= ComputeRequiresSwap();

    private static bool ComputeRequiresSwap()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT SystemFamily FROM Win32_ComputerSystem");

            foreach (System.Management.ManagementBaseObject result in searcher.Get())
            {
                if (result["SystemFamily"] is string family && !string.IsNullOrWhiteSpace(family))
                {
                    // Anything outside the known no-swap set defaults to "swap" — the same direction
                    // GCC's own Manufacture flag defaults to — so an unrecognized SystemFamily string
                    // behaves the same way GCC itself would treat it.
                    return !NoSwapFamilies.Contains(family.Trim());
                }
            }
        }
        catch
        {
            // WMI failure: same fail-safe direction as GigabyteLaptopFanBackend.IsConfirmedBoard —
            // assume the family that needs the swap. An unswapped garbage value (tens of thousands of
            // RPM) is a much louder failure than defaulting one more gate closed.
        }

        return true;
    }

    // GCC's GetBytesInt16, reproduced exactly:
    //   bytes = BitConverter.GetBytes(rawUInt16)     // [lowByte, highByte] on this little-endian platform
    //   Manufacture != 0 -> bytes[1]*256 + bytes[0]   // reconstructs the value as-is (no swap)
    //   Manufacture == 0 -> bytes[0]*256 + bytes[1]   // byte-swapped
    // GigabyteWmiDriver.GetMethod already widens WMI's 16-bit "Data" out-parameter to a uint for every
    // caller, so this operates on the low 16 bits only — GB_WMIACPI never returns more than 16 bits of
    // RPM, so the upper 16 are always 0 in practice.
    public static uint Apply(uint raw)
    {
        if (!RequiresSwap()) return raw;

        var value = (ushort)raw;
        byte low = (byte)(value & 0xFF);
        byte high = (byte)(value >> 8);
        return (uint)((low << 8) | high);
    }
}
