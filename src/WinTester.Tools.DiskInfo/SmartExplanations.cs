using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinTester.Tools.DiskInfo;

// One "?" entry: the text shown in the explanation popup for a row of the Disk Info table.
public sealed record SmartExplanation(string Key, string Title, string Body);

// The explanation texts behind every "?" in the Disk Info window, one per row key (SmartRowKeys), written in the
// plain style of a product manual. Wording follows the published meaning of each S.M.A.R.T. attribute (ATA attribute
// tables from drive makers and smartmontools) and the NVMe SMART / Health Information log fields. Thresholds quoted in
// the texts match SmartEvaluator; change both together.
//
// Also builds the web search that the popup's Search button opens: disk type + data point name + value.
public static class SmartExplanations
{
    private static readonly SmartExplanation[] Entries =
    {
        new(SmartRowKeys.Health, "Health Status",
            "Overall condition of the drive, taken from the S.M.A.R.T. data the drive reports about itself.\n\n" +
            "Good: no warning sign was found.\n\n" +
            "Caution: at least one counter shows early wear or trouble, for example remapped or unstable sectors, an SSD that has used most of its rated life, or a high temperature.\n\n" +
            "Bad: the drive reports that a failure is predicted, or a value has reached its manufacturer limit.\n\n" +
            "Unknown: no S.M.A.R.T. data could be read."),

        new(SmartRowKeys.SmartStatus, "S.M.A.R.T. Status",
            "The drive's own overall self-assessment. OK means the drive does not predict a failure. Failure predicted means a critical counter has reached its manufacturer limit.\n\n" +
            "A drive that reports OK can still be developing faults, so read the other values as well."),

        new(SmartRowKeys.Temperature, "Temperature",
            "Current internal temperature reported by the drive, in degrees Celsius.\n\n" +
            "Hard drives are normally specified for operation up to about 60 C and SSDs up to about 70 C. Sustained heat shortens the life of both."),

        new(SmartRowKeys.PowerOnHours, "Power On Hours",
            "Total time the drive has been powered on during its life, in hours. A high figure means more accumulated wear on average, but many drives run well past 40,000 hours."),

        new(SmartRowKeys.PowerCycles, "Power Cycle Count",
            "Number of times the drive has been powered on and off. Each cycle stresses the drive, most of all the motor of a hard disk. A very high count on a young drive points to unstable power or a loose cable."),

        new(SmartRowKeys.FreeSpace, "Free Space",
            "Unused space on the volumes on this disk, shown against their total size. Flagged below 10 percent free.\n\n" +
            "An SSD performs best, and wears more evenly, with 10 to 20 percent of its capacity free."),

        new(SmartRowKeys.Trim, "TRIM",
            "TRIM lets Windows tell an SSD which blocks no longer hold data, so the drive can erase them in the background. This is a Windows setting for the whole computer, not a property of one disk, and it applies to SSD and NVMe drives only. A Disabled value is flagged.\n\n" +
            "To turn it on, open an elevated command prompt and run: fsutil behavior set DisableDeleteNotify NTFS 0"),

        new(SmartRowKeys.ReallocatedSectors, "Reallocated Sectors Count",
            "Number of sectors the drive found unreliable and replaced with spare sectors. On an SSD this counts retired flash blocks.\n\n" +
            "A new drive normally shows 0. A small count that does not change can be tolerated on an old drive. A count that rises means the surface or flash is failing. Any value above 0 is flagged."),

        new(SmartRowKeys.ReallocationEvents, "Reallocation Event Count",
            "Number of attempts to remap a bad sector, counting both successful and failed attempts. It normally follows the Reallocated Sectors Count. Flagged above 0."),

        new(SmartRowKeys.PendingSectors, "Current Pending Sector Count",
            "Number of unstable sectors waiting to be remapped. A sector leaves the list when it is read or rewritten successfully; if the rewrite fails it is reallocated and the Reallocated Sectors Count rises.\n\n" +
            "Data in these sectors may already be unreadable. Flagged above 0. A surface scan will show whether the count grows."),

        new(SmartRowKeys.OfflineUncorrectable, "Offline Uncorrectable Sector Count",
            "Number of sectors that could not be corrected during the drive's background (offline) scan. The drive has already tried to recover them and failed, so this is a stronger warning than a pending sector. Flagged above 0."),

        new(SmartRowKeys.ReportedUncorrectable, "Reported Uncorrectable Errors",
            "Number of read errors that the drive's error correction could not repair and that were reported to the computer. The normal value is 0. Flagged above 0."),

        new(SmartRowKeys.CommandTimeout, "Command Timeout",
            "Number of commands the drive abandoned because they took too long to finish. A count that keeps rising usually points to a bad SATA or power cable, a weak power supply, a faulty controller port, or a drive that has stopped responding properly.\n\n" +
            "Some makers store several counters in this value. For Seagate drives only the total timeout count is shown. Flagged above 0."),

        new(SmartRowKeys.UdmaCrc, "UDMA CRC Error Count",
            "Number of data transfer errors detected on the cable between the drive and the controller. The transfer is repeated, so data is normally not lost. The value never goes down.\n\n" +
            "A count that keeps increasing points to a damaged or badly seated SATA cable, a dirty connector, or a faulty port. Flagged above 0."),

        new(SmartRowKeys.SpinRetry, "Spin Retry Count",
            "Number of times the drive needed more than one attempt to spin up to full speed. The normal value is 0. A value above 0 points to a motor or bearing fault, or a power supply that cannot deliver enough current at start up. Flagged above 0."),

        new(SmartRowKeys.GSense, "G-Sense Error Rate",
            "Number of shock or vibration events detected above the drive's limit by its built-in sensor, which lets the heads park before an impact. A small count, often from shipping, is normal. A count that keeps growing points to knocks, drops or strong vibration.\n\n" +
            "Flagged only when the drive's own score for the attribute falls to its limit."),

        new(SmartRowKeys.LoadCycles, "Load/Unload Cycle Count",
            "Number of times the heads moved on and off the disk surface to park. Laptop drives park often, so the count can climb quickly. Makers commonly rate the heads for several hundred thousand cycles; check the data sheet of the model. Not flagged."),

        new(SmartRowKeys.EndToEnd, "End-to-End Error Count",
            "Number of times the parity check failed on data moving between the drive's cache memory and its interface, meaning the data was corrupted inside the drive's own electronics. The normal value is 0. Flagged above 0."),

        new(SmartRowKeys.ReadErrorRate, "Read Error Rate",
            "Rate of errors met when reading from the disk surface. The drive scores it against its own limit: high when healthy, falling towards the limit as errors increase.\n\n" +
            "The raw count is manufacturer specific and cannot be compared between models, so the score, the lowest score recorded (worst) and the limit are shown. Flagged only when the score has fallen to the limit."),

        new(SmartRowKeys.SeekErrorRate, "Seek Error Rate",
            "Rate of errors met when positioning the heads on a track. As with Read Error Rate, the raw count is manufacturer specific. A score that falls steadily can indicate wear in the head positioning system, or vibration. Flagged only when the score has fallen to the limit."),

        new(SmartRowKeys.SsdLife, "SSD Life Remaining",
            "Estimated share of the SSD's rated write endurance that is left, from 100 percent when new down to 0. Makers report it in different attributes, but the meaning is the same.\n\n" +
            "Flagged at 20 percent or less, and treated as Bad at 5 percent or less."),

        new(SmartRowKeys.ProgramFail, "Program Fail Count",
            "Number of times the SSD failed to write (program) data to a flash block. The controller normally retires the block and carries on, so a very small count is not unusual. A rising count shows flash cells wearing out. Flagged above 0."),

        new(SmartRowKeys.EraseFail, "Erase Fail Count",
            "Number of times the SSD failed to erase a flash block. The controller normally retires the block and carries on, so a very small count is not unusual. A rising count shows flash cells wearing out. Flagged above 0."),

        new(SmartRowKeys.UnexpectedPowerLoss, "Unsafe Shutdown Count",
            "Number of times power was lost without the computer first telling the drive to shut down. Each event can leave cached data unwritten and, on drives without power loss protection, can damage recent writes or the drive's mapping tables.\n\n" +
            "A count that keeps rising points to an unstable power supply, a failing battery, or the computer being switched off at the power switch. Not flagged."),

        new(SmartRowKeys.CriticalWarning, "Critical Warning",
            "A set of flags the drive raises when it needs attention: spare capacity below its threshold, temperature outside its limits, reliability degraded, read only mode, or backup of its volatile memory failed. The normal value is None. Any flag is shown in red. A drive in read only mode accepts no new writes."),

        new(SmartRowKeys.AvailableSpare, "Available Spare",
            "Percentage of reserve flash capacity still available to replace worn blocks. A new drive shows 100 percent. The drive also reports a threshold, shown beside the value; below it the drive raises a critical warning. Flagged at or below the threshold."),

        new(SmartRowKeys.PercentageUsed, "Percentage Used",
            "The manufacturer's estimate of how much of the drive's rated endurance has been used. A new drive shows 0 percent. The value can pass 100, because many drives keep working beyond their rating.\n\n" +
            "Flagged from 80 percent. Compare Data Written with the rated TBW in the data sheet."),

        new(SmartRowKeys.MediaErrors, "Media and Data Integrity Errors",
            "Number of times the controller detected an unrecovered data integrity error, for example a read that the error correction could not repair. The normal value is 0. Flagged above 0."),

        new(SmartRowKeys.ErrorLogEntries, "Error Information Log Entries",
            "Number of entries in the drive's error log over its life. Many entries are harmless; a command rejected for an invalid field is logged as an error, for example. A value above 0 does not by itself show a failing drive. Not flagged."),

        new(SmartRowKeys.DataWritten, "Data Written",
            "Total data the computer has written to the drive over its life. The drive counts in units of 512,000 bytes; the value is shown in decimal terabytes (TB). Compare it with the rated endurance (TBW) in the data sheet of the model. Not flagged."),

        new(SmartRowKeys.DataRead, "Data Read",
            "Total data the computer has read from the drive over its life, shown in decimal terabytes (TB). Not flagged."),

        new(SmartRowKeys.WarningTempTime, "Time Above Warning Temperature",
            "Total minutes the drive has spent above its warning temperature and below its critical temperature. A value above 0 means the drive has run hotter than the maker advises. Check the heatsink on an M.2 drive and the airflow. Flagged above 0."),

        new(SmartRowKeys.CriticalTempTime, "Time Above Critical Temperature",
            "Total minutes the drive has spent at or above its critical temperature. At this temperature a drive may slow down sharply or shut down to protect the data. Flagged above 0."),

        new(SmartRowKeys.ReadErrorsUncorrected, "Uncorrected Read Errors",
            "Number of read errors Windows recorded as not corrected by the drive. This value comes from the Windows storage reliability counters, used when the drive's own S.M.A.R.T. table cannot be read. The normal value is 0. Flagged above 0."),

        new(SmartRowKeys.WriteErrorsUncorrected, "Uncorrected Write Errors",
            "Number of write errors Windows recorded as not corrected by the drive. This value comes from the Windows storage reliability counters, used when the drive's own S.M.A.R.T. table cannot be read. The normal value is 0. Flagged above 0."),

        new(SmartRowKeys.VendorAttribute, "Manufacturer Specific Attribute",
            "An attribute defined by the drive maker whose score has fallen to the limit the maker set. Meanings differ between makes and models, so only the attribute number, the score and the limit are shown. Use Search to look up the attribute number for this make of drive."),
    };

    private static readonly Dictionary<string, SmartExplanation> ByKey =
        Entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

    public static IReadOnlyCollection<SmartExplanation> All => Entries;

    // Null for an unknown key; the window then shows a generic line instead of failing.
    public static SmartExplanation? Find(string key) => ByKey.TryGetValue(key, out var e) ? e : null;

    // The text sent to the search engine: disk type, data point name and value, separated by single spaces
    // (for example "NVMe SSD Percentage Used 3%"). Blank parts are skipped.
    public static string BuildSearchQuery(string diskType, string dataPointName, string value)
    {
        var sb = new StringBuilder();
        foreach (var part in new[] { diskType, dataPointName, value })
        {
            var clean = string.Join(' ', (part ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (clean.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(clean);
        }
        return sb.ToString();
    }

    // Same engine the Hardware Errors lookup uses.
    public static string BuildSearchUrl(string query) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
}

// Stable identifiers for every row the table can show. The evaluator builds rows with these, and each one has an
// explanation above; a check in scratch/DiskInfo.Check fails if one is missing.
public static class SmartRowKeys
{
    public const string Health = "health";
    public const string SmartStatus = "smartStatus";
    public const string Temperature = "temperature";
    public const string PowerOnHours = "powerOnHours";
    public const string PowerCycles = "powerCycles";
    public const string FreeSpace = "freeSpace";
    public const string Trim = "trim";
    public const string ReallocatedSectors = "reallocatedSectors";
    public const string ReallocationEvents = "reallocationEvents";
    public const string PendingSectors = "pendingSectors";
    public const string OfflineUncorrectable = "offlineUncorrectable";
    public const string ReportedUncorrectable = "reportedUncorrectable";
    public const string CommandTimeout = "commandTimeout";
    public const string UdmaCrc = "udmaCrc";
    public const string SpinRetry = "spinRetry";
    public const string GSense = "gSense";
    public const string LoadCycles = "loadCycles";
    public const string EndToEnd = "endToEnd";
    public const string ReadErrorRate = "readErrorRate";
    public const string SeekErrorRate = "seekErrorRate";
    public const string SsdLife = "ssdLife";
    public const string ProgramFail = "programFail";
    public const string EraseFail = "eraseFail";
    public const string UnexpectedPowerLoss = "unexpectedPowerLoss";
    public const string CriticalWarning = "criticalWarning";
    public const string AvailableSpare = "availableSpare";
    public const string PercentageUsed = "percentageUsed";
    public const string MediaErrors = "mediaErrors";
    public const string ErrorLogEntries = "errorLogEntries";
    public const string DataWritten = "dataWritten";
    public const string DataRead = "dataRead";
    public const string WarningTempTime = "warningTempTime";
    public const string CriticalTempTime = "criticalTempTime";
    public const string ReadErrorsUncorrected = "readErrorsUncorrected";
    public const string WriteErrorsUncorrected = "writeErrorsUncorrected";
    public const string VendorAttribute = "vendorAttribute";
}
