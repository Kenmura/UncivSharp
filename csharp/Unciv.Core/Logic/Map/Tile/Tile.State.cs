using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Civilization;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Utils;
using CityClass = Unciv.Logic.City.City;
using CivilizationClass = Unciv.Logic.Civilization.Civilization;
using MapGeneratorClass = Unciv.Logic.Map.MapGenerator.MapGenerator;
using MapUnitClass = Unciv.Logic.Map.MapUnits.MapUnit;

namespace Unciv.Logic.Map.Tiles;

public partial class Tile
{
    #region State-changing functions

    /// <summary>Do not run this on cloned tiles, since then the cloned *units* will be assigned to the civs
    /// Instead run setTerrainTransients</summary>
    public void SetTransients()
    {
        SetTerrainTransients();
        SetUnitTransients(true);
        SetOwnerTransients();
    }

    public void SetTerrainTransients()
    {
        if (!Ruleset.Terrains.ContainsKey(BaseTerrain))
            throw new InvalidOperationException($"Terrain {BaseTerrain} does not exist in ruleset!");
        baseTerrainObject = Ruleset.Terrains[BaseTerrain];
        SetTerrainFeatures(TerrainFeatures);
        IsWater = GetBaseTerrain().Type == TerrainType.Water;
        IsLand = GetBaseTerrain().Type == TerrainType.Land;
        IsOcean = GetBaseTerrain().IsOcean;

        // Resource amounts missing - Old save or bad mapgen?
        if (IsTilemapInitialized() && TileResource?.ResourceType == ResourceType.Strategic && ResourceAmount == 0)
        {
            // Let's assume it's a small deposit
            SetTileResource(TileResource, majorDeposit: false);
        }
    }

    public void SetUnitTransients(bool unitCivTransients)
    {
        foreach (var unit in GetUnits())
        {
            unit.CurrentTile = this;
            if (unitCivTransients)
                unit.AssignOwner((Civilization.Civilization)TileMap.GameInfo.GetCivilization(unit.Owner), false);
            unit.SetTransients(Ruleset);
        }
    }

    public void SetOwnerTransients()
    {
        // If it has an owning city, the state was already set in setOwningCity
        if (OwningCity == null)
            StateThisTile = new GameContext(tile: this,
                // When generating maps we call this function but there's no gameinfo
                gameInfo: TileMap.HasGameInfo() ? TileMap.GameInfo : null);

        if (OwningCity == null && roadOwner != "")
        {
            if (roadOwnerObject == null && TileMap.HasGameInfo())
                roadOwnerObject = TileMap.GameInfo.GetCivilization(roadOwner);
            GetRoadOwner().NotNull().NeutralRoads.Add(Position);
        }
    }

    public void SetOwningCity(CityClass? city)
    {
        if (roadOwner != "" && roadOwnerObject == null)
            roadOwnerObject = TileMap.GameInfo.GetCivilization(roadOwner);
        if (city != null)
        {
            if (RoadStatus != RoadStatus.None && roadOwner != "") // remove previous neutral tile owner
                GetRoadOwner().NotNull().NeutralRoads.Remove(Position);
            roadOwner = city.Civ.CivId; // only when taking control, otherwise last owner
            roadOwnerObject = city.Civ;
        }
        else if (RoadStatus != RoadStatus.None && OwningCity != null)
        {
            // Razing City! Remove owner
            roadOwner = "";
            roadOwnerObject = null;
        }
        OwningCity = GameEventHooks.TileOwnerChanged(this, OwningCity, city);
        StateThisTile = new GameContext(tile: this, city: city, gameInfo: TileMap.GameInfo);
        isCityCenterInternal = GetCity()?.Location == Position;
        RememberOwnerForViewers();
    }

    /// <summary>
    /// Sets this tile's <see cref="Resource"/> field but not its <see cref="ResourceAmount"/> fields. If <paramref name="updateCache"/> is <c>true</c>
    /// it will also set the <see cref="TileResource"/> transient field
    /// </summary>
    public void SetTileResource(string? newResource, bool updateCache = true)
    {
        if (updateCache)
        {
            TileResource = newResource == null ? null : Ruleset.TileResources.GetValueOrDefault(newResource);
            return;
        }
        Resource = newResource;
        if (IsTilemapInitialized() && ReferenceEquals(TileMap.GetOrNull(Position.x, Position.y), this)) TileMap.BumpMapContentRevision();
    }

    /// <summary>
    /// Sets this tile's <see cref="Resource"/> and, if <paramref name="newResource"/> is a Strategic resource, <see cref="ResourceAmount"/> fields.
    /// <see cref="ResourceAmount"/> is determined by <see cref="MapParameters.MapResources"/> and <paramref name="majorDeposit"/>, and
    /// if the latter is <c>null</c> a random choice between major and minor deposit is made, approximating
    /// the frequency MapRegions would use.
    /// </summary>
    /// <param name="newResource">The resource to place.</param>
    /// <param name="majorDeposit"><c>true</c> major, <c>false</c> minor, <c>null</c> chosen randomly.</param>
    /// <param name="rng">The randomness source for that step. Deviation: Kotlin defaults to <c>Random.Default</c>; the engine has no
    /// unseeded randomness, so <c>null</c> with a <c>null</c> <paramref name="majorDeposit"/> throws.</param>
    public void SetTileResource(TileResource newResource, bool? majorDeposit = null, GameRandom? rng = null)
    {
        TileResource = newResource;

        if (newResource.ResourceType != ResourceType.Strategic)
        {
            ResourceAmount = 0;
            return;
        }

        foreach (var unique in ((IHasUniques)newResource).GetMatchingUniques(UniqueType.ResourceAmountOnTiles, StateThisTile))
        {
            if (MatchesTerrainFilter(unique.Params[0], null))
            {
                ResourceAmount = int.Parse(unique.Params[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                return;
            }
        }

        var majorDepositFinal = majorDeposit
            ?? (rng ?? throw new InvalidOperationException("A random source is required to choose the deposit size")).NextDouble() < ApproximateMajorDepositDistribution();
        var depositAmounts = majorDepositFinal ? newResource.MajorDepositAmount : newResource.MinorDepositAmount;
        var mapResources = TileMap.MapParameters.MapResources;
        ResourceAmount = mapResources == MapResourceSetting.Sparse.GetLabel() ? depositAmounts.Sparse
            : mapResources == MapResourceSetting.Abundant.GetLabel() ? depositAmounts.Abundant
            : depositAmounts.Default;
    }

    public void SetTerrainFeatures(IReadOnlyList<string> terrainFeatureList)
    {
        TerrainFeatures = terrainFeatureList;
        TerrainFeatureObjects = terrainFeatureList.Select(it => Ruleset.Terrains.GetValueOrDefault(it)).OfType<Terrain>().ToList();
        var all = new List<Terrain> { baseTerrainObject.NotNull() }; // There is an assumption here that base terrains do not change
        if (NaturalWonder != null) all.Add(GetNaturalWonder());
        all.AddRange(TerrainFeatureObjects);
        AllTerrains = all; // Save in memory
        UpdateUniqueMap();
        if (TerrainFeatures.Count > 0)
            LastTerrain = Ruleset.Terrains.GetValueOrDefault(TerrainFeatures[^1])
                ?? GetBaseTerrain();  // defense against rare edge cases involving baseTerrain Hill deprecation
        else if (NaturalWonder != null)
            LastTerrain = GetNaturalWonder();
        else
            LastTerrain = GetBaseTerrain();

        UnitHeight = AllTerrains.SelectMany(it => ((IHasUniques)it).GetMatchingUniques(UniqueType.VisibilityElevation))
            .Sum(it => ParseInt(it.Params[0]));
        TileHeight = TerrainHasUnique(UniqueType.BlocksLineOfSightAtSameElevation) ? UnitHeight + 1
            : UnitHeight;
        // T349: the vision caches' only terrain input; clones and display tiles share the map but cannot change any map tile's visibility.
        if (IsTilemapInitialized() && ReferenceEquals(TileMap.GetOrNull(Position.x, Position.y), this)) { TileMap.BumpTerrainRevision(); TileMap.BumpMapContentRevision(); }
    }

    private void UpdateUniqueMap()
    {
        if (!IsTilemapInitialized()) return; // This tile is a fake tile, for visual display only (e.g. map editor, civilopedia)
        var terrainNameList = AllTerrains.Select(it => it.Name).ToList();

        // List hash is function of all its items, so the same items in the same order will always give the same hash
        CachedTerrainData = TileMap.TileUniqueMapCache.GetOrAdd(terrainNameList, _ => new TileMap.TerrainListData(
            new UniqueMap(AllTerrains.SelectMany(it => ((IHasUniques)it).UniqueObjects)),
            new HashSet<string>(terrainNameList)));
    }

    public void AddTerrainFeature(string terrainFeature)
    {
        if (!TerrainFeatures.Contains(terrainFeature))
            SetTerrainFeatures([.. TerrainFeatures, terrainFeature]);
    }

    public void RemoveTerrainFeature(string terrainFeature)
    {
        if (TerrainFeatures.Contains(terrainFeature))
        {
            var list = new List<string>(TerrainFeatures);
            list.Remove(terrainFeature);
            SetTerrainFeatures(list);
        }
    }

    public void RemoveTerrainFeatures() => SetTerrainFeatures([]);

    /// <summary>Clean stuff missing in <paramref name="ruleset"/> - called from TileMap.removeMissingTerrainModReferences.
    /// Must be able to run before <see cref="SetTransients"/> - and does not need to fix transients.</summary>
    public void RemoveMissingTerrainModReferences(Ruleset ruleset)
    {
        TerrainFeatures = TerrainFeatures.Where(it => ruleset.Terrains.ContainsKey(it)).ToList();
        if (Resource != null && !ruleset.TileResources.ContainsKey(Resource))
            TileResource = null;
        if (Improvement != null && !ruleset.TileImprovements.ContainsKey(Improvement))
            Improvement = null;
        if (ImprovementQueue.Any(it => !ruleset.TileImprovements.ContainsKey(it.Improvement)))
            ImprovementQueue.Clear(); // Just get rid of everything, all bets are off
        if (NaturalWonder != null && !ruleset.Terrains.ContainsKey(NaturalWonder))
            NaturalWonder = null;
    }

    /// <summary>If the unit isn't in the ruleset we can't even know what type of unit this is! So check each place
    /// This works with no transients so can be called from gameInfo.setTransients with no fear</summary>
    public void RemoveUnit(MapUnitClass mapUnit)
    {
        if (AirUnits.Contains(mapUnit)) AirUnits.Remove(mapUnit);
        else if (CivilianUnit == mapUnit) CivilianUnit = null;
        else if (MilitaryUnit == mapUnit) MilitaryUnit = null;
    }

    public void SetImprovementBasic(TileImprovement? tileImprovement)
    {
        if (tileImprovement == null)
        {
            TileImprovement = null;
            Improvement = null;
        }
        else
        {
            TileImprovement = tileImprovement;
            Improvement = tileImprovement.Name;
        }
    }

    /// <summary>Does not remove roads</summary>
    public void RemoveImprovement() =>
        ImprovementFunctions.SetImprovement((TileImprovement?)null);

    public void SetImprovement(string improvementName, IGameContextCivilization? civToHandleCompletion = null, IGameContextUnit? unit = null) =>
        ImprovementFunctions.SetImprovement(improvementName, civToHandleCompletion, unit);

    public void SetImprovement(TileImprovement? improvement, IGameContextCivilization? civToHandleCompletion = null, IGameContextUnit? unit = null) =>
        ImprovementFunctions.SetImprovement(improvement, civToHandleCompletion, unit);

    /// <summary>Called from <c>UnitTurnManager.endTurn</c> when a Worker "spends time" here</summary>
    /// <returns><c>true</c> if any work got finished and upstream might want to update things</returns>
    public bool DoWorkerTurn(MapUnitClass worker)
    {
        if (IsMarkedForCreatesOneImprovement()) return false;
        if (ImprovementQueue.Count == 0) return false;

        if (ImprovementQueue[0].CountDown()) return false;
        var queueEntry = ImprovementQueue[0];
        ImprovementQueue.RemoveAt(0);

        if (worker.Civ.IsActive())
            worker.Civ.GameInfo.AddCompletedTutorialTask(worker.Civ.CivId, "Construct an improvement");

        SetImprovement(queueEntry.Improvement, worker.Civ, worker);
        Archaeology.ArchaeologyRules.OnImprovementBuilt(this, worker); // B010: a finished dig uses up the archaeologist
        return true;
    }

    public void SetImprovementBasic(string improvementName)
    {
        var tileImprovement = Ruleset.TileImprovements.GetValueOrDefault(improvementName);
        TileImprovement = tileImprovement;
        Improvement = tileImprovement?.Name;
    }

    // function handling when removing a road from the tile
    public void RemoveRoad() => SetRoadStatus(RoadStatus.None, null);

    public void SetRoadStatus(RoadStatus newRoadStatus, IGameContextCivilization? creatingCivInfo)
    {
        RoadStatus = newRoadStatus;
        RoadIsPillaged = false;
        var currentOwner = GetOwner();
        if (newRoadStatus == RoadStatus.None && OwningCity == null)
            GetRoadOwner()?.NeutralRoads.Remove(Position);
        else if (currentOwner != null)
        {
            // Owned tiles must not fall through to the neutral-road branch just because
            // the owner field is already correct (city founding sets ownership first).
            if (currentOwner != roadOwnerObject)
            {
                roadOwner = currentOwner.CivId;
                roadOwnerObject = currentOwner;
            }
        }
        else if (creatingCivInfo != null)
        {
            roadOwner = creatingCivInfo.CivId; // neutral tile, use building unit
            roadOwnerObject = creatingCivInfo;
            creatingCivInfo.NeutralRoads.Add(Position);
        }
    }

    /// <summary>Clear road owner, for console <c>civ remove</c></summary>
    public void RemoveRoadOwner()
    {
        roadOwner = "";
        roadOwnerObject = null;
    }

    public void StartWorkingOnImprovement(TileImprovement improvement, CivilizationClass civInfo, MapUnitClass unit)
    {
        if (IsMarkedForCreatesOneImprovement()) return;

        var gameContext = new GameContext(civInfo, unit: unit, tile: this);
        var stockpiledResourceRequirements = improvement.GetStockpiledResourceRequirements(gameContext);
        if (stockpiledResourceRequirements.Any(it => civInfo.GetResourceAmount(it.Key) < it.Value))
            return;
        foreach (var (resourceName, amount) in stockpiledResourceRequirements)
        {
            if (!Ruleset.TileResources.TryGetValue(resourceName, out var resource)) continue;
            civInfo.GainStockpiledResource(resource, -amount);
        }

        ImprovementQueue.Clear();
        QueueImprovement(improvement, civInfo, unit);
    }

    /// <summary>Clears <see cref="ImprovementQueue"/></summary>
    public void StopWorkingOnImprovement()
    {
        if (IsMarkedForCreatesOneImprovement()) return;
        ImprovementQueue.Clear();
    }

    /// <summary>Adds an entry to the <see cref="ImprovementQueue"/>, by looking up the time it takes using <paramref name="civInfo"/> and <paramref name="unit"/></summary>
    public void QueueImprovement(TileImprovement improvement, CivilizationClass civInfo, MapUnitClass unit)
    {
        int turns = civInfo.GameInfo.GameParameters.GodMode ? 1 : improvement.GetTurnsToBuild(civInfo, unit);
        QueueImprovement(improvement.Name, turns);
    }

    /// <summary>Adds an entry to the <see cref="ImprovementQueue"/> with explicit <paramref name="turnsToImprovement"/></summary>
    public void QueueImprovement(string improvementName, int turnsToImprovement) =>
        ImprovementQueue.Add(new ImprovementQueueEntry(improvementName, turnsToImprovement));

    public void SetBaseTerrain(Terrain baseTerrainObject)
    {
        BaseTerrain = baseTerrainObject.Name;
        this.baseTerrainObject = baseTerrainObject;
        TileNormalizer.NormalizeToRuleset(this, Ruleset);
        SetTerrainFeatures(TerrainFeatures);
        SetTerrainTransients();
    }

    /// <summary>Sets tile improvement to pillaged (without prior checks for validity)
    /// and ensures that matching <see cref="UniqueType.CreatesOneImprovement"/> queued buildings are removed.</summary>
    public void SetPillaged()
    {
        if (!CanPillageTile())
            return;
        // http://well-of-souls.com/civ/civ5_improvements.html says that naval improvements are destroyed upon pillage
        //    and I can't find any other sources so I'll go with that
        if (!IsLand)
        {
            RemoveImprovement();
            OwningCity?.ReassignPopulationDeferred();
            return;
        }

        // Setting turnsToImprovement might interfere with UniqueType.CreatesOneImprovement
        ImprovementFunctions.RemoveCreatesOneImprovementMarker();
        ImprovementQueue.Clear(); // remove any in progress work as well
        // if no Repair action, destroy improvements instead
        if (!Ruleset.TileImprovements.ContainsKey(Constants.Repair))
        {
            if (CanPillageTileImprovement())
                RemoveImprovement();
            else
                RemoveRoad();
        }
        else
        {
            // otherwise use pillage/repair systems
            if (CanPillageTileImprovement())
            {
                ImprovementIsPillaged = true;
            }
            else
            {
                RoadIsPillaged = true;
                ClearAllPathfindingCaches();
            }
        }

        OwningCity?.ReassignPopulationDeferred();
        OwningCity?.Civ.Cache.UpdateCivResources();
    }

    private void ClearAllPathfindingCaches()
    {
        var units = TileMap.GameInfo.Civilizations
            .Where(it => it.IsAlive())
            .SelectMany(it => it.GetCivUnits());
        foreach (var otherUnit in units)
            otherUnit.ClearPathfindingCache();
    }

    public void SetRepaired()
    {
        ImprovementQueue.Clear();
        if (ImprovementIsPillaged)
            ImprovementIsPillaged = false;
        else
            RoadIsPillaged = false;

        OwningCity?.ReassignPopulationDeferred();
    }

    public void SetExplored(IGameContextCivilization player, bool isExplored, HexCoord? explorerPosition = null)
    {
        if (isExplored)
        {
            // Upstream disables the undo button here (GUI.clearUndoCheckpoints): a client concern.
            if (!exploredBy.Contains(player.CivId))
                exploredBy = ((IReadOnlySet<string>)exploredBy).WithItem(player.CivId);

            if (player.PlayerType == PlayerType.Human)
                player.CheckExploredRegionTilePosition(Position, explorerPosition);
        }
        else
        {
            exploredBy = ((IReadOnlySet<string>)exploredBy).WithoutItem(player.CivId);
        }
    }

    /// <summary>
    /// Assign a continent ID to this tile.
    /// Should only be set once at map generation.
    /// </summary>
    /// <param name="continent">Numeric ID &gt;= 0</param>
    /// <exception cref="InvalidOperationException">when tile already has a continent ID</exception>
    public void SetContinent(int continent)
    {
        if (this.continent != -1)
            throw new InvalidOperationException($"Continent already assigned @ {Position}");
        this.continent = continent;
    }

    /// <summary>Clear continent ID, for map editor</summary>
    public void ClearContinent() => continent = -1;

    /// <summary>
    /// Sets the "has river" state of one edge of this Tile. Works for all six directions.
    /// </summary>
    /// <param name="otherTile">The neighbor tile in the direction the river we wish to change is (If it's not a neighbor, this does nothing).</param>
    /// <param name="newValue">The new river edge state: <c>true</c> to create a river, <c>false</c> to remove one.</param>
    /// <param name="convertTerrains">If true, calls MapGenerator's convertTerrains to apply <see cref="UniqueType.ChangesTerrain"/> effects.</param>
    /// <returns>The state did change (<c>false</c>: the edge already had the <paramref name="newValue"/>)</returns>
    public bool SetConnectedByRiver(Tile otherTile, bool newValue, bool convertTerrains = false)
    {
        Tile owner;
        int edge; // 0 = bottom-left, 1 = bottom-right, 2 = bottom
        switch (TileMap.GetNeighborTileClockPosition(this, otherTile))
        {
            case 2: (owner, edge) = (otherTile, 0); break; // we're to the bottom-left of it
            case 4: (owner, edge) = (this, 1); break; // we're to the top-left of it
            case 6: (owner, edge) = (this, 2); break; // we're directly above it
            case 8: (owner, edge) = (this, 0); break; // we're to the top-right of it
            case 10: (owner, edge) = (otherTile, 1); break; // we're to the bottom-right of it
            case 12: (owner, edge) = (otherTile, 2); break; // we're directly below it
            default: return false;
        }
        var current = edge == 0 ? owner.HasBottomLeftRiver : edge == 1 ? owner.HasBottomRightRiver : owner.HasBottomRiver;
        if (current == newValue) return false;
        if (edge == 0) owner.HasBottomLeftRiver = newValue;
        else if (edge == 1) owner.HasBottomRightRiver = newValue;
        else owner.HasBottomRiver = newValue;
        foreach (var tile in new[] { this, otherTile })
            tile.ResetAdjacentToRiverTransient(newValue);
        if (convertTerrains)
            MapGeneratorClass.Helpers.ConvertTerrains(Ruleset, [this, otherTile]);
        return true;
    }

    #endregion

    private static int ParseInt(string text) => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static float ParseFloat(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    #region Overrides

    /// <summary>Shows important properties of this tile for debugging _only_, it helps to see what you're doing</summary>
    public override string ToString()
    {
        var lineList = new List<string> { $"Tile @{Position}" };
        if (baseTerrain == null) return lineList[0] + ", uninitialized";
        if (IsCityCenter()) lineList.Add(GetCity().NotNull().Name);
        lineList.Add(BaseTerrain);
        foreach (var terrainFeature in TerrainFeatures) lineList.Add(terrainFeature);
        if (TileResource != null)
        {
            lineList.Add(TileResource.ResourceType == ResourceType.Strategic
                ? $"{{{ResourceAmount}}} {{{Resource}}}"
                : Resource.NotNull());
        }
        if (NaturalWonder != null) lineList.Add(NaturalWonder);
        if (RoadStatus != RoadStatus.None && !IsCityCenter()) lineList.Add(RoadStatus.ToString());
        if (Improvement != null) lineList.Add(Improvement);
        if (CivilianUnit != null) lineList.Add(CivilianUnit.Name + " - " + CivilianUnit.Civ.CivId);
        if (MilitaryUnit != null) lineList.Add(MilitaryUnit.Name + " - " + MilitaryUnit.Civ.CivId);
        if (baseTerrainObject != null && IsImpassible()) lineList.Add(Constants.Impassable);
        return string.Join(", ", lineList);
    }

    #endregion

    #region Json

    /// <summary>
    /// Reads and writes the serialized fields the way the libGDX Json does for Kotlin <c>Tile</c> (T107): field names are the Kotlin ones,
    /// fields equal to the default of a fresh <c>Tile()</c> are omitted, unknown fields are ignored, and a list may be a plain array or the
    /// <c>{"class":..., "items":[...]}</c> form. <c>exploredBy</c> (a Java HashSet there) is written in ordinal order, which is deterministic.
    /// The unit fields (militaryUnit, civilianUnit, airUnits) are written first, as in Kotlin's declaration order, with <c>MapUnit.MapUnitJsonConverter</c>.
    /// </summary>
    internal sealed class TileJsonConverter : JsonConverter<Tile>
    {
        public override Tile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"Expected StartObject for Tile, got {reader.TokenType}");
            var tile = new Tile();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) return tile;
                var name = reader.GetString();
                reader.Read();
                var isNull = reader.TokenType == JsonTokenType.Null;
                switch (name)
                {
                    case "militaryUnit":
                        tile.MilitaryUnit = isNull ? null : new MapUnitClass.MapUnitJsonConverter().Read(ref reader, typeof(MapUnitClass), options);
                        break;
                    case "civilianUnit":
                        tile.CivilianUnit = isNull ? null : new MapUnitClass.MapUnitJsonConverter().Read(ref reader, typeof(MapUnitClass), options);
                        break;
                    case "airUnits":
                        tile.AirUnits = isNull ? [] : ReadUnitList(ref reader, options);
                        break;
                    case "position":
                        tile.Position = isNull ? default : new HexCoordConverter().Read(ref reader, typeof(HexCoord), options);
                        break;
                    case "baseTerrain":
                        if (!isNull) tile.baseTerrain = reader.GetString();
                        break;
                    case "terrainFeatures":
                        tile.TerrainFeatures = isNull ? [] : ReadStringList(ref reader);
                        break;
                    case "exploredBy":
                        tile.exploredBy = isNull ? [] : [.. ReadStringList(ref reader)];
                        break;
                    case "naturalWonder":
                        tile.NaturalWonder = isNull ? null : reader.GetString();
                        break;
                    case "resource":
                        tile.Resource = isNull ? null : reader.GetString();
                        break;
                    case "resourceAmount":
                        tile.ResourceAmount = JsonValueReader.ReadInt(ref reader, "Tile.resourceAmount");
                        break;
                    case "improvement":
                        tile.Improvement = isNull ? null : reader.GetString();
                        break;
                    case "improvementIsPillaged":
                        tile.ImprovementIsPillaged = reader.GetBoolean();
                        break;
                    case "improvementQueue":
                        tile.ImprovementQueue.Clear();
                        if (!isNull) ReadImprovementQueue(ref reader, tile.ImprovementQueue);
                        break;
                    case "roadStatus":
                        tile.RoadStatus = ReadRoadStatus(ref reader);
                        break;
                    case "roadIsPillaged":
                        tile.RoadIsPillaged = reader.GetBoolean();
                        break;
                    case "roadOwner":
                        tile.roadOwner = reader.GetString() ?? "";
                        break;
                    case "hasBottomRightRiver":
                        tile.HasBottomRightRiver = reader.GetBoolean();
                        break;
                    case "hasBottomRiver":
                        tile.HasBottomRiver = reader.GetBoolean();
                        break;
                    case "hasBottomLeftRiver":
                        tile.HasBottomLeftRiver = reader.GetBoolean();
                        break;
                    case "history":
                        tile.History = new TileHistoryConverter().Read(ref reader, typeof(TileHistory), options);
                        break;
                    case "continent":
                        tile.continent = JsonValueReader.ReadInt(ref reader, "Tile.continent");
                        break;
                    case "archaeology":
                        tile.ArchaeologyRecord = JsonSerializer.Deserialize<Archaeology.ArchaeologyRecord>(ref reader, options);
                        break;
                    default:
                        reader.Skip(); // ignoreUnknownFields
                        break;
                }
            }
            throw new JsonException("Unexpected end of JSON while reading Tile");
        }

        private static List<MapUnitClass> ReadUnitList(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            var result = new List<MapUnitClass>();
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString();
                    reader.Read();
                    if (key == "items") result = ReadUnitList(ref reader, options);
                    else reader.Skip();
                }
                return result;
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected StartArray for airUnits, got {reader.TokenType}");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                result.Add(new MapUnitClass.MapUnitJsonConverter().Read(ref reader, typeof(MapUnitClass), options));
            return result;
        }

        private static List<string> ReadStringList(ref Utf8JsonReader reader)
        {
            var result = new List<string>();
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString();
                    reader.Read();
                    if (key == "items") result = ReadStringList(ref reader);
                    else reader.Skip();
                }
                return result;
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected StartArray for a string list, got {reader.TokenType}");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                result.Add(reader.GetString().NotNull());
            return result;
        }

        private static void ReadImprovementQueue(ref Utf8JsonReader reader, List<ImprovementQueueEntry> queue)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString();
                    reader.Read();
                    if (key == "items") ReadImprovementQueue(ref reader, queue);
                    else reader.Skip();
                }
                return;
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected StartArray for improvementQueue, got {reader.TokenType}");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var improvement = "";
                var turns = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString();
                    reader.Read();
                    if (key == "improvement") improvement = reader.GetString() ?? "";
                    else if (key == "turnsToImprovement") turns = JsonValueReader.ReadInt(ref reader, "ImprovementQueueEntry.turnsToImprovement");
                    else reader.Skip();
                }
                queue.Add(new ImprovementQueueEntry(improvement, turns));
            }
        }

        private static RoadStatus ReadRoadStatus(ref Utf8JsonReader reader)
        {
            var text = reader.GetString();
            if (text == null || !Enum.TryParse<RoadStatus>(text, true, out var status) || !Enum.IsDefined(status))
                throw new JsonException($"Unknown RoadStatus '{text}'");
            return status;
        }

        public override void Write(Utf8JsonWriter writer, Tile value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            var unitConverter = new MapUnitClass.MapUnitJsonConverter();
            if (value.MilitaryUnit != null)
            {
                writer.WritePropertyName("militaryUnit");
                unitConverter.Write(writer, value.MilitaryUnit, options);
            }
            if (value.CivilianUnit != null)
            {
                writer.WritePropertyName("civilianUnit");
                unitConverter.Write(writer, value.CivilianUnit, options);
            }
            if (value.AirUnits.Count > 0)
            {
                writer.WriteStartArray("airUnits");
                foreach (var airUnit in value.AirUnits) unitConverter.Write(writer, airUnit, options);
                writer.WriteEndArray();
            }
            if (value.Position != default)
            {
                writer.WritePropertyName("position");
                new HexCoordConverter().Write(writer, value.Position, options);
            }
            if (value.baseTerrain != null) writer.WriteString("baseTerrain", value.baseTerrain);
            if (value.TerrainFeatures.Count > 0)
            {
                writer.WriteStartArray("terrainFeatures");
                foreach (var feature in value.TerrainFeatures) writer.WriteStringValue(feature);
                writer.WriteEndArray();
            }
            if (value.exploredBy.Count > 0)
            {
                writer.WriteStartArray("exploredBy");
                foreach (var civ in value.exploredBy.Order(StringComparer.Ordinal)) writer.WriteStringValue(civ);
                writer.WriteEndArray();
            }
            if (value.NaturalWonder != null) writer.WriteString("naturalWonder", value.NaturalWonder);
            if (value.Resource != null) writer.WriteString("resource", value.Resource);
            if (value.ResourceAmount != 0) writer.WriteNumber("resourceAmount", value.ResourceAmount);
            if (value.Improvement != null) writer.WriteString("improvement", value.Improvement);
            if (value.ImprovementIsPillaged) writer.WriteBoolean("improvementIsPillaged", true);
            if (value.ImprovementQueue.Count > 0)
            {
                writer.WriteStartArray("improvementQueue");
                foreach (var entry in value.ImprovementQueue)
                {
                    writer.WriteStartObject();
                    if (entry.Improvement != "") writer.WriteString("improvement", entry.Improvement);
                    if (entry.TurnsToImprovement != 0) writer.WriteNumber("turnsToImprovement", entry.TurnsToImprovement);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (value.RoadStatus != RoadStatus.None) writer.WriteString("roadStatus", value.RoadStatus.ToString());
            if (value.RoadIsPillaged) writer.WriteBoolean("roadIsPillaged", true);
            if (value.roadOwner != "") writer.WriteString("roadOwner", value.roadOwner);
            if (value.HasBottomRightRiver) writer.WriteBoolean("hasBottomRightRiver", true);
            if (value.HasBottomRiver) writer.WriteBoolean("hasBottomRiver", true);
            if (value.HasBottomLeftRiver) writer.WriteBoolean("hasBottomLeftRiver", true);
            if (!value.History.Equals(new TileHistory()))
            {
                writer.WritePropertyName("history");
                new TileHistoryConverter().Write(writer, value.History, options);
            }
            if (value.continent != -1) writer.WriteNumber("continent", value.continent);
            if (value.ArchaeologyRecord != null)
            {
                writer.WritePropertyName("archaeology");
                JsonSerializer.Serialize(writer, value.ArchaeologyRecord, options);
            }
            writer.WriteEndObject();
        }
    }

    #endregion
}
