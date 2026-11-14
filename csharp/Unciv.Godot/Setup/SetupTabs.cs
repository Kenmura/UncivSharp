using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;

namespace Unciv.GodotClient.Setup;

/// <summary>
/// T414: shared setup tab builders and selection widgets used by <see cref="NewGameScreen"/>, <see cref="HostScreen"/>, and <see cref="LobbyScreen"/>
/// so single-player and multiplayer setup present one consistent visual hierarchy without duplicate page code.
/// </summary>
internal static class SetupTabs
{
    internal static AssetKey UiIcon(string name) => AssetKey.Of(RulesetAssetKeys.UiIcon, name);

    internal static AssetKey StatIcon(string name) => AssetKey.Of(RulesetAssetKeys.StatIcon, name);

    internal static void Clear(Node page)
    {
        foreach (var child in page.GetChildren())
        {
            page.RemoveChild(child);
            child.QueueFree();
        }
    }

    internal static void AddHeading(Container page, UiTheme theme, string text, AssetKey? icon = null) =>
        page.AddChild(new SectionHeader(text, theme, icon));

    internal static void AddChoice(Container page, UiTheme theme, Dictionary<string, ChoiceRow> rows, string field, string heading, IEnumerable<string> options, string selected, Action<string> chosen, Action<string?> change, AssetKey? headingIcon = null, Func<string, AssetKey?>? optionIcon = null) =>
        AddCheckedChoice(page, theme, rows, field, heading, options, selected, value =>
        {
            chosen(value);
            return null;
        }, change, headingIcon, optionIcon);

    internal static void AddCheckedChoice(Container page, UiTheme theme, Dictionary<string, ChoiceRow> rows, string field, string heading, IEnumerable<string> options, string selected, Func<string, string?> chosen, Action<string?> change, AssetKey? headingIcon = null, Func<string, AssetKey?>? optionIcon = null)
    {
        AddHeading(page, theme, heading, headingIcon);
        var row = optionIcon != null ? new ChoiceRow(options, selected, theme, optionIcon) : new ChoiceRow(options, selected);
        row.Chosen += value => change(chosen(value));
        rows[field] = row;
        page.AddChild(row);
    }

    internal static void AddCheck(Container page, string text, bool on, Action<bool> changed, Action<string?> change) =>
        AddCheckedCheck(page, text, on, value =>
        {
            changed(value);
            return null;
        }, change);

    internal static void AddCheckedCheck(Container page, string text, bool on, Func<bool, string?> changed, Action<string?> change)
    {
        var box = new CheckBox { Text = text.TrText(), ButtonPressed = on };
        box.Toggled += value => change(changed(value));
        page.AddChild(box);
    }

    internal static LineEdit AddField(Container page, UiTheme theme, string heading, string name, string text, string placeholder, int minWidth, Action<string> changed, Action onProblemRefresh, AssetKey? headingIcon = null)
    {
        AddHeading(page, theme, heading, headingIcon);
        var field = new LineEdit { Name = name, Text = text, PlaceholderText = placeholder.TrText() }.FitMin(theme, minWidth);
        field.TextChanged += value =>
        {
            changed(value);
            onProblemRefresh();
        };
        page.AddChild(field);
        return field;
    }

    internal static void AddStepper(Container parent, UiTheme theme, string label, Stepper stepper, Func<int, string?> changed, Action<string?> change)
    {
        var row = new HBoxContainer();
        row.FitConstant(theme, "separation", UiTokens.S2);
        row.AddChild(new Label { Text = label.TrText(), ThemeTypeVariation = UiVariants.LabelSecondary, VerticalAlignment = VerticalAlignment.Center });
        row.AddChild(stepper);
        stepper.Changed += value => change(changed(value));
        parent.AddChild(row);
    }

    internal static void FillGame(VBoxContainer page, UiTheme theme, NewGameForm form, Dictionary<string, ChoiceRow> rows, Action<string?> change, Action<Container>? addHostTop = null, bool includeNoBarbarians = false)
    {
        Clear(page);
        addHostTop?.Invoke(page);
        if (includeNoBarbarians)
        {
            AddCheck(page, "No barbarians", form.NoBarbarians, on => form.NoBarbarians = on, change);
        }
        var o = form.Options;
        AddChoice(page, theme, rows, "Difficulty", "Difficulty", o.Difficulties.Select(it => it.Name), form.Difficulty, v => form.Difficulty = v, change, UiIcon("Shield"));
        AddChoice(page, theme, rows, "Speed", "Game speed", o.Speeds.Select(it => it.Name), form.Speed, v => form.Speed = v, change, UiIcon("ForwardArrow"));
        AddChoice(page, theme, rows, "Era", "Starting era", o.Eras.Select(it => it.Name), form.Era, v => form.Era = v, change, UiIcon("Star"));
        AddHeading(page, theme, "Victory types", UiIcon("Score"));
        var victories = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        victories.FitConstant(theme, "h_separation", UiTokens.S4);
        page.AddChild(victories);
        foreach (string victory in form.SelectableVictories)
        {
            string name = victory;
            var item = new HBoxContainer();
            item.FitConstant(theme, "separation", UiTokens.S1);
            var icon = new TextureRect
            {
                Texture = theme.Icon(VictoryIcon(name)),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = Control.TextureFilterEnum.LinearWithMipmaps,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            }.FitMin(theme, 18, 18);
            var box = new CheckBox { Text = name.TrText(), ButtonPressed = form.Victories.Contains(name) };
            box.Toggled += value => change(form.SetVictory(name, value));
            item.AddChild(icon);
            item.AddChild(box);
            victories.AddChild(item);
        }
    }

    internal static LineEdit FillMap(VBoxContainer page, UiTheme theme, NewGameForm form, Dictionary<string, ChoiceRow> rows, Action<string?> change, Action onProblemRefresh)
    {
        Clear(page);
        var o = form.Options.MapOptions;
        AddChoice(page, theme, rows, "Type", "Map type", o.Types, form.MapType, v => form.MapType = v, change, UiIcon("Terrains"));
        AddChoice(page, theme, rows, "Shape", "Map shape", o.Shapes, form.Shape, v => form.Shape = v, change, UiIcon("Hexagon"), ShapeIcon);
        AddChoice(page, theme, rows, "Size", "Map size", o.Sizes, form.Size, v => form.Size = v, change, UiIcon("HexagonOutline"));
        AddChoice(page, theme, rows, "Resources", "Resources", o.Resources, form.Resources, v => form.Resources = v, change, UiIcon("Resources"));
        var flags = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flags.FitConstant(theme, "h_separation", UiTokens.S4);
        page.AddChild(flags);
        AddCheck(flags, "World wrap", form.WorldWrap, on => form.WorldWrap = on, change);
        AddCheck(flags, "No ancient ruins", form.NoRuins, on => form.NoRuins = on, change);
        AddCheck(flags, "No natural wonders", form.NoNaturalWonders, on => form.NoNaturalWonders = on, change);
        return AddField(page, theme, "Seed (empty for a random map)", "SeedField", form.SeedText, "random", 240, text => form.SeedText = text, onProblemRefresh, UiIcon("Search"));
    }

    internal static void FillRules(VBoxContainer page, UiTheme theme, NewGameForm form, Dictionary<string, ChoiceRow> rows, Action<string?> change, string modsHeading)
    {
        Clear(page);
        AddCheckedChoice(page, theme, rows, "Base ruleset", "Base ruleset", form.BaseRulesets, form.BaseRuleset, form.SetBaseRuleset, change, UiIcon("Mods"));
        AddHeading(page, theme, modsHeading, UiIcon("Mods"));
        if (form.AvailableMods.Count == 0)
        {
            var empty = new EmptyState(theme, UiIcon("Mods"), "No mods installed.", wrap: true);
            empty.FitMin(theme, 0, 150);
            page.AddChild(empty);
            return;
        }
        foreach (string mod in form.AvailableMods)
        {
            string name = mod;
            AddCheckedCheck(page, name, form.Mods.Contains(name), on => form.SetMod(name, on), change);
        }
    }

    internal static IconBadge NationEmblem(UiTheme theme, NewGameForm form, string nation, int size)
    {
        var colours = form.NationColours(nation);
        var fill = ProceduralAssets.ToColor(colours?.Background ?? UiTokens.Surface3);
        var glyph = ProceduralAssets.ToColor(colours?.Text ?? UiTokens.TextPrimary);
        return new IconBadge(theme, AssetKey.Of(RulesetAssetKeys.NationIcon, nation), fill, glyph, size);
    }

    internal static HFlowContainer CreateNationGrid(UiTheme theme, NewGameForm form, Dictionary<string, Button> buttons, Func<string, bool> isSelected, Func<string, bool> isDisabled, Action<string> onPick)
    {
        var grid = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.FitConstant(theme, "h_separation", UiTokens.S2);
        grid.FitConstant(theme, "v_separation", UiTokens.S2);
        foreach (string name in new[] { Constants.Random }.Concat(form.PlayableNations))
        {
            string nation = name;
            var button = new Button
            {
                ToggleMode = true,
                ButtonPressed = isSelected(nation),
                ThemeTypeVariation = UiVariants.ButtonRow,
                TooltipText = nation.TrText(),
            };
            button.FitMin(theme, 94, 80);
            button.SetMeta(IconButton.LabelMeta, nation);
            var cell = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            cell.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            cell.FitConstant(theme, "separation", UiTokens.S1);
            theme.Fit(cell, () =>
            {
                int pad = theme.Px(UiTokens.S1);
                cell.OffsetLeft = pad;
                cell.OffsetTop = pad;
                cell.OffsetRight = -pad;
                cell.OffsetBottom = -pad;
            });
            var emblem = NationEmblem(theme, form, nation, 36);
            emblem.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            cell.AddChild(emblem);
            cell.AddChild(new Label
            {
                Text = nation.TrText(),
                ThemeTypeVariation = UiVariants.LabelSmall,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            button.AddChild(cell);
            button.Disabled = isDisabled(nation);
            button.Pressed += () => onPick(nation);
            buttons[nation] = button;
            grid.AddChild(button);
        }
        return grid;
    }

    internal static Control ColourDot(UiTheme theme, Rgb colour, int size)
    {
        var dot = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }.FitMin(theme, size, size);
        dot.AddThemeStyleboxOverride("panel", ThemeBuilder.Swatch(colour, theme.Scale));
        return dot;
    }

    private static AssetKey VictoryIcon(string victory) => victory switch
    {
        "Scientific" => StatIcon("Science"),
        "Cultural" => StatIcon("Culture"),
        "Domination" => UiIcon("Shield"),
        "Diplomatic" => UiIcon("Diplomacy"),
        "Time" => UiIcon("Pause"),
        _ => UiIcon("Score"),
    };

    private static AssetKey? ShapeIcon(string shape) =>
        shape.Contains("Hex", StringComparison.OrdinalIgnoreCase) ? UiIcon("Hexagon") : UiIcon("HexagonOutline");
}
