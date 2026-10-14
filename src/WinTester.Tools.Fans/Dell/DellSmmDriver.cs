using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinTester.Tools.Fans.Dell;

// Commands understood by Dell's SMBIOS/SMM fan interface. Only the fan-relevant subset is exposed; the
// rest of Dell's SMI surface (charger, docking, hotkeys) is intentionally left out to minimize reachable
// SMM surface.
internal enum DellSmbiosCommand : uint
{
    GetFanLevel = 0x00A3,
    SetFanLevel = 0x01A3,
    GetFanRpm = 0x02A3,
    DisableEcFanControl = 0x30A3,
    EnableEcFanControl = 0x31A3,
    DisableEcFanControlAlternate = 0x34A3,

    // 0x35A3, not the 0x31A3 some references give (which duplicates EnableEcFanControl and isn't a real
    // alternate). 0x35A3 pairs with DisableEcFanControlAlternate (0x34A3, CTL2) the way
    // EnableEcFanControl (0x31A3, CTL1) pairs with DisableEcFanControl (0x30A3, CTL1).
    EnableEcFanControlAlternate = 0x35A3,
    CheckSmbiosInterface = 0xFFA3
}

// Dell's fan levels are discrete, not proportional: off / medium / high. This is why the Dell backend
// implements IFanTierControl — a 0-100% slider would imply a resolution the firmware doesn't have.
internal enum DellFanLevel : uint
{
    Off = 0,
    Medium = 1,
    High = 2
}

// The 16-byte request/response block the driver passes through to SMM. On return, Command carries the
// result value (uint.MaxValue meaning failure), which is why it's read back rather than treated as
// write-only.
[StructLayout(LayoutKind.Sequential)]
internal struct DellSmbiosPackage
{
    public uint Command;
    public uint Data;
    public uint Output1;
    public uint Output2;
}

// Loads, talks to, and unloads the bzh_dell_smm_io_x64.sys kernel driver.
//
// This installs a kernel-mode service on demand, opens a handle to it, and uses it to issue Dell SMI
// calls — the highest-privilege thing WinTester does. Notes:
//
//  * Requires administrator rights. Without them CreateService fails and TryActivate reports false,
//    the correct degradation (fall through to the generic LHM backend), not an error to raise.
//  * The driver is third-party and not WHQL-signed. On a machine with driver signature enforcement in
//    its default state, StartService will fail; left as a probe failure rather than something this code
//    works around, since altering a machine-wide security posture isn't a diagnostic tool's call to make.
//  * The service is installed DemandStart and removed again on shutdown. A service someone has since set
//    to auto-start is left alone rather than deleted, so WinTester never uninstalls a driver another
//    tool depends on.
[SupportedOSPlatform("windows")]
internal sealed class DellSmmDriver : IDisposable
{
    private const string DriverServiceName = "BZHDELLSMMIO";
    private const string DriverDevicePath = @"\\.\" + DriverServiceName;
    internal const string DriverFileName = "bzh_dell_smm_io_x64.sys";

    // The driver's own magic ioctl code; it rejects anything else.
    private const int IoctlCode = (0xB424 << 16) | (0xB42 << 2);

    private static readonly IntPtr InvalidHandle = new(-1);

    private IntPtr _driverHandle = InvalidHandle;
    private bool _installedByUs;
    private bool _disposed;

    public bool IsInitialized { get; private set; }

    public bool Initialize()
    {
        try
        {
            // Reuse the driver if it's already loaded by another tool or a previous run, and remember
            // we didn't install it, so shutdown leaves it alone.
            _driverHandle = Native.CreateFile(DriverDevicePath, FileAccess.ReadWrite, FileShare.None,
                IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);

            if (_driverHandle != InvalidHandle)
            {
                IsInitialized = true;
                return true;
            }

            string driverPath = Path.Combine(AppContext.BaseDirectory, DriverFileName);
            if (!File.Exists(driverPath)) return false;

            if (!InstallDriver(driverPath)) return false;
            _installedByUs = true;

            if (!StartDriver()) return false;

            _driverHandle = Native.CreateFile(DriverDevicePath, FileAccess.ReadWrite, FileShare.ReadWrite,
                IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);

            if (_driverHandle == InvalidHandle) return false;

            IsInitialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Returns null on failure. uint.MaxValue coming back in Command is the driver's own "SMI refused
    // this" signal and is translated to null here so callers have one failure shape, not two.
    public uint? ExecuteCommand(DellSmbiosCommand command, uint argument = 0)
    {
        if (!IsInitialized || _disposed) return null;

        try
        {
            var package = new DellSmbiosPackage
            {
                Command = (uint)command,
                Data = argument,
                Output1 = 0,
                Output2 = 0
            };

            uint resultSize = 0;
            int size = Marshal.SizeOf(package);

            bool ok = Native.DeviceIoControl(_driverHandle, IoctlCode, ref package, size,
                ref package, size, ref resultSize, IntPtr.Zero);

            if (!ok) return null;
            return package.Command != uint.MaxValue ? package.Command : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool InstallDriver(string driverPath)
    {
        IntPtr manager = Native.OpenSCManager(null, null, Native.ScManagerAllAccess);
        if (manager == IntPtr.Zero) return false;

        try
        {
            IntPtr service = Native.CreateService(manager, DriverServiceName, DriverServiceName,
                Native.ServiceAllAccess, Native.ServiceKernelDriver, Native.ServiceDemandStart,
                Native.ServiceErrorNormal, driverPath, null, IntPtr.Zero, null, null, null);

            if (service == IntPtr.Zero)
            {
                // Already-exists counts as success: the service is there to be started.
                return Marshal.GetLastWin32Error() == Native.ErrorServiceExists;
            }

            Native.CloseServiceHandle(service);
            return true;
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }
    }

    private static bool StartDriver()
    {
        IntPtr manager = Native.OpenSCManager(null, null, Native.ScManagerAllAccess);
        if (manager == IntPtr.Zero) return false;

        try
        {
            IntPtr service = Native.OpenService(manager, DriverServiceName, Native.ServiceAllAccess);
            if (service == IntPtr.Zero) return false;

            try
            {
                return Native.StartService(service, 0, null) ||
                       Marshal.GetLastWin32Error() == Native.ErrorServiceAlreadyRunning;
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }
    }

    private static void StopAndDeleteDriver()
    {
        IntPtr manager = Native.OpenSCManager(null, null, Native.ScManagerAllAccess);
        if (manager == IntPtr.Zero) return;

        try
        {
            IntPtr service = Native.OpenService(manager, DriverServiceName, Native.ServiceAllAccess);
            if (service == IntPtr.Zero) return;

            try
            {
                var status = new Native.ServiceStatus();
                Native.ControlService(service, Native.ServiceControlStop, ref status);
                Native.DeleteService(service);
            }
            finally
            {
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsInitialized = false;

        try
        {
            if (_driverHandle != InvalidHandle)
            {
                Native.CloseHandle(_driverHandle);
                _driverHandle = InvalidHandle;
            }

            // Only tear down what we put up; a driver already loaded by another tool is left alone.
            if (_installedByUs) StopAndDeleteDriver();
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        internal const uint ScManagerAllAccess = 0xF003F;
        internal const uint ServiceAllAccess = 0xF01FF;
        internal const uint ServiceKernelDriver = 0x00000001;
        internal const uint ServiceDemandStart = 0x00000003;
        internal const uint ServiceErrorNormal = 0x00000001;
        internal const uint ServiceControlStop = 0x00000001;
        internal const int ErrorServiceExists = 1073;
        internal const int ErrorServiceAlreadyRunning = 1056;

        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(
            [MarshalAs(UnmanagedType.LPTStr)] string filename,
            [MarshalAs(UnmanagedType.U4)] FileAccess desiredAccess,
            [MarshalAs(UnmanagedType.U4)] FileShare share,
            IntPtr securityAttributes,
            [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
            [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(
            IntPtr device,
            int ioControlCode,
            ref DellSmbiosPackage inBuffer,
            int inBufferSize,
            ref DellSmbiosPackage outBuffer,
            int outBufferSize,
            ref uint bytesReturned,
            IntPtr overlapped);

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateService(
            IntPtr scManager, string serviceName, string displayName, uint desiredAccess,
            uint serviceType, uint startType, uint errorControl, string binaryPathName,
            string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool StartService(IntPtr service, int numArgs, string[]? args);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
    }
}
