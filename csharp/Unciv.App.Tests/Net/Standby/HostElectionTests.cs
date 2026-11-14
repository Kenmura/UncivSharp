using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Unciv.Client.Net.Hosting;
using Unciv.Client.Net.Session;
using Unciv.Client.Net.Transport;
using Unciv.Commands.Log;
using Unciv.Commands.Turn;
using Unciv.Core.Tests.Relay;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Persistence;
using Unciv.Net.Relay;
using Unciv.Net.Standby;
using Unciv.Net.Turns;
using Unciv.Server;
using Unciv.Server.Transport;
using RulesetClass = Unciv.Models.Ruleset.Ruleset;

namespace Unciv.Core.Tests.Net.Standby;

/// <summary>
/// T274 acceptance A6, A7 and A8: the host is killed (its in-memory transports dropped and its actor stopped, or its listener and sockets closed), the successor promotes only after its own loss window and a quorum of signed votes,
/// split-brain attempts are refused, the old host is fenced when it returns, and when host and successor are both gone the clients end in the manual hand-off state. Time is a manual clock shared by every session and the election.
/// </summary>
[Collection("RulesetCache")]
public sealed class HostElectionTests : IDisposable
{
    private const long Seed = 11L;
    private const long StartClock = 1000L;
    private readonly RulesetClass ruleset;
    private readonly string root = Directory.CreateTempSubdirectory("unciv-election-").FullName;
    private readonly ClientIdentity babylon = ClientIdentity.Generate();
    private readonly ClientIdentity greece = ClientIdentity.Generate();
    private readonly ClientIdentity rome = ClientIdentity.Generate();
    private readonly List<X509Certificate2> certificates = [];
    private readonly List<IAsyncDisposable> toDispose = [];
    private readonly ManualClock clock = new(5_000_000);

    public HostElectionTests()
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
        rome.Dispose();
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

    private InProcessHost StartHost(string name, string successorFingerprint, bool withAi)
    {
        List<Player> players = [new("Babylon", PlayerType.Human), new("Greece", PlayerType.Human), new("Rome", PlayerType.Human)];
        if (withAi) players.Add(new("Egypt", PlayerType.AI));
        var setup = new GameSetupInfo(
            new GameParameters { BaseRuleset = ruleset.Name, NumberOfCityStates = 0, VictoryTypes = ruleset.Victories.Keys.ToList(), Players = players },
            new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny });
        var game = GameStarter.StartNewGame(setup, Seed, StartClock);
        game.StartSimultaneousTurns();
        var humans = new Dictionary<string, SlotBinding>
        {
            ["Babylon"] = new SlotBinding { PlayerKey = babylon.PlayerKey, DisplayName = "Host" },
            ["Greece"] = new SlotBinding { PlayerKey = greece.PlayerKey, DisplayName = "Friend" },
            ["Rome"] = new SlotBinding { PlayerKey = rome.PlayerKey, DisplayName = "Third" },
        };
        var host = Track(InProcessHost.Start(game, setup, Seed, StartClock, Path.Combine(root, name), "Babylon", humans,
            new InProcessHostOptions { TickInterval = null, Store = new StoreOptions { SnapshotEveryTurns = 2 } }));
        host.Host.Standby.Prober = new FakeProber { Answer = (_, _) => successorFingerprint };
        host.Host.Standby.Limits = new StandbyLimits { MinSubscribeMillis = 0 };
        return host;
    }

    private static async Task PlayTurnsAsync(InProcessHost host, int turns)
    {
        for (var turn = 0; turn < turns; turn++)
        {
            await host.Host.EnqueueAsync(() =>
            {
                foreach (var civ in host.Host.Game.Civilizations.Where(it => it.IsHuman() && !host.Host.Game.HasEndedTurn(it)))
                    Assert.True(host.Host.Session.Submit(civ.CivId, new EndTurn()).Accepted);
                host.Host.Tick();
                return Task.CompletedTask;
            });
        }
    }

    private static async Task<(long Seq, ulong Hash)> StateAsync(InProcessHost host)
    {
        (long, ulong) state = default;
        await host.Host.EnqueueAsync(() =>
        {
            state = (host.Host.Store!.LastSeq, ReplayVerifier.FullHash(host.Host.Game));
            return Task.CompletedTask;
        });
        return state;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 750 && !condition(); attempt++) await Task.Delay(20);
        Assert.True(condition(), what);
    }

    /// <summary>One player: a real session with the election wiring. The successor also owns a standby directory, a promotion and an election endpoint.</summary>
    private sealed class Seat
    {
        public ClientSession Session { get; set; } = null!;
        public EventTap Tap { get; set; } = null!;
        public StandbyPromotion? Promotion { get; init; }
        public SuccessorElection? Election { get; init; }
        public string? DataRoot { get; init; }
        public List<int> Epochs { get; } = [];
        public List<PromotionProof> Proofs { get; } = [];
        public List<(int Epoch, string Candidate)> Votes { get; } = [];
        public volatile bool Partitioned;
        public int EndpointCalls;
        public Action<int>? OnEndpointCall { get; set; }
    }

    private sealed class Rig
    {
        public required InProcessHost Host { get; init; }
        public required Seat Successor { get; init; }
        public required Seat Third { get; init; }
        public required int SuccessorPort { get; init; }
        public required X509Certificate2 SuccessorCert { get; init; }
        public required bool Tcp { get; init; }
    }

    private async Task<Seat> JoinAsync(InProcessHost host, ClientIdentity identity, string name, bool tcp, string? dataRoot = null, X509Certificate2? ownCertificate = null, Seat?[]? successorRef = null,
        int? votedEpoch = null, string? votedCandidate = null, ElectionLimits? limits = null)
    {
        var hostOptions = new InProcessHostOptions { TickInterval = null, Store = new StoreOptions { SnapshotEveryTurns = 2 } };
        var promotion = ownCertificate != null && dataRoot != null ? new StandbyPromotion(dataRoot, identity, ownCertificate, hostOptions) : null;
        var election = promotion != null ? new SuccessorElection(promotion, ownCertificate!, clock, limits, listen: tcp) : null;
        var known = new KnownHosts();
        var port = host.Port;
        var seat = new Seat { Promotion = promotion, Election = election, DataRoot = dataRoot };
        seat.Session = new ClientSession(
            token =>
            {
                if (seat.Partitioned) return Task.FromException<ITransport>(new IOException("partitioned"));
                return tcp ? TcpTlsClientTransport.ConnectAsync("127.0.0.1", port!.Value, cancellationToken: token) : Task.FromResult<ITransport>(host.ConnectLoopback());
            },
            new ClientSessionOptions
            {
                Identity = identity,
                DisplayName = name,
                RulesetHash = "",
                ExpectedFingerprint = tcp ? host.Fingerprint : null,
                AllowUnpinnedHost = !tcp,
                HostAddress = tcp ? $"127.0.0.1:{port}" : "memory",
                KnownHosts = tcp ? known : null,
                GameId = host.GameId,
                StandbyRoot = dataRoot,
                ReconnectDelay = (_, _) => Task.Delay(30),
                ElectionPoll = TimeSpan.FromMilliseconds(30),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
                HostSilenceTimeout = Timeout.InfiniteTimeSpan,
                Clock = clock,
                Election = limits ?? new ElectionLimits(),
                VotedEpoch = votedEpoch,
                VotedCandidate = votedCandidate,
                PersistVote = (epoch, candidate) => seat.Votes.Add((epoch, candidate)),
                PersistKnownEpoch = seat.Epochs.Add,
                PersistEpochProof = seat.Proofs.Add,
                OnHandoff = promotion == null ? null : (handoff, token) => promotion.Handler(handoff, token),
                OnHostLost = election == null ? null : (info, token) => election.Handler(info, token),
                ConnectEndpoint = (endpoint, token) =>
                {
                    seat.OnEndpointCall?.Invoke(Interlocked.Increment(ref seat.EndpointCalls));
                    if (tcp)
                    {
                        var separator = endpoint.LastIndexOf(':');
                        return TcpTlsClientTransport.ConnectAsync(endpoint[..separator], int.Parse(endpoint[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture), cancellationToken: token);
                    }
                    var owner = successorRef?[0];
                    if (owner?.Promotion?.Host is { } promoted) return Task.FromResult<ITransport>(promoted.ConnectLoopback());
                    var (client, serverEnd) = InMemoryTransport.CreatePair();
                    if (owner?.Election != null) _ = owner.Election.AdmitAsync(serverEnd, CancellationToken.None);
                    return Task.FromResult<ITransport>(client);
                },
            });
        seat.Tap = new EventTap(seat.Session);
        Track(seat.Tap);
        Track(seat.Session);
        if (election != null) Track(election);
        var joined = await seat.Session.JoinAsync();
        Assert.True(joined.Joined, joined.Code);
        seats.Add(seat);
        return seat;
    }

    /// <summary>The host with Babylon, a designated and ready successor (Greece, own listener) and a third player (Rome).</summary>
    private async Task<Rig> ReadyAsync(string name, bool tcp, bool withAi = false, int? romeVotedEpoch = null, string? romeVotedCandidate = null)
    {
        var successorCert = NewCertificate();
        var successorPort = FreePort();
        var host = StartHost(name, HostCertificate.GetFingerprint(successorCert), withAi);
        if (tcp) host.Listen(NewCertificate(), 0);
        var hostPlayer = await host.ConnectHostPlayerAsync(); // the host's own seat is online too: an election must still not count it
        _ = Task.Run(async () =>
        {
            try { while (await hostPlayer.ReceiveAsync(CancellationToken.None) != null) { } }
            catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException) { /* the host died */ }
        });
        var successorRef = new Seat?[1];
        var successor = await JoinAsync(host, greece, "Friend", tcp, Path.Combine(root, name + "-successor"), successorCert, successorRef);
        successorRef[0] = successor;
        var third = await JoinAsync(host, rome, "Third", tcp, successorRef: successorRef, votedEpoch: romeVotedEpoch, votedCandidate: romeVotedCandidate);
        Assert.Null(await host.Host.DesignateBackupAsync("Greece", babylon.PlayerKey));
        await successor.Tap.WaitAsync<StandbyOffered>();
        await successor.Session.AcceptStandbyAsync(["127.0.0.1"], successorPort, HostCertificate.GetFingerprint(successorCert));
        await WaitUntilAsync(() => host.Host.Standby.State == StandbyStates.Ready, "the standby never became ready");
        await third.Tap.WaitAsync<SuccessorChanged>(it => it.Successor.Successor != null);
        // The roster the host announced shows both players online, and the host's own seat as the host's.
        await WaitUntilAsync(() => third.Session.Successor is { Roster: var roster } && roster.Count(it => it.Connected) == 3 && roster.Any(it => it.IsHost == true && it.KeyId == babylon.PlayerKey), "the roster never showed everybody");
        return new Rig { Host = host, Successor = successor, Third = third, SuccessorPort = successorPort, SuccessorCert = successorCert, Tcp = tcp };
    }

    /// <summary>Plays two turns, then Rome ends the third turn alone (mid-turn), and waits until the standby holds all of it. Returns the seq and hash at that point.</summary>
    private async Task<(long Seq, ulong Hash)> MidTurnAsync(Rig rig)
    {
        await PlayTurnsAsync(rig.Host, 2);
        var ended = await rig.Third.Session.SubmitAsync(new EndTurn()); // through the host's command path, so it is in the log
        Assert.True(ended.Accepted, ended.Code);
        var state = await StateAsync(rig.Host);
        await WaitUntilAsync(() => rig.Successor.Session.StandbyHaveSeq >= state.Seq, "the standby copy never caught up");
        return state;
    }

    private readonly List<Seat> seats = [];

    // The clients set their loss timestamp when they notice the drop; advancing the manual clock before that would let the outage start "after" the advance and never age.
    private async Task KillHostAsync(Rig rig)
    {
        await rig.Host.KillAsync();
        await WaitUntilAsync(() => seats.All(it => it.Session.State != ClientSessionState.InGame), "clients never noticed the kill");
    }

    private async Task AdvanceAsync(int seconds)
    {
        clock.AdvanceSeconds(seconds);
        await Task.Delay(100);
    }

    // ------------------------------------------------------------------ A6

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A6_KillTheHostMidTurn_TheSuccessorPromotesOnlyAfterItsLossWindowAndAQuorum(bool tcp)
    {
        var rig = await ReadyAsync(tcp ? "a6-tcp" : "a6-mem", tcp, withAi: true);
        var (seq, hash) = await MidTurnAsync(rig);

        await KillHostAsync(rig);

        // Nothing happens inside the loss window: nobody voted, nobody promoted, the successor does not even listen for votes.
        await Task.Delay(300);
        Assert.Null(rig.Successor.Promotion!.Host);
        Assert.DoesNotContain(rig.Third.Tap.All(), it => it is HostLossDetected or VoteCast);
        Assert.DoesNotContain(rig.Successor.Tap.All(), it => it is HostLossDetected);
        Assert.Empty(rig.Third.Votes);

        await AdvanceAsync(25); // beyond HostLossSeconds (20) and the reconnect grace (10)

        var vote = await rig.Third.Tap.WaitAsync<VoteCast>();
        Assert.True(vote.Accepted);
        Assert.Equal(1, vote.Epoch);
        Assert.Equal(greece.PlayerKey, vote.Candidate);
        Assert.Equal([(1, greece.PlayerKey)], rig.Third.Votes); // persisted before it was sent
        await rig.Successor.Tap.WaitAsync<HostChanged>(it => it.Epoch == 1);
        await rig.Third.Tap.WaitAsync<HostChanged>(it => it.Epoch == 1);

        // Exactly one host exists, one epoch up, and it holds exactly what the standby held.
        var promoted = rig.Successor.Promotion!.Host!;
        Assert.Null(rig.Third.Promotion);
        Assert.Equal(1, ((FileGameStore)promoted.Host.Store!).ReadMeta().HostEpoch);
        var (newSeq, newHash) = await StateAsync(promoted);
        Assert.Equal(seq, newSeq);
        Assert.Equal(hash, newHash);
        Assert.DoesNotContain(rig.Third.Tap.All(), it => it is HostRolledBack);
        Assert.Contains(rig.Third.Proofs, it => it.Epoch == 1 && it.KeyId == greece.PlayerKey);
        Assert.Contains(1, rig.Third.Epochs);

        // Both clients are seated on the new host with the views it would give them (hashes agree).
        foreach (var (seat, civ) in new[] { (rig.Successor, "Greece"), (rig.Third, "Rome") })
        {
            await WaitUntilAsync(() => promoted.Host.Sessions.Values.Any(it => it.BoundCivId == civ && it.PreviousSnapshot != null && seat.Session.Game.ViewHash == it.PreviousSnapshot.ViewHash), $"{civ} did not resume on the new host");
        }

        // The crashed host's seat dropped (T267 grace and K rules, not a clean leave): not connected, nothing queued as a departure.
        Assert.False(promoted.Host.TurnTimer.IsConnected("Babylon"));
        Assert.False(promoted.Host.TurnTimer.HasPendingTakeover("Babylon"));

        // The game goes on: the log continues without a gap.
        await PlayTurnsAsync(promoted, 1);
        var (afterSeq, _) = await StateAsync(promoted);
        Assert.True(afterSeq > seq);
        await WaitUntilAsync(() => rig.Third.Session.Game.Seq == afterSeq, "the follower did not get the new host's deltas");
    }

    // ------------------------------------------------------------------ A7

    [Fact]
    public async Task A7_AHostThatStillReachesAMajority_IsNeverReplaced_EvenWhenTheSuccessorLosesIt()
    {
        var rig = await ReadyAsync("a7-majority", tcp: false);
        await PlayTurnsAsync(rig.Host, 1);

        // Only the successor is cut off (the host still serves Rome): the electorate is Greece and Rome, so Greece alone is no quorum.
        rig.Successor.Partitioned = true;
        var seatOfGreece = rig.Host.Host.Sessions.Values.First(it => it.BoundCivId == "Greece");
        await seatOfGreece.Transport.CloseAsync();
        await AdvanceAsync(25);
        await rig.Successor.Tap.WaitAsync<HostLossDetected>();
        Assert.NotNull(rig.Successor.Session.SecondsToGiveUp); // T346: the overlay's "giving up in m:ss" counts the election give-up
        Assert.True(rig.Successor.Session.SecondsToGiveUp > 0);
        await WaitUntilAsync(() => rig.Successor.Election!.Tally is { VoteCount: 1, Quorum: 2 }, "the successor never opened its cycle");
        await AdvanceAsync(50); // several polls, even a restarted cycle: still nothing
        await AdvanceAsync(50);
        Assert.Null(rig.Successor.Promotion!.Host);
        Assert.DoesNotContain(rig.Third.Tap.All(), it => it is HostLossDetected or VoteCast or HostChanged);
        await PlayTurnsAsync(rig.Host, 1); // and keeps serving the majority
    }

    [Fact]
    public async Task A7_APlayerVotesForOneCandidatePerEpoch_SoAClaimForAnotherCandidateGetsNothing()
    {
        // Rome already voted for somebody else at epoch 1 (the persisted record of an earlier outage): it never votes for Greece at that epoch.
        var rig = await ReadyAsync("a7-onevote", tcp: false, romeVotedEpoch: 1, romeVotedCandidate: "someone-else");
        await PlayTurnsAsync(rig.Host, 1);
        await KillHostAsync(rig);
        await AdvanceAsync(25);
        await rig.Third.Tap.WaitAsync<HostLossDetected>();
        await rig.Successor.Tap.WaitAsync<HostLossDetected>();
        await WaitUntilAsync(() => rig.Successor.Election!.Tally is { Quorum: 2 }, "no cycle");
        await Task.Delay(400);
        Assert.Null(rig.Successor.Promotion!.Host);
        Assert.DoesNotContain(rig.Third.Tap.All(), it => it is VoteCast);
        Assert.Empty(rig.Third.Votes);
        Assert.Equal(1, rig.Successor.Election!.Tally!.VoteCount); // only the successor's own
    }

    [Fact]
    public async Task A7_TheElectionEndpoint_RefusesEarlyForgedReplayedAndMisaddressedVotes()
    {
        var rig = await ReadyAsync("a7-endpoint", tcp: false);
        await PlayTurnsAsync(rig.Host, 1);
        var election = rig.Successor.Election!;
        using var stranger = ClientIdentity.Generate();

        // Before the successor's own loss window there is no cycle: not-host-yet.
        Assert.Equal(ErrorCodes.NotHostYet, ((Error)await ProbeAsync(election)).Code);

        await KillHostAsync(rig);
        rig.Third.Partitioned = true; // Rome's own session is not what is tested here; it must not vote on its own
        rig.Third.OnEndpointCall = _ => throw new IOException("Rome does not vote in this test");
        await AdvanceAsync(25);
        await WaitUntilAsync(() => election.Tally != null, "no cycle");
        var fingerprint = HostCertificate.GetFingerprint(rig.SuccessorCert);

        // A stranger's valid signature, a vote for another epoch, another game, another candidate: all refused with the one code.
        var cases = new Func<ElectionChallenge, HostLossVote>[]
        {
            c => stranger.SignLossVote(c.GameId, c.Epoch, greece.PlayerKey, 5, c.Nonce),
            c => rome.SignLossVote(c.GameId, c.Epoch + 1, greece.PlayerKey, 5, c.Nonce),
            c => rome.SignLossVote("other-game", c.Epoch, greece.PlayerKey, 5, c.Nonce),
            c => rome.SignLossVote(c.GameId, c.Epoch, rome.PlayerKey, 5, c.Nonce),
            c => rome.SignLossVote(c.GameId, c.Epoch, greece.PlayerKey, 5, "an-old-nonce"),
            c => babylon.SignLossVote(c.GameId, c.Epoch, greece.PlayerKey, 5, c.Nonce), // the crashed host's own seat
        };
        foreach (var make in cases)
        {
            var (challenge, result) = await VoteAsync(election, make);
            Assert.Equal(fingerprint, challenge.HostKeyFingerprint, ignoreCase: true);
            Assert.False(result.Accepted);
            Assert.Equal(ErrorCodes.VoteRefused, result.Code);
        }
        Assert.Equal(1, election.Tally!.VoteCount);
        Assert.Null(rig.Successor.Promotion!.Host);

        // A good vote counts once and then is spent: replaying the very same frame is refused. (Counted votes reach the quorum here, so the check is on a fresh tally copy through the replay.)
        HostLossVote? captured = null;
        var (_, ok) = await VoteAsync(election, c => captured = rome.SignLossVote(c.GameId, c.Epoch, greece.PlayerKey, 5, c.Nonce));
        Assert.True(ok.Accepted);
        var replay = await ReplayAsync(election, captured!);
        Assert.Contains(replay, new[] { ErrorCodes.VoteRefused, ErrorCodes.NotHostYet });
    }

    private static async Task<Message> ProbeAsync(SuccessorElection election)
    {
        var (client, server) = InMemoryTransport.CreatePair();
        _ = election.AdmitAsync(server);
        await client.SendAsync(ProtocolCodec.Encode(Hello()));
        return ProtocolCodec.Decode((await client.ReceiveAsync(CancellationToken.None))!);
    }

    private static Hello Hello() => new() { Protocol = ProtocolVersion.Current, ClientVersion = "t", RulesetHash = "", ModList = [] };

    private static async Task<(ElectionChallenge Challenge, HostLossVoteResult Result)> VoteAsync(SuccessorElection election, Func<ElectionChallenge, HostLossVote> make)
    {
        var (client, server) = InMemoryTransport.CreatePair();
        _ = election.AdmitAsync(server);
        await client.SendAsync(ProtocolCodec.Encode(Hello()));
        var challenge = (ElectionChallenge)ProtocolCodec.Decode((await client.ReceiveAsync(CancellationToken.None))!);
        await client.SendAsync(ProtocolCodec.Encode(make(challenge)));
        return (challenge, (HostLossVoteResult)ProtocolCodec.Decode((await client.ReceiveAsync(CancellationToken.None))!));
    }

    // The same vote frame sent on a fresh connection, after the nonce it carries was used up.
    private static async Task<string?> ReplayAsync(SuccessorElection election, HostLossVote vote)
    {
        var (client, server) = InMemoryTransport.CreatePair();
        _ = election.AdmitAsync(server);
        await client.SendAsync(ProtocolCodec.Encode(Hello()));
        var first = ProtocolCodec.Decode((await client.ReceiveAsync(CancellationToken.None))!);
        if (first is Error error) return error.Code; // the counted vote reached quorum and the election is already over: nothing is left to replay into
        await client.SendAsync(ProtocolCodec.Encode(vote));
        return ((HostLossVoteResult)ProtocolCodec.Decode((await client.ReceiveAsync(CancellationToken.None))!)).Code;
    }

    [Fact]
    public async Task A7_AReturningOldHost_IsSupersededAndFenced_ItsPlayersAreToldAndItRefusesJoins()
    {
        var rig = await ReadyAsync("a7-return", tcp: true);
        await MidTurnAsync(rig);
        await KillHostAsync(rig);
        await AdvanceAsync(25);
        await rig.Third.Tap.WaitAsync<HostChanged>(it => it.Epoch == 1);
        var proof = rig.Third.Proofs.Single(it => it.Epoch == 1);

        // The old host process is restarted from its own directory by mistake and is reachable again.
        var returned = Track(InProcessHost.ResumeFromDirectory(rig.Host.Directory, babylon.PlayerKey, new InProcessHostOptions { TickInterval = null }));
        returned.Listen(NewCertificate(), 0);
        var superseded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        returned.Superseded += superseded.SetResult;
        Assert.False(returned.IsSuperseded);

        // A player of the old game that did not hear about the take-over is welcome: the old host is not yet aware.
        var stillThere = await PlainJoinAsync(returned, rome, knownEpoch: 0, proof: null);
        Assert.True(stillThere.Joined, "the unaware old host refused a plain join");

        // A claim whose proof does not verify does not fence the host; the client still refuses to play on a host below the epoch it knows (the host would admit it).
        var forged = proof with { Signature = proof.Signature[..^2] + (proof.Signature.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA") };
        var bogus = await PlainJoinAsync(returned, rome, knownEpoch: 1, proof: forged, expectRefusal: true);
        Assert.Equal(ClientCodes.StaleEpoch, bogus.Code);
        Assert.False(returned.IsSuperseded);

        // A player that followed the promotion (it holds the proof) reaches the old host: it is superseded for good, its players are told stale-epoch, joins are refused.
        var claim = await PlainJoinAsync(returned, rome, knownEpoch: 1, proof: proof, expectRefusal: true);
        Assert.Equal(JoinRefusedCodes.StaleEpoch, claim.Code);
        await WaitUntilAsync(() => superseded.Task.IsCompleted, "the old host never became superseded");
        Assert.Equal(1, await superseded.Task);
        Assert.True(returned.IsSuperseded);
        var meta = ((FileGameStore)returned.Host.Store!).ReadMeta();
        Assert.Equal(1, meta.SeenEpoch);
        Assert.Equal(0, meta.HostEpoch);
        var late = await PlainJoinAsync(returned, rome, knownEpoch: 0, proof: null, expectRefusal: true);
        Assert.Equal(JoinRefusedCodes.StaleEpoch, late.Code);
        await WaitUntilAsync(() => returned.Host.Sessions.Count == 0, "the superseded host still holds sessions");
    }

    private async Task<JoinResult> PlainJoinAsync(InProcessHost host, ClientIdentity identity, int knownEpoch, PromotionProof? proof, bool expectRefusal = false)
    {
        var session = new ClientSession(token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", host.Port!.Value, cancellationToken: token), new ClientSessionOptions
        {
            Identity = identity,
            DisplayName = "x",
            RulesetHash = "",
            ExpectedFingerprint = host.Fingerprint,
            HostAddress = $"127.0.0.1:{host.Port}",
            KnownHosts = new KnownHosts(),
            GameId = host.GameId,
            KnownEpoch = knownEpoch,
            EpochProof = proof,
            ReconnectDelay = (_, _) => Task.Delay(30),
            KeepAliveInterval = Timeout.InfiniteTimeSpan,
        });
        Track(session);
        var result = await session.JoinAsync();
        Assert.Equal(expectRefusal, !result.Joined);
        return result;
    }

    [Fact]
    public async Task A7_TakeOverNowOnAHealthyHost_IsRefusedByClientsThatStillReachIt()
    {
        var rig = await ReadyAsync("a7-forced", tcp: false);
        await PlayTurnsAsync(rig.Host, 1);
        var (seq, _) = await StateAsync(rig.Host);
        await WaitUntilAsync(() => rig.Successor.Session.StandbyHaveSeq >= seq, "the standby copy never caught up");

        // Rome's link is cut and she cannot reach the host for a while (a partition), but the host is fine and the successor takes over by force.
        rig.Third.Partitioned = true;
        await rig.Host.Host.Sessions.Values.First(it => it.BoundCivId == "Rome").Transport.CloseAsync();
        Assert.True(await rig.Successor.Session.TakeOverNowAsync());
        await rig.Successor.Tap.WaitAsync<HostChanged>(it => it.Epoch == 1);
        var promoted = rig.Successor.Promotion!.Host!;
        Assert.Equal(1, ((FileGameStore)promoted.Host.Store!).ReadMeta().HostEpoch);

        // The partition heals just as Rome meets the new host: she can reach the old one, so she refuses the new epoch and stays.
        rig.Third.OnEndpointCall = call =>
        {
            if (call >= 2) rig.Third.Partitioned = false;
        };
        await AdvanceAsync(25);
        await rig.Third.Tap.WaitAsync<ProtocolError>(it => it.Code == ClientCodes.HostStillAlive);
        await WaitUntilAsync(() => rig.Host.Host.Sessions.Values.Any(it => it.BoundCivId == "Rome"), "Rome did not go back to the host she can reach");
        Assert.DoesNotContain(rig.Third.Tap.All(), it => it is HostChanged);
        Assert.DoesNotContain(promoted.Host.Sessions.Values, it => it.BoundCivId == "Rome");

        // The healthy host keeps its majority.
        await PlayTurnsAsync(rig.Host, 1);
    }

    // ------------------------------------------------------------------ A8

    [Fact]
    public async Task A6_ASuccessorWhoseStandbyCopyIsGone_ReportsItCannotPromote_AndDoesNotRetry()
    {
        var rig = await ReadyAsync("a6-nocopy", tcp: false);
        await MidTurnAsync(rig);
        await KillHostAsync(rig);
        Directory.Delete(Path.Combine(rig.Successor.DataRoot!, "standby"), true);
        await AdvanceAsync(25);
        var error = await rig.Successor.Tap.WaitAsync<ProtocolError>(it => it.Code == ClientCodes.BadPromotion);
        Assert.Equal(ClientCodes.BadPromotion, error.Code);
        Assert.Null(rig.Successor.Promotion!.Host);
    }

    // T282 hand-off: the successor keeps a StandbyListener on the announced port; the election takes the port for the vote and gives it to the promoted host, or back to a standby listener when the host returned.
    private static async Task<string?> ProbeAsync(int port) =>
        await new TlsEndpointProber().PresentedFingerprintAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None);

    [Fact]
    public async Task A6_TheElectionTakesThePortFromTheStandbyListener_AndAHostThatReturnsGetsItBackAsAStandbyListener()
    {
        var rig = await ReadyAsync("a6-standby-back", tcp: true);
        var fingerprint = HostCertificate.GetFingerprint(rig.SuccessorCert);
        rig.Successor.Promotion!.Listener = Track(new StandbyListener(rig.SuccessorCert, rig.SuccessorPort));
        rig.Successor.Promotion.Listener!.Start();
        Assert.True(HostKey.Matches(await ProbeAsync(rig.SuccessorPort), fingerprint));
        var info = rig.Successor.Session.Successor!;
        var election = rig.Successor.Election!;

        using var cancel = new CancellationTokenSource();
        var running = election.Handler(new HostLossInfo(rig.Host.GameId, 0, info.Successor!, info.Roster, true, 0, false), cancel.Token);
        await WaitUntilAsync(() => election.Tally != null, "the election never started");
        Assert.Null(rig.Successor.Promotion.Host);
        await cancel.CancelAsync(); // the old host answered again
        Assert.Null(await running);

        Assert.True(HostKey.Matches(await ProbeAsync(rig.SuccessorPort), fingerprint), "the port was not given back to a standby listener");
        Assert.NotNull(rig.Successor.Promotion.Listener);
    }

    [Fact]
    public async Task A6_TheElectionWithAStandbyListenerOnThePort_PromotesAHostThatListensOnThatPort()
    {
        var rig = await ReadyAsync("a6-standby-port", tcp: true);
        rig.Successor.Promotion!.Listener = Track(new StandbyListener(rig.SuccessorCert, rig.SuccessorPort));
        rig.Successor.Promotion.Listener!.Start();
        await MidTurnAsync(rig);
        await KillHostAsync(rig);
        await AdvanceAsync(25);
        await rig.Third.Tap.WaitAsync<HostChanged>(it => it.Epoch == 1);
        var promoted = rig.Successor.Promotion.Host!;
        Assert.Equal(rig.SuccessorPort, promoted.Port);
    }

    [Fact]
    public async Task A8_HostAndSuccessorBothGone_ClientsGiveUp_AndTheStandbyDirectoryResumesAtTheNextEpoch()
    {
        var rig = await ReadyAsync("a8", tcp: true);
        var (seq, hash) = await MidTurnAsync(rig);
        var dataRoot = rig.Successor.DataRoot!;

        // The host crashes and so does the successor's machine (its session ends and its listener closes).
        await KillHostAsync(rig);
        await rig.Successor.Session.DisposeAsync();
        await rig.Successor.Election!.DisposeAsync();

        await AdvanceAsync(25);
        await rig.Third.Tap.WaitAsync<HostLossDetected>();
        await AdvanceAsync(700); // beyond the give-up time (600 s)
        await rig.Third.Tap.WaitAsync<ElectionGaveUp>();
        var ended = await rig.Third.Tap.WaitAsync<SessionEnded>();
        Assert.Equal(ClientCodes.GaveUp, ended.Reason);

        // The manual hand-off: "Host saved game" on the standby directory resumes at epoch + 1.
        var directory = StandbyStore.DirectoryFor(dataRoot, rig.Host.GameId)!;
        var manual = new StandbyPromotion(dataRoot, greece, rig.SuccessorCert, new InProcessHostOptions { TickInterval = null });
        var resumed = Track(manual.TakeOverFromDisk(rig.Host.GameId, FreePort()));
        Assert.Equal(directory, resumed.Directory);
        var meta = ((FileGameStore)resumed.Host.Store!).ReadMeta();
        Assert.Equal(1, meta.HostEpoch);
        Assert.Equal(greece.PlayerKey, meta.Promotion?.KeyId);
        var (resumedSeq, resumedHash) = await StateAsync(resumed);
        Assert.Equal(seq, resumedSeq);
        Assert.Equal(hash, resumedHash);

        // A game that was never replicated here, a copy that no longer opens, and a port that is taken are refused with a HostingException, not a crash.
        Assert.Throws<HostingException>(() => manual.TakeOverFromDisk("no-such-game", FreePort()));
        var broken = Path.Combine(root, "broken");
        CopyDirectory(Path.Combine(dataRoot, "standby"), Path.Combine(broken, "standby"));
        File.WriteAllText(Path.Combine(StandbyStore.DirectoryFor(broken, rig.Host.GameId)!, "game.json"), "{");
        var brokenPromotion = new StandbyPromotion(broken, greece, rig.SuccessorCert, new InProcessHostOptions { TickInterval = null });
        Assert.Throws<HostingException>(() => brokenPromotion.TakeOverFromDisk(rig.Host.GameId, FreePort()));
        using var taken = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        taken.Start();
        var busyPort = ((System.Net.IPEndPoint)taken.LocalEndpoint).Port;
        Assert.Throws<HostingException>(() => new StandbyPromotion(dataRoot, greece, rig.SuccessorCert, new InProcessHostOptions { TickInterval = null }).TakeOverFromDisk(rig.Host.GameId, busyPort));
    }

    // ------------------------------------------------------------------ T401: relay-only successor in crash election

    [Fact]
    public async Task T401_RelayOnlySuccessor_WinsCrashElectionThroughItsRelayRoom_AndAdoptsTheRoomOnPromotion()
    {
        var relayRig = Track(await RelayRig.StartAsync(RelayMode.SelfSigned, o => o with { PublicNames = [RelayRig.PublicName, "127.0.0.1"] }));
        var relayEndpoint = new RelayEndpoint("127.0.0.1", relayRig.Port, relayRig.PinnedFingerprint);
        var listenerOptions = new RelayListenerOptions { MinBackoff = TimeSpan.FromMilliseconds(50), MaxBackoff = TimeSpan.FromMilliseconds(200) };
        var hostRelaySettings = new RelaySettings { Url = relayEndpoint.ToString() };
        var voterRelaySettings = new RelaySettings { Url = relayEndpoint.ToString() };

        var successorCert = NewCertificate();
        var successorFp = HostCertificate.GetFingerprint(successorCert);
        var host = StartHost("t401-relay", successorFp, withAi: false);
        host.Host.Standby.Prober = new TlsEndpointProber(hostRelaySettings.Agreed);
        host.Listen(NewCertificate(), 0);
        var hostPlayer = await host.ConnectHostPlayerAsync();
        _ = Task.Run(async () =>
        {
            try { while (await hostPlayer.ReceiveAsync(CancellationToken.None) != null) { } }
            catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException) { }
        });

        var hostOptions = new InProcessHostOptions { TickInterval = null, Store = new StoreOptions { SnapshotEveryTurns = 2 } };
        var promotion = new StandbyPromotion(Path.Combine(root, "t401-greece"), greece, successorCert, hostOptions);
        var election = Track(new SuccessorElection(promotion, successorCert, clock));
        var relay = new RelayListener(relayEndpoint, successorCert, host.GameId, listenerOptions);
        var standby = Track(new StandbyListener(successorCert, null, relay: relay));
        promotion.Listener = standby;
        standby.Start();
        using (var roomWait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await relay.WaitForRoomAsync(roomWait.Token);
        var route = RelayRoutes.Of(relayEndpoint, relay.Room!);

        var successorSession = Track(new ClientSession(
            token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", host.Port!.Value, cancellationToken: token),
            new ClientSessionOptions
            {
                Identity = greece,
                DisplayName = "Friend",
                RulesetHash = "",
                ExpectedFingerprint = host.Fingerprint,
                HostAddress = $"127.0.0.1:{host.Port}",
                KnownHosts = new KnownHosts(),
                GameId = host.GameId,
                StandbyRoot = Path.Combine(root, "t401-greece"),
                ReconnectDelay = (_, _) => Task.Delay(30),
                ElectionPoll = TimeSpan.FromMilliseconds(30),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
                HostSilenceTimeout = Timeout.InfiniteTimeSpan,
                Clock = clock,
                OnHandoff = (handoff, token) => promotion.Handler(handoff, token),
                OnHostLost = (info, token) => election.Handler(info, token),
            }));
        var successorTap = Track(new EventTap(successorSession));
        Assert.True((await successorSession.JoinAsync()).Joined);

        var voterSession = Track(new ClientSession(
            token => TcpTlsClientTransport.ConnectAsync("127.0.0.1", host.Port!.Value, cancellationToken: token),
            new ClientSessionOptions
            {
                Identity = rome,
                DisplayName = "Third",
                RulesetHash = "",
                ExpectedFingerprint = host.Fingerprint,
                HostAddress = $"127.0.0.1:{host.Port}",
                KnownHosts = new KnownHosts(),
                GameId = host.GameId,
                ReconnectDelay = (_, _) => Task.Delay(30),
                ElectionPoll = TimeSpan.FromMilliseconds(30),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
                HostSilenceTimeout = Timeout.InfiniteTimeSpan,
                Clock = clock,
                ConfirmRelay = r => voterRelaySettings.ConfirmAsync(r, _ => Task.FromResult(true)),
                ConnectRelay = voterRelaySettings.ConnectAsync,
            }));
        var voterTap = Track(new EventTap(voterSession));
        Assert.True((await voterSession.JoinAsync()).Joined);

        // Designate and accept as relay-only (no TCP endpoints).
        Assert.Null(await host.Host.DesignateBackupAsync("Greece", babylon.PlayerKey));
        await successorTap.WaitAsync<StandbyOffered>();
        await successorSession.AcceptStandbyAsync([], 0, successorFp, route);
        await WaitUntilAsync(() => string.Equals(host.Host.Standby.State, StandbyStates.Ready, StringComparison.Ordinal), "relay standby never became ready");
        await WaitUntilAsync(() => voterSession.Successor?.Successor?.Relay != null, "voter never received relay successor");

        await PlayTurnsAsync(host, 1);
        var (seq, hash) = await StateAsync(host);
        await WaitUntilAsync(() => successorSession.StandbyHaveSeq >= seq, "standby copy never caught up");

        // Kill the host; the relay-only successor serves ElectionChallenge/HostLossVote on its standby RelayListener, reaches quorum, and adopts the room.
        await host.KillAsync();
        await WaitUntilAsync(() => successorSession.State != ClientSessionState.InGame && voterSession.State != ClientSessionState.InGame, "clients never noticed the kill");
        await AdvanceAsync(25);
        var vote = await voterTap.WaitAsync<VoteCast>(it => it.Epoch == 1);
        Assert.True(vote.Accepted);
        await voterTap.WaitAsync<HostChanged>(it => it.Epoch == 1);
        await successorTap.WaitAsync<HostChanged>(it => it.Epoch == 1);

        Assert.Null(standby.Relay); // detached only at promotion
        var promoted = promotion.Host!;
        Assert.Equal((seq, hash), await StateAsync(promoted));
        await PlayTurnsAsync(promoted, 1);
    }

    [Fact]
    public async Task T401_RelayOnlySuccessor_HostReturnsDuringElection_ResumesAnsweringStandbyProbes()
    {
        var relayRig = Track(await RelayRig.StartAsync(RelayMode.SelfSigned, o => o with { PublicNames = [RelayRig.PublicName, "127.0.0.1"] }));
        var relayEndpoint = new RelayEndpoint("127.0.0.1", relayRig.Port, relayRig.PinnedFingerprint);
        var listenerOptions = new RelayListenerOptions { MinBackoff = TimeSpan.FromMilliseconds(50), MaxBackoff = TimeSpan.FromMilliseconds(200) };
        var proberSettings = new RelaySettings { Url = relayEndpoint.ToString() };

        var successorCert = NewCertificate();
        var successorFp = HostCertificate.GetFingerprint(successorCert);
        var promotion = new StandbyPromotion(Path.Combine(root, "t401-back"), greece, successorCert, new InProcessHostOptions { TickInterval = null });
        var election = Track(new SuccessorElection(promotion, successorCert, clock));
        var relay = new RelayListener(relayEndpoint, successorCert, "game-back", listenerOptions);
        var standby = Track(new StandbyListener(successorCert, null, relay: relay));
        promotion.Listener = standby;
        standby.Start();
        using (var roomWait = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await relay.WaitForRoomAsync(roomWait.Token);
        var route = RelayRoutes.Of(relayEndpoint, relay.Room!);

        IReadOnlyList<SuccessorSeat> roster =
        [
            new() { KeyId = babylon.PlayerKey, Connected = true, IsHost = true },
            new() { KeyId = greece.PlayerKey, Connected = true },
            new() { KeyId = rome.PlayerKey, Connected = true },
        ];
        var successorInfo = new SuccessorInfo { KeyId = greece.PlayerKey, Endpoints = [], Fingerprint = successorFp, Relay = route };
        using var cancel = new CancellationTokenSource();
        var running = election.Handler(new HostLossInfo("game-back", 0, successorInfo, roster, true, 0, false), cancel.Token);
        await WaitUntilAsync(() => election.Tally != null, "the relay election never started");
        Assert.NotNull(standby.Relay); // still attached while election runs
        await cancel.CancelAsync();
        Assert.Null(await running);

        Assert.NotNull(standby.Relay); // never detached
        var probed = await new TlsEndpointProber(proberSettings.Agreed).PresentedFingerprintViaRelayAsync(route, successorFp, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(HostKey.Matches(probed, successorFp));
    }

    [Fact]
    public async Task T401_RelayOnlySuccessor_VoterDecliningRelay_NeverDialsIt()
    {
        var successorCert = NewCertificate();
        var successorFp = HostCertificate.GetFingerprint(successorCert);
        var declinedRoute = new RelayRoute { Relay = "127.0.0.1:47830", Room = "K7QF-3MXP4A" };
        var host = StartHost("t401-decline", successorFp, withAi: false);
        host.Host.Standby.Prober = new FakeProber { RelayAnswer = _ => successorFp };
        var hostPlayer = await host.ConnectHostPlayerAsync();
        _ = Task.Run(async () =>
        {
            try { while (await hostPlayer.ReceiveAsync(CancellationToken.None) != null) { } }
            catch (Exception e) when (e is InvalidOperationException or IOException or ObjectDisposedException) { }
        });

        var successor = await JoinAsync(host, greece, "Friend", tcp: false, Path.Combine(root, "t401-decline-succ"), successorCert);
        var dialed = 0;
        var asked = 0;
        var voter = Track(new ClientSession(
            _ => Task.FromResult<ITransport>(host.ConnectLoopback()),
            new ClientSessionOptions
            {
                Identity = rome,
                DisplayName = "Third",
                RulesetHash = "",
                AllowUnpinnedHost = true,
                HostAddress = "memory",
                GameId = host.GameId,
                ReconnectDelay = (_, _) => Task.Delay(30),
                ElectionPoll = TimeSpan.FromMilliseconds(30),
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
                HostSilenceTimeout = Timeout.InfiniteTimeSpan,
                Clock = clock,
                ConfirmRelay = _ => { Interlocked.Increment(ref asked); return Task.FromResult(false); },
                ConnectRelay = (_, _, _) => { Interlocked.Increment(ref dialed); return Task.FromException<ITransport>(new IOException("should not dial")); },
            }));
        Assert.True((await voter.JoinAsync()).Joined);

        Assert.Null(await host.Host.DesignateBackupAsync("Greece", babylon.PlayerKey));
        await successor.Tap.WaitAsync<StandbyOffered>();
        await successor.Session.AcceptStandbyAsync([], 0, successorFp, declinedRoute);
        await WaitUntilAsync(() => string.Equals(host.Host.Standby.State, StandbyStates.Ready, StringComparison.Ordinal), "standby never became ready");
        await WaitUntilAsync(() => voter.Successor?.Successor?.Relay != null, "voter never received relay successor");

        await host.KillAsync();
        await WaitUntilAsync(() => voter.State != ClientSessionState.InGame, "voter never noticed kill");
        await AdvanceAsync(25);
        await WaitUntilAsync(() => Volatile.Read(ref asked) > 0, "voter never asked ConfirmRelay");
        Assert.Equal(0, Volatile.Read(ref dialed));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
