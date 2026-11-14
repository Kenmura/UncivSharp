using System.Globalization;
using Unciv.Net.Visibility;

namespace Unciv.Assets;

/// <summary>What the next-turn overlay draws: a fixed heading (a translation key), the civ line (composed, so left as is by the client), and a 0..1 bar fill.</summary>
public sealed record NextTurnProgressViewData(string Heading, string Detail, double Fraction);

/// <summary>
/// C040: the next-turn progress overlay as plain data, Godot-free so it is unit-tested. It is fed the <see cref="TurnProgress"/> the turn pump reports
/// before each automatic civ plays ("civ n of m") and decides nothing else. The civ is shown by its display name (the caller maps ids to names).
/// </summary>
public static class NextTurnProgressModel
{
    /// <summary>The heading of the overlay (a translation key).</summary>
    public const string Heading = "Processing the other civilizations";

    /// <summary>The heading before the first civ was reported: the human's own end of turn runs first.</summary>
    public const string Starting = "Ending your turn";

    /// <summary>T345: the line of a joiner (or a host whose server resolves the round): the other civs play on the host, so there is no count to show.</summary>
    public const string Waiting = "Waiting for AI/host";

    /// <summary>T345: the hint under the bar while the local turn can still be cancelled; the local turn flow stops between civs and keeps what was played.</summary>
    public const string CancelHint = "Esc to cancel";

    /// <summary>The view of the moment before any civ was reported.</summary>
    public static NextTurnProgressViewData Begin() => new(Starting, "", 0.0);

    /// <summary>T345: what a client that does not run the other civs itself can say: the host (and its AI) is resolving the turn.</summary>
    public static NextTurnProgressViewData WaitingForHost() => new(Heading, Waiting, 0.0);

    /// <summary>"Rome - 3 of 8" for the civ about to play (1-based position); the bar is full when the last civ is reached.</summary>
    public static NextTurnProgressViewData Of(TurnProgress progress, Func<string, string> displayName)
    {
        var total = Math.Max(progress.Total, 1);
        var position = Math.Clamp(progress.Played + 1, 1, total);
        var detail = string.Create(CultureInfo.InvariantCulture, $"{displayName(progress.CivId)} - {position} of {total}");
        return new NextTurnProgressViewData(Heading, detail, Math.Clamp((double)progress.Played / total, 0.0, 1.0));
    }
}

/// <summary>
/// T345: when the next-turn overlay is shown. A turn that takes a fraction of a second must not flash an overlay, so a started turn is only revealed once
/// <see cref="RevealDelaySeconds"/> have passed; progress reports and the "waiting for AI/host" line only update what will be shown, they never reveal it early.
/// Godot-free and advanced by the view each frame (the harness steps it by hand), so the timing is unit-tested.
/// </summary>
public sealed class NextTurnOverlayState
{
    /// <summary>How long a turn may take before the overlay appears; below it the turn is over before the player notices.</summary>
    public const double DefaultRevealDelaySeconds = 0.4;

    /// <summary>T345: how long one back-and-forth sweep of the indeterminate waiting indicator takes.</summary>
    public const double WaitSweepPeriodSeconds = 1.6;

    /// <summary>The overlay's own delay; scenarios may shorten it so a capture does not depend on frames.</summary>
    public double RevealDelaySeconds { get; set; } = DefaultRevealDelaySeconds;

    /// <summary>Whether a turn (local or resolved by the host) is in progress.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether the delay has passed and the overlay is to be drawn.</summary>
    public bool Revealed { get; private set; }

    /// <summary>Time the active turn has taken, in seconds.</summary>
    public double ElapsedSeconds { get; private set; }

    /// <summary>The data last reported for the active turn.</summary>
    public NextTurnProgressViewData View { get; private set; } = new("", "", 0.0);

    /// <summary>The hint under the bar (empty when the turn cannot be cancelled from this client).</summary>
    public string Hint { get; private set; } = "";

    /// <summary>Whether the overlay is showing an indeterminate host/AI wait rather than step progress.</summary>
    public bool IsWaiting => View.Detail == NextTurnProgressModel.Waiting;

    /// <summary>0..1 ping-pong position for the indeterminate wait bar, driven deterministically by <see cref="ElapsedSeconds"/>.</summary>
    public double WaitSweepPosition
    {
        get
        {
            if (!IsWaiting || WaitSweepPeriodSeconds <= 0) return 0.0;
            double cycle = (ElapsedSeconds % WaitSweepPeriodSeconds) / WaitSweepPeriodSeconds;
            return cycle <= 0.5 ? cycle * 2.0 : 2.0 - cycle * 2.0;
        }
    }

    public bool ShouldShow => Active && Revealed;

    /// <summary>Starts a turn (or a wait for the host), hidden until the delay passes.</summary>
    public void Start(NextTurnProgressViewData view, string hint)
    {
        Active = true;
        Revealed = false;
        ElapsedSeconds = 0;
        View = view;
        Hint = hint;
    }

    /// <summary>Replaces the data of the active turn; an inactive overlay ignores it (a late report from a finished turn must not resurrect it).</summary>
    public void SetProgress(NextTurnProgressViewData view)
    {
        if (!Active) return;
        View = view;
    }

    /// <summary>The turn runs on the host: starts waiting when idle, otherwise just updates the line (the delay is not restarted by every snapshot).</summary>
    public void WaitForHost(NextTurnProgressViewData view)
    {
        if (Active) View = view;
        else Start(view, "");
    }

    /// <summary>Shows the data at once (a fixed capture, or a scenario that drives the overlay directly).</summary>
    public void ShowNow(NextTurnProgressViewData view, string hint)
    {
        Active = true;
        Revealed = true;
        ElapsedSeconds = 0;
        View = view;
        Hint = hint;
    }

    /// <summary>Adds frame time; reveals the overlay once the delay has passed and keeps advancing <see cref="ElapsedSeconds"/> while active so indeterminate waiting animates deterministically.</summary>
    public void Advance(double seconds)
    {
        if (!Active) return;
        ElapsedSeconds += seconds;
        if (!Revealed && ElapsedSeconds >= RevealDelaySeconds) Revealed = true;
    }

    public void Close()
    {
        Active = false;
        Revealed = false;
        ElapsedSeconds = 0;
        View = new NextTurnProgressViewData("", "", 0.0);
        Hint = "";
    }
}
