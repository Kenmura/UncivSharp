using Unciv.Logic.Automation.Civilization;
using Unciv.Logic.Automation.Unit;
using Unciv.View;
using TileClass = Unciv.Logic.Map.Tiles.Tile;

namespace Unciv.Core.Tests.Logic.Automation;

// T409. C#-only cases for the per-unit dangerous-tiles cache in UnitAutomation.GetDangerousTiles. The Kotlin UnitAutomation.kt
// has no test fixture for getDangerousTiles; expected values are derived from its source.
public partial class UnitAutomationTests
{
    private TileView TileViewOf(TileClass tile) => new GameView(testGame.GameInfo, civ).TileMapView.GetTile(tile);

    [Fact]
    public void GetDangerousTilesIsCachedPerUnitTileAndId()
    {
        var worker = testGame.AddUnit("Worker", civ, testGame.GetTile(0, 0));
        testGame.AddUnit("Warrior", enemy, testGame.GetTile(3, 0));
        War();
        var cache = new AutomationCache();
        var view = ViewOf(worker);

        var first = UnitAutomation.GetDangerousTiles(view, cache);
        Assert.NotEmpty(first);

        // A second call for the same unit on the same tile returns the cached instance, not a recomputed set.
        var second = UnitAutomation.GetDangerousTiles(view, cache);
        Assert.Same(first, second);
        Assert.Single(cache.DangerousTiles);

        // Moving the unit to another tile changes the key's tile half, so a fresh set is computed and cached.
        worker.RemoveFromTile();
        worker.PutInTile(testGame.GetTile(0, 5));
        var third = UnitAutomation.GetDangerousTiles(view, cache);
        Assert.NotSame(first, third);
        Assert.Equal(2, cache.DangerousTiles.Count);
    }

    [Fact]
    public void DangerousTilesUseTheLocalAttackRangeNotTheBareRange()
    {
        var warrior = testGame.AddUnit("Warrior", enemy, testGame.GetTile(0, 0));
        var worker = testGame.AddUnit("Worker", civ, testGame.GetTile(1, 0));
        War();
        var cache = new AutomationCache();
        int localAttackRange = (warrior.GetMaxMovement() - 1) + warrior.GetRange();
        Assert.True(localAttackRange > warrior.GetRange()); // the local version extends past the bare range (movement > 1)

        var dangerous = UnitAutomation.GetDangerousTiles(ViewOf(worker), cache);

        // A tile exactly at the local attack range from the warrior is dangerous, one past it is not.
        Assert.Contains(TileViewOf(testGame.GetTile(localAttackRange, 0)), dangerous);
        Assert.DoesNotContain(TileViewOf(testGame.GetTile(localAttackRange + 1, 0)), dangerous);
    }
}
