using System.Management;
using System.Runtime.Versioning;

namespace WinTester.Tools.DiskInfo;

public sealed record DiskListResult(IReadOnlyList<PhysicalDiskItem> Disks, string? ErrorMessage);

// Lists the physical disks and decides which one holds Windows. Win32_DiskDrive supplies the identity of each disk;
// MSFT_PhysicalDisk (Windows 8 and later) supplies the media type, bus type and Windows' own health verdict, joined on
// the disk number.
public static class DiskEnumerator
{
    private const int BusTypeNvme = 17;
    private const int MediaTypeHdd = 3;
    private const int MediaTypeSsd = 4;

    private static readonly Dictionary<int, string> BusNames = new()
    {
        [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "IEEE 1394", [5] = "SSA", [6] = "Fibre Channel",
        [7] = "USB", [8] = "RAID", [9] = "iSCSI", [10] = "SAS", [11] = "SATA", [12] = "SD", [13] = "MMC",
        [14] = "Virtual", [15] = "File-backed virtual", [16] = "Storage Spaces", [17] = "NVMe",
    };

    public static string BusName(int? busType) =>
        busType is { } b && BusNames.TryGetValue(b, out var name) ? name : "Unknown";

    // NVMe by bus type first; then the media type Windows reports; then the model name as a last resort for
    // controllers that report neither. A disk that gives no sign stays Unknown rather than being guessed.
    public static DiskMedia Classify(int? busType, int? mediaType, string model)
    {
        if (busType == BusTypeNvme) return DiskMedia.Nvme;
        if (mediaType == MediaTypeSsd) return DiskMedia.Ssd;
        if (mediaType == MediaTypeHdd) return DiskMedia.Hdd;

        if (model.Contains("NVME", StringComparison.OrdinalIgnoreCase)) return DiskMedia.Nvme;
        if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("SOLID STATE", StringComparison.OrdinalIgnoreCase)) return DiskMedia.Ssd;
        return DiskMedia.Unknown;
    }

    // The disk that holds the given drive letter, or null when no partition has it.
    public static int? DiskOfLetter(IEnumerable<(int DiskNumber, char? Letter)> partitions, char letter)
    {
        foreach (var (disk, l) in partitions)
            if (l is { } c && char.ToUpperInvariant(c) == char.ToUpperInvariant(letter))
                return disk;
        return null;
    }

    public static DiskListResult List()
    {
        if (!OperatingSystem.IsWindows())
            return new DiskListResult(Array.Empty<PhysicalDiskItem>(), "Disk information is only available on Windows.");

        try
        {
            var storage = ReadStorageInfo();
            var windowsDisk = FindWindowsDisk();
            var disks = new List<PhysicalDiskItem>();

            WmiQuery.Each(WmiQuery.CimScope,
                "SELECT Index, Model, SerialNumber, FirmwareRevision, PNPDeviceID, Size FROM Win32_DiskDrive",
                d =>
                {
                    var index = WmiQuery.Int(d, "Index");
                    if (index is null) return;

                    var model = WmiQuery.Str(d, "Model");
                    storage.TryGetValue(index.Value, out var info);

                    disks.Add(new PhysicalDiskItem(
                        Index: index.Value,
                        Model: model,
                        SerialNumber: WmiQuery.Str(d, "SerialNumber"),
                        FirmwareRevision: WmiQuery.Str(d, "FirmwareRevision"),
                        PnpDeviceId: WmiQuery.Str(d, "PNPDeviceID"),
                        SizeBytes: WmiQuery.U64(d, "Size") ?? 0,
                        Media: Classify(info?.BusType, info?.MediaType, model),
                        BusTypeName: BusName(info?.BusType),
                        WindowsHealthStatus: info?.HealthStatus,
                        IsWindowsDisk: windowsDisk == index.Value));
                });

            disks.Sort((a, b) => a.Index.CompareTo(b.Index));
            return new DiskListResult(disks, disks.Count == 0 ? "Windows did not report any physical disks." : null);
        }
        catch (Exception ex)
        {
            return new DiskListResult(Array.Empty<PhysicalDiskItem>(), $"The disk list could not be read: {ex.Message}");
        }
    }

    private sealed record StorageInfo(int? BusType, int? MediaType, int? HealthStatus);

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, StorageInfo> ReadStorageInfo()
    {
        var map = new Dictionary<int, StorageInfo>();
        try
        {
            WmiQuery.Each(WmiQuery.StorageScope,
                "SELECT DeviceId, BusType, MediaType, HealthStatus FROM MSFT_PhysicalDisk",
                d =>
                {
                    if (int.TryParse(WmiQuery.Str(d, "DeviceId"), out var id))
                        map[id] = new StorageInfo(WmiQuery.Int(d, "BusType"), WmiQuery.Int(d, "MediaType"), WmiQuery.Int(d, "HealthStatus"));
                });
        }
        catch (Exception)
        {
            // Windows 7 or a broken storage provider: the model name and Win32_DiskDrive carry on alone.
        }
        return map;
    }

    [SupportedOSPlatform("windows")]
    private static int? FindWindowsDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return null;

            var partitions = new List<(int, char?)>();
            WmiQuery.Each(WmiQuery.StorageScope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition", p =>
            {
                var disk = WmiQuery.Int(p, "DiskNumber");
                if (disk is null) return;
                partitions.Add((disk.Value, PartitionLayoutBuilder.LetterOf(p["DriveLetter"])));
            });
            return DiskOfLetter(partitions, root[0]);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
