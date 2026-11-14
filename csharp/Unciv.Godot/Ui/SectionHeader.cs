using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;

namespace Unciv.GodotClient.Ui;

/// <summary>C065: an overline title (upper case) followed by a hairline rule that fills the rest of the width.</summary>
public partial class SectionHeader : HBoxContainer
{
    private readonly Label label = new() { ThemeTypeVariation = UiVariants.LabelOverline, VerticalAlignment = VerticalAlignment.Center };

    public SectionHeader(string title, UiTheme? theme = null, AssetKey? icon = null)
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Title = title;
        if (theme != null && icon is { } key)
        {
            this.FitConstant(theme, "separation", UiTokens.S2);
            var picture = new TextureRect
            {
                Texture = theme.Icon(key),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = TextureFilterEnum.LinearWithMipmaps,
                Modulate = ProceduralAssets.ToColor(UiTokens.TextMuted),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            }.FitMin(theme, 16, 16);
            AddChild(picture);
        }
        label.Text = UiText.StripGlyphs(title.TrText()).TrimStart('~').ToUpperInvariant(); // C073: a stat name translates with its icon marker ("¤Gold"), which the overline cannot draw
        AddChild(label);
        AddChild(new HSeparator { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter });
    }

    /// <summary>The untranslated title as given.</summary>
    public string Title { get; }

    /// <summary>The text shown: the translated title in upper case (invariant culture).</summary>
    public string ShownText => label.Text;
}
