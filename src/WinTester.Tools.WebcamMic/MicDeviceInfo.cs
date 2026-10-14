namespace WinTester.Tools.WebcamMic;

// One entry from MicDeviceEnumerator.List(): NAudio's waveIn device index plus its friendly name. Unlike
// AForge's camera monikers, NAudio has no stable per-device id for waveIn devices — the index is captured
// at enumeration time and used as-is for the lifetime of the dropdown selection.
public sealed record MicDeviceInfo(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}
