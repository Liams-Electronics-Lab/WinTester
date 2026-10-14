namespace WinTester.Tools.DiskInfo;

// Which kind of disk an attribute row makes sense for. A row for an attribute the drive does not report is never
// shown, and a row that does not fit the disk type (a spin retry count on an SSD) is hidden as well.
public enum AttributeApplies
{
    Both,
    HddOnly,
    SsdOnly,
}

// How an attribute is judged.
//   Counter: the raw count is the value; any count above 0 is flagged.
//   Score:   the raw count is vendor specific; the drive's normalized score is shown and the row is flagged only
//            when the score has fallen to the drive's threshold.
//   Info:    shown, never flagged.
public enum AttributeStyle
{
    Counter,
    Score,
    Info,
}

public sealed record AtaAttributeDef(byte Id, string RowKey, AttributeApplies Applies, AttributeStyle Style);

// The ATA S.M.A.R.T. attributes that become rows of the table, in table order. Ids follow the common industry
// meaning of each attribute (drive maker tables and smartmontools). Where several ids carry the same quantity on
// different makes (program fail 171 and 181), the first one the drive reports is used and the rest are skipped.
// Temperature, power-on hours and the SSD life attributes need extra handling and live in SmartEvaluator.
public static class SmartAttributeCatalog
{
    public const byte PowerOnHoursId = 9;
    public const byte PowerCyclesId = 12;
    public const byte AirflowTemperatureId = 190;
    public const byte TemperatureId = 194;

    // Attributes that report remaining life as the normalized value, in the order they are preferred.
    public static readonly byte[] LifeRemainingIds = { 231, 233, 177, 202, 169 };

    public static readonly IReadOnlyList<AtaAttributeDef> Rows = new AtaAttributeDef[]
    {
        new(5, SmartRowKeys.ReallocatedSectors, AttributeApplies.Both, AttributeStyle.Counter),
        new(196, SmartRowKeys.ReallocationEvents, AttributeApplies.Both, AttributeStyle.Counter),
        new(197, SmartRowKeys.PendingSectors, AttributeApplies.Both, AttributeStyle.Counter),
        new(198, SmartRowKeys.OfflineUncorrectable, AttributeApplies.Both, AttributeStyle.Counter),
        new(187, SmartRowKeys.ReportedUncorrectable, AttributeApplies.Both, AttributeStyle.Counter),
        new(188, SmartRowKeys.CommandTimeout, AttributeApplies.Both, AttributeStyle.Counter),
        new(199, SmartRowKeys.UdmaCrc, AttributeApplies.Both, AttributeStyle.Counter),
        new(10, SmartRowKeys.SpinRetry, AttributeApplies.HddOnly, AttributeStyle.Counter),
        new(191, SmartRowKeys.GSense, AttributeApplies.HddOnly, AttributeStyle.Score),
        new(193, SmartRowKeys.LoadCycles, AttributeApplies.HddOnly, AttributeStyle.Info),
        new(184, SmartRowKeys.EndToEnd, AttributeApplies.Both, AttributeStyle.Counter),
        new(1, SmartRowKeys.ReadErrorRate, AttributeApplies.HddOnly, AttributeStyle.Score),
        new(7, SmartRowKeys.SeekErrorRate, AttributeApplies.HddOnly, AttributeStyle.Score),
        new(171, SmartRowKeys.ProgramFail, AttributeApplies.SsdOnly, AttributeStyle.Counter),
        new(181, SmartRowKeys.ProgramFail, AttributeApplies.SsdOnly, AttributeStyle.Counter),
        new(172, SmartRowKeys.EraseFail, AttributeApplies.SsdOnly, AttributeStyle.Counter),
        new(182, SmartRowKeys.EraseFail, AttributeApplies.SsdOnly, AttributeStyle.Counter),
        new(174, SmartRowKeys.UnexpectedPowerLoss, AttributeApplies.SsdOnly, AttributeStyle.Info),
        new(192, SmartRowKeys.UnexpectedPowerLoss, AttributeApplies.SsdOnly, AttributeStyle.Info),
    };

    private static readonly Dictionary<byte, string> KnownNames = new()
    {
        [1] = "Read Error Rate",
        [2] = "Throughput Performance",
        [3] = "Spin-Up Time",
        [4] = "Start/Stop Count",
        [5] = "Reallocated Sectors Count",
        [7] = "Seek Error Rate",
        [8] = "Seek Time Performance",
        [10] = "Spin Retry Count",
        [11] = "Calibration Retry Count",
        [169] = "Remaining Lifetime Percentage",
        [170] = "Available Reserved Space",
        [173] = "Wear Leveling Count",
        [177] = "Wear Leveling Count",
        [179] = "Used Reserved Block Count",
        [180] = "Unused Reserved Block Count",
        [183] = "SATA Downshift Error Count",
        [189] = "High Fly Writes",
        [195] = "Hardware ECC Recovered",
        [200] = "Multi-Zone Error Rate",
        [202] = "Percentage Lifetime Used",
        [231] = "SSD Life Left",
        [232] = "Available Reserved Space",
        [233] = "Media Wearout Indicator",
    };

    // Name shown for an attribute that has no row of its own but has reached its threshold.
    public static string DisplayNameFor(byte id) =>
        KnownNames.TryGetValue(id, out var name) ? $"{name} (attribute {id})" : $"Attribute {id} (0x{id:X2})";

    public static bool Applicable(AttributeApplies applies, DiskMedia media) => applies switch
    {
        AttributeApplies.HddOnly => media != DiskMedia.Ssd && media != DiskMedia.Nvme,
        AttributeApplies.SsdOnly => media != DiskMedia.Hdd,
        _ => true,
    };
}
