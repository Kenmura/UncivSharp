using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Unciv.Client.Net.Hosting;
using Unciv.Client.Net.Session;
using Unciv.Client.Net.Transport;
using Unciv.Commands.Turn;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Setup;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Persistence;
using Unciv.Net.Standby;
using Unciv.Net.Turns;
using Unciv.Server;
using Unciv.Server.Transport;
using RulesetClass = Unciv.Models.Ruleset.Ruleset;

namespace Unciv.Core.Tests.Net.Standby;

/// <summary>
/// T340: with exactly two humans the electorate is the successor's own seat, so a partition can give each machine a host. The host is not killed: only the successor's link is cut, it promotes on
/// its own, and both machines keep playing. The successor's session warns that the game may have split; when the two meet, a claim carrying the promotion proof fences the old host (it refuses
/// players with stale-epoch and its own screen says another host took over), and the old host's player can rejoin the new host with the same key. Time is a shared manual clock.
/// </summary>
[Collection("RulesetCache")]
public sealed class SplitBrainTests : IDisposable
{
    private const long Seed = 23L;
    private const long StartClock = 1000L;
    private readonly RulesetClass ruleset;
    private readonly string root = Directory.CreateTempSubdirectory("unciv-split-brain-").FullName;
    private readonly ClientIdentity babylon = ClientIdentity.Generate();
    private readonly ClientIdentity greece = ClientIdentity.Generate();
    private readonly List<X509Certificate2> certificates = [];
    private readonly List<IAsyncDisposable> toDispose = [];
    private readonly ManualClock clock = new(5_000_000);

    public SplitBrainTests()
    {
        var jsons = Path.Combine(RepoRoot(), "data", "jsons");
        RulesetCache.Current.LoadRulesets(new DirectoryRulesetFileSource(jsons, Path.Combine(jsons, "no mods here")), noMods: true);
        ruleset = RulesetCache.Current.GetVanillaRuleset();
    }

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(toDispose)) item.DisposeAsync().AsTask().GetAwaiter().GetResult();
        RulesetCache.Current.Clear();
        babylon.Dispose();
        greece.Dispose();
        foreach (var cert in certificates) cert.Dispose();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "jsons"))) return dir.FullName;
        throw new DirectoryNotFoundException("data/jsons not found above " + AppContext.BaseDirectory);
    }

    private T Track<T>(T item) where T : IAsyncDisposable
    {
        toDispose.Add(item);
        return item;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private X509Certificate2 NewCertificate()
    {
        var cert = HostCertificate.Generate();
        certificates.Add(cert);
        return cert;
    }

    private sealed class FakeProber(string fingerprint) : IEndpointProber
    {
        public Task<string?> PresentedFingerprintAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<string?>(fingerprint);
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

    private async Task<Rig> StartAsync()
    {
        var hostCertificate = NewCertificate();
        var friendCertificate = NewCertificate();
        var friendPort = FreePort();
        var friendFingerprint = HostCertificate.GetFingerprint(friendCertificate);

        List<Player> players = [new("Babylon", PlayerType.Human), new("Greece", PlayerType.Human)];
        var setup = new GameSetupInfo(
            new GameParameters { BaseRuleset = ruleset.Name, NumberOfCityStates = 0, VictoryTypes = ruleset.Victories.Keys.ToList(), Players = players },
            new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny });
        var game = GameStarter.StartNewGame(setup, Seed, StartClock);
        game.StartSimultaneousTurns();
        var humans = new Dictionary<string, SlotBinding>
        {
            ["Babylon"] = new SlotBinding { PlayerKey = babylon.PlayerKey, DisplayName = "Host" },
            ["Greece"] = new SlotBinding { PlayerKey = greece.PlayerKey, DisplayName = "Friend" },
        };
        var options = new InProcessHostOptions { TickInterval = null, Clock = clock, Store = new StoreOptions { SnapshotEveryTurns = 2 } };
        var host = Track(InProcessHost.Start(game, setup, Seed, StartClock, Path.Combine(root, "host"), "Babylon", humans, options));
        host.Listen(hostCertificate, 0);
        host.Host.Standby.Prober = new FakeProber(friendFingerprint);
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
        Track(hostPlayer);
        Assert.True((await hostPlayer.JoinAsync()).Joined);
        await WaitUntilAsync(() => hostPlayer.Game.Snapshot != null, "the host player never got a snapshot");

        var promotion = new StandbyPromotion(Path.Combine(root, "friend"), greece, friendCertificate, options);
        var election = new SuccessorElection(promotion, friendCertificate, clock, new ElectionLimits(), listen: false);
        Track(election);
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
                StandbyRoot = Path.Combine(root, "friend"),
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
        Track(friend);
        Assert.True((await friend.JoinAsync()).Joined);
        await WaitUntilAsync(() => friend.Game.Snapshot != null, "the friend never got a snapshot");

        var hostScreen = Track(new NetGameSession(hostPlayer, ruleset, host));
        var friendScreen = Track(new NetGameSession(friend, ruleset));

        Assert.Null(await host.Host.DesignateBackupAsync("Greece", babylon.PlayerKey));
        await friend.AcceptStandbyAsync(["127.0.0.1"], friendPort, friendFingerprint);
        await WaitUntilAsync(() => host.Host.Standby.State == StandbyStates.Ready, "the standby never became ready");

        await host.Host.EnqueueAsync(() =>
        {
            foreach (var civ in host.Host.Game.Civilizations.Where(it => it.IsHuman() && !host.Host.Game.HasEndedTurn(it)))
                Assert.True(host.Host.Session.Submit(civ.CivId, new EndTurn()).Accepted);
            host.Host.Tick();
            return Task.CompletedTask;
        });
        var hostSeq = await SeqAsync(host);
        await WaitUntilAsync(() => friend.StandbyHaveSeq >= hostSeq, "the standby copy never caught up");

        return new Rig
        {
            Host = host,
            Friend = friend,
            HostScreen = hostScreen,
            FriendScreen = friendScreen,
            Promotion = promotion,
            Proofs = proofs,
            Partition = value => partitioned = value,
        };
    }

    private static async Task<long> SeqAsync(InProcessHost host)
    {
        long seq = -1;
        await host.Host.EnqueueAsync(() =>
        {
            seq = host.Host.Store!.LastSeq;
            return Task.CompletedTask;
        });
        return seq;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++) await Task.Delay(20);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task TwoHumans_Partition_GivesEachMachineAHost_AndTheSuccessorWarns()
    {
        var rig = await StartAsync();
        var before = await SeqAsync(rig.Host);

        // Only the successor's link is cut: the host machine keeps playing on its own copy (the two-human split of design/HOST-MIGRATION.md section 2).
        rig.Partition(true);
        var greeceSession = rig.Host.Host.Sessions.Values.First(it => it.BoundCivId == "Greece");
        await greeceSession.Transport.CloseAsync();
        await WaitUntilAsync(() => rig.Friend.State != ClientSessionState.InGame, "the friend never noticed the partition");

        clock.AdvanceSeconds(25); // beyond HostLossSeconds and ReconnectGraceSeconds
        await WaitUntilAsync(() => rig.Promotion.Host != null, "the successor never promoted");
        await WaitUntilAsync(() => rig.Friend.State == ClientSessionState.InGame, "the successor did not rejoin its own host");
        await WaitUntilAsync(() => rig.FriendScreen.MigrationWarning != null, "the two-human split warning never reached the successor's screen");

        // Two machines, one host each, no referee: the old host is still at epoch 0, the successor at epoch 1.
        Assert.False(rig.Host.IsSuperseded);
        Assert.Equal(0, ((FileGameStore)rig.Host.Host.Store!).ReadMeta().HostEpoch);
        Assert.Equal(1, ((FileGameStore)rig.Promotion.Host!.Host.Store!).ReadMeta().HostEpoch);
        Assert.True(await SeqAsync(rig.Promotion.Host) >= before);

        // The warning is the playable one (not a disconnect) and names the recovery: agree on one copy, join it with an invite.
        var warning = rig.FriendScreen.MigrationWarning!;
        Assert.Equal("The game may have split", warning.Title);
        Assert.Contains("two separate games", warning.Detail, StringComparison.Ordinal);
        Assert.False(JoinModel.Connection(true, false, null, warning).Final);
        Assert.True(JoinModel.Connection(true, false, null, warning).Dismissible);

        // A warning that was read stays away.
        rig.FriendScreen.DismissMigrationWarning();
        Assert.Null(rig.FriendScreen.MigrationWarning);
    }

    [Fact]
    public async Task TwoHumans_WhenTheSidesMeet_TheOldHostIsFenced_AndItsPlayerCanJoinTheNewHost()
    {
        var rig = await StartAsync();
        rig.Partition(true);
        await rig.Host.Host.Sessions.Values.First(it => it.BoundCivId == "Greece").Transport.CloseAsync();
        await WaitUntilAsync(() => rig.Friend.State != ClientSessionState.InGame, "the friend never noticed the partition");
        clock.AdvanceSeconds(25);
        await WaitUntilAsync(() => rig.Promotion.Host != null, "the successor never promoted");
        await WaitUntilAsync(() => rig.Proofs.Count > 0, "the successor never stored its promotion proof");
        var proof = rig.Proofs.Single(it => it.Epoch == 1);

        // The player who took over reaches the old host: the epoch fence makes it refuse the claim and stop serving for good.
        var claim = Track(new ClientSession(token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", rig.Host.Port!.Value, expectedFingerprint: rig.Host.Fingerprint, cancellationToken: token),
            new ClientSessionOptions
            {
                Identity = greece,
                DisplayName = "Friend",
                RulesetHash = "",
                ExpectedFingerprint = rig.Host.Fingerprint,
                HostAddress = $"127.0.0.1:{rig.Host.Port}",
                KnownHosts = new KnownHosts(),
                GameId = rig.Host.GameId,
                KnownEpoch = 1,
                EpochProof = proof,
                ReconnectDelay = (_, _) => Task.Delay(30),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            }));
        var refused = await claim.JoinAsync();
        Assert.False(refused.Joined);
        Assert.Equal(JoinRefusedCodes.StaleEpoch, refused.Code);
        await WaitUntilAsync(() => rig.Host.IsSuperseded, "the old host was not fenced by the verified claim");
        Assert.Equal(1, ((FileGameStore)rig.Host.Host.Store!).ReadMeta().SeenEpoch);

        // Its own player is told, and the in-game screen says another host took over with the recovery path.
        await WaitUntilAsync(() => rig.HostScreen.EndedReason == ClientCodes.StaleEpoch, "the old host's player was not told stale-epoch");
        var fenced = JoinModel.Connection(true, false, rig.HostScreen.EndedReason);
        Assert.True(fenced.Final);
        Assert.Equal(HostMigrationModel.FencedTitle, fenced.Title);
        Assert.Contains("join the other player's game", fenced.Detail, StringComparison.Ordinal);
        Assert.Equal(HostMigrationModel.FencedTitle + ": this copy of the game is out of date and refuses players. " + HostMigrationModel.FencedDetail, LobbyModel.RefusalText(JoinRefusedCodes.StaleEpoch));

        // A later join is refused too, and the old host no longer serves anyone.
        Assert.Equal(JoinRefusedCodes.StaleEpoch, (await PlainJoinAsync(rig.Host, babylon, knownEpoch: 0)).Code);
        await WaitUntilAsync(() => rig.Host.Host.Sessions.Count == 0, "the superseded host still holds sessions");

        // Recovery: the old host's player joins the new host with the same key and gets its seat and a snapshot back.
        var promoted = rig.Promotion.Host!;
        var returned = Track(new ClientSession(token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", promoted.Port!.Value, expectedFingerprint: promoted.Fingerprint, cancellationToken: token),
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
            }));
        var joined = await returned.JoinAsync();
        Assert.True(joined.Joined, joined.Code);
        await WaitUntilAsync(() => returned.Game.Snapshot != null, "the returning player never got a snapshot from the new host");
        Assert.Equal("Babylon", returned.Game.Snapshot!.You);
    }

    private async Task<JoinResult> PlainJoinAsync(InProcessHost host, ClientIdentity identity, int knownEpoch)
    {
        var session = Track(new ClientSession(token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", host.Port!.Value, expectedFingerprint: host.Fingerprint, cancellationToken: token),
            new ClientSessionOptions
            {
                Identity = identity,
                DisplayName = "x",
                RulesetHash = "",
                ExpectedFingerprint = host.Fingerprint,
                HostAddress = $"127.0.0.1:{host.Port}",
                KnownHosts = new KnownHosts(),
                GameId = host.GameId,
                KnownEpoch = knownEpoch,
                ReconnectDelay = (_, _) => Task.Delay(30),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            }));
        return await session.JoinAsync();
    }
}
