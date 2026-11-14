using System.Globalization;
using Godot;
using Unciv.Assets;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Settings;
using Unciv.GodotClient.Ui;
using Unciv.Models;
using Unciv.Models.Ruleset;
using Unciv.Net.Visibility;
using Unciv.Models.Translations;
using Unciv.GodotClient.Localization;
using RulesetNation = Unciv.Models.Ruleset.Nation.Nation;

namespace Unciv.GodotClient.Saves;

/// <summary>
/// C016: save and load for the running game. The dialog (opened by the main menu, the in-game menu and Ctrl+S / Ctrl+L, T385: there is no floating button and no F5/F9/F10 key here)
/// lists the saves (name, turn, civ, ruleset and mods, date) with Save (under the typed name), Load, Delete (asks again), Resume last and Close; an autosave is written whenever a new turn begins
/// that the settings' autosave interval selects. The game is read and replaced only through <see cref="IGameSession"/>: saving is <see cref="IGameSession.ExportSaveAsync"/>,
/// loading hands the file text to the composition root's <c>loader</c> and then to <c>onLoaded</c>, which shows the new session (the screen's Start). All file work is in
/// <see cref="SaveStore"/>; every failure ends as a message on screen, never an exception or a changed game.
/// </summary>
public partial class SaveController : Control
{
    private const float DialogShare = 0.8f;
    private const float CompactDialogShare = 0.94f;
    private const float CompactWidth = 800f;

    private readonly SaveStore store;
    private readonly ClientSettingsService settings;
    private UiTheme theme;
    private readonly Func<string, IGameSession> loader;
    private readonly Action<IGameSession> onLoaded;
    private IGameSession? session;
    private int lastTurn = -1;
    private string? pendingDelete;
    private Control? dialog;
    private PanelContainer? dialogPanel;
    private ScreenFrame? frame;
    private Button? resumeButton;
    private Button? deleteButton;
    private Button? confirmDelete;
    private Button? loadButton;
    private Button? saveButton;
    private DataTable? table;
    private LineEdit? search;
    private ScrollContainer? scroller;
    private EmptyState? empty;
    private HBoxContainer? detailsHeader;
    private Control? detailsCrestSlot;
    private Label? detailsTitle;
    private Label? detailsBody;
    private Label? detailsWarning;
    private EmptyState? detailsEmpty;
    private Label? saveNameLabel;
    private Label? dialogMessage;
    private string? selectedName;
    private bool compact;
    private IReadOnlyList<SaveEntry> entries = [];
    private IReadOnlyList<SaveEntry> shown = [];
    private readonly SaveProgress progress = new();

    public SaveController(SaveStore store, ClientSettingsService settings, UiTheme theme, Func<string, IGameSession> loader, Action<IGameSession> onLoaded)
    {
        this.store = store;
        this.settings = settings;
        this.theme = theme;
        this.loader = loader;
        this.onLoaded = onLoaded;
        Name = "SaveController";
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Always set after an action, success or failure.</summary>
    public string Message { get; private set; } = "";

    /// <summary>T385: the status of an action that ended while the dialog was closed (a failed autosave, a load, a refused resume); the host shows it (a toast in a game, a line on the main menu). With the dialog open the status is shown in the dialog instead.</summary>
    public event Action<string>? StatusChanged;

    public bool IsDialogOpen => dialog != null;

    /// <summary>C044: whether the attached game has accepted commands since the last save, autosave or load. The in-game menu asks before it throws such progress away.</summary>
    public bool HasUnsavedProgress => session != null && progress.IsUnsaved(session.Snapshot.Seq);

    public LineEdit? NameField { get; private set; }

    /// <summary>The names shown in the open dialog, in list order.</summary>
    public IReadOnlyList<string> ListedNames => shown.Select(it => it.Name).ToList();

    public override void _Ready()
    {
        GetViewport().SizeChanged += Layout;
        theme.LayoutChanged += Layout;
        Layout();
    }

    public override void _ExitTree()
    {
        GetViewport().SizeChanged -= Layout;
        theme.LayoutChanged -= Layout;
        Detach();
    }

    /// <summary>
    /// T405: follows the theme of the game screen the dialog floats over, so its breakpoint and pixel sizes are the game UI's (the screen builds a fresh, bound theme for every game,
    /// load and hotseat hand-over). The main menu's controller never calls this: its theme is bound by the <see cref="ScreenManager"/> for the whole menu.
    /// </summary>
    public void UseTheme(UiTheme value)
    {
        if (ReferenceEquals(theme, value)) return;
        if (IsInsideTree()) theme.LayoutChanged -= Layout;
        theme = value;
        if (IsInsideTree()) theme.LayoutChanged += Layout;
        if (dialog != null) dialog.Theme = theme.Theme;
        Layout();
    }

    /// <summary>T321: the table columns and the dialog share depend on the breakpoint, which changes live; the viewport subscription covers a theme that is not bound.</summary>
    private void Layout()
    {
        Size = GetViewportRect().Size;
        bool wasCompact = compact;
        compact = IsCompact();
        if (table != null && compact != wasCompact) RebuildTable();
        if (dialogPanel != null)
        {
            var panelSize = (Size * (compact ? CompactDialogShare : DialogShare)).Floor();
            dialogPanel.Size = panelSize;
            dialogPanel.Position = ((Size - panelSize) / 2f).Floor();
        }
    }

    /// <summary>
    /// T321: the breakpoint at the current window and scale. An in-game controller gets its own theme that no <see cref="ScreenManager"/> binds, so it is derived from the width here rather than
    /// from <see cref="UiTheme.Breakpoint"/> (which stays Regular without a bound viewport); the 800 px fallback also covers a very narrow dialog on a regular window.
    /// </summary>
    private bool IsCompact() => UiTokens.BreakpointFor(Size.X / theme.Scale) == UiBreakpoint.Compact || Size.X < CompactWidth;

    /// <summary>Starts watching this session for new turns (autosave) and saves/loads it. Call again with the loaded session.</summary>
    public void Attach(IGameSession gameSession)
    {
        Detach();
        session = gameSession;
        lastTurn = gameSession.Snapshot.Turn;
        progress.Reset(gameSession.Snapshot.Seq);
        gameSession.SnapshotChanged += OnSnapshotChanged;
        // C029: an autosave that fell into a hotseat hand-over (the game cannot be saved then) is made once the next player has taken over
        if (deferredAutosave is { } due && gameSession.Snapshot.Turn >= due)
        {
            deferredAutosave = null;
            Run(AutosaveAsync(due));
        }
    }

    private void Detach()
    {
        if (session != null) session.SnapshotChanged -= OnSnapshotChanged;
        session = null;
    }

    private void OnSnapshotChanged(ClientSnapshot snapshot)
    {
        if (snapshot.Turn <= lastTurn) return;
        lastTurn = snapshot.Turn;
        if (SaveStore.AutosaveDue(snapshot.Turn, settings.Current.AutosaveTurns)) Run(AutosaveAsync(snapshot.Turn));
    }

    private int? deferredAutosave;

    private async Task AutosaveAsync(int turn)
    {
        long seq = session?.Snapshot.Seq ?? 0;
        var text = await ExportAsync(quiet: true);
        if (text == null)
        {
            deferredAutosave = turn;
            return;
        }
        var result = store.Autosave(text, turn, MaxNumberedAutosaves);
        if (result.Ok) progress.MarkSaved(seq);
        Say(result.Ok ? $"Autosaved turn {turn.ToString(CultureInfo.InvariantCulture)}." : "Autosave failed: " + result.Error, notify: !result.Ok);
    }

    /// <summary>C099: the app is going to the background (Android may kill it there): writes the autosave now, whatever the turn. A session that cannot be saved (a joined game) is left alone.</summary>
    public Task AutosaveNowAsync() => session == null ? Task.CompletedTask : AutosaveAsync(session.Snapshot.Turn);

    /// <summary>How many numbered autosaves (one per autosaved turn) are kept besides the latest "Autosave".</summary>
    public const int MaxNumberedAutosaves = 10;

    private async Task<string?> ExportAsync(bool quiet = false)
    {
        if (session == null) return null;
        var text = await session.ExportSaveAsync();
        if (text == null && !quiet) Say("This game cannot be saved from here.");
        return text;
    }

    public async Task<bool> SaveAsync(string name)
    {
        if (SaveStore.ValidateName(name) is { } invalid)
        {
            Say(invalid);
            return false;
        }
        long seq = session?.Snapshot.Seq ?? 0;
        var text = await ExportAsync();
        if (text == null) return false;
        var result = store.Write(name, text);
        if (result.Ok) progress.MarkSaved(seq);
        Say(result.Ok ? $"Saved \"{name}\"." : "Save failed: " + result.Error);
        RefreshList();
        return result.Ok;
    }

    public async Task<bool> LoadAsync(string name)
    {
        var read = store.TryRead(name, out string text);
        if (!read.Ok)
        {
            Say($"Cannot load \"{name}\": {read.Error}");
            return false;
        }
        IGameSession loaded;
        try
        {
            loaded = loader(text);
        }
        catch (InvalidDataException e)
        {
            Say($"Cannot load \"{name}\": {e.Message}");
            return false;
        }
        await loaded.ResyncAsync(); // the snapshot the screen is about to draw is current
        Attach(loaded);
        onLoaded(loaded);
        CloseDialog();
        Say($"Loaded \"{name}\" (turn {loaded.Snapshot.Turn.ToString(CultureInfo.InvariantCulture)}).");
        return true;
    }

    /// <summary>Loads the newest readable save (autosaves count).</summary>
    public Task<bool> ResumeLastAsync()
    {
        var latest = store.Latest();
        if (latest == null)
        {
            Say("There is no save to resume.");
            return Task.FromResult(false);
        }
        return LoadAsync(latest.Name);
    }

    public void Delete(string name)
    {
        var result = store.Delete(name);
        Say(result.Ok ? $"Deleted \"{name}\"." : "Delete failed: " + result.Error);
        pendingDelete = null;
        RefreshList();
    }

    public override void _Input(InputEvent @event)
    {
        // Escape closes the dialog before the game screen can open its menu behind it
        if (dialog == null || @event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        CloseDialog();
        GetViewport().SetInputAsHandled();
    }

    // Dialog

    public void OpenDialog()
    {
        if (dialog != null) return;
        dialog = new Control { Name = "SaveDialog", MouseFilter = MouseFilterEnum.Stop, Theme = theme.Theme };
        dialog.SetAnchorsPreset(LayoutPreset.FullRect);
        var scrim = new ColorRect { Name = "Scrim", Color = ProceduralAssets.ToColor(UiTokens.Scrim) with { A = UiTokens.ScrimPercent / 100f }, MouseFilter = MouseFilterEnum.Ignore };
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        dialog.AddChild(scrim);
        dialogPanel = new PanelContainer { Name = "SaveFrame", ThemeTypeVariation = UiVariants.PanelInset };
        // T321: a frame whose side margins grow while the scale changes can clamp the panel above the wanted size; re-apply it when the minimum shrinks again
        dialogPanel.MinimumSizeChanged += Layout;
        dialog.AddChild(dialogPanel);
        frame = new ScreenFrame(theme, "Saves");
        frame.CloseRequested += CloseDialog;
        dialogPanel.AddChild(frame);

        var content = new HBoxContainer { Name = "SaveContent" };
        content.FitConstant(theme, "separation", UiTokens.S3);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.7f };
        left.FitConstant(theme, "separation", UiTokens.S2);
        search = new LineEdit { Name = "SaveSearch", PlaceholderText = "Search saves".Tr(), ClearButtonEnabled = true };
        search.TextChanged += _ => ApplyFilter();
        left.AddChild(search);
        compact = IsCompact();
        table = BuildTable();
        scroller = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroller.AddChild(table);
        left.AddChild(scroller);
        empty = new EmptyState(theme, AssetKey.Of(RulesetAssetKeys.UiIcon, "Mods"), "No saves yet.") { Name = "SaveEmpty", SizeFlagsVertical = SizeFlags.ExpandFill };
        left.AddChild(empty);
        content.AddChild(left);
        content.AddChild(BuildDetails());
        frame.SetContent(content);

        var inGame = session != null;
        resumeButton = new Button { Text = "Resume last".Tr() };
        resumeButton.Pressed += () => Run(ResumeLastAsync());
        deleteButton = new Button { Text = "Delete".Tr() };
        deleteButton.Pressed += ArmDelete;
        confirmDelete = new Button { Text = "Confirm delete".Tr(), Visible = false };
        confirmDelete.Pressed += () => { if (pendingDelete is { } name) Delete(name); };
        loadButton = new Button { Text = "Load".Tr() };
        loadButton.Pressed += () => { if (Selected is { } name) Run(LoadAsync(name)); else Say("Select a save first."); };
        saveButton = new Button { Text = "Save".Tr(), Visible = inGame, Disabled = !inGame };
        saveButton.Pressed += () => Run(SaveAsync(NameField?.Text ?? ""));
        frame.SetFooter([(resumeButton, ChoiceRole.Ghost), (deleteButton, ChoiceRole.Danger), (confirmDelete, ChoiceRole.Danger), (loadButton, ChoiceRole.Secondary), (saveButton, ChoiceRole.Primary)]);
        if (NameField != null && inGame) NameField.Text = $"Game-{(session?.Snapshot.Turn ?? 0).ToString(CultureInfo.InvariantCulture)}";
        AddChild(dialog);
        RefreshList();
        Layout();
    }

    /// <summary>T321: the columns of the save list at the current breakpoint (three on a compact window, five otherwise).</summary>
    private DataTable BuildTable()
    {
        var built = new DataTable(compact
            ? [new DataColumn("Name"), new DataColumn("Turn", true), new DataColumn("Civilization")]
            : [new DataColumn("Name"), new DataColumn("Turn", true), new DataColumn("Civilization"), new DataColumn("Ruleset"), new DataColumn("Date")], selectable: true) { Name = "SaveTable" };
        built.RowTone = row => row < shown.Count && shown[row].Info == null ? ProceduralAssets.ToColor(UiTokens.Warning) : null;
        built.CellLeading = (row, col) => col == 2 && row >= 0 && row < shown.Count ? SaveCrest(shown[row], 16) : null;
        built.RowSelected += OnRowSelected;
        built.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return built;
    }

    /// <summary>T321: replaces the open table when a live resize crosses the Compact breakpoint; the search, selection and details stay.</summary>
    private void RebuildTable()
    {
        if (table == null || scroller == null) return;
        var previous = table;
        table = BuildTable();
        scroller.RemoveChild(previous);
        previous.QueueFree();
        scroller.AddChild(table);
        ApplyFilter();
    }

    private Control BuildDetails()
    {
        bool inGame = session != null;
        var card = new PanelContainer { Name = "SaveDetails", ThemeTypeVariation = UiVariants.PanelCard, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1f };
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        column.FitConstant(theme, "separation", UiTokens.S2);
        detailsEmpty = new EmptyState(theme, AssetKey.Of(RulesetAssetKeys.UiIcon, "Search"), "Select a save to inspect or load.", wrap: true) { Name = "SaveDetailsEmpty", SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(detailsEmpty);
        detailsHeader = new HBoxContainer { Name = "SaveDetailsHeader" };
        detailsHeader.FitConstant(theme, "separation", UiTokens.S2);
        detailsCrestSlot = new HBoxContainer { Name = "SaveDetailsCrest" };
        detailsTitle = new Label { Name = "SaveDetailsTitle", ThemeTypeVariation = UiVariants.LabelHeading, SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        detailsHeader.AddChild(detailsCrestSlot);
        detailsHeader.AddChild(detailsTitle);
        detailsBody = new Label { Name = "SaveDetailsBody", ThemeTypeVariation = UiVariants.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 6, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis }.FitMin(theme, 96);
        detailsWarning = new Label { Name = "SaveDetailsWarning", AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 4, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, Visible = false }.FitMin(theme, 96);
        detailsWarning.AddThemeColorOverride("font_color", ProceduralAssets.ToColor(UiTokens.Warning));
        column.AddChild(detailsHeader);
        column.AddChild(detailsBody);
        column.AddChild(detailsWarning);
        saveNameLabel = new Label { Text = "Save name".Tr(), ThemeTypeVariation = UiVariants.LabelOverline, Visible = inGame };
        NameField = new LineEdit { Name = "SaveName", PlaceholderText = "Save name".Tr(), Visible = inGame };
        if (inGame)
        {
            column.AddChild(new Control().FitMin(theme, 0, UiTokens.S2));
            column.AddChild(saveNameLabel);
            column.AddChild(NameField);
        }
        dialogMessage = new Label { Name = "SaveDialogMessage", ThemeTypeVariation = UiVariants.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 4, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis }.FitMin(theme, 96);
        column.AddChild(dialogMessage);
        var scrolled = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill }; // the card never makes the dialog taller than the window
        scrolled.AddChild(column);
        card.AddChild(scrolled);
        ShowDetails(null);
        return card;
    }

    public void CloseDialog()
    {
        dialog?.QueueFree();
        dialog = null;
        dialogPanel = null;
        frame = null;
        resumeButton = deleteButton = confirmDelete = loadButton = saveButton = null;
        table = null;
        search = null;
        scroller = null;
        empty = null;
        detailsHeader = null;
        detailsCrestSlot = null;
        detailsTitle = detailsBody = detailsWarning = saveNameLabel = dialogMessage = null;
        detailsEmpty = null;
        pendingDelete = null;
        selectedName = null;
        NameField = null;
        entries = [];
        shown = [];
    }

    private string? Selected => selectedName != null && shown.Any(it => it.Name == selectedName) ? selectedName : null;

    private void ArmDelete()
    {
        if (Selected is not { } name)
        {
            Say("Select a save first.");
            return;
        }
        pendingDelete = name;
        if (confirmDelete != null) confirmDelete.Visible = true;
        Say($"Press Confirm delete to delete \"{name}\".");
    }

    /// <summary>Re-reads the save folder into the open dialog (also after something outside changed the folder).</summary>
    public void RefreshList()
    {
        if (dialog == null) return;
        entries = store.List();
        DisarmDelete();
        ApplyFilter();
    }

    private void DisarmDelete()
    {
        pendingDelete = null;
        if (confirmDelete != null) confirmDelete.Visible = false;
    }

    private void ApplyFilter()
    {
        if (table == null || search == null) return;
        DisarmDelete();
        string query = search.Text.Trim();
        shown = entries.Where(it => query.Length == 0 || it.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        table.SetRows(shown.Select(Row).ToList());
        int index = shown.ToList().FindIndex(it => it.Name == selectedName);
        if (index < 0) selectedName = null;
        table.Select(index, notify: false);
        if (frame != null) frame.TitleLabel.Text = $"{(session != null ? "Saves" : "Load game").Tr()} ({entries.Count.ToString(CultureInfo.InvariantCulture)})";
        scroller!.Visible = shown.Count > 0;
        empty!.Visible = shown.Count == 0;
        empty.SetMessage(entries.Count == 0 ? "No saves yet." : "No save matches the search.");
        ShowDetails(index < 0 ? null : shown[index]);
        UpdateActions();
    }

    private string[] Row(SaveEntry entry)
    {
        if (entry.Info is not { } info) return compact ? [entry.Name, "-", "Damaged".Tr()] : [entry.Name, "-", "Damaged".Tr(), "-", ShortDate(entry)];
        string turn = info.Turn.ToString(CultureInfo.InvariantCulture);
        return compact ? [entry.Name, turn, info.Civ] : [entry.Name, turn, info.Civ, info.Ruleset, ShortDate(entry)];
    }

    private static string ShortDate(SaveEntry entry) => entry.ModifiedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void OnRowSelected(int row)
    {
        pendingDelete = null;
        if (confirmDelete != null) confirmDelete.Visible = false;
        if (row < 0 || row >= shown.Count) return;
        var entry = shown[row];
        selectedName = entry.Name;
        if (NameField != null && entry.Info != null) NameField.Text = entry.Name;
        ShowDetails(entry);
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool hasSelection = Selected != null;
        if (loadButton != null)
        {
            loadButton.Disabled = !hasSelection;
            loadButton.TooltipText = hasSelection ? "" : "Select a save first.".Tr();
        }
        if (deleteButton != null)
        {
            deleteButton.Disabled = !hasSelection;
            deleteButton.TooltipText = hasSelection ? "" : "Select a save first.".Tr();
        }
        if (resumeButton != null)
        {
            var latest = store.Latest();
            resumeButton.Disabled = latest == null;
            resumeButton.TooltipText = latest?.Info is { } latestInfo
                ? $"{"Resume".Tr()} \"{latest.Name}\" ({"Turn".Tr()} {latestInfo.Turn.ToString(CultureInfo.InvariantCulture)})"
                : "No saves yet.".Tr();
        }
    }

    private IconBadge SaveCrest(SaveEntry entry, int size)
    {
        if (entry.Info is not { } info)
        {
            return new IconBadge(
                theme,
                AssetKey.Of(RulesetAssetKeys.UiIcon, "ExclamationMark"),
                ProceduralAssets.ToColor(UiTokens.Surface3),
                ProceduralAssets.ToColor(UiTokens.Warning),
                size);
        }
        var nation = LookupNation(info.Civ, info.Ruleset);
        var fill = nation != null ? ToGodotColor(nation.GetOuterColor()) : ProceduralAssets.ToColor(UiTokens.Surface3);
        var glyph = nation != null ? ToGodotColor(nation.GetInnerColor()) : ProceduralAssets.ToColor(UiTokens.TextPrimary);
        return new IconBadge(theme, AssetKey.Of(RulesetAssetKeys.NationIcon, info.Civ), fill, glyph, size);
    }

    private RulesetNation? LookupNation(string civName, string rulesetName)
    {
        if (session is { } active && string.Equals(active.Ruleset.Name, rulesetName, StringComparison.Ordinal) &&
            active.Ruleset.Nations.TryGetValue(civName, out var activeMatch))
        {
            return activeMatch;
        }
        if (RulesetCache.ContainsKey(rulesetName) && RulesetCache.Get(rulesetName) is { } cached && cached.Nations.TryGetValue(civName, out var match)) return match;
        return null;
    }

    private static Color ToGodotColor(EngineColor c) => new(c.R, c.G, c.B, 1f);

    private void ShowDetails(SaveEntry? entry)
    {
        if (detailsTitle == null || detailsBody == null || detailsWarning == null || detailsHeader == null || detailsEmpty == null || detailsCrestSlot == null) return;
        foreach (var child in detailsCrestSlot.GetChildren())
        {
            detailsCrestSlot.RemoveChild(child);
            child.QueueFree();
        }
        bool hasEntry = entry != null;
        detailsEmpty.Visible = !hasEntry;
        detailsEmpty.SetMessage(entries.Count == 0 ? "No saves yet." : "Select a save to inspect or load.");
        detailsHeader.Visible = hasEntry;
        detailsBody.Visible = entry?.Info != null;
        if (entry == null)
        {
            detailsWarning.Visible = false;
            return;
        }
        detailsCrestSlot.AddChild(SaveCrest(entry, 36));
        detailsTitle.Text = entry.Name;
        detailsWarning.Visible = entry.Info == null;
        detailsWarning.Text = entry.Problem is { } problem ? $"{"Damaged".Tr()}: {problem}" : "";
        if (entry.Info is not { } info)
        {
            detailsBody.Text = "";
            return;
        }
        string mods = info.Mods.Count == 0 ? "" : "\n" + string.Join(", ", info.Mods);
        string when = entry.ModifiedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        detailsBody.Text = $"{"Turn".Tr()} {info.Turn.ToString(CultureInfo.InvariantCulture)}\n{info.Civ}\n{info.Ruleset}{mods}\n{when}";
    }

    private void Say(string text, bool notify = true)
    {
        Message = text.TrText();
        if (dialogMessage != null) dialogMessage.Text = Message;
        else if (notify) StatusChanged?.Invoke(Message);
    }

    private async void Run(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            GD.PushError("Save action failed: " + e);
            Say("Save action failed: " + e.Message);
        }
    }
}
