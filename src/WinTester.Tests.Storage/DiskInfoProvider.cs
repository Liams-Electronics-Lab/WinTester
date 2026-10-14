using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using WinTester.Core.Storage;

namespace WinTester.Tests.Storage;

// One physical disk with a drive-lettered volume WinTester can safely place a test file on. Never targets a raw physical device (\\.\PhysicalDriveN) directly; that needs elevated raw-disk access and risks the partition table on a mis-targeted drive; fio instead runs against an ordinary file on a normal volume, same as KDiskMark.
public sealed record PhysicalDiskInfo(
    // e.g. "D:", no trailing backslash, matches Win32_LogicalDisk.DeviceID.
    string DriveLetter,
    // Raw Win32_DiskDrive.Model string, shown as-is in the benchmark overlay; not reformatted or vendor/model-split the way other sensor output sometimes is.
    string Model,
    long TotalBytes,
    long FreeBytes,
    // Win32_DiskDrive.Index; kept for diagnostics/logging only (e.g. "PhysicalDrive1"), not used for any WMI re-query.
    int PhysicalDriveIndex,
    // Phase 3.2: from WinTester.Core.Storage.DriveTypeDetector, keyed onto this disk by PhysicalDriveIndex
    // in EnumerateViaWmi below. Drives StorageStressTestModule's quick-pass gating (skip Rand4K Q1T1 on a
    // mechanical HDD) and defaults to Unknown — same "treat as HDD" posture DriveMediaKind.Unknown's own
    // remark describes — for the _enumerateDisks test seam and for any disk DriveTypeDetector's own lookup
    // didn't cover.
    DriveMediaKind MediaKind = DriveMediaKind.Unknown);

// Enumerates physical disks and resolves each to one drive-lettered volume for the fio benchmark. Degrades gracefully (empty list, never throws) on WMI failure or a non-Windows host, same posture as CpuVendorDetector/ISensorService elsewhere; an out diagnostics list explains, per disk, which step came up empty or threw. Partition/volume association uses ManagementObject.GetRelated() rather than a hand-built ASSOCIATORS OF query string; the query-string form threw WBEM_E_NOT_FOUND on real hardware even with correctly-escaped paths, and GetRelated resolves the association from the already-bound object instead, removing that whole class of escaping bug.
public static class DiskInfoProvider
{
    // Convenience overload for callers that don't need the diagnostics (e.g. StorageStressTestModule's enumerateDisks test seam).
    public static IReadOnlyList<PhysicalDiskInfo> EnumerateTestableDisks() =>
        EnumerateTestableDisks(out _);

    public static IReadOnlyList<PhysicalDiskInfo> EnumerateTestableDisks(out IReadOnlyList<string> diagnostics)
    {
        var diag = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            diag.Add("Not running on Windows; WMI disk enumeration is skipped entirely on this platform.");
            diagnostics = diag;
            return Array.Empty<PhysicalDiskInfo>();
        }

        try
        {
            var result = EnumerateViaWmi(diag);
            diagnostics = diag;
            return result;
        }
        catch (Exception ex)
        {
            // Something above the per-disk try/catch inside EnumerateViaWmi itself threw (e.g. the Win32_DiskDrive query, not just one disk's association walk). Degrades to an empty list rather than taking down the whole storage test, but the reason survives into StorageStressTestModule's failure text.
            diag.Add($"Win32_DiskDrive enumeration threw {ex.GetType().Name}: {ex.Message}");
            diagnostics = diag;
            return Array.Empty<PhysicalDiskInfo>();
        }
    }

    // Walks Win32_DiskDrive → (GetRelated) Win32_DiskPartition → (GetRelated) Win32_LogicalDisk. Not verified against a real multi-partition/multi-volume disk; on a disk with more than one lettered volume, picks whichever GetRelated returns first, a reasonable default but not confirmed as "the right" volume without real hardware to check against.
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<PhysicalDiskInfo> EnumerateViaWmi(List<string> diag)
    {
        var results = new List<PhysicalDiskInfo>();

        // Phase 3.2: one shared drive-type lookup for the whole enumeration, keyed by PhysicalDriveIndex.
        // A lookup failure (or a duplicate-index result from DriveTypeDetector, which ToDictionary would
        // otherwise throw on) degrades to an empty map here, and the per-disk lookup below already treats
        // "not found" the same as DriveMediaKind.Unknown — a media-type detection problem never blocks a
        // disk from being enumerated, matching this whole method's existing degrade-gracefully posture.
        IReadOnlyDictionary<int, DriveTypeInfo> driveTypesByIndex;
        try
        {
            driveTypesByIndex = DriveTypeDetector.DetectAll().ToDictionary(d => d.PhysicalDriveIndex);
        }
        catch
        {
            driveTypesByIndex = new Dictionary<int, DriveTypeInfo>();
        }

        using var driveSearcher = new System.Management.ManagementObjectSearcher(
            "SELECT DeviceID, Model, Index FROM Win32_DiskDrive");

        int diskCount = 0;

        foreach (System.Management.ManagementObject driveObj in driveSearcher.Get())
        {
            using var _driveObj = driveObj; // dispose at end of each loop iteration
            diskCount++;

            var deviceId = driveObj["DeviceID"] as string; // \\.\PHYSICALDRIVE0
            var model = (driveObj["Model"] as string)?.Trim() ?? "Unknown Drive";
            var index = driveObj["Index"] is { } idxVal ? System.Convert.ToInt32(idxVal) : -1;

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                diag.Add($"Disk index {index} ({model}): Win32_DiskDrive row had no DeviceID, skipped.");
                continue;
            }

            string? driveLetter;
            try
            {
                driveLetter = FindFirstDriveLetter(driveObj, deviceId, model, diag);
            }
            catch (Exception ex)
            {
                // Isolates one disk's association-lookup failure; a bad GetRelated call on one disk shouldn't stop the others from being found.
                diag.Add($"{deviceId} ({model}): partition association lookup threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (driveLetter is null)
                continue; // FindFirstDriveLetter already recorded the specific reason in diag.

            if (!TryGetLogicalDiskSpace(driveLetter, out var total, out var free))
            {
                diag.Add($"{deviceId} ({model}) → {driveLetter}: volume found but Win32_LogicalDisk " +
                         "reported no usable Size for it, skipped.");
                continue;
            }

            var mediaKind = driveTypesByIndex.TryGetValue(index, out var driveType)
                ? driveType.MediaKind
                : DriveMediaKind.Unknown;

            results.Add(new PhysicalDiskInfo(driveLetter, model, total, free, index, mediaKind));
        }

        if (diskCount == 0)
            diag.Add("Win32_DiskDrive query returned zero rows: WMI/the Winmgmt service may be " +
                     "unavailable, or the process lacks permission to query it at all.");

        return results;
    }

    // Resolves one disk's first lettered volume via GetRelated. Records its own diagnostic line since it
    // has the most specific context on which step came up empty: zero partitions vs. partitions with no
    // lettered volume.
    [SupportedOSPlatform("windows")]
    private static string? FindFirstDriveLetter(
        System.Management.ManagementObject driveObj, string deviceId, string model, List<string> diag)
    {
        using var partitions = driveObj.GetRelated("Win32_DiskPartition");

        int partitionCount = 0;
        foreach (System.Management.ManagementObject partitionObj in partitions)
        {
            using var _partitionObj = partitionObj;
            partitionCount++;

            using var logicalDisks = partitionObj.GetRelated("Win32_LogicalDisk");
            foreach (System.Management.ManagementObject logicalDiskObj in logicalDisks)
            {
                using var _logicalDiskObj = logicalDiskObj;
                var driveLetter = logicalDiskObj["DeviceID"] as string; // e.g. "D:"
                if (!string.IsNullOrWhiteSpace(driveLetter))
                    return driveLetter;
            }
        }

        diag.Add(partitionCount == 0
            ? $"{deviceId} ({model}): GetRelated(\"Win32_DiskPartition\") returned zero partitions " +
              "(unpartitioned, hidden EFI/recovery-only, or a dynamic/Storage-Spaces disk that WMI's " +
              "standard partition association doesn't cover)."
            : $"{deviceId} ({model}): found {partitionCount} partition(s) but none had a lettered " +
              "Win32_LogicalDisk volume (e.g. an unformatted/unmounted partition).");

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetLogicalDiskSpace(string driveLetter, out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;

        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Size, FreeSpace FROM Win32_LogicalDisk WHERE DeviceID='" + driveLetter + "'");

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            if (obj["Size"] is { } sizeVal) totalBytes = System.Convert.ToInt64(sizeVal);
            if (obj["FreeSpace"] is { } freeVal) freeBytes = System.Convert.ToInt64(freeVal);
            return totalBytes > 0;
        }

        return false;
    }
}
