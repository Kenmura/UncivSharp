using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Logic.Civilization.Managers;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.Ruleset.Unit;
using Unciv.Models.Translations;
using Unciv.Presentation.Components.Extensions;
using Unciv.Presentation.Victory;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using EraObject = Unciv.Models.Ruleset.Tech.Era;
using UniqueObject = Unciv.Models.Ruleset.Unique.Unique;

namespace Unciv.Logic.Civilization;

public partial class Civilization
{
    #region pure functions (continued)
    public bool HasUnique(UniqueType uniqueType, GameContext? gameContext = null) =>
        GetMatchingUniques(uniqueType, gameContext).Any();

    /// <summary>Does not return local uniques, only global ones. <see cref="ForEachMatchingUnique(UniqueType, Action{UniqueObject})"/> is faster. If not viable, then this can still be used.</summary>
    public IEnumerable<UniqueObject> GetMatchingUniques(UniqueType uniqueType, GameContext? gameContext = null)
    {
        var context = gameContext ?? State;
        // Kotlin `sequence {}`: lazily evaluated in this order
        foreach (var unique in Nation.UniqueMap.GetMatchingUniques(uniqueType, context)) yield return unique;
        foreach (var city in Cities)
            foreach (var unique in city.GetMatchingUniquesWithNonLocalEffects(uniqueType, context)) yield return unique;
        foreach (var unique in Policies.PolicyUniques.GetMatchingUniques(uniqueType, context)) yield return unique;
        foreach (var unique in Tech.TechUniques.GetMatchingUniques(uniqueType, context)) yield return unique;
        foreach (var unique in TemporaryUniques.GetMatchingTagUniques(uniqueType, context)) yield return unique;
        foreach (var unique in GetEra().UniqueMap.GetMatchingUniques(uniqueType, context)) yield return unique;
        foreach (var unique in CityStateFunctions.GetUniquesProvidedByCityStates(uniqueType, context)) yield return unique;
        if (ReligionManager.Religion != null)
            foreach (var unique in ReligionManager.Religion.FounderBeliefUniqueMap.GetMatchingUniques(uniqueType, context)) yield return unique;

        foreach (var unique in CivResourcesUniqueMap.GetMatchingUniques(uniqueType, context)) yield return unique;
        foreach (var unique in GameInfo.GetGlobalUniques().UniqueMap.GetMatchingUniques(uniqueType, context)) yield return unique;
    }

    public void ForEachMatchingUnique(UniqueType uniqueType, Action<UniqueObject> op) => ForEachMatchingUnique(uniqueType, State, op);

    public void ForEachMatchingUnique(UniqueType uniqueType, GameContext gameContext, Action<UniqueObject> op)
    {
        Nation.UniqueMap.ForEachMatchingUnique(uniqueType, gameContext, op);
        for (int i = 0; i < Cities.Count; i++)
            Cities[i].ForEachMatchingUniqueWithNonLocalEffects(uniqueType, gameContext, op);
        Policies.PolicyUniques.ForEachMatchingUnique(uniqueType, gameContext, op);
        Tech.TechUniques.ForEachMatchingUnique(uniqueType, gameContext, op);
        TemporaryUniques.ForEachMatchingUnique(uniqueType, gameContext, op);
        GetEra().UniqueMap.ForEachMatchingUnique(uniqueType, gameContext, op);
        CityStateFunctions.ForEachUniqueProvidedByCityStates(uniqueType, gameContext, op);
        ReligionManager.Religion?.FounderBeliefUniqueMap.ForEachMatchingUnique(uniqueType, gameContext, op);
        CivResourcesUniqueMap.ForEachMatchingUnique(uniqueType, gameContext, op);
        GameInfo.GetGlobalUniques().UniqueMap.ForEachMatchingUnique(uniqueType, gameContext, op);
    }

    public IEnumerable<UniqueObject> GetTriggeredUniques(UniqueType trigger, GameContext gameContext, Func<UniqueObject, bool>? triggerFilter = null) =>
        GetTriggeredUniques(trigger, gameContext, triggerFilter ?? (_ => true), false);

    public IEnumerable<UniqueObject> GetTriggeredUniques(UniqueType trigger, GameContext gameContext, Func<UniqueObject, bool> triggerFilter, bool ignoreCities)
    {
        // Gathering all uniques into a list first since triggers can add e.g. buildings
        // which contain triggers, causing concurrent modification errors.
        // Cannot use getTriggeredUniques from uniqueMaps since we don't want to check conditionals yet
        var uniques = new List<UniqueObject>();
        uniques.AddRange(Nation.UniqueMap.GetAllUniques());
        if (!ignoreCities)
            foreach (var city in Cities) uniques.AddRange(city.CityConstructions.BuiltBuildingUniqueMap.GetAllUniques());
        if (ReligionManager.Religion != null)
            uniques.AddRange(ReligionManager.Religion.FounderBeliefUniqueMap.GetAllUniques());
        uniques.AddRange(Policies.PolicyUniques.GetAllUniques());
        uniques.AddRange(Tech.TechUniques.GetAllUniques());
        uniques.AddRange(GetEra().UniqueMap.GetAllUniques());
        uniques.AddRange(GameInfo.GetGlobalUniques().UniqueMap.GetAllUniques());
        // Then check conditionals when triggering rather than before triggering
        return uniques
            .Where(it => it.GetModifiers(trigger).Any(triggerFilter) && it.ConditionalsApply(gameContext))
            .SelectMany(it => it.GetMultiplied(gameContext));
    }

    public void ForEachTriggeredUnique(UniqueType trigger, GameContext gameContext, Func<UniqueObject, bool> triggerFilter, Action<UniqueObject> op) =>
        ForEachTriggeredUnique(trigger, gameContext, triggerFilter, false, op);

    public void ForEachTriggeredUnique(UniqueType trigger, GameContext gameContext, Func<UniqueObject, bool> triggerFilter, bool ignoreCities, Action<UniqueObject> op)
    {
        // Gathering all uniques into a list first since triggers can add e.g. buildings
        // which contain triggers, causing concurrent modification errors.
        // Trigger conditions like [trigger] are modifiers on other uniques, not uniques of
        // their own type, so we must scan all uniques (as getTriggeredUniques does) rather
        // than look [trigger] up as if it were a unique's own type.
        bool UniqueFilter(UniqueObject unique) => unique.GetModifiers(trigger).Any(triggerFilter) && unique.ConditionalsApply(gameContext);
        var uniqueList = new List<UniqueObject>(100);
        void ListOp(UniqueObject unique) => uniqueList.Add(unique);
        Nation.UniqueMap.ForEachUnique(UniqueFilter, ListOp);
        if (!ignoreCities)
        {
            foreach (var city in Cities)
                city.CityConstructions.BuiltBuildingUniqueMap.ForEachUnique(UniqueFilter, ListOp);
        }
        ReligionManager.Religion?.FounderBeliefUniqueMap.ForEachUnique(UniqueFilter, ListOp);
        Policies.PolicyUniques.ForEachUnique(UniqueFilter, ListOp);
        Tech.TechUniques.ForEachUnique(UniqueFilter, ListOp);
        GetEra().UniqueMap.ForEachUnique(UniqueFilter, ListOp);
        GameInfo.GetGlobalUniques().UniqueMap.ForEachUnique(UniqueFilter, ListOp);
        // now its safe to do the op, which might mutate the lists
        foreach (var unique in uniqueList) unique.ForEachMultiplied(gameContext, op);
    }

    /// <summary>Implements UniqueParameterType.CivFilter</summary>
    public bool MatchesFilter(string filter) => MatchesFilter(filter, State, true);

    public bool MatchesFilter(string filter, GameContext? state) => MatchesFilter(filter, state, true);

    public bool MatchesFilter(string filter, GameContext? state, bool multiFilter) =>
        multiFilter ? MultiFilter.Filter(filter, (civ: this, state), static (it, s) => s.civ.MatchesSingleFilter(it, s.state)) : MatchesSingleFilter(filter, state);

    public bool MatchesSingleFilter(string filter, GameContext? state)
    {
        // The state's civ is a Civilization (the only implementer of the IGameContextCivilization seam in the engine)
        var otherCiv = state?.CivInfo as Civilization;
        switch (filter)
        {
            case Constants.HumanPlayer: return IsHuman();
            case Constants.AiPlayer: return IsAI();
            case "Open Borders":
                return otherCiv != null && otherCiv.Diplomacy.TryGetValue(CivId, out var borders) && borders.HasOpenBorders;
            case "Friendly":
                return otherCiv != null && (otherCiv.CivId == CivId
                    || (otherCiv.Diplomacy.TryGetValue(CivId, out var friend) && friend.IsRelationshipLevelGE(RelationshipLevel.Friend)));
            case "Hostile": return otherCiv != null && IsAtWarWith(otherCiv);
            case "Known": return otherCiv != null && (ReferenceEquals(otherCiv, this) || Knows(otherCiv));
            default: return Nation.MatchesFilter(filter, state, false);
        }
    }

    public bool MatchesSingleFilter(string filter) => MatchesSingleFilter(filter, State);

    public bool ShouldOpenTechPicker()
    {
        if (!Tech.CanResearchTech()) return false;
        if (Tech.FreeTechs != 0) return true;
        return Tech.CurrentTechnology() == null && Cities.Count != 0;
    }

    public Building GetEquivalentBuilding(string buildingName)
    {
        if (!GameInfo.Ruleset.Buildings.TryGetValue(buildingName, out var building))
            throw new Exception($"No building by the name of {buildingName} exists!");
        return GetEquivalentBuilding(building);
    }

    public Building GetEquivalentBuilding(Building baseBuilding)
    {
        if (baseBuilding.Replaces != null
                && GameInfo.Ruleset.Buildings.ContainsKey(baseBuilding.Replaces))
            return GetEquivalentBuilding(baseBuilding.Replaces);

        foreach (var building in Cache.UniqueBuildings)
            if (building.Replaces == baseBuilding.Name)
                return building;
        return baseBuilding;
    }

    public TileImprovement GetEquivalentTileImprovement(string tileImprovementName)
    {
        if (!GameInfo.Ruleset.TileImprovements.TryGetValue(tileImprovementName, out var tileImprovement))
            throw new UncivShowableException($"Improvement {tileImprovementName} doesn't seem to exist!");
        return GetEquivalentTileImprovement(tileImprovement);
    }

    public TileImprovement GetEquivalentTileImprovement(TileImprovement tileImprovement)
    {
        if (tileImprovement.Replaces != null)
            return GetEquivalentTileImprovement(tileImprovement.Replaces);

        foreach (var improvement in Cache.UniqueImprovements)
            if (improvement.Replaces == tileImprovement.Name)
                return improvement;
        return tileImprovement;
    }

    public BaseUnit GetEquivalentUnit(string baseUnitName)
    {
        if (!GameInfo.Ruleset.Units.TryGetValue(baseUnitName, out var baseUnit))
            throw new UncivShowableException($"Unit {baseUnitName} doesn't seem to exist!");
        return GetEquivalentUnit(baseUnit);
    }

    public BaseUnit GetEquivalentUnit(BaseUnit baseUnit)
    {
        if (baseUnit.Replaces != null && GameInfo.Ruleset.Units.ContainsKey(baseUnit.Replaces))
            return GetEquivalentUnit(baseUnit.Replaces); // Equivalent of unique unit is the equivalent of the replaced unit

        foreach (var unit in Cache.UniqueUnits)
            if (unit.Replaces == baseUnit.Name)
                return unit;
        return baseUnit;
    }

    public Building? CapitalCityIndicator(CityObject? city = null)
    {
        var gameContext = city != null && ReferenceEquals(city.Civ, this) ? city.State
            : city == null ? State
            : new GameContext(civInfo: this, city: city);
        var indicatorBuildings = GameInfo.Ruleset.Buildings.Values
            .Where(it => ((IHasUniques)it).HasUnique(UniqueType.IndicatesCapital, gameContext));

        var civSpecificBuilding = indicatorBuildings.FirstOrDefault(it => it.UniqueTo != null && MatchesFilter(it.UniqueTo, gameContext));
        return civSpecificBuilding ?? indicatorBuildings.FirstOrDefault();
    }

    public override string ToString() => CivId; // for debug

    /// <summary>
    /// Determine loss conditions.
    ///
    /// If the civ has never controlled an original capital, it stays 'alive' as long as it has units (irrespective of non-original-capitals owned)
    /// Otherwise, it stays 'alive' as long as it has cities (irrespective of settlers owned)
    /// </summary>
    public bool IsDefeated()
    {
        if (IsBarbarian || IsSpectator()) return false; // Barbarians and voyeurs can't lose
        if (HasEverOwnedOriginalCapital) return Cities.Count == 0;
        return Units.GetCivUnitsSize() == 0;
    }

    public EraObject GetEra() => Tech.Era;
    public int GetEraNumber() => GetEra().EraNumber;
    public bool IsAtWarWith(Civilization otherCiv) => DiplomacyFunctions.IsAtWarWith(otherCiv);
    public bool IsAtWar() => Diplomacy.Values.Any(it => it.DiplomaticStatus == DiplomaticStatus.War && !it.OtherCiv.IsDefeated());
    public List<Civilization> GetCivsAtWarWith() =>
        Diplomacy.Values.Where(it => it.DiplomaticStatus == DiplomaticStatus.War && !it.OtherCiv.IsDefeated()).Select(it => it.OtherCiv).ToList();

    /// <summary>
    /// Returns a civilization caption suitable for greetings including player type info:
    /// Like "Milan" if the nation is a city state, "Caesar of Rome" otherwise, with an added
    /// " (AI)", " (Human - Hotseat)", or " (Human - Multiplayer)" if the game is multiplayer.
    /// </summary>
    public string GetLeaderDisplayName()
    {
        bool severalHumans = GameInfo.Civilizations.Count(it => it.PlayerType == PlayerType.Human) > 1;
        bool online = GameInfo.GameParameters.IsOnlineMultiplayer;
        string suffix;
        if (!online && !severalHumans) suffix = ""; // offline single player will know everybody else is AI
        else if (PlayerType == PlayerType.AI) suffix = $" ({"AI".Tr()})";
        else if (online) suffix = $" ({"Human".Tr()} - {"Multiplayer".Tr()})";
        else suffix = $" ({"Human".Tr()} - {"Hotseat".Tr()})";
        return Nation.GetLeaderDisplayName(LeaderTitle).Tr(hideIcons: true) + suffix;
    }

    public int GetStatForRanking(RankingType category)
    {
        if (IsDefeated()) return 0;
        switch (category)
        {
            case RankingType.Score: return KotlinMath.DoubleToInt(CalculateTotalScore());
            case RankingType.Population: return Cities.Sum(it => it.Population.Population);
            case RankingType.Growth: return KotlinMath.RoundToInt(Stats.StatsForNextTurn.Food);
            case RankingType.Production: return KotlinMath.RoundToInt(Stats.StatsForNextTurn.Production);
            case RankingType.Gold: return Gold;
            case RankingType.Territory: return Cities.Sum(it => it.Tiles.Count);
            case RankingType.Force: return GetMilitaryMight();
            case RankingType.Happiness: return GetHappiness();
            case RankingType.Technologies: return Tech.ResearchedTechnologies.Count;
            case RankingType.Culture: return Policies.AdoptedPolicies.Count(it => !Policy.IsBranchCompleteByName(it));

            // Non vanilla ranking types
            case RankingType.TilesExplored: return GameInfo.TileMap.Values.Count(it => it.IsExplored(this));
            default: throw new ArgumentOutOfRangeException(nameof(category));
        }
    }

    private int GetMilitaryMight()
    {
        if (cachedMilitaryMight < 0)
            cachedMilitaryMight = CalculateMilitaryMight();
        return cachedMilitaryMight;
    }

    public void ResetMilitaryMightCache() { cachedMilitaryMight = -1; }

    private int CalculateMilitaryMight()
    {
        int sum = 1; // minimum value, so we never end up with 0
        foreach (var unit in Units.GetCivUnits())
        {
            sum += unit.BaseUnit.IsWaterUnit
                ? unit.GetForceEvaluation() / 2 // Really don't value water units highly
                : unit.GetForceEvaluation();
        }
        float goldBonus = ((float)Math.Sqrt(Math.Max(0f, (float)Gold))).ToPercent(); // 2f if gold == 10000
        sum = KotlinMath.FloatToInt(sum * Math.Min(goldBonus, 2f)); // 2f is max bonus
        return sum;
    }

    public bool IsMinorCivAggressor() => NumMinorCivsAttacked >= 2;
    public bool IsMinorCivWarmonger() => NumMinorCivsAttacked >= 4;

    public bool IsLongCountActive()
    {
        var unique = GetMatchingUniques(UniqueType.MayanGainGreatPerson).FirstOrDefault();
        if (unique == null) return false;
        return Tech.IsResearched(unique.Params[1]);
    }
    public bool IsLongCountDisplay() => HasLongCountDisplayUnique && IsLongCountActive();

    /// <summary>Kotlin <c>HashMap&lt;String, Double&gt;</c>: java.util.HashMap order, which the double sum in <see cref="CalculateTotalScore"/> depends on.</summary>
    public JavaHashMap<string, double> CalculateScoreBreakdown()
    {
        var scoreBreakdown = new JavaHashMap<string, double>();
        // 1276 is the number of tiles in a medium sized map. The original uses 4160 for this,
        // but they have bigger maps
        double mapSizeModifier = 1276 / (double)GameInfo.TileMap.MapParameters.NumberOfTiles();
        if (mapSizeModifier > 1)
            mapSizeModifier = (mapSizeModifier - 1) / 3 + 1;

        var modConstants = GameInfo.Ruleset.ModOptions.Constants;
        scoreBreakdown["Cities"] = Cities.Count * 10 * mapSizeModifier;
        scoreBreakdown["Population"] = Cities.Sum(it => it.Population.Population) * modConstants.ScoreFromPopulation * mapSizeModifier;
        scoreBreakdown["Tiles"] = Cities.Sum(city => city.GetTiles().Count(it => !it.IsWater)) * 1 * mapSizeModifier;
        scoreBreakdown["Wonders"] = modConstants.ScoreFromWonders * (double)Cities
            .Sum(city => city.CityConstructions.GetBuiltBuildings().Count(it => it.IsWonder));
        scoreBreakdown["Technologies"] = Tech.TechsResearched.Count * 4.0;
        scoreBreakdown["Future Tech"] = Tech.RepeatingTechsResearched * 10.0;
        AddBnwScore(scoreBreakdown, modConstants);

        return scoreBreakdown;
    }

    public double CalculateTotalScore()
    {
        // Kotlin Iterable<Double>.sum() accumulates in Double, in map order
        double sum = 0.0;
        foreach (var value in CalculateScoreBreakdown().Values) sum += value;
        return sum;
    }
    #endregion

}
