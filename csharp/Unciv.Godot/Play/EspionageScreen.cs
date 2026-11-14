using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;
using Unciv.Queries.Empire;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C051, redesigned in C074: the espionage screen (Kotlin <c>EspionageScreen</c>) in a <see cref="ScreenFrame"/>. Above, a <see cref="DataTable"/> of the viewer's spies (rank, location, action) whose
/// trailing cell holds the row's Move button and, when the engine allows a coup, a coup button ("Coup 35%", the whole sentence as its tooltip); below, a table of the places a spy can be (the
/// hideout, the cities the engine offers, with the viewer's spies in them). Pressing Move on a spy turns the places it may go to into "Move here" buttons (the button now says Cancel); "Assign spies"
/// in the footer asks the owner to open the multi-spy picker (<see cref="DecisionPickerModel.MoveSpies"/>). Every list is <see cref="EspionageModel"/> over the engine's
/// <see cref="SpyDestinationsQuery"/> answer, which only ever describes the viewer's own spies. The screen's only change to the world is the spy command it sends through the <see cref="IGameSession"/>.
/// </summary>
public partial class EspionageScreen : PanelContainer
{
    private readonly IGameSession session;
    private readonly GameQueries queries;
    private readonly UiTheme theme;
    private ScreenFrame frame = null!; // JUSTIFY: C074 assigned by BuildFrame in the constructor
    private Label message = null!; // JUSTIFY: C074 assigned by BuildFrame in the constructor
    private ScrollContainer scroll = null!; // JUSTIFY: C074 assigned by BuildFrame in the constructor
    private EspionageBoard board = null!; // JUSTIFY: T415 assigned by BuildFrame in the constructor
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
        if (frame != null)
        {
            RemoveChild(frame); // at once: a queued free would leave the old frame in the layout until the frame ends
            frame.QueueFree();
        }
        frame = new ScreenFrame(theme, "Espionage", closeLabel: "Close espionage");
        frame.CloseRequested += Close;
        AddChild(frame);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", theme.Px(UiTokens.S2));
        message = new Label { Name = "Message", ThemeTypeVariation = UiVariants.LabelSecondary, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = messageText, Visible = messageText.Length > 0 };
        column.AddChild(message);
        scroll = new ScrollContainer { Name = "EspionageScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, SizeFlagsVertical = SizeFlags.ExpandFill };
        board = new EspionageBoard(theme, command => Fire(Send(command)), SetMessage) { SelectedSpy = keepSelected };
        scroll.AddChild(board.Root);
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
