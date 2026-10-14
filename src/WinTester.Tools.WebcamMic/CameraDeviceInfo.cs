namespace WinTester.Tools.WebcamMic;

// One entry from CameraDeviceEnumerator.List(): a DirectShow video input device's display name plus the
// moniker string VideoCaptureDevice needs to actually open it. The moniker, not the name or list index, is
// what's passed to CameraCaptureSession.Start — two cameras can share a display name, and the moniker is
// the only value AForge guarantees is unique per device.
public sealed record CameraDeviceInfo(string Name, string MonikerString)
{
    public override string ToString() => Name;
}
