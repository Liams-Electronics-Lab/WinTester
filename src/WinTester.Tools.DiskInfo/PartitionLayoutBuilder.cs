namespace WinTester.Tools.DiskInfo;

public sealed record RawDisk(int Number, int PartitionStyle, bool IsOffline, bool IsReadOnly, ulong SizeBytes);

public sealed record RawPartition(
    int Number,
    ulong OffsetBytes,
    ulong SizeBytes,
    int? MbrType,
    string GptType,
    char? DriveLetter,
    bool IsActive,
    bool IsBoot,
    bool IsSystem,
    IReadOnlyList<string> AccessPaths);

public sealed record RawVolume(
    string Path,
    char? DriveLetter,
    string Label,
    string FileSystem,
    ulong SizeBytes,
    ulong FreeBytes,
    int? HealthStatus);

public sealed record LayoutResult(DiskLayout? Layout, ulong? FreeBytes, ulong? TotalBytes, string? ErrorMessage);

// Joins the raw disk, partition and volume records into the Disk Management style layout, adds the unallocated gaps and
// decides Basic or Dynamic. Pure: no I/O.
public static class PartitionLayoutBuilder
{
    // Gaps smaller than this are alignment padding and the GPT backup area, not space a technician can use.
    public const ulong MinimumGapBytes = 2UL * 1024 * 1024;

    private const int StyleMbr = 1;
    private const int StyleGpt = 2;

    private static readonly Dictionary<string, string> GptNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}"] = "EFI System Partition",
        ["{E3C9E316-0B5C-4DB8-817D-F92DF00215AE}"] = "Microsoft Reserved Partition",
        ["{DE94BBA4-06D1-4D40-A16A-BFD50179D6AC}"] = "Recovery Partition",
        ["{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}"] = "Basic Data Partition",
        ["{5808C8AA-7E8F-42E0-85D2-E1E90434CFB3}"] = "LDM Metadata Partition",
        ["{AF9B60A0-1431-4F62-BC68-3311714A69AD}"] = "LDM Data Partition",
        ["{E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D}"] = "Storage Spaces Partition",
    };

    private static readonly string[] DynamicGuids =
    {
        "{5808C8AA-7E8F-42E0-85D2-E1E90434CFB3}",
        "{AF9B60A0-1431-4F62-BC68-3311714A69AD}",
    };

    // WMI returns a drive letter as a char, a number or a string depending on the provider. Zero and blank mean none.
    public static char? LetterOf(object? value) => value switch
    {
        char c when c != '\0' && c != ' ' => c,
        ushort u when u != 0 => (char)u,
        int i when i != 0 => (char)i,
        string s when s.Trim().Length > 0 => s.Trim()[0],
        _ => null,
    };

    public static bool IsDynamicPartition(RawPartition p) =>
        p.MbrType == 0x42 || DynamicGuids.Contains(p.GptType.Trim(), StringComparer.OrdinalIgnoreCase);

    public static LayoutResult Build(RawDisk disk, IReadOnlyList<RawPartition> partitions, IReadOnlyList<RawVolume> volumes)
    {
        var sorted = partitions.OrderBy(p => p.OffsetBytes).ToList();
        var dynamic = sorted.Any(IsDynamicPartition);

        var items = new List<PartitionItem>();
        ulong free = 0, total = 0;
        var anyVolume = false;
        ulong cursor = 0;

        foreach (var p in sorted)
        {
            if (p.OffsetBytes > cursor && p.OffsetBytes - cursor > MinimumGapBytes)
                items.Add(Unallocated(cursor, p.OffsetBytes - cursor));

            var volume = FindVolume(p, volumes);
            if (volume is not null)
            {
                free += volume.FreeBytes;
                total += volume.SizeBytes;
                anyVolume = true;
            }

            items.Add(Describe(disk, p, volume));
            cursor = Math.Max(cursor, p.OffsetBytes + p.SizeBytes);
        }

        if (disk.SizeBytes > cursor && disk.SizeBytes - cursor > MinimumGapBytes)
            items.Add(Unallocated(cursor, disk.SizeBytes - cursor));

        var style = disk.PartitionStyle switch { StyleMbr => "MBR", StyleGpt => "GPT", _ => "Not initialized" };
        var uninitialized = disk.PartitionStyle is not (StyleMbr or StyleGpt);

        var layout = new DiskLayout(
            DiskNumber: disk.Number,
            TypeText: uninitialized ? "Unknown" : dynamic ? "Dynamic" : "Basic",
            StyleText: style,
            StatusText: disk.IsOffline ? "Offline" : uninitialized ? "Not Initialized" : "Online",
            SizeBytes: disk.SizeBytes,
            IsOffline: disk.IsOffline,
            IsReadOnly: disk.IsReadOnly,
            Partitions: items);

        return new LayoutResult(layout, anyVolume ? free : null, anyVolume ? total : null, null);
    }

    private static PartitionItem Unallocated(ulong offset, ulong size) =>
        new(offset, size, "Unallocated", SizeFormat.Format(size), "Unallocated", true);

    // A volume belongs to a partition by drive letter, or for a partition with no letter by the volume's path
    // appearing in the partition's access paths.
    private static RawVolume? FindVolume(RawPartition p, IReadOnlyList<RawVolume> volumes)
    {
        if (p.DriveLetter is { } letter)
        {
            var byLetter = volumes.FirstOrDefault(v => v.DriveLetter is { } l &&
                char.ToUpperInvariant(l) == char.ToUpperInvariant(letter));
            if (byLetter is not null) return byLetter;
        }

        foreach (var v in volumes)
        {
            if (string.IsNullOrEmpty(v.Path)) continue;
            if (p.AccessPaths.Any(a => string.Equals(a, v.Path, StringComparison.OrdinalIgnoreCase))) return v;
        }
        return null;
    }

    private static PartitionItem Describe(RawDisk disk, RawPartition p, RawVolume? volume)
    {
        var typeName = TypeName(disk, p);

        string title;
        if (p.DriveLetter is { } letter)
            title = string.IsNullOrWhiteSpace(volume?.Label) ? $"({char.ToUpperInvariant(letter)}:)" : $"{volume!.Label} ({char.ToUpperInvariant(letter)}:)";
        else if (!string.IsNullOrWhiteSpace(volume?.Label))
            title = volume!.Label;
        else
            title = typeName;

        var fsText = string.IsNullOrWhiteSpace(volume?.FileSystem)
            ? SizeFormat.Format(p.SizeBytes)
            : $"{SizeFormat.Format(p.SizeBytes)} {volume!.FileSystem}";

        var health = volume?.HealthStatus switch { 1 => "Warning", 2 => "Unhealthy", _ => "Healthy" };

        var flags = new List<string>();
        if (p.IsBoot) flags.Add("Boot");
        if (p.IsSystem) flags.Add("System");
        if (p.IsActive) flags.Add("Active");
        flags.Add(typeName);

        return new PartitionItem(p.OffsetBytes, p.SizeBytes, title, fsText, $"{health} ({string.Join(", ", flags)})", false);
    }

    private static string TypeName(RawDisk disk, RawPartition p)
    {
        if (disk.PartitionStyle == StyleGpt)
        {
            if (GptNames.TryGetValue(p.GptType.Trim(), out var name)) return name;
            return "Basic Data Partition";
        }

        return p.MbrType switch
        {
            0x42 => "LDM Partition",
            0x27 => "Recovery Partition",
            0xEE => "Protective Partition",
            0xEF => "EFI System Partition",
            _ => p.Number > 4 ? "Logical Drive" : "Primary Partition",
        };
    }
}
