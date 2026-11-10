using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C040/T345: the overlay shown while End turn is processed off the UI thread: a compact card under the navigation strip with a heading, the civ being
/// played ("Rome - 3 of 8"), a progress bar and (for a local turn) a cancel hint. It takes no input at all: the camera, the HUD and the menus stay usable
/// while the host or the worker plays the other civs, and a turn that finishes quickly never shows it (<see cref="NextTurnOverlayState"/>).
/// </summary>
public partial class NextTurnProgressView : Control
{
    private readonly NextTurnOverlayState state = new();
    private readonly Label heading = new() { Name = "ProgressHeading", HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = UiVariants.LabelTitle };
    private readonly Label detail = new() { Name = "ProgressDetail", HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = UiVariants.LabelSecondary };
    private readonly Label hint = new() { Name = "ProgressHint", HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = UiVariants.LabelSmall };
    private readonly ProgressBar bar = new() { Name = "ProgressBar", MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false, ThemeTypeVariation = UiVariants.StatBarThin };
    private readonly PanelContainer panel = new() { Name = "ProgressPanel", ThemeTypeVariation = UiVariants.PanelCard };
    private float top;

    public NextTurnProgressView()
    {
        Name = "NextTurnProgress";
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore; // T345: the overlay is not a blocker; the map, HUD and menus keep working
        this.Fill();
        panel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(panel);
        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 8);
        panel.AddChild(column);
        foreach (var label in new[] { heading, detail, hint }) label.MouseFilter = MouseFilterEnum.Ignore;
        bar.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(heading);
        column.AddChild(detail);
        column.AddChild(bar);
        column.AddChild(hint);
    }

    public string HeadingText => heading.Text;

    public string DetailText => detail.Text;

    public string HintText => hint.Text;

    public double Fraction => bar.Value;

    /// <summary>T345: the card's drawn rect, so a scenario can assert it does not cover the multiplayer turn bar.</summary>
    public Rect2 CardRect => panel.GetGlobalRect();

    /// <summary>T345: how long a turn may take before the card appears (the default is <see cref="NextTurnOverlayState.DefaultRevealDelaySeconds"/>).</summary>
    public double RevealDelaySeconds
    {
        get => state.RevealDelaySeconds;
        set => state.RevealDelaySeconds = value;
    }

    /// <summary>T345: the overlay advances with the real frame time in the game; the scenario runner turns this off and steps <see cref="Advance"/> explicitly, so no capture depends on frame speed.</summary>
    public bool RealTime { get; set; } = true;

    public void Init(UiTheme uiTheme)
    {
        Theme = uiTheme.Theme;
        bar.CustomMinimumSize = new Vector2(uiTheme.Px(320), uiTheme.Px(14));
        panel.CustomMinimumSize = new Vector2(uiTheme.Px(320), 0);
    }

    /// <summary>C067/T345: the overlay fills its parent by anchors and its card sits centred under <paramref name="top"/> (the navigation strip).</summary>
    public void LayoutIn(Vector2 screen, float top)
    {
        if (Size != screen) Size = screen; // the bar can be redrawn between layouts; an unchanged size must not be set again (Godot warns and the anchors would override it)
        this.top = top;
        LayoutCard();
    }

    public override void _Process(double delta)
    {
        if (RealTime) Advance(delta);
    }

    /// <summary>Adds frame time so a short turn never flashes the card (the harness steps it directly).</summary>
    public void Advance(double seconds)
    {
        state.Advance(seconds);
        Apply();
    }

    /// <summary>Starts a turn (local End turn or a host-resolved round): hidden until the reveal delay has passed.</summary>
    public void Start(NextTurnProgressViewData view, string hintText)
    {
        state.Start(view, hintText);
        Apply();
    }

    /// <summary>Updates the line of the running turn (the civ about to play); never reveals it early.</summary>
    public void SetProgress(NextTurnProgressViewData view)
    {
        state.SetProgress(view);
        Apply();
    }

    /// <summary>T345: another client resolves the turn: show "waiting for AI/host" after the same delay, without a cancel hint (it is not ours to cancel).</summary>
    public void WaitForHost(NextTurnProgressViewData view)
    {
        state.WaitForHost(view);
        Apply();
    }

    /// <summary>Shows the data at once (a fixed scenario capture or a direct test).</summary>
    public void ShowNow(NextTurnProgressViewData view, string hintText = "")
    {
        state.ShowNow(view, hintText);
        Apply();
    }

    public void Close()
    {
        state.Close();
        Apply();
    }

    private void Apply()
    {
        Visible = state.ShouldShow;
        if (!Visible) return;
        heading.Text = state.View.Heading.TrText();
        detail.Text = state.View.Detail.TrText();
        hint.Text = state.Hint.TrText();
        hint.Visible = state.Hint.Length > 0;
        bar.Value = state.View.Fraction;
        LayoutCard(); // the text decides the card's width, so measure it again after every change
    }

    private void LayoutCard()
    {
        panel.Size = panel.GetCombinedMinimumSize();
        panel.Position = new Vector2(Math.Max(0f, (Size.X - panel.Size.X) / 2f), top);
    }
}
