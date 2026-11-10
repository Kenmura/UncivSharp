using System.Security.Cryptography.X509Certificates;
using Godot;
using Unciv.Client.Net.Session;
using Unciv.Client.Net.Transport;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Ui;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Persistence;
using Unciv.Net.Turns;
using Unciv.Server;
using Unciv.Server.Transport;
using Unciv.Utils;

namespace Unciv.GodotHarness;

/// <summary>
/// T346: the player-visible reconnect path over real loopback TCP. A two-human simultaneous game is hosted in this process (a fixed clock, so no countdown depends on wall
/// time); the friend (Greece) joins and plays it, the host's own player (Babylon) is a second real client. Babylon's connection is killed: the friend's turn bar must name
/// Babylon as offline and count down the host's 120 s drop grace, and the restore must clear it. Then Greece's connection is killed: the reconnect overlay must show the
/// attempt countdown, the waiting line and a Rejoin button; pressing Rejoin dials again, and the overlay goes away once the mirror is back.
/// </summary>
public sealed class ReconnectScenario : Scenario
{
    public override string Name => "reconnect";

    public override bool Solo => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Does the turn bar make clear that the game waits for a player who is offline, and how long it will still wait?",
        "Is the reconnect overlay's status (attempt, next try) understandable without knowing the protocol?",
        "Is Rejoin obvious, and does the game come back by itself if the player does nothing?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session) => new Flow(host, session).Steps();

    private sealed class Flow
    {
        private const string Vanilla = "Civ V - Vanilla";
        private const long Clock = 5_000L;

        private readonly UiTheme theme;
        private readonly Control root;
        private readonly Ruleset ruleset;
        private readonly ClientIdentity hostIdentity = ClientIdentity.Generate();
        private readonly ClientIdentity friendIdentity = ClientIdentity.Generate();
        private readonly X509Certificate2 certificate = HostCertificate.Generate();
        private readonly string dir = Directory.CreateTempSubdirectory("unciv-reconnect-").FullName;
        private readonly TaskCompletionSource hostGate = new(TaskCreationOptions.RunContinuationsAsynchronously); // released to restore the host player
        private readonly TaskCompletionSource friendGate = new(TaskCreationOptions.RunContinuationsAsynchronously); // only Rejoin releases the friend's backoff

        private InProcessHost? host;
        private ClientSession? hostPlayer;
        private ClientSession? friend;
        private NetGameSession? net;
        private GameScreen? game;
        private ReconnectOverlay? overlay;
        private KillableTransport? hostTransport;
        private KillableTransport? friendTransport;
        private Task? starting;
        private Exception? failure;

        public Flow(Control host, GameSession session)
        {
            theme = new UiTheme(AssetRegistry.ForRuleset(session.Game.Ruleset));
            host.AddChild(new ColorRect { Color = new Color(0.12f, 0.14f, 0.2f), AnchorRight = 1, AnchorBottom = 1 });
            root = new Control { AnchorRight = 1, AnchorBottom = 1 };
            host.AddChild(root);
            ruleset = RulesetCache.GetVanillaRuleset();
        }

        private static Vector2 Centre(Func<Control?> find) => find()?.GetGlobalRect().GetCenter() ?? Vector2.Zero;

        private GameSetupInfo Setup() => new(
            new GameParameters
            {
                BaseRuleset = Vanilla,
                NumberOfCityStates = 0,
                VictoryTypes = ruleset.Victories.Keys.ToList(),
                Players = [new("Babylon", PlayerType.Human), new("Greece", PlayerType.Human), new("Rome", PlayerType.AI)],
            },
            new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny });

        private async Task StartAsync()
        {
            var setup = Setup();
            var gameInfo = GameStarter.StartNewGame(setup, 7L, Clock);
            gameInfo.StartSimultaneousTurns();
            host = InProcessHost.Start(gameInfo, setup, 7L, Clock, dir, "Babylon",
                new Dictionary<string, SlotBinding>
                {
                    ["Babylon"] = new SlotBinding { PlayerKey = hostIdentity.PlayerKey, DisplayName = "Host" },
                    ["Greece"] = new SlotBinding { PlayerKey = friendIdentity.PlayerKey, DisplayName = "Friend" },
                },
                new InProcessHostOptions { Clock = new FixedClock(Clock), AnnounceRoster = true, TickInterval = null }); // no background tick: every change here is explicit
            host.Listen(certificate, 0);

            hostPlayer = new ClientSession(HostConnectAsync, Options(hostIdentity, "Host", HostDelay));
            if (!(await hostPlayer.JoinAsync()).Joined) throw new InvalidOperationException("The host player could not join its own game");
            friend = new ClientSession(FriendConnectAsync, Options(friendIdentity, "Friend", FriendDelay));
            if (!(await friend.JoinAsync()).Joined) throw new InvalidOperationException("The friend could not join");

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (friend.Game.Snapshot == null) await Task.Delay(20, limit.Token);

            net = new NetGameSession(friend, ruleset);
            game = new GameScreen { Name = "ReconnectGame", NowMs = () => Clock };
            root.AddChild(game);
            game.Start(net, AssetRegistry.ForRuleset(ruleset));
            overlay = new ReconnectOverlay(theme) { NowMs = () => Clock };
            overlay.RejoinRequested += () => net.RejoinNow(); // what App does while the session still retries
            root.AddChild(overlay);
            overlay.Attach(net);
        }

        private ClientSessionOptions Options(ClientIdentity identity, string name, Func<int, CancellationToken, Task> delay) => new()
        {
            Identity = identity,
            DisplayName = name,
            RulesetHash = "",
            ExpectedFingerprint = host.NotNull().Fingerprint,
            GameId = host.GameId,
            ReconnectDelay = delay,
        };

        private async Task<ITransport> HostConnectAsync(CancellationToken token)
        {
            var inner = await TcpTlsClientTransport.ConnectAsync("127.0.0.1", host.NotNull().Port.NotNull(), expectedFingerprint: host.Fingerprint, cancellationToken: token);
            return hostTransport = new KillableTransport(inner);
        }

        private async Task<ITransport> FriendConnectAsync(CancellationToken token)
        {
            var inner = await TcpTlsClientTransport.ConnectAsync("127.0.0.1", host.NotNull().Port.NotNull(), expectedFingerprint: host.Fingerprint, cancellationToken: token);
            return friendTransport = new KillableTransport(inner);
        }

        /// <summary>No timer in a scenario: a backoff is a manual gate. Cancelling it is what Rejoin does, so the loop dials at once.</summary>
        private static async Task Gate(Task release, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(release, cancelled.Task) == cancelled.Task) throw new OperationCanceledException(token);
        }

        private Task HostDelay(int attempt, CancellationToken token) => Gate(hostGate.Task, token);

        private Task FriendDelay(int attempt, CancellationToken token) => Gate(friendGate.Task, token);

        private void Start()
        {
            starting = StartAsync();
            _ = starting.ContinueWith(task => failure = task.Exception?.GetBaseException(), TaskScheduler.Default);
        }

        public IReadOnlyList<ScenarioStep> Steps() =>
            Starting().Concat(HostDrops()).Concat(FriendDrops()).Concat(CleanUp()).ToList();

        private ScenarioStep[] Starting() =>
        [
            new Act("host a two-human simultaneous game and join it twice over loopback TCP", Start),
            new WaitUntil("the friend's game screen is up", () => failure != null || (starting is { IsCompleted: true } && friend?.Game.Snapshot != null && game != null), 6000),
            new AssertQuery("both real clients are in the game the friend plays", () =>
                failure == null && friend.NotNull().Game.Snapshot.NotNull().You == "Greece" && hostPlayer.NotNull().Game.Snapshot.NotNull().You == "Babylon"),
            new WaitUntil("the roster of both humans reaches the friend", () => game.NotNull().TurnBar.HeadlineText == "Waiting for Host, Friend", 1200),
        ];

        private ScenarioStep[] HostDrops() =>
        [
            new Act("the host player's connection is killed", () => _ = hostTransport.NotNull().Kill()),
            new WaitUntil("the friend sees the host offline with the drop grace", () =>
                game.NotNull().TurnBar.HeadlineText.Contains("Host (offline, 2:00 to reconnect)", StringComparison.Ordinal), 1200),
            new AssertQuery("the grace countdown comes from the host's 120 s drop window, not from the client's clock", () =>
                game.NotNull().TurnBar.HeadlineText == "Waiting for Host (offline, 2:00 to reconnect), Friend" && game.TurnBar.Data.NotNull().GraceCountdown == "2:00"),
            new Capture("waiting-grace", "The friend's turn bar while the host player's connection is down: 'Waiting for Host (offline, 2:00 to reconnect), Friend'. The countdown is the host's 120 s drop grace, so the friend knows the game will not wait forever."),
            new Act("the host player's connection comes back", () => hostGate.TrySetResult()),
            new WaitUntil("the friend's bar no longer counts the host down", () =>
                game.NotNull().TurnBar.HeadlineText == "Waiting for Host, Friend", 1800),
            new AssertQuery("the restored seat is waited for as connected again", () => game.NotNull().TurnBar.Data.NotNull().GraceCountdown == null && game.TurnBar.Data.NotNull().Rows.Any(it => it is { CivId: "Babylon", Connected: true })),
        ];

        private ScenarioStep[] FriendDrops() =>
        [
            new Act("the friend's own connection is killed", () => _ = friendTransport.NotNull().Kill()),
            new WaitUntil("the reconnect overlay opens", () => overlay.NotNull().Visible && net.NotNull().Connection == ClientSessionState.Reconnecting, 1200),
            new AssertQuery("the overlay names the outage, the attempt, the waiting line, and offers Rejoin (not Leave)", () =>
                overlay.NotNull().Title == "Connection lost" && overlay.Detail.Contains("Attempt 1 of 5", StringComparison.Ordinal) && overlay.Detail.Contains("next try in 1 s", StringComparison.Ordinal)
                && overlay.Detail.Contains("Waiting for Host, Friend", StringComparison.Ordinal) && overlay.RejoinButton.Visible && !overlay.LeaveButton.Visible && !overlay.IsFinal),
            new Capture("reconnect-overlay", "The friend's connection dropped: the overlay says it is reconnecting with the same key, shows the attempt and the 1 s backoff, repeats whom the game waits for, and offers Rejoin now (or Leave)."),
            new Click(() => Centre(() => overlay.NotNull().RejoinButton)),
            new WaitUntil("Rejoin dials at once and the game is back", () => net.NotNull().Connection == ClientSessionState.InGame && !overlay.NotNull().Visible, 1800),
            new AssertQuery("the seat and mirror came back with no fresh join", () =>
                friend.NotNull().Game.Snapshot.NotNull().You == "Greece" && overlay.NotNull().RejoinButton.Visible == false && game.NotNull().Visible),
            new Capture("restored", "After Rejoin: the overlay is gone and the friend plays the same seat, mirror and turn bar again. Nothing asked for the address or the password."),
        ];

        private async Task CleanUpAsync()
        {
            if (overlay != null) overlay.QueueFree();
            if (game != null) game.QueueFree();
            if (net != null) await net.DisposeAsync();
            if (friend != null) await friend.DisposeAsync();
            if (hostPlayer != null) await hostPlayer.DisposeAsync();
            if (host != null) await host.DisposeAsync();
            certificate.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        private ScenarioStep[] CleanUp() =>
        [
            new Act("clean up", () => _ = CleanUpAsync()),
        ];

        /// <summary>A transport whose connection the scenario can kill without ending the session's reconnect path.</summary>
        private sealed class KillableTransport(ITransport inner) : ITransport
        {
            public bool IsOpen => inner.IsOpen;

            public ValueTask SendAsync(string frame, CancellationToken cancellationToken = default) => inner.SendAsync(frame, cancellationToken);

            public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken = default) => inner.ReceiveAsync(cancellationToken);

            public ValueTask CloseAsync() => inner.CloseAsync();

            public ValueTask DisposeAsync() => inner.DisposeAsync();

            public ValueTask Kill() => inner.CloseAsync();
        }

        /// <summary>The host's clock: the scenario's fixed time, so the grace deadline and every countdown are stable.</summary>
        private sealed class FixedClock(long now) : IClock
        {
            public long NowMillis => now;
        }
    }
}
