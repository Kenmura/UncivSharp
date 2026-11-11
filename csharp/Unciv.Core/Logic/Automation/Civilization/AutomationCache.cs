using Unciv.Logic.Map.Tiles;
using Unciv.View;

namespace Unciv.Logic.Automation.Civilization;

// Ported from core/src/com/unciv/logic/automation/civilization/AutomationCache.kt (T087).
// Kotlin HashMap<TileView, ...> is only looked up, never iterated, so a Dictionary is exact.

/// <summary>
/// Holds data that automation caches while a civ's moves are being automated.
/// Created in NextTurnAutomation.automateCivMoves and passed down to wherever it's needed - it is never stored on game state,
/// so it cannot go stale between turns.
/// </summary>
public class AutomationCache
{
    public class ClosestEnemyTileData(int distanceSearched, List<(TileView Tile, int Distance)> tilesWithEnemies)
    {
        /// <summary>The farthest radius in which we have checked tiles for enemies.
        /// A value of 2 means all enemies at a radius of 2 are in tilesWithEnemies.</summary>
        public int DistanceSearched { get; set; } = distanceSearched;

        /// <summary>Stores the location of the enemy tiles that we saw with the distance at which we saw them.
        /// Tiles are sorted by distance in increasing order.
        /// This allows us to quickly check if they are still alive and if we should search farther.
        /// It is not guaranteed that each tile in this list has an enemy (since they may have died).</summary>
        public List<(TileView Tile, int Distance)> TilesWithEnemies { get; set; } = tilesWithEnemies;
    }

    public Dictionary<TileView, ClosestEnemyTileData> DistanceToClosestEnemyTiles { get; } = [];

    /// <summary>
    /// T409: caches the per-unit dangerous-tile set computed by <c>UnitAutomation.GetDangerousTiles</c>, keyed by the
    /// unit's current tile and its id (so a moved unit or a different unit on the same tile gets a fresh entry).
    /// Like <see cref="DistanceToClosestEnemyTiles"/>, entries are only valid for this civ's turn: the cache is recreated each turn.
    /// </summary>
    public Dictionary<(TileView Tile, int UnitId), HashSet<TileView>> DangerousTiles { get; } = [];

    /// <summary>
    /// T406: caches <c>WorkerAutomation.GetImprovementRanking</c> per (tile, improvement name) so the ranking of one improvement
    /// on one tile is computed once per turn. Keyed by the logic <see cref="Tile"/> (reference identity: the same tile object is
    /// used for the whole turn, and <see cref="Tile.Clone"/> results are distinct and deliberately not shared). Only the
    /// non-road, non-removal results are cached, because those are a pure function of the civ, tile and improvement; road plans
    /// and the recursive removal branch are not.
    /// Like the other entries, entries are only valid for this civ's turn: the cache is recreated each turn.
    /// </summary>
    public Dictionary<(Tile Tile, string Improvement), float> WorkerImprovementRankings { get; } = [];

    /// <summary>
    /// T406: caches the improvement-building problems computed by <c>ImprovementFunctions.GetImprovementBuildingProblems</c> for
    /// <c>WorkerAutomation.ChooseImprovement</c>, keyed by (tile, improvement name). Like the other entries, entries are only valid
    /// for this civ's turn: the cache is recreated each turn.
    /// </summary>
    public Dictionary<(Tile Tile, string Improvement), HashSet<ImprovementBuildingProblem>> WorkerImprovementBuildingProblems { get; } = [];
}
