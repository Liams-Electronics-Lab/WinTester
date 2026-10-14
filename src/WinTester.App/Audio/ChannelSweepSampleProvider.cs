using System;
using NAudio.Wave;

namespace WinTester.App.Audio;

// Sits after VisualizingSampleProvider in the playback chain and re-renders its (stereo) output across
// the WASAPI device's real channel count. Every frame is downmixed to mono, then written to all output
// channels; when SweepEnabled, per-channel gains instead trace a continuous loop around the device's
// SpeakerLayout ring — one speaker at a time, equal-power crossfaded into the next — so the same audio
// keeps playing but each physical speaker gets an isolated turn. The ring loops for as long as playback
// continues, independent of how many times it completes during one track.
public sealed class ChannelSweepSampleProvider : ISampleProvider
{
    // Time a speaker holds full presence before crossfading into the next; the ring's total loop time is
    // (DwellSeconds + CrossfadeSeconds) * ring length. Both easily tunable.
    private const double DwellSeconds = 2.2;
    private const double CrossfadeSeconds = 0.8;
    private const double StepSeconds = DwellSeconds + CrossfadeSeconds;

    private readonly ISampleProvider _source;
    private readonly WaveFormat _outputFormat;
    private readonly int _sourceChannels;
    private readonly SpeakerLayout.Step[] _ring;
    private readonly float[] _gains;
    private readonly double _cycleSeconds;

    private float[] _sourceBuffer = Array.Empty<float>();
    private double _phaseSeconds;

    public ChannelSweepSampleProvider(ISampleProvider source, int outputChannels, SpeakerLayout.Step[] ring)
    {
        _source = source;
        _sourceChannels = Math.Max(1, source.WaveFormat.Channels);
        _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
        _ring = ring;
        _gains = new float[outputChannels];
        _cycleSeconds = StepSeconds * Math.Max(1, ring.Length);
    }

    public WaveFormat WaveFormat => _outputFormat;

    // Off by default; AudioTesterWindow flips this from the Sweep toggle. Toggling never tears down or
    // reinitializes the WASAPI stream — off just means identical content on every physical channel,
    // which is how the file always sounded before this feature existed.
    public bool SweepEnabled { get; set; }

    // Current ring position as a display label ("FR", or "FR\u2192RR" mid-crossfade), refreshed every
    // ComputeRingGains call. Written on the WASAPI playback thread; read by the UI's render timer. A
    // string reference read/write is atomic in .NET, so no lock is needed for this display-only value.
    public string CurrentLabel { get; private set; } = string.Empty;

    // Single-speaker label for the overlay image (no crossfade arrow) — which speaker's image should be
    // on screen right now. Holds the current ring stop for the whole dwell, then switches to the next
    // stop halfway through the crossfade (rather than exactly when the crossfade starts) so the image
    // swap roughly lines up with the point where the next speaker actually becomes the louder of the two.
    public string CurrentImageLabel { get; private set; } = string.Empty;

    // Restarts the ring at its first stop. Called on Stop so the next Play begins the sweep from the
    // front-left speaker again rather than resuming mid-ring from wherever it left off.
    public void ResetPhase() => _phaseSeconds = 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var outputChannels = _outputFormat.Channels;
        var framesRequested = count / outputChannels;
        var sweepActive = SweepEnabled && _ring.Length > 1;

        EnsureSourceBuffer(framesRequested * _sourceChannels);
        var sourceSamplesRead = _source.Read(_sourceBuffer, 0, framesRequested * _sourceChannels);
        var framesRead = sourceSamplesRead / _sourceChannels;

        for (var frame = 0; frame < framesRequested; frame++)
        {
            var mixed = 0f;
            if (frame < framesRead)
            {
                for (var c = 0; c < _sourceChannels; c++)
                    mixed += _sourceBuffer[frame * _sourceChannels + c];
                mixed /= _sourceChannels;
            }

            if (sweepActive)
            {
                ComputeRingGains();
                _phaseSeconds += 1.0 / _outputFormat.SampleRate;
                for (var c = 0; c < outputChannels; c++)
                    buffer[offset + frame * outputChannels + c] = mixed * _gains[c];
            }
            else
            {
                for (var c = 0; c < outputChannels; c++)
                    buffer[offset + frame * outputChannels + c] = mixed;
            }
        }

        // Always returns a full block — including zero-padding past the source's natural end — rather
        // than signalling end-of-stream via a short/zero read. AudioTesterWindow already detects and
        // handles end-of-track itself by polling AudioFileReader.CurrentTime against TotalTime, so this
        // just avoids WasapiOut's own end-of-stream path racing that UI-driven stop.
        return framesRequested * outputChannels;
    }

    private void EnsureSourceBuffer(int minLength)
    {
        if (_sourceBuffer.Length < minLength)
            _sourceBuffer = new float[minLength];
    }

    private void ComputeRingGains()
    {
        Array.Clear(_gains, 0, _gains.Length);

        var cyclePos = _phaseSeconds % _cycleSeconds;
        var stepIndex = (int)(cyclePos / StepSeconds);
        if (stepIndex >= _ring.Length) stepIndex = _ring.Length - 1; // guards float rounding at the wrap
        var intoStep = cyclePos - stepIndex * StepSeconds;

        var current = _ring[stepIndex];
        var next = _ring[(stepIndex + 1) % _ring.Length];

        float currentGain, nextGain;
        if (intoStep < DwellSeconds)
        {
            currentGain = 1f;
            nextGain = 0f;
            CurrentLabel = current.Label;
            CurrentImageLabel = current.Label;
        }
        else
        {
            // Equal-power crossfade (cos/sin rather than linear) so perceived loudness doesn't dip
            // mid-transition between the two active speakers.
            var t = (intoStep - DwellSeconds) / CrossfadeSeconds;
            currentGain = MathF.Cos((float)t * MathF.PI / 2f);
            nextGain = MathF.Sin((float)t * MathF.PI / 2f);
            CurrentLabel = $"{current.Label}\u2192{next.Label}";
            CurrentImageLabel = t < 0.5 ? current.Label : next.Label;
        }

        _gains[current.ChannelIndex] += currentGain;
        _gains[next.ChannelIndex] += nextGain;
    }
}
