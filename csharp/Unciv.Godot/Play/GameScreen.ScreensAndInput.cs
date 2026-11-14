using Godot;
using Unciv.Assets;
using Unciv.Commands.Turn;
using Unciv.GodotClient.Map;
using Unciv.GodotClient.Settings;
using Unciv.GodotClient.Ui;
using Unciv.Logic.Map;
using Unciv.Models;
using Unciv.Models.Translations;
using Unciv.Net;
using Unciv.Net.Visibility;
using Unciv.Utils;

namespace Unciv.GodotClient.Play;

public partial class GameScreen
{
    /// <summary>Opens the policy screen, closing the city and diplomacy screens.</summary>
    public void OpenPolicyScreen()
    {
        ShowCity(null);
        if (Trade.Visible) Trade.Close();
        if (Diplomacy.Visible) Diplomacy.Close();
        if (Tech.Visible) Tech.Close();
        Deselect();
        SetStatus("Policies", quiet: true);
        Run(Policies.OpenAsync());
        ShowTutorial(TutorialTrigger.CultureAndPolicies);
    }

    /// <summary>Opens the diplomacy screen on the civ with this id (the first met civ when null), closing the city screen.</summary>
    public void OpenDiplomacyScreen(string? civId = null)
    {
        ShowCity(null);
        if (Policies.Visible) Policies.Close();
        Deselect();
        SetStatus("Diplomacy", quiet: true);
        if (Tech.Visible) Tech.Close();
        if (Overview.Visible) Overview.Close();
        if (Espionage.Visible) Espionage.Close();
        if (Trade.Visible) Trade.Close();
        Run(Diplomacy.OpenAsync(civId));
    }

    /// <summary>C023: the empire overview, opened by the Overview button.</summary>
    public OverviewScreen Overview { get; private set; } = null!; // JUSTIFY: C023 assigned in Start before any use

    public bool IsOverviewVisible => Overview.Visible;

    public Button OverviewButton => World.Overview;

    /// <summary>Opens the overview on a tab (the last shown when null), closing the other full screens.</summary>
    public void OpenOverviewScreen(string? tab = null)
    {
        ShowCity(null);
        if (Diplomacy.Visible) Diplomacy.Close();
        if (Tech.Visible) Tech.Close();
        Deselect();
        if (Trade.Visible) Trade.Close();
        if (Espionage.Visible) Espionage.Close();
        SetStatus("Overview", quiet: true);
        Run(Overview.OpenAsync(tab));
    }

    /// <summary>Closes the overview and shows a city of the viewer's: the camera goes to it and its screen opens.</summary>
    private void JumpToCity(string name)
    {
        var city = snapshot.Cities.FirstOrDefault(it => it.Owner == session.ViewerCivId && it.Name == name);
        if (city == null) return;
        Overview.Close();
        Map.FocusOn(MapLayout.TileTop(city.X, city.Y));
        ShowCity(city);
    }

    /// <summary>Closes the overview, selects one of the viewer's units and centres the camera on it.</summary>
    private void JumpToUnit(int id)
    {
        if (!unitNodes.TryGetValue(id, out var node) || !node.IsOwn) return;
        Overview.Close();
        SelectUnit(node);
        Map.FocusOn(UnitPosition(new HexCoord(node.Unit.X, node.Unit.Y), 0));
    }

    /// <summary>C022: the trade screen, opened by the Trade button.</summary>
    public TradeScreen Trade { get; private set; } = null!; // JUSTIFY: C022 assigned in Start before any use

    public bool IsTradeVisible => Trade.Visible;

    public Button TradeButton => World.Trade;

    /// <summary>Opens the trade screen on the civ with this id (the first met major civ when null), with the offer of <paramref name="stageFrom"/> staged as a counter proposal, closing the other screens.</summary>
    public void OpenTradeScreen(string? civId = null, PendingTradeDto? stageFrom = null)
    {
        ShowCity(null);
        if (Diplomacy.Visible) Diplomacy.Close();
        if (Tech.Visible) Tech.Close();
        if (Overview.Visible) Overview.Close();
        if (Espionage.Visible) Espionage.Close();
        Deselect();
        SetStatus("Trade", quiet: true);
        Run(Trade.OpenAsync(civId, stageFrom));
    }

    /// <summary>C027: the Civilopedia, opened by the Civilopedia button.</summary>
    public PediaScreen Pedia { get; private set; } = null!; // JUSTIFY: C027 assigned in Start before any use

    public bool IsPediaVisible => Pedia.Visible;

    public Button PediaButton => World.Civilopedia;

    /// <summary>Opens the Civilopedia on a link (<c>Unit/Warrior</c>), or where it was last left; it covers whatever screen is open and Close returns to it.</summary>
    public void OpenPediaScreen(string? link = null)
    {
        Tooltips.Unpoint();
        Pedia.Open(link);
    }

    /// <summary>C051: the espionage screen, opened by the Espionage button.</summary>
    public EspionageScreen Espionage { get; private set; } = null!; // JUSTIFY: C051 assigned in Start before any use

    public bool IsEspionageVisible => Espionage.Visible;

    public Button EspionageButton => World.Espionage;

    /// <summary>Opens the espionage screen, closing the other full screens.</summary>
    public void OpenEspionageScreen()
    {
        ShowCity(null);
        if (Diplomacy.Visible) Diplomacy.Close();
        if (Tech.Visible) Tech.Close();
        if (Policies.Visible) Policies.Close();
        if (Overview.Visible) Overview.Close();
        if (Trade.Visible) Trade.Close();
        Deselect();
        SetStatus("Espionage", quiet: true);
        Run(Espionage.OpenAsync());
    }

    /// <summary>C027: the one tooltip of the game screen; the map feeds it the tile under the pointer.</summary>
    public TooltipHost Tooltips { get; private set; } = null!; // JUSTIFY: C027 assigned in Start before any use

    /// <summary>The tile under the pointer (null over the HUD or off the map) and the BBCode its tooltip will show once the engine answered ("" until then).</summary>
    public HexCoord? HoveredTile { get; private set; }

    public string HoverTip { get; private set; } = "";

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Map != null && Tooltips != null && !DisplayProfile.Current.Touch) UpdateHover(motion.Position); // C100: a finger does not hover; a tap shows the tile tip
        if (@event is InputEventMouseButton { Pressed: true } press && MarkerMenu is { Visible: true } menu && !menu.GetGlobalRect().HasPoint(press.Position)) menu.Close(); // C089: any click outside the marker menu closes it
    }

    /// <summary>Follows the pointer: over the map (no widget under it) the tile under it is asked of the engine for the tooltip; over a widget the tip goes away.</summary>
    private void UpdateHover(Vector2 pointer)
    {
        var over = GetViewport().GuiGetHoveredControl();
        ShowTileTip(over == null || over == this ? Map.PickTile(pointer) : null, null);
    }

    /// <summary>Shows the tooltip of <paramref name="tile"/> (hides it for null). <paramref name="touchAt"/> is a tap's position (C100): the tip then shows above it at once, even when the tile is the one shown last.</summary>
    private void ShowTileTip(HexCoord? tile, Vector2? touchAt)
    {
        if (tile == HoveredTile && touchAt == null) return;
        HoveredTile = tile;
        HoverTip = "";
        SiteOverlay.Clear();
        Tooltips.Restart();
        if (tile is not { } coord)
        {
            Tooltips.Unpoint();
            return;
        }
        Tooltips.Point(() => HoverTip, touchAt);
        Run(LoadHoverTip(coord));
    }

    private async Task LoadHoverTip(HexCoord coord)
    {
        var info = await queries.TileInfo(coord.X, coord.Y);
        if (HoveredTile != coord || info == null) return; // the pointer moved on, or the engine would not describe an unexplored tile
        HoverTip = TooltipModel.ToBbcode(TooltipModel.Tile(info, session.Ruleset), text => text.Tr());
        if (!foundsCities) return;
        // C088: with a settler selected the tooltip says what a city here would get (it replaces the tile's own text, which would cover the map), and the overlay marks its work radius
        var site = await queries.CitySite(coord.X, coord.Y);
        if (HoveredTile != coord || !foundsCities || site == null) return;
        HoverTip = TooltipModel.ToBbcode(TooltipModel.CitySite(site), text => text.Tr());
        SiteOverlay.Show([.. site.Tiles.Select(it => new HexCoord(it.X, it.Y))], TileTopAt);
    }

    /// <summary>C019: the tech screen, opened by the Technology button.</summary>
    public TechScreen Tech { get; private set; } = null!; // JUSTIFY: C019 assigned in Start before any use

    public bool IsTechVisible => Tech.Visible;

    public Button TechButton => World.Technology;

    /// <summary>Opens the tech screen on a tech (the current research when null), closing the city and diplomacy screens.</summary>
    public void OpenTechScreen(string? tech = null)
    {
        ShowCity(null);
        if (Diplomacy.Visible) Diplomacy.Close();
        if (Policies.Visible) Policies.Close();
        if (Overview.Visible) Overview.Close();
        if (Espionage.Visible) Espionage.Close();
        if (Trade.Visible) Trade.Close();
        Deselect();
        SetStatus("Technology", quiet: true);
        Run(Tech.OpenAsync(tech));
    }

    private ClientSettingsService? boundSettings;
    /// <summary>C026: false keeps tutorial popups away (scenarios that are about something else turn them off; the game never does).</summary>
    public bool TutorialsEnabled { get; set; } = true;

    /// <summary>
    /// C054: shows the floating tutorial task card (needs <see cref="BindSettings"/> with Show tutorials on). The game turns it on; scenarios that are about
    /// something else leave it off so their pictures do not change.
    /// </summary>
    public bool TaskCardEnabled { get; set; }

    /// <summary>C054: the floating card of the current tutorial task.</summary>
    public TutorialTaskCard TaskCard { get; private set; } = null!; // JUSTIFY: C054 assigned in _Ready before any use

    private readonly HashSet<string> reportedTasks = [];

    private TutorialQueue tutorialQueue = null!; // JUSTIFY: C026 assigned in Start before any use

    /// <summary>
    /// C026: queues a tutorial (shown unless tutorials are off or it was seen) and shows the first waiting one. Tutorials appear only when the screen follows client
    /// settings: without a binding there is nowhere to remember what was seen, so nothing is shown.
    /// </summary>
    public void ShowTutorial(TutorialTrigger trigger)
    {
        if (!TutorialsEnabled || boundSettings == null || tutorialQueue == null) return;
        if (tutorialQueue.Enqueue(trigger, boundSettings.Current) == TutorialEnqueue.NothingToShow) MarkTutorialSeen(trigger);
        OpenTutorialPage();
    }

    private void CheckTutorials()
    {
        if (!TutorialsEnabled || boundSettings == null || !boundSettings.Current.ShowTutorials || GameOver.Visible || VoteResult.Visible) return;
        foreach (var trigger in TutorialModel.Detect(snapshot, session.Ruleset))
        {
            if (tutorialQueue.Enqueue(trigger, boundSettings.Current) == TutorialEnqueue.NothingToShow) MarkTutorialSeen(trigger);
        }
        OpenTutorialPage();
    }

    private void OpenTutorialPage()
    {
        if (Tutorials.Visible) return;
        if (tutorialQueue.Current is { } page) Tutorials.Open(page);
    }

    private void DismissTutorial()
    {
        if (tutorialQueue.Dismiss() is { } finished) MarkTutorialSeen(finished);
        if (tutorialQueue.Current is { } page) Tutorials.Open(page);
        else Tutorials.Close();
    }

    /// <summary>T299: "Never show tutorials" (popup or task card): turns the persisted setting off; <see cref="ApplySettings"/> then removes everything showing.</summary>
    private void NeverShowTutorials()
    {
        if (boundSettings == null) return;
        boundSettings.Update(s => s with { ShowTutorials = false });
        StopTutorials(); // also when the save failed: gone for this session at least
    }

    /// <summary>T299: closes the tutorial popup, forgets the waiting tutorials (they are not marked seen, so turning the setting back on shows them) and hides the task card.</summary>
    private void StopTutorials()
    {
        tutorialQueue?.Clear();
        Tutorials.Close();
        TaskCard.Show(null);
    }

    private void MarkTutorialSeen(TutorialTrigger trigger) => boundSettings?.Update(s => s.WithTutorialSeen(TutorialModel.SeenKey(trigger)));

    /// <summary>
    /// C028: follows the client settings: key bindings (map pan keys and the screen's hotkeys), scroll speed, edge-pan and the resource toggle apply now and after every
    /// change. Call after the screen entered the tree; calling it again replaces the earlier binding.
    /// </summary>
    public void BindSettings(ClientSettingsService settings)
    {
        if (boundSettings != null) boundSettings.Changed -= ApplySettings;
        boundSettings = settings;
        settings.Changed += ApplySettings;
        Overview?.BindSettings(settings);
        ApplySettings(settings.Current);
        if (session != null) CheckTutorials(); // the first look at the game came before the settings were known
    }

    private void ApplySettings(ClientSettings settings)
    {
        if (!settings.ShowTutorials) StopTutorials();
        if (!settings.ConfirmDestructiveActions)
        {
            Panel.CancelConfirmation();
            if (City != null) City.CancelConfirmation();
            if (Diplomacy != null) Diplomacy.CancelConfirmation();
        }
        if (City != null) City.ConfirmDestructive = settings.ConfirmDestructiveActions;
        if (Diplomacy != null) Diplomacy.ConfirmDestructive = settings.ConfirmDestructiveActions;
        if (session != null && snapshot != null) Run(RefreshTaskCard()); // C054: Show tutorials may have changed
        if (session != null && snapshot != null) RefreshMarkers();
        hotkeys = settings.KeyBindings;
        World.SetKeys(KeyOf);
        Hud.EndTurnKey = KeyOf(HotkeyAction.EndTurn);
        Panel.SetKeys(KeyOf);
        Map.KeyBindings = settings.KeyBindings;
        Map.ScrollSpeed = settings.ScrollSpeed;
        Map.EdgePan = settings.EdgePan;
        Map.ShowResources = settings.ShowResources;
        ApplyGraphics(settings);
        Overlays.ShowYields = settings.ShowYields;
        var nextMode = AccessibilityModel.ParseColorblindMode(settings.ColorblindMode);
        if (nextMode != colorblindMode)
        {
            colorblindMode = nextMode;
            if (session != null && snapshot != null) Refresh();
        }
        Hud.ShowAutoPlayButton(settings.AutoPlay.ShowButton);
        if (!settings.AutoPlay.ShowButton)
        {
            AutoPlayMenu.Close();
            if (AutoPlay != null) Run(StopAutoPlayAsync());
        }
    }

    /// <summary>C097: the graphics level in effect follows the setting (Ultra only where the renderer shows it; harness scenarios pin Low, see <see cref="GraphicsRuntime"/>); the unit level of detail follows its caps.</summary>
    private void ApplyGraphics(ClientSettings settings)
    {
        var preset = GraphicsModel.Preset(GraphicsRuntime.EffectiveFor(settings));
        if (preset == Map.Graphics) return;
        Map.ApplyGraphics(preset);
        if (session != null && snapshot != null) ApplyUnitLod();
    }

    private IReadOnlyDictionary<string, string> hotkeys = new Dictionary<string, string>();
    private ColorblindMode colorblindMode = ColorblindMode.Off;

    /// <summary>T352: the tile currently targeted by the keyboard map cursor (Shift+Arrows to step, Enter to select/order).</summary>
    public HexCoord? CursorTile { get; private set; }

    /// <summary>C028: runs the action a hotkey text is bound to (true when it was one of the game screen's actions and ran). Ignored while a full screen (city, diplomacy, tech, policies, overview, game over) or an alert covers the map.</summary>
    public bool TryHotkey(string keyText)
    {
        if (Tutorials.Visible)
        {
            if (keyText != "Space") return false;
            Tutorials.OkButton.EmitSignal(BaseButton.SignalName.Pressed);
            return true;
        }
        if (Pedia.Visible) // only its own key (toggle) works over the Civilopedia
        {
            if (Hotkeys.Resolve(hotkeys, keyText) != HotkeyAction.OpenPedia) return false;
            Pedia.Close();
            return true;
        }
        if (GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit) return false; // keys belong to a text field being typed in (Ctrl and F keys are not swallowed by it)
        if (!IsVisibleInTree() || MarkerEditor?.Visible == true || GameMenu.Visible || VictoryStatus.Visible || Trade.Visible || Picker.Visible || AutoPlayMenu.Visible) return false; // covered or hidden (the options screen hides this one)
        if (ConsolePopup.Visible) return false; // keys belong to the console's input while it is open
        if (IsEndingTurn || City.Visible || Diplomacy.Visible || Tech.Visible || Policies.Visible || Overview.Visible || Espionage.Visible || GameOver.Visible || VoteResult.Visible || Alerts.Visible || Proposals.Visible) return false;
        switch (Hotkeys.Resolve(hotkeys, keyText))
        {
            case HotkeyAction.EndTurn or HotkeyAction.EndTurnAlternate:
                OnNextTurn();
                return true;
            case HotkeyAction.NextUnit:
                SelectNextUnit();
                return true;
            case HotkeyAction.OpenTech:
                OpenTechScreen();
                return true;
            case HotkeyAction.OpenPolicies:
                OpenPolicyScreen();
                return true;
            case HotkeyAction.OpenDiplomacy:
                OpenDiplomacyScreen();
                return true;
            case HotkeyAction.OpenConsole:
                OpenConsole();
                return true;
            case HotkeyAction.OpenPedia:
                if (Pedia.Visible) Pedia.Close();
                else OpenPediaScreen();
                return true;
            case HotkeyAction.OpenOverview:
                OpenOverviewScreen();
                return true;
            case HotkeyAction.OverviewCities:
                OpenOverviewScreen(OverviewModel.Cities);
                return true;
            case HotkeyAction.OverviewUnits:
                OpenOverviewScreen(OverviewModel.Units);
                return true;
            case HotkeyAction.OverviewPolitics:
                OpenOverviewScreen(OverviewModel.Politics);
                return true;
            case HotkeyAction.OverviewStats:
                OpenOverviewScreen(OverviewModel.Stats);
                return true;
            case HotkeyAction.OverviewResources:
                OpenOverviewScreen(OverviewModel.Resources);
                return true;
            case HotkeyAction.OpenVictory:
                Run(OnMenuChosen(GameMenuKind.VictoryStatus));
                return true;
            case HotkeyAction.SaveGame:
                Run(OnMenuChosen(GameMenuKind.Save));
                return true;
            case HotkeyAction.LoadGame:
                Run(OnMenuChosen(GameMenuKind.Load));
                return true;
            case HotkeyAction.OpenOptions:
                Run(OnMenuChosen(GameMenuKind.Options));
                return true;
            case HotkeyAction.ViewCapital:
                return ViewCapital();
            case HotkeyAction.ToggleResources:
                boundSettings?.Update(s => s with { ShowResources = !s.ShowResources });
                return boundSettings != null;
            case HotkeyAction.PlaceMarker:
                OpenMarkerEditor(HoveredTile);
                return true;
            case HotkeyAction.PingMap:
                if (social == null || HoveredTile is not { } pinged) return false;
                Run(SendPing(pinged, MapPingKinds.Look));
                return true;
            case HotkeyAction.ToggleYields:
                boundSettings?.Update(s => s with { ShowYields = !s.ShowYields });
                return boundSettings != null;
            default:
                return TryUnitHotkey(keyText); // a pan key can also be a unit action's key (A: air sweep)
        }
    }

    /// <summary>C078: centres the camera on the viewer's capital (false when it has none).</summary>
    private bool ViewCapital()
    {
        if (session == null || snapshot == null) return false;
        var capital = snapshot.Cities.FirstOrDefault(it => it.Owner == session.ViewerCivId && it.IsCapital);
        if (capital == null) return false;
        Map.FocusOn(MapLayout.TileTop(capital.X, capital.Y));
        return true;
    }

    /// <summary>
    /// C078: runs the selected unit's menu entry whose unit action is bound to <paramref name="keyText"/> (the first enabled one: several actions share a key but a unit is offered
    /// at most one of them). Entries are the panel's own, so a hotkey does exactly what its button does, confirmation included. True when one ran.
    /// </summary>
    private bool TryUnitHotkey(string keyText)
    {
        if (!Panel.Visible || SelectedUnitId == null || Panel.IsAsking) return false;
        var bound = Hotkeys.ResolveUnit(hotkeys, keyText);
        if (bound.Count == 0) return false;
        var entry = Panel.Entries.FirstOrDefault(it => it.Enabled && Hotkeys.ForEntry(it) is { } action && bound.Contains(action));
        if (entry == null) return false;
        OnMenuEntry(entry);
        return true;
    }

    /// <summary>C078: the key a hotkey action shows in tooltips and key chips (the bound key, as the options page shows it).</summary>
    private string KeyOf(HotkeyAction action) => Hotkeys.KeyFor(hotkeys, action);

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && IsVisibleInTree() && TryEscape()) GetViewport().SetInputAsHandled();
        if (@event is InputEventKey { Pressed: true, Echo: false } cursorKey && TryCursorKey(cursorKey)) { GetViewport().SetInputAsHandled(); return; }
        if (@event is InputEventKey { Pressed: true, Echo: false } key && HotkeyInput.TextOf(key) is { } text && TryHotkey(text)) GetViewport().SetInputAsHandled();
    }

    /// <summary>T352: keyboard map cursor navigation (Shift+Arrows) and tile activation (Enter/Space when no UI control has focus).</summary>
    private bool TryCursorKey(InputEventKey key)
    {
        if (!IsVisibleInTree() || session == null || snapshot == null || IsEndingTurn) return false;
        if (GetViewport()?.GuiGetFocusOwner() is Control focused && focused is LineEdit or TextEdit) return false;
        if (Tutorials.Visible || Pedia.Visible || GameMenu.Visible || ConsolePopup.Visible || City.Visible || Diplomacy.Visible || Tech.Visible || Policies.Visible || Overview.Visible || GameOver.Visible) return false;
        if (key.ShiftPressed)
        {
            var step = key.Keycode switch
            {
                Key.Up => (0, 1),
                Key.Down => (0, -1),
                Key.Left => (-1, 0),
                Key.Right => (1, 0),
                _ => (0, 0),
            };
            if (step != (0, 0)) return StepMapCursor(step.Item1, step.Item2);
        }
        if (key.Keycode is Key.Enter or Key.KpEnter && GetViewport()?.GuiGetFocusOwner() == null && CursorTile is { } cursor)
        {
            OnTileClicked(cursor);
            return true;
        }
        return false;
    }

    public bool StepMapCursor(int dx, int dy)
    {
        if (session == null || snapshot == null || snapshot.Tiles.Count == 0) return false;
        var current = CursorTile ?? Map.Selected ?? (snapshot.Cities.FirstOrDefault(it => it.Owner == session.ViewerCivId) is { } cap ? new HexCoord(cap.X, cap.Y) : new HexCoord(snapshot.Tiles[0].X, snapshot.Tiles[0].Y));
        var next = AccessibilityModel.StepCursor(current, dx, dy);
        if (Map.TileNodeAt(next) == null) return false;
        CursorTile = next;
        Map.Select(next);
        Map.FocusOn(MapLayout.TileTop(next.X, next.Y));
        return true;
    }

    /// <summary>
    /// C044/T345: Escape closes the menu or the victory page when one is open, cancels a running local turn (the overlay says so), else opens the menu unless a
    /// screen or popup already covers the map (those take Escape themselves).
    /// </summary>
    private bool TryEscape()
    {
        if (AutoPlayMenu.Visible)
        {
            AutoPlayMenu.Close();
            return true;
        }
        if (ConsolePopup.Visible) return ConsolePopup.Cancel();
        if (MarkerMenu.Visible)
        {
            MarkerMenu.Close();
            return true;
        }
        if (GameMenu.Visible) return GameMenu.Cancel();
        if (VictoryStatus.Visible)
        {
            VictoryStatus.Close();
            return true;
        }
        if (City.Visible || Diplomacy.Visible || Tech.Visible || Policies.Visible || Trade.Visible || Overview.Visible || Espionage.Visible || Alerts.Visible || Proposals.Visible || GameOver.Visible || VoteResult.Visible) return false;
        if (IsEndingTurn)
        {
            CancelTurnProcessing();
            return true;
        }
        OpenGameMenu();
        return true;
    }

    /// <summary>
    /// T345: Esc while the local End turn runs. The turn flow stops between two civs (never inside one), keeps what was played and leaves the round mid-way;
    /// pressing Next turn again continues with the next civ, so nothing is lost and no turn is ended twice.
    /// </summary>
    public void CancelTurnProcessing()
    {
        if (turnCancellation == null) return;
        SetStatus("Cancelling the turn...");
        turnCancellation.Cancel();
    }

    /// <summary>C046: opens the developer console with the history and "Keep open" choice from the client settings (the screen's own memory when no settings are bound).</summary>
    public void OpenConsole()
    {
        if (session == null) return;
        ConsolePopup.Open(boundSettings?.Current.ConsoleHistory ?? ConsolePopup.History, boundSettings?.Current.KeepConsoleOpen ?? ConsolePopup.KeepOpen);
    }

    /// <summary>Opens the in-game menu. The entries depend on the engine's game-over status (a defeated civ cannot resign) and on whether the game has unsaved progress.</summary>
    public void OpenGameMenu() => Run(OpenGameMenuAsync());

    private async Task OpenGameMenuAsync()
    {
        // T345: a menu must open while the worker owns the engine gate; the game-over query would wait for it, so use what the snapshot already says.
        string status = Unciv.Queries.Victory.GameOverQuery.Playing;
        if (IsEndingTurn)
        {
            if (snapshot.Civs.Any(it => it.CivId == session.ViewerCivId && it.IsDefeated)) status = Unciv.Queries.Victory.GameOverQuery.Defeated;
        }
        else
        {
            var outcome = await queries.GameOver();
            if (outcome?.Status is { } reported) status = reported;
        }
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        bool unsaved = HasUnsavedProgress?.Invoke() ?? true;
        GameMenu.Open(GameMenuModel.Build(status, unsaved), hotkeys);
    }

    private async Task OnMenuChosen(GameMenuKind kind)
    {
        GameMenu.Close();
        switch (kind)
        {
            case GameMenuKind.Save:
                SaveRequested?.Invoke();
                break;
            case GameMenuKind.Load:
                LoadRequested?.Invoke();
                break;
            case GameMenuKind.NewGame:
                NewGameRequested?.Invoke();
                break;
            case GameMenuKind.Options:
                ReportTaskDone(TutorialTaskModel.OpenOptions);
                OptionsRequested?.Invoke();
                break;
            case GameMenuKind.MainMenu:
                MainMenuRequested?.Invoke();
                break;
            case GameMenuKind.VictoryStatus:
                VictoryStatus.Open(VictoryStatusModel.Build(await queries.VictoryProgress()));
                break;
            case GameMenuKind.Resign:
                await Resign();
                break;
        }
    }

    /// <summary>The engine validates the actor and does the change; the screen only sends the command and shows what is left: the resigned screen with the way out.</summary>
    private async Task Resign()
    {
        var result = await session.SubmitAsync(new Resign());
        if (!result.Accepted)
        {
            SetStatus("Resign refused: " + result.Message);
            return;
        }
        var rankings = await queries.Rankings();
        var history = await queries.History();
        resigned = true;
        gameOverCheck++; // a check that was already asking must not close the screen
        Refresh();
        GameOver.Open(GameOverModel.BuildResigned(rankings, history), civ => OwnerColour(civ));
        SetStatus("You resigned.");
    }

    /// <summary>Opens the city screen for a city of the snapshot (the viewer's own in full, any other from its snapshot entry), or closes it with null.</summary>
    public void OpenCityScreen(CityDto? city) => ShowCity(city);

    public IReadOnlyDictionary<int, UnitNode> Units => unitNodes;

}
