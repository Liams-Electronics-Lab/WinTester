using System.Management;
using System.Runtime.Versioning;

namespace WinTester.Tools.DiskInfo;

// Reads MSFT_StorageReliabilityCounter through its association with MSFT_PhysicalDisk. Windows fills only the fields the
// driver provides, so every value can be missing. Returns null when the class is not available at all.
[SupportedOSPlatform("windows")]
public static class ReliabilityCounterReader
{
    public static WindowsReliability? Read(int diskIndex)
    {
        try
        {
            WindowsReliability? result = null;
            WmiQuery.Each(WmiQuery.StorageScope, $"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskIndex}'", disk =>
            {
                using var related = disk.GetRelated("MSFT_StorageReliabilityCounter", "MSFT_PhysicalDiskToStorageReliabilityCounter",
                    null, null, null, null, false, null);
                foreach (var item in related)
                {
                    using var counter = (ManagementObject)item;
                    result = new WindowsReliability(
                        TemperatureCelsius: WmiQuery.Int(counter, "Temperature"),
                        WearPercent: WmiQuery.Int(counter, "Wear"),
                        PowerOnHours: WmiQuery.U64(counter, "PowerOnHours"),
                        ReadErrorsUncorrected: WmiQuery.U64(counter, "ReadErrorsUncorrected"),
                        WriteErrorsUncorrected: WmiQuery.U64(counter, "WriteErrorsUncorrected"),
                        LoadUnloadCycles: WmiQuery.U64(counter, "LoadUnloadCycleCount"));
                    break;
                }
            });

            if (result is null) return null;
            var any = result.TemperatureCelsius is not null || result.WearPercent is not null || result.PowerOnHours is not null ||
                      result.ReadErrorsUncorrected is not null || result.WriteErrorsUncorrected is not null ||
                      result.LoadUnloadCycles is not null;
            return any ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
