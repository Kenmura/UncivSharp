using System.Globalization;
using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Map;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tech;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.Ruleset.Unit;
using Unciv.Models.Translations;
using Unciv.Presentation.Components.Extensions;
using Unciv.Utils;
using EraObject = Unciv.Models.Ruleset.Tech.Era;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;

namespace Unciv.Logic.Civilization.Managers;

// Ported from core/src/com/unciv/logic/civilization/managers/TechManager.kt (T056).
// Stripped: the purity annotations.
// Deviations:
// - Kotlin has both `var allTechsAreResearched` and `fun allTechsAreResearched()`, and both `var scienceFromResearchAgreements` and the
//   private `fun scienceFromResearchAgreements()`; C# cannot overload a member name by kind. The flag is the private field
//   allTechsAreResearched read by AllTechsAreResearched(); the private function is GetScienceFromResearchAgreements().
// - Kotlin HashSet/HashMap fields keep java.util iteration order (JavaHashSet/JavaHashMap).
// - Fonts.infinity (a UI class) is inlined as the constant InfinityChar.

public partial class TechManager : IIsPartOfGameInfoSerialization
{
    /// <summary>Fonts.infinity, inlined because Fonts is a UI class.</summary>
    private const char InfinityChar = '∞';

    [JsonIgnore]
    public EraObject Era { get; set; } = new();

    private Civilization? civInfo;
    /// <summary>Kotlin <c>@Transient lateinit var</c>, set by <see cref="SetTransients"/>: reading before assignment throws.</summary>
    [JsonIgnore]
    public Civilization CivInfo
    {
        get => civInfo ?? throw new InvalidOperationException("lateinit property civInfo has not been initialized");
        set => civInfo = value;
    }

    /// <summary>This is the Transient list of Technologies</summary>
    [JsonIgnore]
    public List<Technology> ResearchedTechnologies { get; set; } = [];

    [JsonIgnore]
    internal UniqueMap TechUniques { get; set; } = new();

    // MapUnit.canPassThrough is the most called function in the game, and having these extremely specific booleans is one way of improving the time cost
    [JsonIgnore]
    public bool UnitsCanEmbark { get; set; }
    [JsonIgnore]
    public bool EmbarkedUnitsCanEnterOcean { get; set; }
    [JsonIgnore]
    public bool AllUnitsCanEnterOcean { get; set; }
    [JsonIgnore]
    public bool SpecificUnitsCanEnterOcean { get; set; }

    // UnitMovementAlgorithms.getMovementCostBetweenAdjacentTiles is a close second =)
    [JsonIgnore]
    public float MovementSpeedOnRoads { get; set; } = 1f;
    [JsonIgnore]
    public bool RoadsConnectAcrossRivers { get; set; }
    private bool allTechsAreResearched;

    [JsonPropertyName("freeTechs")]
    public int FreeTechs { get; set; }
    // For calculating score
    [JsonPropertyName("repeatingTechsResearched")]
    public int RepeatingTechsResearched { get; set; }

    /// <summary>For calculating Great Scientist yields - see https://civilization.fandom.com/wiki/Great_Scientist_(Civ5)</summary>
    [JsonPropertyName("scienceOfLast8Turns")]
    public int[] ScienceOfLast8Turns { get; set; } = new int[8];
    [JsonPropertyName("scienceFromResearchAgreements")]
    public int ScienceFromResearchAgreements { get; set; }
    /// <summary>This is the list of strings, which is serialized</summary>
    [JsonPropertyName("techsResearched")]
    public JavaHashSet<string> TechsResearched { get; set; } = new();

    /// <summary>When moving towards a certain tech, the user doesn't have to manually pick every one.</summary>
    [JsonPropertyName("techsToResearch")]
    public List<string> TechsToResearch { get; set; } = [];
    [JsonPropertyName("overflowScience")]
    [JsonInclude]
    private int overflowScience;
    [JsonPropertyName("techsInProgress")]
    public JavaHashMap<string, int> TechsInProgress { get; set; } = new();

    /// <summary>In civ IV, you can auto-convert a certain percentage of gold in cities to science</summary>
    [JsonPropertyName("goldPercentConvertedToScience")]
    public float GoldPercentConvertedToScience { get; set; } = 0.6f;

    #region state-changing functions
    public TechManager Clone()
    {
        var toReturn = new TechManager();
        foreach (var techName in TechsResearched) toReturn.TechsResearched.Add(techName);
        toReturn.FreeTechs = FreeTechs;
        toReturn.RepeatingTechsResearched = RepeatingTechsResearched;
        toReturn.TechsInProgress.PutAll(TechsInProgress);
        toReturn.TechsToResearch.AddRange(TechsToResearch);
        toReturn.ScienceOfLast8Turns = (int[])ScienceOfLast8Turns.Clone();
        toReturn.ScienceFromResearchAgreements = ScienceFromResearchAgreements;
        toReturn.overflowScience = overflowScience;
        toReturn.GoldPercentConvertedToScience = GoldPercentConvertedToScience;
        return toReturn;
    }

    /// <summary>Get total number of techs researched, including repeated future tech</summary>
    public int GetNumberOfTechsResearched() => TechsResearched.Count + RepeatingTechsResearched;

    public int GetOverflowScience() => overflowScience;

    private float GetScienceModifier(string techName) // https://forums.civfanatics.com/threads/the-mechanics-of-overflow-inflation.517970/
    {
        var numberOfCivsResearchedThisTech = CivInfo.GetKnownCivs()
            .Count(it => it.IsMajorCiv() && it.Tech.IsResearched(techName));
        var numberOfCivsRemaining = CivInfo.GameInfo.Civilizations
            .Count(it => it.IsMajorCiv() && !it.IsDefeated());
        return 1 + numberOfCivsResearchedThisTech / (float)numberOfCivsRemaining * 0.3f;
    }

    private Ruleset GetRuleset() => CivInfo.GameInfo.Ruleset;

    public int CostOfTech(string techName)
    {
        var techCost = (float)GetRuleset().Technologies[techName].Cost;
        if (CivInfo.IsHuman())
            techCost *= CivInfo.GetDifficulty().ResearchCostModifier;
        techCost *= CivInfo.GameInfo.Speed.ScienceCostModifier;
        techCost /= GetScienceModifier(techName);
        var mapSizePredef = CivInfo.GameInfo.TileMap.MapParameters.MapSize.GetPredefinedOrNextSmaller();
        techCost *= mapSizePredef.GetTechCostMultiplier();
        var cityModifier = (CivInfo.Cities.Count(it => !it.IsPuppet) - 1) * mapSizePredef.GetTechCostPerCityModifier();
        CivInfo.ForEachMatchingUnique(UniqueType.LessTechCostFromCities, unique => cityModifier *= 1 - ParseFloat(unique.Params[0]) / 100);
        CivInfo.ForEachMatchingUnique(UniqueType.LessTechCost, unique => techCost *= unique.Params[0].ToPercent());
        techCost *= 1 + cityModifier;
        int memberDiscount = CivInfo.GameInfo.LeagueTechDiscountPercent(CivInfo, techName); // B016: Scholars in Residence
        if (memberDiscount != 0) techCost *= 1 - memberDiscount / 100f;
        return KotlinMath.FloatToInt(techCost);
    }

    private static float ParseFloat(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    public Technology? CurrentTechnology()
    {
        var currentTechnologyName = CurrentTechnologyName();
        if (currentTechnologyName == null) return null;
        return GetRuleset().Technologies.GetValueOrDefault(currentTechnologyName);
    }

    public string? CurrentTechnologyName() => TechsToResearch.Count == 0 ? null : TechsToResearch[0];

    public int ResearchOfTech(string? techName) =>
        techName != null && TechsInProgress.TryGetValue(techName, out var research) ? research : 0;
    // Was once duplicated as fun scienceSpentOnTech(tech: String): Int

    public int RemainingScienceToTech(string techName)
    {
        var spareScience = CanBeResearched(techName) ? GetOverflowScience() : 0;
        return CostOfTech(techName) - ResearchOfTech(techName) - spareScience;
    }

    public string TurnsToTech(string techName)
    {
        var remainingCost = (double)RemainingScienceToTech(techName);
        if (remainingCost <= 0f) return 0.Tr();
        if (CivInfo.Stats.StatsForNextTurn.Science <= 0f) return InfinityChar.ToString();
        return Math.Max(
            1,
            KotlinMath.DoubleToInt(Math.Ceiling(remainingCost / CivInfo.Stats.StatsForNextTurn.Science))
        ).Tr();
    }

    public bool IsResearched(string techName) => TechsResearched.Contains(techName);
    public bool IsResearched(INonPerpetualConstruction construction) => construction.RequiredTechs().All(requiredTech => IsResearched(requiredTech));

    /// <summary>resources which need no research count as researched</summary>
    public bool IsRevealed(TileResource resource)
    {
        var revealedBy = resource.RevealedBy;
        if (revealedBy == null) return true;
        // B010: a resource may also be revealed by a policy (Hidden Antiquity Site, Exploration Complete)
        if (!GetRuleset().Technologies.ContainsKey(revealedBy) && GetRuleset().Policies.ContainsKey(revealedBy))
            return CivInfo.Policies.IsAdopted(revealedBy);
        return IsResearched(revealedBy);
    }

    public bool IsObsolete(BaseUnit unit) => unit.TechsThatObsoleteThis().Any(obsoleteTech => IsResearched(obsoleteTech));

    public bool IsUnresearchable(Technology tech)
    {
        if (((IHasUniques)tech).GetMatchingUniques(UniqueType.OnlyAvailable, GameContext.IgnoreConditionalsState).Any(it => !it.ConditionalsApply(CivInfo.State)))
            return true;
        if (((IHasUniques)tech).HasUnique(UniqueType.Unavailable, CivInfo.State)) return true;
        return false;
    }

    public bool CanBeResearched(string techName)
    {
        var tech = GetRuleset().Technologies[techName];

        if (IsUnresearchable(tech)) return false;
        if (IsResearched(tech.Name) && !tech.IsContinuallyResearchable()) return false;

        return tech.Prerequisites.All(it => IsResearched(it));
    }

    public bool AllTechsAreResearched() => allTechsAreResearched;

    #endregion

    /// <summary>Returns empty list if no path exists</summary>
    public List<Technology> GetRequiredTechsToDestination(Technology destinationTech)
    {
        var prerequisites = new List<Technology>();

        var checkPrerequisites = new Queue<Technology>();
        if (IsUnresearchable(destinationTech)) return [];
        checkPrerequisites.Enqueue(destinationTech);

        while (checkPrerequisites.Count != 0)
        {
            var techToCheck = checkPrerequisites.Dequeue();
            if (IsUnresearchable(techToCheck)) return [];
            // future tech can have been researched even when we're researching it,
            // so...if we skip it we'll end up with 0 techs in the "required techs", which will mean that we don't have anything to research. Yeah.
            if (!techToCheck.IsContinuallyResearchable() &&
                    (IsResearched(techToCheck.Name) || prerequisites.Contains(techToCheck)))
                continue; //no need to add or check prerequisites
            foreach (var prerequisite in techToCheck.Prerequisites)
                checkPrerequisites.Enqueue(GetRuleset().Technologies[prerequisite]);
            prerequisites.Add(techToCheck);
        }

        return prerequisites.OrderBy(it => it.Column.NotNull().ColumnNumber).ToList();
    }

    public int GetScienceFromGreatScientist()
    {
        // https://civilization.fandom.com/wiki/Great_Scientist_(Civ5)
        var sum = 0;
        foreach (var science in ScienceOfLast8Turns) sum += science;
        return KotlinMath.FloatToInt(sum * CivInfo.GameInfo.Speed.ScienceCostModifier);
    }

    private void AddCurrentScienceToScienceOfLast8Turns(int science)
    {
        ScienceOfLast8Turns[CivInfo.GameInfo.Turns % 8] = science;
    }

    private int LimitOverflowScience(int overflowScience)
    {
        // http://www.civclub.net/bbs/forum.php?mod=viewthread&tid=123976
        // Apparently yes, we care about the absolute tech cost, not the actual calculated-for-this-player tech cost,
        //  so don't change to costOfTech()
        return Math.Min(overflowScience, Math.Max(KotlinMath.FloatToInt(CivInfo.Stats.StatsForNextTurn.Science) * 5,
                GetRuleset().Technologies[CurrentTechnologyName().NotNull()].Cost));
    }

    private int GetScienceFromResearchAgreements()
    {
        // https://forums.civfanatics.com/resources/research-agreements-bnw.25568/
        var researchAgreementModifier = 0.5f;
        CivInfo.ForEachMatchingUnique(UniqueType.ScienceFromResearchAgreements, unique =>
        {
            researchAgreementModifier += ParseFloat(unique.Params[0]) / 200f;
        });
        return KotlinMath.FloatToInt(ScienceFromResearchAgreements / 3 * researchAgreementModifier);
    }

    public void EndTurn(int scienceForNewTurn)
    {
        AddCurrentScienceToScienceOfLast8Turns(scienceForNewTurn);
        if (CurrentTechnologyName() == null) return;

        var finalScienceToAdd = scienceForNewTurn;

        if (ScienceFromResearchAgreements != 0)
        {
            var scienceBoost = GetScienceFromResearchAgreements();
            finalScienceToAdd += scienceBoost;
            ScienceFromResearchAgreements = 0;
            CivInfo.AddNotification($"We gained [{scienceBoost.ToString(CultureInfo.InvariantCulture)}] Science from Research Agreement",
                NotificationCategory.General,
                NotificationIcon.Science);
        }
        if (overflowScience != 0)
        {
            finalScienceToAdd += GetOverflowScience();
            overflowScience = 0;
        }

        AddScience(finalScienceToAdd);
    }

    /// <summary>
    /// Adds science "income" e.g. from normal research (see <see cref="EndTurn"/>) or a triggered Unique.
    ///
    /// Also called from <see cref="UpdateResearchProgress"/> to _apply_ overflow.
    /// Adds the points to the <see cref="CurrentTechnology">current research</see>, or, if there is none, to overflow.
    /// </summary>
    public void AddScience(int scienceGet)
    {
        var currentTechnology = CurrentTechnologyName();
        if (currentTechnology == null)
        {
            overflowScience += scienceGet;
            return;
        }
        TechsInProgress[currentTechnology] = ResearchOfTech(currentTechnology) + scienceGet;
        if (TechsInProgress[currentTechnology] < CostOfTech(currentTechnology))
            return;

        // We finished it!
        // http://www.civclub.net/bbs/forum.php?mod=viewthread&tid=123976
        var extraScienceLeftOver = TechsInProgress[currentTechnology] - CostOfTech(currentTechnology);
        overflowScience += LimitOverflowScience(extraScienceLeftOver);
        AddTechnology(currentTechnology);
    }

    /// <summary>
    /// Checks whether the research on the current technology can be completed
    /// and, if so, completes the research.
    /// </summary>
    public void UpdateResearchProgress()
    {
        var currentTechnology = CurrentTechnologyName();
        if (currentTechnology == null) return;
        var realOverflow = GetOverflowScience();
        var scienceSpent = ResearchOfTech(currentTechnology) + realOverflow;
        if (scienceSpent >= CostOfTech(currentTechnology))
        {
            overflowScience = 0;
            AddScience(realOverflow);
        }
    }

    public void GetFreeTechnology(string techName)
    {
        FreeTechs--;
        AddTechnology(techName);
    }

    public bool CanResearchTech() => GetRuleset().Technologies.Values.Any(it => CanBeResearched(it.Name));
}
