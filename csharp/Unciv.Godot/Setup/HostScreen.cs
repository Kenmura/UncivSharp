using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Godot;
using Unciv.Assets;
using Unciv.Client.Net.Hosting;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;
using Unciv.Net.Identity;
using Unciv.Net.Lobby;
using Unciv.Net.Relay;
using TabBar = Unciv.GodotClient.Ui.TabBar;

namespace Unciv.GodotClient.Setup;

/// <summary>What the host screen needs from the machine, as plain callbacks so a scenario can supply fixed ones (no real user folder, clipboard or network cards).</summary>
public sealed class HostScreenEnvironment
{
    public required Func<ClientIdentity> Identity { get; init; }

    public required Func<X509Certificate2> Certificate { get; init; }

    /// <summary>The folder with one directory per saved hosted game.</summary>
    public required string HostedGamesDirectory { get; init; }

    /// <summary>The name the host appears under in the lobby.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Wall clock in Unix milliseconds: the map seed when none was typed.</summary>
    public required Func<long> Clock { get; init; }

    public required Action<string> SetClipboard { get; init; }

    /// <summary>T338: the relay to host through (null: not set or switched off, hosting is direct only).</summary>
    public Func<RelayEndpoint?> Relay { get; init; } = () => null;

    /// <summary>T339: true when a relay address is set at all (even if hosting through it is switched off); it only picks which sentence explains a disabled "List this game publicly".</summary>
    public Func<bool> RelaySet { get; init; } = () => false;

    public Func<IReadOnlyList<LocalAddress>> Addresses { get; init; } = LocalAddresses.Enumerate;
}

/// <summary>
/// C034: the Host screen. Tabs for Game (name, civs, city-states, AI fill, difficulty, speed, era, victories), Map, Rules (base ruleset and mods), Network (turn timer, password, port,
/// the addresses of this machine and the port-forward hint) and Saved games (resume a hosted game). It only edits a <see cref="HostSettingsForm"/> (whose game side is the C015
/// <see cref="NewGameForm"/>, with the same refusals) and shows what is wrong with it. Start hosting opens the lobby and listens (<see cref="HostingSession"/>); the screen then shows who
/// sits in which slot, the invite for the chosen address and Copy invite until Stop hosting. The slot table here is deliberately plain: the lobby screen (C035) replaces it.
/// Resuming a saved game reopens it with the same host and listens again.
/// </summary>
public partial class HostScreen : Control
{
    private readonly UiTheme theme;
    private readonly HostScreenEnvironment env;
    private readonly VBoxContainer gamePage = new() { Name = "GamePage" };
    private readonly VBoxContainer mapPage = new() { Name = "MapPage" };
    private readonly VBoxContainer rulesPage = new() { Name = "RulesPage" };
    private readonly VBoxContainer networkPage = new() { Name = "NetworkPage" };
    private readonly VBoxContainer savedPage = new() { Name = "SavedPage" };
    private readonly Label messageLabel = new() { Name = "HostMessage", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Dictionary<string, ChoiceRow> rows = [];
    private readonly List<Button> resumeButtons = [];
    private readonly ScreenFrame frame;
    private readonly VBoxContainer settingsColumn = new() { Name = "SettingsColumn" };
    private readonly VBoxContainer hostingPanel = new() { Name = "HostingPanel", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly ScrollContainer hostingScroll = new() { Name = "HostingScroll", Visible = false, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly VBoxContainer slotsBox = new() { Name = "Slots" };
    private readonly Label hostingTitle = new() { Name = "HostingTitle" };
    private readonly Label hostingStatus = new() { Name = "HostingStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label addressNote = new() { Name = "AddressNote", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly VBoxContainer hostingAddresses = new() { Name = "HostingAddresses" };
    private readonly Label listingStatus = new() { Name = "ListingStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private LobbySettings? hostedSettings;
    private HostedGameInfo? resumedInfo;
    private ClientIdentity? identity;
    private X509Certificate2? certificate;
    private int selectedAddress;
    private string slotSignature = "";
    private string standbySignatureSeen = "";
    private bool stopConfirmed;
    private bool busy;

    public HostScreen(UiTheme theme, HostSettingsForm form, HostScreenEnvironment env)
    {
        this.theme = theme;
        this.env = env;
        Form = form;
        Name = "HostScreen";
        Theme = theme.Theme; // T318: the frame and the footer take the kit's theme even under a bare parent (the scenarios build some screens without a ScreenManager)
        this.Fill();
        var backdrop = new ColorRect { Name = "Backdrop", Color = ProceduralAssets.ToColor(UiTokens.Bg1), MouseFilter = MouseFilterEnum.Ignore };
        backdrop.Fill();
        AddChild(backdrop);
        // T318: the same frame as New game and Options: a titled header, the content capped to the reading width and the actions in the footer
        frame = new ScreenFrame(theme, "Host game", closeButton: false);
        frame.Fill();
        AddChild(frame);
        var column = new VBoxContainer { Name = "HostColumn" };
        column.FitConstant(theme, "separation", UiTokens.S2);

        settingsColumn.SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(settingsColumn);
        Tabs = new TabBar { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var (title, page, icon) in new[] { ("Game", gamePage, "Score"), ("Map", mapPage, "Terrains"), ("Rules", rulesPage, "Mods"), ("Network", networkPage, "Multiplayer"), ("Saved games", savedPage, "Load") })
        {
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            page.FitConstant(theme, "separation", UiTokens.S2);
            scroll.AddChild(page);
            Tabs.AddTab(title, scroll, theme, SetupTabs.UiIcon(icon));
        }
        settingsColumn.AddChild(Tabs);

        StartButton = new Button { Text = "Start hosting".Tr(), Name = "StartHostingButton" };
        StartButton.Pressed += () => _ = StartAsync();
        BackButton = new Button { Text = "Back".Tr(), Name = "BackButton" };
        BackButton.Pressed += () => BackRequested?.Invoke();

        BuildHostingPanel(column);
        column.AddChild(messageLabel);
        frame.SetContent(column);
        // both hosting actions live in the footer for the frame's lifetime (SetFooter frees what it removes, so they are toggled, not rebuilt)
        frame.SetFooter([(BackButton, ChoiceRole.Ghost), (StartButton, ChoiceRole.Primary), (StopButton, ChoiceRole.Primary)]);
        Refresh();
    }

    public HostSettingsForm Form { get; }

    public TabBar Tabs { get; }

    public Button StartButton { get; private set; }

    public Button BackButton { get; }

    private ReadyRef<Button> stopButtonRef;
    private ReadyRef<Button> copyInviteButtonRef;
    private ReadyRef<LineEdit> inviteFieldRef;
    private ReadyRef<LineEdit> nameFieldRef;
    private ReadyRef<LineEdit> seedFieldRef;
    private ReadyRef<LineEdit> passwordFieldRef;
    private ReadyRef<LineEdit> portFieldRef;
    private ReadyRef<LineEdit> publicAddressFieldRef;
    private ReadyRef<CheckBox> dynamicTimerCheckRef;
    private ReadyRef<CheckBox> aiFillCheckRef;
    private ReadyRef<CheckBox> listPubliclyCheckRef;
    private ReadyRef<Label> standbyStatusLabelRef;
    private ReadyRef<Label> relayInUseLabelRef;
    private ReadyRef<VBoxContainer> relayInviteBoxRef;
    private ReadyRef<LineEdit> relayInviteFieldRef;
    private ReadyRef<Button> copyRelayInviteButtonRef;

    public Button StopButton => stopButtonRef.Value;

    public Button CopyInviteButton => copyInviteButtonRef.Value;

    public LineEdit InviteField => inviteFieldRef.Value;

    public LineEdit NameField => nameFieldRef.Value;

    public LineEdit SeedField => seedFieldRef.Value;

    public LineEdit PasswordField => passwordFieldRef.Value;

    public LineEdit PortField => portFieldRef.Value;

    public LineEdit PublicAddressField => publicAddressFieldRef.Value;

    public CheckBox DynamicTimerCheck => dynamicTimerCheckRef.Value;

    public CheckBox AiFillCheck => aiFillCheckRef.Value;

    /// <summary>The running host (new lobby or resumed game), or null while the settings are shown.</summary>
    public HostingSession? Session { get; private set; }

    public bool IsHosting => Session != null;

    /// <summary>Raised when the player leaves the screen; hosting, if running, is stopped first.</summary>
    public event Action? BackRequested;

    /// <summary>Raised after hosting started (a new lobby or a resumed game).</summary>
    public event Action<HostingSession>? HostingStarted;

    /// <summary>What the screen currently tells the player.</summary>
    public string Message => messageLabel.Text;

    /// <summary>The addresses offered (those of this machine); the invite uses the selected one unless a public address was typed.</summary>
    public IReadOnlyList<LocalAddress> Addresses { get; private set; } = [];

    /// <summary>The address in the invite: the typed public address, else the selected local one, else the loopback address.</summary>
    public string InviteAddress =>
        Form.PublicAddress.Trim().Length > 0 ? Form.PublicAddress.Trim() : selectedAddress < Addresses.Count ? Addresses[selectedAddress].Address : "127.0.0.1";

    /// <summary>The invite string for <see cref="InviteAddress"/>; empty while not hosting.</summary>
    public string Invite => Session?.Invite(InviteAddress) ?? "";

    /// <summary>The Resume button of the n-th saved game (newest first).</summary>
    public Button? ResumeButton(int index) => index >= 0 && index < resumeButtons.Count ? resumeButtons[index] : null;

    /// <summary>The option button of a choice row: field is "Difficulty", "Speed", "Era", "Type", "Shape", "Size", "Resources", "Base ruleset", "Timer" or "Shortest turn".</summary>
    public Button? ChoiceButton(string field, string option) => rows.GetValueOrDefault(field)?.ButtonFor(option);

    /// <summary>The address buttons of the Network tab (before hosting) or the hosting panel.</summary>
    public Button? AddressButton(string address) => FindAddressRow()?.ButtonFor(address);

    /// <summary>The text of the slot table while hosting, one line per slot.</summary>
    public IReadOnlyList<string> SlotLines => slotsBox.GetChildren().OfType<Label>().Select(it => it.Text).ToList();

    public void Reveal(Control control)
    {
        for (var node = control.GetParent(); node != null; node = node.GetParent())
        {
            if (node is not ScrollContainer scroll) continue;
            scroll.EnsureControlVisible(control);
            return;
        }
    }

    public override void _Process(double delta)
    {
        if (Session is not { } session) return;
        if (RelayInviteField.Text != RelayInvite) SyncRelayInviteField();
        var signature = string.Join("|", session.Slots.Select(it => $"{it.Slot}:{it.Kind}:{it.PlayerName}:{it.Connected}"));
        var standbySignature = StandbyState() ?? "";
        if (signature == slotSignature && standbySignature == standbySignatureSeen) return;
        slotSignature = signature;
        standbySignatureSeen = standbySignature;
        FillSlots();
        UpdateStandby();
        UpdateListing();
    }

    public override void _ExitTree()
    {
        // Leaving the screen without Stop hosting (Back, the game replaced the stack) must not leave a port open.
        if (Session is { } running)
        {
            Session = null;
            _ = running.DisposeAsync().AsTask();
        }
    }

    private void SetMessage(string text, bool isError)
    {
        messageLabel.Text = text.TrText();
        messageLabel.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(isError ? PlaceholderPalette.ErrorText : PlaceholderPalette.Text));
    }

    /// <summary>Rebuilds every settings page from the form and shows its problems.</summary>
    public void Refresh()
    {
        rows.Clear();
        Addresses = env.Addresses();
        selectedAddress = Math.Clamp(selectedAddress, 0, Math.Max(0, Addresses.Count - 1));
        FillGame();
        FillMap();
        FillRules();
        FillNetwork();
        FillSaved();
        ShowProblems();
    }

    private void ShowProblems()
    {
        var problems = Form.Problems();
        SetMessage(string.Join("\n", problems), problems.Count > 0);
    }

    private void Change(string? error)
    {
        Refresh();
        if (error != null) SetMessage(error, true);
    }

    // Start and resume

    private async Task StartAsync()
    {
        if (busy || Session != null) return;
        busy = true;
        StartButton.Disabled = true;
        try
        {
            var built = Form.Build(env.Clock());
            if (built.Settings is not { } settings)
            {
                SetMessage(string.Join("\n", built.Errors), true);
                return;
            }
            SetMessage("Opening the lobby ...", false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var started = await HostingSession.StartLobbyAsync(settings, Identity(), Certificate(), env.DisplayName, Form.Port, env.Relay());
            hostedSettings = settings;
            ShowHosting(started, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetMessage(e.Message, true);
        }
        finally
        {
            busy = false;
            StartButton.Disabled = false;
        }
    }

    private async Task ResumeAsync(HostedGameInfo game)
    {
        if (busy || Session != null) return;
        busy = true;
        try
        {
            if (!HostSettingsForm.TryParsePort(Form.Port.ToString(CultureInfo.InvariantCulture), out int port, out string? problem))
            {
                SetMessage(problem ?? "", true);
                return;
            }
            SetMessage("Reopening the saved game ...", false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var resumed = await HostingSession.ResumeAsync(game.Directory, Identity(), Certificate(), port);
            ShowHosting(resumed, game);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetMessage(e.Message, true);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task StopAsync()
    {
        if (Session is not { } running) return;
        // T275: a quit without a ready backup host leaves the others stranded if this process ends; the player confirms once.
        if (!stopConfirmed && StandbyPending)
        {
            stopConfirmed = true;
            StopButton.Text = "Stop anyway".Tr();
            SetMessage(HostMigrationModel.QuitWarningDetail, true);
            return;
        }
        stopConfirmed = false;
        Session = null;
        StopButton.Disabled = true;
        await running.DisposeAsync();
        StopButton.Disabled = false;
        StopButton.Visible = false;
        StopButton.Text = "Stop hosting".Tr();
        StartButton.Visible = true;
        hostingScroll.Visible = false;
        settingsColumn.Visible = true;
        resumedInfo = null;
        hostedSettings = null;
        slotSignature = "";
        standbySignatureSeen = "";
        Refresh();
        SetMessage("Hosting stopped.", false);
    }

    private ClientIdentity Identity() => identity ??= env.Identity();

    private X509Certificate2 Certificate() => certificate ??= env.Certificate();

    private void ShowHosting(HostingSession started, HostedGameInfo? resumed)
    {
        if (!IsInsideTree())
        {
            // The player left (Back) while the host was starting: nothing shows it, so do not leave the port open.
            _ = started.DisposeAsync().AsTask();
            return;
        }
        Session = started;
        resumedInfo = resumed;
        slotSignature = "";
        settingsColumn.Visible = false;
        hostingScroll.Visible = true;
        StartButton.Visible = false;
        StopButton.Visible = true;
        hostingTitle.Text = resumed != null ? "Hosting a saved game".TrText() : $"{"Hosting".TrText()} {Form.GameName.Trim()}";
        hostingStatus.Text = $"{"Friends connect to port".TrText()} {started.Port.ToString(CultureInfo.InvariantCulture)}. {"Host key".TrText()}: {started.Fingerprint}";
        RefreshInvite();
        FillSlots();
        standbySignatureSeen = "";
        UpdateStandby();
        UpdateListing();
        SetMessage("", false);
        HostingStarted?.Invoke(started);
    }

    // Hosting panel

    private void BuildHostingPanel(Container parent)
    {
        hostingScroll.AddChild(hostingPanel);
        hostingPanel.FitConstant(theme, "separation", UiTokens.S1); // T318: a compact stack keeps the invite and the slot table in the framed content
        parent.AddChild(hostingScroll);
        hostingPanel.AddChild(hostingTitle);
        hostingPanel.AddChild(hostingStatus);
        var addressRow = new HBoxContainer();
        addressRow.FitConstant(theme, "separation", UiTokens.S2);
        addressRow.AddChild(new Label { Text = "Address friends use:".Tr(), VerticalAlignment = VerticalAlignment.Center });
        hostingAddresses.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        addressRow.AddChild(hostingAddresses);
        hostingPanel.AddChild(addressRow);
        hostingPanel.AddChild(addressNote);
        hostingPanel.AddChild(new Label { Text = "Invite:".Tr() });
        // T318: the invite and its copy button share a row, so the slot table stays above the fold in the framed layout
        var inviteRow = new HBoxContainer();
        inviteRow.FitConstant(theme, "separation", UiTokens.S2);
        inviteFieldRef.Set(new LineEdit { Name = "InviteField", Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        inviteRow.AddChild(InviteField);
        copyInviteButtonRef.Set(new Button { Text = "Copy invite".Tr(), Name = "CopyInviteButton" });
        CopyInviteButton.Pressed += CopyInvite;
        inviteRow.AddChild(CopyInviteButton);
        hostingPanel.AddChild(inviteRow);
        relayInviteBoxRef.Set(new VBoxContainer { Name = "RelayInviteBox", Visible = false });
        RelayInviteBox.AddChild(new Label { Text = "Invite through the relay (works behind any router):".Tr() });
        relayInUseLabelRef.Set(new Label { Name = "RelayInUseLabel", Text = "" });
        RelayInviteBox.AddChild(RelayInUseLabel);
        relayInviteFieldRef.Set(new LineEdit { Name = "RelayInviteField", Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        RelayInviteBox.AddChild(RelayInviteField);
        copyRelayInviteButtonRef.Set(new Button { Text = "Copy relay invite".Tr(), Name = "CopyRelayInviteButton" });
        CopyRelayInviteButton.Pressed += CopyRelayInvite;
        RelayInviteBox.AddChild(CopyRelayInviteButton);
        hostingPanel.AddChild(RelayInviteBox);
        hostingPanel.AddChild(listingStatus);
        standbyStatusLabelRef.Set(new Label { Name = "StandbyStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        hostingPanel.AddChild(StandbyStatusLabel);
        hostingPanel.AddChild(slotsBox);
        stopButtonRef.Set(new Button { Text = "Stop hosting".Tr(), Name = "StopHostingButton", Visible = false });
        StopButton.Pressed += () => _ = StopAsync();
    }

    /// <summary>T339: the "List this game publicly" box on the Network tab.</summary>
    public CheckBox ListPubliclyCheck => listPubliclyCheckRef.Value;

    /// <summary>T339: what the screen says about the public listing while hosting (empty when there is no relay to list on).</summary>
    public string ListingText => listingStatus.Text;

    /// <summary>T275: where the backup-host designation stands (empty while none).</summary>
    public Label StandbyStatusLabel => standbyStatusLabelRef.Value;

    /// <summary>T275: what the screen says about the backup host, as a sentence.</summary>
    public string StandbyStatusText => StandbyStatusLabel.Text;

    private bool CanList => env.Relay() != null;

    // T339: publishes the lobby on the relay's directory (or removes it). Called on every change of the seats; the listener coalesces to one message per 5 s.
    private void UpdateListing()
    {
        bool relayed = Session is { Lobby: not null, Relay: not null };
        listingStatus.Visible = relayed;
        if (!relayed || Session is not { } session || hostedSettings == null) return;
        var listing = Form.ListPublicly ? ListingFactory.From(hostedSettings, env.DisplayName, session.Slots) : null;
        session.SetListing(listing);
        listingStatus.Text = (listing != null ? "Listed publicly on the relay while you host." : Form.ListPublicly ? "This game's name cannot be listed." : BrowseModel.ListingHelp(true, true)).Tr();
    }

    /// <summary>T392: which relay this game is reached through (its host and port), shown above the relay invite.</summary>
    public Label RelayInUseLabel => relayInUseLabelRef.Value;

    public VBoxContainer RelayInviteBox => relayInviteBoxRef.Value;

    public LineEdit RelayInviteField => relayInviteFieldRef.Value;

    public Button CopyRelayInviteButton => copyRelayInviteButtonRef.Value;

    /// <summary>The invite that reaches this host through the relay by room code; empty while not hosting through one or before the relay answered.</summary>
    public string RelayInvite => Session?.RelayInvite() ?? "";

    private void CopyRelayInvite()
    {
        if (RelayInvite.Length == 0) return;
        env.SetClipboard(RelayInvite);
        SetMessage("Relay invite copied. Send it to your friends.", false);
    }

    private void CopyInvite()
    {
        if (Invite.Length == 0) return;
        env.SetClipboard(Invite);
        SetMessage("Invite copied. Send it to your friends.", false);
    }

    private void RefreshInvite()
    {
        SetupTabs.Clear(hostingAddresses);
        if (Session is not { } session) return;
        if (Addresses.Count > 0)
        {
            var row = new ChoiceRow(Addresses.Select(it => it.Address), Addresses[selectedAddress].Address) { Name = "AddressRow" };
            row.Chosen += address =>
            {
                selectedAddress = Math.Max(0, Addresses.ToList().FindIndex(it => it.Address == address));
                RefreshInvite();
            };
            hostingAddresses.AddChild(row);
        }
        InviteField.Text = session.Invite(InviteAddress);
        RelayInviteBox.Visible = session.Relay != null;
        RelayInUseLabel.Text = env.Relay() is { } inUse ? "Relay in use:".Tr() + " " + inUse.Key : "";
        SyncRelayInviteField();
        if (session.Relay is { Room: null }) _ = RefreshRelayInviteWhenReadyAsync(session);
        addressNote.Text = AddressNoteText(session.Port);
    }

    private void SyncRelayInviteField()
    {
        bool ready = RelayInvite.Length > 0;
        RelayInviteField.Text = RelayInvite;
        RelayInviteField.PlaceholderText = Session?.Relay != null && !ready ? "Allocating code...".Tr() : "";
        CopyRelayInviteButton.Disabled = !ready;
    }

    // The room code arrives when the relay has answered, a moment after hosting starts.
    private async Task RefreshRelayInviteWhenReadyAsync(HostingSession session)
    {
        try
        {
            if (session.Relay is { } relay) await relay.WaitForRoomAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { return; }
        if (IsInstanceValid(this) && Session == session) SyncRelayInviteField();
    }

    private ChoiceRow? FindAddressRow() => (Session != null ? hostingAddresses : networkPage).GetChildren().OfType<ChoiceRow>().FirstOrDefault(it => it.Name == "AddressRow");

    /// <summary>T275: the backup-host state of the running host (a new lobby or a resumed game), or null when not hosting.</summary>
    private string? StandbyState() => Session?.Lobby?.Lobby.Standby.State ?? Session?.Resumed?.Host.Standby.State;

    /// <summary>T275: whether the host's backup host is designated but not ready, so a quit would leave the others stranded.</summary>
    private bool StandbyPending =>
        Session is { } s && (s.Lobby?.Lobby.Standby.KeyId != null || s.Resumed?.Host.Standby.KeyId != null)
        && StandbyState() is { } state && state != Unciv.Net.Standby.StandbyStates.Ready;

    private void UpdateStandby()
    {
        StandbyStatusLabel.Text = HostMigrationModel.BackupHostStateText(StandbyState());
        StandbyStatusLabel.Visible = StandbyStatusLabel.Text.Length > 0;
    }

    private void FillSlots()
    {
        SetupTabs.Clear(slotsBox);
        if (Session is not { } session) return;
        if (resumedInfo is { } game)
        {
            slotsBox.AddChild(new Label { Text = $"{"Saved game with".TrText()} {string.Join(", ", game.Players)}" });
            slotsBox.AddChild(new Label { Text = "They rejoin with the key they used before.".Tr() });
            return;
        }
        foreach (var slot in session.Slots)
        {
            string text = slot.Kind switch
            {
                "Human" when slot.PlayerKeyId == Identity().PlayerKey => $"{slot.Slot + 1}. {slot.PlayerName} ({"you, the host".TrText()})",
                "Human" => $"{slot.Slot + 1}. {slot.PlayerName}{(slot.Connected == false ? $" ({"disconnected".TrText()})" : "")}",
                _ => $"{slot.Slot + 1}. {"Open".TrText()}",
            };
            slotsBox.AddChild(new Label { Text = text });
        }
    }

    // What the host can tell friends about the chosen address, and the port-forward hint.
    private string AddressNoteText(int port)
    {
        string p = port.ToString(CultureInfo.InvariantCulture);
        var typed = Form.PublicAddress.Trim();
        string scope = typed.Length > 0
            ? $"{typed}: {"the address you typed is used in the invite.".TrText()}"
            : Addresses.Count == 0
                ? "No network address was found; the invite uses 127.0.0.1, which only works on this computer.".TrText()
                : Addresses[selectedAddress].Scope == AddressScope.Lan
                    ? $"{Addresses[selectedAddress].Address}: {"friends on the same network (Wi-Fi or LAN) can use this.".TrText()}"
                    : $"{Addresses[selectedAddress].Address}: {"friends on the internet can use this once the port is open.".TrText()}";
        return $"{scope}\n{PortForwardHint(p)}";
    }

    private static string PortForwardHint(string port) =>
        $"{"Friends over the internet need two things: forward TCP port".TrText()} {port} {"on your router to this computer, and your public address (search for \"what is my IP\"; type it under Network, Public address). Friends on the same network need neither.".TrText()}";

    // Pages

    private void AddHeading(Container page, string text, AssetKey? icon = null) => SetupTabs.AddHeading(page, theme, text, icon);

    private LineEdit AddField(Container page, string heading, string name, string text, string placeholder, Action<string> changed, AssetKey? icon = null) =>
        SetupTabs.AddField(page, theme, heading, name, text, placeholder, 260, changed, ShowProblems, icon);

    private void FillGame() => SetupTabs.FillGame(gamePage, theme, Form.Game, rows, Change, AddHostGameTop, includeNoBarbarians: true);

    private void AddHostGameTop(Container page)
    {
        nameFieldRef.Set(AddField(page, "Game name", "NameField", Form.GameName, "Multiplayer Game", value => Form.GameName = value, SetupTabs.UiIcon("Pencil")));
        var counts = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        counts.FitConstant(theme, "h_separation", UiTokens.S6);
        SetupTabs.AddStepper(counts, theme, "Players", new Stepper(theme, NewGameForm.MinPlayers, Math.Max(NewGameForm.MinPlayers, Form.Game.MaxPlayers), Form.Game.Slots.Count, decreaseLabel: "Fewer civs", increaseLabel: "More civs"),
            value => Form.Game.SetPlayerCount(value), Change);
        SetupTabs.AddStepper(counts, theme, "City-states", new Stepper(theme, 0, Form.Game.MaxCityStates, Form.Game.CityStates, decreaseLabel: "Fewer city-states", increaseLabel: "More city-states"),
            value => Form.Game.SetCityStates(value), Change);
        page.AddChild(counts);
        aiFillCheckRef.Set(new CheckBox { Name = "AiFillCheck", Text = "Fill empty civs with AI when the game starts".Tr(), ButtonPressed = Form.AiFill });
        AiFillCheck.Toggled += on => Form.AiFill = on;
        page.AddChild(AiFillCheck);
    }

    private void FillMap() => seedFieldRef.Set(SetupTabs.FillMap(mapPage, theme, Form.Game, rows, Change, ShowProblems));

    private void FillRules() => SetupTabs.FillRules(rulesPage, theme, Form.Game, rows, Change, "Mods (friends need the same ones)");

    private void FillNetwork()
    {
        SetupTabs.Clear(networkPage);
        AddHeading(networkPage, "Turn timer (turns are simultaneous)", SetupTabs.UiIcon("Pause"));
        var timer = new ChoiceRow(HostSettingsForm.TimerChoices.Select(HostSettingsForm.TimerLabel), HostSettingsForm.TimerLabel(Form.TurnTimerSeconds));
        timer.Chosen += label =>
        {
            Form.TurnTimerSeconds = HostSettingsForm.TimerChoices.FirstOrDefault(it => HostSettingsForm.TimerLabel(it) == label);
            Change(null);
        };
        rows["Timer"] = timer;
        networkPage.AddChild(timer);
        dynamicTimerCheckRef.Set(new CheckBox { Name = "DynamicTimerCheck", Text = "Scale each turn's time with the game (era, units, wars)".Tr(), ButtonPressed = Form.DynamicTurnTimer });
        DynamicTimerCheck.Toggled += on =>
        {
            Form.DynamicTurnTimer = on;
            Change(null);
        };
        networkPage.AddChild(DynamicTimerCheck);
        AddHeading(networkPage, "Shortest turn when scaling", SetupTabs.UiIcon("ForwardArrow"));
        var minTimer = new ChoiceRow(HostSettingsForm.MinTimerChoices.Select(HostSettingsForm.TimerLabel), HostSettingsForm.TimerLabel(Form.TurnTimerMinSeconds));
        minTimer.Chosen += label =>
        {
            Form.TurnTimerMinSeconds = HostSettingsForm.MinTimerChoices.FirstOrDefault(it => HostSettingsForm.TimerLabel(it) == label);
            Change(null);
        };
        rows["Shortest turn"] = minTimer;
        networkPage.AddChild(minTimer);
        passwordFieldRef.Set(AddField(networkPage, "Password (empty for an open game)", "PasswordField", Form.Password, "none", value => Form.Password = value, SetupTabs.UiIcon("LockSmall")));
        portFieldRef.Set(AddField(networkPage, "Port (TCP)", "PortField", Form.Port.ToString(CultureInfo.InvariantCulture), HostSettingsForm.DefaultPort.ToString(CultureInfo.InvariantCulture), value =>
        {
            Form.Port = HostSettingsForm.TryParsePort(value, out int port, out _) ? port : -1;
        }, SetupTabs.UiIcon("Link")));
        publicAddressFieldRef.Set(AddField(networkPage, "Public address or name (optional, used in the invite)", "PublicAddressField", Form.PublicAddress, "e.g. 203.0.113.7", value => Form.PublicAddress = value, SetupTabs.UiIcon("Multiplayer")));

        if (!CanList) Form.ListPublicly = false; // nothing to list on
        listPubliclyCheckRef.Set(new CheckBox { Name = "ListPubliclyCheck", Text = "List this game publicly".Tr(), ButtonPressed = Form.ListPublicly && CanList, Disabled = !CanList });
        ListPubliclyCheck.Toggled += on =>
        {
            Form.ListPublicly = on;
            Change(null);
        };
        networkPage.AddChild(ListPubliclyCheck);
        networkPage.AddChild(new Label { Name = "ListingHelp", Text = BrowseModel.ListingHelp(env.RelaySet(), CanList).Tr(), AutowrapMode = TextServer.AutowrapMode.WordSmart }.FitMin(theme, 420));

        AddHeading(networkPage, "Addresses of this computer", SetupTabs.UiIcon("Multiplayer"));
        if (Addresses.Count == 0)
        {
            networkPage.AddChild(new Label { Text = "No network address found.".Tr() });
        }
        else
        {
            var row = new ChoiceRow(Addresses.Select(it => it.Address), Addresses[selectedAddress].Address) { Name = "AddressRow" };
            row.Chosen += address =>
            {
                selectedAddress = Math.Max(0, Addresses.ToList().FindIndex(it => it.Address == address));
                Change(null);
            };
            networkPage.AddChild(row);
        }
        networkPage.AddChild(new Label
        {
            Name = "AddressHint",
            Text = AddressNoteText(Form.Port is >= 1 and <= 65535 ? Form.Port : HostSettingsForm.DefaultPort),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        }.FitMin(theme, 420));
    }

    private void FillSaved()
    {
        SetupTabs.Clear(savedPage);
        resumeButtons.Clear();
        IReadOnlyList<HostedGameInfo> games;
        try
        {
            games = HostedGames.List(env.HostedGamesDirectory, Identity().PlayerKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            savedPage.AddChild(new EmptyState(theme, SetupTabs.UiIcon("ExclamationMark"), $"{"Saved games cannot be read".TrText()}: {e.Message}", wrap: true));
            return;
        }
        if (games.Count == 0)
        {
            var empty = new EmptyState(theme, SetupTabs.UiIcon("Load"), "No saved hosted games yet. A hosted game is saved when you stop hosting.", wrap: true);
            empty.FitMin(theme, 0, 150);
            savedPage.AddChild(empty);
            return;
        }
        AddHeading(savedPage, "Resume a hosted game (it listens on the port from the Network tab)", SetupTabs.UiIcon("Load"));
        foreach (var game in games)
        {
            var line = new HBoxContainer { Name = "Saved" + (resumeButtons.Count + 1).ToString(CultureInfo.InvariantCulture) };
            var info = new Label
            {
                Text = $"{string.Join(", ", game.Players)}  ({game.LastWrittenUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC)",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            var resume = new Button { Text = "Resume".Tr(), Disabled = !game.HostBound };
            if (!game.HostBound) resume.TooltipText = "Another player hosted this game; only they can resume it.".Tr();
            var captured = game;
            resume.Pressed += () => _ = ResumeAsync(captured);
            line.AddChild(info);
            line.AddChild(resume);
            savedPage.AddChild(line);
            resumeButtons.Add(resume);
        }
    }
}
