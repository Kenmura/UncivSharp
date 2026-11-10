using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Godot;
using Unciv.Client.Net.Hosting;
using Unciv.Client.Net.Session;
using Unciv.Client.Net.Transport;
using Unciv.Commands;
using Unciv.Commands.Turn;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Setup;
using Unciv.GodotClient.Ui;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Persistence;
using Unciv.Net.Standby;
using Unciv.Net.Turns;
using Unciv.Server;
using Unciv.Server.Transport;
using Unciv.Utils;
using RulesetClass = Unciv.Models.Ruleset.Ruleset;

namespace Unciv.GodotHarness;

/// <summary>
/// T340: the two-human split brain, played for real. Two humans (the host Babylon and the backup host Greece) are in a running game; only the link between the machines is cut, and the host
/// machine keeps playing on its own copy while the backup host, alone in the electorate, takes over its copy after the loss window. Each side's screen is shown: the successor is warned that
/// the game may have split (a playable notice with the recovery), the old host keeps playing with no warning, and when the sides meet, a claim with the promotion proof fences the old host: its
/// screen says another host took over and its player rejoins the new host with the same key and seat. Time is a manual clock, so nothing waits on the wall clock.
/// </summary>
public sealed class SplitBrainScenario : Scenario
{
    public override string Name => "split-brain";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Does the successor's notice make clear that a second game may exist and that only one copy can continue?",
        "Can you tell from the old host's screen that its copy is out of date, and what to do to rejoin?",
        "Is the warning clear without blocking play for someone who already understands it?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session) => new Flow(host, session.Game.Ruleset).Steps();

    private sealed class Flow
    {
        private readonly UiTheme theme;
        private readonly Control root;
        private readonly RulesetClass ruleset;
        private readonly string dir = Directory.CreateTempSubdirectory("unciv-split-brain-scenario-").FullName;
        private readonly ClientIdentity babylon = ClientIdentity.Generate();
        private readonly ClientIdentity greece = ClientIdentity.Generate();
        private readonly X509Certificate2 hostCertificate = HostCertificate.Generate();
        private readonly X509Certificate2 friendCertificate = HostCertificate.Generate();
        private readonly ManualClock clock = new(5_000_000);
        private readonly int friendPort = FreePort();
        private Task<Rig>? setup;
        private Rig? rig;
        private GameScreen? screen;
        private ReconnectOverlay? overlay;
        private Task<JoinResult>? claim;
        private Task<(bool Joined, string? Code, bool HasSnapshot)>? recovery;

        public Flow(Control host, RulesetClass ruleset)
        {
            this.ruleset = ruleset;
            theme = new UiTheme(AssetRegistry.ForRuleset(ruleset));
            root = new Control { AnchorRight = 1, AnchorBottom = 1 };
            host.AddChild(root);
            setup = Task.Run(SetUpAsync);
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private sealed class Rig
        {
            public required InProcessHost Host { get; init; }
            public required ClientSession Friend { get; init; }
            public required NetGameSession HostScreen { get; init; }
            public required NetGameSession FriendScreen { get; init; }
            public required StandbyPromotion Promotion { get; init; }
            public required List<PromotionProof> Proofs { get; init; }
            public required Action<bool> Partition { get; init; }
        }

        private static async Task WaitAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 1500 && !condition(); attempt++) await Task.Delay(20);
            if (!condition()) throw new InvalidOperationException("condition never became true");
        }

        private async Task<Rig> SetUpAsync()
        {
            List<Player> players = [new("Babylon", PlayerType.Human), new("Greece", PlayerType.Human)];
            var setupInfo = new GameSetupInfo(
                new GameParameters { BaseRuleset = ruleset.Name, NumberOfCityStates = 0, VictoryTypes = ruleset.Victories.Keys.ToList(), Players = players },
                new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny });
            var game = GameStarter.StartNewGame(setupInfo, 23L, 1000L);
            game.StartSimultaneousTurns();
            var humans = new Dictionary<string, SlotBinding>
            {
                ["Babylon"] = new SlotBinding { PlayerKey = babylon.PlayerKey, DisplayName = "Host" },
                ["Greece"] = new SlotBinding { PlayerKey = greece.PlayerKey, DisplayName = "Friend" },
            };
            var options = new InProcessHostOptions { TickInterval = null, Clock = clock, Store = new StoreOptions { SnapshotEveryTurns = 2 } };
            var host = InProcessHost.Start(game, setupInfo, 23L, 1000L, Path.Combine(dir, "host"), "Babylon", humans, options);
            host.Listen(hostCertificate, 0);
            host.Host.Standby.Prober = new FixedProber(HostCertificate.GetFingerprint(friendCertificate));
            host.Host.Standby.Limits = new StandbyLimits { MinSubscribeMillis = 0 };

            var partitioned = false;
            var hostPlayer = new ClientSession(_ => Task.FromResult<ITransport>(host.ConnectLoopback()), new ClientSessionOptions
            {
                Identity = babylon,
                DisplayName = "Host",
                RulesetHash = "",
                AllowUnpinnedHost = true,
                HostAddress = "memory",
                GameId = host.GameId,
                Clock = clock,
                ReconnectDelay = (_, _) => Task.Delay(10),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
                HostSilenceTimeout = Timeout.InfiniteTimeSpan,
            });
            if (!(await hostPlayer.JoinAsync()).Joined) throw new InvalidOperationException("the host player could not join");
            await WaitAsync(() => hostPlayer.Game.Snapshot != null);

            var promotion = new StandbyPromotion(Path.Combine(dir, "friend"), greece, friendCertificate, options);
            var election = new SuccessorElection(promotion, friendCertificate, clock, new ElectionLimits(), listen: false);
            var proofs = new List<PromotionProof>();
            var friend = new ClientSession(_ => partitioned
                    ? Task.FromException<ITransport>(new IOException("partitioned"))
                    : Task.FromResult<ITransport>(host.ConnectLoopback()),
                new ClientSessionOptions
                {
                    Identity = greece,
                    DisplayName = "Friend",
                    RulesetHash = "",
                    AllowUnpinnedHost = true,
                    HostAddress = "memory",
                    GameId = host.GameId,
                    StandbyRoot = Path.Combine(dir, "friend"),
                    Clock = clock,
                    ReconnectDelay = (_, _) => Task.Delay(10),
                    KeepAliveInterval = Timeout.InfiniteTimeSpan,
                    HostSilenceTimeout = Timeout.InfiniteTimeSpan,
                    ElectionPoll = TimeSpan.FromMilliseconds(10),
                    Election = new ElectionLimits(),
                    OnHandoff = promotion.Handler,
                    OnHostLost = election.Handler,
                    PersistEpochProof = proofs.Add,
                });
            if (!(await friend.JoinAsync()).Joined) throw new InvalidOperationException("the friend could not join");
            await WaitAsync(() => friend.Game.Snapshot != null);

            var hostScreen = new NetGameSession(hostPlayer, ruleset, host);
            var friendScreen = new NetGameSession(friend, ruleset);

            if (await host.Host.DesignateBackupAsync("Greece", babylon.PlayerKey) != null) throw new InvalidOperationException("the backup host was not designated");
            await friend.AcceptStandbyAsync(["127.0.0.1"], friendPort, HostCertificate.GetFingerprint(friendCertificate));
            await WaitAsync(() => host.Host.Standby.State == StandbyStates.Ready);
            await host.Host.EnqueueAsync(() =>
            {
                foreach (var civ in host.Host.Game.Civilizations.Where(it => it.IsHuman() && !host.Host.Game.HasEndedTurn(it)))
                    host.Host.Session.Submit(civ.CivId, new EndTurn());
                host.Host.Tick();
                return Task.CompletedTask;
            });
            var seq = host.Host.Store.NotNull().LastSeq;
            await WaitAsync(() => friend.StandbyHaveSeq >= seq);
            return new Rig { Host = host, Friend = friend, HostScreen = hostScreen, FriendScreen = friendScreen, Promotion = promotion, Proofs = proofs, Partition = value => partitioned = value };
        }

        private sealed class FixedProber(string fingerprint) : IEndpointProber
        {
            public Task<string?> PresentedFingerprintAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<string?>(fingerprint);
        }

        private void Show(NetGameSession net)
        {
            if (screen == null)
            {
                screen = new GameScreen { Name = "SplitBrainGame", NowMs = () => clock.NowMillis };
                root.AddChild(screen);
                overlay = new ReconnectOverlay(theme);
                root.AddChild(overlay);
            }
            overlay.NotNull().Attach(net);
            overlay.WarningAcknowledged += net.DismissMigrationWarning;
            screen.Start(net, AssetRegistry.ForRuleset(net.Ruleset));
        }

        private Rig Ready()
        {
            if (setup is { IsFaulted: true }) setup.GetAwaiter().GetResult();
            return rig ??= setup.NotNull().GetAwaiter().GetResult();
        }

        private async Task<JoinResult> ClaimAsync(Rig live)
        {
            var proof = live.Proofs.Single(it => it.Epoch == 1);
            await using var claimSession = new ClientSession(token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", live.Host.Port.NotNull(), expectedFingerprint: live.Host.Fingerprint, cancellationToken: token),
                new ClientSessionOptions
                {
                    Identity = greece,
                    DisplayName = "Friend",
                    RulesetHash = "",
                    ExpectedFingerprint = live.Host.Fingerprint,
                    HostAddress = $"127.0.0.1:{live.Host.Port}",
                    KnownHosts = new KnownHosts(),
                    GameId = live.Host.GameId,
                    KnownEpoch = 1,
                    EpochProof = proof,
                    ReconnectDelay = (_, _) => Task.Delay(30),
                    KeepAliveInterval = Timeout.InfiniteTimeSpan,
                });
            return await claimSession.JoinAsync();
        }

        private async Task<(bool, string?, bool)> RecoverAsync(Rig live)
        {
            var promoted = live.Promotion.Host.NotNull();
            await using var returned = new ClientSession(token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", promoted.Port.NotNull(), expectedFingerprint: promoted.Fingerprint, cancellationToken: token),
                new ClientSessionOptions
                {
                    Identity = babylon,
                    DisplayName = "Host",
                    RulesetHash = "",
                    ExpectedFingerprint = promoted.Fingerprint,
                    HostAddress = $"127.0.0.1:{promoted.Port}",
                    KnownHosts = new KnownHosts(),
                    GameId = promoted.GameId,
                    KnownEpoch = 1,
                    ReconnectDelay = (_, _) => Task.Delay(30),
                    KeepAliveInterval = Timeout.InfiniteTimeSpan,
                });
            var joined = await returned.JoinAsync();
            if (!joined.Joined) return (false, joined.Code, false);
            await WaitAsync(() => returned.Game.Snapshot != null);
            return (true, null, returned.Game.Snapshot != null);
        }

        public IReadOnlyList<ScenarioStep> Steps() => new ScenarioStep[]
        {
            new WaitUntil("the two machines are in the same game", () => setup is { IsCompletedSuccessfully: true }, 6000),
            new Act("show the backup host's game", () => Show(Ready().FriendScreen)),
            new WaitFrames(6),
            new AssertQuery("both machines play the same game with no warning yet", () =>
                Ready().HostScreen.MigrationWarning == null && Ready().FriendScreen.MigrationWarning == null && Overlay().Title.Length == 0),

            new Act("only the link between the two machines is cut", () =>
            {
                var live = Ready();
                var greeceSession = live.Host.Host.Sessions.Values.First(it => it.BoundCivId == "Greece");
                _ = greeceSession.Transport.CloseAsync();
                live.Partition(true);
            }),
            new WaitUntil("the backup host notices the host is gone", () => Ready().Friend.State != ClientSessionState.InGame),
            new Act("the loss window passes", () => clock.AdvanceSeconds(25)),
            new WaitUntil("the backup host takes over its own copy", () => Ready().Promotion.Host != null && Ready().Friend.State == ClientSessionState.InGame, 3000),
            new WaitUntil("the two-human warning is on its screen", () => Ready().FriendScreen.MigrationWarning != null, 600),
            new WaitFrames(4),
            new AssertQuery("the successor is warned that the game may have split and can keep playing", () =>
                Overlay().Visible && Overlay().Title == HostMigrationModel.MayHaveSplitNotice().Title && Overlay().ContinueButton.Visible && !Overlay().LeaveButton.Visible),
            new Capture("successor-warning", "The backup host after the partition: it lost the host, continued alone because there are only two players, and is warned that the other player may still be playing on their own copy. The notice says to agree on one copy and how to rejoin; Continue playing puts it aside."),

            new Press("Continue playing"),
            new WaitFrames(4),
            new AssertQuery("putting the warning aside clears it", () => Ready().FriendScreen.MigrationWarning == null && Ready().FriendScreen.Connection == ClientSessionState.InGame),
            new AssertQuery("putting the warning aside leaves the game playable", () => !Overlay().Visible),

            new Act("show the old host's machine, which never heard the takeover", () => Show(Ready().HostScreen)),
            new WaitFrames(6),
            new AssertQuery("the old host kept playing with no warning", () => !Overlay().Visible && Ready().HostScreen.Connection == ClientSessionState.InGame && !Ready().Host.IsSuperseded),
            new Capture("old-host-playing", "The old host's machine during the same partition: it still plays its copy and has no warning, because it does not know that the other machine continued without it. That is the split."),

            new Act("the sides meet: the player who took over connects to the old host with the proof", () => claim = Task.Run(() => ClaimAsync(Ready()))),
            new WaitUntil("the old host refuses the claim", () => claim is { IsCompleted: true }, 1200),
            new AssertQuery("the refused claim is stale-epoch and the old host is fenced", () =>
                claim.NotNull().Result.Code == JoinRefusedCodes.StaleEpoch && Ready().Host.IsSuperseded),
            new WaitUntil("the old host's player is told", () => Ready().HostScreen.EndedReason == ClientCodes.StaleEpoch, 1200),
            new WaitFrames(4),
            new AssertQuery("the old host's screen says another host took over and how to recover", () =>
                Overlay().Visible && Overlay().Title == HostMigrationModel.FencedTitle && Overlay().LeaveButton.Visible && !Overlay().ContinueButton.Visible && Overlay().Detail.Contains("join the other player's game", StringComparison.Ordinal)),
            new Capture("old-host-fenced", "The old host when the two machines meet: the verified claim fences it at the newer epoch, its own player is told stale-epoch, and the screen says another host took over and to leave and join that game with the same key. The recovery path is the invite."),

            new Act("the old host's player joins the new host with the same key", () => recovery = Task.Run(() => RecoverAsync(Ready()))),
            new WaitUntil("the returning player is seated on the new host", () => recovery is { IsCompleted: true }, 1200),
            new AssertQuery("the same seat and key continue on the new host", () =>
                recovery.NotNull().Result is { Joined: true, HasSnapshot: true }),

            new Act("clean up", () =>
            {
                foreach (var child in root.GetChildren()) child.QueueFree();
                hostCertificate.Dispose();
                friendCertificate.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }),
        };

        private ReconnectOverlay Overlay() => overlay ?? throw new InvalidOperationException("the screen was not shown");
    }
}
