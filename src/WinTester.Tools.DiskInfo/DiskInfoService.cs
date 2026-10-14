using System.Runtime.Versioning;

namespace WinTester.Tools.DiskInfo;

// Two calls for the window: the list of disks, and the full report for one disk. Both block, so the window runs them
// off the UI thread. Neither throws: every failure becomes a note in the report.
public static class DiskInfoService
{
    public static Task<DiskListResult> ListDisksAsync() => Task.Run(DiskEnumerator.List);

    public static Task<DiskReport> BuildReportAsync(PhysicalDiskItem disk) => Task.Run(() => BuildReport(disk));

    public static DiskReport BuildReport(PhysicalDiskItem disk)
    {
        if (!OperatingSystem.IsWindows())
            return Unsupported(disk);

        return BuildWindowsReport(disk);
    }

    private static DiskReport Unsupported(PhysicalDiskItem disk)
    {
        var evaluation = SmartEvaluator.Evaluate(new DiskReadings(disk.Media, disk.Model, null, null, null, null, null, null, null));
        return new DiskReport(disk, evaluation.Source, evaluation.Health, evaluation.Rows, null,
            new[] { "Disk information is only available on Windows." });
    }

    [SupportedOSPlatform("windows")]
    private static DiskReport BuildWindowsReport(PhysicalDiskItem disk)
    {
        var notes = new List<string>();

        AtaSmartData? ata = null;
        NvmeHealth? nvme = null;

        // NVMe disks have no ATA table, and SATA disks have no NVMe log, so each reader is tried only where it can
        // answer. A disk of unknown type is tried both ways.
        if (disk.Media != DiskMedia.Nvme)
        {
            var result = AtaSmartReader.Read(disk);
            ata = result.Data;
            if (result.ErrorMessage is not null) notes.Add(result.ErrorMessage);
        }

        if (ata is null && disk.Media is DiskMedia.Nvme or DiskMedia.Unknown)
        {
            var result = NvmeSmartReader.Read(disk.Index);
            nvme = result.Health;
            if (nvme is null && disk.Media == DiskMedia.Nvme && result.ErrorMessage is not null) notes.Add(result.ErrorMessage);
        }

        var windows = ata is null && nvme is null ? ReliabilityCounterReader.Read(disk.Index) : null;
        var layout = PartitionLayoutReader.Read(disk.Index);
        if (layout.ErrorMessage is not null) notes.Add(layout.ErrorMessage);

        var readings = new DiskReadings(
            disk.Media, disk.Model, ata, nvme, windows, disk.WindowsHealthStatus,
            layout.FreeBytes, layout.TotalBytes,
            disk.Media is DiskMedia.Ssd or DiskMedia.Nvme ? TrimReader.Read() : null);

        var evaluation = SmartEvaluator.Evaluate(readings);

        if (evaluation.Source == SmartSource.WindowsCounters)
            notes.Add("The drive's own S.M.A.R.T. table could not be read. The values shown come from Windows' storage counters and may be incomplete.");
        else if (evaluation.Source == SmartSource.None)
            notes.Add("No S.M.A.R.T. data could be read for this disk. This is common for drives in USB enclosures and behind some RAID controllers.");

        return new DiskReport(disk, evaluation.Source, evaluation.Health, evaluation.Rows, layout.Layout, notes);
    }
}
