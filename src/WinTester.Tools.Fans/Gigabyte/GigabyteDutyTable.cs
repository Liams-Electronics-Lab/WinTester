using System;

namespace WinTester.Tools.Fans.Gigabyte;

// Gigabyte's 16-step EC duty register table, confirmed against ComData.dll's own compiled static
// constructor (ComData.DutyValue). The EC's SetFixedFanSpeed register does not take a percentage — it
// takes one of these raw values, so a commanded percent has to be quantized onto the table before it's
// written.
//
// Also used for SetGPUFanDuty (GigabyteFanStateMachine), not just SetFixedFanSpeed (CPU):
// ComData.DutyValue/DutyPercent is keyed generically across Fan1Duty..Fan4Duty in the vendor's own field
// naming, not a CPU-specific table.
//
// Kept even though the confirmed board's hardware notes say the speed registers are inert there (see
// GigabyteFanStateMachine's class comment): the registers are still written so the backend degrades
// usefully on other Gigabyte/AORUS models whose EC may honour them, and a wrong value here would be a
// silent miscalibration rather than a visible failure.
internal static class GigabyteDutyTable
{
    internal static readonly byte[] Table =
    {
        57, 68, 80, 91, 103, 114, 125, 137, 148, 160, 171, 183, 194, 206, 217, 229
    };

    // ComData.dll pairs this exact byte table with its own percent labels — "25".."100" in 5% steps, one
    // per Table entry, same order. The table has no entry below 25%: Table[0] (57) is GCC's own floor,
    // not a stand-in for 0%. PercentFloor/PercentStep encode that mapping so a caller can go from a
    // commanded percentage to the correct table index without re-deriving it.
    private const double PercentFloor = 25.0;
    private const double PercentStep = 5.0;

    internal static byte ToRegisterSpeed(double percentage)
    {
        if (percentage <= PercentFloor)
            return Table[0];

        int index = (int)Math.Round((percentage - PercentFloor) / PercentStep);
        index = Math.Clamp(index, 0, Table.Length - 1);
        return Table[index];
    }
}

// The discrete modes Gigabyte's EC actually implements. Auto is firmware control (the state this backend
// must always return to); the rest are session-only overrides.
internal enum GigabyteFanMode
{
    Auto = 0,
    WhisperEco = 1,
    StepCurve = 2,
    FixedBoost = 3,
    MaxTurbo = 4
}
