using Godot;
using Unciv.GodotClient.Ui;

namespace Unciv.GodotClient.Settings;

/// <summary>C028: puts the machine-level settings into effect on the running window: master volume, window mode, UI scale, and text scale (T352). Called at start and after every change (with the settings before it): the window is only touched when its own settings changed, so a window the player maximized or resized by hand is left alone (T301).</summary>
public static class ClientSettingsApplier
{
    public static void Apply(ClientSettings settings, Window? window, ClientSettings? previous = null, UiTheme? theme = null)
    {
        AudioServer.SetBusVolumeDb(0, OptionsModel.VolumeDb(settings.SoundVolume));
        theme?.SetTextScale(settings.TextScale);
        if (window == null || !OptionsModel.WindowNeedsApplying(previous, settings)) return;
        window.MinSize = new Vector2I(Unciv.Assets.UiTokens.MinWindowWidth, Unciv.Assets.UiTokens.MinWindowHeight); // T311
        window.Mode = settings.WindowMode switch
        {
            "Fullscreen" => Window.ModeEnum.ExclusiveFullscreen,
            "Borderless" => Window.ModeEnum.Fullscreen,
            _ => Window.ModeEnum.Windowed,
        };
        window.ContentScaleMode = settings.UiScale == 1f ? Window.ContentScaleModeEnum.Disabled : Window.ContentScaleModeEnum.CanvasItems;
        window.ContentScaleFactor = settings.UiScale;
    }
}
