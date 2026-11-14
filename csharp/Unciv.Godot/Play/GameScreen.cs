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
using Unciv.GodotClient.Setup;
using Unciv.GodotClient.Ui;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models;
using Unciv.Models.Translations;
using Unciv.Net;
using Unciv.Net.Visibility;
using Unciv.Queries.Empire;
using Unciv.Queries.Units;
using Unciv.Utils;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C010: the playable vertical slice. The map (C009) with a coloured block per visible unit, click to select one of your units, a path of dots to a
/// destination, a second click on it (or a right click) to move, and a plain Next turn button that ends the turn, lets the AI civs play and shows the result.
/// The screen reads the world only from the viewer's <see cref="ClientSnapshot"/> and the preview queries, and changes it only by submitting commands through
/// <see cref="LocalHost"/>; after every accepted command it redraws from a fresh snapshot.
/// </summary>
public partial class GameScreen : Control
{
    private const float DotSpacing = 0.4f;
    private const float DotLift = 0.12f;
    private const float StackOffset = 0.28f;
    private const float SelectedScale = 1.3f;
    private const float HudMargin = 12f;

    private readonly Dictionary<int, UnitNode> unitNodes = [];
    private IGameSession session = null!; // JUSTIFY: C010 assigned by Start before the screen is used
    private AssetRegistry assets = null!; // JUSTIFY: C010 assigned by Start before the screen is used
    private GameQueries queries = null!; // JUSTIFY: C010 assigned by Start before the screen is used
    private Node3D unitLayer = null!; // JUSTIFY: C010 assigned in _Ready before any use
    private Node3D dotLayer = null!; // JUSTIFY: C010 assigned in _Ready before any use
    private Label statusLabel = null!; // JUSTIFY: C010 assigned in _Ready before any use
    private PanelContainer statusPanel = null!; // JUSTIFY: C068 assigned in _Ready before any use
    private string statusText = "";
    private int statusFrames;
    private const int StatusFrames = 240; // frames, not seconds: captures must not depend on machine speed
    private Vector2 statusLaidOutForSize; // T363: the geometry the status line was last placed against, so a late HUD layout can trigger another pass
    private Rect2 statusLaidOutForUnitPanel;
    private Rect2 statusLaidOutForDock;
    private int bannerTurn = -1;
    private int rivalsTurn = -1;
    private int rivalsRequest;
    private ClientSnapshot snapshot = null!; // JUSTIFY: C010 assigned by Refresh in Start
    private int previewRequest;
    private PathPreviewResult? preview;
    private bool mapLoaded;
    private int selectionRequest;
    private HashSet<(int X, int Y)> attackTiles = [];
    private (string Kind, HexCoord Target)? pendingAttack;

    public MapView Map { get; private set; } = null!; // JUSTIFY: C010 assigned in _Ready before any use

    public WorldHud World { get; private set; } = null!; // JUSTIFY: C067 assigned in _Ready before any use

    public HudView Hud { get; private set; } = null!; // JUSTIFY: C011 assigned in _Ready before any use

    public MinimapView Minimap { get; private set; } = null!; // JUSTIFY: C014 assigned in _Ready before any use

    private TileOwnerMemoryStore? ownerStore;
    private readonly TileOwnerMemory ownerMemory = new(); // what this screen has seen of tile ownership, so fogged tiles keep their last-known owner (T298)

    /// <summary>Cities, borders, roads and improvements drawn on the map (C041).</summary>
    public MapOverlayLayer Overlays { get; private set; } = null!; // JUSTIFY: C041 assigned in _Ready before any use

    public UnitPanelView Panel { get; private set; } = null!; // JUSTIFY: C012 assigned in _Ready before any use

    public CombatPreviewView CombatPreview { get; private set; } = null!; // JUSTIFY: C012 assigned in _Ready before any use

    public TileOverlay ReachableOverlay { get; private set; } = null!; // JUSTIFY: C012 assigned in _Ready before any use

    /// <summary>C088: the work radius of the city site under the pointer while a unit that can found a city is selected.</summary>
    public TileOverlay SiteOverlay { get; private set; } = null!; // JUSTIFY: C088 assigned in _Ready before any use

    /// <summary>C089: the player's private map markers (pins and labels), drawn from the client settings only.</summary>
    public MapMarkerLayer MarkerLayer { get; private set; } = null!; // JUSTIFY: C089 assigned in _Ready before any use

    /// <summary>C089: the marker editor and list (built again for every game started on this screen).</summary>
    public MapMarkerView? MarkerEditor { get; private set; }

    /// <summary>C089: the context menu a right-click on a tile opens when no unit is selected.</summary>
    public MapMarkerMenuView MarkerMenu { get; private set; } = null!; // JUSTIFY: C089 assigned in _Ready before any use

    /// <summary>C090: the allies' map pings (rings, a column and the sender's name), shown for <see cref="MapPingModel.Lifetime"/> seconds. Never stored or sent anywhere.</summary>
    public MapPingLayer PingLayer { get; private set; } = null!; // JUSTIFY: C090 assigned in _Ready before any use

    private bool foundsCities;

    public TileOverlay AttackOverlay { get; private set; } = null!; // JUSTIFY: C012 assigned in _Ready before any use

    /// <summary>C033: plays the movement and combat events of every command on the map. Off until a host turns <see cref="CombatPresenter.Enabled"/> on.</summary>
    public CombatPresenter Presenter { get; } = new() { Name = "Presenter" };

    public int? SelectedUnitId { get; private set; }

    // C091: the units shift-clicked into the selection besides the selected one, and the escort pairs of each viewer (client-side only; every move is an ordinary command)
    private IReadOnlyList<int> extraGroupIds = [];
    private readonly Dictionary<string, List<EscortPair>> escorts = [];

    /// <summary>C091: the selected unit and the shift-clicked ones in the order they were added; empty unless two or more are selected.</summary>
    public IReadOnlyList<int> GroupUnitIds => extraGroupIds.Count == 0 || SelectedUnitId is not { } primary ? [] : [primary, .. extraGroupIds];

    /// <summary>C091: the escort pairs of the viewer.</summary>
    public IReadOnlyList<EscortPair> Escorts => escorts.TryGetValue(session?.ViewerCivId ?? "", out var list) ? list : [];

    /// <summary>C013: the clickable notification list.</summary>
    public NotificationListView Notifications { get; private set; } = null!; // JUSTIFY: C013 assigned in _Ready before any use

    /// <summary>C013: the modal popup for the first pending alert.</summary>
    public AlertPopupView Alerts { get; private set; } = null!; // JUSTIFY: C013 assigned in _Ready before any use

    /// <summary>C039: the modal popup for the first incoming trade or peace proposal.</summary>
    public TradeProposalView Proposals { get; private set; } = null!; // JUSTIFY: C039 assigned in _Ready before any use

    /// <summary>C026: the tutorial popup; it shows only while the screen follows client settings (<see cref="BindSettings"/>) that have tutorials on.</summary>
    public TutorialPopupView Tutorials { get; private set; } = null!; // JUSTIFY: C026 assigned in _Ready before any use

    /// <summary>C053: the World Congress result, above the world and below the victory screen.</summary>
    public VoteResultScreen VoteResult { get; private set; } = null!; // JUSTIFY: C053 assigned in _Ready before any use

    /// <summary>C024: the victory/defeat screen, above everything else.</summary>
    public GameOverScreen GameOver { get; private set; } = null!; // JUSTIFY: C024 assigned in _Ready before any use

    /// <summary>Raised when the player leaves the finished (or any) game from the game-over screen or the menu; the composition root shows the main menu.</summary>
    public event Action? MainMenuRequested;

    /// <summary>C044: the in-game menu (Escape or the Menu button).</summary>
    public GameMenuView GameMenu { get; private set; } = null!; // JUSTIFY: C044 assigned in _Ready before any use

    /// <summary>C044: the menu's "Victory status" page.</summary>
    public VictoryStatusView VictoryStatus { get; private set; } = null!; // JUSTIFY: C044 assigned in _Ready before any use

    /// <summary>C046: the developer console (the console hotkey); it does not block the map, so tiles and units can be selected for the next line.</summary>
    public ConsoleView ConsolePopup { get; private set; } = null!; // JUSTIFY: C046 assigned in _Ready before any use

    public Button MenuButton => World.Menu;

    /// <summary>
    /// C044: the menu asks the owner whether the game has progress no save holds (the save controller knows). Without an owner's answer the menu assumes it has, so a
    /// destructive entry asks.
    /// </summary>
    public Func<bool>? HasUnsavedProgress { get; set; }

    /// <summary>C044: the owner opens its save dialog / the load dialog / the new-game setup / the options. The game screen keeps no files and no settings of its own.</summary>
    public event Action? SaveRequested;

    public event Action? LoadRequested;

    public event Action? NewGameRequested;

    public event Action? OptionsRequested;

    private bool resigned;

    /// <summary>C047: the menu of the AutoPlay button.</summary>
    public AutoPlayMenuView AutoPlayMenu { get; private set; } = null!; // JUSTIFY: C047 assigned in _Ready before any use

    /// <summary>C047: the running AutoPlay as the client tracks it, null while idle.</summary>
    public AutoPlayRun? AutoPlay { get; private set; }

    /// <summary>C047: the last finished run's status line (also shown in the status label).</summary>
    public string? AutoPlayResult { get; private set; }

    /// <summary>C047: frames to wait between two auto-played turns, so the player sees each turn and can press Stop; scenarios raise it to capture a run midway.</summary>
    public int AutoPlayFramesPerTurn { get; set; } = 1;

    private int autoPlayGeneration;

    /// <summary>T376: what the running AutoPlay animates (fixed when the run starts).</summary>
    private AutoPlayAnimations autoPlayAnimations;

    /// <summary>C040/T345: the overlay shown (after a delay) while End turn is processed off the UI thread.</summary>
    public NextTurnProgressView TurnOverlay { get; private set; } = null!; // JUSTIFY: C040 assigned in _Ready before any use

    /// <summary>C040/T345: True from pressing Next turn until the host answered; a turn can be cancelled (Esc) but the map, camera and menus stay usable.</summary>
    public bool IsEndingTurn => turnCancellation != null;

    /// <summary>C036: the multiplayer turn bar (who ended, deadline, connection marks, toasts); hidden unless the session is a simultaneous game (<see cref="ITurnRoster"/>).</summary>
    public TurnBarView TurnBar { get; private set; } = null!; // JUSTIFY: C036 assigned in _Ready before any use

    /// <summary>C045: the players-and-chat panel of a hosted game (the Players button toggles it); the button is hidden unless the session is an <see cref="IGameSocial"/>.</summary>
    public SocialView Social { get; private set; } = null!; // JUSTIFY: C045 assigned in _Ready before any use

    public Button PlayersButton => World.Players;

    private IGameSocial? social;

    /// <summary>C036: the clock the countdown reads (Unix ms); wall time in the game, fixed in scenarios.</summary>
    public Func<long> NowMs { get; set; } = static () => (long)(Time.GetUnixTimeFromSystem() * 1000.0);

    /// <summary>C036: true while the host resolves the round of a simultaneous game.</summary>
    public bool IsResolving => snapshot?.TurnStatus.Phase == "Resolving" && session is ITurnRoster;

    private string? shownCountdown;
    private string? shownGrace; // T346: the soonest drop-grace countdown drawn, so the bar moves with the clock

    /// <summary>The turn bar sits centred below the two rows of screen buttons, so it covers none of them.</summary>
    private float TurnBarTop() => World.NavBottom + HudMargin;

    /// <summary>T345: the progress card goes under the turn bar when that is shown, so it never covers the bar it reports on; otherwise it takes the bar's place.</summary>
    private float OverlayTop() => TurnBarTop() + (TurnBar.Visible ? TurnBar.PanelSize.Y + HudMargin : 0f);

    private CancellationTokenSource? turnCancellation;

    /// <summary>
    /// C029: raised, with the civ id, when End turn finished and the next turn belongs to another human on this device (hotseat). The listener must <see cref="Conceal"/> the screen at once (nothing is
    /// drawn from the finished turn first), show the pass-the-device cover and later <see cref="Start"/> the screen with the other human's session and <see cref="Reveal"/> it.
    /// </summary>
    public event Action<string>? HandOverRequested;

    /// <summary>C029: the session the screen currently shows (a hotseat owner swaps it for the next human's with <see cref="Start"/>).</summary>
    public IGameSession? Session => session;

    /// <summary>C029: true between <see cref="Conceal"/> and <see cref="Reveal"/>: nothing of the game is drawn and no input reaches it.</summary>
    public bool IsConcealed { get; private set; }

    /// <summary>
    /// C029: hides everything the previous player could see (map, units, overlays, HUD, panels, popups) and stops the screen taking input. Used while the device changes hands in a hotseat game;
    /// the state itself is replaced by the next <see cref="Start"/>. Safe to call repeatedly.
    /// </summary>
    public void Conceal()
    {
        IsConcealed = true;
        turnCancellation?.Cancel();
        Alerts.Close();
        Proposals.Close();
        Picker.Close();
        GameMenu.Close();
        AutoPlayMenu.Close();
        ConsolePopup.Close();
        MarkerMenu.Close();
        MarkerEditor?.Close();
        Tutorials.Close();
        VictoryStatus.Close();
        TurnOverlay.Close();
        ClearPreview();
        SelectedUnitId = null;
        ClearSelectionUi();
        SetStatus("", quiet: true);
        Visible = false;
        ProcessMode = ProcessModeEnum.Disabled;
        foreach (var layer in WorldLayers()) layer.Visible = false;
    }

    /// <summary>C029: shows the screen again after <see cref="Conceal"/> (the caller started it with the next player's session first).</summary>
    public void Reveal()
    {
        IsConcealed = false;
        foreach (var layer in WorldLayers()) layer.Visible = true;
        ProcessMode = ProcessModeEnum.Inherit;
        Visible = true;
    }

    // The 3D parts do not follow the screen's own visibility (a Node3D under a Control is a separate branch), so they are hidden one by one.
    private IEnumerable<Node3D> WorldLayers() => [Map, Overlays, unitLayer, dotLayer, ReachableOverlay, AttackOverlay, SiteOverlay, MarkerLayer, PingLayer];

    private bool watchingAfterDefeat;
    private int gameOverCheck;
    private int gameOverTurn = -1;

    /// <summary>C038: the modal decision picker (free great person, religion, spies, vote, promotion).</summary>
    public DecisionPickerView Picker { get; private set; } = null!; // JUSTIFY: C038 assigned in _Ready before any use

    /// <summary>C038: opens the promotion picker for the selected unit while the engine offers a promotion.</summary>
    public Button PromoteButton { get; } = new() { Name = "PromoteButton", Text = "Promote".Tr(), Visible = false };

    private readonly HashSet<string> deferredAlerts = [];
    private readonly HashSet<string> toastedAlerts = [];
    private readonly HashSet<string> deferredProposals = [];
    private readonly OfferTracker offerTracker = new();
    private bool reviewingAlerts;

    /// <summary>The city whose screen (<see cref="City"/>) is open: the hook that tells which city.</summary>
    public CityDto? OpenCity { get; private set; }

    /// <summary>The city screen (C017); created by <see cref="Start"/>.</summary>
    public CityScreen City { get; private set; } = null!; // JUSTIFY: C017 assigned in Start before any use

    public bool IsCityPanelVisible => City.Visible;

    /// <summary>C021: the diplomacy screen, opened by the Diplomacy button.</summary>
    public DiplomacyScreen Diplomacy { get; private set; } = null!; // JUSTIFY: C021 assigned in Start before any use

    public bool IsDiplomacyVisible => Diplomacy.Visible;

    public Button DiplomacyButton => World.Diplomacy;

    /// <summary>C020: the policy screen, opened by the Policies button or the "Pick a policy" decision.</summary>
    public PolicyScreen Policies { get; private set; } = null!; // JUSTIFY: C020 assigned in Start before any use

    public bool IsPolicyScreenVisible => Policies.Visible;

    public Button PolicyButton => World.Policies;

    public int Turn => snapshot.Turn;

    public string StatusText => statusText;

    /// <summary>C068: whether the transient message line is on screen.</summary>
    public bool StatusShown => statusPanel.Visible;

    public int PathDotCount => dotLayer.GetChildCount();

    /// <summary>The destination of the path preview currently drawn; a click on it moves.</summary>
    public HexCoord? PreviewTarget { get; private set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore; // the map reads unhandled input; only the HUD widgets take the mouse
        CreateWorldLayers();
        CreateHudWidgets();
        CreatePopups();

        GetViewport().SizeChanged += Layout;
        Layout();

        Map.TileClicked += OnTileClicked;
        Map.ViewChanged += ApplyUnitLod;
        Map.TileRightClicked += OnTileRightClicked;
        foreach (Control popup in new Control[]
                 {
                     CombatPreview, Picker, Alerts, Proposals, AutoPlayMenu, VoteResult, GameOver, Tutorials, VictoryStatus, GameMenu, ConsolePopup, MarkerMenu,
                 })
            DismissBannerOnShow(popup);
    }

    private void CreateWorldLayers()
    {
        Map = new MapView { Name = "Map" };
        AddChild(Map);
        Overlays = new MapOverlayLayer { Name = "Overlays" };
        AddChild(Overlays);
        unitLayer = new Node3D { Name = "Units" };
        AddChild(unitLayer);
        Presenter.PlaySound = name => PlaySound?.Invoke(name);
        AddChild(Presenter);
        dotLayer = new Node3D { Name = "PathDots" };
        AddChild(dotLayer);
        ReachableOverlay = new TileOverlay(PlaceholderPalette.ReachableTint) { Name = "ReachableOverlay" };
        AttackOverlay = new TileOverlay(PlaceholderPalette.AttackTint) { Name = "AttackOverlay" };
        SiteOverlay = new TileOverlay(PlaceholderPalette.SiteTint) { Name = "SiteOverlay" };
        AddChild(ReachableOverlay);
        AddChild(AttackOverlay);
        AddChild(SiteOverlay);
        MarkerLayer = new MapMarkerLayer { Name = "MarkerLayer" };
        AddChild(MarkerLayer);
        PingLayer = new MapPingLayer { Name = "PingLayer" };
        AddChild(PingLayer);
    }

    private void CreateHudWidgets()
    {
        statusLabel = new Label { Name = "StatusLabel" };
        statusPanel = new PanelContainer { Name = "StatusPanel", Visible = false, MouseFilter = MouseFilterEnum.Ignore, ThemeTypeVariation = UiVariants.PanelTooltip };
        statusLabel.MouseFilter = MouseFilterEnum.Ignore;
        statusPanel.AddChild(statusLabel);
        AddChild(statusPanel);
        Hud = new HudView { Name = "Hud" };
        World = new WorldHud();
        AddChild(World);
        World.Requested += OnNav;
        World.Rivals.ExpandedChanged += _ => Run(RefreshRivals(snapshot.Turn, force: true));
        World.Rivals.Resized += () => World.LayoutOverlays();
        Hud.ActionRequested += OnHudAction;
        Hud.EndTurnRequested += OnNextTurn;
        Hud.AutoPlayRequested += OnAutoPlayButton;
        AutoPlayMenu = new AutoPlayMenuView();
        AddChild(AutoPlayMenu);
        AutoPlayMenu.Chosen += kind => Run(StartAutoPlayAsync(kind));
        Notifications = new NotificationListView();
        Notifications.Resized += () => World.LayoutOverlays();
        Notifications.Chosen += OnNotificationChosen;
        Notifications.ReviewRequested += () => Run(ReviewAlerts());
        TaskCard = new TutorialTaskCard();
        AddChild(TaskCard);
        TaskCard.ChoiceChosen += (eventName, index) => Run(ChooseTaskChoice(eventName, index));
        Panel = new UnitPanelView();
        AddChild(Panel);
        Panel.EntryChosen += OnMenuEntry;
        Panel.Confirmed += entry => Run(ExecuteEntry(entry, confirmed: true));
        Panel.PopoverOpened += () => Hud.HideBanner();
        CombatPreview = new CombatPreviewView();
        AddChild(CombatPreview);
        CombatPreview.ConfirmRequested += () => Run(ConfirmAttack());
        CombatPreview.CancelRequested += ClearAttackPreview;
        Minimap = new MinimapView { Name = "Minimap" };
        Minimap.Attach(Map);
        World.Mount(Hud, Notifications, Minimap);
        Panel.SetPromoteChip(PromoteButton);
        PromoteButton.Pressed += () => Run(OpenPromotionPicker());
    }

    private void CreatePopups()
    {
        Picker = new DecisionPickerView();
        AddChild(Picker);
        Picker.Confirmed += (spec, commands) => Run(ConfirmPicker(spec, commands));
        Picker.Dismissed += spec => Run(DismissPicker(spec));
        Alerts = new AlertPopupView();
        AddChild(Alerts);
        Alerts.Answered += (popup, answer) => Run(AnswerAlert(popup, answer));
        Alerts.Deferred += popup =>
        {
            deferredAlerts.Add(popup.Key);
            Alerts.Close();
            Run(RefreshHud());
        };
        Proposals = new TradeProposalView();
        AddChild(Proposals);
        Proposals.Answered += (popup, answer) => Run(AnswerProposal(popup, answer));
        Proposals.Deferred += popup =>
        {
            deferredProposals.Add(popup.Key);
            Proposals.Close();
            Run(RefreshHud());
        };
        TurnBar = new TurnBarView();
        AddChild(TurnBar);
        Social = new SocialView();
        AddChild(Social);
        Social.ChatSubmitted += text => Run(SendChat(text));
        Social.KickRequested += civ => Run(Admin(civ, null));
        Social.RebindRequested += (civ, key) => Run(Admin(civ, key));
        TurnOverlay = new NextTurnProgressView();
        AddChild(TurnOverlay);
        VoteResult = new VoteResultScreen();
        AddChild(VoteResult);
        VoteResult.Continued += () => Run(AcknowledgeVoteResult());
        GameOver = new GameOverScreen();
        AddChild(GameOver);
        Tutorials = new TutorialPopupView();
        AddChild(Tutorials);
        Tutorials.Dismissed += DismissTutorial;
        Tutorials.NeverShowRequested += NeverShowTutorials;
        TaskCard.NeverShowRequested += NeverShowTutorials;
        GameOver.Chosen += kind => Run(OnGameOverChosen(kind));
        VictoryStatus = new VictoryStatusView();
        AddChild(VictoryStatus);
        GameMenu = new GameMenuView();
        AddChild(GameMenu);
        GameMenu.Chosen += kind => Run(OnMenuChosen(kind));
        ConsolePopup = new ConsoleView();
        AddChild(ConsolePopup);
        ConsolePopup.StateChanged += (history, keepOpen) => boundSettings?.Update(s => s with { ConsoleHistory = [.. history], KeepConsoleOpen = keepOpen });
        MarkerMenu = new MapMarkerMenuView();
        AddChild(MarkerMenu);
        MarkerMenu.Chosen += OnMarkerMenuChosen;
    }

    private void DismissBannerOnShow(Control control) => control.VisibilityChanged += () => { if (control.Visible) Hud.HideBanner(); };

    /// <summary>
    /// C079: a screen that rebuilt its content for a new scale or breakpoint shrinks its minimum size, but a control never shrinks by itself: it keeps the larger size it had at the old
    /// scale and sticks out of a smaller window. Sizing every screen again once the rebuild is done (deferred, so after all the screens handled the change) puts them back.
    /// </summary>
    private void RelayoutAfterRebuild()
    {
        if (IsInsideTree()) CallDeferred(nameof(Layout));
    }

    // Sized from the viewport, not from anchors: the screen may sit under a parent Control that has no size (the scenario runner).
    private void Layout()
    {
        Size = GetViewportRect().Size;
        if (City != null)
        {
            City.Position = new Vector2(0f, Hud.TopBarHeight);
            City.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Diplomacy != null)
        {
            Diplomacy.Position = new Vector2(0f, Hud.TopBarHeight);
            Diplomacy.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Trade != null)
        {
            Trade.Position = new Vector2(0f, Hud.TopBarHeight);
            Trade.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Tech != null)
        {
            Tech.Position = new Vector2(0f, Hud.TopBarHeight);
            Tech.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Overview != null)
        {
            Overview.Position = new Vector2(0f, Hud.TopBarHeight);
            Overview.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Policies != null)
        {
            Policies.Position = new Vector2(0f, Hud.TopBarHeight);
            Policies.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Pedia != null)
        {
            Pedia.Position = new Vector2(0f, Hud.TopBarHeight);
            Pedia.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        if (Espionage != null)
        {
            Espionage.Position = new Vector2(0f, Hud.TopBarHeight);
            Espionage.Size = new Vector2(Size.X, Size.Y - Hud.TopBarHeight);
        }
        LayoutSocial();
        Alerts.LayoutIn(Size);
        Proposals.LayoutIn(Size);
        VoteResult.LayoutIn(Size);
        GameOver.LayoutIn(Size);
        VictoryStatus.LayoutIn(Size);
        GameMenu.LayoutIn(Size);
        AutoPlayMenu.Size = AutoPlayMenu.GetCombinedMinimumSize();
        var autoPlayRect = Hud.AutoPlayButton?.GetGlobalRect() ?? default; // the dock's own layout places the button; the menu opens above it, right-aligned
        AutoPlayMenu.Position = new Vector2(autoPlayRect.End.X - AutoPlayMenu.Size.X, autoPlayRect.Position.Y - AutoPlayMenu.Size.Y - HudMargin / 2f);
        ConsolePopup.LayoutIn(Size);
        MarkerEditor?.LayoutIn(Size);
        Picker.LayoutIn(Size);
        Tutorials.LayoutIn(Size);
        TaskCard.LayoutIn(Size, World.NavBottom + HudMargin);
        TurnBar.LayoutIn(Size.X, TurnBarTop());
        TurnOverlay.LayoutIn(Size, OverlayTop()); // T345: a card under the navigation strip (below the multiplayer turn bar when shown), out of the way of the map and the menus
        World.LayoutOverlays();
        Panel.LayoutIn(Size);
        if (statusPanel.Visible) LayoutStatus(); // after the unit panel and the HUD's bottom dock, whose rectangles it must avoid
        CombatPreview.LayoutIn(Size);
    }

    public override void _ExitTree()
    {
        GetViewport().SizeChanged -= Layout;
        if (boundSettings != null) boundSettings.Changed -= ApplySettings; // T343: a freed screen must not keep answering settings changes
        ui?.Unbind(); // C079: a freed screen must not keep receiving the window's resizes through its theme
        turnCancellation?.Cancel(); // a turn still being processed stops after the civ it is playing; nothing waits for it
        if (session != null)
        {
            session.SnapshotChanged -= OnSnapshotChanged;
            session.EventsReceived -= OnEventsReceived;
            if (session is NetGameSession oldNet)
            {
                oldNet.HostRolledBack -= OnHostRolledBack;
                oldNet.HostChangedNotice -= OnHostChangedNotice;
            }
            if (session is ITurnRoster roster) roster.RosterChanged -= RefreshTurnBar;
            BindSocial(null);
        }
    }

    public override void _Process(double delta)
    {
        if (pings.Count > 0) AdvancePings((float)delta);
        if (statusFrames > 0 && --statusFrames == 0) statusPanel.Visible = false;
        else if (statusPanel.Visible && StatusLayoutStale()) LayoutStatus(); // T363: the HUD's rebuilt containers may only get their rect in a later frame
        // the countdowns move with the clock, not with snapshots
        if (session is not ITurnRoster || TurnBar.Data == null) return;
        var now = NowMs();
        var soonestGrace = TurnBar.Data.Rows.Where(it => !it.Ended && it.GraceEndsAt != null).Select(it => it.GraceEndsAt!.Value).DefaultIfEmpty(0).Min();
        if (TurnBarModel.Countdown(snapshot?.TurnStatus.Deadline, now) != shownCountdown
            || (soonestGrace != 0 && TurnBarModel.Countdown(soonestGrace, now) != shownGrace))
            RefreshTurnBar();
    }

    private void OnHostRolledBack(HostMigrationNotice notice) => Notifications.PushToast(notice.Title + ": " + notice.Detail);

    private void OnHostChangedNotice(HostMigrationNotice notice) => Notifications.PushToast(notice.Title + ": " + notice.Detail);

    /// <summary>Connects the screen to a game and draws it, centred on the human's first unit. Call after the screen entered the tree; calling it again switches to another game (C016: load).</summary>
    public void Start(IGameSession gameSession, AssetRegistry registry)
    {
        if (session != null) DetachPreviousSession();
        if (session != gameSession) ownerMemory.Clear(); // T298: what one game or viewer saw must never colour another's fog (hotseat hand-over, load)
        if (session != gameSession) sessionMarkers = ClientSettings.Default; // C089: in-memory markers belong to one game
        var previousSession = session;
        session = gameSession;
        if (session != previousSession) RestoreOwnerMemory(); // T378: a new session of a game this viewer played before (load, reconnect, restart) starts from what it saw
        assets = registry;
        queries = new GameQueries(session);
        ui?.Unbind();
        var theme = new UiTheme(registry);
        ui = theme;
        World.Init(theme);
        theme.LayoutChanged += World.OnLayoutChanged;
        theme.LayoutChanged += RelayoutAfterRebuild;
        theme.ScaleChanged += () => Overlays.UiScale = theme.Scale;
        theme.Bind(GetViewport());
        SharedThemeChanged?.Invoke(theme); // T405: after the bind, so a follower reads the fitted scale and breakpoint
        InitPopupsForTheme(theme);
        RebuildChildScreens(theme);
        ReachableOverlay.Setup(registry);
        AttackOverlay.Setup(registry);
        SiteOverlay.Setup(registry);
        session.EventsReceived += OnEventsReceived;
        Presenter.Bind(id => unitNodes.GetValueOrDefault(id), coord => UnitPosition(coord, 0));
        session.SnapshotChanged += OnSnapshotChanged; // commands made elsewhere (another screen, a scenario) change what the HUD shows
        // T275: a host change that rolled this client back, or moved it to a new host, is a toast (the two-human split has its own dismissible overlay, wired by the owner).
        if (session is NetGameSession netSession)
        {
            netSession.HostRolledBack += OnHostRolledBack;
            netSession.HostChangedNotice += OnHostChangedNotice;
        }
        if (session is ITurnRoster roster) roster.RosterChanged += RefreshTurnBar;
        BindSocial(session as IGameSocial);
        Refresh();
        RefreshTurnBar();
        var first = snapshot.Units.FirstOrDefault(it => it.Own != null);
        if (first != null) Map.FocusOn(MapLayout.TileTop(first.X, first.Y));
        SetStatus("Click one of your units.", quiet: true);
    }

    private void DetachPreviousSession()
    {
        session.SnapshotChanged -= OnSnapshotChanged;
        session.EventsReceived -= OnEventsReceived;
        if (session is NetGameSession oldNet)
        {
            oldNet.HostRolledBack -= OnHostRolledBack;
            oldNet.HostChangedNotice -= OnHostChangedNotice;
        }
        if (session is ITurnRoster previous) previous.RosterChanged -= RefreshTurnBar;
        BindSocial(null);
        Presenter.Reset();
        turnCancellation?.Cancel();
        SelectedUnitId = null;
        extraGroupIds = [];
        escorts.Clear();
        escortTurnHandled.Clear();
        mapLoaded = false;
        bannerTurn = -1;
        rivalsTurn = -1;
    }

    private void InitPopupsForTheme(UiTheme theme)
    {
        TurnBar.Init(theme);
        Social.Init(theme);
        Alerts.Init(theme);
        Proposals.Init(theme);
        Proposals.Close();
        VoteResult.Init(theme);
        VoteResult.Close();
        GameOver.Init(theme);
        Tutorials.Init(theme);
        Tutorials.Close();
        TaskCard.Init(theme);
        reportedTasks.Clear();
        tutorialQueue = new TutorialQueue(session.Ruleset);
        TaskCard.ResetClosed();
        GameOver.Close();
        VictoryStatus.Init(theme);
        VictoryStatus.Close();
        GameMenu.Init(theme);
        GameMenu.Close();
        AutoPlayMenu.Init(theme);
        AutoPlayMenu.Close();
        autoPlayGeneration++; // a run of the previous game ends with it
        Presenter.SpeedScale = 1f;
        AutoPlay = null;
        Hud.SetAutoPlay(null);
        ConsolePopup.Init(theme);
        ConsolePopup.Close();
        ConsolePopup.Bind(session, queries, () => (Map.Selected, SelectedUnitId), Run);
        ConsolePopup.LayoutIn(Size);
        resigned = false;
        TurnOverlay.Init(theme);
        TurnOverlay.Close();
        watchingAfterDefeat = false;
        Picker.Init(theme);
        statusPanel.Theme = theme.Theme;
        deferredAlerts.Clear();
        toastedAlerts.Clear();
        deferredProposals.Clear();
        offerTracker.Reset();
        reviewingAlerts = false;
    }

    private void RebuildChildScreens(UiTheme theme)
    {
        City = new CityScreen(session, theme);
        City.ConfirmDestructive = ConfirmDestructive; // T343
        AddChild(City);
        City.CommandAccepted += Refresh;
        City.CommandSent += command =>
        {
            if (TutorialTaskModel.FromCommand(command) is { } task) ReportTaskDone(task);
        };
        City.Closed += () => ShowCity(null);
        if (Diplomacy != null)
        {
            RemoveChild(Diplomacy);
            Diplomacy.QueueFree();
        }
        Diplomacy = new DiplomacyScreen(session, theme);
        Diplomacy.ConfirmDestructive = ConfirmDestructive; // T343
        AddChild(Diplomacy);
        Diplomacy.CommandAccepted += Refresh;
        if (Tech != null)
        {
            RemoveChild(Tech);
            Tech.QueueFree();
        }
        if (Trade != null)
        {
            RemoveChild(Trade);
            Trade.QueueFree();
        }
        Trade = new TradeScreen(session, theme);
        AddChild(Trade);
        Trade.CommandAccepted += Refresh;
        Trade.Closed += () => Run(RefreshHud());
        Trade.Retracting += offerTracker.NoteRetracted;
        Tech = new TechScreen(session, theme);
        AddChild(Tech);
        Tech.CommandAccepted += Refresh;
        if (Policies != null)
        {
            RemoveChild(Policies);
            Policies.QueueFree();
        }
        Policies = new PolicyScreen(session, theme);
        AddChild(Policies);
        Policies.CommandAccepted += Refresh;
        if (Overview != null)
        {
            RemoveChild(Overview);
            Overview.QueueFree();
        }
        Overview = new OverviewScreen(session, theme);
        AddChild(Overview);
        Overview.CommandAccepted += Refresh;
        Overview.CityChosen += JumpToCity;
        Overview.UnitChosen += JumpToUnit;
        if (boundSettings != null) Overview.BindSettings(boundSettings);
        if (MarkerEditor != null)
        {
            RemoveChild(MarkerEditor);
            MarkerEditor.QueueFree();
        }
        MarkerEditor = new MapMarkerView(theme);
        AddChild(MarkerEditor);
        MarkerEditor.Saved += OnMarkerSaved;
        MarkerEditor.Removed += OnMarkerRemoved;
        MarkerEditor.GoTo += OnMarkerGoTo;
        MarkerMenu.Init(theme);
        MarkerMenu.Close();
        if (Tooltips != null)
        {
            RemoveChild(Tooltips);
            Tooltips.QueueFree();
        }
        Tooltips = new TooltipHost(theme) { Name = "Tooltips", TopInset = () => World.BarBottom };
        Tooltips.PointEnded += () =>
        {
            // C100: a touch tile tip ended by itself (next press, timeout): the tile it described and the site overlay go with it
            HoveredTile = null;
            HoverTip = "";
            SiteOverlay.Clear();
        };
        HoveredTile = null;
        HoverTip = "";
        if (Pedia != null)
        {
            RemoveChild(Pedia);
            Pedia.QueueFree();
        }
        Pedia = new PediaScreen(session.Ruleset, theme, Tooltips);
        AddChild(Pedia);
        if (Espionage != null)
        {
            RemoveChild(Espionage);
            Espionage.QueueFree();
        }
        Espionage = new EspionageScreen(session, theme);
        AddChild(Espionage);
        Espionage.CommandAccepted += Refresh;
        foreach (Control screen in new Control[] { City, Diplomacy, Trade, Tech, Overview, Policies, Pedia, Espionage, MarkerEditor })
            DismissBannerOnShow(screen);
        Panel.Init(theme);
        CombatPreview.Init(theme);
        MoveChild(Picker, -1);
        MoveChild(Proposals, -1);
        MoveChild(Alerts, -1); // the popups sit above every screen opened above
        MoveChild(VoteResult, -1);
        MoveChild(GameOver, -1);
        MoveChild(VictoryStatus, -1);
        MoveChild(GameMenu, -1);
        MoveChild(AutoPlayMenu, -1);
        MoveChild(ConsolePopup, -1);
        MoveChild(MarkerEditor, -1);
        MoveChild(MarkerMenu, -1);
        MoveChild(Tutorials, -1);
        AddChild(Tooltips); // above everything, so a tip is never covered by a screen
        // T345: the progress card stays below the menus, popups and screens (it was added before them), because it no longer blocks any of them.
    }

    /// <summary>T378: persists what this viewer saw of tile ownership under the store's folder (per game and viewer); a session without a key keeps it in memory only. Call before <see cref="Start"/> or after, in which case an earlier memory is restored now.</summary>
    public void BindOwnerMemory(TileOwnerMemoryStore store)
    {
        ownerStore = store;
        if (session == null) return;
        bool restored = RestoreOwnerMemory();
        if (ownerMemory.Remember(session.Snapshot.Tiles)) SaveOwnerMemory(); // the first look at this game, before the store was known
        if (restored) Refresh();
    }

    private bool RestoreOwnerMemory() =>
        ownerStore != null && session is { OwnerMemoryKey: { } key } && ownerStore.Load(key, session.ViewerCivId, session.Snapshot.Turn, session.Snapshot.Tiles, ownerMemory);

    private void SaveOwnerMemory()
    {
        if (ownerStore != null && session is { OwnerMemoryKey: { } key }) ownerStore.Save(key, session.ViewerCivId, session.Snapshot.Turn, session.Snapshot.Tiles, ownerMemory);
    }

    /// <summary>Redraws everything from the session's current snapshot (the screen does this itself after its own commands; a caller that changed the game another way calls it).</summary>
    public void Refresh()
    {
        FullRefreshCount++;
        autoPlayStale = false;
        snapshot = session.Snapshot;
        Map.Load(assets, snapshot.Tiles, keepCamera: mapLoaded);
        mapLoaded = true;
        Minimap.Update(snapshot.Tiles, OwnerColour);
        Layout();
        if (ownerMemory.Remember(snapshot.Tiles)) SaveOwnerMemory();
        Overlays.UiScale = ui?.Scale ?? 1f;
        Overlays.Rebuild(assets, session.Ruleset, Map, snapshot.Tiles, snapshot.Cities, OwnerColour, ownerMemory, OwnerInnerColour, colorblindMode == ColorblindMode.Off ? null : OwnerCueFor);
        RefreshMarkers();
        if (OpenCity != null) ShowCity(snapshot.Cities.FirstOrDefault(it => it.X == OpenCity.X && it.Y == OpenCity.Y));
        RebuildUnits();
        ClearPreview();
        Run(RefreshHud());
        if (SelectedUnitId is { } id && unitNodes.TryGetValue(id, out var selected) && selected.IsOwn)
            Map.Select(new HexCoord(selected.Unit.X, selected.Unit.Y));
        else SelectedUnitId = null;
        extraGroupIds = SelectedUnitId == null ? [] : [.. extraGroupIds.Where(it => unitNodes.TryGetValue(it, out var member) && member.IsOwn)];
        ApplySelectionScale();
        Run(RefreshSelection());
        UpdateAudioForSnapshot();
    }

    private int lastSoundTurn = -1;

    private void UpdateAudioForSnapshot()
    {
        if (lastSoundTurn >= 0 && snapshot.Turn > lastSoundTurn)
            PlaySound?.Invoke("chimes");
        lastSoundTurn = snapshot.Turn;
        bool atWar = snapshot.Diplomacy.Any(it => it.AtWar &&
            (string.Equals(it.A, session.ViewerCivId, StringComparison.Ordinal) || string.Equals(it.B, session.ViewerCivId, StringComparison.Ordinal)));
        MusicMoodChanged?.Invoke(ProceduralMusic.MoodForState(inGame: true, atWar: atWar, gameOver: GameOver.Visible));
    }

    /// <summary>T379: how many times <see cref="Refresh"/> rebuilt the whole screen (a run that skips animations must not do it every turn, see <see cref="AutoPlayModel.RefreshesAfter"/>).</summary>
    public int FullRefreshCount { get; private set; }

    /// <summary>T379: turns of the running AutoPlay were played since the map was last redrawn.</summary>
    private bool autoPlayStale;

    private Rgb? OwnerInnerColour(string owner)
    {
        if (!session.Ruleset.Nations.TryGetValue(owner, out var nation)) return null;
        return AccessibilityModel.RemapCivColour(colorblindMode, owner, ToRgb(nation.GetOuterColor()), ToRgb(nation.GetInnerColor())).Inner;
    }

    private Rgb? OwnerColour(string owner)
    {
        if (!session.Ruleset.Nations.TryGetValue(owner, out var nation)) return null;
        return AccessibilityModel.RemapCivColour(colorblindMode, owner, ToRgb(nation.GetOuterColor()), ToRgb(nation.GetInnerColor())).Outer;
    }

    private string OwnerCueFor(string owner)
    {
        var dip = snapshot.Diplomacy.FirstOrDefault(it =>
            (string.Equals(it.A, session.ViewerCivId, StringComparison.Ordinal) && string.Equals(it.B, owner, StringComparison.Ordinal)) ||
            (string.Equals(it.B, session.ViewerCivId, StringComparison.Ordinal) && string.Equals(it.A, owner, StringComparison.Ordinal)));
        bool atWar = dip?.AtWar == true;
        bool allied = dip?.Status is { } status &&
            (string.Equals(status, "Ally", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(status, "Protector", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(status, "DefensivePact", StringComparison.OrdinalIgnoreCase));
        bool barb = session.Ruleset.Nations.TryGetValue(owner, out var nation) && nation.IsBarbarian;
        return AccessibilityModel.OwnerCue(owner, session.ViewerCivId, atWar, allied, barb);
    }

    private static Rgb ToRgb(Unciv.Models.EngineColor colour) =>
        new((byte)Math.Round(colour.R * 255f, MidpointRounding.AwayFromZero), (byte)Math.Round(colour.G * 255f, MidpointRounding.AwayFromZero), (byte)Math.Round(colour.B * 255f, MidpointRounding.AwayFromZero));

    private void RebuildUnits()
    {
        foreach (var node in unitNodes.Values) node.QueueFree();
        unitNodes.Clear();
        var stack = new Dictionary<HexCoord, int>();
        foreach (var unit in snapshot.Units)
        {
            var coord = new HexCoord(unit.X, unit.Y);
            int index = stack.GetValueOrDefault(coord);
            stack[coord] = index + 1;
            var colour = OwnerColour(unit.Owner);
            var cue = colorblindMode == ColorblindMode.Off ? null : $"{OwnerCueFor(unit.Owner)} {AccessibilityModel.UnitStateCue(unit)}".Trim();
            var node = new UnitNode { Name = $"Unit_{unit.Id}", Unit = unit };
            node.Place(UnitPosition(coord, index));
            node.Build(assets, session.Ruleset, session.Ruleset.Units.GetValueOrDefault(unit.Name), colour is { } c ? (c.R / 255f, c.G / 255f, c.B / 255f) : null, cue);
            unitLayer.AddChild(node);
            unitNodes[unit.Id] = node;
        }
        ApplyUnitLod();
        Presenter.Refresh();
    }

    /// <summary>C085: reduced formations when the camera is far out, no units outside its footprint (<see cref="UnitLod"/>).</summary>
    private void ApplyUnitLod()
    {
        var far = UnitLod.IsFar(Map.Zoom, Map.Graphics.FarZoom);
        Overlays.YieldsFar = FarZoomLod.HideYields(Map.Zoom);
        foreach (var node in unitNodes.Values)
        {
            node.ApplyLod(far);
            node.ApplyView(Map.VisibleGround);
        }
    }

    private Vector3 UnitPosition(HexCoord coord, int stackIndex)
    {
        var elevation = Map.TileNodeAt(coord)?.Elevation ?? 1f;
        var top = MapLayout.TileTop(coord.X, coord.Y, elevation);
        return top + new Vector3(stackIndex * StackOffset, 0f, stackIndex * StackOffset);
    }

    private void ApplySelectionScale()
    {
        foreach (var (id, node) in unitNodes) node.Scale = id == SelectedUnitId || extraGroupIds.Contains(id) ? Vector3.One * SelectedScale : Vector3.One;
    }

    // Input

    private void OnTileClicked(HexCoord? coord)
    {
        if (IsEndingTurn) return; // T345: the camera still pans and zooms, but no order starts while the worker owns the engine gate
        MarkerMenu.Close();
        if (DisplayProfile.Current.Touch && Tooltips != null) ShowTileTip(coord, Map.ClickAt); // C100: tile info without hover
        if (coord is not { } tile)
        {
            Deselect();
            return;
        }
        var own = OwnUnitsAt(tile);
        if ((Map.LastClickShift || Panel.GroupMode) && own.Count > 0) // C100: Panel.GroupMode is the touch stand-in for Shift
        {
            ToggleGroup(own);
            return;
        }
        var city = snapshot.Cities.FirstOrDefault(it => it.X == tile.X && it.Y == tile.Y);
        if (city != null && (own.Count == 0 ? SelectedUnitId == null : SelectedUnitId is { } selected && own.Any(it => it.Unit.Id == selected)))
        {
            ShowCity(city); // a click on a city opens it, unless a unit elsewhere is selected (then the click is a move target) or a unit there is still to be selected
            return;
        }
        switch (UnitOrdersModel.ClickIntent((tile.X, tile.Y), own.Count > 0, SelectedUnitId != null, attackTiles))
        {
            case TileClickIntent.SelectOwn:
                int at = SelectedUnitId is { } current ? own.FindIndex(it => it.Unit.Id == current) : -1;
                SelectUnit(own[(at + 1) % own.Count]);
                break;
            case TileClickIntent.AttackPreview:
                Run(ShowAttackPreview(tile));
                break;
            case TileClickIntent.PathPreview:
                if (PreviewTarget == tile) Run(MoveSelectedTo());
                else Run(ShowPreview(tile));
                break;
        }
    }

    private void OnTileRightClicked(HexCoord? coord)
    {
        if (IsEndingTurn) return; // T345: no order while the turn runs (the overlay's cancel hint says how to stop it)
        MarkerMenu.Close();
        if (coord is { } marked && SelectedUnitId == null)
        {
            // C089: with no unit selected a right-click on a tile opens the marker menu there (with a unit selected it moves or attacks, as before)
            markerTile = marked;
            MarkerMenu.Open(Map.RightClickAt, Size, MapMarkerModel.At(MarkerSettings, MarkerKey, marked.X, marked.Y) != null, canPing: social != null);
            return;
        }
        if (coord is not { } tile || SelectedUnitId == null) return;
        if (attackTiles.Contains((tile.X, tile.Y))) Run(ShowAttackPreview(tile));
        else Run(RightClickMove(tile));
    }

    // C089: private map markers. Stored in the client settings under the game and viewer; a game without an id keeps them in memory for the session.
    private const string SessionMarkerKey = "session";
    private ClientSettings sessionMarkers = ClientSettings.Default;
    private HexCoord? markerTile;

    private string? MarkerKey => MapMarkerModel.Key(session?.GameId, session?.ViewerCivId ?? "");

    private bool MarkersPersist => boundSettings != null && MarkerKey != null;

    private ClientSettings MarkerSettings => MarkersPersist ? boundSettings!.Current : sessionMarkers;

    private string StoreKey => MarkersPersist ? MarkerKey! : SessionMarkerKey;

    /// <summary>The viewer's markers of this game, in the order they were placed.</summary>
    public IReadOnlyList<MapMarker> Markers => MapMarkerModel.For(MarkerSettings, StoreKey);

    private void RefreshMarkers()
    {
        if (MarkerLayer == null || session == null) return;
        MarkerLayer.Show([.. Markers.Where(it => Map.TileNodeAt(new HexCoord(it.X, it.Y)) != null)], TileTopAt); // a marker off this map (stale or hand-edited) stays in the file but is not drawn
    }

    private void ChangeMarkers(Func<ClientSettings, string, ClientSettings> change)
    {
        string key = StoreKey;
        if (MarkersPersist) boundSettings!.Update(it => change(it, key)); // Changed redraws the layer
        else
        {
            sessionMarkers = change(sessionMarkers, key);
            RefreshMarkers();
        }
        MarkerEditor?.SetList(Markers);
    }

    /// <summary>Opens the marker editor on a tile (the list only when null).</summary>
    public void OpenMarkerEditor(HexCoord? tile)
    {
        if (session == null || MarkerEditor == null) return;
        MarkerMenu.Close();
        var existing = tile is { } at ? MapMarkerModel.At(MarkerSettings, StoreKey, at.X, at.Y) : null;
        MarkerEditor.Open(tile, existing, Markers);
    }

    private void OnMarkerMenuChosen(MarkerMenuChoice choice)
    {
        MarkerMenu.Close();
        switch (choice)
        {
            case MarkerMenuChoice.Edit:
                OpenMarkerEditor(markerTile);
                break;
            case MarkerMenuChoice.Remove:
                if (markerTile is { } tile) OnMarkerRemoved(tile);
                break;
            case MarkerMenuChoice.List:
                OpenMarkerEditor(null);
                break;
            case MarkerMenuChoice.PingLook:
            case MarkerMenuChoice.PingDanger:
            case MarkerMenuChoice.PingHelp:
                if (markerTile is { } at) Run(SendPing(at, choice == MarkerMenuChoice.PingDanger ? MapPingKinds.Danger : choice == MarkerMenuChoice.PingHelp ? MapPingKinds.Help : MapPingKinds.Look));
                break;
        }
    }

    // C090: shared map pings (hosted games only). The host relays a ping to the allies who know the tile and echoes it to the sender; what comes back is shown for a few seconds.
    private IReadOnlyList<ActivePing> pings = [];

    /// <summary>The pings on screen now.</summary>
    public IReadOnlyList<ActivePing> Pings => pings;

    private async Task SendPing(HexCoord tile, string kind)
    {
        if (social == null) return;
        try
        {
            await social.SendPingAsync(tile.X, tile.Y, kind);
        }
        catch (InvalidOperationException)
        {
            if (IsInstanceValid(this) && IsInsideTree()) SetStatus("The host cannot be reached.".Tr(), quiet: true);
        }
    }

    private void OnPingReceived(MapPingNotice notice)
    {
        if (Map.TileNodeAt(new HexCoord(notice.X, notice.Y)) == null) return; // a tile this map does not have
        pings = MapPingModel.Add(pings, notice);
        PingLayer.Sync(pings, TileTopAt);
    }

    /// <summary>Lets <paramref name="seconds"/> pass for the pings on screen (called every frame; a scenario calls it to expire them at once).</summary>
    public void AdvancePings(float seconds)
    {
        pings = MapPingModel.Advance(pings, seconds);
        PingLayer.Sync(pings, TileTopAt);
    }

    private void OnMarkerSaved(HexCoord tile, string icon, string label) => ChangeMarkers((it, key) => MapMarkerModel.Place(it, key, tile.X, tile.Y, icon, label));

    private void OnMarkerRemoved(HexCoord tile) => ChangeMarkers((it, key) => MapMarkerModel.Remove(it, key, tile.X, tile.Y));

    private void OnMarkerGoTo(HexCoord tile)
    {
        MarkerEditor?.Close();
        Map.FocusOn(MapLayout.TileTop(tile.X, tile.Y));
    }

}
