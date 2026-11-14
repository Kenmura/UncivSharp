using System.Text.Json.Nodes;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Files;
using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using Unciv.Net.Visibility;
using RulesetClass = Unciv.Models.Ruleset.Ruleset;

namespace Unciv.Core.Tests.Net.Visibility;

// T303: a civ's last-seen tile owners (the fogged borders a client draws) live in the game, so they survive save and load, a reconnect rebuilds the same view from them,
// and a save from before the field loads with nothing remembered rather than a guess from the live map.
[Collection("RulesetCache")]
public sealed class LastSeenOwnerSaveTests : IDisposable
{
    private static readonly TurnStatusDto Status = new() { Ended = [], Deadline = 1700000000000, Phase = "playing" };

    private readonly RulesetClass ruleset;

    public LastSeenOwnerSaveTests()
    {
        RulesetCache.Current.Clear();
        var jsons = Path.Combine(RepoRoot(), "data", "jsons");
        RulesetCache.Current.LoadRulesets(new DirectoryRulesetFileSource(jsons, Path.Combine(jsons, "no mods here")), noMods: true);
        ruleset = RulesetCache.Current.GetVanillaRuleset();
    }

    public void Dispose() => RulesetCache.Current.Clear();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "jsons"))) return dir.FullName;
        throw new DirectoryNotFoundException("data/jsons not found above " + AppContext.BaseDirectory);
    }

    private GameInfo NewGame()
    {
        var game = StartGame();
        var babylon = game.GetCivilization("Babylon"); // found the capital (a new game starts with settlers only), which also runs a sight update
        var settler = babylon.Units.GetCivUnits().First(it => it.BaseUnit.IsCityFounder());
        babylon.AddCity(settler.CurrentTile.Position, settler);
        return game;
    }

    private GameInfo StartGame() => GameStarter.StartNewGame(
        new GameSetupInfo(
            new GameParameters
            {
                BaseRuleset = ruleset.Name,
                NumberOfCityStates = 0,
                VictoryTypes = ruleset.Victories.Keys.ToList(),
                Players = [new Player("Babylon", PlayerType.Human), new Player("Greece", PlayerType.Human), new Player("Rome", PlayerType.AI)],
            },
            new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny }),
        1L, 1000L);

    private static string Save(GameInfo game) => UncivFiles.GameInfoToString(game, forceZip: false);

    private static ClientSnapshot View(GameInfo game, string civ) => SnapshotBuilder.Build(game, game.GetCivilization(civ), 1, Status);

    /// <summary>An explored tile Babylon cannot see right now, with a remembered owner planted on it.</summary>
    private static Unciv.Logic.Map.Tiles.Tile PlantFogged(GameInfo game, string owner)
    {
        var babylon = game.GetCivilization("Babylon");
        var tile = game.TileMap.TileList.First(it => !babylon.ViewableTiles.Contains(it));
        tile.SetExplored(babylon, true);
        babylon.SetLastSeenOwner(tile.Position, owner);
        return tile;
    }

    [Fact]
    public void ASightUpdateRecordsTheOwnersOfWhatIsInSightForHumansOnly()
    {
        var game = NewGame();
        var babylon = game.GetCivilization("Babylon");
        var capital = babylon.GetCapital()!.GetCenterTile();
        Assert.Equal("Babylon", babylon.GetLastSeenOwner(capital.Position));
        Assert.Equal(Civilization.UnownedMark, babylon.GetLastSeenOwner(babylon.ViewableTiles.First(it => it.GetOwner() == null).Position));
        Assert.Null(game.GetCivilization("Rome").LastSeenOwner);
    }

    [Fact]
    public void LastSeenOwnersSurviveSaveAndLoad_AndAReconnectRebuildsTheSameFoggedView()
    {
        var game = NewGame();
        var fogged = PlantFogged(game, "Greece");
        var loaded = UncivFiles.GameInfoFromString(Save(game));
        var tile = View(loaded, "Babylon").Tiles.Single(it => it.X == fogged.Position.X && it.Y == fogged.Position.Y);
        Assert.False(tile.Visible);
        Assert.Equal("Greece", tile.LastOwner);
        Assert.Null(tile.Owner); // the live owner stays secret
        Assert.Equal("Babylon", loaded.GetCivilization("Babylon").GetLastSeenOwner(game.GetCivilization("Babylon").GetCapital()!.Location));
        // Greece never saw that tile: nothing of Babylon's memory reaches it
        Assert.All(View(loaded, "Greece").Tiles, it => Assert.NotEqual("Greece", it.LastOwner));
        // Loading the same text again (a second reconnect) gives the same view, hash included
        var again = UncivFiles.GameInfoFromString(Save(loaded));
        Assert.Equal(View(loaded, "Babylon").ViewHash, View(again, "Babylon").ViewHash);
    }

    [Fact]
    public void ASaveFromBeforeTheFieldLoadsWithNothingRememberedForFoggedTiles()
    {
        var game = NewGame();
        var fogged = PlantFogged(game, "Greece");
        var root = JsonNode.Parse(Save(game))!.AsObject();
        foreach (var civ in root["civilizations"]!.AsArray()) civ!.AsObject().Remove("lastSeenOwner");
        var loaded = UncivFiles.GameInfoFromString(root.ToJsonString());
        var babylon = loaded.GetCivilization("Babylon");
        Assert.Null(babylon.GetLastSeenOwner(fogged.Position)); // unknown, not a guess from the live map
        Assert.NotNull(babylon.GetLastSeenOwner(babylon.GetCapital()!.Location)); // what is in sight on load is recorded again
        var snapshot = View(loaded, "Babylon");
        Assert.All(snapshot.Tiles.Where(it => !it.Visible), it => Assert.Null(it.LastOwner));
    }

    [Fact]
    public void TheTileDistanceHelpersWalkTheSameRingsAsTheMap()
    {
        var game = NewGame();
        var tile = game.GetCivilization("Babylon").GetCapital()!.GetCenterTile();
        var ring = new List<Unciv.Logic.Map.Tiles.Tile>();
        tile.ForEachTileAtDistance(1, ring.Add);
        var filtered = new List<Unciv.Logic.Map.Tiles.Tile>();
        tile.ForEachTileAtDistance(1, it => it.IsLand, filtered.Add);
        var range = new List<Unciv.Logic.Map.Tiles.Tile>();
        tile.ForEachTileInDistanceRange(new Unciv.Utils.IntRange(1, 1), range.Add);
        var rangeFiltered = new List<Unciv.Logic.Map.Tiles.Tile>();
        tile.ForEachTileInDistanceRange(new Unciv.Utils.IntRange(1, 1), it => it.IsLand, rangeFiltered.Add);
        Assert.Equal(ring, range);
        Assert.Equal(filtered, rangeFiltered);
        Assert.Equal(ring.Where(it => it.IsLand), filtered);
    }
}
