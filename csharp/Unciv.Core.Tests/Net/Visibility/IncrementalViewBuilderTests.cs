using System.Globalization;
using Unciv.Commands;
using Unciv.Commands.Log;
using Unciv.Commands.Turn;
using Unciv.Harness;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map.Tiles;
using Unciv.Net.Visibility;

namespace Unciv.Core.Tests.Net.Visibility;

// T342: the incremental builder must produce exactly the frames of the full build, from the same recorded command sequence, and fall back to the full
// build whenever it cannot prove the reuse safe.
[Collection("RulesetCache")]
public sealed class IncrementalViewBuilderTests
{
    private static readonly TurnStatusDto Status = new() { Ended = [], Phase = "playing" };

    private static string FirstDifference(string expected, string actual)
    {
        var at = Enumerable.Range(0, System.Math.Min(expected.Length, actual.Length)).FirstOrDefault(i => expected[i] != actual[i], -1);
        if (at < 0 && expected.Length == actual.Length) return "none";
        return $"at {at}: ...{expected.Substring(System.Math.Max(0, at - 150), System.Math.Min(300, expected.Length - System.Math.Max(0, at - 150)))}"
            + $" || ...{actual.Substring(System.Math.Max(0, at - 150), System.Math.Min(300, actual.Length - System.Math.Max(0, at - 150)))}";
    }

    private sealed class Recipient
    {
        public Recipient(Civilization viewer, GameInfo game, long seq)
        {
            Viewer = viewer;
            Full = SnapshotBuilder.Build(game, viewer, seq, Status);
            Mirror = SnapshotBuilder.Build(game, viewer, seq, Status);
            Incremental = Full;
            State = IncrementalViewBuilder.Seed(game, viewer, Full);
            Mark = ViewDeltaBuilder.Capture(game, viewer);
        }

        public ClientSnapshot Full { get; set; }
        public ClientSnapshot Incremental { get; set; }
        public ClientSnapshot Mirror { get; set; }
        public IncrementalViewBuilder.State State { get; set; }
        public ViewDeltaBuilder.ViewMark Mark { get; set; }
        public readonly Civilization Viewer;
    }

    [Fact]
    public void ScriptedLateGameCommands_ProduceByteIdenticalSnapshotsAndDiffs()
    {
        var created = ScenarioGame.Create();
        var session = new GameSession(created.Game) { Clock = ScriptedHuman.DeterministicClock(2000L) };
        var game = session.Game;
        var recipients = new[] { ScenarioGame.HumanA, ScenarioGame.HumanB }
            .Select(it => new Recipient(game.GetCivilization(it), game, session.LastSeq))
            .ToList();
        var accepted = 0;
        var fallbacks = 0;
        string? lastRefusal = null;

        void Step(Func<CommandResult> run)
        {
            foreach (var r in recipients) r.Mark = ViewDeltaBuilder.Capture(game, r.Viewer);
            var result = run();
            if (!result.Accepted) { lastRefusal = result.Validation.Code + ": " + result.Validation.Message; return; }
            lastRefusal = null;
            accepted++;
            foreach (var r in recipients)
            {
                var events = result.Events.OfType<Unciv.Commands.Events.EngineEvent>().ToList();
                var (fullDelta, fullAfter) = ViewDeltaBuilder.BuildWithDiff(game, r.Viewer, r.Full, r.Mark, events, result.Seq, Status);
                var built = IncrementalViewBuilder.TryBuildWithDiff(game, r.Viewer, r.Incremental, r.Mark, events, result.Seq, Status, r.State);
                if (built == null)
                {
                    // Unprovable change (a reveal or map-content revision, a stale state): the host falls back to the full build and reseeds.
                    fallbacks++;
                    r.Full = fullAfter;
                    r.Incremental = fullAfter;
                    r.State = IncrementalViewBuilder.Seed(game, r.Viewer, fullAfter);
                    var fallbackDiff = SnapshotBuilder.FromElement<SnapshotDiff>(fullDelta.Diff!.Value);
                    r.Mirror = fallbackDiff.Apply(r.Mirror);
                    continue;
                }
                var expected = SnapshotBuilder.ToCanonicalJson(fullAfter);
                var actual = SnapshotBuilder.ToCanonicalJson(built.After);
                Assert.True(expected == actual, $"{r.Viewer.CivId} seq {result.Seq}: snapshot differs {FirstDifference(expected, actual)}");
                Assert.Equal(fullDelta.ViewHash, built.Delta.ViewHash);
                Assert.Equal(SnapshotBuilder.ToJson(fullDelta.Diff), SnapshotBuilder.ToJson(built.Delta.Diff));
                Assert.Equal(SnapshotBuilder.ToJson(fullDelta.Events), SnapshotBuilder.ToJson(built.Delta.Events));
                var diff = SnapshotBuilder.FromElement<SnapshotDiff>(built.Delta.Diff!.Value);
                Assert.Equal(built.After.ViewHash, diff.Apply(r.Mirror).ViewHash);
                r.Full = fullAfter;
                r.Incremental = built.After;
                r.Mirror = diff.Apply(r.Mirror);
                r.State = built.State;
            }
        }

        for (var step = 0; step < 300 && game.TurnPhase is not TurnPhase.GameOver; step++)
        {
            switch (game.TurnPhase)
            {
                case TurnPhase.AutoPlayers auto:
                    Step(() => session.SubmitAsServer(new RunAiTurn { CivId = auto.NextCiv.CivId }));
                    break;
                case TurnPhase.AwaitingInput waiting when !game.IsTurnStarted(waiting.Civ):
                    Step(() => session.SubmitAsServer(new BeginTurn { CivId = waiting.Civ.CivId }));
                    break;
                case TurnPhase.AwaitingInput waiting:
                    Step(() => session.SubmitAsServer(new ForceEndTurn { CivId = waiting.Civ.CivId }));
                    break;
                default:
                    step = int.MaxValue;
                    break;
            }
        }
        Assert.True(accepted > 10, $"only {accepted} commands were applied (last refusal: {lastRefusal ?? "none"}; phase: {game.TurnPhase}; {fallbacks} fallbacks)");
        // The equivalence above is only evidence if most commands actually took the incremental path: a regression that always falls back must fail here.
        Assert.True(accepted - fallbacks > accepted / 2,
            $"only {accepted - fallbacks} of {accepted} accepted commands built incrementally ({fallbacks} fell back), so the equivalence path was barely exercised");
    }

    // The test host runs in globalization-invariant mode (no ICU, no named cultures): derive a hostile culture by hand, as EndToEndDeterminismTests does.
    private static CultureInfo OddCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.PositiveSign = "#";
        return culture;
    }

    [Fact]
    public void AChangedVisibleTileAtANegativeCoordinate_StaysInTheDiffUnderAHostileCulture()
    {
        var s = Fresh();
        var (_, visible) = ViewDeltaBuilder.TileSets(s.Game, s.Viewer);
        var target = visible.First(it => it.x < 0 || it.y < 0);
        // A road change on a visible tile bumps no map-content or reveal revision, so the builder must rebuild it and the diff must find its key.
        var gameTile = s.Game.TileMap[target];
        gameTile.SetRoadStatus(gameTile.RoadStatus == RoadStatus.None ? RoadStatus.Road : RoadStatus.None, null);

        var saved = CultureInfo.CurrentCulture;
        try
        {
            // The incremental tile key and SnapshotDiff.TileKey must agree regardless of the ambient culture's negative sign.
            CultureInfo.CurrentCulture = OddCulture();
            var mark = ViewDeltaBuilder.Capture(s.Game, s.Viewer);
            var (fullDelta, fullAfter) = ViewDeltaBuilder.BuildWithDiff(s.Game, s.Viewer, s.Before, mark, [], 1, Status);
            var built = IncrementalViewBuilder.TryBuildWithDiff(s.Game, s.Viewer, s.Before, mark, [], 1, Status, s.State);
            Assert.NotNull(built);
            Assert.Equal(SnapshotBuilder.ToCanonicalJson(fullAfter), SnapshotBuilder.ToCanonicalJson(built!.After));
            Assert.Equal(fullDelta.ViewHash, built.Delta.ViewHash);
            Assert.Equal(SnapshotBuilder.ToJson(fullDelta.Diff), SnapshotBuilder.ToJson(built.Delta.Diff));

            var mirror = SnapshotBuilder.FromElement<SnapshotDiff>(built.Delta.Diff!.Value).Apply(s.Before);
            Assert.Equal(built.After.ViewHash, mirror.ViewHash);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- fallbacks: anything the builder cannot prove safe goes back to the full build ------------------------------------------------

    private static (GameInfo Game, Civilization Viewer, ClientSnapshot Before, ViewDeltaBuilder.ViewMark Mark, IncrementalViewBuilder.State State) Fresh(string civ = ScenarioGame.HumanA)
    {
        var session = ScenarioGame.Create();
        var game = session.Game;
        var viewer = game.GetCivilization(civ);
        var before = SnapshotBuilder.Build(game, viewer, 0, Status);
        return (game, viewer, before, ViewDeltaBuilder.Capture(game, viewer), IncrementalViewBuilder.Seed(game, viewer, before));
    }

    private static IncrementalViewBuilder.Built? Build((GameInfo Game, Civilization Viewer, ClientSnapshot Before, ViewDeltaBuilder.ViewMark Mark, IncrementalViewBuilder.State State) s,
        ViewDeltaBuilder.ViewMark? mark = null) =>
        IncrementalViewBuilder.TryBuildWithDiff(s.Game, s.Viewer, s.Before, mark ?? s.Mark, [], 1, Status, s.State);

    [Fact]
    public void UnchangedState_BuildsIncrementally()
    {
        var s = Fresh();
        Assert.NotNull(Build(s));
    }

    [Fact]
    public void AnAiViewer_AlwaysUsesTheFullBuild()
    {
        var s = Fresh(ScenarioGame.Ai);
        Assert.Null(Build(s));
    }

    [Fact]
    public void AStaleState_UsesTheFullBuild()
    {
        var s = Fresh();
        var other = SnapshotBuilder.Build(s.Game, s.Viewer, 0, Status);
        Assert.Null(IncrementalViewBuilder.TryBuildWithDiff(s.Game, s.Viewer, other, s.Mark, [], 1, Status, s.State));
    }

    [Fact]
    public void AMapContentChange_UsesTheFullBuild()
    {
        var s = Fresh();
        s.Game.TileMap.TileList[0].SetTileResource((string?)null); // a resource/site change bumps the map-content revision
        Assert.Null(Build(s));
    }

    [Fact]
    public void ANewTechnologyOrPolicy_UsesTheFullBuild()
    {
        var s = Fresh();
        s.Viewer.Tech.TechsResearched.Add(s.Game.Ruleset.Technologies.Keys.First(it => !s.Viewer.Tech.IsResearched(it)));
        Assert.Null(Build(s));
    }

    [Fact]
    public void AMarkWithoutTheVisibleSet_UsesTheFullBuild()
    {
        var s = Fresh();
        Assert.Null(Build(s, s.Mark with { Visible = null }));
    }
}
