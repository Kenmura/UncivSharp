using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C026: the modal popup of one tutorial step (<see cref="TutorialQueue"/>): the tutorial's name, the step's text, a step counter when there are several, and an OK
/// button (Space also presses it). It raises <see cref="Dismissed"/>; the owner advances the queue and marks the tutorial seen after its last step.
/// C066: a <see cref="Modal"/>; Escape dismisses the step like OK.
/// </summary>
public partial class TutorialPopupView : Modal
{
    private readonly Label body = new() { Name = "TutorialBody", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label counter = new() { Name = "TutorialCounter", ThemeTypeVariation = UiVariants.LabelMuted, Visible = false };
    private readonly StepPipStrip stepPips = new() { Name = "TutorialPips", Visible = false };
    private readonly Button ok = new() { Name = "TutorialOk" };
    private readonly Button never = new() { Name = "TutorialNever" };

    public TutorialPopupView()
    {
        Name = "TutorialPopup";
        TitleLabel.Name = "TutorialTitle";
        Body.AddChild(body);
        Body.AddChild(stepPips);
        Body.AddChild(counter);
        SetActions([(never, ChoiceRole.Ghost), (ok, ChoiceRole.Primary)]);
        never.Pressed += () => NeverShowRequested?.Invoke();
        ok.Pressed += () => Dismissed?.Invoke();
    }

    public TutorialPage? Current { get; private set; }

    public string TitleText => TitleLabel.Text;

    public string BodyText => body.Text;

    public string CounterText => counter.Text;

    public Button OkButton => ok;

    public Button NeverButton => never;

    /// <summary>The player chose "Never show tutorials": the owner turns the persisted setting off and closes the popup.</summary>
    public event Action? NeverShowRequested;

    /// <summary>The OK button was pressed (or Space, or Escape).</summary>
    public event Action? Dismissed;

    public void Open(TutorialPage page)
    {
        Current = page;
        SetHeader(page.Title);
        Fit(480, body); // T323: re-fitted on a live resize
        body.Text = page.Text.TrText();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        counter.Text = page.StepCount > 1 ? $"{(page.Step + 1).ToString(inv)} / {page.StepCount.ToString(inv)}" : "";
        counter.Visible = false;
        stepPips.Visible = page.StepCount > 1;
        stepPips.SetSteps(page.Step, page.StepCount);
        ok.Text = "OK".Tr();
        never.Text = "Never show tutorials".Tr();
        Visible = true;
    }

    public void Close()
    {
        Current = null;
        Visible = false;
    }

    protected override bool OnCancel()
    {
        if (Current == null) return false;
        Dismissed?.Invoke();
        return true;
    }

    private sealed partial class StepPipStrip : Control
    {
        private int current;
        private int total;

        public void SetSteps(int stepIndex, int stepCount)
        {
            current = Math.Max(0, stepIndex);
            total = Math.Max(0, stepCount);
            float scale = Math.Max(1f, GetThemeDefaultFontSize() / (float)UiTokens.FontSize);
            float w = total * 16f * scale + Math.Max(0, total - 1) * 6f * scale;
            CustomMinimumSize = total > 1 ? new Vector2(w, 10f * scale) : Vector2.Zero;
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (total <= 1) return;
            float scale = Math.Max(1f, GetThemeDefaultFontSize() / (float)UiTokens.FontSize);
            float pipW = 16f * scale;
            float pipH = 6f * scale;
            float gap = 6f * scale;
            float y = (Size.Y - pipH) / 2f;
            var done = ProceduralAssets.ToColor(UiTokens.Accent);
            var active = ProceduralAssets.ToColor(UiTokens.AccentHi);
            var pending = ProceduralAssets.ToColor(UiTokens.Inset);
            var border = ProceduralAssets.ToColor(UiTokens.BorderStrong);

            for (int i = 0; i < total; i++)
            {
                var rect = new Rect2(i * (pipW + gap), y, pipW, pipH);
                Color fill = i == current ? active : i < current ? done : pending;
                DrawRect(rect, fill, filled: true);
                if (i > current) DrawRect(rect, border, filled: false, width: Math.Max(1f, scale));
            }
        }
    }
}
