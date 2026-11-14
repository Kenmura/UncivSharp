using System.Collections.Frozen;

namespace Unciv.Assets;

/// <summary>Music mood selected by the client's current state (menu, peace, war tension, or game over).</summary>
public enum MusicMood
{
    Menu,
    Peace,
    Tension,
    Victory,
}

/// <summary>
/// T353: deterministic, original CC0/MPL-2.0 procedural soundtrack synthesis. Generates 16-bit mono PCM tracks at
/// <see cref="SampleRate"/> using additive harmonics and smooth envelopes with seamless loop boundaries.
/// Overridable via <c>assets/override/music/&lt;name&gt;.{ogg,wav}</c> or an asset pack's <c>music/</c> entries.
/// </summary>
public static class ProceduralMusic
{
    public const string Category = "music";
    public const int SampleRate = ProceduralSounds.SampleRate;
    private const float PeakGain = 0.55f;

    private readonly record struct MusicVoice(float[] FrequenciesHz, float StepSeconds, float Gain, bool WarmPad);

    private static readonly FrozenDictionary<string, MusicVoice[]> Tracks = new Dictionary<string, MusicVoice[]>(StringComparer.Ordinal)
    {
        ["menu"] =
        [
            new([261.63f, 293.66f, 329.63f, 392.00f], 2.0f, 0.30f, true),
            new([523.25f, 587.33f, 659.25f, 587.33f, 523.25f, 440.00f, 392.00f, 440.00f], 1.0f, 0.22f, false),
        ],
        ["peace_1"] =
        [
            new([220.00f, 261.63f, 293.66f, 246.94f], 2.0f, 0.28f, true),
            new([440.00f, 523.25f, 587.33f, 659.25f, 587.33f, 523.25f, 493.88f, 440.00f], 1.0f, 0.22f, false),
        ],
        ["peace_2"] =
        [
            new([196.00f, 246.94f, 293.66f, 220.00f], 2.0f, 0.28f, true),
            new([392.00f, 493.88f, 587.33f, 659.25f, 587.33f, 493.88f, 440.00f, 392.00f], 1.0f, 0.22f, false),
        ],
        ["tension_1"] =
        [
            new([146.83f, 146.83f, 164.81f, 130.81f], 2.0f, 0.34f, true),
            new([293.66f, 349.23f, 329.63f, 293.66f, 261.63f, 293.66f, 329.63f, 261.63f], 1.0f, 0.20f, false),
        ],
        ["victory"] =
        [
            new([261.63f, 329.63f, 392.00f], 2.0f, 0.30f, true),
            new([523.25f, 659.25f, 783.99f, 1046.50f, 783.99f, 1046.50f], 1.0f, 0.24f, false),
        ],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> TrackNames => Tracks.Keys;

    public static AssetKey KeyFor(string trackName) => AssetKey.Of(Category, trackName);

    public static MusicMood MoodForState(bool inGame, bool atWar, bool gameOver)
    {
        if (gameOver) return MusicMood.Victory;
        if (!inGame) return MusicMood.Menu;
        return atWar ? MusicMood.Tension : MusicMood.Peace;
    }

    public static string TrackForMood(MusicMood mood, int seed = 0) => mood switch
    {
        MusicMood.Menu => "menu",
        MusicMood.Tension => "tension_1",
        MusicMood.Victory => "victory",
        _ => (seed & 1) == 0 ? "peace_1" : "peace_2",
    };

    /// <summary>Synthesizes deterministic 16-bit mono PCM samples for <paramref name="trackName"/>.</summary>
    public static short[] Samples(string trackName)
    {
        var voices = Tracks.TryGetValue(trackName, out var found) ? found : Tracks["menu"];
        float durationSeconds = voices.Max(v => v.FrequenciesHz.Length * v.StepSeconds);
        int totalSamples = (int)(durationSeconds * SampleRate);
        var mix = new float[totalSamples];

        foreach (var voice in voices)
            AddVoice(mix, voice);

        // Gentle 20 ms fade-in/out at loop boundaries so looping has zero click.
        int edge = Math.Min(totalSamples / 4, SampleRate / 50);
        for (int i = 0; i < edge; i++)
        {
            float env = (float)i / edge;
            mix[i] *= env;
            mix[totalSamples - 1 - i] *= env;
        }

        var samples = new short[totalSamples];
        for (int i = 0; i < totalSamples; i++)
            samples[i] = (short)(Math.Clamp(mix[i], -PeakGain, PeakGain) * short.MaxValue);
        return samples;
    }

    private static void AddVoice(float[] mix, MusicVoice voice)
    {
        int stepSamples = Math.Max(1, (int)(voice.StepSeconds * SampleRate));
        for (int step = 0; step < voice.FrequenciesHz.Length; step++)
        {
            float freq = voice.FrequenciesHz[step];
            int start = step * stepSamples;
            int end = Math.Min(mix.Length, start + stepSamples);
            int len = end - start;
            if (len <= 0) continue;

            int attack = Math.Max(1, voice.WarmPad ? len / 4 : len / 10);
            for (int i = 0; i < len; i++)
            {
                float t = (start + i) / (float)SampleRate;
                float fade = 1f - (float)i / len;
                float env = Math.Min(1f, (float)i / attack) * (voice.WarmPad ? 0.4f + 0.6f * fade : fade * fade);
                double phase = 2.0 * Math.PI * freq * t;
                float wave = voice.WarmPad
                    ? (float)(0.75 * Math.Sin(phase) + 0.25 * Math.Sin(phase * 2.0))
                    : (float)(0.70 * Math.Sin(phase) + 0.20 * Math.Sin(phase * 2.0) + 0.10 * Math.Sin(phase * 3.0));
                mix[start + i] += wave * env * voice.Gain;
            }
        }
    }

    public static byte[] Pcm(string trackName)
    {
        var samples = Samples(trackName);
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            bytes[i * 2] = (byte)(samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return bytes;
    }
}
