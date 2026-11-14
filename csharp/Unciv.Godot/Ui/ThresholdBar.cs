using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;

namespace Unciv.GodotClient.Ui;

/// <summary>
/// T413: vote progress bar with a crisp threshold marker (used on the World Congress result screen to show each civ's votes against the World Leader target).
/// </summary>
public partial class ThresholdBar : Control
{
    private readonly UiTheme theme;
    private int current;
    private int threshold = 1;
    private bool met;

    public ThresholdBar(UiTheme uiTheme)
    {
        theme = uiTheme;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
        UpdateSize();
    }

    public override void _EnterTree()
    {
        theme.ScaleChanged += UpdateSize;
        UpdateSize();
    }

    public override void _ExitTree() => theme.ScaleChanged -= UpdateSize;

    public void SetValues(int votes, int votesNeeded)
    {
        current = Math.Max(0, votes);
        threshold = Math.Max(1, votesNeeded);
        met = current >= threshold;
        UpdateSize();
        QueueRedraw();
    }

    private void UpdateSize() => CustomMinimumSize = new Vector2(theme.Px(80), theme.Px(12));

    public override void _Draw()
    {
        float w = Size.X;
        float h = Size.Y;
        if (w <= 2f || h <= 2f) return;

        float borderW = Math.Max(1f, theme.Px(1));
        float barY = theme.Px(2);
        float barH = Math.Max(4f, h - theme.Px(4));
        float maxVal = Math.Max(threshold * 1.15f, current);
        float fillW = Math.Clamp(current / maxVal, 0f, 1f) * w;
        float markX = Math.Clamp(threshold / maxVal, 0f, 1f) * w;

        DrawRect(new Rect2(0, barY, w, barH), ProceduralAssets.ToColor(UiTokens.Inset), filled: true);
        if (fillW > 0f)
        {
            Color fill = ProceduralAssets.ToColor(met ? UiTokens.Accent : UiTokens.Info);
            DrawRect(new Rect2(0, barY, fillW, barH), fill, filled: true);
        }
        DrawRect(new Rect2(0, barY, w, barH), ProceduralAssets.ToColor(UiTokens.BorderStrong), filled: false, width: borderW);
        DrawLine(new Vector2(markX, 0f), new Vector2(markX, h), ProceduralAssets.ToColor(UiTokens.AccentHi), Math.Max(2f, borderW));
    }
}
