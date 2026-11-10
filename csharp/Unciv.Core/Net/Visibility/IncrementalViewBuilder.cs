using System.Text.Json;
using Unciv.Commands.Events;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Utils;
using Unciv.View;
using CivilizationClass = Unciv.Logic.Civilization.Civilization;

namespace Unciv.Net.Visibility;

// T342: an incremental host view builder. The full build walks the whole map and re-serializes every explored tile for every accepted command and every
// recipient; in a late game that is thousands of tiles per command. A recipient's view of an explored, fogged tile cannot change between commands for a
// human viewer: its fog snapshot is the viewer's own remembered improvement/owner, and the only live inputs it carries are the terrain and resource, both
// covered by TileMap.MapContentRevision, and resource reveal, covered by the viewer's tech/policy revision. So those DTOs (and their canonical JSON) are
// reused by reference. Every currently visible tile is rebuilt, as are tiles that changed visibility and the positions in the command's events. Units are
// rebuilt from the visible tiles in map order, which is exactly the set and order the full build emits; the rest of the snapshot (cities, civs, diplomacy,
// notifications, ...) is small and is built the same way. The resulting frames are byte-identical (IncrementalViewBuilderTests compares them on scripted
// games); anything the builder cannot prove safe (a non-human viewer, a map-content or reveal change, a stale state) falls back to the full build.
// Invariant for the remembered fog values: the base game writes LastSeenImprovement/LastSeenOwner only while a tile is visible (or when it becomes visible),
// and every such command rebuilds that tile, so a reused fogged entry cannot go stale; a mod or future unique that writes them for an explored, fogged tile
// without a map-content or reveal revision must bump the map-content revision (or the builder will reuse the old snapshot).

/// <summary>Builds per-recipient view deltas from the previous snapshot plus the changes since it, with a full-build fallback.</summary>
public static class IncrementalViewBuilder
{
    /// <summary>The per-recipient state of the incremental builder: the snapshot it produced and the canonical JSON of that snapshot's tiles.</summary>
    public sealed class State
    {
        internal ClientSnapshot? Snapshot { get; init; }
        internal List<JsonElement> TileElements { get; init; } = [];
        internal List<int> TileOrder { get; init; } = [];
        internal int MapContentRevision { get; init; }
        internal long RevealRevision { get; init; }
        internal bool Valid { get; init; }
    }

    /// <summary>One incremental result: the delta to send, the snapshot to keep as the next "before", and the state that goes with it.</summary>
    public sealed record Built(ViewDelta Delta, ClientSnapshot After, State State);

    /// <summary>Seeds the state from a full snapshot (attach, resume, resync); the first incremental build after it may run.</summary>
    public static State Seed(GameInfo gameInfo, CivilizationClass viewer, ClientSnapshot snapshot)
    {
        var elements = new List<JsonElement>(snapshot.Tiles.Count);
        var order = new List<int>(snapshot.Tiles.Count);
        foreach (var tile in snapshot.Tiles)
        {
            var mapTile = gameInfo.TileMap.GetOrNull(tile.X, tile.Y);
            if (mapTile == null) return new State { Valid = false };
            elements.Add(SnapshotBuilder.ToElement(tile));
            order.Add(mapTile.ZeroBasedIndex);
        }
        return new State
        {
            Snapshot = snapshot,
            TileElements = elements,
            TileOrder = order,
            MapContentRevision = gameInfo.TileMap.MapContentRevision,
            RevealRevision = ViewDeltaBuilder.RevealRevision(viewer),
            Valid = true
        };
    }

    /// <summary>
    /// The incremental delta for one accepted command, or null when the builder cannot prove the reuse safe; the caller then runs the full build and seeds the
    /// returned state. <paramref name="mark"/> is the recipient's ViewMark captured before the command.
    /// </summary>
    public static Built? TryBuildWithDiff(GameInfo gameInfo, CivilizationClass viewer, ClientSnapshot before, ViewDeltaBuilder.ViewMark mark,
        IReadOnlyList<EngineEvent> events, long seq, TurnStatusDto turnStatus, State? state)
    {
        if (state is not { Valid: true } incremental || mark.Visible is not { } beforeVisible) return null;
        if (!CanReuse(gameInfo, viewer, before, mark, incremental)) return null;

        var revision = gameInfo.TileMap.MapContentRevision;
        var gameView = new GameView(gameInfo, viewer);
        var plan = PlanTiles(gameInfo, viewer, before, beforeVisible, mark.Explored, events, incremental, gameView);
        var (units, rebuiltUnits) = PlanUnits(gameInfo, before, plan.VisibleOrder, gameView);

        ClientSnapshot after;
        using (TurnProbe.Measure("View.Complete")) after = SnapshotBuilder.Complete(gameView, gameInfo, viewer, seq, turnStatus, plan.Tiles, units, computeHash: false);
        using (TurnProbe.Measure("View.Hash")) after = after with { ViewHash = SnapshotBuilder.ComputeViewHash(after, plan.Elements) };
        SnapshotDiff diff;
        using (TurnProbe.Measure("View.DiffInc")) diff = SnapshotDiff.ComputeIncremental(before, after, plan.Rebuilt, rebuiltUnits);
        var revealCandidates = plan.VisibleOrder.Concat(plan.NewlyExplored).Distinct()
            .OrderBy(it => gameInfo.TileMap[it].ZeroBasedIndex).ToList();
        ViewDelta delta;
        using (TurnProbe.Measure("View.EventsInc")) delta = ViewDeltaBuilder.Build(gameInfo, viewer, mark, events, seq, revealCandidates) with
        {
            Diff = SnapshotBuilder.ToElement(diff),
            ViewHash = after.ViewHash
        };
        var newState = new State
        {
            Snapshot = after,
            TileElements = plan.Elements,
            TileOrder = plan.Order,
            MapContentRevision = revision,
            RevealRevision = mark.RevealRevision,
            Valid = true
        };
        return new Built(delta, after, newState);
    }

    /// <summary>Every input of a reused fogged tile, plus the state's identity, must be unchanged; otherwise the full build is the safe answer.</summary>
    private static bool CanReuse(GameInfo gameInfo, CivilizationClass viewer, ClientSnapshot before, ViewDeltaBuilder.ViewMark mark, State state)
    {
        if (!ReferenceEquals(state.Snapshot, before)) return false;
        if (viewer.PlayerType != PlayerType.Human || viewer.IsSpectator()) return false; // an AI/spectator view shows live improvement in fog: no reuse
        if (state.TileElements.Count != before.Tiles.Count || state.TileOrder.Count != before.Tiles.Count) return false;
        var revision = gameInfo.TileMap.MapContentRevision;
        if (mark.MapContentRevision != revision || state.MapContentRevision != revision) return false;
        return mark.RevealRevision == ViewDeltaBuilder.RevealRevision(viewer) && state.RevealRevision == mark.RevealRevision;
    }

    private sealed record TilePlan(List<TileDto> Tiles, List<JsonElement> Elements, List<int> Order, HashSet<string> Rebuilt,
        List<HexCoord> VisibleOrder, List<HexCoord> NewlyExplored);

    private static TilePlan PlanTiles(GameInfo gameInfo, CivilizationClass viewer, ClientSnapshot before, IReadOnlySet<HexCoord> beforeVisible,
        HashSet<HexCoord> beforeExplored, IReadOnlyList<EngineEvent> events, State state, GameView gameView)
    {
        HashSet<HexCoord> afterExplored;
        List<HexCoord> visibleOrder;
        using (TurnProbe.Measure("View.TileSets")) (afterExplored, visibleOrder) = ViewDeltaBuilder.TileSets(gameInfo, viewer);
        var visibleSet = visibleOrder.ToHashSet();
        var newlyExplored = afterExplored.Where(it => !beforeExplored.Contains(it)).ToList();
        var rebuilt = new HashSet<string>(StringComparer.Ordinal);
        var beforeByKey = new Dictionary<string, int>(before.Tiles.Count, StringComparer.Ordinal);
        for (var i = 0; i < before.Tiles.Count; i++) beforeByKey[Key(before.Tiles[i])] = i;
        var resolved = new Dictionary<string, (TileDto Dto, JsonElement Element)>(StringComparer.Ordinal);

        void Resolve(HexCoord position)
        {
            var key = Key(position);
            if (resolved.ContainsKey(key)) return;
            var tile = gameInfo.TileMap[position];
            var dto = SnapshotBuilder.BuildTile(gameView.TileMapView.GetTile(tile), gameView);
            if (beforeByKey.TryGetValue(key, out var index) && SameTile(before.Tiles[index], dto))
            {
                resolved[key] = (before.Tiles[index], state.TileElements[index]);
                return;
            }
            resolved[key] = (dto, SnapshotBuilder.ToElement(dto));
            rebuilt.Add(key);
        }

        using (TurnProbe.Measure("View.Tiles"))
        {
            foreach (var position in visibleOrder) Resolve(position);
            // A tile can be explored during a command and already fogged again at its end (a unit moved on): the full build still emits its fog snapshot.
            foreach (var position in newlyExplored) Resolve(position);

            // Tiles that were visible before and are fogged now: their live owner/road/units must disappear into the fog snapshot.
            foreach (var position in beforeVisible)
                if (!visibleSet.Contains(position) && gameInfo.TileMap.Contains(position.x, position.y)) Resolve(position);

            // Changes that leave no trace in the visible set but are reported at a position (terrain, sites, ...) are rebuilt explicitly.
            foreach (var gameEvent in events)
                foreach (var position in gameEvent.Positions())
                    if (gameInfo.TileMap.GetOrNull(position.x, position.y) is { } tile && tile.IsExplored(viewer)) Resolve(position);
        }

        var added = new List<(int Index, string Key)>();
        foreach (var position in visibleOrder.Concat(newlyExplored).Distinct())
        {
            var key = Key(position);
            if (!beforeByKey.ContainsKey(key)) added.Add((gameInfo.TileMap[position].ZeroBasedIndex, key));
        }
        added = added.OrderBy(it => it.Index).ToList();

        var tiles = new List<TileDto>(before.Tiles.Count + added.Count);
        var elements = new List<JsonElement>(before.Tiles.Count + added.Count);
        var order = new List<int>(before.Tiles.Count + added.Count);
        using (TurnProbe.Measure("View.TileList"))
        {
            var nextAdded = 0;
            for (var i = 0; i < before.Tiles.Count; i++)
            {
                while (nextAdded < added.Count && added[nextAdded].Index < state.TileOrder[i]) AddNew(added[nextAdded++]);
                var beforeTile = before.Tiles[i];
                if (resolved.TryGetValue(Key(beforeTile), out var changed))
                {
                    tiles.Add(changed.Dto);
                    elements.Add(changed.Element);
                }
                else
                {
                    tiles.Add(beforeTile);
                    elements.Add(state.TileElements[i]);
                }
                order.Add(state.TileOrder[i]);
            }
            while (nextAdded < added.Count) AddNew(added[nextAdded++]);
        }

        void AddNew((int Index, string Key) entry)
        {
            var changed = resolved[entry.Key];
            tiles.Add(changed.Dto);
            elements.Add(changed.Element);
            order.Add(entry.Index);
        }

        return new TilePlan(tiles, elements, order, rebuilt, visibleOrder, newlyExplored);
    }

    private static (List<UnitDto> Units, HashSet<string> Rebuilt) PlanUnits(GameInfo gameInfo, ClientSnapshot before,
        IReadOnlyList<HexCoord> visibleOrder, GameView gameView)
    {
        var units = new List<UnitDto>();
        var rebuilt = new HashSet<string>(StringComparer.Ordinal);
        using (TurnProbe.Measure("View.Units"))
        {
            var beforeUnits = before.Units.ToDictionary(it => it.Id);
            var emittedUnitIds = new HashSet<int>();
            foreach (var position in visibleOrder)
            {
                var tileView = gameView.TileMapView.GetTile(gameInfo.TileMap[position]);
                if (!tileView.IsVisible()) continue;
                foreach (var unit in tileView.GetVisibleUnits())
                {
                    if (!emittedUnitIds.Add(unit.Id)) continue;
                    var dto = SnapshotBuilder.BuildUnit(unit, tileView);
                    if (beforeUnits.TryGetValue(unit.Id, out var old) && SameUnit(old, dto))
                    {
                        units.Add(old);
                        continue;
                    }
                    units.Add(dto);
                    rebuilt.Add(unit.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        return (units, rebuilt);
    }

    private static bool SameTile(TileDto a, TileDto b) =>
        a.Visible == b.Visible && a.BaseTerrain == b.BaseTerrain && a.Features.SequenceEqual(b.Features)
        && a.NaturalWonder == b.NaturalWonder && a.Resource == b.Resource && a.Improvement == b.Improvement
        && a.RiverBottom == b.RiverBottom && a.RiverBottomLeft == b.RiverBottomLeft && a.RiverBottomRight == b.RiverBottomRight
        && a.Owner == b.Owner && a.Road == b.Road && a.RoadPillaged == b.RoadPillaged
        && a.ImprovementPillaged == b.ImprovementPillaged && a.LastOwner == b.LastOwner;

    private static bool SameUnit(UnitDto a, UnitDto b)
    {
        if (a.Id != b.Id || a.Name != b.Name || a.Owner != b.Owner || a.X != b.X || a.Y != b.Y
            || a.HealthBucket != b.HealthBucket || a.IsEmbarked != b.IsEmbarked || a.IsFortified != b.IsFortified)
            return false;
        if (a.Own is null || b.Own is null) return a.Own == b.Own;
        return a.Own.Health == b.Own.Health && BitConverter.SingleToInt32Bits(a.Own.CurrentMovement) == BitConverter.SingleToInt32Bits(b.Own.CurrentMovement)
            && a.Own.Xp == b.Own.Xp && a.Own.Action == b.Own.Action && a.Own.Promotions.SequenceEqual(b.Own.Promotions)
            && a.Own.Automated == b.Own.Automated && a.Own.CanBePromoted == b.Own.CanBePromoted;
    }

    // Shared with SnapshotDiff.TileKey so the rebuilt set and the diff always agree, whatever the ambient culture's negative sign is.
    private static string Key(TileDto tile) => SnapshotDiff.TileKey(tile.X, tile.Y);

    private static string Key(HexCoord position) => SnapshotDiff.TileKey(position.x, position.y);
}
