using Unciv.Assets;
using Unciv.Queries.Empire;

namespace Unciv.Core.Tests.Assets;

public class VoteResultModelTests
{
    [Fact]
    public void NothingIsShownUntilTheEngineSaysTheResultIsDue()
    {
        Assert.Null(VoteResultModel.Build(null));
        Assert.Null(VoteResultModel.Build(new DiplomaticVoteBreakdownResult(false, null, null, "", [])));
    }

    [Fact]
    public void RowsWordTheEnginesAnswer()
    {
        var result = new DiplomaticVoteBreakdownResult(true, "United Nations", "Rome", "{No world leader was elected.}",
        [
            new VoteRow("Babylon", true, 12, 1, false, "Rome", false),
            new VoteRow("Rome", true, 0, 2, false, null, true),
            new VoteRow("Zurich", false, 0, 1, true, null, false),
        ]);
        var view = VoteResultModel.Build(result)!;
        Assert.Equal("World Congress", view.Title);
        Assert.Equal("{No world leader was elected.}", view.Verdict);
        Assert.Equal("Continue", view.ContinueLabel);
        Assert.Equal(new VoteResultRow("Babylon", "12", "[1] vote", null, "Voted for", "Rome"), view.Rows[0]);
        Assert.Equal(new VoteResultRow("Rome", "0", "[2] votes", "United Nations", "Voted for", DiplomaticVoteResultQuery.UnknownCiv), view.Rows[1]);
        Assert.Equal(new VoteResultRow("Zurich", "", "[1] vote", null, "Abstained", null), view.Rows[2]);
    }

    [Fact]
    public void ThresholdRowsOrderMajorCivsAndReportVotesNeeded()
    {
        Assert.Empty(VoteResultModel.ThresholdRows((DiplomaticVoteBreakdownResult?)null));
        var hidden = new DiplomaticVoteBreakdownResult(false, null, null, "", [], VotesNeeded: 10);
        Assert.Empty(VoteResultModel.ThresholdRows(hidden));

        var noThreshold = new DiplomaticVoteBreakdownResult(true, null, null, "", [new VoteRow("Rome", true, 5, 1, false, null, false)], VotesNeeded: 0);
        Assert.Empty(VoteResultModel.ThresholdRows(noThreshold));

        var shown = new DiplomaticVoteBreakdownResult(true, "United Nations", "Rome", "{Rome won}",
        [
            new VoteRow("Babylon", true, 6, 1, false, "Babylon", false),
            new VoteRow("Rome", true, 12, 2, false, "Rome", false),
            new VoteRow("Zurich", false, 0, 1, true, null, false),
        ], VotesNeeded: 10);

        var view = VoteResultModel.Build(shown)!;
        Assert.Equal(10, view.VotesNeeded);

        var thresholds = VoteResultModel.ThresholdRows(shown);
        Assert.Equal(["Rome", "Babylon"], thresholds.Select(it => it.Civ));
        Assert.Equal([true, false], thresholds.Select(it => it.MetThreshold));
        Assert.Equal([12, 6], thresholds.Select(it => it.Votes));
        Assert.All(thresholds, it => Assert.Equal(10, it.VotesNeeded));
        Assert.Equal(thresholds, VoteResultModel.ThresholdRows(view));
        Assert.Empty(VoteResultModel.ThresholdRows((VoteResultView?)null));
    }
}
