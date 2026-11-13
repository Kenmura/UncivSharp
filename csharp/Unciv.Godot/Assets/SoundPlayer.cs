using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Settings;
using Unciv.Models;

namespace Unciv.GodotClient.Assets;

/// <summary>
/// C030: plays <see cref="UncivSound"/>s through a small pool of players on the master bus (whose level the options screen sets). The streams come from
/// <see cref="AssetRegistry.Sound"/>, so a dropped-in <c>assets/override/sound/&lt;name&gt;.ogg</c> or sound pack replaces the generated tones. C057: the game screen plays the engine's
/// <c>SoundRequested</c> cues through it (<see cref="PlaySound"/>). T353: adds looping music tracks (<see cref="PlayMusic"/>), separate effects/music levels,
/// focus-loss muting, and headless test silence.
/// </summary>
public sealed partial class SoundPlayer : Node
{
    public const int Voices = 8;

    private AssetRegistry registry;
    private readonly List<AudioStreamPlayer> voices = [];
    private readonly AudioStreamPlayer musicPlayer;
    private int cursor;
    private bool focusMuted;
    private float effectsVolumeDb;
    private float musicVolumeDb;
    private string? currentMusicTrack;

    public SoundPlayer(AssetRegistry registry)
    {
        this.registry = registry;
        Name = "SoundPlayer";
        IsHeadless = string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase) || OS.HasFeature("headless");
        for (var i = 0; i < Voices; i++)
        {
            var voice = new AudioStreamPlayer { Name = "Voice" + i };
            voices.Add(voice);
            AddChild(voice);
        }
        musicPlayer = new AudioStreamPlayer { Name = "MusicPlayer" };
        AddChild(musicPlayer);
        UpdateBusMute();
    }

    /// <summary>True when running under Godot's headless driver/tests (bus 0 stays muted so no audio device output occurs).</summary>
    public bool IsHeadless { get; }

    public string? CurrentMusicTrack => currentMusicTrack;

    /// <summary>Replaces the active asset registry (e.g. when switching asset/sound packs).</summary>
    public void SetRegistry(AssetRegistry newRegistry)
    {
        registry = newRegistry;
        if (currentMusicTrack is { } track && musicPlayer.Playing)
        {
            musicPlayer.Stream = registry.Music(track);
            musicPlayer.VolumeDb = musicVolumeDb;
            musicPlayer.Play();
        }
    }

    /// <summary>Applies independent effects and music volume levels (0..1).</summary>
    public void ApplyVolumes(float effectsVolume, float musicVolume)
    {
        effectsVolumeDb = OptionsModel.VolumeDb(effectsVolume);
        musicVolumeDb = OptionsModel.VolumeDb(musicVolume);
        foreach (var voice in voices) voice.VolumeDb = effectsVolumeDb;
        musicPlayer.VolumeDb = musicVolumeDb;
    }

    /// <summary>Mutes or unmutes all output when the application loses/gains window focus.</summary>
    public void SetFocusMuted(bool muted)
    {
        focusMuted = muted;
        UpdateBusMute();
    }

    private void UpdateBusMute() => AudioServer.SetBusMute(0, IsHeadless || focusMuted);

    /// <summary>Starts the sound; false when it is silent. A busy pool takes over its oldest voice.</summary>
    public bool Play(UncivSound sound)
    {
        if (registry.Sound(sound) is not { } stream) return false;
        var index = ProceduralSounds.PickVoice(voices.Select(it => it.Playing).ToList(), cursor);
        cursor = (index + 1) % Voices;
        voices[index].Stream = stream;
        voices[index].VolumeDb = effectsVolumeDb;
        voices[index].Play();
        return true;
    }

    /// <summary>Starts or switches the looping music track for <paramref name="mood"/>.</summary>
    public void PlayMusic(MusicMood mood, int seed = 0)
    {
        var track = ProceduralMusic.TrackForMood(mood, seed);
        if (string.Equals(currentMusicTrack, track, StringComparison.Ordinal) && musicPlayer.Playing) return;
        currentMusicTrack = track;
        musicPlayer.Stream = registry.Music(track);
        musicPlayer.VolumeDb = musicVolumeDb;
        musicPlayer.Play();
    }

    public void StopMusic()
    {
        currentMusicTrack = null;
        musicPlayer.Stop();
    }

    public bool HasSound(string soundName) => !string.IsNullOrEmpty(soundName);

    /// <summary>Voices currently playing (for the harness).</summary>
    public int PlayingVoices => voices.Count(it => it.Playing);

    public void PlaySound(string soundName) => Play(new UncivSound(soundName));
}
