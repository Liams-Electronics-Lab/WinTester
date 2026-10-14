using System.Collections.Generic;
using AForge.Video.DirectShow;

namespace WinTester.Tools.WebcamMic;

public static class CameraDeviceEnumerator
{
    public static IReadOnlyList<CameraDeviceInfo> List()
    {
        var result = new List<CameraDeviceInfo>();
        var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
        foreach (FilterInfo device in devices)
            result.Add(new CameraDeviceInfo(device.Name, device.MonikerString));
        return result;
    }
}
