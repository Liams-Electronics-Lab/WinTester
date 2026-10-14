using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace WinTester.Tools.DiskInfo;

public sealed record AtaReadResult(AtaSmartData? Data, string? ErrorMessage);

// Reads the S.M.A.R.T. attribute table of a SATA or ATA disk through the root\wmi classes
// MSStorageDriver_ATAPISmartData, MSStorageDriver_FailurePredictThresholds and MSStorageDriver_FailurePredictStatus.
// Needs administrator rights. A disk that does not expose ATA S.M.A.R.T. (NVMe, most USB bridges) simply has no instance.
[SupportedOSPlatform("windows")]
public static class AtaSmartReader
{
    private static readonly Regex Suffix = new(@"^_\d+$", RegexOptions.Compiled);

    // The WMI InstanceName is the disk's PNP device id followed by "_" and an index, for example
    // "SCSI\Disk&Ven_...\4&1b2c3d&0&000000_0". Compared without regard to case.
    public static bool InstanceMatches(string instanceName, string pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(instanceName) || string.IsNullOrWhiteSpace(pnpDeviceId)) return false;
        if (!instanceName.StartsWith(pnpDeviceId, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = instanceName[pnpDeviceId.Length..];
        return rest.Length == 0 || Suffix.IsMatch(rest);
    }

    public static AtaReadResult Read(PhysicalDiskItem disk)
    {
        if (string.IsNullOrWhiteSpace(disk.PnpDeviceId))
            return new AtaReadResult(null, "The disk has no device id to match S.M.A.R.T. data to.");

        try
        {
            byte[]? data = null, thresholds = null;
            bool? predicted = null;

            WmiQuery.Each(WmiQuery.WmiScope, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData", o =>
            {
                if (data is null && InstanceMatches(WmiQuery.Str(o, "InstanceName"), disk.PnpDeviceId))
                    data = WmiQuery.Bytes(o, "VendorSpecific");
            });

            if (data is null) return new AtaReadResult(null, null);

            WmiQuery.Each(WmiQuery.WmiScope, "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds", o =>
            {
                if (thresholds is null && InstanceMatches(WmiQuery.Str(o, "InstanceName"), disk.PnpDeviceId))
                    thresholds = WmiQuery.Bytes(o, "VendorSpecific");
            });

            WmiQuery.Each(WmiQuery.WmiScope, "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", o =>
            {
                if (predicted is null && InstanceMatches(WmiQuery.Str(o, "InstanceName"), disk.PnpDeviceId))
                    predicted = WmiQuery.Bool(o, "PredictFailure");
            });

            var parsed = AtaSmartParser.Parse(data, thresholds, predicted);
            return parsed.Attributes.Count == 0 ? new AtaReadResult(null, null) : new AtaReadResult(parsed, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new AtaReadResult(null, "Access to the S.M.A.R.T. data was denied. Run WinTester as administrator.");
        }
        catch (Exception ex)
        {
            return new AtaReadResult(null, $"S.M.A.R.T. data could not be read: {ex.Message}");
        }
    }
}
