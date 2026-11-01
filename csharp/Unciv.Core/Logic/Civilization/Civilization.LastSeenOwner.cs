using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Map;

namespace Unciv.Logic.Civilization;

// T303. Split out of Civilization.cs to keep that file within its size ratchet.
public partial class Civilization
{
    /// <summary>
    /// The owner (a civ id; <see cref="UnownedMark"/> when none) this civ last saw on each tile, so a fogged tile keeps showing the border it had when last in sight, across reconnect and load.
    /// Same shape and same human-only rule as <see cref="LastSeenImprovement"/>; a tile it never saw has no entry. Null until a human civ first sees a tile, so games without one (and saves from
    /// before this field) serialize, and so hash, exactly as before.
    /// </summary>
    [JsonPropertyName("lastSeenOwner")]
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LastSeenImprovement? LastSeenOwner { get; private set; }

    /// <summary>The <see cref="LastSeenOwner"/> value of a tile seen without an owner (an absent entry means never seen).</summary>
    public const string UnownedMark = "";

    /// <summary>The civ id this civ last saw owning the tile, <see cref="UnownedMark"/> if it last saw it unowned, null if it never saw it (always null for AI and spectators).</summary>
    public string? GetLastSeenOwner(HexCoord position)
    {
        if (IsAI() || IsSpectator()) return null;
        return LastSeenOwner != null && LastSeenOwner.TryGetValue(position, out var owner) ? owner : null;
    }

    /// <summary>Remembers what this civ sees on the tile right now; a null <paramref name="owner"/> is "seen unowned". No-op for AI and spectators.</summary>
    public void SetLastSeenOwner(HexCoord position, string? owner)
    {
        if (IsAI() || IsSpectator()) return;
        (LastSeenOwner ??= new LastSeenImprovement())[position] = owner ?? UnownedMark;
    }

    private void CopyLastSeenInto(Civilization copy)
    {
        foreach (var (position, improvement) in LastSeenImprovement) copy.LastSeenImprovement[position] = improvement;
        if (LastSeenOwner == null) return;
        copy.LastSeenOwner = new LastSeenImprovement(); // not SetLastSeenOwner: a clone has no nation yet, so it cannot tell AI or spectator
        foreach (var (position, owner) in LastSeenOwner) copy.LastSeenOwner[position] = owner;
    }
}
