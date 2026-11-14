using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Unciv.Logic.Map.Tiles;
using Unciv.Utils;

namespace Unciv.Logic.Map.MapUnits;

// Ported from the end of core/src/com/unciv/logic/map/mapunit/movement/UnitMovement.kt (T067).
// Kotlin extends LinkedHashMap; ours is sealed, so this wraps one (insertion-ordered, replacing a key keeps its position).

/// <summary>Should contain current unit location even when it has no movement</summary>
public class PathsToTilesWithinTurn : IReadOnlyDictionary<Tile, UnitMovement.ParentTileAndTotalMovement>
{
    private readonly LinkedHashMap<Tile, UnitMovement.ParentTileAndTotalMovement> map = new();

    // T408: a buffer reused across the frontier expansions of one GetShortestPath search keeps the ParentTileAndTotalMovement
    // objects it handed out and recycles them on the next fill instead of allocating one per improved tile. The values are only
    // read for .TotalMovement during a fill, and GetShortestPath only iterates Keys, so mutating a recycled object is safe.
    // A fresh instance (e.g. a GetDistanceToTiles result held by a caller) never recycles: its pool stays empty.
    private readonly List<UnitMovement.ParentTileAndTotalMovement> recycled = [];

    public int Count => map.Count;
    public IEnumerable<Tile> Keys => map.Keys;
    public IEnumerable<UnitMovement.ParentTileAndTotalMovement> Values => map.Values;

    public UnitMovement.ParentTileAndTotalMovement this[Tile key]
    {
        get => map[key];
        set => map.Put(key, value);
    }

    public bool ContainsKey(Tile key) => map.ContainsKey(key);
    public bool TryGetValue(Tile key, [MaybeNullWhen(false)] out UnitMovement.ParentTileAndTotalMovement value) => map.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<Tile, UnitMovement.ParentTileAndTotalMovement>> GetEnumerator() => map.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Sets the best path to <paramref name="tile"/>, reusing a recycled <see cref="UnitMovement.ParentTileAndTotalMovement"/>
    /// (or the object already held at the key) when one is available. Insertion order is that of <see cref="map"/>: a new key is
    /// appended, a replaced key keeps its position.</summary>
    internal void SetParent(Tile tile, Tile parentTile, float totalMovement)
    {
        UnitMovement.ParentTileAndTotalMovement result;
        if (map.TryGetValue(tile, out var existing))
            result = existing; // replacing a key keeps its position; the object can be mutated in place
        else if (recycled.Count > 0)
        {
            result = recycled[^1];
            recycled.RemoveAt(recycled.Count - 1);
        }
        else
            result = new UnitMovement.ParentTileAndTotalMovement(tile, parentTile, totalMovement);

        result.Tile = tile;
        result.ParentTile = parentTile;
        result.TotalMovement = totalMovement;
        map.Put(tile, result);
    }

    /// <summary>Clears the map and returns every held <see cref="UnitMovement.ParentTileAndTotalMovement"/> to the recycle pool (T408).</summary>
    internal void ClearAndRecycle()
    {
        foreach (var entry in map) recycled.Add(entry.Value);
        map.Clear();
    }

    public List<Tile> GetPathToTile(Tile tile)
    {
        if (!ContainsKey(tile))
        {
            Log.Null.Debug("PathsToTilesWithinTurn#getPathToTile does not contain {0}", tile);
            throw new InvalidOperationException($"Can't reach {tile}");
        }
        var reversePathList = new List<Tile>();
        var currentTile = tile;
        while (this[currentTile].ParentTile != currentTile)
        {
            reversePathList.Add(currentTile);
            currentTile = this[currentTile].ParentTile;
        }
        reversePathList.Reverse();
        return reversePathList;
    }
}
