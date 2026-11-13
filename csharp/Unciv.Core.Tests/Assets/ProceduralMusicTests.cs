using Unciv.Assets;
using Unciv.GodotClient.Settings;

namespace Unciv.Core.Tests.Assets;

public class ProceduralMusicTests
{
    [Fact]
    public void EveryTrackIsDeterministicAndNonEmpty()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string track in ProceduralMusic.TrackNames)
        {
            var first = ProceduralMusic.Samples(track);
            var second = ProceduralMusic.Samples(track);
            Assert.Equal(first, second);
            Assert.True(first.Length > ProceduralMusic.SampleRate);
            Assert.Contains(first, s => System.Math.Abs(s) > 1000);
            Assert.True(seen.Add(Convert.ToBase64String(ProceduralMusic.Pcm(track))));
        }
    }

    [Fact]
    public void TrackLoopsCleanlyAtEdges()
    {
        foreach (string track in ProceduralMusic.TrackNames)
        {
            var samples = ProceduralMusic.Samples(track);
            Assert.Equal(0, samples[0]);
            Assert.Equal(0, samples[^1]);
        }
    }

    [Fact]
    public void MoodSelectionCoversAllGameStates()
    {
        Assert.Equal(MusicMood.Menu, ProceduralMusic.MoodForState(inGame: false, atWar: false, gameOver: false));
        Assert.Equal(MusicMood.Peace, ProceduralMusic.MoodForState(inGame: true, atWar: false, gameOver: false));
        Assert.Equal(MusicMood.Tension, ProceduralMusic.MoodForState(inGame: true, atWar: true, gameOver: false));
        Assert.Equal(MusicMood.Victory, ProceduralMusic.MoodForState(inGame: true, atWar: true, gameOver: true));

        Assert.Equal("menu", ProceduralMusic.TrackForMood(MusicMood.Menu));
        Assert.Equal("tension_1", ProceduralMusic.TrackForMood(MusicMood.Tension));
        Assert.Equal("victory", ProceduralMusic.TrackForMood(MusicMood.Victory));
        Assert.Equal("peace_1", ProceduralMusic.TrackForMood(MusicMood.Peace, 0));
        Assert.Equal("peace_2", ProceduralMusic.TrackForMood(MusicMood.Peace, 1));
        Assert.Equal("peace_1", ProceduralMusic.TrackForMood(MusicMood.Peace, int.MinValue));
    }

    [Fact]
    public void MusicKeyUsesMusicCategoryAndMapsToMusicKind()
    {
        var key = ProceduralMusic.KeyFor("menu");
        Assert.Equal("music/menu", key.ToString());
        Assert.Equal(AssetKind.Music, AssetKinds.Of(key));
    }

    [Fact]
    public void UnknownTrackFallsBackDeterministically()
    {
        Assert.Equal(ProceduralMusic.Samples("menu"), ProceduralMusic.Samples("nonexistent_track"));
    }

    [Fact]
    public void AudioSettingsSanitizeRanges()
    {
        var dirty = new ClientSettings
        {
            SoundVolume = -1f,
            MusicVolume = float.NaN,
            EffectsVolume = 5f,
            MuteOnFocusLoss = false,
        };
        var clean = dirty.Sanitized();
        Assert.Equal(0f, clean.SoundVolume);
        Assert.Equal(ClientSettings.Default.MusicVolume, clean.MusicVolume);
        Assert.Equal(1f, clean.EffectsVolume);
        Assert.False(clean.MuteOnFocusLoss);
    }
}
