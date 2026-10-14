using System.Globalization;

namespace WinTester.Tools.RestorePoints;

// Interval and time to live, stored by Windows in seconds. Also holds the defaults,
// validation and unit conversions shared by the service and the window.
public sealed record RestoreSettings(long IntervalSeconds, long LifetimeSeconds)
{
    public const long SecondsPerDay = 86_400;
    public const long DefaultIntervalSeconds = SecondsPerDay;
    public const long DefaultLifetimeSeconds = 90 * SecondsPerDay;
    public const double DefaultMaxPercent = 5.0;

    // vssadmin Resize ShadowStorage rejects anything below this (tracker O4).
    public const long MinMaxBytes = 320L * 1024 * 1024;

    private const double BytesPerGb = 1024.0 * 1024 * 1024;
    private const double BytesPerMb = 1024.0 * 1024;

    public static RestoreSettings Defaults => new(DefaultIntervalSeconds, DefaultLifetimeSeconds);

    public int LifetimeDays => (int)(LifetimeSeconds / SecondsPerDay);

    public static long ToSeconds(int days, int hours, int minutes, int seconds) =>
        days * SecondsPerDay + hours * 3_600L + minutes * 60L + seconds;

    public static (int Days, int Hours, int Minutes, int Seconds) Split(long totalSeconds)
    {
        var total = Math.Max(0, totalSeconds);
        var days = total / SecondsPerDay;
        total %= SecondsPerDay;
        var hours = total / 3_600;
        total %= 3_600;
        return ((int)days, (int)hours, (int)(total / 60), (int)(total % 60));
    }

    public static string? ValidateInterval(long seconds) =>
        seconds > 0 ? null : "Interval must be greater than zero.";

    public static string? ValidateLifetimeDays(int days) =>
        days >= 1 ? null : "Time to live must be at least 1 day.";

    public static string? ValidateMaxBytes(long bytes, long driveCapacityBytes)
    {
        if (bytes < MinMaxBytes) return "Maximum space must be at least 320 MB.";
        if (driveCapacityBytes > 0 && bytes > driveCapacityBytes) return "Maximum space cannot exceed the drive size.";
        return null;
    }

    // Default maximum: 5% of the drive.
    public static long DefaultMaxBytes(long driveCapacityBytes) =>
        (long)(driveCapacityBytes * (DefaultMaxPercent / 100.0));

    public static double BytesToGb(long bytes) => bytes / BytesPerGb;

    public static long GbToBytes(double gb) => (long)Math.Round(gb * BytesPerGb);

    // Size argument for vssadmin, in whole megabytes.
    public static long ToResizeMegabytes(long bytes) => (long)Math.Round(bytes / BytesPerMb);

    public static double PercentOfDrive(long bytes, long driveCapacityBytes) =>
        driveCapacityBytes > 0 ? bytes * 100.0 / driveCapacityBytes : 0;

    public static string FormatGb(long bytes) =>
        BytesToGb(bytes).ToString("0.0", CultureInfo.CurrentCulture);

    // Accepts the current culture's decimal separator, then falls back to invariant.
    public static bool TryParseGb(string? text, out double gb)
    {
        gb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        return (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out gb)
                || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out gb))
               && gb >= 0 && !double.IsInfinity(gb);
    }
}
