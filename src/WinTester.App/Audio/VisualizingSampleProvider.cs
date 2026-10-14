using System;
using NAudio.Wave;

namespace WinTester.App.Audio;

// Sits between AudioFileReader and WaveOutEvent in the playback chain. Every Read() call NAudio's playback
// thread makes for actual audio output also gets mixed down to mono and copied into a small ring buffer,
// which AudioTesterWindow's render-timer tick (running on the UI thread) snapshots via CopyLatest — that's
// the whole basis for the visualizer, since WPF's own MediaPlayer never exposes sample data at all.
public sealed class VisualizingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _ring;
    private readonly object _lock = new();
    private int _writePos;

    public VisualizingSampleProvider(ISampleProvider source, int ringSize = 4096)
    {
        _source = source;
        _ring = new float[ringSize];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);
        var channels = Math.Max(1, WaveFormat.Channels);

        lock (_lock)
        {
            for (var i = 0; i + channels <= samplesRead; i += channels)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += buffer[offset + i + c];

                _ring[_writePos] = sum / channels;
                _writePos = (_writePos + 1) % _ring.Length;
            }
        }

        return samplesRead;
    }

    // Fills destination with the most recent destination.Length mono samples, oldest first. Safe to call
    // from any thread; if fewer samples have ever been written than requested, the untouched slots are left
    // at 0 (silence) rather than reading garbage.
    public void CopyLatest(float[] destination)
    {
        lock (_lock)
        {
            var n = destination.Length;
            for (var i = 0; i < n; i++)
            {
                var idx = ((_writePos - n + i) % _ring.Length + _ring.Length) % _ring.Length;
                destination[i] = _ring[idx];
            }
        }
    }
}
