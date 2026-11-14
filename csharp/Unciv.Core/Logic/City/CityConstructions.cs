using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Civilization;
using Unciv.Logic.Map.MapUnits;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.Ruleset.Unit;
using Unciv.Models.StatTypes;
using Unciv.Presentation.Components.Extensions;
using Unciv.Utils;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;
using TileClass = Unciv.Logic.Map.Tiles.Tile;

namespace Unciv.Logic.City;

// Ported from core/src/com/unciv/logic/city/CityConstructions.kt (T063).
//
// Deviations:
// - timeThis profiling, the purity annotations and the "single read, the field may be swapped by another thread" comments are dropped; the
//   queue is still only ever replaced as a whole, never mutated in place (see EditQueue).
// - getProductionMarkup is the Kotlin member of the same name; FormattedLine is T095's class.
// - chooseNextConstruction: calls ConstructionAutomation (T158).
//   `Automation.getTileForConstructionImprovement(city, improvement)` is ported (T087).
//   The client settings Kotlin reads from UncivGame.Current (settings.autoAssignCityProduction, worldScreen.autoPlay, multiplayer user id)
//   are the IGameInfoState members IsUsersTurn / AutoAssignCityProduction / IsFullAutoPlayAI, which T046 implements.
// - completeConstruction's saved-promotion block builds PromotionTree(unit) (a UI-package class in Kotlin, ported in T126).
// - builtBuildings is a java.util.HashSet (JavaHashSet) because setTransients builds the built building list in its iteration order, which
//   reaches the unique map, stats and float sums. freeBuildingsProvidedFromThisCity stays a System Dictionary/HashSet like the maps of
//   CivConstructions (nothing iterates it in a state-relevant order).
// - inProgressConstructions is a JavaHashMap: validateInProgressConstructions iterates its keys, and the refunds/upgrades depend on that order.
// - Kotlin `Exception` thrown for a missing building in setTransients is an InvalidOperationException.

/// <summary>
/// City constructions manager.
/// </summary>
/// <remarks>
/// <c>city</c> the city it refers to; <c>currentConstructionName()</c> name of the construction that is currently being produced;
/// <c>currentConstructionIsUserSet</c> a flag indicating if the current construction has been set by the user or by the AI;
/// <c>constructionQueue</c> a list of constructions names enqueued.
/// </remarks>
// Some members are virtual so that the tests of City, CityStats and CivConstructions can replace the construction logic by data-holding fakes
// (Kotlin classes are final; this is the same device as CityStats.Update).
public partial class CityConstructions : IsPartOfGameInfoSerialization
{
    private const int QueueMaxSize = 10;

    #region Non-Serialized Properties
    private City? city;
    /// <summary>Transient. Kotlin <c>lateinit var city</c>: reading before assignment throws.</summary>
    [JsonIgnore]
    public City City
    {
        get => city ?? throw new InvalidOperationException("lateinit property city has not been initialized");
        set => city = value;
    }

    private List<Building> builtBuildingObjects = [];

    private readonly ConcurrentDictionary<string, bool> containedBuildingFiltersCache = new(StringComparer.Ordinal);

    [JsonIgnore]
    public UniqueMap BuiltBuildingUniqueMap { get; } = new();

    public virtual string CurrentConstructionName()
    {
        var queue = ConstructionQueue;
        return queue.Count == 0 ? "" : queue[0];
    }

    public void SetCurrentConstruction(string value) => EditQueue(queue =>
    {
        if (queue.Count == 0) queue.Add(value);
        else queue[0] = value;
    });

    /// <summary>
    /// The queue is never mutated in place, only replaced as a whole, so concurrent readers always see a consistent list.
    /// All modifications must go through here (or assign a freshly built list).
    /// </summary>
    public void EditQueue(Action<List<string>> edit)
    {
        var newQueue = new List<string>(ConstructionQueue);
        edit(newQueue);
        ConstructionQueue = newQueue;
    }
    #endregion

    #region Serialized Fields
    [JsonPropertyName("builtBuildings")]
    public JavaHashSet<string> BuiltBuildings { get; set; } = new();
    [JsonPropertyName("inProgressConstructions")]
    [JsonInclude]
    public JavaHashMap<string, int> InProgressConstructions { get; private set; } = new();
    [JsonPropertyName("currentConstructionIsUserSet")]
    public bool CurrentConstructionIsUserSet { get; set; }

    [JsonPropertyName("constructionQueue")]
    [JsonInclude]
    public IReadOnlyList<string> ConstructionQueue { get; private set; } = new List<string>(QueueMaxSize);
    [JsonPropertyName("productionOverflow")]
    public int ProductionOverflow { get; set; }

    /// <summary>
    /// Maps cities by id to a set of the buildings they received (by nation equivalent name)
    /// Source: <see cref="UniqueType.GainFreeBuildings"/>
    /// </summary>
    [JsonPropertyName("freeBuildingsProvidedFromThisCity")]
    [JsonInclude]
    public JavaHashMap<string, JavaHashSet<string>> FreeBuildingsProvidedFromThisCity { get; private set; } = new();
    #endregion

    #region pure functions
    public virtual CityConstructions Clone()
    {
        var toReturn = new CityConstructions();
        foreach (var building in BuiltBuildings) toReturn.BuiltBuildings.Add(building);
        toReturn.InProgressConstructions.PutAll(InProgressConstructions);
        toReturn.CurrentConstructionIsUserSet = CurrentConstructionIsUserSet;
        toReturn.ConstructionQueue = new List<string>(ConstructionQueue);
        toReturn.ProductionOverflow = ProductionOverflow;
        foreach (var (key, value) in FreeBuildingsProvidedFromThisCity)
            toReturn.FreeBuildingsProvidedFromThisCity[key] = new JavaHashSet<string>(value);
        return toReturn;
    }

    // Why is one of these called 'buildable' and the other 'constructable'?
    internal IEnumerable<Building> GetBuildableBuildings() => City.GetRuleset().Buildings.Values
        .Where(it => it.IsBuildable(this));

    public IEnumerable<BaseUnit> GetConstructableUnits() => City.GetRuleset().Units.Values
        .Where(it => it.IsBuildable(this));

    /// <returns><see cref="StatTreeNode"/> provided by all built buildings in city</returns>
    public virtual StatTreeNode GetStats()
    {
        var stats = new StatTreeNode();
        foreach (var building in GetBuiltBuildings())
            stats.AddStats(building.GetStats(City), building.Name);
        return stats;
    }

    /// <returns>Maintenance cost of all built buildings</returns>
    public virtual float GetMaintenanceCosts()
    {
        var maintenanceCost = 0f;
        var freeBuildings = City.Civ.CivConstructions.GetFreeBuildingNames(City);

        var buildingMaintenanceUniques = City.GetMatchingUniques(UniqueType.BuildingMaintenance)
            .Where(it => City.MatchesFilter(it.Params[2])).ToList();

        foreach (var building in GetBuiltBuildings().Where(it => !freeBuildings.Contains(it.Name)))
        {
            var maintenanceForThisBuilding = (float)building.Maintenance;
            foreach (var unique in buildingMaintenanceUniques)
                if (building.MatchesFilter(unique.Params[1]))
                    maintenanceForThisBuilding *= unique.Params[0].ToPercent();

            maintenanceCost += maintenanceForThisBuilding;
        }

        return maintenanceCost;
    }

    public virtual IConstruction GetCurrentConstruction() => GetConstruction(CurrentConstructionName());

    public bool IsBuilt(string buildingName) => BuiltBuildings.Contains(buildingName);

    // Note: There was a isEnqueued here functionally identical to isBeingConstructedOrEnqueued,
    // which was calling both isEnqueued and isBeingConstructed - BUT:  currentConstructionName() is just a
    // a wrapper for constructionQueue[0], so that was redundant. Also, isEnqueued was used nowhere,
    // and isBeingConstructed _only_ redundantly as described above.
    // `isEnqueuedForLater` is not optimal code as it can iterate the whole list where checking size
    // and first() would suffice, but the one current use in CityScreenConstructionMenu isn't critical.

    /// <returns><c>true</c> if <paramref name="constructionName"/> is the top queue entry, the one receiving production points</returns>
    public bool IsBeingConstructed(string constructionName) => CurrentConstructionName() == constructionName;
    /// <returns><c>true</c> if <paramref name="constructionName"/> is queued but not the top queue entry</returns>
    public bool IsEnqueuedForLater(string constructionName) => IndexOfName(ConstructionQueue, constructionName) > 0;
    /// <returns><c>true</c> if <paramref name="constructionName"/> is anywhere in the construction queue - <see cref="IsBeingConstructed"/> **or** <see cref="IsEnqueuedForLater"/></returns>
    public virtual bool IsBeingConstructedOrEnqueued(string constructionName) => IndexOfName(ConstructionQueue, constructionName) >= 0;

    private static int IndexOfName(IReadOnlyList<string> list, string name)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == name) return i;
        return -1;
    }

    public bool IsQueueFull() => ConstructionQueue.Count >= QueueMaxSize;

    public bool IsBuildingWonder()
    {
        var currentConstruction = GetCurrentConstruction();
        return currentConstruction is Building { IsWonder: true };
    }

    public bool CanBeHurried()
    {
        var currentConstruction = GetCurrentConstruction();
        return currentConstruction is INonPerpetualConstruction nonPerpetual
            && !((IHasUniques)nonPerpetual).HasUnique(UniqueType.CannotBeHurried);
    }

    /// <summary>If the city is constructing multiple units of the same type, subsequent units will require the full cost</summary>
    public bool IsFirstConstructionOfItsKind(int constructionQueueIndex, string name) =>
        // Simply compare index of first found [name] with given index
        constructionQueueIndex == IndexOfName(ConstructionQueue, name);

    private sealed class NotBuildingOrUnitException(string message) : Exception(message);

    internal IConstruction GetConstruction(string constructionName)
    {
        var gameBasics = City.GetRuleset();
        if (constructionName == "") return PerpetualConstruction.Idle;
        if (gameBasics.Buildings.TryGetValue(constructionName, out var building)) return building;
        if (gameBasics.Units.TryGetValue(constructionName, out var unit)) return unit;
        if (PerpetualConstruction.TryGetPerpetual(constructionName, out var special)) return special;

        throw new NotBuildingOrUnitException($"{constructionName} is not a building or a unit!");
    }

    public virtual IEnumerable<Building> GetBuiltBuildings() => builtBuildingObjects;

    public virtual bool ContainsBuildingOrEquivalent(string buildingNameOrUnique)
    {
        if (containedBuildingFiltersCache.TryGetValue(buildingNameOrUnique, out var cached)) return cached;
        var result = IsBuilt(buildingNameOrUnique)
            || GetBuiltBuildings().Any(it => it.Replaces == buildingNameOrUnique || ((IHasUniques)it).HasTagUnique(buildingNameOrUnique, City.State));
        containedBuildingFiltersCache[buildingNameOrUnique] = result;
        return result;
    }

    public virtual int GetWorkDone(string constructionName) =>
        InProgressConstructions.TryGetValue(constructionName, out var workDone) ? workDone : 0;

    public int GetRemainingWork(string constructionName, bool useStoredProduction = true)
    {
        var constr = GetConstruction(constructionName);
        if (constr is PerpetualConstruction) return 0;
        if (useStoredProduction) return ((INonPerpetualConstruction)constr).GetProductionCost(City.Civ, City) - GetWorkDone(constructionName);
        return ((INonPerpetualConstruction)constr).GetProductionCost(City.Civ, City);
    }

    public int TurnsToConstruction(string constructionName, bool useStoredProduction = true)
    {
        var workLeft = GetRemainingWork(constructionName, useStoredProduction);
        if (workLeft <= 0) // This most often happens when a production is more than finished in a multiplayer game while its not your turn
            return 0; // So we finish it at the start of the next turn. This could technically also happen when we lower production costs during our turn,
        // but distinguishing those two cases is difficult, and the second one is much rarer than the first
        if (workLeft <= ProductionOverflow) // if we already have stored up enough production to finish it directly
            return 1; // we'll finish this next turn

        return KotlinMath.DoubleToInt(Math.Ceiling((workLeft - ProductionOverflow) / (double)ProductionForConstruction(constructionName)));
    }

    public int ProductionForConstruction(string constructionName)
    {
        Stats cityStatsForConstruction;
        if (CurrentConstructionName() == constructionName) cityStatsForConstruction = City.CityStats.CurrentCityStats;
        else
        {
            // The ol' Switcharoo - what would our stats be if that was our current construction?
            // Since this is only ever used for UI purposes, I feel fine with having it be a bit inefficient
            // and recalculating the entire city stats
            // We don't want to change our current construction queue - what if we have an empty queue,
            //  this can affect the city if we run it on another thread like in ConstructionsTable -
            // So we run the numbers for the other construction
            // ALSO apparently if we run on the actual cityStats from another thread,
            //   we get all sorts of fun concurrency problems when accessing various parts of the cityStats.
            // SO, we create an entirely new CityStats and iterate there - problem solve!
            var cityStats = new CityStats(City)
            {
                StatsFromTiles = City.CityStats.StatsFromTiles, // take as-is
            };
            var construction = City.CityConstructions.GetConstruction(constructionName);
            cityStats.Update(construction, updateTileStats: false, updateCivStats: false);
            cityStatsForConstruction = cityStats.CurrentCityStats;
        }

        return KotlinMath.RoundToInt(cityStatsForConstruction.Production);
    }

    public virtual Building? CheapestStatBuilding(Stat stat)
    {
        // Kotlin minByOrNull: the first element with the smallest key wins
        Building? best = null;
        foreach (var it in City.GetRuleset().Buildings.Values)
        {
            if (it.IsAnyWonder() || !it.IsStatRelated(stat, City)
                || !(it.IsBuildable(this) || IsBeingConstructedOrEnqueued(it.Name))) continue;
            if (best == null || it.Cost < best.Cost) best = it;
        }
        return best;
    }
    #endregion

    #region state changing functions
    public virtual void SetTransients()
    {
        builtBuildingObjects = BuiltBuildings.Select(it =>
            City.GetRuleset().Buildings.GetValueOrDefault(it)
                ?? throw new InvalidOperationException($"Building {it} is not found!")).ToList();
        UpdateUniques(true);
    }

    public virtual void AddProductionPoints(int productionToAdd)
    {
        var constructionName = CurrentConstructionName();
        var construction = GetConstruction(constructionName);
        if (construction is PerpetualConstruction)
        {
            ProductionOverflow += productionToAdd;
            return;
        }
        if (!InProgressConstructions.ContainsKey(constructionName))
            InProgressConstructions[constructionName] = 0;
        InProgressConstructions[constructionName] = InProgressConstructions[constructionName] + productionToAdd;
    }

    public void ConstructIfEnough()
    {
        ValidateConstructionQueue();

        // Update InProgressConstructions for any available refunds
        ValidateInProgressConstructions();

        var constructionName = CurrentConstructionName();
        var construction = GetConstruction(constructionName);
        if (construction is PerpetualConstruction) ChooseNextConstruction(); // check every turn if we could be doing something better, because this doesn't end by itself
        else
        {
            var nonPerpetual = (INonPerpetualConstruction)construction;
            var productionCost = nonPerpetual.GetProductionCost(City.Civ, City);
            if (InProgressConstructions.ContainsKey(constructionName)
                && InProgressConstructions[constructionName] >= productionCost)
            {
                var potentialOverflow = InProgressConstructions[constructionName] - productionCost;
                if (CompleteConstruction(nonPerpetual))
                {
                    // See the URL below for explanation for this cap
                    // https://forums.civfanatics.com/threads/hammer-overflow.419352/
                    var maxOverflow = Math.Max(productionCost, KotlinMath.RoundToInt(City.CityStats.CurrentCityStats.Production));
                    ProductionOverflow = Math.Min(maxOverflow, potentialOverflow);
                }
                else
                {
                    City.Civ.AddNotification($"No space available to place [{construction.Name}] near [{City.Name}]",
                        City.Location, NotificationCategory.Production, construction.Name);
                }
                City.Civ.CivConstructions.BuiltItemsWithIncreasingCost[construction.Name] += 1;
            }
        }
    }

    public void EndTurn(Stats cityStats)
    {
        ValidateConstructionQueue();
        ValidateInProgressConstructions();

        if (GetConstruction(CurrentConstructionName()) is PerpetualConstruction.LeagueProcess process)
        {
            // B017: the production of the turn goes to the World Congress project, not to the city
            process.Contribute(City, Math.Max(0, KotlinMath.RoundToInt(cityStats.Production) + ProductionOverflow));
            ProductionOverflow = 0;
        }
        else if (GetConstruction(CurrentConstructionName()) is not PerpetualConstruction)
        {
            if (GetWorkDone(CurrentConstructionName()) == 0)
            {
                ConstructionBegun(GetConstruction(CurrentConstructionName()));
            }
            AddProductionPoints(KotlinMath.RoundToInt(cityStats.Production) + ProductionOverflow);
            ProductionOverflow = 0;
        }
    }

    private void ValidateConstructionQueue()
    {
        var queueSnapshot = ConstructionQueue.ToList();
        // Build into a new list and swap it in at the end, so concurrent readers never see an empty queue
        var validatedQueue = new List<string>(QueueMaxSize);

        foreach (var constructionName in queueSnapshot)
        {
            var construction = GetConstruction(constructionName);
            // First construction will be built next turn, we need to make sure it has the correct resources
            if (validatedQueue.Count == 0 && GetWorkDone(constructionName) == 0)
            {
                var stockpileCosts = construction.GetStockpiledResourceRequirements(City.State);
                var civResources = City.Civ.GetCivResourcesByName();

                if (stockpileCosts.Any(entry => !civResources.TryGetValue(entry.Key, out var available) || entry.Value > available))
                {
                    if (construction is Building building)
                        RemoveImprovementForBuilding(building);
                    continue; // Removes this construction from the queue
                }
            }
            if (construction.IsBuildable(this))
                validatedQueue.Add(constructionName);
            else if (construction is Building notBuildable)
                RemoveImprovementForBuilding(notBuildable);
        }
        ConstructionQueue = validatedQueue;
        ChooseNextConstruction();
        ValidateCreatesOneImprovementMarkers();
    }

    /// <summary>Remove orphaned <see cref="UniqueType.CreatesOneImprovement"/> markers whose queue entry was removed elsewhere.</summary>
    private void ValidateCreatesOneImprovementMarkers()
    {
        var markedTiles = City.GetTiles()
            .Where(it => it.IsMarkedForCreatesOneImprovement())
            .ToList();
        if (markedTiles.Count == 0) return;

        var improvementsInQueue = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ConstructionQueue)
        {
            if (GetConstruction(name) is not Building building) continue;
            var improvement = building.GetImprovementToCreate(City.GetRuleset(), City.Civ);
            if (improvement != null) improvementsInQueue.Add(improvement.Name);
        }

        foreach (var tile in markedTiles)
        {
            var improvementInProgress = tile.ImprovementInProgress
                ?? throw new InvalidOperationException(
                    $"Tile {tile.Position} is marked for {nameof(UniqueType.CreatesOneImprovement)} without an improvement in progress");
            if (!improvementsInQueue.Contains(improvementInProgress))
                tile.ImprovementFunctions.RemoveCreatesOneImprovementMarker(removeConstruction: false);
        }
    }

    public virtual void ValidateInProgressConstructions()
    {
        // remove obsolete stuff from in progress constructions - happens often and leaves clutter in memory and save files
        // should have little visible consequences - any accumulated points that may be reused later should stay (nukes when manhattan project city lost, nat wonder when conquered an empty city...), all other points should be refunded
        // Should at least be called before each turn - if another civ completes a wonder after our previous turn, we should get the refund this turn
        var inProgressSnapshot = InProgressConstructions.Keys.Where(it => it != CurrentConstructionName()).ToList();
        foreach (var constructionName in inProgressSnapshot)
        {
            var construction = GetConstruction(constructionName);
            // Perpetual constructions should always still be valid (I hope)
            if (construction is PerpetualConstruction) continue;

            var rejectionReasons = ((INonPerpetualConstruction)construction).GetRejectionReasons(this).ToList();

            if (!rejectionReasons.Any(it => it.HasAReasonToBeRemovedFromQueue())) continue;

            var workDone = GetWorkDone(constructionName);
            if (construction is Building building)
            {
                // Production put into wonders gets refunded
                if (building.IsWonder && workDone != 0)
                {
                    City.Civ.AddGold(workDone);
                    City.Civ.AddNotification(
                        $"Excess production for [{constructionName}] converted to [{workDone}] gold",
                        City.Location,
                        NotificationCategory.Production,
                        NotificationIcon.Gold, $"BuildingIcons/{constructionName}");
                }
                RemoveImprovementForBuilding(building);
            }
            else if (construction is BaseUnit baseUnit)
            {
                // Production put into upgradable units gets put into upgraded version
                // Kotlin minByOrNull: the first element with the smallest key wins
                BaseUnit? cheapestUpgradeUnit = null;
                foreach (var upgradeUnit in baseUnit.GetRulesetUpgradeUnits(City.State))
                {
                    var equivalent = City.Civ.GetEquivalentUnit(upgradeUnit);
                    if (!equivalent.IsBuildable(this)) continue;
                    if (cheapestUpgradeUnit == null || equivalent.Cost < cheapestUpgradeUnit.Cost) cheapestUpgradeUnit = equivalent;
                }
                if (rejectionReasons.All(it => it.Type == RejectionReasonType.Obsoleted) && cheapestUpgradeUnit != null)
                {
                    InProgressConstructions[cheapestUpgradeUnit.Name] = (InProgressConstructions.TryGetValue(cheapestUpgradeUnit.Name, out var existing) ? existing : 0) + workDone;
                }
            }
            InProgressConstructions.Remove(constructionName);
        }
    }

    private void RemoveImprovementForBuilding(Building building)
    {
        var improvementToCreate = building.GetImprovementToCreate(City.GetRuleset(), City.Civ);
        if (improvementToCreate == null) return;
        var tile = City.GetTiles().FirstOrDefault(it => it.IsMarkedForCreatesOneImprovement(improvementToCreate.Name));
        tile?.ImprovementFunctions.RemoveCreatesOneImprovementMarker(removeConstruction: false);
    }

    private void ConstructionBegun(IConstruction construction)
    {
        foreach (var (resourceName, amount) in construction.GetStockpiledResourceRequirements(City.State))
        {
            if (!City.Civ.GameInfo.Ruleset.TileResources.TryGetValue(resourceName, out var resource)) continue;
            City.GainStockpiledResource(resource, -amount);
        }

        if (construction is not INonPerpetualConstruction nonPerpetual) return;
        if (!((IHasUniques)nonPerpetual).HasUnique(UniqueType.TriggersAlertOnStart)) return;
        var icon = construction is Building ? $"BuildingIcons/{construction.Name}" : $"UnitIcons/{construction.Name}";
        foreach (var otherCiv in City.Civ.GameInfo.Civilizations)
        {
            if (otherCiv == City.Civ) continue;
            if (otherCiv.HasExplored(City.GetCenterTile()))
                otherCiv.AddNotification($"The city of [{City.Name}] has started constructing [{construction.Name}]!",
                    City.Location, NotificationCategory.General, NotificationIcon.Construction, icon);
            else if (otherCiv.Knows(City.Civ))
                otherCiv.AddNotification($"[{City.Civ.CivName}] has started constructing [{construction.Name}]!",
                    NotificationCategory.General, NotificationIcon.Construction, icon);
            else
                otherCiv.AddNotification($"[An unknown civilization] has started constructing [{construction.Name}]!",
                    NotificationCategory.General, NotificationIcon.Construction, icon);
        }
    }

    /// <summary>Returns false if we tried to construct a unit but it has nowhere to go</summary>
    public virtual bool CompleteConstruction(INonPerpetualConstruction construction)
    {
        MapUnit? unit = null;
        if (construction is Building building) building.Construct(this);
        else if (construction is BaseUnit baseUnit)
        {
            unit = baseUnit.Construct(this, null);
            if (unit == null) return false; // unable to place unit

            // check if it's true that we should load saved promotion for the unitType,
            // Then check if the player want to rebuild the unit the saved promotion,
            // and do a null check.
            // and finally check if the current unit has enough XP.
            var possiblePromotions = new HashSet<string>(StringComparer.Ordinal);

            // to get promotion in order from nodes
            var promotionTreeOrder = new LinkedHashSet<string>();

            // Added all the possible Prmotion that the unit can be promoted,
            // to avoid edge case where upgrading a scout to spearman
            // whould give the rest of the spearman the "ignore terrain cost" promotion
            // when built with auto promotion.
            foreach (var promotion in new PromotionTree(unit).PossiblePromotions) possiblePromotions.Add(promotion.Name);
            foreach (var roots in new PromotionTree(unit).AllNodes()) promotionTreeOrder.Add(roots.Promotion.Name);

            City.UnitToPromotions.TryGetValue(unit.Name, out var savedPromotion);

            if (City.UnitShouldUseSavedPromotion.TryGetValue(unit.Name, out var shouldUse) && shouldUse
                && savedPromotion != null && unit.Promotions.XP >= savedPromotion.XP)
            {
                // this variable is the filted promotion from savedPrmotion to only get promotion that are possible for this unit.
                var possiblePromotionFilted = savedPromotion.Promotions.Where(it => possiblePromotions.Contains(it)).ToList();
                // sort in order to avoid getting Accuracy III before Accuracy I
                foreach (var promotions in promotionTreeOrder)
                {
                    // check if here is enough XP for the next promotion
                    if (unit.Promotions.XP - unit.Promotions.XpForNextPromotion() >= 0)
                    {
                        if (possiblePromotionFilted.Contains(promotions)) unit.Promotions.AddPromotion(promotions);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        if (InProgressConstructions.ContainsKey(construction.Name))
            InProgressConstructions.Remove(construction.Name);
        if (construction.Name == CurrentConstructionName())
            RemoveCurrentConstruction();

        ValidateConstructionQueue(); // if we've built e.g. the Great Lighthouse, then Lighthouse is no longer relevant in the queue

        var rulesetObject = (IRulesetObject)construction; // Always OK for INonPerpetualConstruction, but compiler doesn't know

        var buildingIcon = $"BuildingIcons/{construction.Name}";
        var pediaAction = new CivilopediaAction(rulesetObject.MakeLink());
        NotificationAction locationAction = construction is BaseUnit
            ? new MapUnitAction(unit.NotNull().GetTile().Position, unit.Id)
            : new LocationAction(City.Location);
        var locationAndPediaActions = new List<NotificationAction> { locationAction, pediaAction };

        if (construction is Building { IsWonder: true })
        {
            City.Civ.AddPopupAlert(new PopupAlert(AlertType.WonderBuilt, construction.Name));
            foreach (var civ in City.Civ.GameInfo.Civilizations)
            {
                if (civ.HasExplored(City.GetCenterTile()))
                    civ.AddNotification($"[{construction.Name}] has been built in [{City.Name}]",
                        locationAndPediaActions,
                        civ == City.Civ ? NotificationCategory.Production : NotificationCategory.General, buildingIcon);
                else
                    civ.AddNotification($"[{construction.Name}] has been built in a faraway land",
                        pediaAction, NotificationCategory.General, buildingIcon);
            }
        }
        else
        {
            var icon = construction is Building ? buildingIcon : construction.Name; // could be a unit, in which case take the unit name.
            City.Civ.AddNotification(
                $"[{construction.Name}] has been built in [{City.Name}]",
                locationAndPediaActions, NotificationCategory.Production, NotificationIcon.Construction, icon);
        }

        if (((IHasUniques)construction).HasUnique(UniqueType.TriggersAlertOnCompletion, City.State))
        {
            foreach (var otherCiv in City.Civ.GameInfo.Civilizations)
            {
                // No need to notify ourself, since we already got the building notification anyway
                if (otherCiv == City.Civ) continue;
                var completingCivDescription =
                    otherCiv.Knows(City.Civ) ? $"[{City.Civ.CivName}]" : "[An unknown civilization]";
                otherCiv.AddNotification($"{completingCivDescription} has completed [{construction.Name}]!",
                    pediaAction, NotificationCategory.General, NotificationIcon.Construction, buildingIcon);
            }
        }
        return true;
    }

    public void AddBuilding(string buildingName)
    {
        var building = City.GetRuleset().Buildings[buildingName];
        AddBuilding(building);
    }

    public virtual void AddBuilding(Building building,
        // False when creating initial buildings in city - so we don't "waste" a free building on a building we're going to get anyway, from settler buildings
        bool tryAddFreeBuildings = true)
    {
        var buildingName = building.Name;
        var civ = City.Civ;

        if (building.CityHealth > 0)
        {
            // city built a building that increases health so add a portion of this added health that is
            // proportional to the city's current health
            City.Health += KotlinMath.FloatToInt(building.CityHealth * (float)City.Health / City.GetMaxHealth());
        }
        builtBuildingObjects = builtBuildingObjects.WithItem(building);
        BuiltBuildings.Add(buildingName);

        UpdateUniques();

        // Support for UniqueType.CreatesOneImprovement
        ApplyCreateOneImprovement(building);

        TriggerNewBuildingUniques(building);
        City.PlaceFreeGreatWorks(building); // B007

        if (((IHasUniques)building).HasUnique(UniqueType.EnemyUnitsSpendExtraMovement))
            civ.Cache.UpdateHasActiveEnemyMovementPenalty();

        // The Korean unique (https://civilization.fandom.com/wiki/Korean_(Civ5)#Strategy) gives the same tech boost as a research agreement
        // (https://civilization.fandom.com/wiki/Diplomacy_(Civ5)#Research_Agreement).
        // We use Vanilla / G&K logic as it is more straightforward, i.e. half of the median cost of our researchable techs.
        // It is unclear whether RA modifiers should apply. For now, they do not.
        void ApplyKoreanUnique()
        {
            if (!building.IsStatRelated(Stat.Science, City)) return;
            if (!civ.HasUnique(UniqueType.TechBoostWhenScientificBuildingsBuiltInCapital)) return;
            if (!City.IsCapital()) return;
            var availableTechCosts = City.GetRuleset().Technologies.Values
                .Where(it => civ.Tech.CanBeResearched(it.Name))
                .Select(it => civ.Tech.CostOfTech(it.Name))
                .OrderBy(it => it)
                .ToList();
            if (availableTechCosts.Count == 0) return;
            var n = availableTechCosts.Count;
            var medianCost =
                n % 2 == 1 ? (float)availableTechCosts[n / 2]
                : (availableTechCosts[n / 2 - 1] + availableTechCosts[n / 2]) / 2f;
            var techBoost = KotlinMath.RoundToInt(0.5f * medianCost);
            civ.Tech.AddScience(techBoost);
        }

        ApplyKoreanUnique();

        var previousHappiness = civ.GetHappiness();
        // can cause civ happiness update: reassignPopulationDeferred -> reassignPopulation -> cityStats.update -> civ.updateHappiness
        City.ReassignPopulationDeferred();
        var newHappiness = civ.GetHappiness();

        // Same check as Civilization.updateStatsForNextTurn - but that triggers *stat calculation* whereas this is for *population assignment*
        if (previousHappiness != newHappiness && City.Civ.GameInfo.Ruleset.AllHappinessLevelsThatAffectUniques
                .Any(it => (newHappiness < it) != (previousHappiness < it)))
            foreach (var otherCity in City.Civ.Cities.Where(it => it != City).ToList())
                otherCity.ReassignPopulationDeferred();

        if (tryAddFreeBuildings)
            City.Civ.CivConstructions.TryAddFreeBuildings();
    }

    public void TriggerNewBuildingUniques(Building building)
    {
        var stateForConditionals = City.State;
        var triggerNotificationText = $"due to constructing [{building.Name}]";

        foreach (var unique in building.UniqueObjects)
        {
            if (unique.HasTriggerConditional() || !unique.ConditionalsApply(stateForConditionals)) continue;
            var repeats = unique.GetUniqueMultiplier(City.State);
            for (var i = 0; i < repeats; i++)
                UniqueTriggerActivation.TriggerUnique(unique, City, triggerNotificationText: triggerNotificationText);
        }

        City.ForEachTriggeredUnique(UniqueType.TriggerUponConstructingBuilding, stateForConditionals,
            it => building.MatchesFilter(it.Params[0], stateForConditionals), true, unique =>
            {
                UniqueTriggerActivation.TriggerUnique(unique, City, triggerNotificationText: triggerNotificationText);
            });

        City.ForEachTriggeredUnique(UniqueType.TriggerUponConstructingBuildingCityFilter, stateForConditionals,
            it => building.MatchesFilter(it.Params[0], stateForConditionals) && City.MatchesFilter(it.Params[1]), true, unique =>
            {
                UniqueTriggerActivation.TriggerUnique(unique, City, triggerNotificationText: triggerNotificationText);
            });
    }

    public void RemoveBuilding(string buildingName)
    {
        var buildingObject = City.GetRuleset().Buildings.GetValueOrDefault(buildingName);
        if (buildingObject != null) RemoveBuilding(buildingObject);
    }

    public virtual void RemoveBuilding(Building building)
    {
        builtBuildingObjects = builtBuildingObjects.WithoutItem(building);
        BuiltBuildings.Remove(building.Name);
        City.ClearGreatWorks(building.Name); // B007

        if (building.HasCreateOneImprovementUnique())
        {
            var improvement = building.GetImprovementToCreate(City.GetRuleset(), City.Civ).NotNull();
            var tileWithImprovementToRemove = City.GetTiles().FirstOrDefault(it => it.TileImprovement == improvement);
            tileWithImprovementToRemove?.RemoveImprovement();
        }

        UpdateUniques();
    }

    private void UpdateUniques(bool onLoadGame = false)
    {
        BuiltBuildingUniqueMap.Clear();
        containedBuildingFiltersCache.Clear();

        foreach (var building in GetBuiltBuildings())
            BuiltBuildingUniqueMap.AddUniques(building.UniqueObjects);
        if (!onLoadGame)
        {
            City.Civ.Cache.UpdateCitiesConnectedToCapital(false); // could be a connecting building, like a harbor
            City.CityStats.Update();
            City.Civ.Cache.UpdateCivResources();
        }
    }

    /// <summary>
    /// Purchase a construction for gold (or another stat)
    /// called from NextTurnAutomation and the City UI
    /// Build / place the new item, deduct cost, and maintain queue.
    /// </summary>
    /// <param name="constructionName">What to buy (needed since buying something not queued is allowed)</param>
    /// <param name="queuePosition">Position in the queue or -1 if not from queue. Note: -1 does not guarantee queue will remain unchanged (validation)</param>
    /// <param name="automatic">Flag whether automation should try to choose what next to build (not coming from UI). Note: settings.autoAssignCityProduction is handled later</param>
    /// <param name="stat">Stat object of the stat with which was paid for the construction</param>
    /// <param name="tile">Supports <see cref="UniqueType.CreatesOneImprovement"/> the tile to place the improvement from that unique on.
    /// Ignored when the <paramref name="constructionName"/> does not have that unique. If null and the building has the unique, a tile is chosen automatically.</param>
    /// <returns>Success (false e.g. unit cannot be placed)</returns>
    public bool PurchaseConstruction(string constructionName, int queuePosition, bool automatic, Stat stat = Stat.Gold, TileClass? tile = null)
    {
        if (GetConstruction(constructionName) is not INonPerpetualConstruction construction) return false;
        return PurchaseConstruction(construction, queuePosition, automatic, stat, tile);
    }

    /// <summary>Purchase a construction for gold (or another stat), see the overload taking the construction name.</summary>
    public bool PurchaseConstruction(INonPerpetualConstruction construction, int queuePosition, bool automatic, Stat stat = Stat.Gold, TileClass? tile = null)
    {
        // Support UniqueType.CreatesOneImprovement: it is active when getImprovementToCreate returns an improvement
        var improvementToPlace = (construction as Building)?.GetImprovementToCreate(City.GetRuleset(), City.Civ);
        if (improvementToPlace != null)
        {
            // If active without a predetermined tile to place the improvement on, automate a tile
            var finalTile = tile ?? GetTileForConstructionImprovementAutomation(improvementToPlace);
            if (finalTile == null) return false; // This was never reached in testing
            if (!TryPlaceCreateOneImprovementMarker(improvementToPlace, finalTile)) return false;
            // postBuildEvent does the rest by calling cityConstructions.applyCreateOneImprovement
        }

        if (construction is Building building) building.Construct(this);
        else if (construction is BaseUnit baseUnit)
        {
            if (baseUnit.Construct(this, stat) == null)
                return false; // nothing built - no pay
        }

        if (!City.Civ.GameInfo.GameParameters.GodMode)
        {
            var constructionCost = construction.GetStatBuyCost(City, stat);
            if (constructionCost == null)
                return false; // We should never end up here anyway, so things have already gone _way_ wrong
            City.AddStat(stat, -1 * constructionCost.Value);

            var conditionalState = City.State;

            if (construction is BaseUnit bought && bought.CostFunctions.IsFaithPurchasableGreatPerson(City, stat)) // BNW (B011)
                City.Civ.CivConstructions.BoughtItemsWithIncreasingPrice.Add(construction.Name, 1);
            else if (City.Civ.GetMatchingUniques(UniqueType.BuyUnitsIncreasingCost, conditionalState)
                    .Concat(City.Civ.GetMatchingUniques(UniqueType.BuyBuildingsIncreasingCost, conditionalState))
                    .Any(it =>
                        (construction is BaseUnit unitConstruction && unitConstruction.MatchesFilter(it.Params[0], conditionalState)
                            || construction is Building buildingConstruction && buildingConstruction.MatchesFilter(it.Params[0], conditionalState))
                        && City.MatchesFilter(it.Params[3])
                        && it.Params[2] == stat.ToString()))
            {
                City.Civ.CivConstructions.BoughtItemsWithIncreasingPrice.Add(construction.Name, 1);
            }

            // Consume stockpiled resources - usually consumed when construction starts, but not when bought
            if (GetWorkDone(construction.Name) == 0) // we didn't pay the resources when we started building
            {
                foreach (var (resourceName, amount) in construction.GetStockpiledResourceRequirements(conditionalState))
                {
                    if (!City.Civ.GameInfo.Ruleset.TileResources.TryGetValue(resourceName, out var resource)) continue;
                    City.GainStockpiledResource(resource, -amount);
                }
            }
        }

        if (queuePosition >= 0 && queuePosition < ConstructionQueue.Count)
            RemoveFromQueue(queuePosition, automatic);
        ValidateConstructionQueue();

        // A purchase should never leave the city idle if we invalidated or emptied the queue
        if (IsQueueEmptyOrIdle()) ChooseNextConstruction();

        return true;
    }

    /// <summary>
    /// This is the *one true test* of "can we buy this construction"
    /// This tests whether the buy button should be _enabled_
    /// </summary>
    public bool IsConstructionPurchaseAllowed(INonPerpetualConstruction construction, Stat stat, int constructionBuyCost)
    {
        if (City.IsPuppet && !City.GetMatchingUniques(UniqueType.MayBuyConstructionsInPuppets).Any()) return false;
        if (City.IsInResistance()) return false;
        if (!construction.IsPurchasable(City.CityConstructions)) return false; // checks via 'rejection reason'
        if (construction is BaseUnit baseUnit && !City.CanPlaceNewUnit(baseUnit)) return false;
        if (!construction.CanBePurchasedWithStat(City, stat)) return false;
        if (City.Civ.GameInfo.GameParameters.GodMode) return true;
        if (constructionBuyCost == 0) return true;
        return City.GetStatReserve(stat) >= constructionBuyCost;
    }

    public bool IsConstructionPurchaseBlockedByUnit(INonPerpetualConstruction construction) =>
        !City.IsPuppet && !City.GetMatchingUniques(UniqueType.MayBuyConstructionsInPuppets).Any() &&
        !City.IsInResistance() &&
        construction.IsPurchasable(City.CityConstructions) &&
        construction is BaseUnit baseUnit && !City.CanPlaceNewUnit(baseUnit);

    private void RemoveCurrentConstruction() => RemoveFromQueue(0, true);
    #endregion
}
