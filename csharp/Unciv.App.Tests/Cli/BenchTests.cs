using System.Text.Json.Nodes;
using Unciv.Cli;
using Unciv.Models.Ruleset;

namespace Unciv.Core.Tests.Cli;

// T261: the turn-time bench. Its numbers are for a human (Release build, minutes); the build only proves that the instrumentation changes nothing:
// the per-turn state hashes of a probed run (with the host-side ViewDelta measurement on for the last turns) equal those of a run without the probe.
[Collection("RulesetCache")]
public sealed class BenchTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("unciv-bench-").FullName;

    public void Dispose()
    {
        RulesetCache.Clear();
        Directory.Delete(dir, recursive: true);
    }

    private JsonNode Bench(params string[] args)
    {
        var path = Path.Combine(dir, $"bench{Directory.GetFiles(dir).Length}.json");
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliRunner.Run(["bench", "--scenario", "small", "--json", path, .. args], output, error);
        Assert.True(code == 0, error.ToString());
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static List<string> Hashes(JsonNode report) =>
        report["scenarios"]![0]!["turns"]!.AsArray().Select(it => (string)it!["hash"]!).ToList();

    [Fact]
    public void StateHashesAreIdenticalWithAndWithoutTheProbe()
    {
        var probed = Bench("--turns", "10", "--host-turns", "3", "--recipients", "2");
        var plain = Bench("--turns", "10", "--no-probe");
        Assert.Equal(10, Hashes(plain).Count);
        Assert.Equal(Hashes(plain), Hashes(probed));
        Assert.Equal(10, Hashes(plain).Distinct().Count());
    }

    [Fact]
    public void TheReportNamesThePhasesAndTheHostCost()
    {
        var scenario = Bench("--turns", "6", "--host-turns", "2", "--recipients", "2")["scenarios"]![0]!;
        var phases = scenario["all"]!["phases"]!.AsArray().Select(it => (string)it!["phase"]!).ToList();
        Assert.Contains("Command.Apply", phases);
        Assert.Contains("TurnManager.AutomateTurn", phases);
        Assert.Contains("RefreshTransientCaches", phases);
        Assert.NotEmpty(scenario["all"]!["civs"]!.AsArray());
        Assert.True((int)scenario["host"]!["commands"]! > 0);
        Assert.NotNull(scenario["host"]!["phases"]!["Host.BuildDelta"]);

        // T351: every phase row carries the GC collections, and the drill-down and spike sections are present.
        var topPhase = scenario["all"]!["phases"]!.AsArray()[0]!;
        Assert.NotNull(topPhase["gc0"]);
        Assert.NotNull(topPhase["gc0PerTurn"]);
        var callProfile = scenario["callProfile"]!["lastQuarter"]!.AsArray().Select(it => (string)it!["phase"]!).ToList();
        Assert.Contains("Path.MovementToTiles", callProfile);
        Assert.Contains("Unit.TerrainDamage", callProfile);
        Assert.NotNull(scenario["callProfile"]!["all"]!.AsArray()[0]!["gc2PerTurn"]);
        Assert.Equal(5, scenario["spikeTurns"]!.AsArray().Count);
        var spike = scenario["spikeTurns"]!.AsArray()[0]!;
        Assert.NotNull(spike["wallMs"]);
        Assert.NotNull(spike["gen0"]);
        Assert.NotEmpty(spike["phases"]!.AsArray());
        var turns = scenario["turns"]!.AsArray();
        Assert.NotNull(turns[0]!["gen0"]);
        Assert.NotNull(turns[0]!["gen1"]);
        Assert.NotNull(turns[0]!["gen2"]);
    }

    [Fact]
    public void HostParallel_KeepsTheStateHashes_AndReportsTheTaskCount()
    {
        var sequential = Bench("--turns", "6", "--host-turns", "2", "--recipients", "3");
        var parallel = Bench("--turns", "6", "--host-turns", "2", "--recipients", "3", "--host-parallel", "2");
        Assert.Equal(Hashes(sequential), Hashes(parallel));
        var host = parallel["scenarios"]![0]!["host"]!;
        Assert.Equal(2, (int)host["parallelTasks"]!);
        Assert.True((int)host["commands"]! > 0);
        Assert.NotNull(host["phases"]!["Host.BuildDelta"]);
        Assert.Equal(0, (int)sequential["scenarios"]![0]!["host"]!["parallelTasks"]!);
    }

    [Fact]
    public void RepeatedRunsMustAgreeOnTheStateHashes()
    {
        static BenchRunner.ScenarioResult Run(params string[] hashes)
        {
            var result = new BenchRunner.ScenarioResult
            {
                Scenario = BenchRunner.Small,
                Ruleset = "test",
                Engine = new BenchRunner.Recorder(),
            };
            for (var i = 0; i < hashes.Length; i++)
                result.Turns.Add(new BenchRunner.TurnRecord { Turn = i + 1, Hash = hashes[i], WallMs = 10 + i });
            return result;
        }

        static BenchRunner.ScenarioRuns Repeat(params BenchRunner.ScenarioResult[] runs)
        {
            var repeated = new BenchRunner.ScenarioRuns { Scenario = BenchRunner.Small };
            repeated.Runs.AddRange(runs);
            return repeated;
        }

        var equal = Repeat(Run("a", "b"), Run("a", "b"));
        Assert.True(equal.HashesAgree(out _, out _));

        var different = Repeat(Run("a", "b"), Run("a", "c"));
        Assert.False(different.HashesAgree(out var run, out var turn));
        Assert.Equal(1, run);
        Assert.Equal(1, turn);

        var shorter = Repeat(Run("a", "b"), Run("a"));
        Assert.False(shorter.HashesAgree(out run, out turn));
        Assert.Equal(1, run);
        Assert.Equal(1, turn);
    }

    [Fact]
    public void GoldenFileMatchesMismatchesAndSkipsUnlistedScenarios()
    {
        var hashes = Hashes(Bench("--turns", "4", "--no-probe"));
        var golden = Path.Combine(dir, "golden.json");
        string Run(out int code)
        {
            var output = new StringWriter();
            code = CliRunner.Run(["bench", "--scenario", "small", "--turns", "4", "--no-probe", "--golden", golden], output, new StringWriter());
            return output.ToString();
        }

        File.WriteAllText(golden, new JsonObject { ["small"] = new JsonArray(hashes.Select(it => (JsonNode?)it).ToArray()) }.ToJsonString());
        Assert.Contains("4 turn hashes identical", Run(out var code));
        Assert.Equal(0, code);

        var bad = hashes.ToList();
        bad[2] = "deadbeef";
        File.WriteAllText(golden, new JsonObject { ["small"] = new JsonArray(bad.Select(it => (JsonNode?)it).ToArray()) }.ToJsonString());
        Assert.Contains("1 turn hashes differ, first at turn 3", Run(out code));
        Assert.Equal(1, code);

        File.WriteAllText(golden, new JsonObject { ["small"] = new JsonArray(hashes.Take(2).Select(it => (JsonNode?)it).ToArray()) }.ToJsonString());
        Assert.Contains("differ, first at turn 3", Run(out code));
        Assert.Equal(1, code);

        File.WriteAllText(golden, "{}");
        Assert.Contains("nothing was compared", Run(out code));
        Assert.Equal(1, code);

        File.WriteAllText(golden, new JsonObject { ["small"] = new JsonArray(1, 2, 3, 4) }.ToJsonString());
        Assert.Contains("4 turn hashes differ", Run(out code)); // non-string elements are a mismatch, not a crash
        Assert.Equal(1, code);

        File.WriteAllText(golden, "[]");
        Assert.Equal(2, CliRunner.Run(["bench", "--scenario", "small", "--turns", "2", "--no-probe", "--golden", golden], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void MedianAndSpreadSummary()
    {
        Assert.Equal(0, BenchRunner.Median([]));
        Assert.Equal(2, BenchRunner.Median([3, 1, 2]));
        Assert.Equal(2.5, BenchRunner.Median([4, 1, 3, 2]));

        var spread = BenchRunner.Spread([30, 10, 20]);
        Assert.Equal(20, spread.Median);
        Assert.Equal(10, spread.Min);
        Assert.Equal(30, spread.Max);
        Assert.Equal(100, spread.SpreadPercent, 6);

        var points = BenchRunner.Spread([4, 4]);
        Assert.Equal(4, points.Median);
        Assert.Equal(0, points.SpreadPercent, 6);
        Assert.Equal(new BenchRunner.SpreadSummary(0, 0, 0, 0), BenchRunner.Spread([]));
    }

    [Fact]
    public void RepeatReportsTheMedianRunAndTheSpreadBetweenRuns()
    {
        var output = new StringWriter();
        var path = Path.Combine(dir, "repeat.json");
        var code = CliRunner.Run(["bench", "--scenario", "small", "--turns", "3", "--repeat", "2", "--json", path], output, new StringWriter());
        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("2 runs", text);
        Assert.Contains("run 1/2", text);
        Assert.Contains("run 2/2", text);
        Assert.Contains("across 2 runs: late median", text);

        var report = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, (int)report["repeat"]!);
        var scenario = report["scenarios"]![0]!;
        Assert.Equal(2, (int)scenario["repeat"]!);
        var runs = scenario["runs"]!.AsArray();
        Assert.Equal(2, runs.Count);
        Assert.Equal(1, (int)runs[0]!["run"]!);
        Assert.Equal(2, (int)runs[1]!["run"]!);
        Assert.Equal(3, (int)runs[0]!["turnsPlayed"]!);
        Assert.Equal(3, (int)runs[1]!["turnsPlayed"]!);
        var late = (double)scenario["lateMedianWallMs"]!;
        Assert.InRange(late, (double)scenario["lateMedianWallMsMin"]!, (double)scenario["lateMedianWallMsMax"]!);
        Assert.InRange((double)scenario["totalSeconds"]!, (double)scenario["totalSecondsMin"]!, (double)scenario["totalSecondsMax"]!);
        Assert.True((double)scenario["lateMedianWallMsSpreadPercent"]! >= 0);
    }

    [Fact]
    public void RepeatedGoldenComparisonUsesTheMedianRun()
    {
        var hashes = Hashes(Bench("--turns", "3", "--no-probe"));
        var golden = Path.Combine(dir, "golden-repeat.json");
        File.WriteAllText(golden, new JsonObject { ["small"] = new JsonArray(hashes.Select(it => (JsonNode?)it).ToArray()) }.ToJsonString());
        var output = new StringWriter();
        var code = CliRunner.Run(["bench", "--scenario", "small", "--turns", "3", "--repeat", "2", "--no-probe", "--golden", golden], output, new StringWriter());
        Assert.Equal(0, code);
        Assert.Contains("3 turn hashes identical (median of 2 runs)", output.ToString());
    }

    // The golden list is frozen at the pre-T262 engine: after a deliberate engine change regenerate it (bench --json, see design/PERFORMANCE.md).
    [Fact]
    public void TheCommittedGoldenListStillMatchesTheEngine()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "migration", "design", "perf-golden.json"))) root = root.Parent;
        Assert.NotNull(root);
        var output = new StringWriter();
        var code = CliRunner.Run(["bench", "--scenario", "small", "--turns", "25", "--no-probe", "--golden", Path.Combine(root.FullName, "migration", "design", "perf-golden.json")],
            output, new StringWriter());
        Assert.True(code == 0, output.ToString());
        Assert.Contains("25 turn hashes identical", output.ToString());
    }

    [Fact]
    public void MediumScenarioIsAvailable()
    {
        var path = Path.Combine(dir, "medium.json");
        var code = CliRunner.Run(["bench", "--scenario", "medium", "--turns", "1", "--no-probe", "--json", path], new StringWriter(), new StringWriter());
        Assert.Equal(0, code);
        var scenario = JsonNode.Parse(File.ReadAllText(path))!["scenarios"]![0]!;
        Assert.Equal("medium", (string)scenario["name"]!);
        Assert.Equal(1, (int)scenario["repeat"]!);
        Assert.Single(scenario["turns"]!.AsArray());
    }

    [Fact]
    public void HelpAndBadOptionsAreHandled()
    {
        var output = new StringWriter();
        Assert.Equal(0, CliRunner.Run(["bench", "--help"], output, new StringWriter()));
        Assert.Contains("--scenario", output.ToString());
        Assert.Contains("--repeat", output.ToString());
        Assert.Equal(2, CliRunner.Run(["bench", "--scenario", "huge"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, CliRunner.Run(["bench", "--repeat", "0"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, CliRunner.Run(["bench", "--repeat", "21"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, CliRunner.Run(["bench", "--repeat", "many"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, CliRunner.Run(["bench", "--repeat"], new StringWriter(), new StringWriter()));
    }
}
