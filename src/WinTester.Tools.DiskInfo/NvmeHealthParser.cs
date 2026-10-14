namespace WinTester.Tools.DiskInfo;

// The fields of the NVMe SMART / Health Information log (log page 02h) that the table uses. Counters wider than 64
// bits saturate at ulong.MaxValue, which no real drive reaches.
public sealed record NvmeHealth(
    byte CriticalWarning,
    int? TemperatureCelsius,
    byte AvailableSparePercent,
    byte AvailableSpareThreshold,
    byte PercentageUsed,
    ulong DataUnitsRead,
    ulong DataUnitsWritten,
    ulong PowerCycles,
    ulong PowerOnHours,
    ulong UnsafeShutdowns,
    ulong MediaErrors,
    ulong ErrorLogEntries,
    uint WarningTempMinutes,
    uint CriticalTempMinutes)
{
    // One data unit is 1000 blocks of 512 bytes.
    public const double BytesPerDataUnit = 512_000.0;

    public double TerabytesWritten => DataUnitsWritten * BytesPerDataUnit / 1e12;
    public double TerabytesRead => DataUnitsRead * BytesPerDataUnit / 1e12;

    public static readonly (byte Mask, string Text)[] WarningBits =
    {
        (0x01, "Available spare below threshold"),
        (0x02, "Temperature outside limits"),
        (0x04, "Reliability degraded"),
        (0x08, "Read only mode"),
        (0x10, "Volatile memory backup failed"),
        (0x20, "Persistent memory region read only"),
    };

    public string CriticalWarningText
    {
        get
        {
            if (CriticalWarning == 0) return "None";
            var parts = WarningBits.Where(b => (CriticalWarning & b.Mask) != 0).Select(b => b.Text).ToList();
            var unknown = CriticalWarning & ~WarningBits.Aggregate(0, (acc, b) => acc | b.Mask);
            if (unknown != 0) parts.Add($"Other flags (0x{unknown:X2})");
            return string.Join(", ", parts);
        }
    }
}

// Parses the 512-byte SMART / Health Information log page. Pure: no I/O.
//
// Layout (NVMe base specification): byte 0 critical warning; bytes 1-2 composite temperature in kelvin; byte 3
// available spare; byte 4 spare threshold; byte 5 percentage used; then 128-bit counters from byte 32: data units
// read, data units written, host read commands, host write commands, controller busy time, power cycles, power on
// hours, unsafe shutdowns, media and data integrity errors, error log entries; 32-bit warning and critical
// composite temperature times at bytes 192 and 196.
public static class NvmeHealthParser
{
    public const int LogLength = 512;

    public static NvmeHealth? Parse(byte[]? log)
    {
        if (log is null || log.Length < LogLength) return null;

        var kelvin = log[1] | (log[2] << 8);
        int? celsius = kelvin == 0 ? null : kelvin - 273;
        if (celsius is < -40 or > 200) celsius = null;

        return new NvmeHealth(
            CriticalWarning: log[0],
            TemperatureCelsius: celsius,
            AvailableSparePercent: log[3],
            AvailableSpareThreshold: log[4],
            PercentageUsed: log[5],
            DataUnitsRead: Counter128(log, 32),
            DataUnitsWritten: Counter128(log, 48),
            PowerCycles: Counter128(log, 112),
            PowerOnHours: Counter128(log, 128),
            UnsafeShutdowns: Counter128(log, 144),
            MediaErrors: Counter128(log, 160),
            ErrorLogEntries: Counter128(log, 176),
            WarningTempMinutes: BitConverter.ToUInt32(log, 192),
            CriticalTempMinutes: BitConverter.ToUInt32(log, 196));
    }

    private static ulong Counter128(byte[] log, int offset)
    {
        for (var i = 8; i < 16; i++)
            if (log[offset + i] != 0) return ulong.MaxValue;
        return BitConverter.ToUInt64(log, offset);
    }
}
