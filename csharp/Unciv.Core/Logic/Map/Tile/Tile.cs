using System.Globalization;
using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Civilization;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Utils;
using CityClass = Unciv.Logic.City.City;
using MapUnitClass = Unciv.Logic.Map.MapUnits.MapUnit;

namespace Unciv.Logic.Map.Tiles;

// Ported from core/src/com/unciv/logic/map/tile/Tile.kt (T053), against the seams in GameContextTile.cs: TileMap, Civilization, City and MapUnit
// are reached through the IGameContext* interfaces, so Tile does not depend on the concrete classes.
//
// Stripped: timeThis profiling wrappers, GUI.clearUndoCheckpoints (UI), the @Readonly/@Cache purity annotations, the Gdx Json
// private constructor of ImprovementQueueEntry. Fonts.turn is inlined.
// T054 added the `improvementFunctions` and `stats` fields, removeImprovement, both setImprovement overloads, setBaseTerrain and
// setPillaged (+ clearAllPathfindingCaches).
// T066 added doWorkerTurn; T139 added startWorkingOnImprovement and queueImprovement(TileImprovement, civ, unit).
// T107 added the Gdx-compatible JSON (TileJsonConverter at the end of the class); T110 wired the unit fields (MapUnit.MapUnitJsonConverter).

/// <summary>
/// A single hex of the map. Kotlin <c>Tile</c> has no <c>equals</c>/<c>hashCode</c>: this class keeps reference identity.
/// </summary>
[JsonConverter(typeof(Tile.TileJsonConverter))]
public partial class Tile : IsPartOfGameInfoSerialization, IGameContextTile, ITileHistorySource
{
    #region Serialized fields
    public MapUnitClass? MilitaryUnit { get; set; }
    public MapUnitClass? CivilianUnit { get; set; }
    public List<MapUnitClass> AirUnits { get; set; } = [];

    public HexCoord Position { get; set; } = new();
    private string? baseTerrain;
    /// <summary>Kotlin <c>lateinit var</c>: reading before assignment throws.</summary>
    public string BaseTerrain
    {
        get => baseTerrain ?? throw new InvalidOperationException("lateinit property baseTerrain has not been initialized");
        set => baseTerrain = value;
    }
    public IReadOnlyList<string> TerrainFeatures { get; private set; } = [];

    /// <summary>Should be immutable - never be altered in-place, instead replaced</summary>
    private HashSet<string> exploredBy = [];

    public string? NaturalWonder { get; set; }
    public string? Resource { get; private set; }
    public int ResourceAmount { get; set; }

    public string? Improvement { get; set; }
    public bool ImprovementIsPillaged { get; set; }

    /// <summary>B010 (bnw-spec 4.4.1): what happened here, for archaeology. Null until a historical event or a site writes it.</summary>
    public Archaeology.ArchaeologyRecord? ArchaeologyRecord { get; set; }

    internal List<ImprovementQueueEntry> ImprovementQueue { get; } = [];

    public RoadStatus RoadStatus { get; set; } = RoadStatus.None;

    public bool RoadIsPillaged { get; set; }
    private string roadOwner = ""; // either who last built the road or last owner of tile

    private IGameContextCivilization? roadOwnerObject;

    public bool HasBottomRightRiver { get; set; }
    public bool HasBottomRiver { get; set; }
    public bool HasBottomLeftRiver { get; set; }

    public TileHistory History { get; set; } = new();

    private int continent = -1;
    #endregion

    #region Transient fields
    private TileMap? tileMap;
    /// <summary>Kotlin <c>lateinit var</c>: reading before assignment throws.</summary>
    public TileMap TileMap
    {
        get => tileMap ?? throw new InvalidOperationException("lateinit property tileMap has not been initialized");
        set => tileMap = value;
    }

    public int ZeroBasedIndex { get; set; }

    public Tile()
    {
        ImprovementFunctions = new TileImprovementFunctions(this);
        Stats = new TileStatFunctions(this);
    }

    public TileImprovementFunctions ImprovementFunctions { get; }

    public TileStatFunctions Stats { get; }

    private Ruleset? ruleset;
    /// <summary>A tile can be a tile with a ruleset, even without a map. Kotlin <c>lateinit var</c>.</summary>
    public Ruleset Ruleset
    {
        get => ruleset ?? throw new InvalidOperationException("lateinit property ruleset has not been initialized");
        set => ruleset = value;
    }

    // This is for performance - since we access the neighbors of a tile ALL THE TIME,
    // and the neighbors of a tile never change, it's much more efficient to save the list once and for all!
    private IReadOnlyList<Tile>? neighbors;
    public IReadOnlyList<Tile> Neighbors => neighbors ??= !IsTilemapInitialized() ? [] : GetTilesAtDistance(1).ToList();

    private bool isCityCenterInternal;

    public CityClass? OwningCity { get; private set; }

    private Terrain? baseTerrainObject;

    // These are for performance - checked with every tile movement and "canEnter" check, which makes them performance-critical
    public bool IsLand { get; set; }
    public bool IsWater { get; set; }
    public bool IsOcean { get; set; }

    private bool? isAdjacentToCoastCache;
    private bool IsAdjacentToCoastLazy => isAdjacentToCoastCache ??= Neighbors.Any(it => it.GetBaseTerrain().IsCoast);

    public int UnitHeight { get; set; }
    public int TileHeight { get; set; }

    public IReadOnlyList<Terrain> TerrainFeatureObjects { get; private set; } = [];

    /// <summary>Saves a sequence of a list</summary>
    public IReadOnlyList<Terrain> AllTerrains { get; private set; } = [];

    private Terrain? lastTerrain;
    public Terrain LastTerrain
    {
        get => lastTerrain ?? throw new InvalidOperationException("lateinit property lastTerrain has not been initialized");
        private set => lastTerrain = value;
    }

    public Unciv.Logic.Map.TileMap.TerrainListData CachedTerrainData { get; private set; } = Unciv.Logic.Map.TileMap.TerrainListData.Empty;

    /// <summary>Between 0.0 and 1.0 - For map generation use only</summary>
    public double? Humidity { get; set; }

    /// <summary>Between -1.0 and 1.0 - For map generation use only</summary>
    public double? Temperature { get; set; }

    /// <summary>For map generation use only</summary>
    public bool HasVegetation { get; set; }

    public int Latitude => HexMath.GetLatitude(Position);
    public int Longitude => HexMath.GetLongitude(Position);

    private TileResource? tileResourceCache;
    public TileResource? TileResource
    {
        get
        {
            if (tileResourceCache == null && Resource != null)
                tileResourceCache = Ruleset.TileResources.GetValueOrDefault(Resource);
            return tileResourceCache;
        }
        set
        {
            if (value == null)
            {
                Resource = null;
                tileResourceCache = null;
            }
            else
            {
                Resource = value.Name;
                tileResourceCache = value;
            }
            if (IsTilemapInitialized() && ReferenceEquals(TileMap.GetOrNull(Position.x, Position.y), this)) TileMap.BumpMapContentRevision();
        }
    }

    private TileImprovement? tileImprovementField;
    public TileImprovement? TileImprovement
    {
        get
        {
            if (tileImprovementField == null && Improvement != null)
                tileImprovementField = Ruleset.TileImprovements.GetValueOrDefault(Improvement);
            return tileImprovementField;
        }
        set => tileImprovementField = value;
    }

    public string? ImprovementInProgress => ImprovementQueue.FirstOrDefault()?.Improvement;
    public int TurnsToImprovement => ImprovementQueue.FirstOrDefault()?.TurnsToImprovement ?? 0;

    internal GameContext StateThisTile { get; private set; } = GameContext.EmptyState;
    #endregion

    #region Seam members (IGameContextTile, ITileHistorySource)
    IGameContextGameInfo? IGameContextTile.GameInfo => IsTilemapInitialized() && TileMap.HasGameInfo() ? TileMap.GameInfo : null;
    IEnumerable<IGameContextTile> IGameContextTile.Neighbors => Neighbors;
    IEnumerable<IGameContextUnit> IGameContextTile.AirUnits => AirUnits;
    IGameContextUnit? IGameContextTile.MilitaryUnit => MilitaryUnit;
    IEnumerable<IGameContextUnit> IGameContextTile.GetUnits() => GetUnits();
    IEnumerable<IGameContextTile> IGameContextTile.GetTilesInDistance(int distance) => GetTilesInDistance(distance);
    bool IGameContextTile.MatchesFilter(string filter) => MatchesFilter(filter);
    int ITileHistorySource.Turns => TileMap.GameInfo.Turns;
    string? ITileHistorySource.OwnerCivID => GetOwner()?.CivId;
    bool ITileHistorySource.IsCityCenter => IsCityCenter();
    bool ITileHistorySource.IsCityCapital => GetCity()?.IsCapital() == true;
    #endregion

    #region Pure functions

    /// <param name="addUnits">For stat diff checks, units are meaningless</param>
    public Tile Clone(bool addUnits = true)
    {
        var toReturn = new Tile
        {
            TileMap = TileMap,
            Ruleset = Ruleset,
            isCityCenterInternal = isCityCenterInternal,
            OwningCity = OwningCity,
            baseTerrainObject = baseTerrainObject,
            IsLand = IsLand,
            IsWater = IsWater,
            IsOcean = IsOcean,
        };
        if (addUnits)
        {
            if (MilitaryUnit != null) toReturn.MilitaryUnit = MilitaryUnit.Clone();
            if (CivilianUnit != null) toReturn.CivilianUnit = CivilianUnit.Clone();
            toReturn.AirUnits = AirUnits.Select(it => it.Clone()).ToList();
        }
        toReturn.Position = Position;
        toReturn.BaseTerrain = BaseTerrain;
        toReturn.TerrainFeatures = TerrainFeatures; // immutable lists can be directly passed around
        toReturn.TerrainFeatureObjects = TerrainFeatureObjects;
        toReturn.NaturalWonder = NaturalWonder;
        toReturn.Resource = Resource;
        toReturn.ResourceAmount = ResourceAmount;
        toReturn.Improvement = Improvement;
        toReturn.ImprovementQueue.AddRange(ImprovementQueue.Select(it => it.Clone()));
        toReturn.ImprovementIsPillaged = ImprovementIsPillaged;
        toReturn.ArchaeologyRecord = ArchaeologyRecord?.Clone();
        toReturn.RoadStatus = RoadStatus;
        toReturn.RoadIsPillaged = RoadIsPillaged;
        toReturn.roadOwner = roadOwner;
        toReturn.HasBottomLeftRiver = HasBottomLeftRiver;
        toReturn.HasBottomRightRiver = HasBottomRightRiver;
        toReturn.HasBottomRiver = HasBottomRiver;
        toReturn.continent = continent;
        toReturn.exploredBy = exploredBy;
        toReturn.History = History.Clone();
        // Setting even though it's transient - where it's needed, it's a real performance saver
        toReturn.tileResourceCache = tileResourceCache;
        return toReturn;
    }

    public Terrain? GetHillTerrain()
    {
        var baseT = GetBaseTerrain();
        if (baseT.IsHill)
            return baseT;
        return TerrainFeatureObjects.FirstOrDefault(it => it.IsHill);
    }
    public bool IsHill() => GetHillTerrain() != null;

    /// <summary>Returns military, civilian and air units in tile</summary>
    public IEnumerable<MapUnitClass> GetUnits()
    {
        // common case - do not allocate memory for a new sequence
        if (MilitaryUnit == null && CivilianUnit == null && AirUnits.Count == 0)
            return [];
        return GetUnitsIterator();
    }

    private IEnumerable<MapUnitClass> GetUnitsIterator()
    {
        if (MilitaryUnit != null) yield return MilitaryUnit;
        if (CivilianUnit != null) yield return CivilianUnit;
        if (AirUnits.Count > 0)
        {
            foreach (var unit in AirUnits) yield return unit;
        }
    }

    /// <summary>This is for performance reasons of canPassThrough() - faster than getUnits().firstOrNull()</summary>
    public MapUnitClass? GetFirstUnit()
    {
        if (MilitaryUnit != null) return MilitaryUnit;
        if (CivilianUnit != null) return CivilianUnit;
        if (AirUnits.Count > 0) return AirUnits[0];
        return null;
    }

    public CityClass? GetCity() => OwningCity;
    IGameContextCity? IGameContextTile.GetCity() => GetCity();

    internal Terrain GetNaturalWonder() =>
        NaturalWonder == null ? throw new InvalidOperationException("No natural wonder exists for this tile!") : Ruleset.Terrains[NaturalWonder];

    /// <summary>Actively observed tiles (no fog of war)</summary>
    public bool IsVisible(IGameContextCivilization player)
    {
        if (player.GameInfo?.EngineOptions.VisibleMap == true)
            return true;
        return player.IsTileViewable(this);
    }

    /// <summary>Tiles that at some point have been explored (may have fog of war)</summary>
    public bool IsExplored(IGameContextCivilization player)
    {
        if (player.GameInfo?.EngineOptions.VisibleMap == true || player.IsSpectator())
            return true;
        return exploredBy.Contains(player.CivId);
    }

    public bool IsCityCenter() => isCityCenterInternal;
    public bool IsNaturalWonder() => NaturalWonder != null;
    public bool IsImpassible() => LastTerrain.Impassable;

    public bool IsBarbarianEncampment() =>
        TileImprovement?.IsBarbarianCampEquivalent(StateThisTile) == true;

    public bool HasImprovementInProgress() => ImprovementQueue.Count > 0;

    public TileImprovement? GetTileImprovementInProgress() =>
        ImprovementQueue.Count == 0 ? null : Ruleset.TileImprovements.GetValueOrDefault(ImprovementQueue[0].Improvement);
    public bool IsPillaged() => ImprovementIsPillaged || RoadIsPillaged;

    public string? GetUnpillagedImprovement() => ImprovementIsPillaged ? null : Improvement;

    public TileImprovement? GetUnpillagedTileImprovement() => ImprovementIsPillaged ? null : TileImprovement;

    public TileImprovement? GetRoadTileImprovement() =>
        RoadStatus == RoadStatus.None ? null : Ruleset.TileImprovements.GetValueOrDefault(RoadStatus.ToString());

    /// <returns><see cref="RoadStatus"/> on this <see cref="Tile"/>, pillaged road counts as <see cref="RoadStatus.None"/></returns>
    public RoadStatus GetUnpillagedRoad() => RoadIsPillaged ? RoadStatus.None : RoadStatus;

    public TileImprovement? GetUnpillagedRoadImprovement() => RoadIsPillaged ? null : GetRoadTileImprovement();

    public TileImprovement? GetImprovementToPillage()
    {
        if (CanPillageTileImprovement())
            return TileImprovement.NotNull();
        if (CanPillageRoad())
            return GetRoadTileImprovement().NotNull();
        return null;
    }

    /// <summary>same as above, but slightly quicker</summary>
    public string? GetImprovementToPillageName()
    {
        if (CanPillageTileImprovement())
            return Improvement;
        if (CanPillageRoad())
            return RoadStatus.ToString();
        return null;
    }

    public TileImprovement? GetImprovementToRepair()
    {
        if (Improvement != null && ImprovementIsPillaged)
            return TileImprovement.NotNull();
        if (RoadStatus != RoadStatus.None && RoadIsPillaged)
            return GetRoadTileImprovement().NotNull();
        return null;
    }

    public bool CanPillageTile() => CanPillageTileImprovement() || CanPillageRoad();

    public bool CanPillageTileImprovement()
    {
        var improvement = TileImprovement;
        if (improvement == null) return false;
        var uniques = (IHasUniques)improvement;
        return !ImprovementIsPillaged &&
            !uniques.HasUnique(UniqueType.Unpillagable) &&
            !uniques.HasUnique(UniqueType.Irremovable);
    }

    public bool CanPillageRoad()
    {
        return RoadStatus != RoadStatus.None &&
            !RoadIsPillaged &&
            !((IHasUniques)GetRoadTileImprovement().NotNull()).HasUnique(UniqueType.Unpillagable) &&
            !((IHasUniques)GetRoadTileImprovement().NotNull()).HasUnique(UniqueType.Irremovable);
    }

    /// <summary>
    /// Improvement to display, accounting for knowledge about a Tile possibly getting stale when a human player is no longer actively watching it.
    /// Relies on a Civilization's lastSeenImprovement always being up to date while the civ can see the Tile.
    /// </summary>
    /// <param name="viewingCiv"><c>null</c> means civ-agnostic and thus always showing the actual improvement</param>
    /// <returns>The improvement name, or <c>null</c> if no improvement should be shown</returns>
    public string? GetShownImprovement(IGameContextCivilization? viewingCiv) =>
        viewingCiv == null || viewingCiv.PlayerType == PlayerType.AI || viewingCiv.IsSpectator() ? Improvement
        : viewingCiv.GetLastSeenImprovement(Position);

    /// <summary>Returns true if this tile has fallout or an equivalent terrain feature</summary>
    public bool HasFalloutEquivalent() =>
        TerrainFeatures.Any(it => ((IHasUniques)Ruleset.Terrains[it]).HasUnique(UniqueType.NullifyYields));

    public int GetRow() => HexMath.GetRow(Position);
    public int GetColumn() => HexMath.GetColumn(Position);

    public Terrain GetBaseTerrain() =>
        baseTerrainObject ?? throw new InvalidOperationException("lateinit property baseTerrainObject has not been initialized");

    public IGameContextCivilization? GetOwner() => GetCity()?.Civ;

    public IGameContextCivilization? GetRoadOwner() => roadOwnerObject ?? GetOwner();

    public bool IsFriendlyTerritory(IGameContextCivilization civInfo)
    {
        var tileOwner = GetOwner();
        if (tileOwner == null) return false;
        if (tileOwner == civInfo) return true;
        if (!civInfo.Knows(tileOwner)) return false;
        return tileOwner.IsConsideredFriendlyTerritoryOf(civInfo);
    }

    public bool IsEnemyTerritory(IGameContextCivilization civInfo)
    {
        var tileOwner = GetOwner();
        if (tileOwner == null) return false;
        return civInfo.IsAtWarWith(tileOwner);
    }

    public bool IsRoughTerrain() => AllTerrains.Any(it => it.IsRough);

    /// <summary>Checks whether any of the TERRAINS of this tile has a certain unique</summary>
    public bool TerrainHasUnique(UniqueType uniqueType, GameContext? state = null) =>
        CachedTerrainData.Uniques.HasMatchingUnique(uniqueType, state ?? StateThisTile);

    /// <summary>Get all uniques of this type that any TERRAIN on this tile has</summary>
    public IEnumerable<Unique> GetTerrainMatchingUniques(UniqueType uniqueType, GameContext? gameContext = null) =>
        CachedTerrainData.Uniques.GetMatchingUniques(uniqueType, gameContext ?? StateThisTile);

    /// <summary>Get all uniques of this type that any part of this tile has: terrains, improvement, resource</summary>
    public IEnumerable<Unique> GetMatchingUniques(UniqueType uniqueType, GameContext? gameContext = null)
    {
        var context = gameContext ?? StateThisTile;
        var uniques = GetTerrainMatchingUniques(uniqueType, context);
        var tileImprovement = GetUnpillagedTileImprovement();
        if (tileImprovement != null)
            uniques = uniques.Concat(((IHasUniques)tileImprovement).GetMatchingUniques(uniqueType, context));
        if (TileResource != null)
            uniques = uniques.Concat(((IHasUniques)TileResource).GetMatchingUniques(uniqueType, context));
        return uniques;
    }

    public CityClass? GetWorkingCity()
    {
        var civInfo = GetCity()?.Civ;
        if (civInfo == null) return null;
        if (OwningCity?.IsWorked(this) == true) return OwningCity; // common case
        return civInfo.Cities.FirstOrDefault(it => it != OwningCity && it.IsWorked(this));
    }

    public bool IsBlockaded()
    {
        var owner = GetOwner();
        if (owner == null) return false;
        var unit = MilitaryUnit;

        if (unit != null)
        {
            if (unit.Civ == owner) return false;            // Own - unblocks tile;
            if (unit.Civ.IsAtWarWith((Civilization.Civilization)owner)) return true;   // Enemy - blocks tile;
            return false;                                   // Neutral - unblocks tile;
        }
        if (IsLand) // Only water tiles are blocked if empty
            return false;

        // For water tiles need also to check neighbors:
        // enemy military naval units blockade all adjacent water tiles.
        foreach (var neighbor in Neighbors.Where(it => it.IsWater))
        {
            var neighborUnit = neighbor.MilitaryUnit;
            if (neighborUnit == null) continue;

            // Embarked units do not blockade adjacent tiles
            if (neighborUnit.Civ.IsAtWarWith((Civilization.Civilization)owner) && !neighborUnit.IsEmbarked())
                return true;
        }
        return false;
    }

    public bool IsWorked() => GetWorkingCity() != null;

    public bool ProvidesYield()
    {
        if (GetCity() == null) return false;
        return IsCityCenter()
            || IsWorked()
            || (GetUnpillagedTileImprovement() is { } improvement
                && ((IHasUniques)improvement).HasUnique(UniqueType.TileProvidesYieldWithoutPopulation, StateThisTile))
            || TerrainHasUnique(UniqueType.TileProvidesYieldWithoutPopulation);
    }

    public bool IsLocked()
    {
        var workingCity = GetWorkingCity();
        return workingCity != null && workingCity.LockedTiles.Contains(Position);
    }

    public bool ProvidesResources(IGameContextCivilization civInfo)
    {
        var resource = TileResource; // To avoid additional checks later
        if (!civInfo.CanSeeResource(resource)) return false;
        // canSeeResource is false for a null resource, so resource is not null from here on
        if (IsCityCenter())
        {
            var possibleImprovements = resource.NotNull().GetImprovements();
            if (possibleImprovements.Count == 0) return true;
            // Per Civ V, resources under city tiles require the *possibility of extraction* -
            //  that is, there needs to be a tile improvement you have the tech for.
            // Does NOT take all GetImprovementBuildingProblems into account.
            return possibleImprovements.Any(improvement =>
                Ruleset.TileImprovements.GetValueOrDefault(improvement) is { } it &&
                    it.TurnsToBuild != -1 && // Buildable by workers (not just 'placeable')
                    (it.TechRequired == null || civInfo.IsTechResearched(it.TechRequired)));
        }
        var unpillagedImprovement = GetUnpillagedTileImprovement();
        if (unpillagedImprovement != null && resource.NotNull().GetImprovements().Contains(unpillagedImprovement.Name)
                && (unpillagedImprovement.TechRequired == null || civInfo.IsTechResearched(unpillagedImprovement.TechRequired))
            ) return true;
        // Generic-ify to unique (upstream note)
        return resource.NotNull().ResourceType == ResourceType.Strategic &&
            unpillagedImprovement != null &&
            unpillagedImprovement.IsGreatImprovement();
    }

    /// <summary>This should be the only adjacency function</summary>
    public bool IsAdjacentTo(string terrainFilter, IGameContextCivilization? observingCiv = null)
    {
        // Rivers are odd, as they aren't technically part of any specific tile but still count towards adjacency
        if (terrainFilter == Constants.River) return IsAdjacentToRiver();
        if (terrainFilter == Constants.FreshWater && IsAdjacentToRiver()) return true;
        return Neighbors.Any(neighbor => neighbor.MatchesFilter(terrainFilter, observingCiv));
    }

    /// <summary>Implements <c>UniqueParameterType.TileFilter</c></summary>
    public bool MatchesFilter(string filter, IGameContextCivilization? civInfo = null) =>
        MultiFilter.Filter(filter, (tile: this, civInfo), static (it, s) => s.tile.MatchesSingleFilter(it, s.civInfo));

    private bool MatchesSingleFilter(string filter, IGameContextCivilization? civInfo = null)
    {
        if (MatchesSingleTerrainFilter(filter, civInfo)) return true;
        if ((Improvement == null || ImprovementIsPillaged) && filter == "unimproved") return true;
        if (Improvement != null && !ImprovementIsPillaged && filter == "improved") return true;
        if (IsPillaged() && filter == "pillaged") return true;
        if (filter == "worked" && IsWorked()) return true;
        if (GetUnpillagedTileImprovement()?.MatchesFilter(filter, StateThisTile, false) == true) return true;
        return GetUnpillagedRoadImprovement()?.MatchesFilter(filter, StateThisTile, false) == true;
    }

    /// <summary>Implements <c>UniqueParameterType.TerrainFilter</c></summary>
    public bool MatchesTerrainFilter(string filter, IGameContextCivilization? observingCiv, bool multiFilter = true) =>
        multiFilter ? MultiFilter.Filter(filter, (tile: this, observingCiv), static (it, s) => s.tile.MatchesSingleTerrainFilter(it, s.observingCiv))
        : MatchesSingleTerrainFilter(filter, observingCiv);

    private bool MatchesSingleTerrainFilter(string filter, IGameContextCivilization? observingCiv)
    {
        switch (filter)
        {
            case "Terrain": return true;
            case Constants.UppercaseAll:
            case Constants.LowercaseAll: return true;
            case "Water": return IsWater;
            case "Land": return IsLand;
            case Constants.Coastal: return IsAdjacentToCoast();
            case Constants.River: return IsAdjacentToRiver();
            case "Unowned": return GetOwner() == null;
            case "your": return observingCiv != null && GetOwner() == observingCiv;
            case "Foreign Land":
            case "Foreign": return observingCiv != null && !IsFriendlyTerritory(observingCiv);
            case "Friendly Land":
            case "Friendly": return observingCiv != null && IsFriendlyTerritory(observingCiv);
            case "Enemy Land":
            case "Enemy": return observingCiv != null && IsEnemyTerritory(observingCiv);
            case "resource": return observingCiv != null && observingCiv.CanSeeResource(TileResource);
            case "Water resource": return IsWater && observingCiv != null && observingCiv.CanSeeResource(TileResource);
            case "Featureless": return TerrainFeatures.Count == 0;
            case "Open terrain": return AllTerrains.All(it => !it.IsRough); // special case - if *one* terrain is open, we don't care, we need *all*
            case Constants.FreshWaterFilter:
                return IsAdjacentTo(Constants.FreshWater, observingCiv);
        }

        if (filter == baseTerrain) return true;
        if (filter == Resource) return observingCiv == null || observingCiv.CanSeeResource(TileResource);

        if (AllTerrains.Any(it => it.MatchesFilter(filter, StateThisTile, false))) return true;

        var owner = GetOwner();
        if (owner != null && owner.MatchesFilter(filter, StateThisTile, false)) return true;

        // Checks 'luxury resource', 'strategic resource' and 'bonus resource' - only those that are visible of course
        // not using canSeeResource as observingCiv is often not passed in,
        // and we want to be able to at least test for non-strategic in that case.
        // Resource type check is last - cannot succeed if no resource here
        var resourceObject = TileResource;
        if (resourceObject == null) return false;
        var hasResourceWithFilter =
            resourceObject.Name == filter ||
                ((IHasUniques)resourceObject).HasTagUnique(filter, StateThisTile) ||
                (filter.EndsWith(" resource", StringComparison.Ordinal) ? filter[..^" resource".Length] : filter) == resourceObject.ResourceType.ToString();
        if (!hasResourceWithFilter) return false;

        // Now that we know that this resource matches the filter - can the observer see that there's a resource here?
        if (resourceObject.RevealedBy == null) return true;  // no need for tech
        if (observingCiv == null) return true;  // can't check tech, assume visible - consistent with the direct resource-name check above
        return observingCiv.CanSeeResource(resourceObject);
    }

    public bool IsAdjacentToCoast() => IsLand && IsAdjacentToCoastLazy;

    public List<Tile> GetViewableTilesList(int distance) => TileMap.GetViewableTiles(Position, distance);
    public IEnumerable<Tile> GetTilesInDistance(int distance) => TileMap.GetTilesInDistance(Position, distance);
    public IEnumerable<Tile> GetTilesInDistanceRange(IntRange range) => TileMap.GetTilesInDistanceRange(Position, range);
    public IEnumerable<Tile> GetTilesAtDistance(int distance) => TileMap.GetTilesAtDistance(Position, distance);

    public void ForEachTileInDistance(int distance, Action<Tile> op) => TileMap.ForEachTileInDistance(Position, distance, _ => true, op);
    public void ForEachTileInDistance(int distance, Func<Tile, bool> filter, Action<Tile> op) => TileMap.ForEachTileInDistance(Position, distance, filter, op);
    public void ForEachTileInDistanceRange(IntRange range, Action<Tile> op) => TileMap.ForEachTileInDistanceRange(Position, range, _ => true, op);
    public void ForEachTileInDistanceRange(IntRange range, Func<Tile, bool> filter, Action<Tile> op) => TileMap.ForEachTileInDistanceRange(Position, range, filter, op);
    public void ForEachTileAtDistance(int distance, Action<Tile> op) => TileMap.ForEachTileAtDistance(Position, distance, _ => true, op);
    public void ForEachTileAtDistance(int distance, Func<Tile, bool> filter, Action<Tile> op) => TileMap.ForEachTileAtDistance(Position, distance, filter, op);

    public float GetDefensiveBonus(bool includeImprovementBonus = true, IGameContextUnit? unit = null)
    {
        var bonus = baseTerrainObject.NotNull().DefenceBonus;
        if (TerrainFeatureObjects.Count > 0)
        {
            var otherTerrainBonus = TerrainFeatureObjects.Max(it => it.DefenceBonus);
            if (otherTerrainBonus != 0f) bonus = otherTerrainBonus;  // replaces baseTerrainObject
        }
        if (NaturalWonder != null) bonus += GetNaturalWonder().DefenceBonus;
        var tileImprovement = GetUnpillagedTileImprovement();
        if (tileImprovement != null && includeImprovementBonus)
        {
            var uniques = (IHasUniques)tileImprovement;
            uniques.ForEachMatchingUnique(UniqueType.DefensiveBonus, unit?.CacheState ?? StateThisTile, unique =>
            {
                bonus += ParseFloat(unique.Params[0]) / 100;
            });
        }
        return bonus;
    }

    /// <param name="otherTile">Destination tile</param>
    /// <returns>Shortest distance from this <see cref="Tile"/> to <paramref name="otherTile"/> in count of tiles including impassable tiles but not including origin tile</returns>
    public int AerialDistanceTo(Tile otherTile)
    {
        var positionHexcoord = Position;
        var otherPositionHexcoord = otherTile.Position;

        var xDelta = positionHexcoord.x - otherPositionHexcoord.x;
        var yDelta = positionHexcoord.y - otherPositionHexcoord.y;
        var distance = Math.Max(Math.Max(Math.Abs(xDelta), Math.Abs(yDelta)), Math.Abs(xDelta - yDelta));

        if (!TileMap.MapParameters.WorldWrap || distance <= TileMap.Width / 2) return distance;

        var otherTileUnwrappedPos = TileMap.GetUnwrappedPosition(otherPositionHexcoord);
        var xDeltaWrapped = positionHexcoord.x - otherTileUnwrappedPos.x;
        var yDeltaWrapped = positionHexcoord.y - otherTileUnwrappedPos.y;
        var wrappedDistance = Math.Max(Math.Max(Math.Abs(xDeltaWrapped), Math.Abs(yDeltaWrapped)), Math.Abs(xDeltaWrapped - yDeltaWrapped));

        return Math.Min(distance, wrappedDistance);
    }

    public bool CanBeSettled(IGameContextCivilization civ)
    {
        var modConstants = TileMap.GameInfo.GetModConstants();
        if (IsWater || IsImpassible()) return false;
        if (GetTilesInDistance(modConstants.MinimalCityDistanceOnDifferentContinents)
                .Any(it => it.IsCityCenter() && it.GetContinent() != GetContinent())) return false;
        if (GetTilesInDistance(modConstants.MinimalCityDistance)
                .Any(it => it.IsCityCenter() && it.GetContinent() == GetContinent())) return false;
        // cannot settle in someone else's territory
        if (OwningCity != null && OwningCity.Civ != civ) return false;
        return true;
    }

    /// <summary>The two tiles have a river between them</summary>
    public bool IsConnectedByRiver(Tile otherTile)
    {
        if (otherTile == this) throw new InvalidOperationException("Should not be called to compare to self!");

        return TileMap.GetNeighborTileClockPosition(this, otherTile) switch
        {
            2 => otherTile.HasBottomLeftRiver, // we're to the bottom-left of it
            4 => HasBottomRightRiver, // we're to the top-left of it
            6 => HasBottomRiver, // we're directly above it
            8 => HasBottomLeftRiver, // we're to the top-right of it
            10 => otherTile.HasBottomRightRiver, // we're to the bottom-right of it
            12 => otherTile.HasBottomRiver, // we're directly below it
            _ => throw new InvalidOperationException("Should never call this function on a non-neighbor!"),
        };
    }

    private bool isAdjacentToRiver;
    private bool isAdjacentToRiverKnown;

    public bool IsAdjacentToRiver()
    {
        if (!isAdjacentToRiverKnown)
        {
            isAdjacentToRiver =
                // These are so if you add a river at the bottom of the map (no neighboring tile to be connected to)
                //   that tile is still considered adjacent to river
                HasBottomLeftRiver || HasBottomRiver || HasBottomRightRiver
                    || Neighbors.Any(IsConnectedByRiver);
            isAdjacentToRiverKnown = true;
        }
        return isAdjacentToRiver;
    }

    /// <summary>Allows resetting the cached value <see cref="IsAdjacentToRiver"/> will return</summary>
    /// <param name="isKnownTrue">Set this to indicate you need to update the cache due to <b>adding</b> a river edge
    /// (removing would need to look at other edges, and that is what isAdjacentToRiver will do)</param>
    private void ResetAdjacentToRiverTransient(bool isKnownTrue = false)
    {
        isAdjacentToRiver = isKnownTrue;
        isAdjacentToRiverKnown = isKnownTrue;
    }

    /// <returns>whether units of <paramref name="civInfo"/> can pass through this tile, considering only civ-wide filters.
    /// Use <c>UnitMovement.canPassThrough</c> to check whether a specific unit can pass through a tile.</returns>
    public bool CanCivPassThrough(IGameContextCivilization civInfo)
    {
        var tileOwner = GetOwner();
        // comparing the CivInfo objects is cheaper than comparing strings
        if (tileOwner == null || tileOwner == civInfo) return true;
        if (IsCityCenter() && civInfo.IsAtWarWith(tileOwner)
                && !GetCity().NotNull().HasJustBeenConquered) return false;
        return civInfo.CanPassThroughTiles(tileOwner);
    }

    public bool HasConnection(IGameContextCivilization civInfo) =>
        GetUnpillagedRoad() != RoadStatus.None || ForestOrJungleAreRoads(civInfo);

    public bool HasRoadConnection(IGameContextCivilization civInfo, bool mustBeUnpillaged) =>
        mustBeUnpillaged
            ? GetUnpillagedRoad() == RoadStatus.Road || ForestOrJungleAreRoads(civInfo)
            : RoadStatus == RoadStatus.Road || ForestOrJungleAreRoads(civInfo);

    public bool HasRailroadConnection(bool mustBeUnpillaged) =>
        mustBeUnpillaged
            ? GetUnpillagedRoad() == RoadStatus.Railroad
            : RoadStatus == RoadStatus.Railroad;

    public RoadStatus GetConnectionStatus(IGameContextCivilization civInfo)
    {
        var roadType = GetUnpillagedRoad();
        if (roadType != RoadStatus.None)
            return roadType;
        if (ForestOrJungleAreRoads(civInfo))
            return RoadStatus.Road;
        return RoadStatus.None;
    }

    private bool ForestOrJungleAreRoads(IGameContextCivilization civInfo) =>
        civInfo.ForestsAndJunglesAreRoads
            && (TerrainFeatures.Contains(Constants.Jungle) || TerrainFeatures.Contains(Constants.Forest))
            && IsFriendlyTerritory(civInfo);

    /// <remarks>Deviation: Kotlin returns a <c>HashSet</c> (JVM hash order), this one keeps insertion order.</remarks>
    public LinkedHashSet<string> GetRulesetIncompatibility(Ruleset ruleset)
    {
        var output = new LinkedHashSet<string>();
        if (!ruleset.Terrains.ContainsKey(BaseTerrain))
            output.Add($"Base terrain [{BaseTerrain}] does not exist in ruleset!");
        foreach (var terrainFeature in TerrainFeatures.Where(it => !ruleset.Terrains.ContainsKey(it)))
            output.Add($"Terrain feature [{terrainFeature}] does not exist in ruleset!");
        if (Resource != null && !ruleset.TileResources.ContainsKey(Resource))
            output.Add($"Resource [{Resource}] does not exist in ruleset!");
        if (Improvement != null && !ruleset.TileImprovements.ContainsKey(Improvement))
            output.Add($"Improvement [{Improvement}] does not exist in ruleset!");
        if (NaturalWonder != null && !ruleset.Terrains.ContainsKey(NaturalWonder))
            output.Add($"Natural Wonder [{NaturalWonder}] does not exist in ruleset!");
        return output;
    }

    public int GetContinent() => continent;

    /// <summary>Checks if this tile is marked as target tile for a building with a <see cref="UniqueType.CreatesOneImprovement"/> unique</summary>
    public bool IsMarkedForCreatesOneImprovement() =>
        TurnsToImprovement < 0 && ImprovementInProgress != null;

    /// <summary>Checks if this tile is marked as target tile for a building with a <see cref="UniqueType.CreatesOneImprovement"/> unique creating a specific <paramref name="improvement"/></summary>
    public bool IsMarkedForCreatesOneImprovement(string improvement) =>
        TurnsToImprovement < 0 && ImprovementInProgress == improvement;

    private double ApproximateMajorDepositDistribution()
    {
        // We can't replicate the MapRegions resource distributor, so let's try to get
        // a close probability of major deposits per tile
        var probability = 0.0;
        foreach (var unique in AllTerrains.SelectMany(it => ((IHasUniques)it).GetMatchingUniques(UniqueType.MajorStrategicFrequency)))
        {
            if (!int.TryParse(unique.Params[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequency)) continue;
            if (frequency <= 0) continue;
            // The unique param is literally "every N tiles", so to get a probability p=1/f
            probability += 1.0 / frequency;
        }
        return probability == 0.0 ? 0.04  // This is the default of 1 per 25 tiles
            : probability;
    }

    /// <summary>Will be false if this is a "fake tile" - either created for calculation purposes,
    /// or to display how things look e.g. in Civilopedia</summary>
    public bool IsTilemapInitialized() => tileMap != null;

    #endregion
}
