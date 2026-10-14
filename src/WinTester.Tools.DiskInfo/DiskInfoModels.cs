namespace WinTester.Tools.DiskInfo;

public enum DiskMedia
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
}

public enum HealthLevel
{
    Unknown,
    Good,
    Caution,
    Bad,
}

// Where the numbers in a report came from, best source first.
public enum SmartSource
{
    None,
    Ata,
    Nvme,
    WindowsCounters,
}

// One physical disk in the drop-down. Index is the Windows disk number (Win32_DiskDrive.Index, the N in
// \\.\PhysicalDriveN and the "Disk N" of Disk Management).
public sealed record PhysicalDiskItem(
    int Index,
    string Model,
    string SerialNumber,
    string FirmwareRevision,
    string PnpDeviceId,
    ulong SizeBytes,
    DiskMedia Media,
    string BusTypeName,
    int? WindowsHealthStatus,
    bool IsWindowsDisk)
{
    // "Disk 0: Samsung SSD 970 EVO 500GB (465.76 GB) - Windows"
    public string DisplayName =>
        $"Disk {Index}: {(string.IsNullOrWhiteSpace(Model) ? "Unknown disk" : Model.Trim())} ({SizeFormat.Format(SizeBytes)})" +
        (IsWindowsDisk ? " - Windows" : "");

    public string MediaText => Media switch
    {
        DiskMedia.Nvme => "NVMe SSD",
        DiskMedia.Ssd => "SSD",
        DiskMedia.Hdd => "HDD",
        _ => "Disk",
    };
}

// One line of the data table. Flagged rows are drawn in bold red. Detail is optional small text shown after the
// value (for example the threshold next to Available Spare).
public sealed record SmartRow(string Key, string Name, string Value, bool Flagged, string? Detail = null);

public sealed record HealthSummary(HealthLevel Level, string StatusText, int? LifePercent, IReadOnlyList<string> Reasons);

// One block of the Disk Management style layout: a partition, or unallocated space.
public sealed record PartitionItem(
    ulong OffsetBytes,
    ulong SizeBytes,
    string Title,
    string FileSystemText,
    string StatusText,
    bool IsUnallocated);

public sealed record DiskLayout(
    int DiskNumber,
    string TypeText,
    string StyleText,
    string StatusText,
    ulong SizeBytes,
    bool IsOffline,
    bool IsReadOnly,
    IReadOnlyList<PartitionItem> Partitions)
{
    public string Name => $"Disk {DiskNumber}";
}

public sealed record DiskReport(
    PhysicalDiskItem Disk,
    SmartSource Source,
    HealthSummary Health,
    IReadOnlyList<SmartRow> Rows,
    DiskLayout? Layout,
    IReadOnlyList<string> Notes);

// Disk Management writes binary sizes with decimal looking labels ("476.94 GB" for 512,110,190,592 bytes).
// The same convention is used here so figures match what the technician sees there.
public static class SizeFormat
{
    public static string Format(ulong bytes)
    {
        const double kb = 1024.0;
        double value = bytes;
        if (value >= kb * kb * kb * kb) return $"{value / (kb * kb * kb * kb):0.00} TB";
        if (value >= kb * kb * kb) return $"{value / (kb * kb * kb):0.00} GB";
        if (value >= kb * kb) return $"{value / (kb * kb):0.00} MB";
        if (value >= kb) return $"{value / kb:0.00} KB";
        return $"{bytes} bytes";
    }
}
