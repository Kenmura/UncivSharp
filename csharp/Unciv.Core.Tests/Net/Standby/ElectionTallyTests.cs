using Unciv.Core.Tests.Net.Identity;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Standby;
using Unciv.Net.Turns;

namespace Unciv.Core.Tests.Net.Standby;

/// <summary>T274 (A6, A7 at the tally level): quorum arithmetic, challenge nonces, and every vote that must not count.</summary>
public sealed class ElectionTallyTests : IDisposable
{
    private const string Game = "game-1";
    private readonly ClientIdentity host = ClientIdentity.Generate();
    private readonly ClientIdentity candidate = ClientIdentity.Generate();
    private readonly ClientIdentity second = ClientIdentity.Generate();
    private readonly ClientIdentity third = ClientIdentity.Generate();
    private readonly ClientIdentity stranger = ClientIdentity.Generate();
    private readonly ManualClock clock = new(1_000_000);

    public void Dispose()
    {
        host.Dispose();
        candidate.Dispose();
        second.Dispose();
        third.Dispose();
        stranger.Dispose();
    }

    private List<SuccessorSeat> Roster(bool secondOnline = true, bool thirdOnline = true) =>
    [
        new() { KeyId = host.PlayerKey, Connected = true, IsHost = true },
        new() { KeyId = candidate.PlayerKey, Connected = true },
        new() { KeyId = second.PlayerKey, Connected = secondOnline },
        new() { KeyId = third.PlayerKey, Connected = thirdOnline },
    ];

    private ElectionTally Tally(IReadOnlyList<SuccessorSeat>? roster = null, bool fresh = true, ElectionLimits? limits = null) =>
        new(Game, 1, candidate.PlayerKey, roster ?? Roster(), fresh, clock, new CountingRandom(), limits);

    private HostLossVote Vote(ClientIdentity who, string nonce, int epoch = 1, string? game = null, string? candidateKey = null) =>
        who.SignLossVote(game ?? Game, epoch, candidateKey ?? candidate.PlayerKey, 7, nonce);

    [Fact]
    public void QuorumIsMoreThanHalfOfTheConnectedSeatsWithoutTheHost()
    {
        var tally = Tally(); // candidate, second, third connected; the host's seat never counts
        Assert.Equal(3, tally.ElectorateSize);
        Assert.Equal(2, tally.Quorum);
        Assert.False(tally.HasQuorum); // the successor's own vote is 1 of 2
        Assert.Null(tally.Cast(Vote(second, tally.IssueChallenge()!)));
        Assert.True(tally.HasQuorum);
    }

    [Fact]
    public void OfflineSeatsDoNotCount_SoFourHumansWithOneOfflineNeedTwoVotes()
    {
        var tally = Tally(Roster(thirdOnline: false)); // candidate + second connected: quorum 2
        Assert.Equal(2, tally.ElectorateSize);
        Assert.Equal(2, tally.Quorum);
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(third, tally.IssueChallenge()!))); // an offline seat cannot swing it
        Assert.False(tally.HasQuorum);
    }

    [Fact]
    public void TwoHumans_TheSuccessorAloneIsTheQuorum()
    {
        var tally = Tally([new() { KeyId = host.PlayerKey, Connected = true, IsHost = true }, new() { KeyId = candidate.PlayerKey, Connected = true }]);
        Assert.Equal(1, tally.Quorum);
        Assert.True(tally.HasQuorum);
    }

    [Fact]
    public void AStaleRoster_CountsEverySeatSoALongOutageCannotShrinkTheQuorum()
    {
        var tally = Tally(Roster(secondOnline: false, thirdOnline: false), fresh: false);
        Assert.Equal(3, tally.ElectorateSize);
        Assert.Equal(2, tally.Quorum);
        Assert.False(tally.HasQuorum);
    }

    [Fact]
    public void ADedicatedHost_AllConnectedHumansCount()
    {
        var tally = Tally([new() { KeyId = candidate.PlayerKey, Connected = true }, new() { KeyId = second.PlayerKey, Connected = true }, new() { KeyId = third.PlayerKey, Connected = true }]);
        Assert.Equal(3, tally.ElectorateSize);
        Assert.Equal(2, tally.Quorum);
    }

    [Fact]
    public void ForgedAndMisaddressedVotesAreRefused()
    {
        var tally = Tally();
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(second, "never-issued"))); // a nonce nobody issued
        var forged = Vote(second, tally.IssueChallenge()!) with { KeyId = third.PlayerKey }; // signed by one key, claimed by another
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(forged));
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(stranger, tally.IssueChallenge()!))); // a key that is no seat
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(second, tally.IssueChallenge()!, epoch: 2))); // another epoch
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(second, tally.IssueChallenge()!, game: "other"))); // another game
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(second, tally.IssueChallenge()!, candidateKey: third.PlayerKey))); // another candidate
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(Vote(host, tally.IssueChallenge()!))); // the crashed host's own seat
        var tampered = Vote(second, tally.IssueChallenge()!) with { LastSeq = 99 }; // a field changed after signing
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(tampered));
        Assert.Equal(1, tally.VoteCount);
    }

    [Fact]
    public void ANonceWorksOnce_SoAVoteCannotBeReplayed()
    {
        var tally = Tally();
        var nonce = tally.IssueChallenge()!;
        var vote = Vote(second, nonce);
        Assert.Null(tally.Cast(vote));
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(vote));
    }

    [Fact]
    public void AVoteOlderThanTheChallengeWindowIsRefused_AndANewCycleVoidsOldNonces()
    {
        var tally = Tally();
        var nonce = tally.IssueChallenge()!;
        var vote = Vote(second, nonce);
        clock.AdvanceSeconds(61);
        Assert.True(tally.IsExpired);
        Assert.Equal(ErrorCodes.VoteRefused, tally.Cast(vote));
        Assert.Null(tally.IssueChallenge()); // an expired cycle issues nothing
        var next = Tally(); // the restarted cycle: the old nonce means nothing there
        Assert.Equal(ErrorCodes.VoteRefused, next.Cast(vote));
    }

    [Fact]
    public void ADistinctKeyCountsOnce_AndTheChallengeCountIsBounded()
    {
        var tally = Tally(limits: new ElectionLimits { MaxOutstandingChallenges = 3 });
        Assert.Null(tally.Cast(Vote(second, tally.IssueChallenge()!)));
        Assert.Null(tally.Cast(Vote(second, tally.IssueChallenge()!))); // accepted again, still one vote
        Assert.Equal(2, tally.VoteCount);
        Assert.NotNull(tally.IssueChallenge());
        Assert.NotNull(tally.IssueChallenge());
        Assert.NotNull(tally.IssueChallenge());
        Assert.Null(tally.IssueChallenge()); // three outstanding: no more
        Assert.Equal(7, tally.HighestVoterSeq);
    }

    [Fact]
    public void TheVoteSignatureIsInItsOwnDomain()
    {
        var vote = Vote(second, "n");
        Assert.True(PlayerKeys.VerifyLossVote(vote));
        // An auth proof or a promotion signature over similar text can never pass as a vote.
        var promotion = second.SignPromotion(Game, 1, "n");
        Assert.False(PlayerKeys.VerifyLossVote(vote with { Signature = promotion }));
        Assert.False(PlayerKeys.VerifyLossVote(vote with { Signature = "not base64 !!" }));
    }
}
