using System.Collections.Generic;

namespace WinTester.Core.Devices;

// Translates a WMI PNPClass value ("Net", "HIDClass") into the group heading Device Manager shows
// ("Network adapters", "Human Interface Devices"), so the Devices tool's tree reads the same way the tool
// a tech already knows does.
//
// A static table rather than a registry lookup of the class GUID's friendly name: reading
// HKLM\SYSTEM\CurrentControlSet\Control\Class\{guid} would give the exact localised string Windows itself
// uses, but costs a registry dependency in a project that otherwise only needs WMI, and this list covers
// every class a repair shop realistically sees. An unmapped class falls back to the raw PNPClass value, so
// nothing is ever hidden or mislabelled by omission — it just reads less prettily.
public static class DeviceClassNames
{
    // Device Manager's heading for devices Windows hasn't classified at all (empty PNPClass), which is
    // where freshly-imaged machines missing chipset drivers pile up.
    public const string Unclassified = "Other devices";

    private static readonly Dictionary<string, string> Names = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["AudioEndpoint"] = "Audio inputs and outputs",
        ["Battery"] = "Batteries",
        ["Biometric"] = "Biometric devices",
        ["Bluetooth"] = "Bluetooth",
        ["Camera"] = "Cameras",
        ["CDROM"] = "DVD/CD-ROM drives",
        ["Computer"] = "Computer",
        ["DiskDrive"] = "Disk drives",
        ["Display"] = "Display adapters",
        ["Firmware"] = "Firmware",
        ["FloppyDisk"] = "Floppy disk drives",
        ["HDC"] = "IDE ATA/ATAPI controllers",
        ["HIDClass"] = "Human Interface Devices",
        ["Image"] = "Imaging devices",
        ["Keyboard"] = "Keyboards",
        ["MEDIA"] = "Sound, video and game controllers",
        ["Modem"] = "Modems",
        ["Monitor"] = "Monitors",
        ["Mouse"] = "Mice and other pointing devices",
        ["Net"] = "Network adapters",
        ["PrintQueue"] = "Print queues",
        ["Printer"] = "Printers",
        ["Processor"] = "Processors",
        ["SCSIAdapter"] = "Storage controllers",
        ["SecurityDevices"] = "Security devices",
        ["SmartCardReader"] = "Smart card readers",
        ["SoftwareComponent"] = "Software components",
        ["SoftwareDevice"] = "Software devices",
        ["Sensor"] = "Sensors",
        ["System"] = "System devices",
        ["Ports"] = "Ports (COM & LPT)",
        ["USB"] = "Universal Serial Bus controllers",
        ["USBDevice"] = "Universal Serial Bus devices",
        ["Volume"] = "Storage volumes",
        ["WSDPrintDevice"] = "Print queues"
    };

    public static string Friendly(string? pnpClass)
    {
        if (string.IsNullOrWhiteSpace(pnpClass)) return Unclassified;
        return Names.TryGetValue(pnpClass, out var friendly) ? friendly : pnpClass;
    }
}
