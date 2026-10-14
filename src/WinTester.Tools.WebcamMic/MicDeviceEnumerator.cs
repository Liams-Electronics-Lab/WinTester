using System.Collections.Generic;
using NAudio.Wave;

namespace WinTester.Tools.WebcamMic;

public static class MicDeviceEnumerator
{
    public static IReadOnlyList<MicDeviceInfo> List()
    {
        var result = new List<MicDeviceInfo>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            result.Add(new MicDeviceInfo(i, caps.ProductName));
        }
        return result;
    }
}
