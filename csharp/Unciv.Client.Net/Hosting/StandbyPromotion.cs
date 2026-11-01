using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Unciv.Client.Net.Session;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Persistence;
using Unciv.Net.Standby;
using Unciv.Server;
using Unciv.Server.Transport;

namespace Unciv.Client.Net.Hosting;

/// <summary>
/// T273: the designated successor's side of a graceful hand-off (design/HOST-MIGRATION.md section 2). Wire <see cref="Handler"/> as <see cref="ClientSessionOptions.OnHandoff"/> of the successor's own session:
/// when the old host sends <see cref="HostHandoff"/>, the standby copy under <c>standby/&lt;gameId&gt;/</c> is promoted with <see cref="InProcessHost.ResumeFromDirectory"/> at the new epoch (after proving it
/// reproduces the old host's final state hash), a listener is opened on the announced endpoint with this player's own certificate, and the player's session continues through an in-memory loopback with the
/// same key and seat. Everything it needs to refuse is checked first: the hand-off must name this player's key, the certificate must be the announced fingerprint, and the standby directory must exist.
/// </summary>
public sealed class StandbyPromotion(string standbyRoot, ClientIdentity identity, X509Certificate2 certificate, InProcessHostOptions? hostOptions = null)
{
    /// <summary>The promoted host once <see cref="Handler"/> succeeded (null before, or when the hand-off was not for this player).</summary>
    public InProcessHost? Host { get; private set; }

    /// <summary>T282: the standby listener this player keeps open on the announced port (<see cref="StandbyListener"/>); promotion closes it just before the promoted host binds the same port.</summary>
    public StandbyListener? Listener { get; set; }

    // T354: the standby's relay room once detached from the listener; kept across a failed port bind so a retry of the same hand-off still has it.
    private RelayListener? adoptedRelay;

    /// <summary>The game directory of the promoted host (the standby directory).</summary>
    public string? Directory { get; private set; }

    /// <summary>For <see cref="ClientSessionOptions.OnHandoff"/>. Returns the loopback connector of the new host, or null when this player is not the successor (it then uses <see cref="ClientSessionOptions.ConnectEndpoint"/>).</summary>
    public Task<Func<CancellationToken, Task<ITransport>>?> Handler(HostHandoff handoff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(handoff.Successor.KeyId, identity.PlayerKey, StringComparison.Ordinal)) return Task.FromResult<Func<CancellationToken, Task<ITransport>>?>(null);
        var host = Promote(handoff);
        return Task.FromResult<Func<CancellationToken, Task<ITransport>>?>(_ => Task.FromResult(host.ConnectLoopback()));
    }

    private InProcessHost Promote(HostHandoff handoff) =>
        PromoteCore(handoff.GameId, handoff.Epoch, handoff.Successor, new HostPromotionFacts(handoff.FinalSeq, handoff.FinalHash, handoff.DepartedKeyId, null));

    /// <summary>What distinguishes a promotion: a graceful hand-off knows the old host's final seq and hash and that its seat left cleanly; a crash election knows neither and the crashed host's seat dropped.</summary>
    private sealed record HostPromotionFacts(long? FinalSeq, string? FinalHash, string? DepartedKeyId, string? DroppedKeyId);

    /// <summary>
    /// T274: promotes the standby copy after a crash election (or "Take over now") at <paramref name="epoch"/>: there is no final seq or hash to check (the old host is gone), the copy must still recover cleanly and the
    /// epoch must rise. <paramref name="droppedKeyId"/> is the key of the crashed host's own seat (it dropped: the T267 grace and K rules apply). Returns the loopback connector of the new host for this player's own session.
    /// A second call returns the same host (a promotion happens once).
    /// </summary>
    public Func<CancellationToken, Task<ITransport>> PromoteAfterCrash(string gameId, int epoch, SuccessorInfo successor, string? droppedKeyId)
    {
        var host = Host ?? PromoteCore(gameId, epoch, successor, new HostPromotionFacts(null, null, null, droppedKeyId));
        return _ => Task.FromResult(host.ConnectLoopback());
    }

    /// <summary>
    /// T274, the manual fallback when host and successor are both gone and no session runs ("Host saved game" on the standby directory): resumes <c>standby/&lt;gameId&gt;/</c> as the host at the stored epoch plus one on
    /// <paramref name="port"/>, fenced like any promotion, so the old host cannot return unnoticed. The caller's own player connects with <see cref="InProcessHost.ConnectLoopback"/>.
    /// </summary>
    public InProcessHost TakeOverFromDisk(string gameId, int port)
    {
        var directory = StandbyStore.DirectoryFor(standbyRoot, gameId);
        if (directory == null || !System.IO.Directory.Exists(directory)) throw new HostingException("There is no standby copy of this game");
        int epoch;
        try
        {
            using var opened = FileGameStore.Open(directory, hostOptions?.Store);
            epoch = opened.Store.ReadMeta().HostEpoch + 1;
        }
        catch (Exception e) when (e is GameStoreException or IOException or System.Text.Json.JsonException)
        {
            throw new HostingException("The standby copy cannot be opened: " + e.Message, e);
        }
        var successor = new SuccessorInfo { KeyId = identity.PlayerKey, Endpoints = [$"127.0.0.1:{port}"], Fingerprint = HostCertificate.GetFingerprint(certificate) };
        return Host ?? PromoteCore(gameId, epoch, successor, new HostPromotionFacts(null, null, null, null));
    }

    private InProcessHost PromoteCore(string gameId, int epoch, SuccessorInfo successor, HostPromotionFacts facts)
    {
        if (Host != null) throw new HostingException("This player already promoted a standby copy");
        var fingerprint = HostCertificate.GetFingerprint(certificate);
        if (!HostKey.Matches(fingerprint, successor.Fingerprint)) throw new HostingException("This player's certificate is not the announced successor fingerprint");
        var directory = StandbyStore.DirectoryFor(standbyRoot, gameId);
        if (directory == null || !System.IO.Directory.Exists(directory)) throw new HostingException("There is no standby copy of this game");
        var port = successor.Endpoints.Count == 0 ? 0 : PortOf(successor.Endpoints); // a relay-only successor has no announced port: its local listener takes any free one

        var proof = new PromotionProof
        {
            GameId = gameId,
            Epoch = epoch,
            KeyId = identity.PlayerKey,
            Fingerprint = fingerprint,
            Signature = identity.SignPromotion(gameId, epoch, fingerprint),
        };
        InProcessHost host;
        try
        {
            host = InProcessHost.ResumeFromDirectory(directory, identity.PlayerKey, (hostOptions ?? new InProcessHostOptions()) with
            {
                Promotion = new HostPromotion { Epoch = epoch, Proof = proof, FinalSeq = facts.FinalSeq, FinalHash = facts.FinalHash, DepartedKeyId = facts.DepartedKeyId, DroppedKeyId = facts.DroppedKeyId },
            });
        }
        catch (Exception e) when (e is GameStoreException or IOException or System.Text.Json.JsonException)
        {
            throw new HostingException("The standby copy cannot be promoted: " + e.Message, e);
        }
        try
        {
            Listener?.Stop();
            adoptedRelay ??= Listener?.DetachRelay(); // T354: the room the standby held on the relay is the new host's room, with the address the clients already have
            host.Listen(certificate, port, adoptRelay: adoptedRelay);
            adoptedRelay = null;
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            host.StopAsync().GetAwaiter().GetResult(); // the copy stays promoted: a retry with the same hand-off resumes it (the promotion is idempotent)
            throw new HostingException($"Port {port} cannot be used ({e.Message}).", e);
        }
        Host = host;
        Directory = directory;
        return host;
    }

    // The successor announced one port (every endpoint shares it).
    private static int PortOf(IReadOnlyList<string> endpoints)
    {
        var endpoint = endpoints[0];
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
            throw new HostingException("The announced endpoint is not usable");
        return port;
    }
}
