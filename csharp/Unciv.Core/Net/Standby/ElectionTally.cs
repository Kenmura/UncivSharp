using Unciv.Net.Identity;
using Unciv.Net.Turns;

namespace Unciv.Net.Standby;

/// <summary>T274 timers of the crash election (design/HOST-MIGRATION.md section 2). Seconds, all defaults are the design's.</summary>
public sealed record ElectionLimits
{
    /// <summary>How long a client or the successor must have been without the host (no frame at all) before it acts on the loss.</summary>
    public int HostLossSeconds { get; init; } = 20;

    /// <summary>A client also keeps retrying the old address for this long after the loss before it votes.</summary>
    public int ReconnectGraceSeconds { get; init; } = 10;

    /// <summary>One election cycle on the successor: votes and challenges older than this are void and the cycle restarts (a client then votes again for the same candidate).</summary>
    public int ElectionWindowSeconds { get; init; } = 60;

    /// <summary>The roster the host last announced counts as fresh only while the host was heard from this recently; a longer outage falls back to every seat of the roster (a long outage cannot shrink the quorum).</summary>
    public int RosterMaxAgeSeconds { get; init; } = 300;

    /// <summary>Clients stop retrying host and successor after this long (default 10 minutes) and show the manual hand-off help.</summary>
    public int GiveUpSeconds { get; init; } = 600;

    /// <summary>Challenges outstanding at once on the successor (bounds what an anonymous caller can make it remember).</summary>
    public int MaxOutstandingChallenges { get; init; } = 256;
}

/// <summary>
/// T274: one election cycle on the designated successor. It issues <see cref="ElectionChallenge"/> nonces and counts signed <see cref="HostLossVote"/>s; the quorum is strictly more than half of the electorate,
/// which is the seats of the roster the host last announced as connected (the successor's own seat included, the host's own seat never), or every seat of that roster when it is stale
/// (<see cref="ElectionLimits.RosterMaxAgeSeconds"/>). Distinct key ids count once, a nonce works once, and a vote is refused for another game, epoch or candidate, for a key outside the electorate, or with a bad signature, all
/// with the one code <see cref="ErrorCodes.VoteRefused"/>. Not thread-safe: the owner serialises calls.
/// </summary>
public sealed class ElectionTally
{
    private readonly ElectionLimits limits;
    private readonly IClock clock;
    private readonly ISecureRandom random;
    private readonly Dictionary<string, long> nonces = new(StringComparer.Ordinal); // nonce -> expiry
    private readonly Dictionary<string, long> votes = new(StringComparer.Ordinal); // key id -> last seq
    private readonly HashSet<string> electorate;

    public ElectionTally(string gameId, int epoch, string candidateKeyId, IReadOnlyList<SuccessorSeat> roster, bool rosterFresh, IClock clock, ISecureRandom random, ElectionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(random);
        GameId = gameId;
        Epoch = epoch;
        CandidateKeyId = candidateKeyId;
        this.clock = clock;
        this.random = random;
        this.limits = limits ?? new ElectionLimits();
        electorate = new HashSet<string>(roster.Where(it => it.IsHost != true && (it.Connected || !rosterFresh)).Select(it => it.KeyId), StringComparer.Ordinal) { candidateKeyId };
        StartedAt = clock.NowMillis;
        votes[candidateKeyId] = long.MinValue; // the successor's own vote
        HostKeyId = roster.FirstOrDefault(it => it.IsHost == true)?.KeyId;
    }

    public string GameId { get; }

    /// <summary>The epoch being elected (the old host's epoch plus one).</summary>
    public int Epoch { get; }

    public string CandidateKeyId { get; }

    /// <summary>The key of the dead host's own player seat, when the roster named one.</summary>
    public string? HostKeyId { get; }

    public long StartedAt { get; }

    /// <summary>Seats whose votes count, the candidate's own included.</summary>
    public int ElectorateSize => electorate.Count;

    /// <summary>Votes needed: strictly more than half of the electorate.</summary>
    public int Quorum => electorate.Count / 2 + 1;

    public int VoteCount => votes.Count;

    public bool HasQuorum => votes.Count >= Quorum;

    /// <summary>The highest last-applied seq any voter reported (informational: voters ahead of the successor will be rolled back).</summary>
    public long HighestVoterSeq => votes.Values.Where(it => it != long.MinValue).DefaultIfEmpty(-1).Max();

    public bool IsExpired => clock.NowMillis - StartedAt >= limits.ElectionWindowSeconds * 1000L;

    /// <summary>A fresh nonce for one vote request, or null when too many are outstanding or the cycle is over.</summary>
    public string? IssueChallenge()
    {
        var now = clock.NowMillis;
        foreach (var stale in nonces.Where(it => it.Value <= now).Select(it => it.Key).ToList()) nonces.Remove(stale);
        if (IsExpired || nonces.Count >= limits.MaxOutstandingChallenges) return null;
        Span<byte> bytes = stackalloc byte[16];
        random.Fill(bytes);
        var nonce = Convert.ToHexString(bytes).ToLowerInvariant();
        nonces[nonce] = Math.Min(now + limits.ElectionWindowSeconds * 1000L, StartedAt + limits.ElectionWindowSeconds * 1000L);
        return nonce;
    }

    /// <summary>Counts <paramref name="vote"/>. Null when it counted (or its key already had), else <see cref="ErrorCodes.VoteRefused"/>. The nonce is used up either way once it was issued.</summary>
    public string? Cast(HostLossVote vote)
    {
        var now = clock.NowMillis;
        if (!nonces.Remove(vote.Nonce, out var expiry) || expiry <= now) return ErrorCodes.VoteRefused;
        if (!string.Equals(vote.GameId, GameId, StringComparison.Ordinal) || vote.Epoch != Epoch || !string.Equals(vote.CandidateKeyId, CandidateKeyId, StringComparison.Ordinal)) return ErrorCodes.VoteRefused;
        if (!electorate.Contains(vote.KeyId) || string.Equals(vote.KeyId, HostKeyId, StringComparison.Ordinal) || !PlayerKeys.VerifyLossVote(vote)) return ErrorCodes.VoteRefused;
        votes.TryAdd(vote.KeyId, vote.LastSeq);
        return null;
    }
}
