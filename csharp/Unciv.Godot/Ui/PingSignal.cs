using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;

namespace Unciv.GodotClient.Ui;

/// <summary>
/// T413: 4-bar ascending signal-strength indicator for relay latency.
/// </summary>
public partial class PingSignal : Control
{
    private readonly UiTheme theme;
    private int bars;

    public PingSignal(UiTheme uiTheme)
    {
        theme = uiTheme;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
        UpdateSize();
    }

    public int Bars => bars;

    public override void _EnterTree()
    {
        theme.ScaleChanged += UpdateSize;
        UpdateSize();
    }

    public override void _ExitTree() => theme.ScaleChanged -= UpdateSize;

    public void SetBars(int activeBars)
    {
        bars = Math.Clamp(activeBars, 0, 4);
        QueueRedraw();
    }

    private void UpdateSize() => CustomMinimumSize = new Vector2(theme.Px(20), theme.Px(14));

    public override void _Draw()
    {
        float barW = theme.Px(3);
        float gap = theme.Px(2);
        float maxH = Size.Y;
        Color activeColor = bars switch
        {
            >= 3 => ProceduralAssets.ToColor(UiTokens.Success),
            2 => ProceduralAssets.ToColor(UiTokens.Warning),
            _ => ProceduralAssets.ToColor(UiTokens.Danger),
        };
        Color dimColor = ProceduralAssets.ToColor(UiTokens.BorderStrong) with { A = 0.45f };

        for (int i = 0; i < 4; i++)
        {
            float frac = (i + 1) / 4f;
            float h = Math.Max(theme.Px(3), maxH * frac);
            float x = i * (barW + gap);
            float y = maxH - h;
            DrawRect(new Rect2(x, y, barW, h), i < bars ? activeColor : dimColor, filled: true);
        }
    }
}
