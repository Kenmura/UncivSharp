using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Ui;

namespace Unciv.GodotHarness;

/// <summary>
/// C079: the rectangle checks every responsive and localization run asserts, from the real controls and not from pixels: no visible text control leaves
/// the window, no text is cut off without an ellipsis, and no two text controls that are not part of each other overlap. Controls scrolled out of
/// view (clipped by a scroll container or a clipping parent) do not count: only what the player can see does.
/// </summary>
internal static class LayoutChecks
{
    private const float Tolerance = 1.5f;

    /// <summary>A script step asserting <see cref="Problems"/> is empty under <paramref name="root"/> in the current window; the problems are printed.</summary>
    public static ScenarioStep Assert(string what, Control host, Func<Node> root) => new AssertQuery(what, () =>
    {
        var node = root();
        var problems = Problems(node, new Rect2(Vector2.Zero, host.GetViewport().GetVisibleRect().Size));
        foreach (string problem in problems.Take(12)) GD.PrintErr($"[layout] {what}: {problem.Replace("\n", "\\n")}");
        return problems.Count == 0;
    });

    /// <summary>
    /// The steps that run a screen through the window sizes: from 640x480 (only when <paramref name="wideCaption"/> is given; otherwise the screen is already at 1280x720) to 1280x720 and 1920x1080, each with the rectangle asserts (and <paramref name="extra"/>
    /// when given) and a golden (<c>wide</c> unless <paramref name="wideCaption"/> is null, <c>fhd</c>), then back to 640x480 where the screen must still fit.
    /// </summary>
    public static IEnumerable<ScenarioStep> Widths(Control host, string screen, Func<Node> root, string? wideCaption, string fhdCaption, Func<bool>? extra = null)
    {
        ScenarioStep Check(string size) => Assert($"at {size} no {screen} text is cut off, leaves the window or overlaps another", host, root);
        ScenarioStep More(string size) => new AssertQuery($"at {size} the {screen} is intact", extra ?? (() => true));
        ScenarioStep[] Resize(Vector2I size) => [new Act($"resize the window to {size.X}x{size.Y}", () => host.GetWindow().Size = size), new WaitFrames(size.X < 1000 ? 8 : 6)];
        var steps = new List<ScenarioStep>();
        if (wideCaption != null)
        {
            steps.Add(Check("640x480"));
            steps.AddRange(Resize(new Vector2I(1280, 720)));
            steps.AddRange([More("1280x720"), Check("1280x720"), new Capture("wide", wideCaption)]);
        }
        else steps.AddRange([More("1280x720"), Check("1280x720")]);
        steps.AddRange(Resize(new Vector2I(1920, 1080)));
        steps.AddRange([More("1920x1080"), Check("1920x1080"), new Capture("fhd", fhdCaption)]);
        steps.AddRange(Resize(new Vector2I(640, 480)));
        steps.AddRange([More("640x480"), Check("640x480 again")]);
        return steps;
    }

    /// <summary>T284/T286: a step asserting every visible button under <paramref name="root"/> is at least <paramref name="minTargetPx"/> tall (a touch target) unless <paramref name="exempt"/> excuses it; the offenders are printed.</summary>
    public static ScenarioStep Touchable(string tag, string what, int minTargetPx, Func<Node> root, Func<BaseButton, bool>? exempt = null) => new AssertQuery($"every visible button of the {what} is at least 48 dp ({minTargetPx} px) tall", () =>
    {
        var bad = Buttons(root()).Where(it => !(exempt?.Invoke(it) ?? false) && it.IsVisibleInTree() && it.Size.Y > 0 && it.Size.Y + 0.5f < minTargetPx).Select(it => $"{it.GetType().Name} '{(it as Button)?.Text}' {it.Name} {it.Size.Y}").ToList();
        foreach (string line in bad.Take(12)) GD.PrintErr($"[{tag}] {what}: undersized {line}");
        return bad.Count == 0;
    });

    /// <summary>T284/T286: a step asserting no scroll container under <paramref name="root"/> hides content on an axis it cannot scroll.</summary>
    public static ScenarioStep Reachable(string tag, string what, Func<Node> root) => new AssertQuery($"nothing of the {what} is cut off by a container that cannot scroll", () =>
    {
        var bad = Unscrollable(root()).ToList();
        foreach (string line in bad.Take(12)) GD.PrintErr($"[{tag}] {what}: {line}");
        return bad.Count == 0;
    });

    /// <summary>Scroll containers whose content is larger than they are on an axis they do not scroll: what lies beyond is unreachable for a finger.</summary>
    private static IEnumerable<string> Unscrollable(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is ScrollContainer { Visible: true } scroll && scroll.IsVisibleInTree() && scroll.GetChildCount() > 0 && scroll.GetChild(0) is Control content)
            {
                var need = content.GetCombinedMinimumSize();
                if (scroll.VerticalScrollMode == ScrollContainer.ScrollMode.Disabled && need.Y > scroll.Size.Y + 1.5f) yield return $"{scroll.Name} needs {need.Y} high, has {scroll.Size.Y}";
                if (scroll.HorizontalScrollMode == ScrollContainer.ScrollMode.Disabled && need.X > scroll.Size.X + 1.5f) yield return $"{scroll.Name} needs {need.X} wide, has {scroll.Size.X}";
            }
            foreach (string nested in Unscrollable(child)) yield return nested;
        }
    }

    /// <summary>
    /// T312: the target problems under <paramref name="root"/>: a visible button shorter than <paramref name="minPx"/> (too small to hit) or taller than <paramref name="maxPx"/> (a huge widget), unless <paramref name="exempt"/> excuses it.
    /// </summary>
    public static List<string> TargetProblems(Node root, int minPx, int maxPx, Func<BaseButton, bool>? exempt = null) => Buttons(root)
        .Where(it => !(exempt?.Invoke(it) ?? false) && it.IsVisibleInTree() && it.Size.Y > 0 && Visible(it) != null && (it.Size.Y + 0.5f < minPx || it.Size.Y > maxPx))
        .Select(it => $"{it.GetType().Name} '{(it as Button)?.Text}' {it.Name} is {it.Size.X}x{it.Size.Y} (allowed height {minPx}..{maxPx})").ToList();

    /// <summary>
    /// T329: the shared widgets under <paramref name="root"/> whose fixed sizes no longer match the live theme: a StatChip's separation and badge, a StatBar's thickness,
    /// an IconLabel's icon and a ListRow's icon. They follow the theme through <see cref="UiTheme.Fit"/>; this checks the result after a live resize or scale change.
    /// </summary>
    public static List<string> SharedWidgetProblems(Node root, UiTheme theme)
    {
        var problems = new List<string>();
        void Expect(string what, float actual, float expected)
        {
            if (Math.Abs(actual - expected) > 0.5f) problems.Add($"{what} is {actual}, the live scale gives {expected}");
        }
        void Walk(Node node)
        {
            switch (node)
            {
                case StatChip chip:
                    Expect("StatChip separation", chip.GetThemeConstant("separation"), theme.Px(UiTokens.S1));
                    if (chip.GetChildren().OfType<IconBadge>().FirstOrDefault() is { } badge) Expect("StatChip badge", badge.CustomMinimumSize.X, theme.Px(20));
                    break;
                case StatBar bar:
                    float height = bar.Bar.CustomMinimumSize.Y;
                    if (Math.Abs(height - theme.Px(UiTokens.BarLabelled)) > 0.5f && Math.Abs(height - theme.Px(UiTokens.BarThin)) > 0.5f)
                        problems.Add($"StatBar height is {height}, the live scale gives {theme.Px(UiTokens.BarThin)} or {theme.Px(UiTokens.BarLabelled)}");
                    break;
                case IconLabel label:
                    if (label.GetChildren().OfType<TextureRect>().FirstOrDefault() is { } picture) Expect("IconLabel icon", picture.CustomMinimumSize.X, theme.Px(UiTheme.IconSize));
                    break;
                case ListRow row:
                    if (row.FindChildren("*", "", true, false).OfType<TextureRect>().FirstOrDefault() is { } icon) Expect("ListRow icon", icon.CustomMinimumSize.X, theme.Px(UiTheme.IconSize));
                    break;
            }
            foreach (var child in node.GetChildren()) Walk(child);
        }
        Walk(root);
        return problems;
    }

    /// <summary>
    /// T329: the shared widgets that are actually visible under <paramref name="root"/>. The live-resize phases assert these counts before trusting
    /// <see cref="SharedWidgetProblems"/>: a root that does not contain the widgets (an emptied <c>HudView</c>, a tech screen built without a selection)
    /// would make that check pass without testing anything.
    /// </summary>
    public static (int Chips, int Bars, int Icons, int Rows, int Cards) VisibleWidgets(Node root)
    {
        int chips = 0, bars = 0, icons = 0, rows = 0, cards = 0;
        void Walk(Node node)
        {
            if (node is CanvasItem item && !item.IsVisibleInTree()) return;
            switch (node)
            {
                case StatChip: chips++; break;
                case StatBar: bars++; break;
                case IconLabel: icons++; break;
                case ListRow: rows++; break;
                case TechNodeCard: cards++; break;
            }
            foreach (var child in node.GetChildren()) Walk(child);
        }
        Walk(root);
        return (chips, bars, icons, rows, cards);
    }

    /// <summary>T322: the rows wider than <paramref name="maxPx"/> (plus the rounding tolerance): a form row that spans the window leaves its label far from its control.</summary>
    public static List<string> WidthProblems(IEnumerable<Control> rows, int maxPx) => rows
        .Where(it => it.Size.X > maxPx + Tolerance)
        .Select(it => $"{it.GetType().Name} '{it.Name}' is {it.Size.X} wide, more than the {maxPx} px form width cap")
        .ToList();

    /// <summary>T333: the content column of the frame under <paramref name="root"/> is no wider than <paramref name="maxPx"/>: a cap in logical pixels that grew with the whole automatic scale would let a 4K window's content span the window.</summary>
    public static List<string> ContentWidthProblems(Node root, float maxPx)
    {
        if (Find<ScreenFrame>(root) is not { } frame) return [];
        float width = frame.ContentColumnRect.Size.X;
        return width <= maxPx + Tolerance ? [] : [$"the content column is {width:0} px wide, more than the {maxPx:0} px content cap"];
    }

    /// <summary>
    /// T333: the visible pairs whose horizontal gap between the left control's drawn label and the right control is wider than <paramref name="maxGap"/>.
    /// The left extent is its position plus its combined minimum size (its own minimum plus <c>CustomMinimumSize</c>), not its rectangle end: a stretched row
    /// button draws its left-aligned label at its start, so a rectangle-end measurement is always one separation wide and misses the label that sits far from
    /// the control beside it (the bug this check exists for).
    /// </summary>
    public static List<string> GapProblems(IEnumerable<(Control Left, Control Right, string What)> pairs, float maxGap) => pairs
        .Where(it => it.Left.IsVisibleInTree() && it.Right.IsVisibleInTree())
        .Select(it => (it.What, Gap: it.Right.GetGlobalRect().Position.X - (it.Left.GetGlobalRect().Position.X + it.Left.GetCombinedMinimumSize().X)))
        .Where(it => it.Gap > maxGap + Tolerance)
        .Select(it => $"{it.What}: {it.Gap:0} px gap, more than the {maxGap:0} px allowed")
        .ToList();

    /// <summary>T317: the share of the usable width (the window, or the content cap on a wider window) a full-screen frame must fill before its empty side margins count as wasted space.</summary>
    public const float MinContentShare = 0.85f;

    /// <summary>
    /// T317: the automatic wasted-space check. A full-screen screen (a <see cref="ScreenFrame"/>) whose content column is narrower than <paramref name="minShare"/> of the usable width
    /// leaves an empty band down the side, so the layout wasted the window. The usable width is the window, capped at <paramref name="contentCapPx"/>: above the cap the side margins
    /// are deliberate, and the content only has to fill the cap.
    /// </summary>
    public static List<string> WastedSpaceProblems(Node root, Rect2 window, float contentCapPx, float minShare = MinContentShare)
    {
        var frame = Find<ScreenFrame>(root);
        if (frame == null) return [];
        float usable = MathF.Min(window.Size.X, contentCapPx);
        float content = frame.ContentColumnRect.Size.X;
        if (usable <= 0f || content + Tolerance >= usable * minShare) return [];
        return [$"the content column is {content:0} px wide, less than {minShare:P0} of the {usable:0} px usable width (window {window.Size.X:0} px, cap {contentCapPx:0} px): wasted side space"];
    }

    /// <summary>
    /// T363: the transient status line under a <see cref="GameScreen"/> is fully readable (inside the window, not clipped) and clear of the bottom-left
    /// unit panel and the bottom-right next-action dock. The BNW re-record showed it running under the dock; this keeps that from coming back.
    /// </summary>
    public static List<string> StatusProblems(GameScreen screen)
    {
        var problems = new List<string>();
        if (screen.FindChild("StatusPanel", true, false) is not Control panel || !panel.IsVisibleInTree())
            return ["the status line is not shown"];
        var window = new Rect2(Vector2.Zero, screen.GetViewport().GetVisibleRect().Size);
        problems.AddRange(Problems(panel, window));
        foreach ((string what, Control other) in new (string, Control)[] { ("next-action dock", screen.Hud.Dock), ("unit panel", screen.Panel) })
        {
            if (!other.IsVisibleInTree() || other.Size.X <= 0f) continue;
            var overlap = panel.GetGlobalRect().Intersection(other.GetGlobalRect());
            if (overlap.Size.X > Tolerance && overlap.Size.Y > Tolerance)
                problems.Add($"the status line {panel.GetGlobalRect()} overlaps the {what} {other.GetGlobalRect()}");
        }
        return problems;
    }

    /// <summary>The script step asserting <see cref="StatusProblems"/> is empty.</summary>
    public static ScenarioStep StatusClear(string what, GameScreen screen) => new AssertQuery(what, () =>
    {
        var problems = StatusProblems(screen);
        foreach (string problem in problems.Take(12)) GD.PrintErr($"[status] {what}: {problem}");
        return problems.Count == 0;
    });

    private static T? Find<T>(Node root) where T : Node
    {
        if (root is T found) return found;
        foreach (var child in root.GetChildren())
            if (Find<T>(child) is { } nested) return nested;
        return null;
    }

    private static IEnumerable<BaseButton> Buttons(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is BaseButton button) yield return button;
            foreach (var nested in Buttons(child)) yield return nested;
        }
    }

    /// <summary>The problems found under <paramref name="root"/> inside a window of <paramref name="window"/> (empty when the layout is sound).</summary>
    public static List<string> Problems(Node root, Rect2 window)
    {
        var problems = new List<string>();
        var seen = new List<(Control Control, Rect2 Rect)>();
        Walk(root, window, problems, seen);
        for (int i = 0; i < seen.Count; i++)
            for (int j = i + 1; j < seen.Count; j++)
            {
                var (a, b) = (seen[i], seen[j]);
                if (a.Control.IsAncestorOf(b.Control) || b.Control.IsAncestorOf(a.Control)) continue;
                var overlap = a.Rect.Intersection(b.Rect);
                if (overlap.Size.X > Tolerance && overlap.Size.Y > Tolerance)
                    problems.Add($"{Describe(a.Control)} {a.Rect} overlaps {Describe(b.Control)} {b.Rect}");
            }
        return problems;
    }

    private static void Walk(Node node, Rect2 window, List<string> problems, List<(Control, Rect2)> seen)
    {
        if (node is Control { Visible: false }) return;
        if (node is Control control && TextOf(control) is { Length: > 0 } && control.IsVisibleInTree())
        {
            if (Visible(control) is { } rect)
            {
                seen.Add((control, rect));
                var full = control.GetGlobalRect();
                if (!window.Grow(Tolerance).Encloses(rect)) problems.Add($"{Describe(control)} {full} leaves the window {window}");
                else if (Clipped(control) is { } cut) problems.Add($"{Describe(control)} {full} cuts its text off without an ellipsis: {cut}");
            }
        }
        foreach (var child in node.GetChildren()) Walk(child, window, problems, seen);
    }

    /// <summary>The part of the control that shows: its rectangle cut by every scrolling or clipping ancestor; null when nothing of it shows or it is only partly scrolled into view.</summary>
    private static Rect2? Visible(Control control)
    {
        var rect = control.GetGlobalRect();
        for (var parent = control.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent is Control p && (p is ScrollContainer || p.ClipContents))
            {
                var clip = p.GetGlobalRect();
                if (!clip.Intersects(rect)) return null;
                if (!clip.Encloses(rect)) return null; // partly scrolled out: not judged until it is fully in view
            }
        }
        return rect;
    }

    private static string? TextOf(Control control) => control switch
    {
        Label label => label.Text,
        Button button => button.Text,
        _ => null,
    };

    private static string? Clipped(Control control)
    {
        switch (control)
        {
            case Label label when label.AutowrapMode == TextServer.AutowrapMode.Off && label.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming:
                float need = label.Text.Split('\n').Max(line => label.GetThemeFont("font").GetStringSize(line, HorizontalAlignment.Left, -1, label.GetThemeFontSize("font_size")).X);
                return label.Size.X + Tolerance < need ? $"needs {need} wide" : null;
            case Label label when label.AutowrapMode != TextServer.AutowrapMode.Off:
                return label.GetLineCount() > 0 && label.GetVisibleLineCount() < label.GetLineCount() && label.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming ? "lines hidden" : null;
            case Button button when button.ClipText && button.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming:
                var font = button.GetThemeFont("font");
                float width = font.GetStringSize(button.Text, HorizontalAlignment.Left, -1, button.GetThemeFontSize("font_size")).X;
                return width > button.Size.X + Tolerance ? $"needs {width} wide" : null;
            default:
                return null;
        }
    }

    private static string Describe(Control control) => $"{control.GetType().Name} \"{TextOf(control)}\" ({control.GetPath().ToString().Split('/').TakeLast(3).Aggregate((x, y) => x + "/" + y)})";
}
