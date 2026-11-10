using Godot;
using Unciv.Client.Net.Session;
using Unciv.GodotClient.Setup;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;

namespace Unciv.GodotClient.Play;

/// <summary>
/// C006: the cover over a networked game while the connection is down. It follows <see cref="NetGameSession.ConnectionChanged"/> through <see cref="JoinModel.Connection"/>: invisible while
/// connected; "Connection lost" while the session retries with the same key (commands are kept and resent); "The host went away" after the host's closing notice; and a final
/// "Disconnected" with only Leave once the session gave up. While visible it swallows the mouse so nothing under it can be clicked.
/// </summary>
public partial class ReconnectOverlay : Control
{
    private readonly Label titleLabel = new() { Name = "ReconnectTitle", HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Label detailLabel = new() { Name = "ReconnectDetail", AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center };
    private NetGameSession? net;
    private readonly int mainThread = System.Environment.CurrentManagedThreadId;

    public ReconnectOverlay(UiTheme theme)
    {
        Name = "ReconnectOverlay";
        AnchorRight = 1;
        AnchorBottom = 1;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new ColorRect { Color = theme.Panel.BgColor with { A = 0.85f }, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var centre = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(centre);
        var panel = new PanelContainer();
        centre.AddChild(panel);
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        panel.AddChild(column);
        column.AddChild(titleLabel);
        column.AddChild(detailLabel);
        LeaveButton = new Button { Text = "Leave game".Tr(), Name = "ReconnectLeaveButton", Visible = false };
        LeaveButton.Pressed += () => LeaveRequested?.Invoke();
        column.AddChild(LeaveButton);
        ContinueButton = new Button { Text = "Continue playing".Tr(), Name = "ReconnectContinueButton", Visible = false };
        ContinueButton.ThemeTypeVariation = UiVariants.ButtonPrimary;
        ContinueButton.Pressed += () =>
        {
            Visible = false; // the warning is only advice; the game under it stays playable
            WarningAcknowledged?.Invoke();
        };
        column.AddChild(ContinueButton);
    }

    public Button LeaveButton { get; }

    /// <summary>T340: the button of a playable warning that puts the notice aside (a final one leaves instead).</summary>
    public Button ContinueButton { get; }

    /// <summary>T340: the player put aside a playable warning; the owner clears it so it does not come back on the next connection change.</summary>
    public event Action? WarningAcknowledged;

    public string Title => titleLabel.Text;

    public string Detail => detailLabel.Text;

    /// <summary>True once the session gave up: only Leave remains.</summary>
    public bool IsFinal => LeaveButton.Visible;

    /// <summary>Raised when the player leaves a game whose connection is gone; the owner ends the session and returns to the menu.</summary>
    public event Action? LeaveRequested;

    /// <summary>Follows <paramref name="session"/> from now on (and shows its current state).</summary>
    public void Attach(NetGameSession session)
    {
        if (net != null) net.ConnectionChanged -= Refresh;
        net = session;
        session.ConnectionChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree()
    {
        if (net != null) net.ConnectionChanged -= Refresh;
    }

    public void Refresh()
    {
        if (net == null) return;
        // NetGameSession raises its events on the thread that pumps the session; a session built without a synchronization context (scenarios, tools) would otherwise touch Godot nodes off the main thread.
        if (System.Environment.CurrentManagedThreadId != mainThread)
        {
            Callable.From(Refresh).CallDeferred();
            return;
        }
        Apply(JoinModel.Connection(net.Connection == ClientSessionState.InGame, net.HostClosing, net.EndedReason, net.MigrationWarning));
    }

    /// <summary>Shows <paramref name="view"/> (what <see cref="Refresh"/> does with the session's state; a scenario uses it to show each state without a live connection).</summary>
    public void Apply(ConnectionView view)
    {
        Visible = view.Visible;
        titleLabel.Text = view.Title.Tr();
        detailLabel.Text = view.Detail.Tr();
        LeaveButton.Visible = view.Final;
        ContinueButton.Visible = view.Dismissible;
    }
}
