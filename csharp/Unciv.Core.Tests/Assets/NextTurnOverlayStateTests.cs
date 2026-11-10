using Unciv.Assets;
using Unciv.Net.Visibility;

namespace Unciv.Core.Tests.Assets;

/// <summary>T345: the overlay appears only when a turn outlasts the delay, and a host-resolved round can be waited for without restarting that delay.</summary>
public sealed class NextTurnOverlayStateTests
{
    private static NextTurnProgressViewData Progress() => NextTurnProgressModel.Of(new TurnProgress(2, 8, "Rome"), id => id);

    [Fact]
    public void Start_HidesTheOverlayUntilTheDelayHasPassed()
    {
        var state = new NextTurnOverlayState();
        state.Start(NextTurnProgressModel.Begin(), NextTurnProgressModel.CancelHint);
        Assert.False(state.ShouldShow);
        Assert.Equal(0.0, state.ElapsedSeconds);
        Assert.Equal(NextTurnProgressModel.CancelHint, state.Hint);

        state.Advance(NextTurnOverlayState.DefaultRevealDelaySeconds / 2);
        Assert.False(state.ShouldShow);

        state.Advance(NextTurnOverlayState.DefaultRevealDelaySeconds / 2);
        Assert.True(state.ShouldShow);
        Assert.Equal(NextTurnProgressModel.Starting, state.View.Heading);
    }

    [Fact]
    public void ProgressUpdatesDoNotRevealTheOverlayEarly()
    {
        var state = new NextTurnOverlayState();
        state.Start(NextTurnProgressModel.Begin(), NextTurnProgressModel.CancelHint);
        state.SetProgress(Progress());

        Assert.False(state.ShouldShow);
        Assert.Equal("Rome - 3 of 8", state.View.Detail);

        state.Advance(NextTurnOverlayState.DefaultRevealDelaySeconds);
        Assert.True(state.ShouldShow);
        Assert.Equal("Rome - 3 of 8", state.View.Detail);
        Assert.Equal(0.25, state.View.Fraction);
    }

    [Fact]
    public void WaitingForTheHostStartsOnceAndNeverResetsTheDelay()
    {
        var state = new NextTurnOverlayState();
        state.WaitForHost(NextTurnProgressModel.WaitingForHost());
        state.Advance(NextTurnOverlayState.DefaultRevealDelaySeconds);

        state.WaitForHost(NextTurnProgressModel.WaitingForHost()); // every snapshot in the round asks again
        state.Advance(0.01);

        Assert.True(state.ShouldShow);
        Assert.Equal(NextTurnProgressModel.Waiting, state.View.Detail);
        Assert.Equal("", state.Hint);
    }

    [Fact]
    public void LateProgressAfterCloseDoesNotResurrectTheOverlay()
    {
        var state = new NextTurnOverlayState();
        state.Start(NextTurnProgressModel.Begin(), NextTurnProgressModel.CancelHint);
        state.Advance(NextTurnOverlayState.DefaultRevealDelaySeconds);
        Assert.True(state.ShouldShow);

        state.Close();
        state.SetProgress(Progress());
        state.Advance(1.0);

        Assert.False(state.ShouldShow);
        Assert.Equal("", state.View.Detail);
    }

    [Fact]
    public void WaitingForTheHostCarriesNoCancelHintBecauseItCannotBeCancelledHere()
    {
        var state = new NextTurnOverlayState();
        state.WaitForHost(NextTurnProgressModel.WaitingForHost());
        Assert.Equal("", state.Hint);

        state.ShowNow(Progress(), NextTurnProgressModel.CancelHint);
        Assert.True(state.ShouldShow);
        Assert.Equal(NextTurnProgressModel.CancelHint, state.Hint);
        Assert.Equal("Rome - 3 of 8", state.View.Detail);
    }

    [Fact]
    public void ZeroDelayRevealsOnTheNextAdvance()
    {
        var state = new NextTurnOverlayState { RevealDelaySeconds = 0 };
        state.Start(NextTurnProgressModel.Begin(), "");
        state.Advance(0);
        Assert.True(state.ShouldShow);
    }
}
