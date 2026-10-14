using System;
using System.Windows.Threading;
using NAudio.Wave;

namespace WinTester.Tools.WebcamMic;

// Wraps one NAudio WaveInEvent for the selected mic. Always raises SamplesAvailable for every buffer —
// the live-waveform display and (while a take is in progress) AudioRecordingBuffer both consume the same
// stream; this class knows nothing about recording state, that decision lives in the window. Samples are
// always re-marshalled onto _dispatcher before the event fires, regardless of which thread NAudio itself
// calls back on, so consumers (waveform drawing, buffer writes) never have to think about it.
public sealed class AudioCaptureSession : IDisposable
{
    public const int SampleRate = 16000;
    private const int Channels = 1;

    private readonly Dispatcher _dispatcher;
    private WaveInEvent? _waveIn;

    public static WaveFormat Format { get; } = new(SampleRate, 16, Channels);

    public event Action<short[]>? SamplesAvailable;

    public AudioCaptureSession(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start(int deviceNumber)
    {
        Stop();

        var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = Format,
            BufferMilliseconds = 50
        };
        waveIn.DataAvailable += WaveIn_DataAvailable;
        _waveIn = waveIn;
        waveIn.StartRecording();
    }

    public void Stop()
    {
        if (_waveIn is null) return;

        var waveIn = _waveIn;
        _waveIn = null;
        waveIn.DataAvailable -= WaveIn_DataAvailable;
        waveIn.StopRecording();
        waveIn.Dispose();
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        var sampleCount = e.BytesRecorded / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, sampleCount * 2);

        _dispatcher.BeginInvoke(() => SamplesAvailable?.Invoke(samples));
    }

    public void Dispose() => Stop();
}
