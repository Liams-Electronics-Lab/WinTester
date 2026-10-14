using System.Globalization;

namespace WinTester.Tools.RestorePoints;

// One System Restore point as reported by WMI (root\default, class SystemRestore).
public sealed record RestorePointInfo(uint Sequence, DateTime Created, string Description, uint TypeCode)
{
    public string TypeName => RestorePointTypes.NameOf(TypeCode);

    // WMI CreationTime is a DMTF string: yyyymmddHHMMSS.mmmmmmsUUU (UUU is the UTC offset in minutes).
    // Returns local time, or DateTime.MinValue when the text cannot be read.
    public static DateTime ParseWmiDate(string? dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf) || dmtf.Length < 14)
            return DateTime.MinValue;

        if (!DateTime.TryParseExact(dmtf[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stamp))
            return DateTime.MinValue;

        if (dmtf.Length >= 25 && (dmtf[21] == '+' || dmtf[21] == '-')
            && int.TryParse(dmtf.AsSpan(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            var offset = TimeSpan.FromMinutes(dmtf[21] == '-' ? -minutes : minutes);
            return DateTime.SpecifyKind(stamp - offset, DateTimeKind.Utc).ToLocalTime();
        }

        return stamp;
    }
}
