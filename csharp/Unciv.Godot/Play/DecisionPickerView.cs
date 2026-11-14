using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;
using Unciv.Utils;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C038: the modal decision picker. It shows one <see cref="PickerSpec"/> (free great person, pantheon, religion, beliefs, spies, World Congress vote, promotion): a column
/// of options per slot, the highlighted option's detail, an optional name field and checkbox, and Confirm / "Decide later". The view decides nothing: which options exist,
/// why Confirm is disabled (<see cref="DecisionPickerModel.Problem"/>) and which commands a confirmed choice sends (<see cref="DecisionPickerModel.Commands"/>) come from the model;
/// the owner submits the commands it is given in <see cref="Confirmed"/>.
/// </summary>
public partial class DecisionPickerView : Modal
{
    private readonly StatText prompt = new() { Name = "PickerPrompt", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly HBoxContainer columns = new() { Name = "PickerSlots" };
    private readonly StatText detail = new() { Name = "PickerDetail", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly LineEdit nameField = new() { Name = "PickerName", Visible = false };
    private readonly CheckBox flagBox = new() { Name = "PickerFlag", Visible = false };
    private readonly Label problem = new() { Name = "PickerProblem", ThemeTypeVariation = UiVariants.LabelSmall, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Button confirm = new() { Name = "PickerConfirm" };
    private readonly Button later = new() { Name = "PickerLater" };
    private readonly Dictionary<(int Slot, string Id), Button> optionButtons = [];
    private string?[] selected = [];
    private bool nameEdited;
    private bool interacted;
    private ScrollContainer? scroll; // T317: only on a window shorter than 480, so the footer stays on screen
    private VBoxContainer? content;
    private ReadyRef<UiTheme> themeRef;
    private UiTheme theme => themeRef.Value;

    public DecisionPickerView()
    {
        Name = "DecisionPicker";
        TitleLabel.Name = "PickerTitle";
        problem.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(UiTokens.TextSecondary));
        Body.AddChild(prompt);
        Body.AddChild(columns);
        Body.AddChild(detail);
        Body.AddChild(nameField);
        Body.AddChild(flagBox);
        Body.AddChild(problem);
        SetActions([(confirm, ChoiceRole.Primary), (later, ChoiceRole.Ghost)]);
        confirm.Pressed += () => Confirmed?.Invoke(Spec.NotNull(), DecisionPickerModel.Commands(Spec.NotNull(), Choice()));
        later.Pressed += () => Dismissed?.Invoke(Spec.NotNull());
        nameField.TextChanged += _ =>
        {
            interacted = true;
            nameEdited = true;
            Update();
        };
        flagBox.Toggled += _ =>
        {
            interacted = true;
            Update();
        };
    }

    public PickerSpec? Spec { get; private set; }

    public string TitleText => TitleLabel.Text;

    public string PromptText => prompt.Text;

    public string DetailText => detail.Text;

    public string ProblemText => problem.Text;

    public string NameText => nameField.Text;

    public Button ConfirmButton => confirm;

    public Button LaterButton => later;

    public CheckBox FlagBox => flagBox;

    public LineEdit NameField => nameField;

    public IReadOnlyList<string?> Selected => selected;

    /// <summary>Raised with the spec and the commands to send, in order.</summary>
    public event Action<PickerSpec, IReadOnlyList<GameCommand>>? Confirmed;

    /// <summary>Raised when "Decide later" / the dismiss button was pressed or Escape typed.</summary>
    public event Action<PickerSpec>? Dismissed;

    public Button? OptionButton(int slot, string id) => optionButtons.GetValueOrDefault((slot, id));

    public override void Init(UiTheme uiTheme)
    {
        themeRef.Set(uiTheme);
        base.Init(uiTheme);
        prompt.Init(uiTheme);
        detail.Init(uiTheme);
    }

    public void Open(PickerSpec spec)
    {
        Spec = spec;
        selected = new string?[spec.Slots.Count];
        nameEdited = false;
        interacted = false;
        float width = Fit(560, prompt, detail); // T328: the wrap width follows a live resize/scale change
        SetHeader(spec.Title);
        prompt.CustomMinimumSize = new Vector2(width, 0);
        prompt.Text = spec.Prompt.TrText();
        confirm.Text = spec.ConfirmLabel.TrText();
        later.Text = spec.DismissLabel.TrText();
        nameField.Visible = spec.TextLabel != null;
        nameField.PlaceholderText = (spec.TextLabel ?? "").TrText();
        nameField.Text = "";
        flagBox.Visible = spec.FlagLabel != null;
        flagBox.Text = (spec.FlagLabel ?? "").TrText();
        flagBox.SetPressedNoSignal(false);
        detail.Text = "";
        detail.CustomMinimumSize = new Vector2(width, theme.Px(48));
        optionButtons.Clear();
        foreach (var child in columns.GetChildren())
        {
            columns.RemoveChild(child);
            child.QueueFree();
        }
        for (int s = 0; s < spec.Slots.Count; s++) columns.AddChild(BuildColumn(s, spec.Slots[s]));
        Update();
        Visible = true;
        // T317: at the 640x360 minimum window the picker is taller than the screen, so only its content scrolls and Confirm / Decide later stay reachable
        FitBody(GetViewportRect().Size.Y < 400f);
    }

    /// <summary>T317: move the body between the modal body and a scroll area, preserving the direct layout (and every existing capture) on a normal window.</summary>
    private void FitBody(bool shortWindow)
    {
        Control[] items = [prompt, columns, detail, nameField, flagBox, problem];
        if (shortWindow)
        {
            if (scroll == null)
            {
                scroll = new ScrollContainer { Name = "PickerScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
                content = new VBoxContainer { Name = "PickerContent" };
                scroll.AddChild(content);
                Body.AddChild(scroll);
            }
            foreach (var item in items)
                if (item.GetParent() == Body)
                {
                    Body.RemoveChild(item);
                    content.NotNull().AddChild(item);
                }
            FitScroll(scroll.NotNull(), content.NotNull(), 0.6f);
        }
        else if (scroll is { } current)
        {
            scroll = null;
            foreach (var item in items)
                if (item.GetParent() == content)
                {
                    content.NotNull().RemoveChild(item);
                    Body.AddChild(item);
                }
            Body.RemoveChild(current);
            current.QueueFree();
            content = null;
        }
    }

    public void Close()
    {
        Spec = null;
        Visible = false;
    }

    /// <summary>Chooses an option as pressing its button does; choosing the chosen option of an optional slot clears it.</summary>
    public void Choose(int slot, string id)
    {
        if (Spec == null) return;
        var option = Spec.Slots[slot].Options.FirstOrDefault(it => it.Id == id);
        if (option == null) return;
        interacted = true;
        selected[slot] = selected[slot] == id && Spec.Slots[slot].Optional ? null : id;
        detail.Set((option.Detail.Length > 0 ? option.Detail : option.Label).TrText());
        if (Spec.TextFollowsSlot == slot && !nameEdited) nameField.Text = (DecisionPickerModel.DefaultText(Spec, Choice()));
        Update();
    }

    /// <summary>Escape is "Decide later" / the dismiss button.</summary>
    protected override bool OnCancel()
    {
        if (Spec == null) return false;
        Dismissed?.Invoke(Spec);
        return true;
    }

    private PickerChoice Choice() => new(selected, nameField.Text, flagBox.ButtonPressed);

    private Control BuildColumn(int slotIndex, PickerSlot slot)
    {
        var box = new VBoxContainer { Name = "Slot_" + slot.Id };
        box.AddChild(new Label { Text = slot.Title.TrText() });
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(theme.Px(190), theme.Px(160)) };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(list);
        box.AddChild(scroll);
        foreach (var option in slot.Options)
        {
            var button = new Button
            {
                Name = "Opt_" + slotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + option.Id,
                Text = UiText.StripGlyphs(option.Label.TrText()),
                ToggleMode = true,
                Disabled = !option.Enabled,
                Alignment = HorizontalAlignment.Left,
                TooltipText = UiText.StripGlyphs((option.Reason ?? "").TrText()),
            };
            if (option.Icon is { } iconKey)
            {
                button.Icon = theme.Icon(iconKey);
                button.ExpandIcon = true;
                button.AddThemeConstantOverride("icon_max_width", theme.Px(20));
            }
            string id = option.Id;
            string shownDetail = (option.Detail.Length > 0 ? option.Detail : option.Label).TrText();
            button.Pressed += () =>
            {
                detail.Set(shownDetail); // C100: without hover a tap shows the detail as well as choosing
                Choose(slotIndex, id);
            };
            button.MouseEntered += () => detail.Text = shownDetail;
            list.AddChild(button);
            optionButtons[(slotIndex, id)] = button;
        }
        return box;
    }

    private void Update()
    {
        if (Spec == null) return;
        foreach (var ((slot, id), button) in optionButtons) button.SetPressedNoSignal(selected[slot] == id);
        string? why = DecisionPickerModel.Problem(Spec, Choice());
        problem.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(interacted && why != null ? UiTokens.DangerText : UiTokens.TextSecondary));
        problem.Text = UiText.StripGlyphs((why ?? "").TrText());
        confirm.Disabled = why != null;
    }
}
