using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using WinTester.Core;
using WinTester.Core.Storage;

namespace WinTester.Tests.Power;

// Fixed-wattage component table: detects every applicable line item via WMI/PnP, applies the fixed per-component wattage table (everything except the two measured CPU/GPU peaks), sums them, adds headroom, and rounds up to the nearest commercial PSU tier.
// Every detection method degrades to zero/empty rather than throwing; a WMI class being unavailable on a given machine shrinks the total, it doesn't crash the Power tile.
public static class ComponentInventory
{
    // Fixed tier list: round the recommended budget up to the nearest value here, never down. Values above the last tier round to nearest 50W and are flagged as outside the standard list, rather than clamping.
    private static readonly int[] Tiers =
    {
        20, 25, 45, 65, 90, 120, 180, 200, 250, 300, 350, 400, 450, 500, 550, 600, 650, 700, 750, 800,
        850, 900, 950, 1000, 1200, 1500, 2000
    };

    // Laptops don't take a headroom percentage; the recommended figure rounds straight up to the nearest commercial laptop-charger spec instead (desktops keep the headroom + PSU-tier approach via Tiers). Includes in-between specs from 120W up to the 400W ceiling so a recommended budget doesn't round as far past what a real charger offers in that range.
    private static readonly int[] LaptopChargerTiers =
    {
        20, 30, 45, 65, 90, 95, 120, 135, 150, 170, 180, 200, 220, 230, 240, 260, 280, 300, 330, 360, 380, 400
    };

    // Desktop headroom multiplier.
    private const double DesktopHeadroomMultiplier = 1.20;

    public static PowerBudgetResult Calculate(
        MachineKind machineKind,
        PeakPowerResult cpuPeak,
        PeakPowerResult gpuPeak,
        IReadOnlyList<FanTelemetry> fans)
    {
        // Clamped to >= 0 for the budget total only; the unclamped PeakPowerResult (in the returned
        // PowerBudgetResult and in PowerTestModule's "CPU/GPU Peak Power" KeyMetrics) still shows the real
        // measured value, so a technician can see an anomaly. A negative wattage is never physically real
        // and would silently understate the recommendation if folded into the total.
        double cpuComponentWatts = Math.Max(0, cpuPeak.Watts ?? 0);
        double gpuComponentWatts = Math.Max(0, gpuPeak.Watts ?? 0);
        var components = new List<ComponentLine>
        {
            new("GPU (peak, summed across GPUs)", gpuComponentWatts, gpuPeak.Detail),
            new("CPU (peak)", cpuComponentWatts, cpuPeak.Detail)
        };

        components.AddRange(DetectStorage(machineKind));
        components.AddRange(DetectOpticalDrives(machineKind));
        components.Add(DetectFans(machineKind, fans));
        components.Add(DetectAdditionalPciCards(machineKind));
        components.AddRange(DetectNetworkAdapters(machineKind));
        components.Add(DetectMotherboard(machineKind));
        components.Add(DetectCardReader());
        if (machineKind == MachineKind.Laptop)
            components.Add(DetectInternalWebcam());
        if (machineKind == MachineKind.Laptop)
            components.Add(new ComponentLine("Screen (LCD)", 5, ""));
        components.Add(DetectModemWwan(machineKind));
        components.Add(DetectRam(machineKind));

        // Drop zero-watt lines from the visible breakdown, except the two measured peaks; always shown, even at 0/unavailable, so the report is explicit about why.
        var visibleComponents = components
            .Where(c => c.Watts > 0 || c.Name.StartsWith("GPU") || c.Name.StartsWith("CPU"))
            .ToList();

        double total = components.Sum(c => c.Watts);

        double recommended;
        int? tier;
        bool exceedsTierList;

        if (machineKind == MachineKind.Laptop)
        {
            // Laptops: no headroom percentage; round straight up to the smallest commercial charger spec that
            // covers the total (239W -> 240W, not the next spec above it). The component wattages are already
            // deliberately worst-case, so no extra step is added on top.
            recommended = total;
            (tier, exceedsTierList) = RoundToLaptopCharger(recommended);
        }
        else
        {
            recommended = total * DesktopHeadroomMultiplier;
            int foundTier = Tiers.FirstOrDefault(t => t >= recommended, -1);
            tier = foundTier > 0 ? foundTier : null;
            exceedsTierList = tier is null;
        }

        return new PowerBudgetResult(
            MachineKind: machineKind,
            Components: visibleComponents,
            TotalWatts: Math.Round(total, 1),
            RecommendedWatts: Math.Round(recommended, 1),
            RecommendedTierWatts: tier,
            ExceedsTierList: exceedsTierList,
            CpuPeak: cpuPeak,
            GpuPeak: gpuPeak);
    }

    // Smallest commercial laptop-charger spec that covers the wattage; null (and exceeds = true) above the list.
    private static (int? Tier, bool Exceeds) RoundToLaptopCharger(double watts)
    {
        int found = LaptopChargerTiers.FirstOrDefault(t => t >= watts, -1);
        return found > 0 ? (found, false) : (null, true);
    }

    // Laptop budget built from a measured whole-platform peak (PSYS / "Total System Power", or a reading shown to
    // include the GPU). That figure already contains the CPU, every GPU, RAM, storage, the screen and the board,
    // so nothing is added on top of it; the CPU and GPU peaks are listed at 0 W as informational lines only.
    public static PowerBudgetResult CalculateFromMeasuredPlatform(
        PeakPowerResult platformPeak, PeakPowerResult cpuPeak, PeakPowerResult gpuPeak, string basis)
    {
        double platformWatts = Math.Max(0, platformPeak.Watts ?? 0);

        static string Informational(PeakPowerResult p) =>
            p.Watts is { } w ? $"{w:0.0}W measured; counted in the platform figure. {p.Detail}".Trim() : p.Detail;

        var components = new List<ComponentLine>
        {
            new("Measured platform power (peak under CPU+GPU load)", platformWatts, platformPeak.Detail),
            new("CPU (peak, informational: included in the platform figure)", 0, Informational(cpuPeak)),
            new("GPU (peak, informational: included in the platform figure)", 0, Informational(gpuPeak))
        };

        var (tier, exceeds) = RoundToLaptopCharger(platformWatts);

        return new PowerBudgetResult(
            MachineKind: MachineKind.Laptop,
            Components: components,
            TotalWatts: Math.Round(platformWatts, 1),
            RecommendedWatts: Math.Round(platformWatts, 1),
            RecommendedTierWatts: tier,
            ExceedsTierList: exceeds,
            CpuPeak: cpuPeak,
            GpuPeak: gpuPeak,
            MeasurementBasis: basis);
    }

    // Laptop budget when a whole-platform reading also exists: the summed components (CPU package peak + discrete-GPU
    // peak + the fixed table) stay the recommendation, because the separate CPU and GPU peaks added together are the
    // deliberate worst case, whereas the platform reading is only what the machine happened to draw during the passes
    // (the combined pass is CPU-starved by the GPU engine and never reaches the true simultaneous peak). The platform
    // peak is listed as a cross-check that adds nothing, and acts as a floor: if it is higher than the sum, the
    // difference is added as its own line so the breakdown still adds up to the recommended figure.
    public static PowerBudgetResult WithPlatformCrossCheck(PowerBudgetResult summed, PeakPowerResult platformPeak)
    {
        double platformWatts = Math.Max(0, platformPeak.Watts ?? 0);
        var components = summed.Components.ToList();
        double total = summed.TotalWatts;
        string basis;

        if (platformWatts > total)
        {
            components.Add(new ComponentLine("Uplift to measured platform peak", Math.Round(platformWatts - total, 1),
                $"The measured whole-platform peak ({platformWatts:0.0}W) was higher than the component sum ({total:0.0}W)."));
            total = platformWatts;
            basis = $"Summed components, raised to the measured whole-platform peak of {platformWatts:0.0}W because that was higher.";
        }
        else
        {
            components.Add(new ComponentLine("Measured platform power (cross-check, not added)", 0,
                $"{platformWatts:0.0}W peak. {platformPeak.Detail}".Trim()));
            basis = "Summed components: CPU package peak plus discrete-GPU peak (an integrated GPU is inside the CPU package and is " +
                    $"not added), plus the fixed component table. The measured whole-platform peak ({platformWatts:0.0}W) is lower and is shown only as a cross-check.";
        }

        var (tier, exceeds) = RoundToLaptopCharger(total);
        return summed with
        {
            Components = components,
            TotalWatts = Math.Round(total, 1),
            RecommendedWatts = Math.Round(total, 1),
            RecommendedTierWatts = tier,
            ExceedsTierList = exceeds,
            MeasurementBasis = basis
        };
    }

    // Rounds up to the nearest 50W for the "exceeds the standard tier list" case.
    public static int RoundUpToNearest50(double watts) => (int)(Math.Ceiling(watts / 50.0) * 50);

    // --- Storage ------------------------------------------------------------------------------------

    // Fixed wattages per storage type. HDD form factor (2.5"/3.5") has no reliable WMI signal, so it's inferred from machine kind: laptops assume a 2.5" 5400RPM drive, desktops a 3.5" drive. The RPM tier is picked from MSFT_PhysicalDisk.SpindleSpeed when available, else the machine kind's default tier (5400RPM laptop / 7200RPM desktop).
    private const double NvmeWatts = 3.5;
    private const double SataSsdWatts = 2.6;
    private const double Hdd25Watts = 5.0;          // 2.5" 5400RPM (laptop default)
    private const double Hdd25Watts7200 = 6.5;      // 2.5" 7200RPM (laptop, detected only)
    private const double Hdd35Watts7200 = 20.0;
    private const double Hdd35WattsOver7200 = 24.0;
    private const double Hdd35WattsUnder7200 = 10.0;

    // Phase 3.1: the actual WMI drive-type detection now lives in WinTester.Core.Storage.DriveTypeDetector
    // (shared with the Storage test's quick-pass gating); this method only maps its DriveTypeInfo results
    // onto Power's fixed-wattage ComponentLine table, same collapse of Hdd/Unknown into the HDD wattage
    // placeholder this method always made before the extraction.
    private static IReadOnlyList<ComponentLine> DetectStorage(MachineKind machineKind)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ComponentLine>();

        IReadOnlyList<DriveTypeInfo> drives;
        try
        {
            drives = DriveTypeDetector.DetectAll();
        }
        catch
        {
            // DriveTypeDetector.DetectAll() already degrades to an empty list internally, mirroring this
            // method's own pre-Phase-3 try/catch-per-backend shape; this catch is only a backstop in case a
            // future change inside it forgets to.
            return Array.Empty<ComponentLine>();
        }

        return drives
            .Select(d => d.MediaKind switch
            {
                DriveMediaKind.Nvme => new ComponentLine("NVMe SSD", NvmeWatts, d.Name),
                DriveMediaKind.Ssd => new ComponentLine("SATA SSD (2.5\")", SataSsdWatts, d.Name),
                // Hdd and Unknown both collapse to the HDD wattage placeholder — Unknown covers "bus known
                // but media isn't" (some RAID/virtual controllers), same as before this extraction.
                _ => BuildHddLine(machineKind, d.Name, d.SpindleRpm)
            })
            .ToList();
    }

    // Builds the HDD component line. Form factor (2.5"/3.5") can't be read from WMI, so it's inferred from
    // machine kind: a laptop can't hold a 3.5" drive, so laptops get the 2.5" tiers (5400RPM by default) and
    // desktops keep the 3.5" tiers. Only the RPM tier varies beyond that, using spindleRpm when known.
    private static ComponentLine BuildHddLine(MachineKind machineKind, string name, uint? spindleRpm)
    {
        if (machineKind == MachineKind.Laptop)
        {
            if (spindleRpm is { } lrpm)
            {
                return lrpm > 5400
                    ? new ComponentLine("HDD (2.5\" laptop, >5400RPM detected)", Hdd25Watts7200, $"{name}, {lrpm} RPM (SpindleSpeed).")
                    : new ComponentLine("HDD (2.5\" laptop, 5400RPM detected)", Hdd25Watts, $"{name}, {lrpm} RPM (SpindleSpeed).");
            }

            return new ComponentLine("HDD (RPM tier undetected: assumed 2.5\" 5400RPM laptop drive)", Hdd25Watts, name);
        }

        if (spindleRpm is { } rpm)
        {
            if (rpm > 7200)
                return new ComponentLine("HDD (3.5\" placeholder, >7200RPM detected)", Hdd35WattsOver7200, $"{name}, {rpm} RPM (SpindleSpeed).");
            if (rpm < 7200)
                return new ComponentLine("HDD (3.5\" placeholder, <7200RPM detected)", Hdd35WattsUnder7200, $"{name}, {rpm} RPM (SpindleSpeed).");
            return new ComponentLine("HDD (3.5\" placeholder, 7200RPM detected)", Hdd35Watts7200, $"{name}, {rpm} RPM (SpindleSpeed).");
        }

        return new ComponentLine("HDD (form factor/RPM tier undetected: assumed 3.5\" 7200RPM)", Hdd35Watts7200, name);
    }

    // --- Optical drives -------------------------------------------------------------------------------

    private static IReadOnlyList<ComponentLine> DetectOpticalDrives(MachineKind kind)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ComponentLine>();

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_CDROMDrive");
            int count = searcher.Get().Count;
            if (count == 0) return Array.Empty<ComponentLine>();

            double perDriveWatts = kind == MachineKind.Desktop ? 30 : 8;
            return Enumerable.Range(0, count)
                .Select(i => new ComponentLine("Optical drive", perDriveWatts,
                    kind == MachineKind.Desktop ? "Assumed BDR" : "Assumed slim"))
                .ToList();
        }
        catch
        {
            return Array.Empty<ComponentLine>();
        }
    }

    // --- Fans -----------------------------------------------------------------------------------------

    private static ComponentLine DetectFans(MachineKind kind, IReadOnlyList<FanTelemetry> fans)
    {
        double perFanWatts = kind == MachineKind.Desktop ? 5.0 : 1.5;
        return new ComponentLine("Fan interface", fans.Count * perFanWatts,
            $"{fans.Count} fan(s) reporting RPM via ISensorService ({perFanWatts:0.0}W/fan, {kind}).");
    }

    // --- Additional PCI cards ---------------------------------------------------------------------------

    private static ComponentLine DetectAdditionalPciCards(MachineKind kind)
    {
        if (kind == MachineKind.Laptop)
            return new ComponentLine("Additional PCI card", 0, "Laptops rarely expose user PCI slots; not counted.");

        if (!OperatingSystem.IsWindows())
            return new ComponentLine("Additional PCI card", 0, "Not running on Windows.");

        try
        {
            return DetectAdditionalPciCardsViaWmi();
        }
        catch (Exception ex)
        {
            return new ComponentLine("Additional PCI card", 0, $"WMI query failed: {ex.Message}");
        }
    }

    private static readonly string[] ExcludedPnpClasses = { "Display", "SCSI", "USB", "Net", "System", "HDC" };
    private static readonly string[] GpuNameHints = { "nvidia", "geforce", "rtx", "gtx", "radeon", "amd", "arc", "intel iris", "intel uhd" };

    [SupportedOSPlatform("windows")]
    private static ComponentLine DetectAdditionalPciCardsViaWmi()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT DeviceID, Name, PNPClass FROM Win32_PnPEntity");

        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            var deviceId = obj["DeviceID"] as string ?? "";
            if (!deviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;

            var pnpClass = obj["PNPClass"] as string ?? "";
            if (ExcludedPnpClasses.Any(c => c.Equals(pnpClass, StringComparison.OrdinalIgnoreCase))) continue;

            var name = obj["Name"] as string ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Catch GPU sub-functions (e.g. HDMI audio) that show up as their own PCI-class entry.
            if (GpuNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;

            distinctNames.Add(name);
        }

        int count = distinctNames.Count;
        return new ComponentLine("Additional PCI card", count * 25,
            count == 0 ? "None detected." : string.Join("; ", distinctNames));
    }

    // --- Network adapters -----------------------------------------------------------------------------

    private static readonly string[] WirelessHints = { "wi-fi", "wifi", "wireless", "802.11", "bluetooth" };
    private static readonly string[] EthernetHints = { "ethernet", "gigabit", "gbe" };

    // Name substrings for common virtualization/software adapters, a backstop alongside the DeviceID bus check below (some virtual adapters enumerate under non-obvious device IDs).
    private static readonly string[] VirtualAdapterHints =
    {
        "virtual", "vmware", "virtualbox", "hyper-v", "wan miniport", "loopback", "tap-", "npcap",
        "wintun", "wi-fi direct virtual", "personal area network"
    };

    private static readonly double EthernetPhysicalWatts = 3.0;
    private static readonly double WifiPhysicalWatts = 2.0;

    private static IReadOnlyList<ComponentLine> DetectNetworkAdapters(MachineKind kind)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ComponentLine>();

        try
        {
            return DetectNetworkAdaptersViaWmi();
        }
        catch
        {
            return Array.Empty<ComponentLine>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ComponentLine> DetectNetworkAdaptersViaWmi()
    {
        var lines = new List<ComponentLine>();

        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Name, PNPClass, DeviceID FROM Win32_PnPEntity");

        foreach (System.Management.ManagementBaseObject obj in searcher.Get())
        {
            var pnpClass = obj["PNPClass"] as string ?? "";
            if (!pnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase)) continue;

            var name = obj["Name"] as string ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Only count adapters on a real physical bus (PCI/USB); virtual/software adapters (VirtualBox, VMware, Hyper-V, Wi-Fi Direct, Bluetooth PAN, etc.) enumerate under other prefixes (typically ROOT\) and draw negligible power.
            var deviceId = obj["DeviceID"] as string ?? "";
            bool onPhysicalBus = deviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) ||
                                  deviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
            if (!onPhysicalBus) continue;
            if (VirtualAdapterHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;

            if (WirelessHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                lines.Add(new ComponentLine("Network adapter: WiFi/BT", WifiPhysicalWatts, name));
            else if (EthernetHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                lines.Add(new ComponentLine("Network adapter: Ethernet", EthernetPhysicalWatts, name));
        }

        return lines;
    }

    // --- Motherboard (fixed, unconditional) --------------------------------------------------------

    private static ComponentLine DetectMotherboard(MachineKind kind) =>
        kind == MachineKind.Desktop
            ? new ComponentLine("Motherboard", 15, "Desktop; fixed 15W.")
            : new ComponentLine("Motherboard", 3.5, "");

    // --- Card reader ----------------------------------------------------------------------------------

    private static ComponentLine DetectCardReader()
    {
        if (!OperatingSystem.IsWindows())
            return new ComponentLine("Multi-card reader", 0, "Not running on Windows.");

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%card reader%'");
            bool found = searcher.Get().Count > 0;
            return new ComponentLine("Multi-card reader", found ? 2.5 : 0, found ? "Detected." : "Not detected.");
        }
        catch (Exception ex)
        {
            return new ComponentLine("Multi-card reader", 0, $"WMI query failed: {ex.Message}");
        }
    }

    // --- Webcam (laptop only, internal only) -----------------------------------------------------------

    private static readonly string[] BuiltInWebcamHints = { "integrated", "built-in", "internal" };

    private static ComponentLine DetectInternalWebcam()
    {
        if (!OperatingSystem.IsWindows())
            return new ComponentLine("Webcam (internal)", 0, "Not running on Windows.");

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPClass FROM Win32_PnPEntity");

            foreach (System.Management.ManagementBaseObject obj in searcher.Get())
            {
                var pnpClass = obj["PNPClass"] as string ?? "";
                if (!pnpClass.Equals("Image", StringComparison.OrdinalIgnoreCase) &&
                    !pnpClass.Equals("Camera", StringComparison.OrdinalIgnoreCase)) continue;

                var name = obj["Name"] as string ?? "";
                if (BuiltInWebcamHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    return new ComponentLine("Webcam (internal)", 2, name);
            }

            return new ComponentLine("Webcam (internal)", 0, "No built-in webcam detected.");
        }
        catch (Exception ex)
        {
            return new ComponentLine("Webcam (internal)", 0, $"WMI query failed: {ex.Message}");
        }
    }

    // --- Modem/WWAN -------------------------------------------------------------------------------------

    private static ComponentLine DetectModemWwan(MachineKind kind)
    {
        if (!OperatingSystem.IsWindows())
            return new ComponentLine("Modem/WWAN", 0, "Not running on Windows.");

        try
        {
            using var potsSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_POTSModem");
            if (potsSearcher.Get().Count > 0)
                return new ComponentLine("Modem/WWAN", 15, "Win32_POTSModem device detected.");

            using var netSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%WWAN%' OR Name LIKE '%mobile broadband%'");
            if (netSearcher.Get().Count > 0)
                return new ComponentLine("Modem/WWAN", 15, "WWAN-class network adapter detected.");

            return new ComponentLine("Modem/WWAN", 0, "Not detected.");
        }
        catch (Exception ex)
        {
            return new ComponentLine("Modem/WWAN", 0, $"WMI query failed: {ex.Message}");
        }
    }

    // --- RAM --------------------------------------------------------------------------------------------

    private static ComponentLine DetectRam(MachineKind kind)
    {
        // Per-module draw from datasheet figures.
        double perModuleWatts = kind == MachineKind.Desktop ? 1.56 : 1.16;

        if (!OperatingSystem.IsWindows())
            return new ComponentLine("RAM", 0, "Not running on Windows.");

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Capacity FROM Win32_PhysicalMemory");
            int moduleCount = searcher.Get().Count;

            return new ComponentLine("RAM", moduleCount * perModuleWatts,
                $"{moduleCount} populated module(s) x {perModuleWatts:0.00}W.");
        }
        catch (Exception ex)
        {
            return new ComponentLine("RAM", 0, $"WMI query failed: {ex.Message}");
        }
    }
}
