using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinTester.Tools.DiskInfo;

public sealed record NvmeReadResult(NvmeHealth? Health, string? ErrorMessage);

// Reads the NVMe SMART / Health Information log (log page 02h) with IOCTL_STORAGE_QUERY_PROPERTY and
// StorageDeviceProtocolSpecificProperty against \\.\PhysicalDriveN. The request and the reply are plain byte arrays,
// built and unpacked by the two static helpers below so they can be checked without a drive.
//
// Request layout (STORAGE_PROPERTY_QUERY header, then STORAGE_PROTOCOL_SPECIFIC_DATA, then room for the log):
//   0   PropertyId          = StorageDeviceProtocolSpecificProperty (50)
//   4   QueryType           = PropertyStandardQuery (0)
//   8   ProtocolType        = ProtocolTypeNvme (3)
//   12  DataType            = NVMeDataTypeLogPage (2)
//   16  ProtocolDataRequestValue    = health information log (2)
//   20  ProtocolDataRequestSubValue = 0 (or 0xFFFFFFFF, the global namespace, on a retry)
//   24  ProtocolDataOffset  = 40 (size of the protocol specific structure)
//   28  ProtocolDataLength  = 512
//   32  FixedProtocolReturnData and three further sub values = 0
//   48  512 bytes for the log
// The reply starts with an 8 byte descriptor header, then the same protocol specific structure, and the log sits at
// ProtocolDataOffset from the start of that structure.
//
// Whether the call succeeds on every NVMe driver (the Microsoft stornvme driver and vendor drivers differ) is not yet
// verified on real hardware. A failure returns an error message and the caller falls back to the Windows counters.
[SupportedOSPlatform("windows")]
public static class NvmeSmartReader
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeLogPage = 2;
    private const int NvmeLogPageHealthInfo = 2;
    private const int QueryHeaderLength = 8;
    private const int ProtocolSpecificLength = 40;
    private const int DescriptorHeaderLength = 8;

    public const int BufferLength = QueryHeaderLength + ProtocolSpecificLength + NvmeHealthParser.LogLength;
    public const uint GlobalNamespace = 0xFFFFFFFF;

    public static byte[] BuildHealthLogRequest(uint subValue = 0)
    {
        var buffer = new byte[BufferLength];
        Put(buffer, 0, StorageDeviceProtocolSpecificProperty);
        Put(buffer, 4, 0);
        Put(buffer, 8, ProtocolTypeNvme);
        Put(buffer, 12, NvmeDataTypeLogPage);
        Put(buffer, 16, NvmeLogPageHealthInfo);
        Put(buffer, 20, unchecked((int)subValue));
        Put(buffer, 24, ProtocolSpecificLength);
        Put(buffer, 28, NvmeHealthParser.LogLength);
        return buffer;
    }

    // Returns the 512 byte log from a reply, or null when the reply is too short or does not describe an NVMe log.
    public static byte[]? ExtractHealthLog(byte[] reply, int bytesReturned)
    {
        var length = Math.Min(bytesReturned, reply.Length);
        if (length < DescriptorHeaderLength + ProtocolSpecificLength) return null;

        var protocolType = BitConverter.ToInt32(reply, DescriptorHeaderLength);
        var dataOffset = BitConverter.ToInt32(reply, DescriptorHeaderLength + 16);
        var dataLength = BitConverter.ToInt32(reply, DescriptorHeaderLength + 20);
        if (protocolType != ProtocolTypeNvme || dataOffset < 0 || dataLength < NvmeHealthParser.LogLength) return null;

        var start = DescriptorHeaderLength + dataOffset;
        if (start < 0 || start + NvmeHealthParser.LogLength > length) return null;

        var log = new byte[NvmeHealthParser.LogLength];
        Array.Copy(reply, start, log, 0, log.Length);
        return log;
    }

    private static void Put(byte[] buffer, int offset, int value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);

    public static NvmeReadResult Read(int diskIndex)
    {
        var path = $@"\\.\PhysicalDrive{diskIndex}";
        string? lastError = null;

        // The query IOCTL needs no access rights, so a handle with none is tried first and nothing is opened for
        // writing unless a driver refuses that. Some vendor drivers only answer on a read/write handle, so that is
        // the second try. Either way nothing is ever written to the disk.
        foreach (var access in new uint[] { 0, GenericRead | GenericWrite })
        {
            using var handle = CreateFileW(path, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                lastError = $"The disk could not be opened (error {Marshal.GetLastWin32Error()}).";
                continue;
            }

            foreach (var subValue in new uint[] { 0, GlobalNamespace })
            {
                var buffer = BuildHealthLogRequest(subValue);
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero))
                {
                    lastError = $"The NVMe health query was refused (error {Marshal.GetLastWin32Error()}).";
                    continue;
                }

                var log = ExtractHealthLog(buffer, returned);
                var health = NvmeHealthParser.Parse(log);
                if (health is not null) return new NvmeReadResult(health, null);
                lastError = "The drive returned an unexpected NVMe health reply.";
            }
        }

        return new NvmeReadResult(null, lastError);
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
