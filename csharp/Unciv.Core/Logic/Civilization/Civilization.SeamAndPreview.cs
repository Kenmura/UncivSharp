using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Logic.Civilization.Managers;
using Unciv.Logic.Map;
using Unciv.Logic.Map.MapUnits;
using Unciv.Logic.Map.Tiles;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.Ruleset.Unit;
using Unciv.Models.StatTypes;
using Unciv.Utils;
using UniqueObject = Unciv.Models.Ruleset.Unique.Unique;

namespace Unciv.Logic.Civilization;

public partial class Civilization
{
    #region Seam members (IGameContextCivilization, IConstructionCivilization, IGameProgressCivilization, ILocalUniqueCacheCivilization)
    IGameContextGameInfo? IGameContextCivilization.GameInfo => gameInfo;
    IConstructionGameInfo IConstructionCivilization.GameInfo => GameInfo;
    IReadOnlyList<IGameContextCity> IGameContextCivilization.Cities => Cities;
    string ILocalUniqueCacheCivilization.CivName => CivName;
    int IGameContextCivilization.Happiness => GetHappiness();
    bool IGameContextCivilization.IsTileViewable(Tile tile) => ViewableTiles.Contains(tile);
    bool IGameContextCivilization.Knows(IGameContextCivilization otherCiv) => Knows((Civilization)otherCiv);
    bool IGameContextCivilization.IsAtWarWith(IGameContextCivilization otherCiv) => IsAtWarWith((Civilization)otherCiv);
    bool IGameContextCivilization.IsConsideredFriendlyTerritoryOf(IGameContextCivilization otherCiv) =>
        GetDiplomacyManager((Civilization)otherCiv).NotNull().IsConsideredFriendlyTerritory();
    bool IGameContextCivilization.CanPassThroughTiles(IGameContextCivilization otherCiv) => DiplomacyFunctions.CanPassThroughTiles((Civilization)otherCiv);
    bool IGameContextCivilization.ForestsAndJunglesAreRoads => Nation.ForestsAndJunglesAreRoads;
    ISet<HexCoord> IGameContextCivilization.NeutralRoads => NeutralRoads;
    void IGameContextCivilization.CheckExploredRegionTilePosition(HexCoord tilePosition, HexCoord? explorerPosition) =>
        ExploredRegion.CheckTilePosition(tilePosition, explorerPosition);
    IGameContextUnit IGameContextCivilization.NewMapUnit(BaseUnit baseUnit, int? unitId) => baseUnit.NewMapUnit(this, unitId);
    void IGameContextCivilization.RemoveUnit(IGameContextUnit unit, bool updateCivInfo) => Units.RemoveUnit((MapUnit)unit, updateCivInfo);
    void IGameContextCivilization.UpdateCivResources() => Cache.UpdateCivResources();
    IGameContextCity? IGameContextCivilization.GetCapital() => GetCapital();
    IEnumerable<IGameContextCivilization> IGameContextCivilization.GetKnownCivs() => GetKnownCivs();
    bool IGameContextCivilization.IsTechResearched(string techName) => Tech.IsResearched(techName);
    bool IGameContextCivilization.AnyResearchedTechnologyMatchesFilter(string filter) => Tech.ResearchedTechnologies.Any(it => it.MatchesFilter(filter));
    bool IGameContextCivilization.CurrentTechnologyMatchesFilter(string filter) => Tech.CurrentTechnology()?.MatchesFilter(filter) == true;
    IEnumerable<string> IGameContextCivilization.TechsResearched => Tech.TechsResearched;
    bool IGameContextCivilization.IsPolicyAdopted(string policyName) => Policies.IsAdopted(policyName);
    int IGameContextCivilization.CountAdoptedPoliciesMatching(string filter, GameContext state) => Policies.GetAdoptedPoliciesMatching(filter, state).Count();
    IGameContextReligion? IGameContextCivilization.Religion => ReligionManager.Religion;
    Managers.ReligionState IGameContextCivilization.ReligionState => ReligionManager.ReligionState;
    int IGameContextCivilization.GreatProphetsEarned() => ReligionManager.GreatProphetsEarned();
    bool IGameContextCivilization.IsGoldenAge() => GoldenAges.IsGoldenAge();
    int IGameContextCivilization.GetCivUnitsSize() => Units.GetCivUnitsSize();
    IEnumerable<IGameContextUnit> IGameContextCivilization.GetCivUnits() => Units.GetCivUnits();
    IEnumerable<float> IGameContextCivilization.GetStatMapForNextTurnValues(Stat stat) => Stats.GetStatMapForNextTurn().Values.Select(it => it[stat]);
    int IGameContextCivilization.GetCivResourceSupplyAmount(string resourceName) => GetCivResourceSupply().SumBy(resourceName);
    IEnumerable<TileImprovement> IGameContextCivilization.UniqueImprovements => Cache.UniqueImprovements;
    float IGameContextCivilization.GetGameProgressModifier(UniqueObject unique) => unique.GetGameProgressModifier(this);
    int IConstructionCivilization.GetBuiltItemsWithIncreasingCost(string name) => CivConstructions.GetBuiltItemsWithIncreasingCost(name);
    int IConstructionCivilization.GetBoughtItemsWithIncreasingPrice(string name) => CivConstructions.GetBoughtItemsWithIncreasingPrice(name);
    int IGameProgressCivilization.ResearchedTechnologiesCount => Tech.ResearchedTechnologies.Count;
    int IGameProgressCivilization.RulesetTechnologiesCount => GameInfo.Ruleset.Technologies.Count;
    int IGameProgressCivilization.AdoptedPoliciesCount => Policies.AdoptedPolicies.Count;
    int IGameProgressCivilization.RulesetPoliciesCount => GameInfo.Ruleset.Policies.Count;
    #endregion
}

/// <summary>Reduced variant of CivilizationInfo used for load preview.</summary>
public class CivilizationInfoPreview()
{
    public string CivName { get; set; } = "";
    public string CivId { get; set; } = "";
    public PlayerType PlayerType { get; set; } = PlayerType.AI;
    public string PlayerId { get; set; } = "";
    public int PlayerMinutesBeforeForceResign { get; set; } = 60 * 24 * 3;
    public int TotalTurnTimeSeconds { get; set; }
    public int TurnsPlayedAsHuman { get; set; }
    public bool? IsAlive { get; set; }

    public bool IsPlayerCivilization() => PlayerType == PlayerType.Human;

    /// <summary>Converts a CivilizationInfo object (can be uninitialized) into a CivilizationInfoPreview object.</summary>
    public CivilizationInfoPreview(Civilization civilization) : this()
    {
        CivName = civilization.CivName;
        CivId = civilization.CivId;
        PlayerType = civilization.PlayerType;
        PlayerId = civilization.PlayerId;
        PlayerMinutesBeforeForceResign = civilization.PlayerMinutesBeforeForceResign;
        TotalTurnTimeSeconds = civilization.TotalTurnTimeSeconds;
        TurnsPlayedAsHuman = civilization.TurnsPlayedAsHuman;
        IsAlive = civilization.IsAlive();
    }
}

public enum CivFlags
{
    CityStateGreatPersonGift,
    TurnsTillCityStateElection,
    TurnsTillNextDiplomaticVote,
    ShowDiplomaticVotingResults,
    ShouldResetDiplomaticVotes,
    RecentlyBullied,
    TurnsTillCallForBarbHelp,
    RevoltSpawning,
    CityRevolt,
}
