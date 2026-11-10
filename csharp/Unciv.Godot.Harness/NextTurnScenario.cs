using Godot;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.Harness;
using Unciv.Assets;
using Unciv.Net.Visibility;

namespace Unciv.GodotHarness;

/// <summary>
/// C040: End turn on a crowded map (the human and ten AI civs) is processed off the UI thread. The overlay is captured with fixed progress (so the golden does not depend on timing), then
/// Next turn is pressed for real: the scenario asserts the window kept drawing (no frame took long, and several frames passed while the civs played), the overlay is gone afterwards and
/// the turn advanced. The headless replay of the command log must reach the same state hash as the run.
/// </summary>
public sealed class NextTurnScenario : Scenario
{
    /// <summary>A frame this far apart from the previous one means the UI thread was blocked by the turn.</summary>
    private const double MaxFrameMs = 750;

    public override string Name => "next-turn";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override bool Crowd => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Is it clear the game is busy (not frozen), and which civ is being played?",
        "Does a turn that finishes quickly stay free of an overlay, while a long one shows the card and its cancel hint?",
        "Can the map still be panned and the menu opened while the civs play?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session)
    {
        var game = session.Game;
        long now = 1000;
        var localHost = new LocalHost(session, ScenarioGame.HumanA, () => now += 1000);
        var screen = new GameScreen();
        host.AddChild(screen);
        screen.Start(localHost, AssetRegistry.ForRuleset(game.Ruleset));
        screen.Hud.HoldBanner = true;
        int startTurn = game.Turns;

        return
        [
            new WaitFrames(4),
            new AssertQuery("the game has the human and the crowd of AI civs", () => game.Civilizations.Count(it => it.IsAI() && it.IsMajorCiv()) >= ScenarioGame.CrowdAiCount),
            // T345 review: real time is frozen in the harness, so only an explicit Advance may move the delay; a reveal after these frames would mean a second driver steps it.
            new Act("start an overlay with no delay while the harness clock is frozen", () =>
            {
                screen.TurnOverlay.RevealDelaySeconds = 0;
                screen.TurnOverlay.Start(NextTurnProgressModel.Begin(), NextTurnProgressModel.CancelHint);
            }),
            new WaitFrames(5),
            new AssertQuery("the frozen harness clock does not advance the reveal delay", () => !screen.TurnOverlay.Visible),
            new Act("step the delay explicitly, like a frame would", () => screen.TurnOverlay.Advance(0)),
            new AssertQuery("the explicit step reveals the card", () => screen.TurnOverlay.Visible),
            new Act("close the card again", () => screen.TurnOverlay.Close()),
            new Act("start the overlay as a short turn would", () =>
            {
                screen.TurnOverlay.RevealDelaySeconds = 5;
                screen.TurnOverlay.Start(NextTurnProgressModel.Begin(), NextTurnProgressModel.CancelHint);
            }),
            new AssertQuery("a turn that is over quickly shows no overlay", () => !screen.TurnOverlay.Visible),
            new Act("the delay passes", () => screen.TurnOverlay.Advance(6)),
            new AssertQuery("a long turn shows the card and its cancel hint", () => screen.TurnOverlay.Visible && screen.TurnOverlay.HintText == NextTurnProgressModel.CancelHint),
            new Act("show the overlay at a fixed point of the turn", () => screen.TurnOverlay.ShowNow(NextTurnProgressModel.Of(new TurnProgress(2, ScenarioGame.CrowdAiCount, "Rome"), id => id))),
            new WaitFrames(2),
            new AssertQuery("the overlay names the civ and its position", () => screen.TurnOverlay.Visible && screen.TurnOverlay.DetailText == "Rome - 3 of 10"),
            new Capture("overlay", "Next turn in progress: a compact card under the navigation strip, 'Rome - 3 of 10' and 'Esc to cancel'"),
            new Act("hide the overlay again", () => screen.TurnOverlay.Close()),
            new Act("reveal the real turn at once (the delay is tested above)", () => screen.TurnOverlay.RevealDelaySeconds = 0),
            new Act("press Next turn (not settled: the processing is what is tested)", () =>
            {
                screen.TryHotkey("Space");
                screen.TurnOverlay.Advance(1); // the runner froze real time; step the reveal like a frame would
            }),
            new AssertQuery("pressing Next turn started processing", () => screen.IsEndingTurn && localHost.IsProcessingTurn),
            new WaitUntil("the overlay appears while the civs play", () => screen.TurnOverlay.Visible && screen.IsEndingTurn, 600),
            new WaitUntil("the turn is processed", () => !screen.IsEndingTurn && game.Turns > startTurn),
            new AssertQuery("the window kept drawing while the civs played (no long frame)", () => ScenarioRunner.LongestFrameMs < MaxFrameMs),
            new AssertQuery("the turn took several frames, so the work really ran beside the UI", () => ScenarioRunner.FramesWaited >= 3),
            new AssertQuery("the overlay is gone and the game moved on", () => !screen.TurnOverlay.Visible && !localHost.IsProcessingTurn && game.Turns == startTurn + 1 && screen.Turn == game.Turns),
            new AssertQuery("the status line reports the civs played", () => screen.StatusText.Contains("civ(s) played", StringComparison.Ordinal)),
            new WaitFrames(2),
            new Capture("after", "Back in control on the next turn"),
        ];
    }
}
