using System.Diagnostics;
using System.Globalization;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Files;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using Unciv.Utils;
using Xunit.Abstractions;

namespace Unciv.Core.Tests.Determinism;

// T341: the recurring soak matrix that T167/T208/B026 do not cover: the Vanilla ruleset (the third bundled ruleset; the G&K soak and the BNW soak
// already run the other two) and games that load the bundled mods in Mods/ next to a base ruleset. Every run saves and reloads at many points of the
// game (not only at the end) and compares the state hash of the live game with the hash of its reload, so a save/load-only divergence such as T293 is
// caught at the turn that introduces it. The full runs are env-gated like the other soaks (UNCIV_SOAK=1, Category=Soak); the short variants run in
// every build. UNCIV_SOAK_SEED / UNCIV_SOAK_TURNS / UNCIV_SOAK_SAVE_EVERY narrow a run to reproduce a failure (tools/soak.py sets them).
[Collection("RulesetCache")]
[Trait("Category", "Soak")]
public sealed class RulesetSoakTests : IDisposable
{
    private const int DefaultTurnLimit = 300;
    private const int DefaultSaveEvery = 10;
    private static readonly TimeSpan GameTimeout = TimeSpan.FromMinutes(30);

    private static readonly string[] DefaultNations =
        ["Babylon", "Greece", "Rome", "Egypt", "China", "England", "France", "Russia"];

    // golden-mod is the T217 mod fixture (a Testland nation); LateMod is one of the small bundled mods of the mod-loading tests (a Late Hut building).
    private static readonly string[] BundledMods = ["golden-mod", "LateMod"];

    private readonly ITestOutputHelper output;
    private readonly string modsFolder;

    public RulesetSoakTests(ITestOutputHelper output)
    {
        this.output = output;
        modsFolder = Directory.CreateTempSubdirectory("unciv-soak-mods-").FullName;
        CopyDirectory(Fixture("Determinism", "golden-mod"), Path.Combine(modsFolder, "golden-mod"));
        CopyDirectory(Fixture("Models", "Ruleset", "ModLoading", "Mods", "LateMod"), Path.Combine(modsFolder, "LateMod"));
        RulesetCache.Clear();
        var errors = RulesetCache.LoadRulesets(new DirectoryRulesetFileSource(SoakTests.JsonsPath(), modsFolder));
        Assert.Empty(errors);
    }

    public void Dispose()
    {
        RulesetCache.Clear();
        try { Directory.Delete(modsFolder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort: the test result does not depend on it */ }
    }

    private static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, .. parts]);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static int FromEnv(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw == null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return fallback;
        return System.Math.Clamp(value, min, max);
    }

    private static long SeedFromEnv(string name, long fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw != null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    // The matrix. `short` variants are the same games on a Tiny map for the default build; the soak ones are the full run.
    private static List<RulesetSoakConfig> Configs(bool shortGames)
    {
        var turns = shortGames ? 40 : FromEnv("UNCIV_SOAK_TURNS", DefaultTurnLimit, 20, 10_000);
        var saveEvery = FromEnv("UNCIV_SOAK_SAVE_EVERY", shortGames ? 10 : DefaultSaveEvery, 1, turns);
        var seed = SeedFromEnv("UNCIV_SOAK_SEED", 0L);
        long GameSeed(long fallback) => seed == 0 ? fallback : seed;
        var size = shortGames ? MapSize.Tiny : MapSize.Small;
        var type = shortGames ? MapType.Pangaea : MapType.SmallContinents;
        var majors = shortGames ? 4 : 6;
        var cityStates = shortGames ? 2 : 6;
        return
        [
            new RulesetSoakConfig("vanilla", BaseRuleset.Civ_V_Vanilla.FullName(), [], DefaultNations, size, type, majors, cityStates,
                GameSeed(37L), (int)GameSeed(13L) + 100, turns, saveEvery),
            new RulesetSoakConfig("bundled-mods", BaseRuleset.Civ_V_Vanilla.FullName(), BundledMods,
                ["Babylon", "Testland", "Greece", "Rome", "Egypt", "China", "England", "France"], size, type, majors, cityStates,
                GameSeed(41L), (int)GameSeed(17L) + 200, turns, saveEvery),
        ];
    }

    private GameInfo NewGame(RulesetSoakConfig config)
    {
        var ruleset = RulesetCache.Get(config.Ruleset).NotNull();
        var parameters = new GameParameters
        {
            BaseRuleset = config.Ruleset,
            Mods = new LinkedHashSet<string>(config.Mods),
            UseCiv5Ai = true,
            NumberOfCityStates = config.CityStates,
            EspionageEnabled = true,
            NoBarbarians = false,
            VictoryTypes = ruleset.Victories.Keys.ToList(),
            MaxTurns = config.Turns,
            Players = Enumerable.Range(0, config.Majors)
                .Select(i => new Player(config.Nations[i % config.Nations.Length], i == 0 ? PlayerType.Human : PlayerType.AI)).ToList(),
        };
        return GameStarter.StartNewGame(
            new GameSetupInfo(parameters, new MapParameters
            {
                Type = config.Type,
                Shape = MapShape.Rectangular,
                Seed = config.MapSeed,
                MapSize = config.Size,
                BaseRuleset = config.Ruleset,
                Mods = new LinkedHashSet<string>(config.Mods),
            }),
            config.GameSeed, 1000L);
    }

    private static ulong HashOfText(string text) => StateHasher.ComputeHash(h => h.Add(text));

    private RulesetSoakResult Play(RulesetSoakConfig config)
    {
        output.WriteLine($"[{config.Name}] SOAK CONFIG ruleset={config.Ruleset} mods=[{string.Join(",", config.Mods)}] map={config.Size.Name}/{config.Type} "
            + $"majors={config.Majors} cityStates={config.CityStates} gameSeed={config.GameSeed} mapSeed={config.MapSeed} turns={config.Turns} saveEvery={config.SaveEvery}");
        RulesetSoakResult? result = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { result = PlayOnThread(config); }
            catch (Exception ex) { failure = ex; }
        }, 256 * 1024 * 1024)
        { IsBackground = true };
        worker.Start();
        if (!worker.Join(GameTimeout))
            Assert.Fail($"[{config.Name}] soak did not finish within {GameTimeout} (gameSeed={config.GameSeed} mapSeed={config.MapSeed}), reached turn {result?.Turns}");
        if (failure != null) throw new InvalidOperationException($"[{config.Name}] exception (gameSeed={config.GameSeed} mapSeed={config.MapSeed})", failure);
        return result.NotNull();
    }

    private RulesetSoakResult PlayOnThread(RulesetSoakConfig config)
    {
        var game = NewGame(config);
        var total = Stopwatch.StartNew();
        var hashes = new List<ulong>();
        string? winner = null;
        double maxTurn = 0;
        var slowest = 0;
        while (game.Turns < config.Turns && winner == null)
        {
            game.SimulateUntilWin = true;
            game.SimulateMaxTurns = game.Turns + 1;
            var turnWatch = Stopwatch.StartNew();
            game.NextTurn(2000L);
            var seconds = turnWatch.Elapsed.TotalSeconds;
            if (seconds > maxTurn) { maxTurn = seconds; slowest = game.Turns; }
            winner = game.Civilizations.FirstOrDefault(it => it.IsMajorCiv() && it.VictoryManager.HasWon())?.CivName;
            if (game.Turns % config.SaveEvery != 0 && winner == null) continue;

            // Save/reload at many points, not only at the end: the live game and a reload of its own save must have the same state hash. A raw text diff
            // is only built when the hashes differ, so it names the first differing field (this is what caught T293's tile roadOwner at turn 250).
            var raw = UncivFiles.GameInfoToString(game, forceZip: false);
            var liveText = SoakTests.Normalise(raw);
            var liveHash = HashOfText(liveText);
            var loaded = UncivFiles.GameInfoFromString(raw);
            var reloadedText = SoakTests.Normalise(UncivFiles.GameInfoToString(loaded, forceZip: false));
            var reloadedHash = HashOfText(reloadedText);
            if (liveHash != reloadedHash) SoakTests.AssertSameText(liveText, reloadedText, $"after turn {game.Turns}: the game and a reload of its save");
            Assert.Equal(liveHash, reloadedHash);
            Assert.Equal(SoakTests.Digest(game), SoakTests.Digest(loaded));
            hashes.Add(liveHash);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{config.Name}] turn {game.Turns,3}: save/reload hash {liveHash:x16} identical, last turn {seconds:F2}s, elapsed {total.Elapsed.TotalSeconds,7:F1}s, {Summary(game)}"));
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{config.Name}] SOAK DONE turns={game.Turns} winner={winner ?? "(none)"} checkpoints={hashes.Count} total={total.Elapsed.TotalSeconds:F1}s "
            + $"avgTurn={total.Elapsed.TotalSeconds / System.Math.Max(1, game.Turns):F2}s maxTurn={maxTurn:F2}s (turn {slowest}) "
            + $"gameSeed={config.GameSeed} mapSeed={config.MapSeed}"));
        return new RulesetSoakResult(config, game.Turns, winner, maxTurn, total.Elapsed.TotalSeconds, hashes, game);
    }

    private static string Summary(GameInfo game)
    {
        var majors = game.Civilizations.Count(it => it.IsMajorCiv() && !it.IsDefeated());
        var states = game.Civilizations.Count(it => it.IsCityState && !it.IsDefeated());
        return $"majors={majors} cityStates={states} cities={game.Civilizations.Sum(it => it.Cities.Count)} units={game.Civilizations.Sum(it => it.Units.GetCivUnits().Count())}";
    }

    private void AssertSane(RulesetSoakConfig config, RulesetSoakResult result)
    {
        var game = result.Game;
        Assert.Equal(config.Majors, game.Civilizations.Count(it => it.IsMajorCiv()));
        Assert.Equal(config.CityStates, game.Civilizations.Count(it => it.IsCityState));
        Assert.True(result.Hashes.Count > 0, "no save/reload checkpoint was taken");
        Assert.Contains(game.Civilizations, it => it.IsMajorCiv() && it.Cities.Count > 0);
        foreach (var civ in game.Civilizations.Where(it => it.IsMajorCiv() && !it.IsDefeated()))
            Assert.False(float.IsNaN(civ.Stats.StatsForNextTurn.Gold), $"{civ.CivName} gold is NaN");
        Assert.True(result.TotalSeconds / System.Math.Max(1, result.Turns) < 5.0, "average turn time budget (5 s) exceeded");
        Assert.True(result.MaxTurnSeconds < 60.0, "slowest turn budget (60 s) exceeded");
    }

    private void RunAndCheck(RulesetSoakConfig config, bool repeat)
    {
        var first = Play(config);
        if (repeat)
        {
            var second = Play(config);
            Assert.Equal(first.Turns, second.Turns);
            Assert.Equal(first.Winner, second.Winner);
            Assert.Equal(first.Hashes, second.Hashes);
            output.WriteLine($"[{config.Name}] determinism: {first.Hashes.Count} save/reload checkpoints identical across two runs");
        }
        if (config.Turns >= DefaultTurnLimit) Assert.True(first.Turns == config.Turns || first.Winner != null, $"stopped at turn {first.Turns} with no winner");
        AssertSane(config, first);
    }

    public static TheoryData<string> ShortConfigs => new() { "vanilla", "bundled-mods" };

    // Runs in every build: a short Vanilla and a short bundled-mods game with save/reload checks every 10 turns.
    [Theory]
    [MemberData(nameof(ShortConfigs))]
    public void ShortGamesSaveAndReloadAtManyPoints(string name)
    {
        var config = Configs(shortGames: true).Single(it => it.Name == name);
        RunAndCheck(config, repeat: false);
    }

    [SoakFact]
    public void VanillaRuleset300TurnsSavesAndReloadsAtManyPoints()
    {
        var config = Configs(shortGames: false).Single(it => it.Name == "vanilla");
        output.WriteLine("[vanilla] the G&K soak (SoakTests) and the BNW soak (BnwSoakTests) cover the other two bundled rulesets");
        RunAndCheck(config, repeat: true);
    }

    [SoakFact]
    public void BundledMods300TurnsSavesAndReloadsAtManyPoints()
    {
        var config = Configs(shortGames: false).Single(it => it.Name == "bundled-mods");
        output.WriteLine("[bundled-mods] ruleset=" + config.Ruleset + " mods=" + string.Join(",", config.Mods) + " (Testland nation from golden-mod, Late Hut from LateMod)");
        RunAndCheck(config, repeat: true);
    }
}
