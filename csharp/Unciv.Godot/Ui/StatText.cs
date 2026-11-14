using Godot;
using Unciv.Assets;

namespace Unciv.GodotClient.Ui;

/// <summary>
/// C065: a line of text whose Unciv stat glyphs (food, gold, ...) are drawn as the stat icons, inline at the text height, and whose signed numbers can be
/// coloured by sign. A plain Label cannot do either, so views that show "3 ¤ +2" use this.
/// </summary>
public partial class StatText : RichTextLabel
{
    private UiTheme? theme;
    private string pendingText = "";

    public StatText()
    {
        BbcodeEnabled = true;
        FitContent = true;
        ScrollActive = false;
        AutowrapMode = TextServer.AutowrapMode.Off;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public StatText(UiTheme theme, string text = "") : this()
    {
        Init(theme);
        Set(text);
    }

    /// <summary>Assigns the UI theme when constructed lazily before Init (for example in Picker fields).</summary>
    public void Init(UiTheme uiTheme)
    {
        theme = uiTheme;
        if (pendingText.Length > 0) Set(pendingText);
    }

    /// <summary>The stats whose icons the current text shows, in order.</summary>
    public IReadOnlyList<string> Stats { get; private set; } = [];

    /// <summary>The raw string assigned to this StatText (get/set forwards to <see cref="Set"/>).</summary>
    public new string Text
    {
        get => pendingText;
        set => Set(value);
    }

    /// <summary>Replaces the text: each stat glyph becomes its icon; other characters stay as they are (no markup is interpreted in <paramref name="text"/>).</summary>
    public void Set(string text)
    {
        pendingText = text;
        Clear();
        var stats = new List<string>();
        if (theme == null)
        {
            AddText(UiText.StripGlyphs(text));
            Stats = stats;
            return;
        }
        AppendRuns(this, theme, text, stats);
        Stats = stats;
    }

    /// <summary>Appends plain runs and inline stat icons onto <paramref name="target"/> using <paramref name="uiTheme"/> scaling.</summary>
    public static void AppendRuns(RichTextLabel target, UiTheme uiTheme, string text, ICollection<string>? stats = null)
    {
        int size = uiTheme.Px(UiTheme.IconSize) * 2 / 3;
        foreach (var run in UiText.Runs(text))
        {
            if (run.Stat is { } stat)
            {
                target.AddImage(uiTheme.Icon(AssetKey.Of(RulesetAssetKeys.StatIcon, stat)), size, size);
                stats?.Add(stat);
            }
            else
            {
                target.AddText(run.Text);
            }
        }
    }

    /// <summary>Renders Civilopedia BBCode while substituting any inline Unciv font markers inside text spans with stat icons.</summary>
    public static void AppendBbcode(RichTextLabel target, UiTheme uiTheme, string bbcode)
    {
        var tags = new Stack<string>();
        int index = 0;
        while (index < bbcode.Length)
        {
            int open = bbcode.IndexOf('[', index);
            if (open < 0)
            {
                AppendRuns(target, uiTheme, bbcode[index..]);
                break;
            }
            if (open > index) AppendRuns(target, uiTheme, bbcode[index..open]);
            int close = bbcode.IndexOf(']', open + 1);
            if (close < 0)
            {
                AppendRuns(target, uiTheme, bbcode[open..]);
                break;
            }
            string tag = bbcode[(open + 1)..close];
            if (tag == "lb") target.AddText("[");
            else if (tag == "rb") target.AddText("]");
            else if (tag.StartsWith('/'))
            {
                if (tags.Count > 0 && tags.Peek() == tag[1..])
                {
                    tags.Pop();
                    target.Pop();
                }
            }
            else if (PushTag(target, tag, out string tagName))
            {
                tags.Push(tagName);
            }
            else
            {
                target.AddText(bbcode[open..(close + 1)]);
            }
            index = close + 1;
        }
        while (tags.Count > 0)
        {
            tags.Pop();
            target.Pop();
        }
    }

    private static bool PushTag(RichTextLabel target, string tag, out string tagName)
    {
        if (tag == "b") { target.PushBold(); tagName = "b"; return true; }
        if (tag == "i") { target.PushItalics(); tagName = "i"; return true; }
        if (tag == "center") { target.PushParagraph(HorizontalAlignment.Center); tagName = "center"; return true; }
        if (tag.StartsWith("url=", StringComparison.Ordinal)) { target.PushMeta(tag[4..]); tagName = "url"; return true; }
        if (tag.StartsWith("color=", StringComparison.Ordinal)) { target.PushColor(Unciv.GodotClient.Assets.ProceduralAssets.FromHex(tag.AsSpan(6))); tagName = "color"; return true; }
        if (tag.StartsWith("font_size=", StringComparison.Ordinal) && int.TryParse(tag.AsSpan(10), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int px))
        {
            target.PushFontSize(px);
            tagName = "font_size";
            return true;
        }
        tagName = "";
        return false;
    }

    /// <summary>Appends a signed number coloured by its sign (<see cref="UiText.Delta"/>).</summary>
    public void AppendDelta(int value) => AppendText(UiText.Delta(value));
}
