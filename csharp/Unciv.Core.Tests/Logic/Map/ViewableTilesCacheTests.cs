using Unciv.Core.Tests.Support;
using Unciv.Logic.Map;
using MapUnitClass = Unciv.Logic.Map.MapUnits.MapUnit;
using TileClass = Unciv.Logic.Map.Tiles.Tile;

namespace Unciv.Core.Tests.Logic.Map;

// T349: MapUnit.UpdateVisibleTiles caches the normal GetViewableTiles result per unit, keyed on position, sight range, the map's transients and
// TileMap.TerrainRevision. These tests pin the invalidation (terrain change without movement) and the read-only rule of a cache hit (T296): a
// second call with the same key must return the same set and write no field of the game graph.
[Collection("RulesetCache")]
public class ViewableTilesCacheTests
{
    private static (TestGame Game, MapUnitClass Unit) NewGame()
    {
        var game = new TestGame();
        game.MakeHexagonalMap(5);
        var civ = game.AddCiv();
        var unit = game.AddDefaultMeleeUnitWithUniques(civ, game.GetTile(HexCoord.Zero));
        return (game, unit);
    }

    private static void AssertMatchesFullScan(TestGame game, MapUnitClass unit)
    {
        var expected = game.TileMap.GetViewableTiles(unit.GetTile().Position, unit.GetVisibilityRange());
        Assert.Equal(expected.Count, unit.ViewableTiles.Count);
        Assert.Equal(expected.ToHashSet(), unit.ViewableTiles);
    }

    private static TileClass Tile(TestGame game, int x, int y) => game.GetTile(HexCoord.Of(x, y));

    [Fact]
    public void TerrainChangeWithoutMoving_InvalidatesTheCache()
    {
        var (game, unit) = NewGame();
        // A hill at distance 1 hides the forest at distance 2 (both are at the same calculated height).
        game.SetTileTerrainAndFeatures(HexCoord.Of(1, 0), "Hill");
        var forest = game.SetTileTerrainAndFeatures(HexCoord.Of(2, 0), "Grassland", "Forest");

        unit.UpdateVisibleTiles(false);
        AssertMatchesFullScan(game, unit);
        Assert.DoesNotContain(forest, unit.ViewableTiles);
        var hiddenRevision = game.TileMap.TerrainRevision;

        // Flattening the hill changes only terrain, not the unit's position or range.
        game.SetTileTerrainAndFeatures(HexCoord.Of(1, 0), "Desert");
        Assert.NotEqual(hiddenRevision, game.TileMap.TerrainRevision);
        unit.UpdateVisibleTiles(false);

        AssertMatchesFullScan(game, unit);
        Assert.Contains(forest, unit.ViewableTiles);
    }

    [Fact]
    public void MovingTheUnit_InvalidatesTheCache()
    {
        var (game, unit) = NewGame();
        unit.UpdateVisibleTiles(false);
        var atOrigin = unit.ViewableTiles.ToHashSet();

        var destination = Tile(game, 3, 0);
        unit.PutInTile(destination);
        unit.UpdateVisibleTiles(false);

        AssertMatchesFullScan(game, unit);
        Assert.NotEqual(atOrigin, unit.ViewableTiles);
    }

    [Fact]
    public void SightRangeChange_InvalidatesTheCache()
    {
        var (game, unit) = NewGame();
        unit.UpdateVisibleTiles(false);
        var normalRange = unit.ViewableTiles.ToHashSet();

        var promotion = new Unciv.Models.Ruleset.Unit.Promotion { Name = "T349 extra sight", Uniques = ["[+2] Sight"] };
        game.Ruleset.UnitPromotions.Put(promotion.Name, promotion);
        unit.Promotions.AddPromotion(promotion.Name, isFree: true); // also calls UpdateVisibleTiles
        unit.UpdateVisibleTiles(false);

        Assert.Equal(4, unit.GetVisibilityRange());
        AssertMatchesFullScan(game, unit);
        Assert.NotEqual(normalRange, unit.ViewableTiles);
    }

    [Fact]
    public void CacheHit_IsReadOnlyLikeT296()
    {
        var (game, unit) = NewGame();
        unit.UpdateVisibleTiles(false); // warm-up: allowed to fill the cache

        var before = ObjectGraphFingerprint.Of(game.GameInfo);
        unit.UpdateVisibleTiles(false);
        var after = ObjectGraphFingerprint.Of(game.GameInfo);

        Assert.Empty(before.Except(after));
        Assert.Empty(after.Except(before));
    }
}
