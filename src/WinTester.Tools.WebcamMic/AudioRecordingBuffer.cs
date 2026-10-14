using System;

namespace WinTester.Tools.WebcamMic;

// Holds at most MaxSeconds of one take as 16-bit PCM samples, entirely in a managed array — no temp file,
// nothing written to disk. Reset() wipes whatever was captured before rather than accumulating takes, so
// "record" always means "replace the one stored clip."
public sealed class AudioRecordingBuffer
{
    public const int MaxSeconds = 30;

    private readonly short[] _samples;
    private int _writeIndex;

    public int SampleRate { get; }
    public int Channels { get; }
    public int CapacitySamples { get; }

    // Number of samples actually written this take (<= CapacitySamples).
    public int RecordedSampleCount => _writeIndex;

    public bool IsFull => _writeIndex >= CapacitySamples;

    public AudioRecordingBuffer(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        CapacitySamples = sampleRate * channels * MaxSeconds;
        _samples = new short[CapacitySamples];
    }

    public void Reset() => _writeIndex = 0;

    // Appends as many of the given samples as still fit before the 30-second cap and returns that count,
    // so the caller (AudioCaptureSession's consumer) knows when a take has hit the limit and should stop
    // itself rather than silently discarding the tail of every buffer once full.
    public int Append(ReadOnlySpan<short> samples)
    {
        var remaining = CapacitySamples - _writeIndex;
        var toCopy = Math.Min(remaining, samples.Length);
        if (toCopy <= 0) return 0;

        samples.Slice(0, toCopy).CopyTo(_samples.AsSpan(_writeIndex, toCopy));
        _writeIndex += toCopy;
        return toCopy;
    }

    // Copies out just the recorded portion — safe for the caller to keep/play back even while this buffer
    // keeps recording into the same backing array afterward.
    public short[] ToArray() => _samples.AsSpan(0, _writeIndex).ToArray();
}
