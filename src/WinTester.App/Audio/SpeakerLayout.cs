namespace WinTester.App.Audio;

// WASAPI exposes a device's real hardware channel count via AudioClient.MixFormat.Channels, but NAudio
// 2.2.1's WaveFormatExtensible wrapper does not surface the format's channel mask — the bit flags that
// say which physical position each channel index actually is (front-left, rear-right, subwoofer, etc.).
// Reading that mask directly would mean hand-rolling native WAVEFORMATEXTENSIBLE marshaling for one
// field, so this instead maps channel COUNT to the standard Windows speaker layout for that count
// (ksmedia.h's KSAUDIO_SPEAKER_* configs) — the same convention a WaveFormatExtensible falls back to
// when no explicit mask is given, and what the overwhelming majority of real 2.0/2.1/4.0/5.1/7.1 setups
// report. Channel index assignment below matches the bit order of those standard masks (lowest set bit
// = channel 0), so it lines up with how Windows actually lays out samples within a frame.
public static class SpeakerLayout
{
    // One stop on the sweep ring: which physical output channel it drives, and its display label.
    public readonly struct Step
    {
        public Step(int channelIndex, string label)
        {
            ChannelIndex = channelIndex;
            Label = label;
        }

        public int ChannelIndex { get; }
        public string Label { get; }
    }

    // Order is the clockwise sweep path (front-left round to front-right, then the rear/side pair,
    // looping back to front-left), with the subwoofer — which has no spatial position to sweep through —
    // appended as a final extra stop rather than folded into the circle. Unrecognized channel counts fall
    // back to a plain sequential CH1..CHn sweep so the feature degrades gracefully instead of guessing.
    public static Step[] BuildSweepOrder(int channelCount) => channelCount switch
    {
        1 => new[] { new Step(0, "Mono") },
        2 => new[] { new Step(0, "FL"), new Step(1, "FR") },
        3 => new[] { new Step(0, "FL"), new Step(1, "FR"), new Step(2, "SW") },
        4 => new[] { new Step(0, "FL"), new Step(1, "FR"), new Step(3, "RR"), new Step(2, "RL") },
        5 => new[] { new Step(0, "FL"), new Step(2, "C"), new Step(1, "FR"), new Step(4, "SR"), new Step(3, "SL") },
        6 => new[] { new Step(0, "FL"), new Step(2, "C"), new Step(1, "FR"), new Step(5, "RR"), new Step(4, "RL"), new Step(3, "SW") },
        7 => new[] { new Step(0, "FL"), new Step(2, "C"), new Step(1, "FR"), new Step(6, "SR"), new Step(4, "RR"), new Step(3, "RL"), new Step(5, "SL") },
        8 => new[] { new Step(0, "FL"), new Step(2, "C"), new Step(1, "FR"), new Step(7, "SR"), new Step(5, "RR"), new Step(4, "RL"), new Step(6, "SL"), new Step(3, "SW") },
        _ => BuildGenericSequentialOrder(channelCount)
    };

    private static Step[] BuildGenericSequentialOrder(int channelCount)
    {
        var steps = new Step[channelCount];
        for (var i = 0; i < channelCount; i++)
            steps[i] = new Step(i, $"CH{i + 1}");
        return steps;
    }

// Maps audio channel counts to corresponding speaker-diagram image sets for visual representation.
// Falls back to a universal diagram with an overlaid text label for channel counts without dedicated artwork.
    public static string GetDiagramKey(int channelCount) => channelCount switch
    {
        2 => "2.0",
        4 => "4.0",
        6 => "5.1",
        8 => "7.1",
        _ => "UNIVERSAL"
    };
}
