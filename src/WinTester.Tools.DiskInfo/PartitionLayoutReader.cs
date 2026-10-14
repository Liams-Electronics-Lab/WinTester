using System.Management;
using System.Runtime.Versioning;

namespace WinTester.Tools.DiskInfo;

// Reads MSFT_Disk, MSFT_Partition and MSFT_Volume for one disk and hands them to PartitionLayoutBuilder.
// Needs Windows 8 or later; older systems return an error message and no layout.
[SupportedOSPlatform("windows")]
public static class PartitionLayoutReader
{
    public static LayoutResult Read(int diskNumber)
    {
        try
        {
            RawDisk? disk = null;
            WmiQuery.Each(WmiQuery.StorageScope,
                $"SELECT Number, PartitionStyle, IsOffline, IsReadOnly, Size FROM MSFT_Disk WHERE Number = {diskNumber}",
                d => disk = new RawDisk(
                    diskNumber,
                    WmiQuery.Int(d, "PartitionStyle") ?? 0,
                    WmiQuery.Bool(d, "IsOffline") ?? false,
                    WmiQuery.Bool(d, "IsReadOnly") ?? false,
                    WmiQuery.U64(d, "Size") ?? 0));

            if (disk is null)
                return new LayoutResult(null, null, null, "Windows did not report a partition layout for this disk.");

            var partitions = new List<RawPartition>();
            WmiQuery.Each(WmiQuery.StorageScope,
                $"SELECT PartitionNumber, Offset, Size, MbrType, GptType, DriveLetter, IsActive, IsBoot, IsSystem, AccessPaths " +
                $"FROM MSFT_Partition WHERE DiskNumber = {diskNumber}",
                p => partitions.Add(new RawPartition(
                    WmiQuery.Int(p, "PartitionNumber") ?? 0,
                    WmiQuery.U64(p, "Offset") ?? 0,
                    WmiQuery.U64(p, "Size") ?? 0,
                    WmiQuery.Int(p, "MbrType"),
                    WmiQuery.Str(p, "GptType"),
                    PartitionLayoutBuilder.LetterOf(p["DriveLetter"]),
                    WmiQuery.Bool(p, "IsActive") ?? false,
                    WmiQuery.Bool(p, "IsBoot") ?? false,
                    WmiQuery.Bool(p, "IsSystem") ?? false,
                    WmiQuery.StrArray(p, "AccessPaths"))));

            var volumes = new List<RawVolume>();
            WmiQuery.Each(WmiQuery.StorageScope,
                "SELECT Path, DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining, HealthStatus FROM MSFT_Volume",
                v => volumes.Add(new RawVolume(
                    WmiQuery.Str(v, "Path"),
                    PartitionLayoutBuilder.LetterOf(v["DriveLetter"]),
                    WmiQuery.Str(v, "FileSystemLabel"),
                    WmiQuery.Str(v, "FileSystem"),
                    WmiQuery.U64(v, "Size") ?? 0,
                    WmiQuery.U64(v, "SizeRemaining") ?? 0,
                    WmiQuery.Int(v, "HealthStatus"))));

            return PartitionLayoutBuilder.Build(disk, partitions, volumes);
        }
        catch (Exception ex)
        {
            return new LayoutResult(null, null, null, $"The partition layout could not be read: {ex.Message}");
        }
    }
}
