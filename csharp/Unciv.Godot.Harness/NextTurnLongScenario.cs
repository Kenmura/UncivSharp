using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.Harness;

namespace Unciv.GodotHarness;

/// <summary>
/// T345: the End turn progress overlay on the game it is for - a late-game table of eight major civs, each with eight cities and a stack of units. Esc stops the turn at
/// the next command boundary (the card says it can) and Next turn continues it; the resumed long turn is the one measured. While it runs the overlay names the civ
/// currently moving and its count, the camera still zooms and the in-game menu still opens, and the window never takes a long frame.
/// </summary>
public sealed class NextTurnLongScenario : Scenario
{
    /// <summary>A frame this far apart from the previous one means the UI thread was blocked by the turn.</summary>
    private const double MaxFrameMs = 750;

    public override string Name => "next-turn-long";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override bool LateGame => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Is the civ currently moving (and how many are done) clear on a late-game eight-civ turn?",
        "Can the camera be panned/zoomed and the menu opened while the civs play, without the card getting in the way?",
        "Does a short turn stay free of the overlay, and does Esc cancel a long one as the card says?",
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
        screen.TurnOverlay.RevealDelaySeconds = 0; // this scenario measures the long turn; the delay itself is tested by next-turn and the model tests
        int startTurn = game.Turns;
        ulong started = 0;
        ulong measured = 0;
        double zoomBefore = 0;
        bool overlayUp = false;
        bool endingWhenPanned = false;
        bool endingWhenOpened = false;

        return
        [
            new WaitFrames(4),
            new AssertQuery("the late-game table has eight major civs", () => game.Civilizations.Count(it => it.IsMajorCiv()) == ScenarioGame.LateGameCivCount),
            new AssertQuery("several civs own cities and units, so the turn is a real one", () =>
                game.Civilizations.Count(it => it.IsMajorCiv() && it.Cities.Count >= 2) >= 6
                && game.Civilizations.Count(it => it.IsMajorCiv() && it.Units.GetCivUnits().Count() >= 4) >= 6),
            new Act("start an End turn and cancel it at once", () =>
            {
                screen.TryHotkey("Space");
                InputSimulator.SimulateKey(host.GetViewport(), Key.Escape);
            }),
            new WaitUntil("the cancelled turn stops and reports it", () => !screen.IsEndingTurn && screen.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase), 900),
            new AssertQuery("no overlay and the status says the turn was cancelled", () => !screen.TurnOverlay.Visible && screen.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase)),
            new Act("press Next turn to continue the interrupted turn, and measure it", () =>
            {
                started = Time.GetTicksMsec();
                screen.TryHotkey("Space");
                screen.TurnOverlay.Advance(1); // the runner froze real time; step the reveal like a frame would
            }),
            new AssertQuery("pressing Next turn resumed processing", () => screen.IsEndingTurn && localHost.IsProcessingTurn),
            new WaitUntil("the overlay names the civ currently moving", () => screen.TurnOverlay.Visible && screen.TurnOverlay.DetailText.Contains(" of ", StringComparison.Ordinal), 1200),
            new AssertQuery("the overlay names the civ, its count and the cancel key", () =>
            {
                string detail = screen.TurnOverlay.DetailText;
                return screen.TurnOverlay.HeadingText.Length > 0 && detail.Contains(" - ", StringComparison.Ordinal)
                    && screen.TurnOverlay.HintText == NextTurnProgressModel.CancelHint;
            }),
            new AssertQuery("the resumed turn shows no stale cancelled status under the card", () =>
                screen.TurnOverlay.Visible && !screen.StatusText.Contains("cancelled", StringComparison.OrdinalIgnoreCase)),
            new AssertQuery("the first civ of the turn is America, one of eight", () => screen.TurnOverlay.DetailText == "America - 1 of 8"),
            new Capture("overlay", "A late-game eight-civ End turn in progress: a compact card under the navigation strip names the civ moving ('America - 1 of 8'), with a progress bar and 'Esc to cancel'"),
            new Act("zoom the map with the wheel while the overlay is up", () =>
            {
                zoomBefore = screen.Map.Zoom;
                overlayUp = screen.TurnOverlay.Visible;
                endingWhenPanned = screen.IsEndingTurn;
                InputSimulator.SimulateWheel(host.GetViewport(), new Vector2(80, 320), up: true);
            }),
            new AssertQuery("the camera still zooms under the overlay", () => overlayUp && endingWhenPanned && screen.Map.Zoom != zoomBefore),
            new Act("open the menu while the turn runs", () =>
            {
                endingWhenOpened = screen.IsEndingTurn;
                InputSimulator.SimulateMouseClick(host.GetViewport(), screen.MenuButton.GetGlobalRect().GetCenter());
            }),
            new AssertQuery("the in-game menu opens during the turn", () => screen.GameMenu.Visible && endingWhenOpened),
            new Act("close the menu", () => screen.GameMenu.Close()),
            new WaitUntil("the turn is processed", () => !screen.IsEndingTurn && game.Turns > startTurn, 3000),
            new Act("record the measured wall time", () =>
            {
                measured = Time.GetTicksMsec() - started;
                GD.Print($"[scenario] late-game 8-civ end turn measured {measured} ms");
            }),
            new AssertQuery("the window kept drawing while the civs played (no long frame)", () => ScenarioRunner.LongestFrameMs < MaxFrameMs),
            new AssertQuery("the overlay is gone and the game moved on", () => !screen.TurnOverlay.Visible && !localHost.IsProcessingTurn && game.Turns == startTurn + 1 && screen.Turn == game.Turns),
        ];
    }
}
