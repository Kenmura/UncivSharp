using System.Threading.Channels;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Relay;
using Unciv.Net.Standby;
using Unciv.Utils;

namespace Unciv.Client.Net.Session;

public sealed partial class ClientSession
{
    /// <summary>Consents to the backup-host role offered by <see cref="StandbyOffered"/>: <paramref name="endpoints"/> are the addresses this player can be reached on, <paramref name="port"/> and <paramref name="fingerprint"/> those of its own listener. The host probes one endpoint before the standby counts as ready. T354: <paramref name="relay"/> is this player's unlisted room on a relay (<see cref="Unciv.Server.Transport.StandbyListener.Relay"/>), for a player with no reachable address: <paramref name="endpoints"/> may then be empty.</summary>
    public ValueTask AcceptStandbyAsync(IReadOnlyList<string> endpoints, int port, string fingerprint, RelayRoute? relay = null)
    {
        // From here the host's replication stream of this game may be stored under StandbyRoot: in memory for this process and as a per-game record for the next one.
        standbyConsentedGame = gameId ?? AnyGame; // a session joins one game: with its id not known yet the accept covers the game the host's next status names
        if (gameId != null && options.StandbyRoot != null) StandbyStore.RecordConsent(options.StandbyRoot, gameId);
        return SendAsync(new StandbyAccept { Endpoints = [.. endpoints], Port = port, Fingerprint = fingerprint, Relay = relay });
    }

    /// <summary>Declines the offer, or withdraws an accepted standby.</summary>
    public ValueTask DeclineStandbyAsync()
    {
        var kept = standbyGameId ?? gameId;
        EndStandbyReplication();
        if (kept != null && options.StandbyRoot != null && StandbyStore.DirectoryFor(options.StandbyRoot, kept) is { } path)
        {
            // Withdrawing consent removes the copy: a later host status must not find the directory and treat it as an earlier accept.
            try { Directory.Delete(path, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
        return SendAsync(new StandbyDecline());
    }

    /// <summary>
    /// Takes the host's announcement. It came over the authenticated host channel; one from a lower epoch than this client already knows is ignored. A reachable successor (the host names one only while its probe passes, whatever the replication state) is pinned in <see cref="KnownHosts"/>
    /// under each announced endpoint, vouched for by the host this session trusts, so a later reconnect there has no first-use prompt (design/HOST-MIGRATION.md section 3).
    /// </summary>
    private void HandleSuccessor(HostSuccessor successor)
    {
        if (successor.Epoch != connectionEpoch) return; // the connected host speaks for its own epoch only
        Successor = successor;
        if (successor.Successor is { } info && options.KnownHosts is { } known && connectedHostFingerprint.Length > 0)
        {
            var added = false;
            foreach (var endpoint in info.Endpoints.Take(8).Where(it => it.Length is > 2 and <= 260 && it[0] != '$' && it.Contains(':')))
            {
                var had = known.Pins.ContainsKey(endpoint);
                if (known.PinVouched(endpoint, info.Fingerprint, connectedHostFingerprint, successor.Epoch) && !had) added = true;
            }
            if (RelayRoutes.Normalize(info.Relay) is { } route)
            {
                var address = KnownHosts.RelayAddress(route.Relay, route.Room);
                var had = known.Pins.ContainsKey(address);
                if (known.PinVouched(address, info.Fingerprint, connectedHostFingerprint, successor.Epoch) && !had) added = true;
            }
            if (added) options.PersistKnownHosts?.Invoke(known);
        }
        Emit(new SuccessorChanged(successor));
    }

    // ------------------------------------------------------------ T272: standby replication

    private void EndStandbyReplication()
    {
        var game = standbyGameId ?? gameId;
        standbyConsentedGame = null;
        if (game != null && options.StandbyRoot != null) StandbyStore.ClearConsent(options.StandbyRoot, game); // the designation ended: the copy stays, but nothing is stored into it until a new accept
        CloseStandbyStore();
    }

    private void CloseStandbyStore()
    {
        lock (standbyGate)
        {
            standbyWriter = null;
            standbyFrames?.Writer.TryComplete();
            standbyFrames = null;
            standbyStore?.Dispose();
            standbyStore = null;
            standbyGameId = null;
            standbyBroken = false;
            standbyRestarting = false;
            standbyRestarts = 0;
            standbyAppliedSinceReject = 0;
            standbyResubscribes = 0;
            standbyResubscribePending = false;
            standbyResubscribeDue = false;
            standbyLastAck = long.MinValue;
        }
    }

    /// <summary>
    /// Subscribes to the host's replication stream, once per connection, resuming from what the standby copy holds. Only for a player that consented to this game (in this process or by the per-game record
    /// an earlier one left), and only for a game id that is a safe directory name (it comes from the host).
    /// </summary>
    private async Task SubscribeStandbyAsync(string? statusGameId, CancellationToken token)
    {
        if (standbyStore != null || options.StandbyRoot == null) return;
        if (statusGameId != null && gameId != null && !string.Equals(statusGameId, gameId, StringComparison.Ordinal)) return; // only the game this session joined
        var id = statusGameId ?? gameId;
        if (id == null) return;
        if (StandbyStore.DirectoryFor(options.StandbyRoot, id) == null) return;
        if (string.Equals(standbyConsentedGame, AnyGame, StringComparison.Ordinal))
        {
            standbyConsentedGame = id;
            StandbyStore.RecordConsent(options.StandbyRoot, id);
        }
        if (!string.Equals(standbyConsentedGame, id, StringComparison.Ordinal) && !StandbyStore.HasConsent(options.StandbyRoot, id)) return;
        StandbyStore opened;
        try
        {
            opened = StandbyStore.Open(options.StandbyRoot, id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Emit(new StandbyRejected("store-unavailable"));
            return;
        }
        long have;
        string? haveSha;
        lock (standbyGate)
        {
            standbyStore = opened;
            standbyGameId = id;
            standbyFrames = Channel.CreateBounded<Message>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            standbyWriter = standbyFrames.Writer;
            _ = Task.Run(() => RunStandbyWorkerAsync(opened, standbyFrames.Reader, token), CancellationToken.None);
            have = opened.HaveSeq;
            haveSha = opened.HaveLineSha256;
        }
        Interlocked.Exchange(ref standbyHave, have);
        await SendAsync(new StandbySubscribe { GameId = id, HaveSeq = have, HaveLineSha256 = haveSha }, token).ConfigureAwait(false);
    }

    // Receive loop: hands the frame to the worker (bounded: a disk that cannot keep up pauses reading, and the host's pacing then stops sending).
    private async Task HandleStandbyFrameAsync(Message frame, CancellationToken token)
    {
        var writer = standbyWriter; // null: never subscribed on this connection (a host cannot push a copy onto a player who did not ask); a broken copy is checked by the worker
        if (writer == null) return;
        var epoch = frame switch { StandbyMeta m => m.Epoch, StandbySnapshot p => p.Epoch, StandbyEntry e => e.Epoch, _ => -1 };
        if (epoch != connectionEpoch) return;
        if (await writer.WaitToWriteAsync(token).ConfigureAwait(false)) writer.TryWrite(frame); // a copy closed while the frame was in flight just drops it
    }

    private async Task RunStandbyWorkerAsync(StandbyStore store, ChannelReader<Message> reader, CancellationToken token)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(token).ConfigureAwait(false))
                await ApplyStandbyFrameAsync(store, frame, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (!IsConnectionGone(e)) { MarkStandbyBroken(); }
        catch (Exception) { /* the connection is gone: the next one subscribes anew */ }
    }

    private void MarkStandbyBroken()
    {
        lock (standbyGate)
        {
            standbyBroken = true; // the disk cannot take the copy: nothing further is acked, the host's stream turns lagging
            standbyWriter = null; // and nothing more is queued: no worker may be left behind a full channel
            standbyFrames?.Writer.TryComplete();
        }
        Emit(new StandbyRejected("store-io"));
    }

    private async Task ApplyStandbyFrameAsync(StandbyStore store, Message frame, CancellationToken token)
    {
        string? emit = null;
        var restartRequest = false;
        long? ack = null;
        string? id;
        lock (standbyGate)
        {
            if (!ReferenceEquals(standbyStore, store) || standbyBroken) return;
            id = standbyGameId;
            // After a restart request the frames already on the wire belong to the old stream: they are skipped up to the new stream's first frame (the meta with the log header).
            if (standbyRestarting)
            {
                if (frame is not StandbyMeta { Header: not null }) return;
                standbyRestarting = false;
            }

            StandbyApply result;
            try
            {
                result = frame switch
                {
                    StandbyMeta m => store.Apply(m),
                    StandbySnapshot p => store.Apply(p),
                    _ => store.Apply((StandbyEntry)frame), // HandleStandbyFrameAsync only queues the three frame types
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                MarkStandbyBroken(); // (the lock is reentrant)
                return;
            }

            if (result == StandbyApply.Rejected)
            {
                emit = store.LastRejection ?? "rejected";
                standbyAppliedSinceReject = 0;
                if (++standbyRestarts > 3)
                {
                    standbyBroken = true;
                }
                else
                {
                    standbyRestarting = true;
                    standbyLastAck = long.MinValue;
                    restartRequest = true;
                }
            }
            else
            {
                Interlocked.Exchange(ref standbyHave, store.HaveSeq);
                // Acked: every stored log line, and a snapshot that restarted the store (the host ignores acks until the one for that snapshot's seq).
                var resetDone = frame is StandbySnapshot { Reset: true } && result == StandbyApply.Applied;
                if (resetDone)
                {
                    standbyRestarts = 0; // a restart that worked
                    standbyAppliedSinceReject = 0;
                    standbyResubscribes = 0;
                }
                if (frame is StandbyEntry && result == StandbyApply.Applied)
                {
                    standbyResubscribes = 0;
                    if (++standbyAppliedSinceReject >= 100) standbyRestarts = 0; // the counter decays: isolated rejections over a long healthy stream must not add up to a permanent stop
                }
                if (resetDone || (frame is StandbyEntry && result == StandbyApply.Applied && store.HaveSeq != standbyLastAck))
                {
                    standbyLastAck = store.HaveSeq;
                    ack = store.HaveSeq;
                }
            }
        }

        if (emit != null) Emit(new StandbyRejected(emit));
        if (restartRequest) await SendAsync(new StandbySubscribe { GameId = id.NotNull(), HaveSeq = -1 }, token).ConfigureAwait(false);
        else if (ack != null) await SendAsync(new StandbyAck { Seq = ack.Value }, token).ConfigureAwait(false);
    }

    // T292: the host refused a subscribe (rate) or ended the stream (a violation, its store failing). The subscribe is repeated after a growing delay, a bounded number of times per connection.
    // The delay runs off the receive loop; when it ends a Ping makes the host's Pong wake the loop, which then does the resubscribe (so the store is only ever touched from the loop and the worker).
    private void ScheduleStandbyResubscribe(CancellationToken token)
    {
        int attempt;
        lock (standbyGate)
        {
            if (standbyStore == null || standbyBroken || standbyResubscribePending || standbyResubscribes >= options.MaxStandbyResubscribes) return;
            standbyResubscribePending = true;
            attempt = ++standbyResubscribes;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await options.StandbyResubscribeDelay(attempt, token).ConfigureAwait(false);
                standbyResubscribeDue = true;
                await SendAsync(new Ping(), token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException || IsConnectionGone(e)) { /* the connection ended during the delay */ }
        }, CancellationToken.None);
    }

    private async Task ResubscribeStandbyAsync(CancellationToken token)
    {
        StandbySubscribe? subscribe = null;
        lock (standbyGate)
        {
            standbyResubscribeDue = false;
            standbyResubscribePending = false;
            if (standbyStore is { } store && !standbyBroken && standbyGameId != null)
            {
                subscribe = standbyRestarting
                    ? new StandbySubscribe { GameId = standbyGameId, HaveSeq = -1 }
                    : new StandbySubscribe { GameId = standbyGameId, HaveSeq = store.HaveSeq, HaveLineSha256 = store.HaveLineSha256 };
            }
        }
        if (subscribe != null) await SendAsync(subscribe, token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ T274: crash election

    private bool IsSuccessorSelf() =>
        options.OnHostLost != null && Successor?.Successor is { } info && string.Equals(info.KeyId, options.Identity.PlayerKey, StringComparison.Ordinal);

    // An election runs while a reachable successor is announced (the host names one only after its probe passed) and this session can take part (vote through ConnectEndpoint, or be the successor with OnHostLost);
    // an election redirect that has not joined yet also stays in it. After a joined redirect the session is an ordinary client of its new host (a second election needs a new designation, not supported).
    private bool ElectionMode()
    {
        if (electionDisabled || !everJoined || gameId == null) return false;
        if (redirectIsElection && !redirectAnnounced) return true;
        if (redirect != null || (!gameStarted && Game.Snapshot == null)) return false;
        return Successor?.Successor is { } successor && (IsSuccessorSelf() || CanDial(successor));
    }

    private HostLossInfo BuildLossInfo(SuccessorInfo info, bool forced) => new(
        gameId.NotNull(), connectionEpoch, info, Successor?.Roster ?? [],
        clock.NowMillis - lastFrameAt < options.Election.RosterMaxAgeSeconds * 1000L, StandbyHaveSeq, forced);

    private async Task ElectionStepAsync(CancellationToken token)
    {
        if (Successor?.Successor is not { } info || gameId == null) return;
        var forced = forceTakeOver;
        var lost = lossStart >= 0 ? lossStart : lastFrameAt;
        var window = Math.Max(options.Election.HostLossSeconds, options.Election.ReconnectGraceSeconds) * 1000L;
        if (!forced && clock.NowMillis - lost < window) return;
        if (!lossAnnounced)
        {
            lossAnnounced = true;
            Emit(new HostLossDetected(connectionEpoch));
        }
        if (IsSuccessorSelf()) await StepAsSuccessorAsync(info, forced, token).ConfigureAwait(false);
        else await VoteAsync(info, token).ConfigureAwait(false);
    }

    private async Task StepAsSuccessorAsync(SuccessorInfo info, bool forced, CancellationToken token)
    {
        if (electionTask == null)
        {
            electionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var load = BuildLossInfo(info, forced);
            var ct = electionCts.Token;
            var onHostLost = options.OnHostLost.NotNull();
            electionTask = Task.Run(() => onHostLost(load, ct), CancellationToken.None);
        }
        if (!electionTask.IsCompleted) return;
        var task = electionTask;
        electionTask = null;
        electionCts?.Dispose();
        electionCts = null;
        try
        {
            var connector = await task.ConfigureAwait(false);
            if (connector != null) StartElectionRedirect(info, connector);
        }
        catch (OperationCanceledException) { /* the host came back (or the session ended) */ }
        catch (Exception)
        {
            Emit(new ProtocolError(ClientCodes.BadPromotion)); // this process could not promote; nobody else can host the game from here
            electionDisabled = true;
            forceTakeOver = false;
        }
    }

    private void CancelElection()
    {
        lossStart = -1;
        lossAnnounced = false;
        forceTakeOver = false;
        electionCts?.Cancel();
    }

    private void StartElectionRedirect(SuccessorInfo info, Func<CancellationToken, Task<ITransport>>? connector)
    {
        redirect = new HostHandoff { GameId = gameId.NotNull(), Epoch = connectionEpoch + 1, Successor = info, FinalSeq = -1, FinalHash = "" };
        redirectIsElection = true;
        electionHumans = Successor?.Roster.Count ?? 0; // T340: read before the new host's JoinOk can replace the announcement
        redirectPrepared = true;
        redirectAnnounced = false;
        redirectConnector = connector;
        hostAddress = info.Endpoints.Count > 0 ? info.Endpoints[0] : "";
        expectedFingerprint = info.Fingerprint;
        forceTakeOver = false;
    }

    /// <summary>
    /// T346: the attempt number the reconnect loop is on (1-based), or 0 while the connection is up. The overlay shows it ("attempt 3 of 5") so the player sees that
    /// the client keeps trying; <see cref="RejoinNow"/> restarts the budget.
    /// </summary>
    public int ReconnectAttempt => reconnectAttempt;

    /// <summary>T346: how many automatic attempts one outage gets before the session ends with <c>reconnect-failed</c>.</summary>
    public int MaxReconnectAttempts => options.MaxReconnectAttempts;

    /// <summary>T346: seconds until the next automatic attempt, read on this session's own clock (the overlay must not mix it with the host's game clock); null while a dial or handshake is in flight.</summary>
    public int? SecondsToNextAttempt
    {
        get
        {
            long at = Volatile.Read(ref nextRetryAt);
            return at == long.MinValue ? null : (int)Math.Max(0L, (at - clock.NowMillis + 999) / 1000);
        }
    }

    /// <summary>T346: seconds until an election-mode outage gives up, read on this session's own clock; null without an election.</summary>
    public int? SecondsToGiveUp
    {
        get
        {
            long at = Volatile.Read(ref giveUpAt);
            return at == long.MinValue ? null : (int)Math.Max(0L, (at - clock.NowMillis + 999) / 1000);
        }
    }

    /// <summary>T346: the wait before automatic attempt <paramref name="attempt"/> (1-based): 1, 2, 4, ... capped at 30 s (the default <see cref="ClientSessionOptions.ReconnectDelay"/>).</summary>
    public static int BackoffSeconds(int attempt) => Math.Min(30, 1 << Math.Min(Math.Max(attempt, 1) - 1, 5));

    /// <summary>
    /// T346: the player pressed Rejoin. While the session is reconnecting this dials the host now instead of waiting out the current backoff (or cuts a dial in flight short and redials),
    /// and gives the attempt budget a fresh start; while it is up or over it does nothing (the overlay then offers Leave or a fresh join instead).
    /// </summary>
    public void RejoinNow()
    {
        if (state != ClientSessionState.Reconnecting) return;
        Interlocked.Exchange(ref rejoinRequested, 1);
        lock (retryGate)
        {
            retryWait?.Cancel(); // the same gate holds off disposal, so a Cancel can never hit a disposed source
            // T346: also cut a dial in flight short, so a Rejoin pressed while the handshake runs redials now instead of waiting it out.
            try { handshakeTimer?.Cancel(); } catch (ObjectDisposedException) { /* that dial already ended */ }
        }
    }

    /// <summary>T346: awaits one backoff (or election poll) and reports a Rejoin request as "skip it".</summary>
    private bool InterruptedByRejoin() => Interlocked.Exchange(ref rejoinRequested, 0) == 1;

    private async Task WaitRetryAsync(Func<CancellationToken, Task> wait, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (retryGate) retryWait = cts;
        try { await wait(cts.Token).ConfigureAwait(false); }
        finally
        {
            lock (retryGate)
            {
                retryWait = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// T274: the successor's player forces the take-over ("Take over now"): the link to the old host is dropped and the successor promotes at once, with no quorum (an explicit local action, fenced by the epoch like any promotion).
    /// Other players who still reach the old host refuse the new host (<see cref="ClientCodes.HostStillAlive"/>) and stay with it. False when this session is not the announced successor's or no game runs.
    /// </summary>
    public async Task<bool> TakeOverNowAsync()
    {
        if (stopped || !everJoined || gameId == null || electionDisabled || redirect != null || !IsSuccessorSelf()) return false;
        forceTakeOver = true;
        var current = transport;
        if (current != null)
        {
            try { await current.CloseAsync().ConfigureAwait(false); }
            catch (Exception e) when (IsConnectionGone(e)) { /* already gone */ }
        }
        return true;
    }

    // A voter: once the host has been gone for the loss window, say Hello at the successor's endpoint. A successor still waiting for its own window answers not-host-yet; one that issues a challenge gets this player's signed vote
    // (persisted before it is sent: one candidate per epoch); one that already is the host answers with a Welcome, and the session follows it like a hand-off.
    private async Task VoteAsync(SuccessorInfo info, CancellationToken token)
    {
        if (!CanDial(info)) return;
        var epoch = connectionEpoch + 1;
        if (votedEpoch == epoch && !string.Equals(votedCandidate, info.KeyId, StringComparison.Ordinal)) return; // never a second candidate for the same epoch
        IReadOnlyList<string> endpoints = options.ConnectEndpoint == null ? [] : info.Endpoints;
        var route = options.ConnectRelay != null && options.ConfirmRelay != null ? RelayRoutes.Normalize(info.Relay) : null;
        var slot = voteRotation++ % (endpoints.Count + (route == null ? 0 : 1));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(options.HandshakeTimeout);
        ITransport? probe = null;
        try
        {
            if (slot < endpoints.Count)
            {
                probe = await options.ConnectEndpoint.NotNull()(endpoints[slot], limit.Token).ConfigureAwait(false);
            }
            else
            {
                // T401: dialing the successor's relay room requires the player's agreement, and the dial timeout pauses while they are asked.
                if (!await ConfirmRelayAsync(route.NotNull(), limit).ConfigureAwait(false)) return;
                probe = await options.ConnectRelay.NotNull()(route.NotNull(), info.Fingerprint, limit.Token).ConfigureAwait(false);
            }
            await probe.SendAsync(ProtocolCodec.Encode(BuildHello()), limit.Token).ConfigureAwait(false);
            var frame = await probe.ReceiveAsync(limit.Token).ConfigureAwait(false);
            if (frame == null) return;
            switch (ProtocolCodec.Decode(frame))
            {
                case ElectionChallenge challenge when challenge.Epoch == epoch && string.Equals(challenge.GameId, gameId, StringComparison.Ordinal) && HostKey.Matches(challenge.HostKeyFingerprint, info.Fingerprint):
                    votedEpoch = epoch;
                    votedCandidate = info.KeyId;
                    options.PersistVote?.Invoke(epoch, info.KeyId);
                    var vote = options.Identity.SignLossVote(gameId.NotNull(), epoch, info.KeyId, Game.Snapshot?.Seq ?? -1, challenge.Nonce);
                    await probe.SendAsync(ProtocolCodec.Encode(vote), limit.Token).ConfigureAwait(false);
                    var reply = await probe.ReceiveAsync(limit.Token).ConfigureAwait(false);
                    if (reply != null && ProtocolCodec.Decode(reply) is HostLossVoteResult result) Emit(new VoteCast(epoch, info.KeyId, result.Accepted));
                    break;
                case Welcome welcome when (welcome.Epoch ?? 0) == epoch && HostKey.Matches(welcome.HostKeyFingerprint, info.Fingerprint):
                    StartElectionRedirect(info, null);
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* unreachable, refused, malformed: tried again on the next poll */ }
        finally
        {
            if (probe != null)
            {
                try { await probe.CloseAsync().ConfigureAwait(false); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    }

    // A higher-epoch host is only followed by a player who really lost the old one: a last look at the old address, and a Welcome from it means the host is alive (a take-over on a healthy host).
    private async Task<bool> OldHostAliveAsync(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        ITransport? probe = null;
        try
        {
            probe = await connect(limit.Token).ConfigureAwait(false);
            await probe.SendAsync(ProtocolCodec.Encode(BuildHello()), limit.Token).ConfigureAwait(false);
            var frame = await probe.ReceiveAsync(limit.Token).ConfigureAwait(false);
            return frame != null && ProtocolCodec.Decode(frame) is Welcome welcome && (welcome.Epoch ?? 0) <= connectionEpoch;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
        finally
        {
            if (probe != null)
            {
                try { await probe.CloseAsync().ConfigureAwait(false); } catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    }
}
