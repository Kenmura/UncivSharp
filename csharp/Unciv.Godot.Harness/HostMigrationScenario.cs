using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Godot;
using Unciv.Client.Net.Session;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Setup;
using Unciv.GodotClient.Ui;
using Unciv.Logic.Map;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Lobby;
using Unciv.Net.Standby;
using Unciv.Server;
using Unciv.Server.Transport;
using Unciv.Utils;

namespace Unciv.GodotHarness;

/// <summary>
/// T275: the host-migration UI, by clicking. Two humans sit in a lobby; the host picks the friend as the backup host, the friend sees the trust notice and the consent prompt and accepts,
/// and the host's lobby shows the standby as ready. A third player's join screen shows the name-free trust notice before it sends anything. The rollback toast (what a player sees when the
/// new host lacked their last actions) is shown on the game screen. The superseded-host screen is the split-brain scenario's old-host-fenced capture.
/// </summary>
public sealed class HostMigrationScenario : Scenario
{
    public override string Name => "host-migration";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Is the trust notice clear that the backup host sees everything, before anyone joins?",
        "Can the host tell from the lobby whether the backup host is ready, and what to do if it is not?",
        "Does the rollback notice make clear that only a few actions were lost, not the game?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session) => new Flow(host, session).Steps();

    private sealed class Flow
    {
        private static readonly string Fingerprint = HostKey.Fingerprint([1, 2, 3, 4]);
        private const string BraveNewWorld = "Civ V - Brave New World";

        private readonly UiTheme theme;
        private readonly Control root;
        private readonly GameSession seeded;
        private readonly NewGameForm nations = NewGameForm.FromRulesetCache();
        private readonly LobbySettings settings;
        private readonly ServerLobby lobby;
        private readonly ClientIdentity hostIdentity = ClientIdentity.Generate();
        private readonly ClientIdentity friendIdentity = ClientIdentity.Generate();
        private readonly X509Certificate2 friendCertificate = HostCertificate.Generate();
        private readonly string friendFingerprint;
        private readonly int friendPort = FreePort();
        private ClientSession? hostPlayer;
        private ClientSession? friend;
        private LobbyScreen? hostLobby;
        private LobbyScreen? friendLobby;
        private JoinScreen? joinScreen;

        public Flow(Control host, GameSession session)
        {
            seeded = session;
            friendFingerprint = HostCertificate.GetFingerprint(friendCertificate);
            theme = new UiTheme(AssetRegistry.ForRuleset(session.Game.Ruleset));
            root = new Control { AnchorRight = 1, AnchorBottom = 1 };
            host.AddChild(root);
            if (nations.SetBaseRuleset(BraveNewWorld) is { } problem) throw new InvalidOperationException(problem);
            settings = new LobbySettings
            {
                GameName = "Friday night",
                Ruleset = BraveNewWorld,
                Difficulty = "King",
                Speed = "Epic",
                MaxMajorCivs = 4,
                CityStateCount = 0,
                AiFill = true,
                TurnTimerSeconds = 300,
                Map = new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny },
            };
            lobby = new ServerLobby(settings, hostFingerprint: Fingerprint, hostName: "Host");
            lobby.Lobby.Standby.Prober = new FixedProber(friendFingerprint);
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static Vector2 Centre(Func<Control?> find) => find()?.GetGlobalRect().GetCenter() ?? Vector2.Zero;

        private ClientSession Connect(string name, ClientIdentity identity)
        {
            var (client, hostEnd) = InMemoryTransport.CreatePair();
            _ = lobby.AttachConnectionAsync(hostEnd);
            var created = new ClientSession(_ => Task.FromResult<ITransport>(client), new ClientSessionOptions
            {
                Identity = identity,
                DisplayName = name,
                RulesetHash = settings.ComputeRulesetHash(),
                ExpectedFingerprint = Fingerprint,
                MaxReconnectAttempts = 0,
            });
            _ = created.JoinAsync();
            return created;
        }

        public IReadOnlyList<ScenarioStep> Steps() =>
        [
            .. LobbySteps(),
            .. JoinSteps(),
            .. RollbackSteps(),
            new Act("clean up", () =>
            {
                foreach (var child in root.GetChildren()) child.QueueFree();
                friendCertificate.Dispose();
                _ = friend?.DisposeAsync().AsTask();
                _ = hostPlayer?.DisposeAsync().AsTask();
                _ = lobby.DisposeAsync().AsTask();
            }),
        ];

        // ---------------------------------------------------------------- lobby: picker, trust notice, consent, standby status

        private ScenarioStep[] LobbySteps() =>
        [
            new Act("the host and a friend sit in a lobby", () =>
            {
                hostPlayer = Connect("Host", hostIdentity);
                hostLobby = new LobbyScreen(theme, nations, hostPlayer.NotNull(), hostIdentity.PlayerKey, lobby);
                root.AddChild(hostLobby);
            }),
            new WaitUntil("the host is seated", () => hostLobby.NotNull().State is { Slots.Count: 4 }),
            new Act("the friend joins", () => friend = Connect("Friend", friendIdentity)),
            new WaitUntil("the friend is seated", () => hostLobby.NotNull().SlotLines.Count == 4 && hostLobby.NotNull().SlotLines[1] == "2. Friend"),
            new AssertQuery("no backup host yet: no notice, no status", () =>
                hostLobby.NotNull().BackupHostNoticeText.Length == 0 && hostLobby.NotNull().StandbyStatusText.Length == 0 && hostLobby.NotNull().BackupHostChoice == "None"),
            new Capture("lobby-picker", "The host's lobby before any backup host: the backup-host picker below the seats offers None and the friend's seat. The host's own seat is the host, not a candidate."),

            new Act("the host picks the friend as the backup host", () => PressBackupHost(hostLobby.NotNull(), "Friend")),
            new WaitUntil("the host sees the friend chosen", () => hostLobby.NotNull().BackupHostChoice == "Friend"),
            new AssertQuery("the host is told the backup host has been asked", () =>
                hostLobby.NotNull().StandbyStatusText.Contains("asked", StringComparison.Ordinal) && hostLobby.NotNull().BackupHostNoticeText.Contains("Friend", StringComparison.Ordinal)),
            new Capture("lobby-standby", "The host's lobby after choosing the friend: the picker shows the friend chosen, the trust notice names them, and the standby status says they have been asked and not answered yet."),

            new Act("the friend's screen opens", () =>
            {
                hostLobby.NotNull().Visible = false;
                friendLobby = new LobbyScreen(theme, nations, friend.NotNull(), friendIdentity.PlayerKey,
                    acceptStandby: () => friend.NotNull().AcceptStandbyAsync(["127.0.0.1"], friendPort, friendFingerprint).AsTask());
                root.AddChild(friendLobby);
            }),
            new WaitUntil("the friend is offered the role", () => friendLobby.NotNull().StandbyOfferText.Length > 0),
            new AssertQuery("the friend sees the trust notice naming them and the consent prompt", () =>
                friendLobby.NotNull().BackupHostNoticeText.Contains("Friend", StringComparison.Ordinal) && friendLobby.NotNull().StandbyOfferText.Length > 0 && friendLobby.NotNull().AcceptStandbyButton is { Visible: true }),
            new Capture("lobby-trust", "The friend's lobby: the trust notice names them as the backup host, and the consent prompt asks them to accept or decline before anything is copied."),

            new Click(() => Centre(() => friendLobby.NotNull().AcceptStandbyButton)),
            new WaitUntil("the backup host is ready", () => lobby.Lobby.Standby.State == StandbyStates.Ready),
            new Act("back to the host", () =>
            {
                friendLobby.NotNull().Visible = false;
                hostLobby.NotNull().Visible = true;
            }),
            new WaitFrames(3),
            new AssertQuery("the host now shows the backup host as ready", () =>
                hostLobby.NotNull().StandbyStatusText.Contains("ready", StringComparison.Ordinal)),
            new Capture("host-standby", "The host's lobby once the friend accepted: the standby status says the backup host is ready and holds an up-to-date copy."),
        ];

        private void PressBackupHost(LobbyScreen screen, string name)
        {
            var button = screen.FindChild("BackupHostPicker", true, false)?
                .GetChildren().OfType<Button>().FirstOrDefault(it => it.Text == name);
            if (button == null) throw new InvalidOperationException("no backup-host picker button for " + name);
            button.EmitSignal(BaseButton.SignalName.Pressed);
        }

        // ---------------------------------------------------------------- join: the name-free trust notice before anything is sent

        private ScenarioStep[] JoinSteps() =>
        [
            new Act("a third player opens the join screen against this lobby", () =>
            {
                hostLobby.NotNull().Visible = false;
                var joinIdentity = ClientIdentity.Generate();
                var known = new KnownHosts();
                var environment = new JoinScreenEnvironment
                {
                    Identity = joinIdentity,
                    DisplayName = "Stranger",
                    RulesetHash = () => settings.ComputeRulesetHash(),
                    KnownHosts = known,
                    PersistKnownHosts = _ => { },
                    RememberAddress = _ => { },
                    Connect = (_, _) =>
                    {
                        var (client, hostEnd) = InMemoryTransport.CreatePair();
                        _ = lobby.AttachConnectionAsync(hostEnd);
                        return Task.FromResult<ITransport>(client);
                    },
                };
                joinScreen = new JoinScreen(theme, environment);
                root.AddChild(joinScreen);
            }),
            new WaitFrames(2),
            new Act("the player pastes the invite", () =>
            {
                joinScreen.NotNull().AddressField.Text = InviteCode.Format("192.168.1.20", 47821, Fingerprint);
            }),
            new Click(() => Centre(() => joinScreen.NotNull().JoinButton)),
            new WaitUntil("the trust notice opens before the join", () => joinScreen.NotNull().BackupNoticePrompt.Length > 0),
            new AssertQuery("the notice is the name-free trust text, and nobody was seated yet", () =>
                joinScreen.NotNull().BackupNoticePrompt == StandbyNotice.Generic && lobby.Lobby.Slots.Count(it => it.Kind == SlotKind.Human) == 2),
            new Capture("join-notice", "The join screen: before anything is sent, the player is told the game has a backup host who holds a complete copy and is trusted like the host. Continue joins; Do not join sends nothing."),

            new Act("the player declines", () => joinScreen.NotNull().BackupNoticeDeclineButton.EmitSignal(BaseButton.SignalName.Pressed)),
            new WaitUntil("the attempt ended without a seat", () => !joinScreen.NotNull().IsBusy && joinScreen.NotNull().Message.Length > 0),
            new AssertQuery("declining sent nothing and seated nobody", () =>
                joinScreen.NotNull().Message.Contains("chose not to join", StringComparison.Ordinal) && lobby.Lobby.Slots.Count(it => it.Kind == SlotKind.Human) == 2),
            new Act("remove the join screen", () => joinScreen.NotNull().QueueFree()),
        ];

        // ---------------------------------------------------------------- rollback: the toast a player sees after a host change that lost their last actions

        private ScenarioStep[] RollbackSteps() =>
        [
            new Act("open the game screen over the seeded game", () =>
            {
                var local = new LocalHost(seeded, seeded.Game.Civilizations.First(it => it.IsHuman()).CivName, () => 0L);
                var screen = new GameScreen { Name = "RollbackGame", NowMs = () => 0L };
                root.AddChild(screen);
                screen.Start(local, AssetRegistry.ForRuleset(local.Ruleset));
                var notice = HostMigrationModel.RolledBackNotice(3);
                screen.Notifications.PushToast(notice.Title + ": " + notice.Detail);
            }),
            new WaitFrames(4),
            new AssertQuery("the rollback toast is shown", () =>
                root.GetChildren().OfType<GameScreen>().First().Notifications.ToastTexts.Any(it => it.Contains("rolled back", StringComparison.Ordinal))),
            new Capture("rollback", "The game screen with the rollback toast: the host changed and did not have the player's last three actions, so they were rolled back. The toast names what happened and that the rest is unchanged."),
        ];

        private sealed class FixedProber(string presented) : IEndpointProber
        {
            public Task<string?> PresentedFingerprintAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<string?>(presented);
        }
    }
}
