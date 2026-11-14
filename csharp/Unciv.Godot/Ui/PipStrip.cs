using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;

namespace Unciv.GodotClient.Ui;

/// <summary>
/// T413: compact sequence of discrete progress/attempt/step pips (policy branch completion, tutorial steps, reconnect attempts).
/// </summary>
public partial class PipStrip : Control
{
    private readonly UiTheme theme;
    private readonly int pipWidth;
    private readonly int pipHeight;
    private readonly int gap;
    private readonly bool circle;
    private Color fillColour;
    private int filled;
    private int total;
    private int activeIndex = -1;

    public PipStrip(UiTheme uiTheme, Color? fill = null, int pipWidth = 8, int pipHeight = 8, int gap = 4, bool circle = true)
    {
        theme = uiTheme;
        this.pipWidth = pipWidth;
        this.pipHeight = pipHeight;
        this.gap = gap;
        this.circle = circle;
        fillColour = fill ?? ProceduralAssets.ToColor(UiTokens.Accent);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public int Filled => filled;
    public int Total => total;
    public int ActiveIndex => activeIndex;

    public override void _EnterTree()
    {
        theme.ScaleChanged += UpdateSize;
        UpdateSize();
    }

    public override void _ExitTree() => theme.ScaleChanged -= UpdateSize;

    public void SetPips(int filledCount, int totalCount, int active = -1, Color? fill = null)
    {
        filled = Math.Max(0, filledCount);
        total = Math.Max(0, totalCount);
        activeIndex = active;
        if (fill is { } colour) fillColour = colour;
        UpdateSize();
        QueueRedraw();
    }

    private void UpdateSize()
    {
        if (total <= 0)
        {
            CustomMinimumSize = Vector2.Zero;
            return;
        }
        float w = total * theme.Px(pipWidth) + Math.Max(0, total - 1) * theme.Px(gap);
        float h = theme.Px(pipHeight) + 2f;
        CustomMinimumSize = new Vector2(w, h);
    }

    public override void _Draw()
    {
        if (total <= 0) return;
        float w = theme.Px(pipWidth);
        float h = theme.Px(pipHeight);
        float g = theme.Px(gap);
        float y = (Size.Y - h) / 2f;
        float borderWidth = Math.Max(1f, theme.Px(1));
        var emptyBg = ProceduralAssets.ToColor(UiTokens.Inset);
        var border = ProceduralAssets.ToColor(UiTokens.BorderStrong);
        var activeRing = ProceduralAssets.ToColor(UiTokens.AccentHi);

        for (int i = 0; i < total; i++)
        {
            float x = i * (w + g);
            bool isFilled = i < filled;
            bool isActive = i == activeIndex;
            var rect = new Rect2(x, y, w, h);

            if (circle && pipWidth == pipHeight)
            {
                var centre = rect.GetCenter();
                float radius = Math.Min(w, h) / 2f;
                DrawCircle(centre, radius, isFilled ? fillColour : emptyBg);
                DrawArc(centre, radius - borderWidth / 2f, 0f, Mathf.Tau, 16, isActive ? activeRing : isFilled ? fillColour : border, borderWidth, antialiased: true);
            }
            else
            {
                DrawRect(rect, isFilled ? fillColour : emptyBg, filled: true);
                DrawRect(rect, isActive ? activeRing : isFilled ? fillColour : border, filled: false, width: borderWidth);
            }
        }
    }
}
