using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Unciv.Commands;
using Unciv.Commands.Log;
using Unciv.Commands.Turn;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Net;
using Unciv.Net.Visibility;
using Unciv.Utils;

namespace Unciv.Cli;

/// <summary>
/// T261: the turn-time profiling harness. <c>bench</c> generates fixed-seed games (no saves needed), plays them through the command layer exactly like the
/// BNW soak (scripted human, AI civs through <see cref="RunAiTurn"/>, a state-hash checkpoint after every human turn start) and reports where the wall time of
/// each turn goes. The engine only announces named phases through <see cref="TurnProbe"/>; every clock reading is made here, so the engine stays clock-free
/// and the state hashes are identical with and without the instrumentation (<c>--no-probe</c> proves it per run, BenchTests per build).
/// T351 adds the unit-automation/pathfinding drill-down (its own <c>callProfile</c> report section), a GC-collection column per phase and the slowest turns.
/// </summary>
public static class BenchRunner
{
    public const string Usage = """
        bench [--scenario small|medium|six|all] [--turns N] [--repeat N] [--json FILE|-] [--ruleset NAME] [--host-turns K] [--recipients R] [--host-parallel N] [--host-views full|incremental] [--no-probe] [--golden FILE]
              Profile end-turn time. small: 4 civs + 2 city states, Small map, 50 turns. medium: 8 civs + 8 city states, Medium map, 200 turns.
              six (T342): 6 civs with 3 human seats + 6 city states, Medium map, 200 turns, the late-game host-view bench.
              Per turn: wall time of the whole turn (the human's turn, every AI civ, the checkpoint hash), split by phase; at the end the top 20 phases by
              inclusive time, the time per civ and per command type, the five slowest turns with their phases, the unit-automation/pathfinding call profile
              (T351), and (medium, the last K turns, default 3) the host-side ViewDelta cost per recipient
              (R recipients, default 3) measured around every command. Every phase row carries the gen0/gen1/gen2 collections that started inside it.
              --json writes the report ('-' = standard output). --turns replaces the turn count.
              --repeat N (T350, 1-20) plays every scenario N times and reports the median of the runs (the median run's detail, plus the min/max/spread
              of the late median and the total time); the state hashes must be equal across runs. --golden compares the median run.
              --host-parallel N (T297, 2-64) builds the recipients' views on up to N tasks the way GameHost does with HostLimits.ParallelViewBuilding: capture and
              build+encode become one timed step each (per-recipient phases are not attributed), so compare the host section with a run without the flag.
              --host-views full|incremental (T342, default incremental) chooses the view builder the host section measures, so a pair of runs compares them.
              --no-probe runs without the phase probe (wall time and hashes only; the per-turn state hashes must be equal to a probed run).
              --golden FILE compares every turn's state hash with migration/design/perf-golden.json (exit 1 on a difference).
              Use a Release build for numbers: dotnet run -c Release --project Unciv.Cli -- bench --scenario small --turns 10
        """;

    private static readonly string[] Nations = ["Babylon", "Greece", "Rome", "Egypt", "China", "England", "France", "Russia"];

    /// <summary>A named, fully seeded game.</summary>
    public sealed record Scenario(string Name, int Majors, int CityStates, string MapSizeName, string MapTypeName, long GameSeed, long MapSeed, int Turns, int HostTurns, int Humans = 1);

    public static readonly Scenario Small = new("small", 4, 2, "Small", MapType.SmallContinents, 11L, 5L, 50, 0);

    public static readonly Scenario Medium = new("medium", 8, 8, "Medium", MapType.ContinentAndIslands, 23L, 9L, 200, 3);

    /// <summary>T342: the late-game host-view bench the ticket asks for: 6 civs, 3 human seats (all but the first idle), 6 city states, 200 turns.</summary>
    public static readonly Scenario Six = new("six", 6, 6, "Medium", MapType.ContinentAndIslands, 31L, 13L, 200, 3, Humans: 3);

    /// <summary>What a scenario run is told besides the scenario.</summary>
    public sealed record Options(bool Probe = true, int? Turns = null, int? HostTurns = null, int Recipients = 3, string? Ruleset = null, int HostParallel = 0,
        bool HostIncrementalViews = true);

    // The phases that make up a turn, in the order the report lists them per turn.
    private static readonly string[] TurnColumns =
    [
        "Command.Apply", "TurnManager.StartTurn", "TurnManager.AutomateTurn", "TurnManager.EndTurn", "RefreshTransientCaches",
        "RecordRankingStats", "ProcessLeagueTurn", "Hash.FullHash",
    ];

    // The phases that are attributed to a civ (the subject of the scope).
    private static readonly string[] CivPhases = ["TurnManager.StartTurn", "TurnManager.AutomateTurn", "TurnManager.EndTurn"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(CliArgs cli, TextWriter output, Action<CliArgs> loadRulesets)
    {
        var scenarioName = cli.Get("scenario") ?? "all";
        var turns = cli.Get("turns") == null ? (int?)null : cli.GetInt("turns", 0, 1, 10_000);
        var repeat = cli.GetInt("repeat", 1, 1, 20);
        var hostTurns = cli.Get("host-turns") == null ? (int?)null : cli.GetInt("host-turns", 0, 0, 1000);
        var recipients = cli.GetInt("recipients", 3, 1, 8);
        var hostParallel = cli.GetInt("host-parallel", 0, 2, 64);
        var hostViews = cli.Get("host-views") ?? "incremental";
        if (hostViews is not ("full" or "incremental")) throw new UsageException("--host-views must be full or incremental");
        var jsonPath = cli.Get("json");
        var noProbe = cli.Flag("no-probe");
        var goldenPath = cli.Get("golden");
        var ruleset = cli.Get("ruleset");
        // T314: a bad option or scenario is a usage error before any ruleset file is read, so `bench --scenario huge` is
        // exit 2 even when the assets are momentarily unavailable (the argument tests must not depend on the data files).
        cli.RejectUnknown();
        var scenarios = scenarioName switch
        {
            "small" => [Small],
            "medium" => [Medium],
            "six" => [Six],
            "all" => new[] { Small, Medium, Six },
            _ => throw new UsageException($"--scenario '{scenarioName}' is not one of: small, medium, six, all"),
        };
        loadRulesets(cli);
        var options = new Options(!noProbe, turns, hostTurns, recipients, ruleset, hostParallel, hostViews == "incremental");
        var results = scenarios.Select(it => RunRepeated(it, options, repeat, output)).ToList();
        var report = new JsonObject
        {
            ["machine"] = Machine(),
            ["probe"] = options.Probe,
            ["repeat"] = repeat,
            ["ruleset"] = options.Ruleset ?? BaseRuleset.Civ_V_BNW.FullName(),
            ["scenarios"] = new JsonArray(results.Select(ToJson).ToArray()),
        };
        var text = report.ToJsonString(JsonOptions);
        if (jsonPath == "-") output.WriteLine(text);
        else if (jsonPath != null) File.WriteAllText(jsonPath, text + "\n");
        return goldenPath == null ? CliRunner.ExitOk : CheckGolden(goldenPath, results, output);
    }

    /// <summary>
    /// T262: compares the per-turn state hashes of every scenario with a golden file ({"scenario": ["hash", ...]}, one hash per turn from turn 1);
    /// a scenario the file does not list is skipped (a run that compares nothing fails), a shorter run (--turns) is compared as far as it goes.
    /// T350: the comparison uses the median run of <c>--repeat</c>; the other runs must carry the same hashes (a repeat that differs is nondeterminism, not noise).
    /// </summary>
    private static int CheckGolden(string path, IReadOnlyList<ScenarioRuns> runs, TextWriter output)
    {
        var golden = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new UsageException($"--golden {path}: not a JSON object");
        var failed = false;
        var compared = 0;
        foreach (var repeated in runs)
        {
            if (golden[repeated.Scenario.Name] as JsonArray is not { } expected) continue;
            compared++;
            var result = repeated.Representative;
            var actual = result.Hashes;
            var mismatches = Enumerable.Range(0, Math.Min(actual.Count, expected.Count))
                .Where(i => actual[i] != expected[i]?.ToString()).ToList();
            if (actual.Count > expected.Count) mismatches.Add(expected.Count);
            var suffix = repeated.Runs.Count == 1 ? "" : $" (median of {repeated.Runs.Count} runs)";
            if (mismatches.Count == 0)
            {
                output.WriteLine($"golden {result.Scenario.Name}: {actual.Count} turn hashes identical{suffix}");
            }
            else
            {
                failed = true;
                output.WriteLine($"golden {result.Scenario.Name}: {mismatches.Count} turn hashes differ, first at turn {mismatches[0] + 1}{suffix}");
            }
            if (!repeated.HashesAgree(out var run, out var turn))
            {
                failed = true;
                output.WriteLine($"golden {result.Scenario.Name}: run {run + 1} hashes differ from run 1, first at turn {turn + 1}");
            }
        }
        if (compared == 0)
        {
            output.WriteLine($"golden: no scenario of the run is listed in {path}, nothing was compared");
            failed = true;
        }
        return failed ? CliRunner.ExitFailure : CliRunner.ExitOk;
    }

    private static JsonObject Machine() => new()
    {
        ["os"] = RuntimeInformation.OSDescription,
        ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
        ["processorCount"] = Environment.ProcessorCount,
        ["runtime"] = RuntimeInformation.FrameworkDescription,
        ["optimized"] = !System.Diagnostics.Debugger.IsAttached && IsOptimized(),
        ["serverGc"] = System.Runtime.GCSettings.IsServerGC,
    };

    private static bool IsOptimized() =>
        typeof(BenchRunner).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .Cast<System.Diagnostics.DebuggableAttribute>().All(it => !it.IsJITOptimizerDisabled);

    // ---- results

    public sealed class TurnRecord
    {
        public int Turn { get; init; }
        public double WallMs { get; init; }
        public string Hash { get; init; } = "";
        public int Cities { get; init; }
        public int Units { get; init; }
        public double GcPauseMs { get; init; }
        public int Gen0Collections { get; init; }
        public int Gen1Collections { get; init; }
        public int Gen2Collections { get; init; }
        public double AllocMb { get; init; }
        public Dictionary<string, double> InclusiveMs { get; init; } = [];
    }

    public sealed class ScenarioResult
    {
        public required Scenario Scenario { get; init; }
        public required string Ruleset { get; init; }
        public List<TurnRecord> Turns { get; } = [];
        public required Recorder Engine { get; init; }
        public Recorder? Host { get; set; }
        public int HostRecipients { get; set; }
        public int HostCommands { get; set; }
        public int HostFromTurn { get; set; }
        public int HostParallel { get; set; }
        public string HostViews { get; set; } = "";
        public int HostIncrementalRounds;
        public int HostFullRounds;
        public double TotalSeconds { get; set; }
        public string Summary { get; set; } = "";
        public int HumanAliveUntilTurn { get; set; }
        public double FinalSerializeMs { get; set; }
        public double FinalDigestMs { get; set; }

        public IReadOnlyList<string> Hashes => Turns.Select(it => it.Hash).ToList();
    }

    /// <summary>T350: the runs of one scenario under <c>--repeat</c>, the median run and the spread between the runs.</summary>
    public sealed class ScenarioRuns
    {
        public required Scenario Scenario { get; init; }

        public List<ScenarioResult> Runs { get; } = [];

        /// <summary>The run whose late median is the median of the runs (the lower middle one for an even count); its detail is the report.</summary>
        public ScenarioResult Representative
        {
            get
            {
                var ordered = Runs.OrderBy(it => LateMedianMs(it)).ToList();
                return ordered[(ordered.Count - 1) / 2];
            }
        }

        public SpreadSummary LateMedianWallMs => Spread(Runs.Select(it => LateMedianMs(it)).ToList());

        public SpreadSummary TotalSeconds => Spread(Runs.Select(it => it.TotalSeconds).ToList());

        /// <summary>False when a later run's per-turn state hashes differ from the first run's (same seeds must give the same game).</summary>
        public bool HashesAgree(out int run, out int turn)
        {
            var first = Runs[0].Hashes;
            for (var i = 1; i < Runs.Count; i++)
            {
                var other = Runs[i].Hashes;
                var limit = Math.Min(first.Count, other.Count);
                for (var t = 0; t < limit; t++)
                    if (first[t] != other[t])
                    {
                        run = i;
                        turn = t;
                        return false;
                    }
                if (first.Count != other.Count)
                {
                    run = i;
                    turn = limit;
                    return false;
                }
            }
            run = 0;
            turn = 0;
            return true;
        }
    }

    /// <summary>The median of several runs and the spread around it (T350); the spread is (max - min) as a percentage of the median.</summary>
    public sealed record SpreadSummary(double Median, double Min, double Max, double SpreadPercent);

    // ---- the clock (the only place of the solution outside the server layer that reads one for the engine's sake)

    private static long Timestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static double TicksToMs(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>T351: totals of one phase name: how often it ran, the time it took with its children and without, and the GCs that ran inside it.</summary>
    public sealed class Agg
    {
        public long Count;
        public long Inclusive;
        public long Exclusive;
        public long AllocBytes;

        /// <summary>Process-wide collections that started inside the phase (nested phases all count the same collection).</summary>
        public long Gc0;
        public long Gc1;
        public long Gc2;

        public void Add(long inclusive, long exclusive, long allocBytes = 0, long gc0 = 0, long gc1 = 0, long gc2 = 0)
        {
            Count++;
            Inclusive += inclusive;
            Exclusive += exclusive;
            AllocBytes += allocBytes;
            Gc0 += gc0;
            Gc1 += gc1;
            Gc2 += gc2;
        }
    }

    /// <summary>Nested phase timing: one stack, totals per phase and per (phase, subject), and the same for the turn being played.</summary>
    public sealed class Recorder
    {
        private readonly record struct Frame(string Name, string? Subject, long Start, long Alloc, int Gc0, int Gc1, int Gc2);

        private readonly List<Frame> stack = [];
        private readonly List<long> children = [];

        public long Skew;

        public Dictionary<string, Agg> Total { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Agg> BySubject { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Agg> Turn { get; private set; } = new(StringComparer.Ordinal);

        public Dictionary<string, Agg> TurnSubject { get; private set; } = new(StringComparer.Ordinal);

        public List<Dictionary<string, Agg>> TurnHistory { get; } = [];

        public List<Dictionary<string, Agg>> TurnSubjectHistory { get; } = [];

        public long Now() => Timestamp() - Skew;

        public void Enter(string name, string? subject)
        {
            stack.Add(new Frame(name, subject, Now(), GC.GetAllocatedBytesForCurrentThread(),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
            children.Add(0);
        }

        public void Exit(string name, string? subject)
        {
            var top = stack[^1];
            if (top.Name != name) throw new InvalidOperationException($"phase '{name}' ended while '{top.Name}' is open");
            var inclusive = Now() - top.Start;
            var exclusive = inclusive - children[^1];
            var alloc = GC.GetAllocatedBytesForCurrentThread() - top.Alloc;
            var gc0 = GC.CollectionCount(0) - top.Gc0;
            var gc1 = GC.CollectionCount(1) - top.Gc1;
            var gc2 = GC.CollectionCount(2) - top.Gc2;
            stack.RemoveAt(stack.Count - 1);
            children.RemoveAt(children.Count - 1);
            if (children.Count > 0) children[^1] += inclusive;
            Bump(Total, name, inclusive, exclusive, alloc, gc0, gc1, gc2);
            Bump(Turn, name, inclusive, exclusive, alloc, gc0, gc1, gc2);
            if (subject == null) return;
            var key = name + "|" + subject;
            Bump(BySubject, key, inclusive, exclusive, alloc, gc0, gc1, gc2);
            Bump(TurnSubject, key, inclusive, exclusive, alloc, gc0, gc1, gc2);
        }

        private static void Bump(Dictionary<string, Agg> map, string key, long inclusive, long exclusive, long alloc, long gc0, long gc1, long gc2)
        {
            if (!map.TryGetValue(key, out var agg)) map[key] = agg = new Agg();
            agg.Add(inclusive, exclusive, alloc, gc0, gc1, gc2);
        }

        /// <summary>Closes the books of one turn: its per-phase totals go to the history.</summary>
        public void EndTurn()
        {
            TurnHistory.Add(Turn);
            TurnSubjectHistory.Add(TurnSubject);
            Turn = new Dictionary<string, Agg>(StringComparer.Ordinal);
            TurnSubject = new Dictionary<string, Agg>(StringComparer.Ordinal);
        }
    }

    /// <summary>The probe: routes the engine's phases to the engine recorder, or (while the host measurement runs) to the host recorder.</summary>
    private sealed class BenchProbe : ITurnProbe
    {
        private readonly Recorder engine;

        public BenchProbe(Recorder engine)
        {
            this.engine = engine;
            Active = engine;
        }

        public Recorder Active { get; set; }

        public bool Paused { get; set; }

        /// <summary>Called when a command is about to be applied / was applied: the host measurement hangs on these.</summary>
        public Action? BeforeCommand { get; set; }

        public Action? AfterCommand { get; set; }

        public void Enter(string phase, string? subject)
        {
            if (Paused) return;
            if (ReferenceEquals(Active, engine) && phase == "Command.Apply") BeforeCommand?.Invoke();
            Active.Enter(phase, subject);
        }

        public void Exit(string phase, string? subject)
        {
            if (Paused) return;
            Active.Exit(phase, subject);
            if (ReferenceEquals(Active, engine) && phase == "Command.Apply") AfterCommand?.Invoke();
        }
    }

    // ---- running a scenario

    private static GameSetupInfo Setup(Scenario scenario, string ruleset) => new(
        new GameParameters
        {
            BaseRuleset = ruleset,
            UseCiv5Ai = true,
            NumberOfCityStates = scenario.CityStates,
            EspionageEnabled = true,
            NoBarbarians = false,
            VictoryTypes = ["Time"],   // a conquest or a victory must not end the measured game early
            MaxTurns = 10_000,
            Players = Enumerable.Range(0, scenario.Majors)
                .Select(i => new Player(Nations[i % Nations.Length], i < scenario.Humans ? PlayerType.Human : PlayerType.AI)).ToList(),
        },
        new MapParameters { Type = scenario.MapTypeName, Shape = MapShape.Rectangular, Seed = scenario.MapSeed, MapSize = new MapSize(scenario.MapSizeName), BaseRuleset = ruleset });

    /// <summary>
    /// Plays <paramref name="options"/>.Turns turns of the scenario (a turn = one <see cref="ScriptedHuman.PlayTurns"/> round: the human, every AI civ, the checkpoint).
    /// Runs on a big-stack thread like the soak. The per-turn state hash is taken after the turn, outside the timing.
    /// </summary>
    public static ScenarioResult RunScenario(Scenario scenario, Options options, TextWriter? log = null)
    {
        ScenarioResult? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = RunOnThread(scenario, options, log); }
            catch (Exception e) { failure = e; }
        }, 256 * 1024 * 1024)
        { IsBackground = true };
        worker.Start();
        worker.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result.NotNull();
    }

    private static ScenarioResult RunOnThread(Scenario scenario, Options options, TextWriter? log)
    {
        var ruleset = options.Ruleset ?? BaseRuleset.Civ_V_BNW.FullName();
        var setup = Setup(scenario, ruleset);
        var turns = options.Turns ?? scenario.Turns;
        var hostTurns = options.Probe ? Math.Min(options.HostTurns ?? scenario.HostTurns, turns) : 0;
        var header = CommandLogHeader.Create(setup, scenario.GameSeed, 1000L);
        var game = GameStarter.StartNewGame(setup, scenario.GameSeed, 1000L);
        var session = new GameSession(game) { Clock = ScriptedHuman.DeterministicClock(2000L) };
        var writer = new CommandLogWriter(new StringWriter(CultureInfo.InvariantCulture), header);
        var human = new ScriptedHuman(session, Nations[0], writer);

        var engine = new Recorder();
        var result = new ScenarioResult { Scenario = scenario, Ruleset = ruleset, Engine = engine };
        var probe = new BenchProbe(engine);
        var hostFrom = turns - hostTurns + 1;
        result.HostFromTurn = hostTurns > 0 ? hostFrom : 0;
        var host = hostTurns > 0 ? new HostMeasurement(game, probe, options.Recipients, result, options.HostParallel, options.HostIncrementalViews) : null;
        var total = Timestamp();
        game.TurnProbe = options.Probe ? probe : null;
        try
        {
            for (var played = 1; played <= turns; played++)
            {
                if (host != null && played == hostFrom) host.Start();
                var gcPause = GC.GetTotalPauseDuration();
                var allocated = GC.GetTotalAllocatedBytes(false);
                var gen0 = GC.CollectionCount(0);
                var gen1 = GC.CollectionCount(1);
                var gen2 = GC.CollectionCount(2);
                var start = engine.Now();
                var done = scenario.Humans == 1 ? PlayRound(session, human, writer) : PlayMultiHumanRound(session, human, writer);
                var wall = engine.Now() - start;
                if (done == 0) break;
                if (options.Probe) engine.EndTurn();
                // Outside the timing and the probe: the per-turn state hash (the golden list of T262 and the equality test).
                probe.Paused = true;
                var hash = ReplayVerifier.FullHash(game).ToString("x16", CultureInfo.InvariantCulture);
                probe.Paused = false;
                var record = new TurnRecord
                {
                    Turn = game.Turns,
                    WallMs = TicksToMs(wall),
                    Hash = hash,
                    GcPauseMs = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds,
                    Gen0Collections = GC.CollectionCount(0) - gen0,
                    Gen1Collections = GC.CollectionCount(1) - gen1,
                    Gen2Collections = GC.CollectionCount(2) - gen2,
                    AllocMb = (GC.GetTotalAllocatedBytes(false) - allocated) / (1024.0 * 1024.0),
                    Cities = game.Civilizations.Sum(it => it.Cities.Count),
                    Units = game.Civilizations.Sum(it => it.Units.GetCivUnits().Count()),
                };
                if (options.Probe && game.GetCivilization(Nations[0]).IsAlive()) result.HumanAliveUntilTurn = game.Turns;
                if (options.Probe)
                {
                    var last = engine.TurnHistory[^1];
                    foreach (var column in TurnColumns)
                        record.InclusiveMs[column] = last.TryGetValue(column, out var agg) ? TicksToMs(agg.Inclusive) : 0;
                }
                result.Turns.Add(record);
                if (played % 10 == 0 || played == turns)
                    log?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[bench {scenario.Name}] turn {record.Turn,3}: {record.WallMs,8:F1} ms, cities {record.Cities}, units {record.Units}"));
            }
        }
        finally
        {
            game.TurnProbe = null;
        }
        // The checkpoint hash of the final state, split (the checkpoints stop when the scripted human is gone, so the per-turn numbers can hide it).
        probe.Paused = true;
        var t0 = Timestamp();
        var text = Unciv.Logic.Files.UncivFiles.GameInfoToString(game, forceZip: false);
        var t1 = Timestamp();
        _ = StateHasher.ComputeHash(h => h.Add(text));
        var t2 = Timestamp();
        probe.Paused = false;
        result.FinalSerializeMs = TicksToMs(t1 - t0);
        result.FinalDigestMs = TicksToMs(t2 - t1);
        result.TotalSeconds = TicksToMs(Timestamp() - total) / 1000.0;
        result.Summary = string.Create(CultureInfo.InvariantCulture,
            $"turn={game.Turns} civs={game.Civilizations.Count} cities={game.Civilizations.Sum(it => it.Cities.Count)} units={game.Civilizations.Sum(it => it.Units.GetCivUnits().Count())}");
        return result;
    }

    /// <summary>T342: one round of a scenario with more than one human seat. The first human plays through <see cref="ScriptedHuman"/>; the other seats
    /// take their turns without scripted actions (the bench measures the host views, not the players' choices).</summary>
    private static int PlayMultiHumanRound(GameSession session, ScriptedHuman human, CommandLogWriter writer)
    {
        var game = session.Game;
        var before = game.Turns;
        var steps = 0;
        void Server(GameCommand command)
        {
            var result = session.SubmitAsServer(command);
            if (!result.Accepted) throw new InvalidOperationException($"{command.Type} refused: {result.Validation.Message}");
            writer.Sync(session);
        }
        while (game.Turns < before + 1 && game.TurnPhase is not TurnPhase.GameOver && steps++ < 100_000)
        {
            switch (game.TurnPhase)
            {
                case TurnPhase.AutoPlayers auto:
                    Server(new RunAiTurn { CivId = auto.NextCiv.CivId });
                    break;
                case TurnPhase.AwaitingInput waiting when !game.IsTurnStarted(waiting.Civ):
                    Server(new BeginTurn { CivId = waiting.Civ.CivId });
                    break;
                case TurnPhase.AwaitingInput waiting when waiting.Civ.CivId == Nations[0] && waiting.Civ.IsAlive():
                    human.PlayCurrentTurn();
                    break;
                case TurnPhase.AwaitingInput waiting:
                    Server(new ForceEndTurn { CivId = waiting.Civ.CivId });
                    break;
                default:
                    return game.Turns - before;
            }
        }
        return game.Turns - before;
    }

    // One round: the human plays (or, when the human is gone, the host keeps the civs going) until the next human turn has begun.
    private static int PlayRound(GameSession session, ScriptedHuman human, CommandLogWriter writer)
    {
        var game = session.Game;
        if (game.GetCivilization(Nations[0]).IsAlive()) return human.PlayTurns(1);
        var before = game.Turns;
        var steps = 0;
        while (game.Turns < before + 1 && game.TurnPhase is not TurnPhase.GameOver && steps++ < 100_000)
        {
            GameCommand command = game.TurnPhase switch
            {
                TurnPhase.AutoPlayers auto => new RunAiTurn { CivId = auto.NextCiv.CivId },
                TurnPhase.AwaitingInput waiting when !game.IsTurnStarted(waiting.Civ) => new BeginTurn { CivId = waiting.Civ.CivId },
                TurnPhase.AwaitingInput waiting => new ForceEndTurn { CivId = waiting.Civ.CivId },
                _ => throw new InvalidOperationException("Unexpected turn phase " + game.TurnPhase),
            };
            var result = session.SubmitAsServer(command);
            if (!result.Accepted) throw new InvalidOperationException($"{command.Type} refused: {result.Validation.Message}");
            writer.Sync(session);
        }
        return game.Turns - before;
    }

    /// <summary>
    /// What the host does after every accepted command for each connected recipient (GameHost.CaptureViewMarks / BroadcastDelta): capture the recipient's
    /// view before, build the snapshot diff and delta after, encode it. Measured on its own recorder around each command; the time it takes is added to the
    /// engine recorder's skew, so the engine numbers are those of a game without recipients. Events are not passed (the filtered-event part is small).
    /// </summary>
    private sealed class HostMeasurement(GameInfo game, BenchProbe probe, int recipientCount, ScenarioResult result, int parallel, bool incremental)
    {
        private readonly Recorder recorder = new();
        private List<(Civilization Civ, ClientSnapshot Snapshot)> recipients = [];
        private List<ViewDeltaBuilder.ViewMark> marks = [];
        private List<IncrementalViewBuilder.State?> states = [];
        private long seq;

        public void Start()
        {
            result.Host = recorder;
            var status = new TurnStatusDto { Ended = [], Deadline = null, Phase = "Playing" };
            recipients = game.Civilizations.Where(it => it.IsMajorCiv() && it.IsAlive()).Take(recipientCount)
                .Select(it => (it, SnapshotBuilder.Build(game, it, 0, status))).ToList();
            states = recipients.Select(it => incremental ? IncrementalViewBuilder.Seed(game, it.Civ, it.Snapshot) : null).ToList();
            result.HostRecipients = recipients.Count;
            result.HostViews = incremental ? "incremental" : "full";
            result.HostParallel = parallel >= 2 && recipients.Count >= 2 ? parallel : 0;
            probe.BeforeCommand = Before;
            probe.AfterCommand = After;
        }

        /// <summary>Runs work(0..recipients-1) like GameHost.BuildPerRecipient (strided over at most <c>parallel</c> tasks, the caller blocks until all end).</summary>
        private T[] Fan<T>(Func<int, T> work)
        {
            var savedProbe = game.TurnProbe;
            game.TurnProbe = null;
            try
            {
                var results = new T[recipients.Count];
                var workers = Math.Min(parallel, recipients.Count);
                // JUSTIFY: T297 bench of the host's optional parallel view building; read-only on the game, one recipient per index, the engine is idle meanwhile.
                var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
                {
                    for (var i = w; i < results.Length; i += workers) results[i] = work(i);
                    return 0;
                })).ToArray();
                Task.WaitAll(tasks);
                return results;
            }
            finally
            {
                game.TurnProbe = savedProbe;
            }
        }

        private void Before() => Measure(() =>
        {
            if (parallel >= 2 && recipients.Count >= 2)
            {
                recorder.Enter("Host.Capture", null);
                marks = Fan(i => ViewDeltaBuilder.Capture(game, recipients[i].Civ)).ToList();
                recorder.Exit("Host.Capture", null);
                return;
            }
            marks = [];
            foreach (var (civ, _) in recipients)
            {
                recorder.Enter("Host.Capture", null);
                marks.Add(ViewDeltaBuilder.Capture(game, civ));
                recorder.Exit("Host.Capture", null);
            }
        });

        /// <summary>T342: the one recipient's view, incremental when the bench runs that mode and the builder can prove it safe, else the full build.</summary>
        private (ViewDelta Delta, ClientSnapshot After) BuildView(int i, Civilization civ, ClientSnapshot before, TurnStatusDto status)
        {
            if (incremental && IncrementalViewBuilder.TryBuildWithDiff(game, civ, before, marks[i], [], seq, status, states[i]) is { } built)
            {
                Interlocked.Increment(ref result.HostIncrementalRounds);
                states[i] = built.State;
                return (built.Delta, built.After);
            }
            Interlocked.Increment(ref result.HostFullRounds);
            var pair = ViewDeltaBuilder.BuildWithDiff(game, civ, before, marks[i], [], seq, status);
            states[i] = incremental ? IncrementalViewBuilder.Seed(game, civ, pair.After) : null;
            return pair;
        }

        private void After() => Measure(() =>
        {
            var status = new TurnStatusDto { Ended = [], Deadline = null, Phase = "Playing" };
            seq++;
            result.HostCommands++;
            if (parallel >= 2 && recipients.Count >= 2)
            {
                recorder.Enter("Host.BuildDelta", null);
                var built = Fan(i =>
                {
                    var (delta, after) = BuildView(i, recipients[i].Civ, recipients[i].Snapshot, status);
                    return (After: after, Frame: ProtocolCodec.Encode(delta));
                });
                recorder.Exit("Host.BuildDelta", null);
                for (var i = 0; i < built.Length; i++) recipients[i] = (recipients[i].Civ, built[i].After);
                return;
            }
            for (var i = 0; i < recipients.Count; i++)
            {
                var (civ, before) = recipients[i];
                recorder.Enter("Host.BuildDelta", null);
                var (delta, after) = BuildView(i, civ, before, status);
                recorder.Exit("Host.BuildDelta", null);
                recorder.Enter("Host.Encode", null);
                _ = ProtocolCodec.Encode(delta);
                recorder.Exit("Host.Encode", null);
                recipients[i] = (civ, after);
            }
        });

        private void Measure(Action work)
        {
            var start = Timestamp();
            var inner = probe.Active;
            probe.Active = recorder;
            try { work(); }
            finally
            {
                probe.Active = inner;
                inner.Skew += Timestamp() - start;
            }
        }
    }

    // ---- report

    private static double R(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    private static double Ms(long ticks) => R(TicksToMs(ticks), 3);

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>T350: the median of the values and their min/max, with the spread as a percentage of the median.</summary>
    public static SpreadSummary Spread(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new SpreadSummary(0, 0, 0, 0);
        var median = Median(values);
        var min = values.Min();
        var max = values.Max();
        return new SpreadSummary(median, min, max, median == 0 ? 0 : (max - min) / median * 100);
    }

    /// <summary>The median wall time of the last fifth of the turns (the number T262's target is measured against).</summary>
    public static double LateMedianMs(ScenarioResult result)
    {
        var count = Math.Max(1, result.Turns.Count / 5);
        return Median(result.Turns.TakeLast(count).Select(it => it.WallMs).ToList());
    }

    private static Dictionary<string, Agg> Sum(IEnumerable<Dictionary<string, Agg>> turns)
    {
        var sum = new Dictionary<string, Agg>(StringComparer.Ordinal);
        foreach (var turn in turns)
            foreach (var (key, agg) in turn)
            {
                if (!sum.TryGetValue(key, out var total)) sum[key] = total = new Agg();
                total.Count += agg.Count;
                total.Inclusive += agg.Inclusive;
                total.Exclusive += agg.Exclusive;
                total.AllocBytes += agg.AllocBytes;
                total.Gc0 += agg.Gc0;
                total.Gc1 += agg.Gc1;
                total.Gc2 += agg.Gc2;
            }
        return sum;
    }

    private static JsonObject AggJson(string name, Agg agg, int turns) => new()
    {
        ["phase"] = name,
        ["calls"] = agg.Count,
        ["inclusiveMs"] = Ms(agg.Inclusive),
        ["exclusiveMs"] = Ms(agg.Exclusive),
        ["inclusiveMsPerTurn"] = turns == 0 ? 0 : R(TicksToMs(agg.Inclusive) / turns, 3),
        ["exclusiveMsPerTurn"] = turns == 0 ? 0 : R(TicksToMs(agg.Exclusive) / turns, 3),
        ["meanMsPerCall"] = agg.Count == 0 ? 0 : R(TicksToMs(agg.Inclusive) / agg.Count, 4),
        ["allocMbPerTurn"] = turns == 0 ? 0 : R(agg.AllocBytes / (1024.0 * 1024.0) / turns, 2),
        ["gc0"] = agg.Gc0,
        ["gc1"] = agg.Gc1,
        ["gc2"] = agg.Gc2,
        ["gc0PerTurn"] = turns == 0 ? 0 : R((double)agg.Gc0 / turns, 2),
        ["gc1PerTurn"] = turns == 0 ? 0 : R((double)agg.Gc1 / turns, 2),
        ["gc2PerTurn"] = turns == 0 ? 0 : R((double)agg.Gc2 / turns, 2),
    };

    private static JsonArray TopPhases(Dictionary<string, Agg> sum, int turns, int top) =>
        new(sum.OrderByDescending(it => it.Value.Inclusive).ThenBy(it => it.Key, StringComparer.Ordinal).Take(top)
            .Select(it => (JsonNode)AggJson(it.Key, it.Value, turns)).ToArray());

    /// <summary>T351: every call-level phase of the unit-automation drill-down, so the tail below the window's top 20 stays visible.</summary>
    private static bool IsCallProfilePhase(string name) =>
        name.StartsWith("Unit.", StringComparison.Ordinal)
        || name.StartsWith("Path.", StringComparison.Ordinal)
        || name.StartsWith("Threat.", StringComparison.Ordinal)
        || name == "AI.UnitPriorityOrder";

    private static JsonArray CallProfile(Dictionary<string, Agg> sum, int turns) =>
        new(sum.Where(it => IsCallProfilePhase(it.Key))
            .OrderByDescending(it => it.Value.Inclusive).ThenBy(it => it.Key, StringComparer.Ordinal)
            .Select(it => (JsonNode)AggJson(it.Key, it.Value, turns)).ToArray());

    /// <summary>T351: the slowest turns of the run with their per-phase breakdown, so a spike can be read without the raw per-turn lists.</summary>
    private static JsonArray SpikeTurns(ScenarioResult result, int top)
    {
        if (result.Engine.TurnHistory.Count != result.Turns.Count || result.Turns.Count == 0) return [];
        return new JsonArray(result.Turns.Select((turn, index) => (Turn: turn, Index: index))
            .OrderByDescending(it => it.Turn.WallMs)
            .Take(top)
            .Select(it => (JsonNode)new JsonObject
            {
                ["turn"] = it.Turn.Turn,
                ["wallMs"] = R(it.Turn.WallMs, 2),
                ["gcPauseMs"] = R(it.Turn.GcPauseMs, 2),
                ["gen0"] = it.Turn.Gen0Collections,
                ["gen1"] = it.Turn.Gen1Collections,
                ["gen2"] = it.Turn.Gen2Collections,
                ["allocMb"] = R(it.Turn.AllocMb, 1),
                ["phases"] = new JsonArray(result.Engine.TurnHistory[it.Index]
                    .OrderByDescending(p => p.Value.Inclusive).ThenBy(p => p.Key, StringComparer.Ordinal).Take(6)
                    .Select(p => (JsonNode)AggJson(p.Key, p.Value, 1)).ToArray()),
            }).ToArray());
    }

    private static JsonNode Window(ScenarioResult result, int from, int count)
    {
        var turns = result.Turns.Skip(from).Take(count).ToList();
        var phases = Sum(result.Engine.TurnHistory.Skip(from).Take(count));
        var subjects = Sum(result.Engine.TurnSubjectHistory.Skip(from).Take(count));
        return new JsonObject
        {
            ["fromTurn"] = turns.Count == 0 ? 0 : turns[0].Turn,
            ["toTurn"] = turns.Count == 0 ? 0 : turns[^1].Turn,
            ["turns"] = turns.Count,
            ["medianWallMs"] = R(Median(turns.Select(it => it.WallMs).ToList()), 2),
            ["meanWallMs"] = R(turns.Count == 0 ? 0 : turns.Average(it => it.WallMs), 2),
            ["phases"] = TopPhases(phases, turns.Count, 20),
            ["civs"] = CivRows(subjects, turns.Count),
        };
    }

    private static JsonArray CivRows(Dictionary<string, Agg> subjects, int turns)
    {
        var civs = subjects.Keys.Where(it => CivPhases.Any(phase => it.StartsWith(phase + "|", StringComparison.Ordinal))).Select(it => it[(it.IndexOf('|', StringComparison.Ordinal) + 1)..]).Distinct(StringComparer.Ordinal).ToList();
        IEnumerable<JsonObject> rows = civs.Select(civ =>
        {
            var row = new JsonObject { ["civ"] = civ };
            long all = 0;
            foreach (var phase in CivPhases)
            {
                var ticks = subjects.TryGetValue(phase + "|" + civ, out var agg) ? agg.Inclusive : 0;
                all += ticks;
                row[phase] = R(turns == 0 ? 0 : TicksToMs(ticks) / turns, 3);
            }
            row["totalMsPerTurn"] = R(turns == 0 ? 0 : TicksToMs(all) / turns, 3);
            return row;
        });
        return new JsonArray(rows.OrderByDescending(it => (double)it["totalMsPerTurn"].NotNull()).ThenBy(it => (string?)it["civ"] ?? "", StringComparer.Ordinal).Select(it => (JsonNode)it).ToArray());
    }

    private static JsonObject ToJson(ScenarioRuns repeated)
    {
        var result = repeated.Representative;
        var scenario = result.Scenario;
        var count = result.Turns.Count;
        var quarter = Math.Max(1, count / 4);
        var late = repeated.LateMedianWallMs;
        var total = repeated.TotalSeconds;
        var json = new JsonObject
        {
            ["name"] = scenario.Name,
            ["majors"] = scenario.Majors,
            ["cityStates"] = scenario.CityStates,
            ["mapSize"] = scenario.MapSizeName,
            ["mapType"] = scenario.MapTypeName,
            ["gameSeed"] = scenario.GameSeed,
            ["mapSeed"] = scenario.MapSeed,
            ["ruleset"] = result.Ruleset,
            ["repeat"] = repeated.Runs.Count,
            ["turnsPlayed"] = count,
            ["finalState"] = result.Summary,
            ["totalSeconds"] = R(total.Median, 2),
            ["totalSecondsMin"] = R(total.Min, 2),
            ["totalSecondsMax"] = R(total.Max, 2),
            ["totalSecondsSpreadPercent"] = R(total.SpreadPercent, 1),
            ["humanAliveUntilTurn"] = result.HumanAliveUntilTurn,
            ["finalHashSerializeMs"] = R(result.FinalSerializeMs, 2),
            ["finalHashDigestMs"] = R(result.FinalDigestMs, 2),
            ["lateMedianWallMs"] = R(late.Median, 2),
            ["lateMedianWallMsMin"] = R(late.Min, 2),
            ["lateMedianWallMsMax"] = R(late.Max, 2),
            ["lateMedianWallMsSpreadPercent"] = R(late.SpreadPercent, 1),
            ["runs"] = new JsonArray(repeated.Runs.Select((it, i) => (JsonNode)new JsonObject
            {
                ["run"] = i + 1,
                ["turnsPlayed"] = it.Turns.Count,
                ["lateMedianWallMs"] = R(LateMedianMs(it), 2),
                ["totalSeconds"] = R(it.TotalSeconds, 2),
            }).ToArray()),
            ["turns"] = new JsonArray(result.Turns.Select(it => (JsonNode)new JsonObject
            {
                ["turn"] = it.Turn,
                ["wallMs"] = R(it.WallMs, 2),
                ["hash"] = it.Hash,
                ["cities"] = it.Cities,
                ["units"] = it.Units,
                ["gcPauseMs"] = R(it.GcPauseMs, 2),
                ["gen0"] = it.Gen0Collections,
                ["gen1"] = it.Gen1Collections,
                ["gen2"] = it.Gen2Collections,
                ["allocMb"] = R(it.AllocMb, 1),
                ["inclusiveMs"] = new JsonObject(it.InclusiveMs.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)R(p.Value, 2)))),
            }).ToArray()),
            ["spikeTurns"] = SpikeTurns(result, 5),
        };
        if (result.Engine.TurnHistory.Count == count && count > 0)
        {
            json["all"] = Window(result, 0, count);
            json["firstQuarter"] = Window(result, 0, quarter);
            json["lastQuarter"] = Window(result, count - quarter, quarter);
            json["callProfile"] = new JsonObject
            {
                ["note"] = "T351: the unit-automation and pathfinding probe points, inclusive and exclusive ms per turn, with the GC collections that started inside each phase",
                ["all"] = CallProfile(Sum(result.Engine.TurnHistory), count),
                ["lastQuarter"] = CallProfile(Sum(result.Engine.TurnHistory.Skip(count - quarter)), quarter),
            };
            json["commands"] = new JsonArray(result.Engine.BySubject.Where(it => it.Key.StartsWith("Command.Apply|", StringComparison.Ordinal))
                .OrderByDescending(it => it.Value.Inclusive)
                .Select(it => (JsonNode)AggJson(it.Key["Command.Apply|".Length..], it.Value, count)).ToArray());
        }
        if (result.Host != null && result.HostCommands > 0)
        {
            var perRecipientCommand = Math.Max(1, result.HostCommands * Math.Max(1, result.HostRecipients));
            var host = new JsonObject
            {
                ["fromTurn"] = result.HostFromTurn,
                ["recipients"] = result.HostRecipients,
                ["commands"] = result.HostCommands,
                ["parallelTasks"] = result.HostParallel,
                ["views"] = result.HostViews,
                ["incrementalRounds"] = result.HostIncrementalRounds,
                ["fullRounds"] = result.HostFullRounds,
                ["note"] = "ViewDeltaBuilder.Capture before and the configured view builder plus ProtocolCodec.Encode after every command, per recipient, without engine events; engine times above exclude this",
            };
            foreach (var (name, agg) in result.Host.Total.OrderBy(it => it.Key, StringComparer.Ordinal))
            {
                var row = AggJson(name, agg, 0);
                row["msPerRecipientCommand"] = R(TicksToMs(agg.Inclusive) / perRecipientCommand, 3);
                ((JsonObject)(host["phases"] ??= new JsonObject()))[name] = row;
            }
            json["host"] = host;
        }
        return json;
    }

    /// <summary>A short text of the report for the terminal.</summary>
    public static void WriteSummary(ScenarioResult result, TextWriter output)
    {
        var count = result.Turns.Count;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"scenario {result.Scenario.Name}: {count} turns in {result.TotalSeconds:F1} s ({result.Summary}); late median {LateMedianMs(result):F1} ms/turn"));
        if (result.Engine.TurnHistory.Count != count || count == 0) return;
        var last = Sum(result.Engine.TurnHistory.Skip(count - Math.Max(1, count / 4)));
        var turns = Math.Max(1, count / 4);
        output.WriteLine("  last quarter, inclusive ms per turn (top 12; gc = gen0/gen1/gen2 collections started inside):");
        foreach (var (name, agg) in last.OrderByDescending(it => it.Value.Inclusive).ThenBy(it => it.Key, StringComparer.Ordinal).Take(12))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {name,-36} {TicksToMs(agg.Inclusive) / turns,9:F2} (self {TicksToMs(agg.Exclusive) / turns:F2}, alloc {agg.AllocBytes / (1024.0 * 1024.0) / turns:F1} MB, gc {agg.Gc0 / (double)turns:F1}/{agg.Gc1 / (double)turns:F1}/{agg.Gc2 / (double)turns:F1})"));
        WriteSpikeTurns(result, output);
        output.WriteLine("  unit-automation call profile, inclusive ms per turn, last quarter (top 10 of the drill-down):");
        foreach (var (name, agg) in last.Where(it => IsCallProfilePhase(it.Key))
                     .OrderByDescending(it => it.Value.Inclusive).ThenBy(it => it.Key, StringComparer.Ordinal).Take(10))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {name,-36} {TicksToMs(agg.Inclusive) / turns,9:F2} (self {TicksToMs(agg.Exclusive) / turns:F2}, calls {agg.Count / turns:F1}/turn, gc {agg.Gc0 / (double)turns:F1}/{agg.Gc1 / (double)turns:F1}/{agg.Gc2 / (double)turns:F1})"));
    }

    /// <summary>T351: the five slowest turns and what they spent their time on (the spike turns the follow-up names).</summary>
    public static void WriteSpikeTurns(ScenarioResult result, TextWriter output)
    {
        var count = result.Turns.Count;
        if (result.Engine.TurnHistory.Count != count || count == 0) return;
        output.WriteLine("  spike turns (slowest 5), wall and top phases in ms:");
        foreach (var (turn, index) in result.Turns.Select((turn, index) => (turn, index)).OrderByDescending(it => it.turn.WallMs).Take(5))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    turn {turn.Turn,3}: {turn.WallMs,8:F1} ms, cities {turn.Cities}, units {turn.Units}, gc pause {turn.GcPauseMs:F1} ms, gc {turn.Gen0Collections}/{turn.Gen1Collections}/{turn.Gen2Collections}, alloc {turn.AllocMb:F1} MB"));
            foreach (var (name, agg) in result.Engine.TurnHistory[index]
                         .OrderByDescending(it => it.Value.Inclusive).ThenBy(it => it.Key, StringComparer.Ordinal).Take(6))
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      {name,-34} {TicksToMs(agg.Inclusive),9:F2} (self {TicksToMs(agg.Exclusive):F2}, gc {agg.Gc0}/{agg.Gc1}/{agg.Gc2})"));
        }
    }

    private static ScenarioRuns RunRepeated(Scenario scenario, Options options, int repeat, TextWriter output)
    {
        var runs = new ScenarioRuns { Scenario = scenario };
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"running scenario {scenario.Name} ({scenario.Majors} civs, {scenario.CityStates} city states, {scenario.MapSizeName} {scenario.MapTypeName}, {options.Turns ?? scenario.Turns} turns){(repeat == 1 ? "" : $", {repeat} runs")}"));
        for (var i = 0; i < repeat; i++)
        {
            // The per-turn progress lines would be repeated N times; for a repeated run print one line per run instead.
            var result = RunScenario(scenario, options, repeat == 1 ? output : null);
            runs.Runs.Add(result);
            if (repeat > 1)
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  run {i + 1}/{repeat}: late median {LateMedianMs(result):F1} ms/turn, {result.TotalSeconds:F1} s total"));
        }
        WriteSummary(runs.Representative, output);
        if (repeat > 1)
        {
            var late = runs.LateMedianWallMs;
            var total = runs.TotalSeconds;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  across {repeat} runs: late median {late.Median:F1} ms/turn (min {late.Min:F1}, max {late.Max:F1}, spread {late.SpreadPercent:F1}%); total {total.Median:F1} s (min {total.Min:F1}, max {total.Max:F1}, spread {total.SpreadPercent:F1}%)"));
        }
        return runs;
    }
}
