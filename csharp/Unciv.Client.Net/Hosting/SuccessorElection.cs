using System.Security.Cryptography.X509Certificates;
using Unciv.Client.Net.Session;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Standby;
using Unciv.Net.Turns;
using Unciv.Server.Transport;

namespace Unciv.Client.Net.Hosting;

/// <summary>
/// T274: the designated successor's side of a crash election (design/HOST-MIGRATION.md section 2, path 2). Wire <see cref="Handler"/> as <see cref="ClientSessionOptions.OnHostLost"/> of the successor's own session.
/// The session calls it once its own link to the host has been dead for the host-loss window (so rule (a) holds); from then on this object answers voters: <see cref="AdmitAsync"/> takes a connection that said
/// <c>Hello</c>, issues a fresh <see cref="ElectionChallenge"/> and counts the signed <see cref="HostLossVote"/> that answers it in an <see cref="ElectionTally"/>. At quorum (strictly more than half of the connected seats the host last
/// announced, this player's own vote included, or every seat when the roster is stale) it stops listening and promotes through <see cref="StandbyPromotion"/>; a cycle that ends without quorum after the election window starts again
/// with fresh challenges (voters repeat the same vote). A forced take-over (<see cref="HostLossInfo.Forced"/>) promotes at once. The election listener is only open while this runs, on the announced port, with this player's certificate.
/// </summary>
public sealed class SuccessorElection(StandbyPromotion promotion, X509Certificate2 certificate, IClock? clock = null, ElectionLimits? limits = null, ISecureRandom? random = null, bool listen = true, int pollMillis = 20) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly IClock clock = clock ?? SystemClock.Instance;
    private readonly ElectionLimits limits = limits ?? new ElectionLimits();
    private ElectionTally? tally;
    private TcpTlsListener? listener;
    private CancellationTokenSource? acceptStop;
    private Task? acceptLoop;
    private string? fingerprint;

    /// <summary>The cycle in progress (null while idle), for tests and the host screen; read only.</summary>
    public ElectionTally? Tally
    {
        get { lock (gate) return tally; }
    }

    /// <summary>For <see cref="ClientSessionOptions.OnHostLost"/>.</summary>
    public async Task<Func<CancellationToken, Task<ITransport>>?> Handler(HostLossInfo info, CancellationToken cancellationToken)
    {
        fingerprint = HostCertificate.GetFingerprint(certificate);
        var epoch = info.OldEpoch + 1;
        var dropped = info.Roster.FirstOrDefault(it => it.IsHost == true)?.KeyId;
        try
        {
            if (info.Forced) return promotion.PromoteAfterCrash(info.GameId, epoch, info.Successor, dropped);
            if (listen) StartListening(info.Successor);
            while (!cancellationToken.IsCancellationRequested)
            {
                var cycle = new ElectionTally(info.GameId, epoch, info.Successor.KeyId, info.Roster, info.RosterFresh, clock, random, limits);
                lock (gate) tally = cycle;
                while (!cycle.IsExpired)
                {
                    bool quorum;
                    lock (gate) quorum = cycle.HasQuorum;
                    if (quorum)
                    {
                        await StopListeningAsync().ConfigureAwait(false);
                        lock (gate) tally = null;
                        return promotion.PromoteAfterCrash(info.GameId, epoch, info.Successor, dropped);
                    }
                    await Task.Delay(pollMillis, cancellationToken).ConfigureAwait(false);
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null; // the host came back, or the session ended
        }
        finally
        {
            lock (gate) tally = null;
            if (promotion.Host == null) await StopListeningAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One connection of a voter: Hello, then a challenge, then the vote. Before a cycle runs (the successor's own loss window has not elapsed) it answers <see cref="ErrorCodes.NotHostYet"/>. Used by the TCP accept loop and,
    /// directly, by in-memory scenarios.
    /// </summary>
    public async Task AdmitAsync(ITransport transport, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (await ReceiveAsync(transport, timeout.Token).ConfigureAwait(false) is not Hello hello || hello.Protocol != ProtocolVersion.Current)
            {
                await SendAsync(transport, new Error { Code = ErrorCodes.InvalidMessage }, timeout.Token).ConfigureAwait(false);
                return;
            }
            string? nonce;
            ElectionTally? current;
            lock (gate)
            {
                current = tally;
                nonce = current?.IssueChallenge();
            }
            if (current == null || nonce == null)
            {
                await SendAsync(transport, new Error { Code = ErrorCodes.NotHostYet }, timeout.Token).ConfigureAwait(false);
                return;
            }
            await SendAsync(transport, new ElectionChallenge { GameId = current.GameId, Epoch = current.Epoch, Nonce = nonce, HostKeyFingerprint = fingerprint ?? HostCertificate.GetFingerprint(certificate) }, timeout.Token).ConfigureAwait(false);
            if (await ReceiveAsync(transport, timeout.Token).ConfigureAwait(false) is not HostLossVote vote)
            {
                await SendAsync(transport, new Error { Code = ErrorCodes.InvalidMessage }, timeout.Token).ConfigureAwait(false);
                return;
            }
            string? code;
            lock (gate) code = ReferenceEquals(tally, current) ? current.Cast(vote) : ErrorCodes.VoteRefused;
            await SendAsync(transport, new HostLossVoteResult { Accepted = code == null, Code = code }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // a voter that went away or sent garbage costs only its own connection
        }
        finally
        {
            try { await transport.CloseAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private void StartListening(SuccessorInfo successor)
    {
        if (listener != null) return;
        var port = PortOfEndpoint(successor.Endpoints[0]);
        var started = new TcpTlsListener(certificate, port, TimeSpan.FromSeconds(5));
        started.Start();
        listener = started;
        acceptStop = new CancellationTokenSource();
        acceptLoop = AcceptLoopAsync(started, acceptStop.Token);
    }

    private async Task AcceptLoopAsync(TcpTlsListener tcp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ITransport accepted;
            try { accepted = await tcp.AcceptTransportAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (System.Net.Sockets.SocketException) when (token.IsCancellationRequested) { return; }
            catch (Exception) { continue; } // a failed handshake costs only that connection
            _ = AdmitAsync(accepted, token);
        }
    }

    private async Task StopListeningAsync()
    {
        var running = listener;
        listener = null;
        if (running == null) return;
        await acceptStop!.CancelAsync().ConfigureAwait(false);
        running.Stop();
        try { await acceptLoop!.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        acceptStop.Dispose();
        acceptStop = null;
    }

    private static int PortOfEndpoint(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            throw new HostingException("The announced endpoint is not usable");
        return port;
    }

    private static async Task<Message?> ReceiveAsync(ITransport transport, CancellationToken token)
    {
        var frame = await transport.ReceiveAsync(token).ConfigureAwait(false);
        return frame == null ? null : ProtocolCodec.Decode(frame);
    }

    private static Task SendAsync(ITransport transport, Message message, CancellationToken token) => transport.SendAsync(ProtocolCodec.Encode(message), token).AsTask();

    public async ValueTask DisposeAsync() => await StopListeningAsync().ConfigureAwait(false);
}
