using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinTester.Tests.Power;

// SetupAPI + IOCTL_BATTERY_QUERY_INFORMATION fallback for BatteryHealthReader; the same battery-class-driver path powercfg /batteryreport uses, for machines where root\WMI's battery classes return no instance. Enumerates GUID_DEVICE_BATTERY device interfaces and reads real design/full-charge capacity via the IOCTL.
[SupportedOSPlatform("windows")]
internal static class Win32BatteryInterop
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;

    // Device-interface class every system battery (ACPI or otherwise) exposes.
    private static readonly Guid GuidDeviceBattery = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;

    // BATTERY_QUERY_INFORMATION_LEVEL values, confirmed against Microsoft's sample and IOCTL docs. Must be
    // 0 ("BatteryInformation"), not 2 ("BatteryTemperature"): the latter marshals a temperature ULONG into
    // a BATTERY_INFORMATION struct, producing garbage fields instead of failing outright.
    private const uint BATTERY_INFORMATION_LEVEL = 0;       // BatteryInformation      -> BATTERY_INFORMATION struct
    private const uint BATTERY_DEVICE_NAME_LEVEL = 4;       // BatteryDeviceName       -> null-terminated Unicode string
    private const uint BATTERY_MANUFACTURE_NAME_LEVEL = 6;  // BatteryManufactureName  -> null-terminated Unicode string
    private const uint BATTERY_SERIAL_NUMBER_LEVEL = 8;     // BatterySerialNumber     -> null-terminated Unicode string

    private const uint BATTERY_SYSTEM_BATTERY = 0x00000004; // BATTERY_INFORMATION.Capabilities flag

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public uint InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved1;
        public byte Reserved2;
        public byte Reserved3;
        public uint Chemistry1;
        public uint Chemistry2;
        public uint Chemistry3;
        public uint Chemistry4;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode, IntPtr inBuffer,
        uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // Enumerates GUID_DEVICE_BATTERY interfaces and returns the first with usable battery data; returns null when no battery is present, throws only on a real SetupAPI error.
    public static BatteryHealthResult? TryReadBatteryInformation()
    {
        var guid = GuidDeviceBattery;
        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            throw new InvalidOperationException($"SetupDiGetClassDevs failed (Win32 error {Marshal.GetLastWin32Error()}).");

        try
        {
            uint index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                    break; // ERROR_NO_MORE_ITEMS (259); normal end-of-list, not an error.

                index++;

                var devicePath = GetDevicePath(deviceInfoSet, ref interfaceData);
                if (devicePath is null) continue;

                var result = TryReadOneBattery(devicePath);
                if (result is not null) return result;
            }

            return null; // zero interfaces, or none yielded usable data: genuinely no battery present.
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        int requiredSize = 0;
        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);
        if (requiredSize == 0) return null;

        IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // Leading cbSize is a fixed platform constant (6 on x86, 8 on x64); standard workaround for marshalling this struct's trailing variable-length path field.
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, ref requiredSize, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringAuto(IntPtr.Add(detailBuffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static BatteryHealthResult? TryReadOneBattery(string devicePath)
    {
        IntPtr handle = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;

        try
        {
            uint tag = QueryBatteryTag(handle);
            if (tag == 0) return null; // no battery currently present on this interface (e.g. removable pack).

            return QueryBatteryInformation(handle, tag);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static uint QueryBatteryTag(IntPtr handle)
    {
        IntPtr inBuffer = Marshal.AllocHGlobal(sizeof(uint));
        IntPtr outBuffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(inBuffer, 0);
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, inBuffer, sizeof(uint), outBuffer, sizeof(uint), out _, IntPtr.Zero))
                return 0;

            return unchecked((uint)Marshal.ReadInt32(outBuffer));
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    private static BatteryHealthResult? QueryBatteryInformation(IntPtr handle, uint tag)
    {
        var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BATTERY_INFORMATION_LEVEL, AtRate = 0 };
        int querySize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
        int infoSize = Marshal.SizeOf<BATTERY_INFORMATION>();

        IntPtr queryBuffer = Marshal.AllocHGlobal(querySize);
        IntPtr infoBuffer = Marshal.AllocHGlobal(infoSize);
        try
        {
            Marshal.StructureToPtr(query, queryBuffer, false);
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, queryBuffer, (uint)querySize, infoBuffer, (uint)infoSize, out _, IntPtr.Zero))
                return null;

            var info = Marshal.PtrToStructure<BATTERY_INFORMATION>(infoBuffer);

            // Skip battery-class hardware not flagged as an actual system battery (e.g. UPS/peripheral devices exposing this same interface).
            if ((info.Capabilities & BATTERY_SYSTEM_BATTERY) == 0) return null;

            if (info.DesignedCapacity == 0 || info.DesignedCapacity == 0xFFFFFFFF) return null;

            double designWh = info.DesignedCapacity / 1000.0;

            // CycleCount 0 is BATTERY_INFORMATION's own unknown/unreported convention on most drivers (matches powercfg's "-"); never surfaced as a literal "0 cycles", which would misleadingly read as a real measurement.
            int? cycleCount = info.CycleCount is > 0 and < 0xFFFFFFFF ? (int)info.CycleCount : null;
            string? chemistry = DecodeChemistry(info.Chemistry1);
            string? deviceName = ReadStringInfo(handle, tag, BATTERY_DEVICE_NAME_LEVEL);
            string? manufacturerName = ReadStringInfo(handle, tag, BATTERY_MANUFACTURE_NAME_LEVEL);
            string? serialNumber = ReadStringInfo(handle, tag, BATTERY_SERIAL_NUMBER_LEVEL);

            if (info.FullChargedCapacity == 0 || info.FullChargedCapacity == 0xFFFFFFFF)
            {
                return new BatteryHealthResult(true, Math.Round(designWh, 1), null, null,
                    "Design capacity found via SetupAPI/IOCTL_BATTERY_QUERY_INFORMATION, but no usable " +
                    "full-charge-capacity reading was available: health % cannot be computed.",
                    cycleCount, serialNumber, chemistry, manufacturerName, deviceName);
            }

            double fullWh = info.FullChargedCapacity / 1000.0;
            double healthPercent = designWh > 0 ? (fullWh / designWh) * 100.0 : 0;

            return new BatteryHealthResult(
                true,
                Math.Round(designWh, 1),
                Math.Round(fullWh, 1),
                Math.Round(healthPercent, 1),
                $"Design capacity {designWh:0.0}Wh, current full-charge capacity {fullWh:0.0}Wh " +
                "(via SetupAPI/IOCTL_BATTERY_QUERY_INFORMATION: the same battery-class-driver path " +
                "powercfg /batteryreport uses).",
                cycleCount, serialNumber, chemistry, manufacturerName, deviceName);
        }
        finally
        {
            Marshal.FreeHGlobal(queryBuffer);
            Marshal.FreeHGlobal(infoBuffer);
        }
    }

    // Decodes Chemistry1; a 4-byte ASCII FourCC packed into a ULONG (e.g. "LION", "NiMH"), the same raw value powercfg's abbreviated "CHEMISTRY" row comes from; returns null rather than garbage when the bytes aren't printable ASCII.
    // Internal, not private: WMI's BatteryStaticData.Chemistry is the same FourCC encoding, so BatteryHealthReader's WMI path reuses this decode instead of duplicating it.
    internal static string? DecodeChemistry(uint chemistry1)
    {
        if (chemistry1 == 0) return null;
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, chemistry1);
        var chars = new char[4];
        int len = 0;
        foreach (var b in bytes)
        {
            if (b == 0) break;
            if (b < 0x20 || b > 0x7E) return null; // not printable ASCII, treat as unreported
            chars[len++] = (char)b;
        }
        return len > 0 ? new string(chars, 0, len) : null;
    }

    // Reads a string-valued IOCTL level (device name, manufacturer, serial; see constants above); best-effort, returns null on failure or empty result rather than throwing, since these are "nice to have" extras that shouldn't block the numeric capacity/cycle-count reading when a driver doesn't support one.
    private static string? ReadStringInfo(IntPtr handle, uint tag, uint level)
    {
        const int bufferChars = 256; // generous; real device/manufacturer/serial strings are short
        var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level, AtRate = 0 };
        int querySize = Marshal.SizeOf<BATTERY_QUERY_INFORMATION>();
        int outSize = bufferChars * 2; // Unicode

        IntPtr queryBuffer = Marshal.AllocHGlobal(querySize);
        IntPtr outBuffer = Marshal.AllocHGlobal(outSize);
        try
        {
            Marshal.StructureToPtr(query, queryBuffer, false);
            // Zero the buffer first; a driver returning fewer bytes than requested without a proper terminator would otherwise leave PtrToStringUni reading garbage.
            for (int i = 0; i < outSize; i += 8) Marshal.WriteInt64(outBuffer, i, 0);

            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, queryBuffer, (uint)querySize, outBuffer, (uint)outSize, out _, IntPtr.Zero))
                return null;

            var s = Marshal.PtrToStringUni(outBuffer)?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(queryBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }
}
