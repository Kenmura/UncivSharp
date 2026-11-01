namespace Unciv.Logic.Map.Tiles;

// T303. Split out of Tile.cs to keep that file within its size ratchet.
public partial class Tile
{
    /// <summary>A civ that can see this tile when its owner changes (SetOwningCity, or the whole city changing hands in MoveToCiv) sees the change, so its last-seen owner follows; a civ that cannot keeps what it saw.</summary>
    internal void RememberOwnerForViewers()
    {
        if (!TileMap.HasGameInfo()) return;
        var owner = GetOwner()?.CivId;
        foreach (var civ in TileMap.GameInfo.Civilizations)
        {
            if (civ.IsDefeated() || !civ.IsMajorCiv()) continue;
            if (IsVisible(civ)) civ.SetLastSeenOwner(Position, owner);
        }
    }
}
