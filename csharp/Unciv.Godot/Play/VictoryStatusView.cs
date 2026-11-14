using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C044: the "Victory status" page opened from the menu: for each victory the viewer's own milestones with their state and every civ's progress, from
/// <see cref="VictoryStatusModel"/>. A scrolling list and a Close button; it shows what it is given.
/// C077: a C066 <see cref="Modal"/> with one <see cref="SectionHeader"/> per victory, the milestones with their state in colour (done green, partly gold, open muted) and the civs' lines
/// (yours in the accent colour, the defeated muted), or an <see cref="EmptyState"/> when no victory is enabled.
/// </summary>
public partial class VictoryStatusView : Modal
{
    private readonly VBoxContainer lines = new() { Name = "VictoryLines" };
    private readonly ScrollContainer scroll = new() { Name = "VictoryScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
    private Button close = new() { Name = "VictoryClose" };

    public VictoryStatusView()
    {
        Name = "VictoryStatus";
        lines.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(lines);
    }

    /// <summary>The text of each line as shown, top to bottom.</summary>
    public IReadOnlyList<string> LineTexts => lines.GetChildren().Select(LineText).Where(it => it.Length > 0).ToList();

    public Button CloseButton => close;

    public override void Init(UiTheme uiTheme)
    {
        base.Init(uiTheme);
        lines.AddThemeConstantOverride("separation", uiTheme.Px(UiTokens.S1));
    }

    /// <summary>Escape closes the page.</summary>
    protected override bool OnCancel()
    {
        Close();
        return true;
    }

    public void Open(IReadOnlyList<VictorySection> sections)
    {
        SetHeader(VictoryStatusModel.Title);
        float width = Fit(MaxWidth, scroll); // T328: the body wrap width follows a live resize/scale change
        foreach (var child in lines.GetChildren())
        {
            lines.RemoveChild(child);
            child.QueueFree();
        }
        if (sections.Count == 0) lines.AddChild(new EmptyState(Ui, AssetKey.Of(RulesetAssetKeys.UiIcon, "Score"), VictoryStatusModel.NoVictories));
        foreach (var section in sections)
        {
            var header = new SectionHeader(section.Header) { Name = "Header_" + section.Name };
            lines.AddChild(header);
            foreach (var own in section.Own)
            {
                var tint = ProceduralAssets.ToColor(own.Status switch { "Completed" => UiTokens.Success, "Partially" => UiTokens.Warning, _ => UiTokens.TextSecondary });
                string iconName = own.Status switch { "Completed" => "Checkmark", "Partially" => "ForwardArrow", _ => "HexagonOutline" };
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", Ui.Px(UiTokens.S1));
                row.AddChild(new TextureRect
                {
                    Texture = Ui.Icon(AssetKey.Of(RulesetAssetKeys.UiIcon, iconName)),
                    Modulate = tint,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    TextureFilter = TextureFilterEnum.LinearWithMipmaps,
                    CustomMinimumSize = new Vector2(Ui.Px(16), Ui.Px(16)),
                    SizeFlagsVertical = SizeFlags.ShrinkCenter,
                });
                var label = Wrapped(own.Text.Tr() + (own.Progress.Length > 0 ? " (" + own.Progress + ")" : ""));
                label.AddThemeColorOverride("font_color", tint);
                row.AddChild(label);
                lines.AddChild(row);
            }
            foreach (var civ in section.Civs)
            {
                var label = Wrapped("  " + TranslateCivLine(civ.Text) + (civ.IsDefeated ? " (defeated)".TrText() : ""));
                label.Name = "Civ_" + section.Name + "_" + civ.Civ;
                if (civ.IsViewer) label.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(UiTokens.Accent));
                else if (civ.IsDefeated) label.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(UiTokens.TextMuted));
                lines.AddChild(label);
            }
        }
        foreach (var child in Body.GetChildren()) Body.RemoveChild(child);
        scroll.CustomMinimumSize = new Vector2(width, Ui.Px(260));
        Body.AddChild(scroll);
        close = new Button { Name = "VictoryClose", Text = GameMenuModel.CloseLabel.TrText() };
        close.Pressed += Close;
        SetActions([(close, ChoiceRole.Primary)]);
        Visible = true;
    }

    public void Close() => Visible = false;

    private static string LineText(Node node) => node switch
    {
        Label label => label.Text,
        SectionHeader header => header.ShownText,
        HBoxContainer row => row.GetChildren().OfType<Label>().FirstOrDefault()?.Text ?? "",
        _ => "",
    };

    private static string TranslateCivLine(string line)
    {
        const string nextMarker = ", next: ";
        int next = line.IndexOf(nextMarker, StringComparison.Ordinal);
        return next < 0 ? line.TrText() : line[..(next + nextMarker.Length)].TrText() + line[(next + nextMarker.Length)..].Tr();
    }

    private static Label Wrapped(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
}
