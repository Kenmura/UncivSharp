using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;
using Unciv.Queries.Empire;
using Unciv.Utils;

namespace Unciv.GodotClient.Play;

/// <summary>
/// T415: shared inline espionage assignment UI used by both <see cref="EspionageScreen"/> and the Overview Espionage tab.
/// Shows the player's spies in one <see cref="DataTable"/>; pressing Move on a spy expands only that spy's valid destinations inline underneath.
/// </summary>
public sealed class EspionageBoard
{
    private readonly UiTheme theme;
    private readonly Action<GameCommand> send;
    private readonly Action<string> onMessage;
    private readonly Dictionary<string, Button> moveButtons = [];
    private readonly Dictionary<string, Button> coupButtons = [];
    private readonly Dictionary<string, Button> hereButtons = [];
    private readonly VBoxContainer root = new() { Name = "EspionageBoard", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    private SpyDestinationsResult? result;
    private EspionageSpec? spec;
    private DataTable? spyTable;
    private string? selectedSpy;

    public EspionageBoard(UiTheme theme, Action<GameCommand> send, Action<string> onMessage)
    {
        this.theme = theme;
        this.send = send;
        this.onMessage = onMessage;
        root.AddThemeConstantOverride("separation", theme.Px(UiTokens.S3));
    }

    public Control Root => root;

    public string? SelectedSpy
    {
        get => selectedSpy;
        set => selectedSpy = value;
    }

    public DataTable? SpyTable => spyTable;

    public EspionageSpec? Spec => spec;

    public Button? MoveButton(string spy) => moveButtons.GetValueOrDefault(spy);

    public Button? CoupButton(string spy) => coupButtons.GetValueOrDefault(spy);

    /// <summary>The "Move here" button of a valid destination (city id, or empty string for Hideout) when a spy is selected for moving.</summary>
    public Button? HereButton(string cityId) => hereButtons.GetValueOrDefault(cityId);

    /// <summary>Updates the model from <paramref name="answer"/> and redraws into <see cref="Root"/>.</summary>
    public string? Render(SpyDestinationsResult? answer, bool showHeader = true)
    {
        result = answer;
        spec = answer == null ? null : EspionageModel.Build(answer);
        if (selectedSpy != null && (answer == null || EspionageModel.Targets(answer, selectedSpy).Count == 0))
            selectedSpy = null;
        return Rebuild(showHeader);
    }

    private string? Rebuild(bool showHeader)
    {
        foreach (var child in root.GetChildren())
        {
            root.RemoveChild(child);
            child.QueueFree();
        }
        moveButtons.Clear();
        coupButtons.Clear();
        hereButtons.Clear();
        spyTable = null;
        if (spec == null || result == null)
        {
            const string off = "Espionage is not enabled in this game.";
            root.AddChild(new EmptyState(theme, null, off));
            return off;
        }
        onMessage(selectedSpy == null ? "" : $"Choose where {selectedSpy} goes.".TrText());
        root.AddChild(BuildSpies(spec, showHeader));
        if (selectedSpy != null)
            root.AddChild(BuildDestinations(spec, result, selectedSpy));
        return null;
    }

    private Control BuildSpies(EspionageSpec page, bool showHeader)
    {
        var box = new VBoxContainer { Name = "SpyTable" };
        box.AddThemeConstantOverride("separation", theme.Px(UiTokens.S1));
        if (showHeader) box.AddChild(new SectionHeader("Spies"));
        if (page.Empty.Length > 0)
        {
            box.AddChild(new EmptyState(theme, null, page.Empty));
            return box;
        }
        var table = new DataTable([new DataColumn("Spy"), new DataColumn("Spy rank", true), new DataColumn("Location"), new DataColumn("Action")], actions: true) { Name = "SpyData" };
        spyTable = table;
        table.SetRows(page.Spies.Select(it => (IReadOnlyList<string>)
        [
            it.Name,
            $"{EspionageModel.RankIcons(it.RankValue)} {it.Rank}",
            it.Location.TrText(),
            it.TurnsRemaining > 0 ? EspionageModel.FormatAction(it.Action.TrText(), it.TurnsRemaining) : it.Action.TrText(),
        ]).ToList(), index =>
        {
            var spy = page.Spies[index];
            var actions = new HBoxContainer { Name = $"SpyActions_{spy.Name}" };
            actions.AddThemeConstantOverride("separation", theme.Px(UiTokens.S1));
            var move = new Button { Name = $"Move_{spy.Name}", Text = (selectedSpy == spy.Name ? "Cancel" : "Move").Tr(), Disabled = !spy.CanMove };
            string name = spy.Name;
            move.Pressed += () =>
            {
                selectedSpy = selectedSpy == name ? null : name;
                Rebuild(showHeader);
            };
            moveButtons[spy.Name] = move;
            actions.AddChild(move);
            if (spy.Coup is { } coup)
            {
                var button = OverviewScreen.CoupButton(theme, spy.Name, spy.CoupShort, spy.CoupLabel, danger: coup.Stage);
                button.Name = $"Coup_{spy.Name}";
                button.Pressed += () => send(coup);
                coupButtons[spy.Name] = button;
                actions.AddChild(button);
            }
            return actions;
        });
        box.AddChild(table);
        return box;
    }

    private Control BuildDestinations(EspionageSpec page, SpyDestinationsResult answer, string spyName)
    {
        var box = new VBoxContainer { Name = "LocationTable" };
        box.AddThemeConstantOverride("separation", theme.Px(UiTokens.S1));
        box.AddChild(new SectionHeader($"Move {spyName} to"));
        var targets = EspionageModel.Targets(answer, spyName);
        var offered = page.Locations.Select(loc => (Row: loc, Target: EspionageModel.TargetFor(targets, loc))).Where(it => it.Target != null).ToList();
        if (offered.Count == 0)
        {
            box.AddChild(new EmptyState(theme, null, "No valid destinations for this spy."));
            return box;
        }
        var table = new DataTable([new DataColumn("City"), new DataColumn("Civilization"), new DataColumn("Spy present")], actions: true) { Name = "LocationData" };
        table.SetRows(offered.Select(it => (IReadOnlyList<string>)[it.Row.Name.TrText(), it.Row.Civ.TrText(), it.Row.SpiesPresent]).ToList(), index =>
        {
            var (location, target) = offered[index];
            var button = new Button { Name = "Here_" + (location.CityId ?? ""), Text = "Move here".Tr() };
            button.Pressed += () =>
            {
                selectedSpy = null;
                send(target.NotNull().Command);
            };
            hereButtons[location.CityId ?? ""] = button;
            return button;
        });
        box.AddChild(table);
        return box;
    }
}
