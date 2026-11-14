using System.Globalization;
using Godot;
using Unciv.Assets;
using Unciv.Client.Net.Session;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Localization;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;
using Unciv.Net;
using Unciv.Net.Lobby;
using Unciv.Server;
using Unciv.Utils;

namespace Unciv.GodotClient.Setup;

/// <summary>
/// C035: the lobby screen, one view for the host and for a joiner. It reads the <see cref="LobbyState"/> its <see cref="ClientSession"/> receives and changes the player's own seat
/// only by sending <c>LobbySet</c> (nation, colour, ready) through that session; the host additionally holds the in-process <see cref="ServerLobby"/> for Kick, Close or Open slot,
/// AI slot and Start. The settings are shown read-only (the host edits them on the Host screen). Chat goes through the session. The screen drains the session's event channel on the main
/// thread (<see cref="_Process"/>), so a refusal, a kick or the host closing the lobby reaches the player as a sentence, and the start of the game as <see cref="GameStarting"/>.
/// A joiner whose join was refused (for example a ruleset mismatch) gets the same screen from <see cref="Refused"/>, which shows only the reason.
/// </summary>
public partial class LobbyScreen : Control
{
    private const int MaxChatLines = 40;

    private readonly UiTheme theme;
    private readonly NewGameForm nations;
    private readonly ClientSession? session;
    private readonly ServerLobby? hostControls;
    private readonly string ownKey;
    private ScreenFrame frame = null!; // JUSTIFY: T318 assigned by Build, which every constructor runs
    private readonly VBoxContainer slotsBox = new() { Name = "Slots" };
    private readonly Label readySummaryLabel = new() { Name = "ReadySummary", ThemeTypeVariation = UiVariants.LabelSmall, Visible = false };
    private readonly ScrollContainer bodyScroll = new() { Name = "LobbyScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly VBoxContainer nationBox = new() { Name = "NationPicker", Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly BoxContainer bottomRow = new() { Name = "SettingsAndChat", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly Label settingsLabel = new() { Name = "Settings", SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly VBoxContainer chatBox = new() { Name = "ChatLog" };
    private readonly ScrollContainer chatScroll = new() { Name = "ChatScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly Label messageLabel = new() { Name = "LobbyMessage", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label startHint = new() { Name = "StartHint", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Dictionary<string, Button> nationButtons = [];
    private readonly Dictionary<string, Button> colourButtons = [];
    private readonly Dictionary<int, Button> kickButtons = [];
    private readonly Dictionary<int, Button> banButtons = [];
    private readonly Dictionary<int, Button> closeButtons = [];
    private readonly Dictionary<int, Button> aiButtons = [];
    private readonly Dictionary<int, Button> openButtons = [];
    private readonly List<Label> slotLabels = [];
    private readonly List<string> chat = [];
    private KickBanView? reasonView;
    private readonly Func<Task>? acceptStandby;
    private readonly Func<Task>? declineStandby;
    private readonly VBoxContainer backupBox = new() { Name = "BackupHost" };
    private readonly Label trustLabel = new() { Name = "BackupHostNotice", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label standbyStatusLabel = new() { Name = "StandbyStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label backupPickerHeader = new() { Name = "BackupHostHeader", ThemeTypeVariation = UiVariants.LabelOverline };
    private readonly HFlowContainer backupPickerBox = new() { Name = "BackupHostPicker" };
    private readonly VBoxContainer offerBox = new() { Name = "StandbyOffer", Visible = false };
    private readonly Label offerLabel = new() { Name = "StandbyOfferText", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private bool standbyOffered;
    private LobbyState? lobby;
    private bool busy;
    private bool ended;

    public LobbyScreen(UiTheme theme, NewGameForm nations, ClientSession session, string ownKey, ServerLobby? hostControls = null, Func<Task>? acceptStandby = null, Func<Task>? declineStandby = null)
    {
        this.theme = theme;
        this.nations = nations;
        this.session = session;
        this.ownKey = ownKey;
        this.hostControls = hostControls;
        this.acceptStandby = acceptStandby;
        this.declineStandby = declineStandby;
        Name = "LobbyScreen";
        Build(hostControls != null);
        reasonView = new KickBanView(theme);
        AddChild(reasonView);
        lobby = session.Lobby;
        Rebuild();
    }

    private LobbyScreen(UiTheme theme, NewGameForm nations, string reason)
    {
        this.theme = theme;
        this.nations = nations;
        ownKey = "";
        Name = "LobbyScreen";
        Build(false);
        End(reason);
    }

    /// <summary>The screen of a joiner the host refused: only the reason (<see cref="LobbyModel.RefusalText"/>) and Leave.</summary>
    public static LobbyScreen Refused(UiTheme theme, NewGameForm nations, string code) => new(theme, nations, LobbyModel.RefusalText(code));

    public Button LeaveButton { get; private set; } = null!; // JUSTIFY: C035 assigned by Build, which every constructor runs

    public Button StartButton { get; private set; } = null!; // JUSTIFY: C035 assigned by Build, which every constructor runs

    public Button ReadyButton { get; private set; } = null!; // JUSTIFY: C035 assigned by Build, which every constructor runs

    public Button NationToggle { get; private set; } = null!; // JUSTIFY: C035 assigned by Build, which every constructor runs

    public Button SendButton { get; private set; } = null!; // JUSTIFY: C035 assigned by Build, which every constructor runs

    public LineEdit ChatField { get; private set; } = null!; // JUSTIFY: C035 assigned by Build, which every constructor runs

    /// <summary>Raised when the player presses Leave; the owner of the screen leaves the session (or stops hosting) and pops the screen.</summary>
    public event Action? LeaveRequested;

    /// <summary>Raised when the host started the game (the session announced it); the game screen takes over from here.</summary>
    public event Action? GameStarting;

    /// <summary>What the screen currently tells the player (an error, a refusal, a closed lobby), or empty.</summary>
    public string Message => messageLabel.Text;

    /// <summary>The text of the slot table, one line per slot.</summary>
    public IReadOnlyList<string> SlotLines => slotLabels.Select(it => it.Text).ToList();

    /// <summary>The read-only settings, one line each.</summary>
    public IReadOnlyList<string> SettingsLines => settingsLabel.Text.Split('\n');

    /// <summary>The chat so far, oldest first, as "Sender: text".</summary>
    public IReadOnlyList<string> ChatLines => chat;

    public string StartReason => startHint.Text;

    public LobbyState? State => lobby;

    public bool IsHost => hostControls != null;

    /// <summary>T275: the trust notice naming the backup host (from <see cref="LobbyState.BackupHostNotice"/>), empty while none is designated.</summary>
    public string BackupHostNoticeText => trustLabel.Text;

    /// <summary>T275: where this player's own standby stands, or what the host sees of the designation, as a sentence.</summary>
    public string StandbyStatusText => standbyStatusLabel.Text;

    /// <summary>T275: the consent prompt's text while it is open (this player was offered the backup-host role), else empty.</summary>
    public string StandbyOfferText => offerBox.Visible ? offerLabel.Text : "";

    /// <summary>T275: the host's backup-host picker: "None" or the name of the human slot that is the backup host.</summary>
    public string BackupHostChoice { get; private set; } = "";

    public Button? AcceptStandbyButton { get; private set; }

    public Button? DeclineStandbyButton { get; private set; }

    /// <summary>The nation button of the picker (the picker is open), including "Random".</summary>
    public Button? NationButton(string nation) => nationButtons.GetValueOrDefault(nation);

    public Button? ColourButton(string colour) => colourButtons.GetValueOrDefault(colour);

    public Button? KickButton(int slot) => kickButtons.GetValueOrDefault(slot);

    public Button? BanButton(int slot) => banButtons.GetValueOrDefault(slot);

    /// <summary>T348: the kick/ban reason popup, open after the host pressed Kick or Ban on a seat.</summary>
    public KickBanView ReasonView => reasonView ?? throw new InvalidOperationException("the host screen always builds a reason view");

    public Button? CloseButton(int slot) => closeButtons.GetValueOrDefault(slot);

    public Button? AiButton(int slot) => aiButtons.GetValueOrDefault(slot);

    public Button? OpenButton(int slot) => openButtons.GetValueOrDefault(slot);

    /// <summary>Scrolls the nation picker and the body so the control is in view.</summary>
    public void Reveal(Control control)
    {
        // T318: the control can sit inside the picker's own scroll and the body's, so both follow it (inner first, outer second)
        for (var node = control.GetParent(); node != null; node = node.GetParent())
            if (node is ScrollContainer scroll) scroll.EnsureControlVisible(control);
    }

    public override void _Process(double delta)
    {
        if (session == null || ended) return;
        bool changed = false;
        while (session.Events.TryRead(out var next))
        {
            switch (next)
            {
                case LobbyUpdated updated:
                    lobby = updated.Lobby;
                    changed = true;
                    break;
                case LobbyChatReceived received:
                    AddChat(received.Sender, received.Text);
                    break;
                case GameStarted:
                    GameStarting?.Invoke();
                    break;
                case JoinRefusedEvent refused:
                    End(LobbyModel.RefusalText(refused.Code, refused.Reason));
                    return;
                case HostClosed:
                    End("The host closed the lobby.");
                    return;
                case StandbyOffered: // T275: this player was chosen as the backup host; nothing is stored or shared until Accept.
                    standbyOffered = true;
                    offerLabel.Text = HostMigrationModel.BackupHostOfferText();
                    offerBox.Visible = true;
                    backupBox.Visible = true;
                    break;
                case StandbyStatusChanged standby: // T275: where this player's own standby stands.
                    standbyStatusLabel.Text = HostMigrationModel.BackupHostStateText(standby.Status.State);
                    standbyStatusLabel.Visible = standbyStatusLabel.Text.Length > 0;
                    break;
                case StandbyRevoked: // T275: the designation ended.
                    standbyOffered = false;
                    offerBox.Visible = false;
                    standbyStatusLabel.Text = "";
                    standbyStatusLabel.Visible = false;
                    break;
                case SessionEnded over when !string.IsNullOrEmpty(over.Reason) && over.Reason != ClientCodes.SessionClosed:
                    End(LobbyModel.RefusalText(over.Reason));
                    return;
            }
        }
        if (changed) Rebuild();
    }

    /// <summary>Rebuilds the slot table, the settings and the Start button from the latest <see cref="LobbyState"/>.</summary>
    public void Rebuild()
    {
        if (lobby is not { } state) return;
        frame.TitleLabel.Text = $"{"Lobby".TrText()}: {LobbyModel.Title(state)}";
        settingsLabel.Text = string.Join("\n", LobbyModel.SettingsLines(state));
        FillSlots(state);
        FillPicker(state);
        var check = LobbyModel.CanStart(state);
        StartButton.Visible = IsHost;
        StartButton.Disabled = busy || !check.CanStart;
        startHint.Visible = IsHost;
        startHint.Text = check.Reason;
        FillBackupHost(state);
    }

    /// <summary>T275: the backup-host block: the trust notice to everyone, the picker and standby status for the host, the consent prompt for the chosen player.</summary>
    private void FillBackupHost(LobbyState state)
    {
        // The notice names the backup host and is shown to every player while one is designated.
        trustLabel.Visible = !string.IsNullOrEmpty(state.BackupHostNotice);
        trustLabel.Text = state.BackupHostNotice ?? "";
        // The host picks the backup host (and sees the standby status of the coordinator); the chosen player sees its own status from the StandbyStatusChanged event.
        if (IsHost && hostControls != null)
        {
            if (hostControls.Lobby.Settings.BackupHost is int designated && designated >= 0 && designated < state.Slots.Count)
            {
                var slot = state.Slots[designated];
                BackupHostChoice = slot.Kind == "Human" && slot.PlayerName != null ? slot.PlayerName : "None";
            }
            else
            {
                BackupHostChoice = "None";
            }
            standbyStatusLabel.Text = HostMigrationModel.BackupHostStateText(hostControls.Lobby.Standby.State);
            FillBackupPicker(state);
        }
        // The block collapses when there is nothing to say, so a lobby without a backup host keeps its old layout.
        standbyStatusLabel.Visible = standbyStatusLabel.Text.Length > 0;
        backupPickerBox.Visible = backupPickerBox.GetChildCount() > 0;
        backupPickerHeader.Visible = backupPickerBox.Visible;
        backupBox.Visible = trustLabel.Visible || standbyStatusLabel.Visible || offerBox.Visible || backupPickerBox.Visible;
    }

    private void FillBackupPicker(LobbyState state)
    {
        Clear(backupPickerBox);
        var picker = new List<(string Label, int? Slot)> { ("None", null) };
        picker.AddRange(state.Slots.Where(it => it.Slot != 0 && it.Kind == "Human" && !string.IsNullOrEmpty(it.PlayerName))
            .Select(it => (it.PlayerName ?? $"Player {it.Slot + 1}", (int?)it.Slot)));
        foreach (var (label, slot) in picker)
        {
            var captured = slot;
            var button = new Button { Text = label.Tr(), Name = "BackupHost_" + (slot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"), ToggleMode = true, ButtonPressed = slot == hostControls.NotNull().Lobby.Settings.BackupHost };
            button.Pressed += () => _ = RunAsync(async () =>
            {
                var refusal = await hostControls.NotNull().Lobby.SetBackupHostAsync(captured);
                if (refusal != null) SetMessage(HostMigrationModel.BackupHostRefusal(refusal), true);
            });
            backupPickerBox.AddChild(button);
        }
    }

    private LobbySlot? Own(LobbyState state) => state.Slots.FirstOrDefault(it => it.Kind == "Human" && it.PlayerKeyId == ownKey);

    private void Build(bool host)
    {
        Theme = theme.Theme; // T318: the frame and the footer take the kit's theme even under a bare parent
        this.Fill();
        var backdrop = new ColorRect { Name = "Backdrop", Color = ProceduralAssets.ToColor(UiTokens.Bg1), MouseFilter = MouseFilterEnum.Ignore };
        backdrop.Fill();
        AddChild(backdrop);
        // T318: the same frame as New game and Options: the lobby title in the header, the seats and picker in a scrolling body, Start/Leave in the footer
        frame = new ScreenFrame(theme, "Lobby", closeButton: false);
        frame.Fill();
        AddChild(frame);
        var column = new VBoxContainer { Name = "LobbyColumn", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        column.FitConstant(theme, "separation", UiTokens.S2);
        bodyScroll.AddChild(column);
        frame.SetContent(bodyScroll);
        slotsBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        slotsBox.FitConstant(theme, "separation", UiTokens.S1);
        var slotsHeader = new HBoxContainer { Name = "SlotsHeader" };
        slotsHeader.AddChild(new Label { Text = "Players".Tr(), ThemeTypeVariation = UiVariants.LabelOverline, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        slotsHeader.AddChild(readySummaryLabel);
        column.AddChild(slotsHeader);
        column.AddChild(slotsBox);

        // T275: the backup-host block (trust notice to everyone, the picker and standby status for the host) and the consent prompt for the chosen player.
        backupBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        backupBox.FitConstant(theme, "separation", UiTokens.S1);
        column.AddChild(backupBox);
        backupBox.AddChild(trustLabel);
        backupBox.AddChild(standbyStatusLabel);
        backupPickerHeader.Text = "Backup host".Tr();
        backupBox.AddChild(backupPickerHeader);
        backupPickerBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        backupPickerBox.FitConstant(theme, "h_separation", UiTokens.S2);
        backupBox.AddChild(backupPickerBox);
        var offer = new VBoxContainer { Name = "StandbyOfferRow" };
        offer.FitConstant(theme, "separation", UiTokens.S1);
        offerBox.AddChild(offerLabel);
        var offerButtons = new HBoxContainer();
        AcceptStandbyButton = new Button { Text = "Accept".Tr(), Name = "AcceptStandbyButton" };
        AcceptStandbyButton.Pressed += () => _ = RunAsync(async () =>
        {
            var accept = acceptStandby;
            if (accept != null) await accept();
            offerBox.Visible = false;
        });
        DeclineStandbyButton = new Button { Text = "Decline".Tr(), Name = "DeclineStandbyButton" };
        DeclineStandbyButton.Pressed += () => _ = RunAsync(async () =>
        {
            var decline = declineStandby ?? (session == null ? null : (Func<Task>)(() => session.DeclineStandbyAsync().AsTask()));
            if (decline != null) await decline();
            offerBox.Visible = false;
        });
        offerButtons.AddChild(AcceptStandbyButton);
        offerButtons.AddChild(DeclineStandbyButton);
        offer.AddChild(offerButtons);
        offerBox.AddChild(offer);
        backupBox.AddChild(offerBox);

        var seat = new HBoxContainer();
        seat.FitConstant(theme, "separation", UiTokens.S2);
        ReadyButton = new Button { Text = "Ready".Tr(), Name = "ReadyButton", ToggleMode = true, Visible = false };
        ReadyButton.Pressed += OnReady;
        NationToggle = new Button { Text = "Pick nation".Tr(), Name = "NationToggle", ToggleMode = true, Visible = false };
        NationToggle.Pressed += () => ShowPicker(NationToggle.ButtonPressed);
        seat.AddChild(ReadyButton);
        seat.AddChild(NationToggle);
        column.AddChild(seat);

        // T414: NationPicker sits directly in bodyScroll (no nested ScrollContainer fighting bodyScroll or slicing the bottom row).
        nationBox.FitConstant(theme, "separation", UiTokens.S2);
        column.AddChild(nationBox);

        // T414: stack Settings and Chat vertically on Compact/phone breakpoints, side-by-side on desktop; Settings is never clipped inside a nested ScrollContainer.
        theme.Fit(bottomRow, () => bottomRow.Vertical = theme.Breakpoint == UiBreakpoint.Compact);
        bottomRow.FitConstant(theme, "separation", UiTokens.S3);
        column.AddChild(bottomRow);
        var settingsCard = new PanelContainer { Name = "SettingsCard", ThemeTypeVariation = UiVariants.PanelCard, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2 };
        var settings = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        settings.FitConstant(theme, "separation", UiTokens.S1);
        settings.AddChild(new Label { Text = (host ? "Settings (change on Host)" : "Settings (by host)").Tr(), ThemeTypeVariation = UiVariants.LabelOverline, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        theme.Fit(settingsLabel, () => settingsLabel.AddThemeFontSizeOverride("font_size", theme.Px(13)));
        settings.AddChild(settingsLabel);
        settingsCard.AddChild(settings);
        bottomRow.AddChild(settingsCard);
        var chatCard = new PanelContainer { Name = "ChatCard", ThemeTypeVariation = UiVariants.PanelCard, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 3 };
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        right.FitConstant(theme, "separation", UiTokens.S1);
        chatCard.AddChild(right);
        bottomRow.AddChild(chatCard);
        right.AddChild(new Label { Text = "Chat".Tr(), ThemeTypeVariation = UiVariants.LabelOverline });
        chatBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        chatScroll.FitMin(theme, 0, 120);
        chatScroll.AddChild(chatBox);
        right.AddChild(chatScroll);
        var send = new HBoxContainer();
        send.FitConstant(theme, "separation", UiTokens.S1);
        ChatField = new LineEdit { Name = "ChatField", PlaceholderText = "Say something".Tr(), SizeFlagsHorizontal = SizeFlags.ExpandFill, MaxLength = SocialModel.MaxChatChars };
        ChatField.TextSubmitted += _ => SendChat();
        SendButton = new Button { Text = "Send".Tr(), Name = "SendButton" };
        SendButton.Pressed += SendChat;
        send.AddChild(ChatField);
        send.AddChild(SendButton);
        right.AddChild(send);

        StartButton = new Button { Text = "Start game".Tr(), Name = "StartGameButton", Visible = false, Disabled = true };
        StartButton.Pressed += () => _ = StartAsync();
        LeaveButton = new Button { Text = "Leave".Tr(), Name = "LeaveButton" };
        LeaveButton.Pressed += () => LeaveRequested?.Invoke();
        frame.SetFooter([(LeaveButton, ChoiceRole.Ghost), (StartButton, ChoiceRole.Primary)]);
        column.AddChild(startHint);
        column.AddChild(messageLabel);
    }

    // The nation picker takes the place of the settings and chat while it is open (the window is small).
    private void ShowPicker(bool show)
    {
        nationBox.Visible = show;
        bottomRow.Visible = !show;
        // T318: closing the picker goes back to the seats at the top of the scrolled body (the picker can be far down after scrolling through nations)
        if (!show) bodyScroll.ScrollVertical = 0;
    }

    private void FillSlots(LobbyState state)
    {
        Clear(slotsBox);
        slotLabels.Clear();
        kickButtons.Clear();
        banButtons.Clear();
        closeButtons.Clear();
        aiButtons.Clear();
        openButtons.Clear();
        var own = Own(state);
        var readySummary = LobbyModel.ReadySummary(state);
        readySummaryLabel.Visible = readySummary.Total > 0;
        readySummaryLabel.Text = readySummary.Text.TrText();
        foreach (var slot in state.Slots)
        {
            int index = slot.Slot;
            var line = new HBoxContainer { Name = $"Slot{(index + 1).ToString(CultureInfo.InvariantCulture)}" };
            line.FitConstant(theme, "separation", UiTokens.S2);
            var text = new Label { Text = LobbyModel.SlotText(slot, ownKey, state.HostName), VerticalAlignment = VerticalAlignment.Center }.FitMin(theme, 150);
            line.AddChild(text);
            slotLabels.Add(text);
            if (slot.Kind == "Human")
            {
                var readyBadge = new IconLabel(theme, AssetKey.Of(RulesetAssetKeys.UiIcon, slot.Ready ? "Checkmark" : "Pause"), (slot.Ready ? "Ready" : "Not ready").TrText(), iconSize: 14).FitMin(theme, 78);
                var tone = ProceduralAssets.ToColor(slot.Ready ? UiTokens.Success : UiTokens.TextMuted);
                readyBadge.IconNode.Modulate = tone;
                readyBadge.LabelNode.AddThemeColorOverride("font_color", tone);
                line.AddChild(readyBadge);
                line.AddChild(NationBadge(slot));
                line.AddChild(ColourChip(slot.Color));
            }
            if (IsHost) AddHostButtons(line, slot);
            slotsBox.AddChild(line);
        }
        ReadyButton.Visible = own != null;
        NationToggle.Visible = own != null;
        if (own != null)
        {
            ReadyButton.ButtonPressed = own.Ready;
            ReadyButton.Text = (own.Ready ? "Ready (press to cancel)" : "Ready").TrText();
        }
        else ShowPicker(false);
    }

    private Control NationBadge(LobbySlot slot)
    {
        string nation = slot.Nation ?? Constants.Random;
        var row = new HBoxContainer();
        row.FitConstant(theme, "separation", UiTokens.S1);
        row.AddChild(SetupTabs.NationEmblem(theme, nations, nation, 22));
        var label = new Label { Text = nation.TrText(), VerticalAlignment = VerticalAlignment.Center }.FitMin(theme, 64);
        if (nations.NationColours(nation) is { } colours) label.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(colours.Background).Lightened(0.35f));
        row.AddChild(label);
        return row;
    }

    private Control ColourChip(string? colour)
    {
        string labelText = string.IsNullOrEmpty(colour) ? "Default" : colour;
        var chip = new PanelContainer { ThemeTypeVariation = UiVariants.PanelKeyHint, TooltipText = $"{"Colour".Tr()}: {labelText.TrText()}" };
        var row = new HBoxContainer();
        row.FitConstant(theme, "separation", UiTokens.S1);
        row.AddChild(SetupTabs.ColourDot(theme, PlaceholderPalette.ForLobbyColour(colour), 12));
        row.AddChild(new Label { Text = labelText.TrText(), ThemeTypeVariation = UiVariants.LabelSmall, VerticalAlignment = VerticalAlignment.Center });
        chip.AddChild(row);
        return chip;
    }

    private void AddHostButtons(HBoxContainer line, LobbySlot slot)
    {
        int index = slot.Slot;
        var lobbyHost = hostControls.NotNull().Lobby;
        switch (slot.Kind)
        {
            case "Human" when slot.PlayerKeyId != ownKey:
                kickButtons[index] = Add(line, "Kick", () => AskReasonAsync("Kick", slot, (reason) => lobbyHost.KickAsync(index, reason)));
                banButtons[index] = Add(line, "Ban", () => AskReasonAsync("Ban", slot, (reason) => lobbyHost.BanAsync(index, reason)));
                break;
            case "Open":
                aiButtons[index] = Add(line, "AI", () => lobbyHost.SetSlotKindAsync(index, SlotKind.AI));
                closeButtons[index] = Add(line, "Close", () => lobbyHost.CloseSlotAsync(index));
                break;
            case "AI":
                openButtons[index] = Add(line, "Open", () => lobbyHost.SetSlotKindAsync(index, SlotKind.Open));
                break;
            case "Closed":
                openButtons[index] = Add(line, "Open", () => lobbyHost.OpenSlotAsync(index));
                break;
        }
    }

    /// <summary>T348: asks the host for an optional reason, then runs the action with it. The reason the removed player sees is never taken from a slot or a name.</summary>
    private Task AskReasonAsync(string verb, LobbySlot slot, Func<string?, Task> action)
    {
        string name = slot.PlayerName ?? $"Player {slot.Slot + 1}";
        reasonView.NotNull().Open(verb, name, reason => _ = RunAsync(() => action(reason)));
        return Task.CompletedTask;
    }

    private Button Add(HBoxContainer line, string text, Func<Task> action)
    {
        var button = new Button { Text = text.Tr(), Name = $"{text}Button" };
        button.Pressed += () => _ = RunAsync(action);
        line.AddChild(button);
        return button;
    }

    private void FillPicker(LobbyState state)
    {
        Clear(nationBox);
        nationButtons.Clear();
        colourButtons.Clear();
        if (Own(state) is not { } own) return;
        SetupTabs.AddHeading(nationBox, theme, "Nation", SetupTabs.UiIcon("Nations"));
        nationBox.AddChild(SetupTabs.CreateNationGrid(
            theme,
            nations,
            nationButtons,
            name => string.Equals(own.Nation, name, StringComparison.OrdinalIgnoreCase),
            name => name != Constants.Random && state.Slots.Any(it => it.Slot != own.Slot && it.Kind != "Closed" && string.Equals(it.Nation, name, StringComparison.OrdinalIgnoreCase)),
            name => _ = RunAsync(async () => await Session().SetLobbySlotAsync(own.Slot, name, null, own.Ready))));

        SetupTabs.AddHeading(nationBox, theme, "Colour", SetupTabs.UiIcon("Options"));
        var coloursRow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        coloursRow.FitConstant(theme, "h_separation", UiTokens.S2);
        coloursRow.FitConstant(theme, "v_separation", UiTokens.S2);
        nationBox.AddChild(coloursRow);
        foreach (string name in PlaceholderPalette.LobbyColourNames)
        {
            string colour = name;
            var button = new Button
            {
                ToggleMode = true,
                ButtonPressed = own.Color == colour,
                ThemeTypeVariation = UiVariants.ButtonRow,
                TooltipText = colour.TrText(),
            };
            button.FitMin(theme, 96, 36);
            button.SetMeta(IconButton.LabelMeta, colour);
            var cell = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
            cell.SetAnchorsPreset(LayoutPreset.FullRect);
            theme.Fit(cell, () =>
            {
                int pad = theme.Px(UiTokens.S2);
                cell.OffsetLeft = pad;
                cell.OffsetRight = -pad;
            });
            cell.FitConstant(theme, "separation", UiTokens.S2);
            var dot = SetupTabs.ColourDot(theme, PlaceholderPalette.ForLobbyColour(colour), 14);
            dot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cell.AddChild(dot);
            cell.AddChild(new Label { Text = colour.TrText(), MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = SizeFlags.ExpandFill });
            button.AddChild(cell);
            button.Disabled = state.Slots.Any(it => it.Slot != own.Slot && it.Kind == "Human" && it.Color == colour);
            button.Pressed += () => _ = RunAsync(async () => await Session().SetLobbySlotAsync(own.Slot, null, colour, own.Ready));
            colourButtons[colour] = button;
            coloursRow.AddChild(button);
        }
    }

    private ClientSession Session() => session.NotNull();

    private void OnReady()
    {
        if (lobby is not { } state || Own(state) is not { } own) return;
        bool ready = !own.Ready;
        _ = RunAsync(async () => await Session().SetLobbySlotAsync(own.Slot, null, null, ready));
    }

    private void SendChat()
    {
        string text = ChatField.Text.Trim();
        if (text.Length == 0 || session == null || ended) return;
        ChatField.Text = "";
        _ = RunAsync(async () => await session.SendChatAsync(text));
    }

    private void AddChat(string? sender, string text)
    {
        chat.Add($"{sender ?? "?"}: {text}");
        while (chat.Count > MaxChatLines) chat.RemoveAt(0);
        Clear(chatBox);
        foreach (string line in chat) chatBox.AddChild(new Label { Text = line, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Callable.From(() => chatScroll.ScrollVertical = (int)chatScroll.GetVScrollBar().MaxValue).CallDeferred();
    }

    private async Task StartAsync()
    {
        if (busy || hostControls == null) return;
        busy = true;
        StartButton.Disabled = true;
        try
        {
            SetMessage("Starting the game ...", false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await hostControls.StartGameAsync();
            SetMessage("", false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetMessage(e.Message, true);
        }
        finally
        {
            busy = false;
            Rebuild();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetMessage(e.Message, true);
        }
    }

    // The lobby is over for this player: nothing more to change, only the reason and Leave.
    private void End(string reason)
    {
        ended = true;
        if (reasonView != null) reasonView.Visible = false;
        ReadyButton.Visible = false;
        NationToggle.Visible = false;
        ShowPicker(false);
        bottomRow.Visible = false;
        StartButton.Visible = false;
        startHint.Visible = false;
        SendButton.Disabled = true;
        ChatField.Editable = false;
        Clear(slotsBox);
        slotLabels.Clear();
        SetMessage(reason, true);
    }

    private void SetMessage(string text, bool isError)
    {
        messageLabel.Text = text.TrText();
        messageLabel.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(isError ? PlaceholderPalette.ErrorText : PlaceholderPalette.Text));
    }

    private static void Clear(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }
}
