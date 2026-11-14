using System.Text.Json.Serialization;
using Unciv.Logic.Map;
using Unciv.Logic.Map.MapGenerator.Mapregions;
using Unciv.Logic.Map.Tiles;
using Unciv.Logic.Trade;
using Unciv.Models;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;

namespace Unciv.Logic.Civilization;

public partial class Civilization : IMapRegionsCivilization
{
    List<string> IMapRegionsCivilization.GetStartBias(Ruleset ruleset) =>
        ruleset.Nations[CivName].GetStartBias(ruleset, GetGameContextForStartBias()).ToList();

    GameRandom IMapRegionsCivilization.StateBasedRandom(string caller) => State.StateBasedRandom(caller);

    [JsonIgnore]
    public IReadOnlySet<Tile> ViewableTiles { get; set; } = new HashSet<Tile>();

    /// <summary>
    /// For each tile some detector unit of ours can see, the unit filters (<see cref="UniqueType.CanSeeInvisibleUnits"/>)
    /// it could detect there - independent of what (if anything) currently occupies the tile.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<Tile, IReadOnlySet<string>> ViewableInvisibleUnitsTiles { get; set; } =
        new Dictionary<Tile, IReadOnlySet<string>>();

    [JsonIgnore]
    public ResourceSupplyList DetailedCivResources { get; set; } = new();

    [JsonIgnore]
    public ResourceSupplyList SummarizedCivResourceSupply { get; set; } = new();

    [JsonIgnore]
    public ModConstants ModConstants => GameInfo.Ruleset.ModOptions.Constants;

    public int GetCompletedPolicyBranchesCount() => Policies.AdoptedPolicies.Count(Policy.IsBranchCompleteByName);

    public bool HasExplored(Tile tile) => tile.IsExplored((IGameContextCivilization)this);

    private IEnumerable<HexCoord> GetCivTerritory() => Cities.SelectMany(it => it.Tiles);

    public bool CanSeeResource(TileResource? resource)
    {
        if (resource == null) return false;
        return Tech.IsRevealed(resource);
    }

    /// <summary>Note that for stockpiled resources, this gives by how much it grows per turn, not current amount</summary>
    public ResourceSupplyList GetCivResourceSupply() => SummarizedCivResourceSupply;

    /// <summary>
    /// Preserves some origins for resources so we can separate them for trades
    /// Stockpiled uniques cannot be traded currently
    /// </summary>
    public ResourceSupplyList GetPerTurnResourcesWithOriginsForTrade()
    {
        var newResourceSupplyList = new ResourceSupplyList(keepZeroAmounts: true);

        foreach (var resourceSupply in DetailedCivResources)
        {
            if (resourceSupply.Resource.IsStockpiled) continue;
            if (((IHasUniques)resourceSupply.Resource).HasUnique(UniqueType.CannotBeTraded, State)) continue;
            // If we got it from another trade or from a CS, preserve the origin
            if (resourceSupply.IsCityStateOrTradeOrigin())
            {
                newResourceSupplyList.Add(new ResourceSupplyList.ResourceSupply(resourceSupply.Resource, resourceSupply.Origin, resourceSupply.Amount));
                newResourceSupplyList.Add(resourceSupply.Resource, Constants.Tradable, 0); // Still add an empty "tradable" entry so it shows up in the list
            }
            else
                newResourceSupplyList.Add(resourceSupply.Resource, Constants.Tradable, resourceSupply.Amount);
        }
        return newResourceSupplyList;
    }

    public ResourceSupplyList GetStockpiledResourcesForTrade()
    {
        var newResourceSupplyList = new ResourceSupplyList(keepZeroAmounts: false);

        foreach (var resourceSupply in DetailedCivResources)
        {
            var resource = resourceSupply.Resource;
            if (!resource.IsStockpiled) continue;
            var resourceUniques = (IHasUniques)resource;
            if (resourceUniques.HasUnique(UniqueType.CannotBeTraded, State)) continue;
            if (!resourceUniques.HasUnique(UniqueType.AiWillSellAt, State) && !resourceUniques.HasUnique(UniqueType.AiWillBuyAt, State)) continue;

            newResourceSupplyList.Add(resource, Constants.Tradable, resourceSupply.Amount);
        }
        return newResourceSupplyList;
    }

    public bool IsCapitalConnectedToCity(CityObject city) => Cache.CitiesConnectedToCapitalToMediums.ContainsKey(city);

    /// <summary>
    /// Returns a dictionary of ALL resource names, and the amount that the civ has of each
    /// Stockpiled resources return the stockpiled amount
    /// </summary>
    public JavaHashMap<string, int> GetCivResourcesByName()
    {
        var hashMap = new JavaHashMap<string, int>(GameInfo.Ruleset.TileResources.Count);
        foreach (var resource in GameInfo.Ruleset.TileResources.Keys) hashMap[resource] = 0;
        foreach (var entry in GetCivResourceSupply())
            if (!entry.Resource.IsStockpiled)
                hashMap[entry.Resource.Name] = entry.Amount;
        foreach (var (key, value) in ResourceStockpiles)
            hashMap[key] = value;
        return hashMap;
    }

    /// <summary>
    /// Gets the number of resources available to this city
    /// Does not include city-wide resources
    /// Returns 0 for undefined resources
    /// </summary>
    public int GetResourceAmount(string resourceName)
    {
        int stockpileValue = ResourceStockpiles[resourceName];
        if (stockpileValue != 0) return stockpileValue;
        return GetCivResourceSupply().FirstOrDefault(it => !it.Resource.IsStockpiled && it.Resource.Name == resourceName)?.Amount ?? 0;
    }

    /// <summary>
    /// Gets the number of resources available to this city
    /// Does not include city-wide resources
    /// Returns 0 for undefined resources
    /// </summary>
    public int GetResourceAmount(TileResource resource)
    {
        if (resource.IsStockpiled) return ResourceStockpiles[resource.Name];
        return GetCivResourceSupply().FirstOrDefault(it => it.Resource == resource)?.Amount ?? 0;
    }

    /// <summary>
    /// Returns the resource production modifier as a multiplier.
    /// For example: 1.0f means no change, 2.0f results in double production.
    /// </summary>
    public float GetResourceModifier(TileResource resource)
    {
        float finalModifier = 1f;

        ForEachMatchingUnique(UniqueType.PercentResourceProduction, unique =>
        {
            if (resource.MatchesFilter(unique.Params[1]))
                finalModifier += ParseFloat(unique.Params[0]) / 100f;
        });

        return finalModifier;
    }

    private static float ParseFloat(string text) =>
        UniqueParameterTypeExtensions.ToFloatOrNull(text) ?? throw new FormatException($"Invalid float: \"{text}\"");

    public bool HasResource(string resourceName) => GetResourceAmount(resourceName) > 0;
    public bool HasResource(TileResource resource) => GetResourceAmount(resource) > 0;

    public Civilization Clone()
    {
        var toReturn = new Civilization
        {
            Gold = Gold,
            PlayerType = PlayerType,
            PlayerId = PlayerId,
            PlayerMinutesBeforeForceResign = PlayerMinutesBeforeForceResign,
            TotalTurnTimeSeconds = TotalTurnTimeSeconds,
            TurnsPlayedAsHuman = TurnsPlayedAsHuman,
            LastTurnProcessedWithVersion = LastTurnProcessedWithVersion,
            CivName = CivName,
            CivId = CivId,
            Tech = Tech.Clone(),
            Policies = Policies.Clone(),
        };
        toReturn.CivConstructions = CivConstructions.Clone();
        toReturn.CivConstructions.SetTransients(toReturn);
        toReturn.ReligionManager = ReligionManager.Clone();
        toReturn.QuestManager = QuestManager.Clone();
        toReturn.GoldenAges = GoldenAges.Clone();
        toReturn.GreatPeople = GreatPeople.Clone();
        toReturn.RuinsManager = RuinsManager.Clone();
        toReturn.EspionageManager = EspionageManager.Clone();
        toReturn.VictoryManager = VictoryManager.Clone();
        toReturn.allyCivName = allyCivName;
        foreach (var diplomacyManager in Diplomacy.Values.Select(it => it.Clone()).ToList())
            toReturn.Diplomacy[diplomacyManager.OtherCivName] = diplomacyManager;
        toReturn.Proximity.PutAll(Proximity);
        toReturn.Cities = Cities.Select(it => it.Clone()).ToList();
        toReturn.NeutralRoads = [.. NeutralRoads]; // T219: copies, not the source's own collections
        foreach (var construction in DisabledCityConstructions) toReturn.DisabledCityConstructions.Add(construction);
        toReturn.ExploredRegion = ExploredRegion.Clone();
        CopyLastSeenInto(toReturn);
        toReturn.LeaderTitle = LeaderTitle;
        toReturn.Notifications.AddRange(Notifications.Select(it => it.Clone()));
        foreach (var log in NotificationsLogs)
            toReturn.NotificationsLogs.Add(new NotificationsLog(log.Turn) { Notifications = [.. log.Notifications.Select(it => it.Clone())] });
        toReturn.NotificationCountAtStartTurn = NotificationCountAtStartTurn;
        toReturn.CitiesCreated = CitiesCreated;
        toReturn.PopupAlerts.AddRange(PopupAlerts.Select(it => new PopupAlert(it.Type, it.Value)));
        toReturn.TradeRequests.AddRange(TradeRequests.Select(it => new TradeRequest(it.RequestingCiv, it.Trade.Clone())));
        toReturn.NaturalWonders.AddRange(NaturalWonders);
        toReturn.CityStatePersonality = CityStatePersonality;
        toReturn.CityStateResource = CityStateResource;
        toReturn.CityStateUniqueUnit = CityStateUniqueUnit;
        toReturn.FlagsCountdown.PutAll(FlagsCountdown);
        toReturn.TemporaryUniques.AddRange(TemporaryUniques.Select(it => it.Clone()));
        toReturn.HasEverOwnedOriginalCapital = HasEverOwnedOriginalCapital;
        foreach (var impassable in PassableImpassables) toReturn.PassableImpassables.Add(impassable);
        toReturn.NumMinorCivsAttacked = NumMinorCivsAttacked;
        toReturn.TotalCultureForContests = TotalCultureForContests;
        toReturn.TotalFaithForContests = TotalFaithForContests;
        toReturn.AttacksSinceTurnStart = AttacksSinceTurnStart.Select(it => it.Clone()).ToList();
        toReturn.HasMovedAutomatedUnits = HasMovedAutomatedUnits;
        toReturn.StatsHistory = StatsHistory.Clone();
        toReturn.ResourceStockpiles = ResourceStockpiles.Clone();
        return CopyCultureTo(CopyCiv5AiTo(toReturn));
    }

    public List<string> GetPreferredVictoryTypes()
    {
        // A victory this civilization cannot achieve is not worth working towards
        var victoryTypes = VictoryManager.GetAvailableVictories().Select(it => it.Name).ToList();
        if (victoryTypes.Count == 1)
            return [victoryTypes[0]]; // That is the most relevant one
        var victoryType = new List<string> { Nation.PreferredVictoryType, GetPersonality().PreferredVictoryType }
            .Where(it => victoryTypes.Contains(it)).ToList();
        return victoryType.Count != 0 ? victoryType : [Constants.NeutralVictoryType];
    }

    public void UpdateStatsForNextTurn()
    {
        int previousHappiness = Stats.Happiness;
        // Kotlin Iterable<Float>.sum() accumulates in Float
        float happinessSum = 0f;
        foreach (var (_, value) in Stats.GetHappinessBreakdown()) happinessSum += value;
        Stats.Happiness = KotlinMath.RoundToInt(happinessSum);
        if (Stats.Happiness != previousHappiness && GameInfo.Ruleset.AllHappinessLevelsThatAffectUniques.Any(it =>
                Stats.Happiness < it != previousHappiness < it)) // If move from being below them to not, or vice versa
            foreach (var city in Cities) city.CityStats.Update(updateCivStats: false);

        var newStats = new Models.StatTypes.Stats();
        foreach (var statsForTurn in Stats.GetStatMapForNextTurn().Values) newStats.Add(statsForTurn);
        Stats.StatsForNextTurn = Policies.InAnarchy ? Policies.WithoutAnarchyStats(newStats) : newStats; // B014: anarchy stops culture and science
    }

    public int GetHappiness() => Stats.Happiness;
}
