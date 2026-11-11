using Godot;
using Unciv.Client.Net.Session;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Setup;
using Unciv.GodotClient.Ui;
using Unciv.Logic.Map;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Lobby;
using Unciv.Server;
using Unciv.Utils;

namespace Unciv.GodotHarness;

/// <summary>
/// T348: the lobby's host comforts, by clicking: three scripted clients over the in-memory transport. The host sees Kick and Ban on every other human seat; the reason popup asks for an
/// optional reason and the removed player is told exactly that sentence (kick) or is also refused on rejoin (ban). A friend who drops and comes back with a fresh key but the same name
/// reclaims their seat and nation. The chat line the friends send is relayed with the host-stamped sender and the host's own answer.
/// </summary>
public sealed class LobbyAdminScenario : Scenario
{
    private static readonly string Fingerprint = HostKey.Fingerprint([1, 2, 3, 4]);
    private const string BraveNewWorld = "Civ V - Brave New World";

    public override string Name => "lobby-admin";

    public override bool Solo => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Is the reason popup clear about who it acts on, and is the removed player told the same reason?",
        "Can you tell Kick (the player may rejoin) from Ban (the key cannot), and does a banned player see why?",
        "Does a dropped player who returns with a new device but the same name get their seat and nation back?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session) => new Flow(host, session).Steps();

    private sealed class Flow
    {
        private readonly UiTheme theme;
        private readonly Control root;
        private readonly NewGameForm nations = NewGameForm.FromRulesetCache();
        private readonly LobbySettings settings;
        private readonly ServerLobby lobby;
        private readonly ClientIdentity[] identities = [ClientIdentity.Generate(), ClientIdentity.Generate(), ClientIdentity.Generate(), ClientIdentity.Generate(), ClientIdentity.Generate()];
        private readonly ClientSession?[] sessions = new ClientSession?[5];
        private readonly Task<JoinResult>?[] joins = new Task<JoinResult>?[5];
        private LobbyScreen? hostScreen;
        private bool bobBanned;
        private bool carolKicked;
        private bool bobRejoinRefused;
        private bool daveRejoined;

        public Flow(Control host, GameSession session)
        {
            theme = new UiTheme(AssetRegistry.ForRuleset(session.Game.Ruleset));
            host.AddChild(new ColorRect { Color = new Color(0.12f, 0.14f, 0.2f), AnchorRight = 1, AnchorBottom = 1 });
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
        }

        private static Vector2 Centre(Func<Control?> find) => find()?.GetGlobalRect().GetCenter() ?? Vector2.Zero;

        private ClientSession Connect(int who, string name)
        {
            var (client, hostEnd) = InMemoryTransport.CreatePair();
            _ = lobby.AttachConnectionAsync(hostEnd);
            var created = new ClientSession(_ => Task.FromResult<ITransport>(client), new ClientSessionOptions
            {
                Identity = identities[who],
                DisplayName = name,
                RulesetHash = settings.ComputeRulesetHash(),
                ExpectedFingerprint = Fingerprint,
                MaxReconnectAttempts = 0,
            });
            sessions[who] = created;
            joins[who] = created.JoinAsync();
            return created;
        }

        private LobbyScreen Host() => hostScreen.NotNull();

        private bool Joined(int who) => joins[who] is { IsCompleted: true, Result.Joined: true };

        private static bool SawEvent(ClientSession session, Func<ClientEvent, bool> match)
        {
            while (session.Events.TryRead(out var e))
                if (match(e)) return true;
            return false;
        }

        public IReadOnlyList<ScenarioStep> Steps() =>
            Seating().Concat(Admin()).Concat(Rejoin()).SelectMany(step => step is Press or Click or Act ? new[] { step, new WaitFrames(2) } : [step]).ToList();

        private ScenarioStep[] Seating() =>
        [
            new Act("the host takes slot 1 and the lobby screen opens", () =>
            {
                var host = Connect(0, "Host");
                hostScreen = new LobbyScreen(theme, nations, host, identities[0].PlayerKey, lobby);
                root.AddChild(hostScreen);
            }),
            new WaitUntil("the host is seated", () => Joined(0) && Host().State is { Slots.Count: 4 }),
            new Act("Bob and Carol join", () => Connect(1, "Bob")),
            new WaitUntil("Bob is seated", () => Joined(1) && Host().SlotLines.Count == 4 && Host().SlotLines[1] == "2. Bob"),
            new Act("Carol joins", () => Connect(2, "Carol")),
            new WaitUntil("Carol is seated", () => Joined(2) && Host().SlotLines[2] == "3. Carol"),
            new AssertQuery("only the host's own row has no Kick or Ban", () =>
                Host().KickButton(0) == null && Host().BanButton(0) == null && Host().KickButton(1) != null && Host().BanButton(1) != null && Host().KickButton(2) != null),
            new Act("Bob says hello in the chat", () => _ = sessions[1].NotNull().SendChatAsync("Ready when you are")),
            new WaitUntil("the chat line arrived with Bob's name", () => Host().ChatLines.Contains("Bob: Ready when you are")),
            new Capture("seated", "Host's lobby: Bob and Carol seated, each row with Kick and Ban (the host's own row has neither); Bob's chat line is in the log."),
        ];

        private ScenarioStep[] Admin() =>
        [
            new Click(() => Centre(() => Host().KickButton(2))),
            new WaitUntil("the kick reason popup is open", () => Host().ReasonView.Visible),
            new Act("the host types the reason", () => Host().ReasonView.ReasonField.Text = "Idle too long"),
            new Capture("reason", "The kick reason popup over the lobby: 'Kick Carol' in the title, the reason 'Idle too long' typed, Cancel and the danger Kick button."),
            new Click(() => Centre(() => Host().ReasonView.ConfirmButton)),
            new WaitUntil("Carol's seat is open again", () => Host().State is { } s && s.Slots[2].Kind == "Open"),
            new Act("Carol's session saw the reason", () => carolKicked = SawEvent(sessions[2].NotNull(), e => e is JoinRefusedEvent { Code: JoinRefusedCodes.Kicked, Reason: "Idle too long" })),
            new AssertQuery("Carol was told the exact reason and the seat is unbound", () => carolKicked && lobby.Lobby.Slots[2].PlayerKeyId == null && Host().SlotLines[2] == "3. Open"),

            new Click(() => Centre(() => Host().BanButton(1))),
            new WaitUntil("the ban reason popup is open", () => Host().ReasonView.Visible),
            new Act("the host types the ban reason", () => Host().ReasonView.ReasonField.Text = "Cheating"),
            new Click(() => Centre(() => Host().ReasonView.ConfirmButton)),
            new WaitUntil("Bob's seat is open again", () => Host().State is { } s && s.Slots[1].Kind == "Open"),
            new Act("Bob's session saw the ban reason", () => bobBanned = SawEvent(sessions[1].NotNull(), e => e is JoinRefusedEvent { Code: JoinRefusedCodes.Banned, Reason: "Cheating" })),
            new AssertQuery("Bob was banned with the reason and his key is on the ban list", () => bobBanned && lobby.Lobby.BannedKeys.Contains(identities[1].PlayerKey) && Host().SlotLines[1] == "2. Open"),

            new Act("Bob's key tries to rejoin and is refused", () =>
            {
                var (client, hostEnd) = InMemoryTransport.CreatePair();
                _ = lobby.AttachConnectionAsync(hostEnd);
                var again = new ClientSession(_ => Task.FromResult<ITransport>(client), new ClientSessionOptions
                {
                    Identity = identities[1],
                    DisplayName = "Bob",
                    RulesetHash = settings.ComputeRulesetHash(),
                    ExpectedFingerprint = Fingerprint,
                    MaxReconnectAttempts = 0,
                });
                _ = again.JoinAsync().ContinueWith(t => bobRejoinRefused = t.Result is { Joined: false, Code: JoinRefusedCodes.Banned }, TaskScheduler.Default);
            }),
            new WaitUntil("the rejoin was refused as banned", () => bobRejoinRefused),
            new Capture("banned", "After Carol was kicked (slot 3 Open) and Bob was banned (slot 2 Open): the host's lobby shows the freed seats; the reason popup is gone."),
        ];

        private ScenarioStep[] Rejoin() =>
        [
            new Act("Dave joins (into the first freed seat) and picks a nation", () => Connect(3, "Dave")),
            new WaitUntil("Dave is seated", () => Joined(3) && Host().SlotLines[1] == "2. Dave"),
            new Act("Dave picks Greece and readies", () => _ = sessions[3].NotNull().SetLobbySlotAsync(1, "Greece", null, true)),
            new WaitUntil("Dave's nation reached the lobby", () => lobby.Lobby.Slots[1].Nation == "Greece"),
            new Act("Dave drops (his connection closes)", () => _ = sessions[3].NotNull().DisposeAsync().AsTask()),
            new WaitUntil("Dave's seat is disconnected", () => !lobby.Lobby.Slots[1].Connected),
            new Act("Dave returns on a new device, same name", () => Connect(4, "Dave")),
            new WaitUntil("Dave reclaimed his seat by name", () => Joined(4) && lobby.Lobby.Slots[1].Connected && lobby.Lobby.Slots[1].PlayerKeyId == identities[4].PlayerKey),
            new Act("the rejoin was by name, not key", () => daveRejoined = lobby.Lobby.Bindings.SlotOf(identities[3].PlayerKey) == null),
            new AssertQuery("the dropped seat kept Greece and Dave's old key is gone", () =>
                daveRejoined && lobby.Lobby.Slots[1].Nation == "Greece" && lobby.Lobby.Bindings.SlotOf(identities[4].PlayerKey) == 1 && Host().SlotLines[1] == "2. Dave"),
            new Act("clean up", () =>
            {
                foreach (var s in sessions) _ = s?.DisposeAsync().AsTask();
                _ = lobby.DisposeAsync().AsTask();
            }),
        ];
    }
}
