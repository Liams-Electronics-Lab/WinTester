using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static WinTester.Tools.UsbValidator.NativeMethods;

namespace WinTester.Tools.UsbValidator
{
    /// <summary>Live status of one physical USB port, as reported by its parent hub.</summary>
    public sealed class UsbPortStatus
    {
        public required string PortKey { get; init; } // hubPath + "#" + port number, stable for the run
        public required string HubPath { get; init; }
        public required int PortNumber { get; init; }
        public bool IsConnected { get; init; }
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }

        /// <summary>
        /// False only when Windows positively reports this port as not user-connectable (no physical connector, or
        /// wired to an internal device). True when it says connectable AND whenever the question could not be
        /// answered (older Windows, hub driver without support, failed call) never hides a port on a guess.
        /// </summary>
        public bool IsUserConnectable { get; init; } = true;
    }

    /// <summary>
    /// One physical USB hub found on the system, with a handle kept open for the run so we can poll its
    /// ports without repeated CreateFile/CloseHandle calls.
    /// </summary>
    internal sealed class UsbHub : IDisposable
    {
        public required string DevicePath { get; init; }
        public required SafeFileHandle Handle { get; init; }
        public required int PortCount { get; init; }

        // Index = port number - 1. Filled once per Refresh (a port's connector properties don't change).
        public bool[] PortUserConnectable { get; init; } = Array.Empty<bool>();

        public void Dispose() => Handle.Dispose();
    }

    /// <summary>
    /// Enumerates every USB hub on the system via SetupAPI and queries real physical port status via the
    /// usbioctl.h IOCTLs the model described in USB_VALIDATOR_DESIGN.md §2/§4. This replaces Phase 1's
    /// WMI PnP-entity watching: because we talk to hub drivers directly by port number, composite devices
    /// (design doc §3) are no longer a source of duplicate tiles a port is either occupied or it isn't,
    /// regardless of how many logical PnP entities the device behind it spawns.
    /// </summary>
    public sealed class UsbPortEnumerator : IDisposable
    {
        private readonly List<UsbHub> hubs = new();

        /// <summary>Total physical port count across every hub found. Call after <see cref="Refresh"/>.</summary>
        public int TotalPortCount => hubs.Sum(h => h.PortCount);

        /// <summary>
        /// Re-discovers every USB hub on the system and how many ports it has. Does not need to be called
        /// again unless the hub topology itself changes (an external hub plugged/unplugged) ordinary
        /// device connect/disconnect on existing ports is picked up by <see cref="PollAllPorts"/> alone.
        /// </summary>
        public void Refresh()
        {
            foreach (var hub in hubs) hub.Dispose();
            hubs.Clear();

            // Per-stage failure counters, so an empty result can say WHY instead of just "no hubs found".
            int interfaces = 0, pathFailures = 0, openFailures = 0, countFailures = 0;
            int lastPathError = 0, lastOpenError = 0, lastCountError = 0, enumEndError = 0;

            var guid = GUID_DEVINTERFACE_USB_HUB;
            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == INVALID_HANDLE_VALUE)
            {
                throw new InvalidOperationException(
                    "SetupDiGetClassDevs failed (GetLastError=" + Marshal.GetLastWin32Error() + ")");
            }

            try
            {
                int index = 0;
                while (true)
                {
                    var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                    if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                    {
                        enumEndError = Marshal.GetLastWin32Error(); // 259 = ERROR_NO_MORE_ITEMS, the normal end
                        break;
                    }

                    interfaces++;
                    string? devicePath = GetDevicePath(deviceInfoSet, ref interfaceData, out int pathError);
                    index++;

                    if (devicePath == null)
                    {
                        pathFailures++;
                        lastPathError = pathError;
                        continue;
                    }

                    var handle = CreateFile(
                        devicePath, GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (handle.IsInvalid)
                    {
                        // couldn't open this hub — skip, don't abort the whole scan
                        openFailures++;
                        lastOpenError = Marshal.GetLastWin32Error();
                        continue;
                    }

                    int portCount = QueryPortCount(handle, out int countError);
                    if (portCount <= 0)
                    {
                        countFailures++;
                        lastCountError = countError;
                        handle.Dispose();
                        continue;
                    }

                    var userConnectable = new bool[portCount];
                    for (int port = 1; port <= portCount; port++)
                    {
                        userConnectable[port - 1] = QueryPortUserConnectable(handle, port);
                    }

                    hubs.Add(new UsbHub
                    {
                        DevicePath = devicePath,
                        Handle = handle,
                        PortCount = portCount,
                        PortUserConnectable = userConnectable
                    });
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            var summary = new System.Text.StringBuilder();
            summary.Append($"Hub interfaces found: {interfaces}. Usable hubs: {hubs.Count}.");
            if (pathFailures > 0) summary.Append($" Path lookup failed: {pathFailures} (Windows error {lastPathError}).");
            if (openFailures > 0) summary.Append($" Could not open: {openFailures} (Windows error {lastOpenError}; 5 = access denied).");
            if (countFailures > 0) summary.Append($" Port-count query failed: {countFailures} (Windows error {lastCountError}).");
            if (interfaces == 0 && enumEndError != 259) summary.Append($" Enumeration stopped with Windows error {enumEndError}.");
            LastScanSummary = summary.ToString();
        }

        /// <summary>
        /// One line describing what the last <see cref="Refresh"/> found and, if hubs were lost along the way, at
        /// which stage and with which Windows error code. Shown when no hubs were usable so the failure is diagnosable.
        /// </summary>
        public string LastScanSummary { get; private set; } = "";

        /// <summary>Queries live connect/disconnect + VID/PID for every port on every known hub.</summary>
        public IReadOnlyList<UsbPortStatus> PollAllPorts()
        {
            var results = new List<UsbPortStatus>();
            foreach (var hub in hubs)
            {
                for (int port = 1; port <= hub.PortCount; port++)
                {
                    results.Add(QueryPortStatus(hub, port));
                }
            }
            return results;
        }

        private static string? GetDevicePath(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData, out int errorCode)
        {
            errorCode = 0;

            // First call with NO buffer: it fails with ERROR_INSUFFICIENT_BUFFER (122) and reports the size needed.
            SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, IntPtr.Zero);
            if (requiredSize <= 0)
            {
                errorCode = Marshal.GetLastWin32Error();
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                Marshal.Copy(new byte[requiredSize], 0, buffer, requiredSize);

                // cbSize is the size of the FIXED part only (a DWORD plus one WCHAR, padded): 8 on 64-bit, 6 on
                // 32-bit — not the size of the buffer.
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

                if (!SetupDiGetDeviceInterfaceDetail(
                        deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, IntPtr.Zero))
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return null;
                }

                // DevicePath is a NUL-terminated UTF-16 string starting right after the DWORD cbSize.
                return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int QueryPortCount(SafeFileHandle hubHandle, out int errorCode)
        {
            errorCode = 0;
            int size = Marshal.SizeOf<USB_NODE_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                // The nested fixed-size byte array must be allocated (non-null) before StructureToPtr —
                // it marshals by copying managed array contents into the native buffer, so a null array
                // here throws at marshal time even though we only care about the input NodeType field.
                var request = new USB_NODE_INFORMATION
                {
                    NodeType = 0, // UsbHub
                    HubInformation = new USB_HUB_INFORMATION
                    {
                        HubDescriptor = new USB_HUB_DESCRIPTOR { bRemoveAndPowerMask = new byte[64] }
                    }
                };
                Marshal.StructureToPtr(request, buffer, false);

                bool ok = DeviceIoControl(
                    hubHandle, IOCTL_USB_GET_NODE_INFORMATION, buffer, (uint)size,
                    buffer, (uint)size, out _, IntPtr.Zero);

                if (!ok)
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return 0;
                }

                var result = Marshal.PtrToStructure<USB_NODE_INFORMATION>(buffer);
                return result.HubInformation.HubDescriptor.bNumberOfPorts;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Asks the hub whether the platform says this port is user-connectable. Fails OPEN: any failure,
        // a short reply or an unsupported IOCTL returns true, so the worst case is that an unwired port stays
        // visible (today's behaviour) — a port is only ever hidden on a positive "not user-connectable" answer.
        private static bool QueryPortUserConnectable(SafeFileHandle hubHandle, int portNumber)
        {
            const int bufferSize = 1024; // header + room for the variable-length companion hub name
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
                var request = new USB_PORT_CONNECTOR_PROPERTIES { ConnectionIndex = (uint)portNumber };
                Marshal.StructureToPtr(request, buffer, false);

                bool ok = DeviceIoControl(
                    hubHandle, IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES, buffer, (uint)bufferSize,
                    buffer, (uint)bufferSize, out uint bytesReturned, IntPtr.Zero);

                if (!ok || bytesReturned < Marshal.SizeOf<USB_PORT_CONNECTOR_PROPERTIES>()) return true;

                var result = Marshal.PtrToStructure<USB_PORT_CONNECTOR_PROPERTIES>(buffer);
                return (result.UsbPortProperties & PortIsUserConnectableMask) != 0;
            }
            catch
            {
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static UsbPortStatus QueryPortStatus(UsbHub hub, int portNumber)
        {
            string portKey = $"{hub.DevicePath}#{portNumber}";
            bool userConnectable = portNumber - 1 < hub.PortUserConnectable.Length
                ? hub.PortUserConnectable[portNumber - 1]
                : true;

            // Over-allocate beyond the fixed header for the driver's variable-length PipeList[] tail —
            // we never read that part, but the IOCTL can fail if the buffer is too small to hold it.
            int headerSize = Marshal.SizeOf<USB_NODE_CONNECTION_INFORMATION_EX>();
            int bufferSize = headerSize + 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var request = new USB_NODE_CONNECTION_INFORMATION_EX { ConnectionIndex = (uint)portNumber };
                Marshal.StructureToPtr(request, buffer, false);

                bool ok = DeviceIoControl(
                    hub.Handle, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, buffer, (uint)bufferSize,
                    buffer, (uint)bufferSize, out _, IntPtr.Zero);

                if (!ok)
                {
                    return new UsbPortStatus
                    {
                        PortKey = portKey,
                        HubPath = hub.DevicePath,
                        PortNumber = portNumber,
                        IsConnected = false,
                        IsUserConnectable = userConnectable
                    };
                }

                var result = Marshal.PtrToStructure<USB_NODE_CONNECTION_INFORMATION_EX>(buffer);
                bool connected = result.ConnectionStatus == (int)USB_CONNECTION_STATUS.DeviceConnected;

                return new UsbPortStatus
                {
                    PortKey = portKey,
                    HubPath = hub.DevicePath,
                    PortNumber = portNumber,
                    IsConnected = connected,
                    VendorId = connected ? result.DeviceDescriptor.idVendor : (ushort)0,
                    ProductId = connected ? result.DeviceDescriptor.idProduct : (ushort)0,
                    IsUserConnectable = userConnectable
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            foreach (var hub in hubs) hub.Dispose();
            hubs.Clear();
        }
    }
}
