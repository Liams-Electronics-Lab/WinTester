using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace WinTester.Core.Storage;

// One physical disk's detected media type/rotational-speed signal, WMI-sourced. Shared between the Power
// test's fixed-wattage component table (WinTester.Tests.Power.ComponentInventory) and the Storage test's
// quick-pass gating (WinTester.Tests.Storage.DiskInfoProvider/StorageStressTestModule, Phase 3) — see
// DriveTypeDetector's own remark for why this lives in Core rather than either Tests.* project.
public enum DriveMediaKind
{
    Nvme,
    Ssd,
    Hdd,

    // MSFT_PhysicalDisk reported MediaType "Unspecified" (bus type known, media type isn't — seen on some
    // RAID/virtual controllers). Every caller here treats Unknown the same as Hdd: ComponentInventory folds
    // it into the same HDD wattage placeholder it always did before this extraction, and
    // StorageStressTestModule's quick-pass gating (Phase 3.2) treats it the same as a confirmed mechanical
    // HDD — erring toward the fast sequential-only path is safe, erring toward the slow full suite is the
    // whole problem Phase 3 fixes. This also covers the Phase 3.5 edge cases: an SMR drive still reports as
    // an ordinary HDD, and a USB-SATA bridge that hides media-type info from WMI entirely lands here too.
    Unknown
}

// PhysicalDriveIndex matches Win32_DiskDrive.Index (and, by extension, DiskInfoProvider.PhysicalDiskInfo's
// same-named field) exactly on the Win32_DiskDrive fallback path — both come from the same WMI class. On
// the primary MSFT_PhysicalDisk path it's parsed from MSFT_PhysicalDisk.DeviceId, documented by Microsoft
// as "the operating system's number for the drive"; this matches Win32_DiskDrive.Index on every
// configuration this was checked against by inspection, but — like the rest of this WMI-facing code — the
// join itself has not been confirmed against real multi-controller/RAID hardware where the two numbering
// schemes could plausibly diverge. A mismatch there would show up as a disk's Storage-test override
// (StorageTestOptions.ForceFullSuitePhysicalDriveIndexes/SkippedPhysicalDriveIndexes) or Power's per-disk
// wattage line landing on the wrong physical drive. Worth a real multi-disk-machine check before trusting
// it fully; this is no less reliable than the pre-Phase-3 code's own confidence level on this same class of
// WMI join — DiskInfoProvider's own remark on GetRelated/partition association makes the identical caveat.
public sealed record DriveTypeInfo(int PhysicalDriveIndex, string Name, DriveMediaKind MediaKind, uint? SpindleRpm);

// Extracted from WinTester.Tests.Power.ComponentInventory's DetectStorageViaMsftPhysicalDisk/
// DetectStorageViaWin32DiskDrive (Phase 3.1): one source of truth for "is this an NVMe/SSD/HDD" instead of
// the two copies that would otherwise exist once the Storage test needed the same signal ComponentInventory
// already had. Lives in WinTester.Core rather than either Tests.* project because Storage and Power don't
// reference each other (and shouldn't start to, just for this) — Core is the one project both already
// depend on. Same degrade-gracefully posture as the code this was extracted from: every method here
// returns an empty list rather than throwing, on a WMI failure or a non-Windows host alike.
public static class DriveTypeDetector
{
    private const int BusTypeNvme = 17; // STORAGE_BUS_TYPE.BusTypeNvme
    private const int MediaTypeHdd = 3; // MSFT_PhysicalDisk.MediaType: 3 = HDD
    private const int MediaTypeSsd = 4; // MSFT_PhysicalDisk.MediaType: 4 = SSD

    // Win32_DiskDrive.InterfaceType reports "SCSI" for most NVMe drives, and Model often contains neither
    // "SSD" nor "NVME"; this short list of known NVMe product-line substrings is the extra signal used only
    // on the Win32_DiskDrive fallback path, when MSFT_PhysicalDisk itself isn't available.
    private static readonly string[] NvmeModelHints =
        { "SNV2S", "SNV1S", "SKC3", "SN770", "SN850", "980PRO", "980EVO", "970EVO", "PM9A", "PM9B", "BC501", "BC711" };

    public static IReadOnlyList<DriveTypeInfo> DetectAll()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DriveTypeInfo>();

        // MSFT_PhysicalDisk's BusType field explicitly identifies NVMe; Win32_DiskDrive.InterfaceType
        // reports "SCSI" for most NVMe drives and is used only as a fallback when the newer WMI namespace
        // isn't present on this OS build.
        try
        {
            var viaStorageMgmt = DetectViaMsftPhysicalDisk();
            if (viaStorageMgmt.Count > 0) return viaStorageMgmt;
        }
        catch
        {
            // Namespace/class not present on this OS build: fall through to Win32_DiskDrive below.
        }

        try
        {
            return DetectViaWin32DiskDrive();
        }
        catch
        {
            return Array.Empty<DriveTypeInfo>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DriveTypeInfo> DetectViaMsftPhysicalDisk()
    {
        var lines = new List<DriveTypeInfo>();

        var scope = new System.Management.ManagementScope(@"root\Microsoft\Windows\Storage");
        scope.Connect();
        using var searcher = new System.Management.ManagementObjectSearcher(scope,
            new System.Management.ObjectQuery(
                "SELECT DeviceId, FriendlyName, MediaType, BusType, SpindleSpeed FROM MSFT_PhysicalDisk"));

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            var name = obj["FriendlyName"] as string ?? "Unknown disk";
            int mediaType = obj["MediaType"] is { } mt ? Convert.ToInt32(mt) : 0;
            int busType = obj["BusType"] is { } bt ? Convert.ToInt32(bt) : 0;

            // DeviceId is a string ("0", "1", ...); see this file's DriveTypeInfo remark on why the join to
            // PhysicalDriveIndex is best-effort, not confirmed, on this specific path.
            int index = obj["DeviceId"] is { } devIdRaw && int.TryParse(devIdRaw.ToString(), out var parsedIdx)
                ? parsedIdx
                : -1;

            uint? spindleRpm = null;
            if (obj["SpindleSpeed"] is { } ssRaw)
            {
                uint ss = Convert.ToUInt32(ssRaw);
                if (ss != 0 && ss != 0xFFFFFFFF) spindleRpm = ss;
            }

            var kind = busType == BusTypeNvme ? DriveMediaKind.Nvme
                : mediaType == MediaTypeSsd ? DriveMediaKind.Ssd
                : mediaType == MediaTypeHdd ? DriveMediaKind.Hdd
                : DriveMediaKind.Unknown; // bus known, media isn't — see DriveMediaKind.Unknown's remark.

            lines.Add(new DriveTypeInfo(index, name, kind, spindleRpm));
        }

        return lines;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<DriveTypeInfo> DetectViaWin32DiskDrive()
    {
        var lines = new List<DriveTypeInfo>();

        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Model, InterfaceType, MediaType, Size, Index FROM Win32_DiskDrive");

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            var model = obj["Model"] as string ?? "Unknown disk";
            var interfaceType = (obj["InterfaceType"] as string ?? "").ToUpperInvariant();
            var mediaType = (obj["MediaType"] as string ?? "");
            int index = obj["Index"] is { } idxVal ? Convert.ToInt32(idxVal) : -1;

            bool isNvme = interfaceType.Contains("NVME") ||
                          model.Contains("NVME", StringComparison.OrdinalIgnoreCase) ||
                          NvmeModelHints.Any(h => model.Contains(h, StringComparison.OrdinalIgnoreCase));
            bool isSsd = !isNvme && (mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                         model.Contains("SSD", StringComparison.OrdinalIgnoreCase));

            // No Unspecified concept on this fallback path (Win32_DiskDrive.MediaType is a free-text
            // string, not MSFT_PhysicalDisk's numeric enum with its own "Unspecified" value); anything
            // that isn't identified as NVMe/SSD is assumed HDD here, same as this code's pre-Phase-3
            // behavior when MSFT_PhysicalDisk wasn't available.
            var kind = isNvme ? DriveMediaKind.Nvme : isSsd ? DriveMediaKind.Ssd : DriveMediaKind.Hdd;

            // Win32_DiskDrive exposes no SpindleSpeed equivalent at all (that's an MSFT_PhysicalDisk-only
            // field); every disk on this fallback path gets a null SpindleRpm regardless of kind.
            lines.Add(new DriveTypeInfo(index, model, kind, null));
        }

        return lines;
    }
}
