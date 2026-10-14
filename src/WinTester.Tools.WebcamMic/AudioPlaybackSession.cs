using System;
using System.Windows.Threading;
using NAudio.Wave;

namespace WinTester.Tools.WebcamMic;

// Minimal IWaveProvider over an in-memory short[] buffer, so WaveOutEvent can read straight out of a
// recorded take's array — the clip never touches disk for playback either.
internal sealed class InMemoryPcmProvider : IWaveProvider
{
    private readonly short[] _samples;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public InMemoryPcmProvider(short[] samples, WaveFormat waveFormat)
    {
        _samples = samples;
        WaveFormat = waveFormat;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var samplesRequested = count / 2;
        var samplesRemaining = _samples.Length - _position;
        var samplesToCopy = Math.Min(samplesRequested, samplesRemaining);
        if (samplesToCopy <= 0) return 0;

        Buffer.BlockCopy(_samples, _position * 2, buffer, offset, samplesToCopy * 2);
        _position += samplesToCopy;
        return samplesToCopy * 2;
    }
}

// Plays back one recorded take end-to-end. GetPlaybackSampleIndex() is polled by the window (on the same
// timer that drives the live waveform) to scroll the recorded waveform in sync with what's audibly
// playing, rather than this class pushing position updates itself. Completed fires only on a natural
// end-of-clip (WaveOutEvent's own PlaybackStopped) — a caller-initiated Stop() already knows it stopped
// things, so it doesn't need telling again.
public sealed class AudioPlaybackSession : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private WaveOutEvent? _waveOut;
    private bool _stoppedByCaller;

    public event Action? Completed;

    public AudioPlaybackSession(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Play(short[] samples, WaveFormat format)
    {
        Stop();

        var provider = new InMemoryPcmProvider(samples, format);
        var waveOut = new WaveOutEvent();
        waveOut.Init(provider);
        waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
        _waveOut = waveOut;
        _stoppedByCaller = false;
        waveOut.Play();
    }

    // Sample index into the take currently playing; 0 once nothing is playing.
    public int GetPlaybackSampleIndex()
    {
        if (_waveOut is not { } waveOut) return 0;
        return (int)(waveOut.GetPosition() / 2);
    }

    public void Stop()
    {
        if (_waveOut is null) return;

        _stoppedByCaller = true;
        var waveOut = _waveOut;
        _waveOut = null;
        waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
        waveOut.Stop();
        waveOut.Dispose();
    }

    private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        var wasCallerStop = _stoppedByCaller;
        _waveOut = null;
        if (wasCallerStop) return;

        _dispatcher.BeginInvoke(() => Completed?.Invoke());
    }

    public void Dispose() => Stop();
}
