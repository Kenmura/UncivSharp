using System.Globalization;
using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.Commands.Events;
using Unciv.Commands.Turn;
using Unciv.Commands.Units;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Map;
using Unciv.GodotClient.Settings;
using Unciv.GodotClient.Ui;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models;
using Unciv.Models.Translations;
using Unciv.Net.Visibility;
using Unciv.Queries.Empire;
using Unciv.Queries.Units;
using Unciv.Utils;

namespace Unciv.GodotClient.Play;

public partial class GameScreen
{
    private async Task RightClickMove(HexCoord tile)
    {
        await ShowPreview(tile);
        if (PreviewTarget == tile) await MoveSelectedTo();
    }

    private List<UnitNode> OwnUnitsAt(HexCoord tile) =>
        unitNodes.Values.Where(it => it.IsOwn && it.Unit.X == tile.X && it.Unit.Y == tile.Y).OrderBy(it => it.Unit.Id).ToList();

    /// <summary>Selects one of the viewer's units by id (what clicking it does); an unknown or foreign id selects nothing.</summary>
    public void SelectUnitById(int id)
    {
        if (unitNodes.TryGetValue(id, out var node) && node.IsOwn) SelectUnit(node);
    }

    /// <summary>C091: shift-click on a tile with own units: adds the first of them that is not in the selection, or (when all are) removes them.</summary>
    private void ToggleGroup(List<UnitNode> here)
    {
        if (SelectedUnitId is not { } primary)
        {
            SelectUnit(here[0]);
            return;
        }
        IReadOnlyList<int> members = GroupUnitIds.Count > 0 ? GroupUnitIds : [primary];
        var absent = here.FirstOrDefault(it => !members.Contains(it.Unit.Id));
        if (absent != null) members = GroupMoveModel.Toggle(members, absent.Unit.Id);
        else foreach (var node in here) members = GroupMoveModel.Toggle(members, node.Unit.Id);
        ApplyGroup(members);
    }

    private void ApplyGroup(IReadOnlyList<int> members)
    {
        if (members.Count == 0)
        {
            Deselect();
            return;
        }
        int newPrimary = SelectedUnitId is { } current && members.Contains(current) ? current : members[0];
        SelectedUnitId = newPrimary;
        extraGroupIds = [.. members.Where(it => it != newPrimary)];
        Hud.HideBanner();
        ClearPreview();
        var node = unitNodes[newPrimary];
        Map.Select(new HexCoord(node.Unit.X, node.Unit.Y));
        ApplySelectionScale();
        string hint = GroupMoveModel.Hint(members.Count, GroupPair() != null);
        SetStatus(hint.Length > 0 ? hint : DescribeSelected(), quiet: true);
        Run(RefreshSelection());
    }

    /// <summary>The escort pair that exactly the selected group forms, if any.</summary>
    private EscortPair? GroupPair()
    {
        var group = GroupUnitIds;
        return group.Count == 2 ? Escorts.FirstOrDefault(it => group.Contains(it.CivilianId) && group.Contains(it.MilitaryId)) : null;
    }

    private void SelectUnit(UnitNode node)
    {
        extraGroupIds = [];
        SelectedUnitId = node.Unit.Id;
        Hud.HideBanner();
        ClearPreview();
        Map.Select(new HexCoord(node.Unit.X, node.Unit.Y));
        ApplySelectionScale();
        SetStatus(DescribeSelected(), quiet: true);
        Run(RefreshSelection());
    }

    /// <summary>Opens (or, with null, closes) the city screen for a city of the snapshot.</summary>
    private void ShowCity(CityDto? city)
    {
        if (city != null && Diplomacy.Visible) Diplomacy.Close();
        if (city != null && Tech.Visible) Tech.Close();
        if (city != null && Policies.Visible) Policies.Close();
        if (city != null && Overview.Visible) Overview.Close();
        if (city != null && Espionage.Visible) Espionage.Close();
        if (city != null && Trade.Visible) Trade.Close();
        if (city != null) Deselect(); // C012: the unit panel gives way to the city screen
        OpenCity = city;
        if (city == null)
        {
            if (City.Visible) City.Close();
            return;
        }
        SetStatus("City: " + city.Name, quiet: true);
        ReportTaskDone(TutorialTaskModel.EnterCityScreen);
        Run(City.OpenAsync(city));
    }

    private void Deselect()
    {
        SelectedUnitId = null;
        extraGroupIds = [];
        ClearPreview();
        ApplySelectionScale();
        ClearSelectionUi();
        SetStatus("Click one of your units.", quiet: true);
    }

    private string DescribeSelected()
    {
        var unit = unitNodes[SelectedUnitId.NotNull()].Unit;
        string moves = (unit.Own?.CurrentMovement ?? 0f).ToString("0.##", CultureInfo.InvariantCulture);
        return $"{unit.Name}   moves {moves}   health {(unit.Own?.Health ?? 0).ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task ShowPreview(HexCoord tile)
    {
        ClearPreview();
        int request = ++previewRequest;
        var unitNode = unitNodes[SelectedUnitId.NotNull()];
        if (unitNode.Unit.Own?.CurrentMovement is not > 0f)
        {
            SetHint("(no movement left)");
            return;
        }
        var result = await queries.Path(unitNode.Unit.Id, tile.X, tile.Y);
        if (request != previewRequest) return; // a newer click or a deselect superseded this answer
        if (result is not { Reachable: true, FirstStep: not null })
        {
            SetHint("(cannot reach that tile)");
            return;
        }
        preview = result;
        PreviewTarget = tile;
        DrawDots(new HexCoord(unitNode.Unit.X, unitNode.Unit.Y), result);
        SetHint($"path {result.Turns.ToString(CultureInfo.InvariantCulture)} turn(s): click again to move");
    }

    private void DrawDots(HexCoord from, PathPreviewResult path)
    {
        var points = new List<Vector3> { Lifted(from) };
        points.AddRange(path.Legs.SelectMany(leg => leg.Tiles).Select(it => Lifted(new HexCoord(it.X, it.Y))));
        for (int i = 1; i < points.Count; i++)
        {
            var start = points[i - 1];
            var end = points[i];
            int steps = Math.Max(1, (int)MathF.Ceiling(start.DistanceTo(end) / DotSpacing));
            for (int step = 1; step <= steps; step++)
            {
                var dot = MapMaterials.PathDot();
                dot.Position = start.Lerp(end, step / (float)steps);
                dotLayer.AddChild(dot);
            }
        }
    }

    private Vector3 Lifted(HexCoord coord)
    {
        var top = MapLayout.TileTop(coord.X, coord.Y, Map.TileNodeAt(coord)?.Elevation ?? 1f);
        return top + new Vector3(0f, DotLift, 0f);
    }

    private void ClearPreview()
    {
        previewRequest++;
        preview = null;
        PreviewTarget = null;
        foreach (var dot in dotLayer.GetChildren()) dot.QueueFree();
        ClearAttackPreview();
    }

    // C012: unit panel, menu and overlays

    private void ClearSelectionUi()
    {
        selectionRequest++;
        attackTiles = [];
        Panel.Clear();
        PromoteButton.Visible = false;
        ReachableOverlay.Clear();
        AttackOverlay.Clear();
        SiteOverlay.Clear();
        if (foundsCities)
        {
            foundsCities = false;
            if (HoveredTile is { } hovered) Run(LoadHoverTip(hovered)); // the tooltip goes back to the tile's own text
        }
        ClearAttackPreview();
    }

    private void ClearAttackPreview()
    {
        pendingAttack = null;
        CombatPreview.Clear();
    }

    private Vector3 TileTopAt(HexCoord coord) => MapLayout.TileTop(coord.X, coord.Y, Map.TileNodeAt(coord)?.Elevation ?? 1f);

    /// <summary>Asks the engine what the selected unit can do (actions, improvements, promotions, reach, attack targets) and shows the panel and both overlays; nothing is computed here.</summary>
    private async Task RefreshSelection()
    {
        int request = ++selectionRequest;
        if (SelectedUnitId is not { } id || !unitNodes.TryGetValue(id, out var node) || !node.IsOwn)
        {
            ClearSelectionUi();
            return;
        }
        var unit = node.Unit;
        var actions = await queries.Actions(id);
        var tile = await queries.TileInfo(unit.X, unit.Y, id);
        var promotions = await queries.Promotions(id);
        var reach = await queries.Reachable(id);
        var targets = await queries.AttackTargets(id);
        if (request != selectionRequest || SelectedUnitId != id) return; // superseded by another selection or a deselect
        session.Ruleset.Units.TryGetValue(unit.Name, out var baseUnit);
        List<MenuEntry> menu = [.. UnitOrdersModel.Menu(actions ?? new UnitActionsResult(id, []), tile, promotions, unit.Own?.CurrentMovement > 0f)];
        bool paired = GroupPair() != null;
        if (paired || GroupMoveModel.Offer([.. GroupUnitIds.Where(unitNodes.ContainsKey).Select(it => unitNodes[it].Unit)], name => session.Ruleset.Units.GetValueOrDefault(name)) != null)
            menu.Insert(menu.Count > 0 && menu[^1].Kind == MenuEntryKind.Disband ? menu.Count - 1 : menu.Count, GroupMoveModel.Entry(paired));
        Panel.Show(UnitOrdersModel.Panel(unit, baseUnit), menu);
        PromoteButton.Visible = promotions is { Promotions.Count: > 0 };
        bool wasFounding = foundsCities;
        foundsCities = actions?.Actions.Any(it => it.Descriptor.ActionType == UnitActionType.FoundCity) == true;
        if (wasFounding && !foundsCities)
        {
            // T257: switching from a settler to another unit; the settler's site answer may have landed in between, so its radius and tip must not outlive the settler
            SiteOverlay.Clear();
            if (HoveredTile is { } hovered) Run(LoadHoverTip(hovered));
        }
        if (foundsCities && HoveredTile is { } under) Run(LoadHoverTip(under)); // the pointer already rests on a tile: show its site now
        attackTiles = targets == null ? [] : [.. UnitOrdersModel.AttackTiles(targets)];
        ReachableOverlay.Show(reach == null ? [] : [.. UnitOrdersModel.ReachableTiles(reach).Select(it => new HexCoord(it.X, it.Y))], TileTopAt);
        AttackOverlay.Show([.. attackTiles.Select(it => new HexCoord(it.X, it.Y))], TileTopAt);
    }

    private async Task ShowAttackPreview(HexCoord tile)
    {
        ClearPreview();
        int request = ++previewRequest;
        if (SelectedUnitId is not { } id) return;
        var result = await queries.AttackPreview(id, tile.X, tile.Y);
        if (request != previewRequest) return;
        if (result == null)
        {
            SetHint("(no battle preview for that tile)");
            return;
        }
        pendingAttack = (result.Kind, tile);
        CombatPreview.Show(UnitOrdersModel.Combat(result));
        SetHint("confirm the attack or cancel", quiet: true); // the battle card says it
    }

    private async Task ConfirmAttack()
    {
        if (pendingAttack is not { } pending || SelectedUnitId is not { } id) return;
        GameCommand command = pending.Kind switch
        {
            "nuke" => new Nuke { UnitId = id, Target = pending.Target },
            "airsweep" => new AirSweep { UnitId = id, Target = pending.Target },
            _ => new AttackWithUnit { UnitId = id, Target = pending.Target },
        };
        var result = await session.SubmitAsync(command);
        ClearAttackPreview();
        if (!result.Accepted)
        {
            SetStatus("Attack refused: " + result.Message);
            return;
        }
        Refresh();
        SetStatus("Attack made.");
    }

    private void OnMenuEntry(MenuEntry entry)
    {
        if (IsEndingTurn) return; // T345: no unit order starts while the turn runs
        if (entry.Kind == MenuEntryKind.Engine && entry.Action?.Confirmation is { } confirmation)
            AskUnitConfirmation(entry, confirmation.Question, confirmation.ConfirmText);
        else if (entry.Kind == MenuEntryKind.Disband)
            AskUnitConfirmation(entry, "Disband this unit?", "Disband");
        else Run(ExecuteEntry(entry, confirmed: false));
    }

    /// <summary>T343: asks before a destructive unit action, or runs it on the first press when the player turned confirmations off.</summary>
    private void AskUnitConfirmation(MenuEntry entry, string question, string confirmText)
    {
        if (ConfirmDestructive) Panel.AskConfirmation(entry, question, confirmText);
        else Run(ExecuteEntry(entry, confirmed: true));
    }

    /// <summary>T343: whether destructive actions ask first (the client setting; on without a settings binding).</summary>
    private bool ConfirmDestructive => boundSettings?.Current.ConfirmDestructiveActions ?? true;

    private async Task ExecuteEntry(MenuEntry entry, bool confirmed)
    {
        if (SelectedUnitId is not { } id) return;
        if (entry.Kind == MenuEntryKind.Group)
        {
            ToggleEscort(entry.Argument == GroupMoveModel.EscortArgument);
            return;
        }
        GameCommand command = entry.Kind switch
        {
            MenuEntryKind.Engine => new ExecuteUnitAction { UnitId = id, Descriptor = entry.Action.NotNull().Descriptor, Confirmed = confirmed },
            MenuEntryKind.Improve => new SetWorkerOrder { UnitId = id, Order = WorkerOrderKind.Build, Improvement = entry.Argument.NotNull() },
            MenuEntryKind.Promote => new PromoteUnit { UnitId = id, Promotion = entry.Argument.NotNull() },
            _ => new DisbandUnit { UnitId = id },
        };
        var result = await session.SubmitAsync(command);
        if (!result.Accepted)
        {
            SetStatus($"{entry.Label} refused: {result.Message}");
            return;
        }
        DropEscortTarget(id);
        Refresh();
        SetStatus($"{entry.Label}: done.");
    }

    private void ToggleEscort(bool make)
    {
        var group = GroupUnitIds;
        string viewer = session.ViewerCivId ?? "";
        if (!escorts.TryGetValue(viewer, out var list)) escorts[viewer] = list = [];
        escortTurnHandled[viewer] = snapshot.Turn;
        list.RemoveAll(it => group.Contains(it.CivilianId) || group.Contains(it.MilitaryId));
        if (make && GroupMoveModel.Offer([.. group.Where(unitNodes.ContainsKey).Select(it => unitNodes[it].Unit)], name => session.Ruleset.Units.GetValueOrDefault(name)) is { } pair) list.Add(pair);
        SetStatus(make ? "Escort pair formed: it moves together." : "Escort ended.");
        Run(RefreshSelection());
    }

    private sealed class GroupEnvironment(GameScreen screen) : IGroupMoveEnvironment
    {
        private UnitDto? Own(int id) => screen.session.Snapshot.Units.FirstOrDefault(it => it.Id == id && it.Own != null);

        public HexCoord? Position(int unitId) => Own(unitId) is { } unit ? new HexCoord(unit.X, unit.Y) : null;

        public float MovesLeft(int unitId) => Own(unitId)?.Own?.CurrentMovement ?? 0f;

        public Task<PathPreviewResult?> PathAsync(int unitId, HexCoord to) => screen.queries.Path(unitId, to.x, to.y);

        public Task<SubmitResult> MoveAsync(int unitId, HexCoord to) => screen.session.SubmitAsync(new MoveUnit { UnitId = unitId, To = to });
    }

    private string UnitName(int id) => unitNodes.TryGetValue(id, out var node) ? node.Unit.Name : "Unit";

    /// <summary>C091: moves the whole selection toward a tile (escort pairs as pairs); the pairs remember the tile and continue at the start of each turn.</summary>
    private async Task MoveGroupTo(HexCoord target)
    {
        var ids = GroupUnitIds;
        escortTurnHandled[session.ViewerCivId ?? ""] = snapshot.Turn;
        var report = await GroupMoveModel.MoveGroupAsync(new GroupEnvironment(this), ids, Escorts, target);
        string viewer = session.ViewerCivId ?? "";
        if (escorts.TryGetValue(viewer, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                var pair = list[i];
                if (!ids.Contains(pair.CivilianId) || !ids.Contains(pair.MilitaryId)) continue;
                var outcome = report.Outcomes.FirstOrDefault(it => it.UnitId == pair.MilitaryId);
                list[i] = pair with { Target = outcome?.Status == GroupMoveStatus.Moved ? target : null };
            }
        }
        if (report.Outcomes.Any(it => it.Status == GroupMoveStatus.Moved)) ReportTaskDone(TutorialTaskModel.MoveUnitTask);
        Refresh();
        SetStatus(GroupMoveModel.Summary(report, UnitName));
    }

    // The last turn whose start was handled for each viewer (escorts continue once per turn, when the viewer's units can move again); a player who already acted this turn counts as handled
    private readonly Dictionary<string, int> escortTurnHandled = [];

    /// <summary>A command given to one unit of a pair on its own ends the pair's standing move.</summary>
    private void DropEscortTarget(int unitId)
    {
        if (!escorts.TryGetValue(session.ViewerCivId ?? "", out var list)) return;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Target != null && (list[i].CivilianId == unitId || list[i].MilitaryId == unitId)) list[i] = list[i] with { Target = null };
    }

    private bool EscortsCanMove() =>
        Escorts.Any(pair => pair.Target != null && snapshot.Units.Any(it => it.Id == pair.MilitaryId && it.Own?.CurrentMovement > 0f));

    /// <summary>C091: at the start of a turn every escort pair with a standing move takes its next step (two ordinary move commands each).</summary>
    private async Task ContinueEscorts()
    {
        if (!escorts.TryGetValue(session.ViewerCivId ?? "", out var list)) return;
        var env = new GroupEnvironment(this);
        bool any = false;
        foreach (var pair in list.ToArray()) // the list can change while a step awaits (the player may end an escort), so entries are found again by value
        {
            if (pair.Target is not { } target || !list.Contains(pair)) continue; // ended or re-targeted while an earlier pair awaited
            var outcome = await GroupMoveModel.EscortStepAsync(env, pair, target);
            any |= outcome.Status == GroupMoveStatus.Moved;
            int at = list.IndexOf(pair);
            if (outcome.Status != GroupMoveStatus.Moved && at >= 0) list[at] = pair with { Target = null };
        }
        if (any) Refresh();
    }

    private async Task MoveSelectedTo()
    {
        var path = preview;
        if (GroupUnitIds.Count > 1 && PreviewTarget is { } groupTarget)
        {
            await MoveGroupTo(groupTarget);
            return;
        }
        if (path?.FirstStep is not { } step || SelectedUnitId is not { } unitId) return;
        var result = await session.SubmitAsync(new MoveUnit { UnitId = unitId, To = new HexCoord(step.X, step.Y) });
        if (!result.Accepted)
        {
            SetStatus("Move refused: " + result.Message);
            return;
        }
        DropEscortTarget(unitId);
        ReportTaskDone(TutorialTaskModel.MoveUnitTask);
        Refresh();
        SetStatus(DescribeSelected(), quiet: true);
    }

    /// <summary>Asks the engine for the bar's numbers and for what is still pending, and shows them; announces a turn the first time it is seen.</summary>
    private async Task RefreshHud()
    {
        var bar = await queries.TopBar();
        var pending = await queries.Pending();
        if (bar == null) return; // a refused query leaves the old bar
        var action = HudModel.NextAction(pending, snapshot.TurnStatus, session.ViewerCivId);
        Hud.Update(bar, action, HudModel.CanEndTurn(pending));
        World.SetResearch(HudModel.CurrentResearch(bar));
        World.SetTechnologyAvailable(pending?.Decisions.Any(it => it.Kind == "Technology") == true);
        await RefreshRivals(bar.Turn, force: false);
        Layout();
        var rows = NotificationsModel.Rows(snapshot.Notifications);
        Notifications.Update(bar.Turn, rows, session.Ruleset);
        if (bar.Turn != bannerTurn)
        {
            bannerTurn = bar.Turn;
            Hud.ShowBanner(HudModel.Banner(bar.Turn, bar.Year, session.ViewerCivId));
            World.LayoutOverlays();
        }
        string viewer = session.ViewerCivId ?? "";
        if (!escortTurnHandled.TryGetValue(viewer, out int handled)) escortTurnHandled[viewer] = bar.Turn; // the first sight of a game: nothing to continue
        else if (bar.Turn > handled && EscortsCanMove())
        {
            escortTurnHandled[viewer] = bar.Turn; // the new turn has begun for the viewer (its units have movement again)
            await ContinueEscorts();
        }
        ShowNextAlert(pending, reopen: false);
        ShowNextProposal(reopen: false);
        await CheckVoteResult();
        await CheckGameOver();
        CheckTutorials();
        await RefreshTaskCard();
    }

    /// <summary>
    /// C087: the rival stats ribbon's rows from the engine's overview (the known major civs) and the Rankings page (the figures the game shows the viewer) and, while the ribbon is open, from each rival's trade
    /// options (the luxuries it could trade). Collapsed, it asks once per turn (the header counts the rivals); open, on every refresh.
    /// </summary>
    private async Task RefreshRivals(int turn, bool force)
    {
        var ribbon = World.Rivals;
        if (!force && !ribbon.Expanded && rivalsTurn == turn) return;
        rivalsTurn = turn;
        int ticket = ++rivalsRequest;
        var overview = await queries.Overview();
        var rankings = await queries.Rankings();
        var trade = new Dictionary<string, TradeOptionsResult?>();
        if (ribbon.Expanded)
            foreach (var rival in RivalRibbonModel.Rivals(overview)) trade[rival.CivName] = await queries.TradeOptions(rival.CivName);
        if (ticket != rivalsRequest) return; // a newer refresh is on its way; never let an older answer overwrite it
        ribbon.Show(RivalRibbonModel.Rows(overview, rankings, trade));
    }

    private bool TaskCardActive => TaskCardEnabled && boundSettings != null && boundSettings.Current.ShowTutorials && !GameOver.Visible;

    /// <summary>
    /// C054: reports what the snapshot proves the player has done, then asks the engine for the current task and shows it (or hides the card).
    /// </summary>
    private async Task RefreshTaskCard()
    {
        if (!TaskCardActive)
        {
            TaskCard.Show(null);
            return;
        }
        foreach (var task in TutorialTaskModel.FromSnapshot(snapshot)) await ReportTask(task);
        await ShowTaskCard();
    }

    /// <summary>C054: tells the engine once per game screen that the player did <paramref name="task"/> (a refusal means the ruleset has no such task: harmless).</summary>
    private async Task ReportTask(string task)
    {
        if (!TaskCardActive || !reportedTasks.Add(task)) return;
        await session.SubmitAsync(new CompleteTutorialTask { Task = task });
    }

    private void ReportTaskDone(string task)
    {
        if (TaskCardActive && !reportedTasks.Contains(task)) Run(ReportTaskThenRefresh(task));
    }

    private async Task ReportTaskThenRefresh(string task)
    {
        await ReportTask(task);
        await ShowTaskCard();
    }

    /// <summary>T299: asks for the current task and shows it, unless tutorials were turned off while the answer was on its way.</summary>
    private async Task ShowTaskCard()
    {
        var result = await queries.TutorialTask(showTutorials: true);
        TaskCard.Show(TaskCardActive ? result : null);
        Layout();
    }

    private async Task ChooseTaskChoice(string eventName, int index)
    {
        await session.SubmitAsync(new ChooseTutorialTaskChoice { Event = eventName, Choice = index });
        await RefreshTaskCard();
    }

    /// <summary>
    /// C053: opens the World Congress result when the engine says it is due (Kotlin shows it before the victory screen and the alerts, when no popup is open), and
    /// closes it when it no longer is. The player's Continue sends <c>AckDiplomaticVoteResult</c>.
    /// </summary>
    private async Task CheckVoteResult()
    {
        if (resigned || Alerts.Visible || Picker.Visible) return;
        var due = VoteResultModel.Build(await queries.DiplomaticVoteResult());
        if (due == null)
        {
            VoteResult.Close();
            return;
        }
        if (!VoteResult.Visible) VoteResult.Open(due);
    }

    private async Task AcknowledgeVoteResult()
    {
        var result = await session.SubmitAsync(new Unciv.Commands.Empire.AckDiplomaticVoteResult());
        if (!result.Accepted) SetStatus("Continue refused: " + result.Message);
        VoteResult.Close();
        Refresh();
    }

    /// <summary>
    /// C024: asks the engine whether the game is over for the viewer and opens the victory/defeat screen. The ranking and the history are asked only when the screen opens;
    /// a game that hides them from the viewer simply shows none. A defeated viewer who chose to keep watching is not asked again.
    /// </summary>
    private async Task CheckGameOver()
    {
        if (resigned) return; // the resigned screen stays until the player leaves; the civ is the AI's now
        int check = ++gameOverCheck;
        var outcome = await queries.GameOver();
        if (check != gameOverCheck) return; // a newer snapshot asked meanwhile
        if (outcome == null || !GameOverModel.IsShown(outcome) || (outcome.Status == Unciv.Queries.Victory.GameOverQuery.Defeated && watchingAfterDefeat))
        {
            GameOver.Close();
            return;
        }
        if (GameOver.Visible && GameOver.Current?.Status == outcome.Status && gameOverTurn == snapshot.Turn) return; // the same screen is not rebuilt, but a new turn changes the graph
        var rankings = await queries.Rankings();
        var history = await queries.History();
        if (check != gameOverCheck) return;
        gameOverTurn = snapshot.Turn;
        GameOver.Open(GameOverModel.Build(outcome, rankings, history), civ => OwnerColour(civ));
    }

    private async Task OnGameOverChosen(GameOverButtonKind kind)
    {
        switch (kind)
        {
            case GameOverButtonKind.OneMoreTurn:
                var result = await session.SubmitAsync(new OneMoreTurn());
                if (!result.Accepted)
                {
                    SetStatus("One more turn refused: " + result.Message);
                    return;
                }
                GameOver.Close();
                Refresh();
                SetStatus("The game goes on.");
                break;
            case GameOverButtonKind.KeepWatching:
                watchingAfterDefeat = true;
                GameOver.Close();
                SetStatus("Watching the game.");
                break;
            default:
                MainMenuRequested?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Opens the first pending alert that asks for a decision and was not put off; with <paramref name="reopen"/> the player asked to review, which also opens the
    /// informational and the put-off alerts, one after the other until none is left. Closes the popup when there is nothing to show.
    /// </summary>
    private void ShowNextAlert(Unciv.Queries.Empire.PendingDecisionsResult? pending, bool reopen)
    {
        if (reopen)
        {
            deferredAlerts.Clear();
            reviewingAlerts = true;
        }
        var next = AlertPopupModel.Next(pending, snapshot, deferredAlerts, reviewingAlerts);
        if (next == null) reviewingAlerts = false;
        if (pending != null)
        {
            // C067 (S2): an alert that only informs is not a popup: it shows once as a toast in the notification column and waits under "Review alerts"
            foreach (var entry in pending.Decisions)
                if (AlertPopupModel.Describe(entry, snapshot) is { Kind: AlertKind.Ambient } ambient && ambient.Type != AlertType.StartIntro && toastedAlerts.Add(ambient.Key))
                    Notifications.PushToast(ambient.Body.Length == 0 ? ambient.Title : ambient.Title + ": " + ambient.Body, ambient.Icon);
        }
        Notifications.SetWaiting(AlertPopupModel.WaitingCount(pending, snapshot, deferredAlerts) + TradeProposalModel.WaitingCount(snapshot, deferredProposals));
        World.LayoutOverlays();
        if (next == null) Alerts.Close();
        else if (Alerts.Current?.Key != next.Key || !Alerts.Visible) Alerts.Open(next);
    }

    /// <summary>
    /// C039: opens the first incoming trade or peace proposal that was not put off, unless something else asks for the player first (an alert, a decision or a full
    /// screen: the proposal then waits and shows once they are closed); with <paramref name="reopen"/> the proposals that were put off come back. Also keeps the
    /// list of our own outstanding offers and what became of the latest ones.
    /// </summary>
    private void ShowNextProposal(bool reopen)
    {
        if (reopen) deferredProposals.Clear();
        Notifications.SetOffers(offerTracker.Update(snapshot));
        World.LayoutOverlays();
        var next = TradeProposalModel.Next(snapshot, deferredProposals);
        bool covered = Alerts.Visible || Picker.Visible || City.Visible || Diplomacy.Visible || Tech.Visible || Policies.Visible || Trade.Visible || Overview.Visible || GameOver.Visible || VoteResult.Visible || Pedia.Visible;
        if (next == null || covered) Proposals.Close();
        else if (Proposals.Current?.Key != next.Key || !Proposals.Visible) Proposals.Open(next);
    }

    private bool answeringProposal;
    /// <summary>C076: the theme the screens share (the harness changes its scale to check that a screen rebuilds).</summary>
    public UiTheme? SharedTheme => ui;

    /// <summary>
    /// T405: raised with the freshly built and bound theme of every <see cref="Start"/> (a new game, a load, a hotseat hand-over), so a view floating over the game keeps the same scale and breakpoint.
    /// </summary>
    public event Action<UiTheme>? SharedThemeChanged;

    private UiTheme? ui;

    private async Task AnswerProposal(TradeProposal popup, AlertButton answer)
    {
        if (answeringProposal) return; // a second press while the engine answers would be refused and look like a withdrawal
        answeringProposal = true;
        try
        {
            await AnswerProposalOnce(popup, answer);
        }
        finally
        {
            answeringProposal = false;
        }
    }

    private async Task AnswerProposalOnce(TradeProposal popup, AlertButton answer)
    {
        offerTracker.NoteAnswered(popup.FromId);
        var request = snapshot.PendingTrades.FirstOrDefault(it => it.From == popup.FromId && it.To == snapshot.You);
        var result = await session.SubmitAsync(answer.Command);
        if (!result.Accepted)
        {
            offerTracker.NoteRefused(popup.FromId);
            SetStatus($"{popup.Title}: {answer.Label} refused: {result.Message}");
            return;
        }
        Proposals.Close();
        if (answer.Id == TradeProposalModel.CounterId)
        {
            Refresh();
            OpenTradeScreen(popup.FromId, request);
            return;
        }
        Refresh();
    }

    private async Task AnswerAlert(AlertPopup popup, AlertButton answer)
    {
        var result = await session.SubmitAsync(answer.Command);
        if (!result.Accepted)
        {
            SetStatus($"{popup.Title}: {answer.Label} refused: {result.Message}");
            return;
        }
        Refresh();
    }

    private void OnNotificationChosen(NotificationRow row)
    {
        switch (row.Target)
        {
            case { Kind: NotificationTargetKind.Tile } tile:
                Map.FocusOn(UnitPosition(new HexCoord(tile.X, tile.Y), 0));
                break;
            case { Kind: NotificationTargetKind.Tech } tech:
                OpenTechScreen(tech.Name);
                break;
            case { Kind: NotificationTargetKind.Promotion } promotion:
                Run(PromoteFromNotification(promotion.Id));
                break;
            case { Kind: NotificationTargetKind.Spies }:
                Run(OpenDecision(DecisionKind.MoveSpies));
                break;
        }
    }

    // Events arrive before the screen redraws from the new snapshot, so the presenter can still take the nodes of units that just died.
    private void OnEventsReceived(EventBatch batch)
    {
        if (AutoPlay == null || AutoPlayModel.Animates(autoPlayAnimations)) // T376: a run that skips animations shows neither the moves and fights nor their sounds
        {
            PresentEvents(batch.Events);
            PlayCues(batch.Events);
        }
        if (session is not ITurnRoster roster) return;
        foreach (var gameEvent in batch.Events)
            if (TurnBarModel.Toast(gameEvent, roster.Roster, session.ViewerCivId) is { } toast) TurnBar.Toast(toast);
    }

    private void LayoutSocial() => Social.LayoutAtLeft(new Vector2(HudMargin, World.NavBottom + HudMargin));

    /// <summary>C067: what a nav button of the <see cref="WorldHud"/> opens.</summary>
    private void OnNav(NavTarget target)
    {
        switch (target)
        {
            case NavTarget.Diplomacy: OpenDiplomacyScreen(); break;
            case NavTarget.Technology: OpenTechScreen(); break;
            case NavTarget.Policies: OpenPolicyScreen(); break;
            case NavTarget.Trade: OpenTradeScreen(); break;
            case NavTarget.Espionage: OpenEspionageScreen(); break;
            case NavTarget.Civilopedia: OpenPediaScreen(); break;
            case NavTarget.Overview: OpenOverviewScreen(); break;
            case NavTarget.Menu: OpenGameMenu(); break;
            case NavTarget.Players:
                Social.Toggle(!Social.Visible);
                if (Social.Visible) LayoutSocial();
                break;
        }
    }

    /// <summary>C045: connects the panel to a hosted game's chat and player list (null disconnects and hides it).</summary>
    private void BindSocial(IGameSocial? next)
    {
        if (social != null)
        {
            social.ChatReceived -= Social.AddChat;
            social.PlayersChanged -= RefreshPlayers;
            social.PingReceived -= OnPingReceived;
        }
        pings = [];
        PingLayer?.Clear();
        social = next;
        World.ShowPlayers(next != null);
        if (next == null)
        {
            Social.Toggle(false);
            return;
        }
        next.ChatReceived += Social.AddChat;
        next.PlayersChanged += RefreshPlayers;
        next.PingReceived += OnPingReceived;
        foreach (var line in next.Chat) Social.AddChat(line);
        RefreshPlayers();
    }

    private void RefreshPlayers()
    {
        if (social == null) return;
        Social.ShowPlayers(social.Players);
        LayoutSocial();
    }

    private async Task SendChat(string text)
    {
        if (social == null) return;
        try
        {
            await social.SendChatAsync(text);
        }
        catch (InvalidOperationException)
        {
            if (IsInstanceValid(this) && IsInsideTree()) Social.SetNotice("The host cannot be reached.".Tr());
        }
    }

    /// <summary>C045: Kick (<paramref name="newKey"/> null) or Rebind on the host's session; the refusal, if any, is shown in the panel's notice line.</summary>
    private async Task Admin(string civId, string? newKey)
    {
        if (social == null) return;
        string? refused;
        try
        {
            refused = newKey == null ? await social.KickAsync(civId) : await social.RebindAsync(civId, newKey);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            refused = "host-error";
        }
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        Social.SetNotice(refused == null ? (newKey == null ? "Seat released." : "Seat rebound: the new device can join now.").Tr() : "Not done: ".Tr() + SocialModel.Describe(refused));
    }

    /// <summary>
    /// C036: redraws the turn bar from the host's TurnStatus and roster and raises the "resolving" overlay while the round is resolved (commands are refused then:
    /// <c>turn-resolving</c>). Hides the bar in a game that is not simultaneous.
    /// </summary>
    private void RefreshTurnBar()
    {
        if (sessionRef.OrNull is not ITurnRoster roster || !snapshotRef.IsSet)
        {
            TurnBar.Show(null);
            TurnOverlay.LayoutIn(Size, OverlayTop());
            return;
        }
        var bar = TurnBarModel.Of(snapshot.TurnStatus, roster.Roster, session.ViewerCivId, NowMs());
        TurnBar.Show(bar);
        shownCountdown = bar?.Countdown;
        shownGrace = bar?.GraceCountdown;
        TurnBar.LayoutIn(Size.X, TurnBarTop());
        // T345: while the host (or the local worker) plays the other civs, both host and joiners see "waiting for AI/host"; a local turn owns the overlay and keeps its progress line.
        if (turnCancellation == null)
        {
            bool resolving = bar is { Resolving: true };
            Hud.SetTurnResolving(resolving);
            if (resolving)
            {
                Hud.HideBanner();
                TurnOverlay.WaitForHost(NextTurnProgressModel.WaitingForHost());
            }
            else TurnOverlay.Close();
        }
        TurnOverlay.LayoutIn(Size, OverlayTop()); // the bar's height decides where the card goes
    }

    /// <summary>C057: where the engine's sound cues are played (the app's sound player); null in scenarios that do not care, which then play nothing.</summary>
    public Action<string>? PlaySound { get; set; }

    /// <summary>T353: notified when the game state's music mood changes (peace, tension, victory).</summary>
    public Action<MusicMood>? MusicMoodChanged { get; set; }

    /// <summary>Plays the sounds the batch asks of this viewer; a missing sound device never throws into the event stream.</summary>
    public void PlayCues(IReadOnlyList<GameEvent> events)
    {
        if (PlaySound is not { } play) return;
        foreach (string sound in SoundCues.For(events, session.ViewerCivId)) play(sound);
    }

    /// <summary>Queues events for the presenter (C033). Call before <see cref="Refresh"/>, while the nodes of units the events destroy are still in the table.</summary>
    public bool PresentEvents(IReadOnlyList<GameEvent> events) => Presenter.Present(events, unitNodes);

    private void OnSnapshotChanged(ClientSnapshot changed)
    {
        snapshotRef.Set(changed);
        RefreshTurnBar();
        Run(RefreshHud());
    }

    private void OnHudAction(HudAction action)
    {
        switch (action.Kind)
        {
            case HudActionKind.EndTurn:
                OnNextTurn();
                break;
            case HudActionKind.UndoEndTurn:
                Run(UndoEndTurn());
                break;
            case HudActionKind.NextUnit:
                SelectNextUnit();
                break;
            case HudActionKind.Decision when action.Decision is { } decision:
                Run(OpenDecision(decision));
                break;
            default:
                SetStatus($"{action.Label}: this screen is not built yet.");
                break;
        }
    }

    /// <summary>C038: takes the player to what a pending decision asks for: a screen, a picker built from the engine's answer, or the engine's own command.</summary>
    public async Task OpenDecision(DecisionKind kind)
    {
        switch (DecisionPickerModel.RouteOf(kind))
        {
            case DecisionRoute.PolicyScreen:
                OpenPolicyScreen();
                return;
            case DecisionRoute.TechScreen:
                OpenTechScreen();
                return;
            case DecisionRoute.AlertPopup:
                await ReviewAlerts();
                return;
            case DecisionRoute.DiplomacyScreen:
                OpenDiplomacyScreen();
                return;
            case DecisionRoute.NextUnit:
                SelectNextUnit();
                return;
            case DecisionRoute.AutoCommand:
                var moved = await session.SubmitAsync(new Unciv.Commands.Turn.MoveAutomatedUnits());
                SetStatus(moved.Accepted ? "Automated units moved." : "Move automated units refused: " + moved.Message);
                Refresh();
                return;
            case DecisionRoute.CityScreen:
                var pending = await queries.Pending();
                string? cityName = pending?.Decisions.FirstOrDefault(it => it.Kind == nameof(DecisionKind.CityProduction))?.Subject;
                var city = snapshot.Cities.FirstOrDefault(it => it.Own != null && it.Name == cityName);
                if (city == null) SetStatus("Pick construction: no city is waiting.");
                else OpenCityScreen(city);
                return;
        }
        ShowCity(null);
        if (Diplomacy.Visible) Diplomacy.Close();
        if (Policies.Visible) Policies.Close();
        if (Tech.Visible) Tech.Close();
        var spec = await BuildPicker(kind);
        if (spec == null)
        {
            SetStatus($"{kind}: the engine offers nothing to choose now.");
            return;
        }
        Picker.Open(spec);
    }

    private async Task<PickerSpec?> BuildPicker(DecisionKind kind)
    {
        var ruleset = session.Ruleset;
        string DescribeUnit(string name) => ruleset.Units.TryGetValue(name, out var unit) ? unit.GetShortDescription() : "";
        string DescribeBelief(string name) => ruleset.Beliefs.TryGetValue(name, out var belief) ? DecisionPickerModel.DescribeUniques(belief) : "";
        switch (kind)
        {
            case DecisionKind.FreeGreatPerson:
                var people = await queries.GreatPeople();
                return people == null ? null : DecisionPickerModel.FreeGreatPerson(people, DescribeUnit);
            case DecisionKind.MoveSpies:
                var spies = await queries.SpyDestinations();
                return spies == null ? null : DecisionPickerModel.MoveSpies(spies);
            case DecisionKind.DiplomaticVote:
                var vote = await queries.DiplomaticVote();
                return vote == null ? null : DecisionPickerModel.DiplomaticVote(vote, id => snapshot.Civs.FirstOrDefault(it => it.CivId == id)?.Name ?? id);
            default:
                var religion = await queries.Religion();
                if (religion == null) return null;
                return kind switch
                {
                    DecisionKind.Pantheon => DecisionPickerModel.Pantheon(religion, religion.State == "Pantheon", DescribeBelief),
                    DecisionKind.FoundReligion => DecisionPickerModel.FoundReligion(religion, DescribeBelief),
                    _ => DecisionPickerModel.ChooseBeliefs(religion, kind == DecisionKind.ReformReligion, DescribeBelief),
                };
        }
    }

    /// <summary>C052: a "can be promoted" notification selects its unit (when it is still ours) and opens the promotion picker.</summary>
    private async Task PromoteFromNotification(int unitId)
    {
        if (!unitNodes.TryGetValue(unitId, out var node) || !node.IsOwn)
        {
            SetStatus("This unit cannot be promoted now.");
            return;
        }
        SelectUnit(node);
        await OpenPromotionPicker();
    }

    private async Task OpenPromotionPicker()
    {
        if (SelectedUnitId is not { } id || !unitNodes.TryGetValue(id, out var node)) return;
        var choices = await queries.Promotions(id);
        var spec = choices == null ? null : DecisionPickerModel.Promotion(choices, node.Unit.Name);
        if (spec == null) SetStatus("This unit cannot be promoted now.");
        else Picker.Open(spec);
    }

    private async Task ConfirmPicker(PickerSpec spec, IReadOnlyList<GameCommand> commands)
    {
        foreach (var command in commands)
        {
            var result = await session.SubmitAsync(command);
            if (!result.Accepted)
            {
                SetStatus($"{spec.Title} refused: {result.Message}");
                Refresh();
                return;
            }
        }
        Picker.Close();
        Refresh();
        if (Espionage.Visible) Run(Espionage.RefreshAsync());
        SetStatus($"{spec.Title}: done.");
    }

    private async Task DismissPicker(PickerSpec spec)
    {
        if (spec.Dismiss != null) await session.SubmitAsync(spec.Dismiss);
        Picker.Close();
        Refresh();
        if (Espionage.Visible) await Espionage.RefreshAsync();
    }

    /// <summary>The snapshot does not say which unit is "due" (the engine decides that), so the cycle visits the viewer's units that could still act: movement left, no standing order, not fortified.</summary>
    private void SelectNextUnit()
    {
        var waiting = unitNodes.Values
            .Where(it => it.IsOwn && it.Unit.Own is { CurrentMovement: > 0f, Action: null } && !it.Unit.IsFortified)
            .Select(it => it.Unit.Id).ToList();
        if (HudModel.NextDueUnit(waiting, SelectedUnitId) is not { } id)
        {
            SetStatus("No unit is waiting for orders.");
            return;
        }
        var node = unitNodes[id];
        SelectUnit(node);
        Map.FocusOn(UnitPosition(new HexCoord(node.Unit.X, node.Unit.Y), 0));
    }

    private async Task ReviewAlerts()
    {
        deferredProposals.Clear();
        ShowNextAlert(await queries.Pending(), reopen: true);
        ShowNextProposal(reopen: true);
    }

    private void OnNextTurn()
    {
        if (AutoPlay != null)
        {
            SetStatus("Stop AutoPlay first.");
            return;
        }
        Run(session is ITurnRoster ? EndTurnSimultaneous() : EndTurn());
    }

    /// <summary>
    /// C036: End turn in a simultaneous game only marks this player as done; the host resolves the round when everybody has (or the deadline passed). So nothing waits here
    /// and nothing is blocked: the turn bar shows whom the game waits for, and Undo end turn stays possible until the host starts resolving.
    /// </summary>
    private async Task EndTurnSimultaneous()
    {
        var result = await session.SubmitAsync(new EndTurn());
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        SetStatus(result.Accepted ? "Turn ended. Waiting for the other players." : "Cannot end the turn: " + Describe(result.Message));
        Refresh();
    }

    private async Task UndoEndTurn()
    {
        var result = await session.SubmitAsync(new UndoEndTurn());
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        SetStatus(result.Accepted ? "Your turn continues." : "Cannot undo: " + Describe(result.Message));
        Refresh();
    }

    private static string Describe(string code) => code == "turn-resolving" ? "the host is resolving the turn" : code;

    /// <summary>C047: the AutoPlay button stops a running run, otherwise opens its menu (or closes it when open).</summary>
    private void OnAutoPlayButton()
    {
        if (AutoPlay != null)
        {
            Run(StopAutoPlayAsync());
            return;
        }
        if (AutoPlayMenu.Visible)
        {
            AutoPlayMenu.Close();
            return;
        }
        var settings = (boundSettings?.Current ?? ClientSettings.Default).AutoPlay;
        AutoPlayMenu.Open(AutoPlayModel.Entries(settings, AutoPlayModel.CanStart(Hud.Action.Kind, GameOver.Visible || resigned)));
        Layout();
    }

    /// <summary>
    /// C047: starts the engine's auto-play with the stored options and then plays one turn per <see cref="AutoPlayFramesPerTurn"/> frames through the session
    /// until the run is out of turns, the engine refuses the next one (the game ended, the civ was defeated) or the player stops it. Stopping (the button, hiding the
    /// button in the options, leaving the game) sends the engine's StopAutoPlay so the log and a replay agree.
    /// </summary>
    public async Task StartAutoPlayAsync(AutoPlayKind kind)
    {
        AutoPlayMenu.Close();
        if (AutoPlay != null) return;
        var settings = (boundSettings?.Current ?? ClientSettings.Default).AutoPlay;
        var started = await session.SubmitAsync(AutoPlayModel.StartCommand(settings, kind));
        if (!started.Accepted)
        {
            SetStatus("Cannot start AutoPlay: " + started.Message);
            return;
        }
        int generation = ++autoPlayGeneration;
        AutoPlay = AutoPlayModel.StartRun(settings, kind);
        AutoPlayResult = null;
        Hud.SetAutoPlay(AutoPlay);
        autoPlayAnimations = settings.AnimationMode;
        Presenter.Complete(); // T376: whatever the player's own moves still queue is over before the run's turns start, so the backlog starts at zero
        Presenter.SpeedScale = AutoPlayModel.Speed(autoPlayAnimations);
        string refusal = "";
        while (AutoPlay is { Active: true } && generation == autoPlayGeneration)
        {
            TurnOutcome outcome;
            try
            {
                outcome = await session.AutoPlayTurnAsync();
            }
            catch (Exception e)
            {
                // review C040: a failed background turn must end the run visibly instead of leaving AutoPlay "active" with nothing playing
                GD.PushError("AutoPlay turn failed: " + e);
                if (generation != autoPlayGeneration) return;
                refusal = "the turn could not be processed: " + e.Message;
                break;
            }
            if (generation != autoPlayGeneration) return; // stopped (or the game replaced) while the turn was played
            if (!outcome.Accepted)
            {
                refusal = outcome.Message;
                await session.SubmitAsync(new Unciv.Commands.Turn.StopAutoPlay()); // review C047: a refusal the engine did not cause itself must not leave its auto-play running (the next Start would be refused); refused harmlessly when it already stopped
                break;
            }
            AutoPlay = AutoPlay.AfterTurn();
            Hud.SetAutoPlay(AutoPlay);
            if (AutoPlayModel.RefreshesAfter(AutoPlay.TurnsPlayed, autoPlayAnimations)) Refresh();
            else autoPlayStale = true; // T379: the HUD follows every turn through SnapshotChanged; the map, overlays and minimap catch up at the next redraw and when the run ends
            for (int i = 0; i < AutoPlayFramesPerTurn && AutoPlay.Active; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (generation != autoPlayGeneration) return;
            }
            if (!await WaitForAnimations(generation)) return;
        }
        FinishAutoPlay(stoppedByPlayer: false, refusal);
    }

    /// <summary>
    /// T376: gives the animations of the turn just played their time (frames, so a scripted run is exact): none when the run skips them, at most
    /// <see cref="AutoPlayModel.FastWaitFrames"/> when fast, until they end when normal. What is left then is dropped, so the queue never holds more than one turn and the next turn
    /// never starts behind a backlog. False when the run was stopped or replaced meanwhile.
    /// </summary>
    private async Task<bool> WaitForAnimations(int generation)
    {
        int frames = AutoPlayModel.WaitFrames(autoPlayAnimations);
        for (int i = 0; i < frames && !Presenter.IsIdle; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (generation != autoPlayGeneration) return false;
            if (Presenter.ManualStepping) Presenter.Step(1f / 60f);
        }
        Presenter.Complete();
        return true;
    }

    /// <summary>C047: the Stop control. Ends the run at once and tells the engine, so the next turn is the player's.</summary>
    public async Task StopAutoPlayAsync()
    {
        if (AutoPlay == null) return;
        autoPlayGeneration++;
        var result = await session.SubmitAsync(new Unciv.Commands.Turn.StopAutoPlay());
        if (!FinishAutoPlay(stoppedByPlayer: true, result.Accepted ? "" : result.Message)) Refresh();
    }

    /// <summary>Ends the run's bookkeeping; true when it redrew the screen because the last turns had not been drawn yet (T379).</summary>
    private bool FinishAutoPlay(bool stoppedByPlayer, string refusal)
    {
        if (AutoPlay == null) return false;
        bool redrawn = autoPlayStale;
        if (redrawn) Refresh(); // T379: the player takes over on the final state of the map, not on the last one that was drawn
        AutoPlayResult = AutoPlayModel.FinishedText(AutoPlay, stoppedByPlayer, refusal);
        AutoPlay = null;
        if (autoPlayAnimations == AutoPlayAnimations.Fast) Presenter.Complete(); // T376: the player takes over on the final state, not on a sped-up backlog
        Presenter.SpeedScale = 1f;
        Hud.SetAutoPlay(null);
        SetStatus(AutoPlayResult);
        Run(RefreshHud());
        return redrawn;
    }

    private async Task EndTurn()
    {
        if (turnCancellation != null) return; // already processing: a second press (or hotkey) must not end the next turn too
        using var cancellation = new CancellationTokenSource();
        turnCancellation = cancellation;
        var turnSession = session;
        SetStatus("", quiet: true); // T345: the card carries the turn's state; a previous result or cancellation line must not sit under the resumed turn
        Hud.HideBanner();
        Hud.SetTurnResolving(true);
        TurnOverlay.Start(NextTurnProgressModel.Begin(), NextTurnProgressModel.CancelHint);
        var progress = new Progress<TurnProgress>(report =>
        {
            // reports arrive posted to this thread, so one can land after the turn ended or the screen left the tree
            if (turnCancellation == cancellation && IsInsideTree()) TurnOverlay.SetProgress(NextTurnProgressModel.Of(report, DisplayName));
        });
        TurnOutcome? outcome = null;
        try
        {
            outcome = await turnSession.EndTurnAsync(progress, cancellation.Token);
        }
        catch (Exception e)
        {
            GD.PushError("End turn failed: " + e);
            if (IsInsideTree()) SetStatus("The turn could not be processed: " + e.Message);
        }
        finally
        {
            if (turnCancellation == cancellation) turnCancellation = null;
            if (IsInstanceValid(this) && IsInsideTree())
            {
                TurnOverlay.Close();
                Hud.SetTurnResolving(turnCancellation != null);
            }
        }
        if (!IsInstanceValid(this) || !IsInsideTree() || session != turnSession) return; // the player left the game meanwhile
        if (outcome is { Accepted: true, HandOverTo: { } next })
        {
            HandOverRequested?.Invoke(next); // before any redraw: the finished turn's view must never reach the screen again
            return;
        }
        Refresh();
        if (outcome == null) return;
        if (!outcome.Accepted && outcome.Message == ViewerSessionFlow.CancelledMessage)
        {
            SetStatus("Turn processing cancelled. Next turn continues it."); // T345
            return;
        }
        SetStatus(outcome.Accepted
            ? $"Turn {snapshot.Turn.ToString(CultureInfo.InvariantCulture)}: {outcome.AiTurnsRun.ToString(CultureInfo.InvariantCulture)} civ(s) played, {outcome.Events.Count.ToString(CultureInfo.InvariantCulture)} event(s)."
            : "Cannot end the turn: " + outcome.Message);
    }

    private string DisplayName(string civId) => civId;

    /// <summary>Observes a UI-triggered Task: a transport failure of the session (IGameSession error contract) is logged and shown instead of vanishing.</summary>
    private async void Run(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            GD.PushError("Session call failed: " + e);
            SetStatus("Connection problem: " + e.Message);
        }
    }

    /// <summary>
    /// C068: the world screen's one-line message. <see cref="StatusText"/> always holds the latest text (scripts read it), but the line is shown only for a message the player did not
    /// cause by looking (a refusal, a result, a path hint) and fades after <see cref="StatusFrames"/>; <paramref name="quiet"/> texts (selection, opened screens) duplicate what is on
    /// screen already and are never shown. <paramref name="shown"/> is the part to display when the text starts with context the unit panel shows.
    /// </summary>
    private void SetStatus(string text, bool quiet = false, string? shown = null)
    {
        statusText = text.TrText();
        statusLabel.Text = (shown ?? text).TrText();
        statusFrames = quiet || statusText.Length == 0 ? 0 : StatusFrames;
        statusPanel.Visible = statusFrames > 0;
        if (statusFrames > 0) LayoutStatus();
    }

    private void SetHint(string hint, bool quiet = false) => SetStatus($"{DescribeSelected()}   {hint}", quiet, hint);

    private void LayoutStatus()
    {
        // T363: the line is centred in the free gap the bottom-left unit panel and the bottom-right next-action dock leave, and wraps when a
        // longer message would run under either of them instead of being cut off by the dock (the BNW turns made that line long enough to hit it).
        float LocalX(float globalX) => globalX - GetGlobalRect().Position.X;
        float LocalY(float globalY) => globalY - GetGlobalRect().Position.Y;
        statusPanel.Size = Vector2.Zero;
        statusLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        statusLabel.CustomMinimumSize = Vector2.Zero;
        var oneLine = statusPanel.GetCombinedMinimumSize();
        var labelOneLine = statusLabel.GetCombinedMinimumSize();
        var padding = new Vector2(Math.Max(0f, oneLine.X - labelOneLine.X), Math.Max(0f, oneLine.Y - labelOneLine.Y));
        float left = HudMargin, right = Size.X - HudMargin, bottom = Size.Y - HudMargin;
        var unitPanel = Panel.Visible && Panel.Size.X > 0f ? Panel.GetGlobalRect() : default;
        if (unitPanel.Size.X > 0f) left = Math.Max(left, LocalX(unitPanel.End.X) + HudMargin);
        var dock = Hud.Dock;
        var dockRect = dock.IsVisibleInTree() && dock.Size.X > 0f ? dock.GetGlobalRect() : default;
        if (dockRect.Size.X > 0f) right = Math.Min(right, LocalX(dockRect.Position.X) - HudMargin);
        float minWidth = ui?.Px(120) ?? 120f;
        if (right - left < minWidth)
        {
            // Too narrow between them: put the line above the bottom controls rather than through one of them.
            left = HudMargin;
            right = Size.X - HudMargin;
            if (unitPanel.Size.Y > 0f) bottom = Math.Min(bottom, LocalY(unitPanel.Position.Y) - HudMargin);
            if (dockRect.Size.Y > 0f) bottom = Math.Min(bottom, LocalY(dockRect.Position.Y) - HudMargin);
        }
        float available = Math.Max(minWidth, right - left);
        float width = Math.Min(oneLine.X, available), height = oneLine.Y;
        if (oneLine.X > available + 0.5f)
        {
            statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            float wrapWidth = available - padding.X;
            statusLabel.CustomMinimumSize = new Vector2(wrapWidth, 0f);
            statusLabel.Size = new Vector2(wrapWidth, 0f); // reshape at the wrap width now: GetLineCount otherwise still reports the previous width's wrap
            statusPanel.Size = new Vector2(available, 0f);
            float textHeight = Math.Max(1, statusLabel.GetLineCount()) * (float)Math.Max(1, statusLabel.GetLineHeight());
            statusLabel.Size = new Vector2(wrapWidth, textHeight);
            // the panel clamps its size to its own minimum, so take the height from it: a line-spacing pixel per line would otherwise sit below the bottom margin
            height = Math.Max(textHeight + padding.Y, statusPanel.GetCombinedMinimumSize().Y);
        }
        statusPanel.Size = new Vector2(width, height);
        float x = left + Math.Max(0f, (right - left - width) / 2f);
        statusPanel.Position = new Vector2(x, bottom - height);
        statusLaidOutForSize = Size;
        statusLaidOutForUnitPanel = unitPanel;
        statusLaidOutForDock = dockRect;
    }

    /// <summary>
    /// T363: whether the controls the status line must stay clear of moved since it was last placed. The HUD container is rebuilt when the window crosses a breakpoint,
    /// and its dock reports size 0 (or its old rectangle) until the rebuilt containers sort, so a layout in that frame put the line under it; the line is laid out again
    /// on the first frame the dock's real rectangle is known.
    /// </summary>
    private bool StatusLayoutStale()
    {
        var unitPanel = Panel.Visible && Panel.Size.X > 0f ? Panel.GetGlobalRect() : default;
        var dock = Hud.Dock;
        var dockRect = dock.IsVisibleInTree() && dock.Size.X > 0f ? dock.GetGlobalRect() : default;
        return Size != statusLaidOutForSize || unitPanel != statusLaidOutForUnitPanel || dockRect != statusLaidOutForDock;
    }
}
