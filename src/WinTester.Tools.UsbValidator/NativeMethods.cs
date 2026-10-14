using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinTester.Tools.UsbValidator
{
    /// <summary>
    /// Raw Win32/SetupAPI/usbioctl.h P/Invoke surface used by <see cref="UsbPortEnumerator"/>.
    /// IOCTL numeric values and struct layouts below are taken from usbioctl.h / usb100.h as documented
    /// in the Windows Driver Kit and Microsoft's own "usbview" sample (Windows-driver-samples repo), not
    /// reverse engineered. Ported unchanged from the standalone USB Validator prototype (Phase 2) into
    /// WinTester proper — still not build/hardware verified. See USB_VALIDATOR_PROGRESS_TRACKER.md.
    /// </summary>
    internal static class NativeMethods
    {
        // ---- SetupAPI: enumerating USB hub device interfaces ----

        public static readonly Guid GUID_DEVINTERFACE_USB_HUB =
            new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        // Missing from the session 94 pass (build error, found session 95): UsbPortEnumerator.Refresh calls both
        // of these to walk the USB hub device-interface set, but only the detail/destroy calls below had
        // declarations here. Signatures are the standard SetupAPI ones, same calling convention/CharSet as the
        // rest of this block.
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        // SP_DEVICE_INTERFACE_DETAIL_DATA_W is variable-length (a DWORD cbSize, then the path inline), so it is
        // NOT modelled as a managed struct: the caller allocates a raw buffer, writes cbSize at offset 0 and reads the
        // UTF-16 path from offset 4. Session 94: the earlier struct-based overload could not be called with a null
        // buffer for the size query — passing a real struct with cbSize 0 makes SetupAPI fail with
        // ERROR_INVALID_USER_BUFFER *without* reporting the required size, so every hub path came back null and the
        // window reported "no USB hubs found".
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true,
            CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, int detailDataSize,
            out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        // ---- Hub handle + IOCTLs ----

        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

        // usbioctl.h — CTL_CODE(FILE_DEVICE_USB=0x22, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
        public const uint IOCTL_USB_GET_NODE_INFORMATION = 0x00220408;
        public const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x00220448;

        // IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES = CTL_CODE(FILE_DEVICE_USB, 278, METHOD_BUFFERED, FILE_ANY_ACCESS),
        // Windows 8 and later. Reports, per hub port, whether the platform (ACPI _UPC) says a user can plug into it.
        // The ordinal (278), the struct below and the bit position are recalled from usbioctl.h and were NOT re-read
        // or hardware-verified (session 94). UsbPortEnumerator treats any failure of this call as "connectable", so a
        // wrong value here can only cause ports to stay visible, never to be hidden.
        public const uint IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES = 0x00220458;

        // Fixed header only (the native struct continues with a variable-length companion-hub symbolic link name,
        // which is never read). UsbPortProperties is a bit field: bit 0 = PortIsUserConnectable, bit 1 =
        // PortIsDebugCapable, bit 2 = PortHasMultipleCompanions, bits 3-4 = PortConnectorType.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_PORT_CONNECTOR_PROPERTIES
        {
            public uint ConnectionIndex;
            public uint ActualLength;
            public uint UsbPortProperties;
            public ushort CompanionIndex;
            public ushort CompanionPortNumber;
        }

        public const uint PortIsUserConnectableMask = 0x1;

        // Every struct in this section (USB_HUB_DESCRIPTOR .. USB_NODE_CONNECTION_INFORMATION_EX) is declared
        // under #include <pshpack1.h> in usb100.h / usbioctl.h, i.e. byte-packed with NO alignment padding.
        // Pack = 1 is required to match: with the default natural alignment the managed
        // USB_NODE_CONNECTION_INFORMATION_EX puts ConnectionStatus at offset 32 (native: 31) and is 36 bytes
        // (native: 35), so every read of ConnectionStatus would pick up the wrong bytes and a plugged-in
        // device would never read as DeviceConnected. (Session 94 fix — the ported prototype lacked this.)
        // ---- USB_NODE_INFORMATION (port count) ----
        // usb100.h USB_HUB_DESCRIPTOR declares bRemoveAndPowerMask as a fixed 64-byte array in every
        // published C# port of this struct (the real descriptor is variable-length up to 32 ports'
        // worth of bitmask, but 64 bytes is generous headroom and matches usbview's own struct).
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_HUB_DESCRIPTOR
        {
            public byte bDescriptorLength;
            public byte bDescriptorType;
            public byte bNumberOfPorts;
            public ushort wHubCharacteristics;
            public byte bPowerOnToPowerGood;
            public byte bHubControlCurrent;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] bRemoveAndPowerMask;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_HUB_INFORMATION
        {
            public USB_HUB_DESCRIPTOR HubDescriptor;
            public byte HubIsBusPowered;
        }

        // NodeType (int) + USB_HUB_INFORMATION union member. We never request MiParent info
        // (NodeType stays UsbHub == 0 for our query), so the union is modelled as just the hub branch.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_NODE_INFORMATION
        {
            public int NodeType;
            public USB_HUB_INFORMATION HubInformation;
        }

        // ---- USB_NODE_CONNECTION_INFORMATION_EX (per-port live status) ----
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_DEVICE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public ushort bcdUSB;
            public byte bDeviceClass;
            public byte bDeviceSubClass;
            public byte bDeviceProtocol;
            public byte bMaxPacketSize0;
            public ushort idVendor;
            public ushort idProduct;
            public ushort bcdDevice;
            public byte iManufacturer;
            public byte iProduct;
            public byte iSerialNumber;
            public byte bNumConfigurations;
        }

        public enum USB_CONNECTION_STATUS
        {
            NoDeviceConnected = 0,
            DeviceConnected = 1,
            DeviceFailedEnumeration = 2,
            DeviceGeneralFailure = 3,
            DeviceCausedOvercurrent = 4,
            DeviceNotEnoughPower = 5,
            DeviceNotEnoughBandwidth = 6,
            DeviceHubNestedTooDeeply = 7,
            DeviceInLegacyHub = 8,
            DeviceEnumerating = 9,
            DeviceReset = 10
        }

        // Fixed-size header only (ConnectionIndex .. ConnectionStatus). The real struct is followed by a
        // variable-length PipeList[] array; we over-allocate the native buffer (see UsbPortEnumerator) so
        // the driver has room to write it even though we only read the fixed header back out.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct USB_NODE_CONNECTION_INFORMATION_EX
        {
            public uint ConnectionIndex;
            public USB_DEVICE_DESCRIPTOR DeviceDescriptor;
            public byte CurrentConfigurationValue;
            public byte Speed;
            public byte DeviceIsHub;
            public ushort DeviceAddress;
            public uint NumberOfOpenPipes;
            public int ConnectionStatus;
        }
    }
}
