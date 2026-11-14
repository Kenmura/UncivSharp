using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;

namespace Unciv.GodotClient.Ui;

/// <summary>
/// T413: segmented City-State Influence meter showing current influence relative to 0, 30 (Friend) and 60 (Ally) thresholds.
/// </summary>
public partial class InfluenceMeter : Control
{
    private readonly UiTheme theme;
    private InfluenceMeterSpec? spec;

    public InfluenceMeter(UiTheme uiTheme)
    {
        theme = uiTheme;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Pass;
        UpdateSize();
    }

    public InfluenceMeterSpec? Spec => spec;

    public override void _EnterTree()
    {
        theme.ScaleChanged += UpdateSize;
        UpdateSize();
    }

    public override void _ExitTree() => theme.ScaleChanged -= UpdateSize;

    public void SetSpec(InfluenceMeterSpec? value)
    {
        spec = value;
        Visible = value != null;
        UpdateSize();
        QueueRedraw();
    }

    private void UpdateSize()
    {
        CustomMinimumSize = new Vector2(theme.Px(140), theme.Px(30));
    }

    public override void _Draw()
    {
        if (spec == null || Size.X <= 4f) return;
        float w = Size.X;
        float barH = theme.Px(10);
        float barY = theme.Px(2);
        float tickH = barH + theme.Px(3);
        float borderW = Math.Max(1f, theme.Px(1));

        // Base recessed track
        DrawRect(new Rect2(0, barY, w, barH), ProceduralAssets.ToColor(UiTokens.Inset), filled: true);

        // Soft Friend (30+) and Ally (60+) background bands
        float friendX = w * spec.FriendFraction;
        float allyX = w * spec.AllyFraction;
        DrawRect(new Rect2(friendX, barY, Math.Max(0f, allyX - friendX), barH), ProceduralAssets.ToColor(UiTokens.Success) with { A = 0.16f }, filled: true);
        DrawRect(new Rect2(allyX, barY, Math.Max(0f, w - allyX), barH), ProceduralAssets.ToColor(UiTokens.Accent) with { A = 0.18f }, filled: true);

        // Active fill anchored at 0 influence
        float zeroX = w * spec.ZeroFraction;
        float valueX = w * spec.Fraction;
        Color fill = spec.Influence < 0f
            ? ProceduralAssets.ToColor(UiTokens.Danger)
            : spec.IsAlly ? ProceduralAssets.ToColor(UiTokens.Accent)
            : spec.Influence >= spec.FriendThreshold ? ProceduralAssets.ToColor(UiTokens.Success)
            : ProceduralAssets.ToColor(UiTokens.Info);
        float left = Math.Min(zeroX, valueX);
        float span = Math.Max(spec.Influence == 0f ? 0f : borderW, Math.Abs(valueX - zeroX));
        if (span > 0f)
            DrawRect(new Rect2(left, barY, span, barH), fill, filled: true);

        // Outer border
        DrawRect(new Rect2(0, barY, w, barH), ProceduralAssets.ToColor(UiTokens.BorderStrong), filled: false, width: borderW);

        // Vertical threshold ticks + labels ("0", "30", "60")
        var font = GetThemeFont("font") ?? ThemeDB.FallbackFont;
        int fontSize = theme.Px(10);
        var tickColor = ProceduralAssets.ToColor(UiTokens.TextSecondary);
        var labelColor = ProceduralAssets.ToColor(UiTokens.TextSecondary);
        var markers = new (float X, string Text, Color LineColor)[]
        {
            (zeroX, "0", tickColor),
            (friendX, "30", ProceduralAssets.ToColor(UiTokens.Success)),
            (allyX, "60", ProceduralAssets.ToColor(UiTokens.AccentHi)),
        };
        foreach (var (x, label, lineColor) in markers)
        {
            DrawLine(new Vector2(x, barY - theme.Px(1)), new Vector2(x, barY + tickH), lineColor, borderW);
            var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
            float tx = Math.Clamp(x - textSize.X / 2f, 0f, Math.Max(0f, w - textSize.X));
            float ty = barY + tickH + textSize.Y - theme.Px(1);
            DrawString(font, new Vector2(tx, ty), label, HorizontalAlignment.Left, -1, fontSize, labelColor);
        }
    }
}
