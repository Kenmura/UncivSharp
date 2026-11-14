using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;
using Unciv.Queries.Empire;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C051, redesigned in C074 and T415: the espionage screen (Kotlin <c>EspionageScreen</c>) in a <see cref="ScreenFrame"/>, backed by the shared
/// <see cref="EspionageBoard"/> component. It shows the viewer's spies with their rank, location, action, Move button, and conditional coup button;
/// pressing Move expands valid inline destination rows under the moving spy (Move becomes Cancel). Every list comes from <see cref="EspionageModel"/>
/// over <see cref="SpyDestinationsQuery"/> and sends only spy commands through <see cref="IGameSession"/>.
/// </summary>
public partial class EspionageScreen : PanelContainer
{
    private readonly IGameSession session;
    private readonly GameQueries queries;
    private readonly UiTheme theme;
    private ReadyRef<ScreenFrame> frameRef;
    private ReadyRef<Label> messageRef;
    private ReadyRef<ScrollContainer> scrollRef;
    private ReadyRef<EspionageBoard> boardRef;
    private ScreenFrame frame => frameRef.Value;
    private Label message => messageRef.Value;
    private ScrollContainer scroll => scrollRef.Value;
    private EspionageBoard board => boardRef.Value;
    private SpyDestinationsResult? result;
    private string? notice;
    private string messageText = "";
    private int generation;

    public EspionageScreen(IGameSession gameSession, UiTheme uiTheme)
    {
        session = gameSession;
        queries = new GameQueries(gameSession);
        theme = uiTheme;
        Name = "EspionagePanel";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        Theme = uiTheme.Theme;
        BuildFrame();
    }

    public override void _EnterTree() => theme.LayoutChanged += OnLayoutChanged;

    public override void _ExitTree() => theme.LayoutChanged -= OnLayoutChanged;

    /// <summary>Sizes are fixed when a view is built, so a change of scale or breakpoint builds the frame again and redraws the answer it has.</summary>
    private void OnLayoutChanged()
    {
        BuildFrame();
        if (Visible) Rebuild();
    }

    private void BuildFrame()
    {
        string? keepSelected = board?.SelectedSpy;
        if (frameRef.OrNull is { } oldFrame)
        {
            RemoveChild(oldFrame); // at once: a queued free would leave the old frame in the layout until the frame ends
            oldFrame.QueueFree();
        }
        frameRef.Set(new ScreenFrame(theme, "Espionage", closeLabel: "Close espionage"));
        frame.CloseRequested += Close;
        AddChild(frame);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", theme.Px(UiTokens.S2));
        messageRef.Set(new Label { Name = "Message", ThemeTypeVariation = UiVariants.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = messageText, Visible = messageText.Length > 0 });
        column.AddChild(message);
        scrollRef.Set(new ScrollContainer { Name = "EspionageScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, SizeFlagsVertical = SizeFlags.ExpandFill });
        var nextBoard = new EspionageBoard(theme, command => Fire(Send(command)), SetMessage) { SelectedSpy = keepSelected };
        boardRef.Set(nextBoard);
        scroll.AddChild(nextBoard.Root);
        column.AddChild(scroll);
        frame.SetContent(column);
    }

    /// <summary>The spy whose Move button was pressed (the place buttons then say "Move here"), or null.</summary>
    public string? SelectedSpy => board.SelectedSpy;

    /// <summary>The page as drawn, null while closed or when the game has no espionage.</summary>
    public EspionageSpec? Spec => board.Spec;

    /// <summary>The text shown instead of the tables (espionage off), or null.</summary>
    public string? Notice => notice;

    public string MessageText => messageText;

    /// <summary>The frame of the screen (title, close button).</summary>
    public ScreenFrame Frame => frame;

    /// <summary>Scrolls the tables so the control is inside the visible part (a click needs it there).</summary>
    public void EnsureVisible(Control control) => scroll.EnsureControlVisible(control);

    public Button? MoveButton(string spy) => board.MoveButton(spy);

    public Button? CoupButton(string spy) => board.CoupButton(spy);

    /// <summary>The "Move here" button of a place (the city id, or empty for the hideout), present only while a spy is selected and the engine offers it that place.</summary>
    public Button? HereButton(string cityId) => board.HereButton(cityId);

    /// <summary>Raised after the engine accepted a command made here, so the owner refreshes the world.</summary>
    public event Action? CommandAccepted;

    public event Action? Closed;

    public async Task OpenAsync()
    {
        Visible = true;
        board.SelectedSpy = null;
        await RefreshAsync();
    }

    public void Close()
    {
        generation++;
        Visible = false;
        Closed?.Invoke();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (Visible && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Asks the engine again and redraws.</summary>
    public async Task RefreshAsync()
    {
        int ticket = ++generation;
        var answer = await queries.SpyDestinations();
        if (ticket != generation) return; // closed or refreshed meanwhile
        result = answer;
        Rebuild();
    }

    private void Rebuild()
    {
        SetMessage("");
        notice = board.Render(result);
    }

    private async Task Send(GameCommand command)
    {
        var sent = await session.SubmitAsync(command);
        if (!sent.Accepted)
        {
            await RefreshAsync();
            SetMessage("Refused: ".Tr() + sent.Message);
            return;
        }
        CommandAccepted?.Invoke();
        await RefreshAsync();
    }

    private async void Fire(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            GD.PushError("Session call failed: " + e);
            SetMessage("Connection problem: " + e.Message);
        }
    }

    private void SetMessage(string text)
    {
        messageText = text;
        message.Text = text;
        message.Visible = text.Length > 0;
    }
}
