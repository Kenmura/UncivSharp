using Unciv.Logic.Battle;
using Unciv.Logic.Civilization.Managers;
using Unciv.Models.Ruleset.Validation;
using Unciv.Models.StatTypes;
using Unciv.Utils;

namespace Unciv.Models.Ruleset.Unique;

/// <summary>What <c>Conditionals</c> reads from <c>UncivGame.Current.settings</c> (a client global).</summary>
public interface IConditionalsSettings
{
    bool ShowTutorials { get; }
    bool IsTutorialTaskCompleted(string task);
}

/// <summary>
/// Ported from core/src/com/unciv/models/ruleset/unique/Conditionals.kt.
/// </summary>
/// <remarks>
/// Deviations: <c>timeThis</c> profiling is dropped. <c>ConditionalChance</c> hashes the Unique by its text and source instead of the JVM
/// identity hash (<c>Unique</c> has no <c>hashCode</c> in Kotlin, so the original result varies per run): see <see cref="UniqueHash"/>.
/// <c>UncivGame.Current.settings</c> is <see cref="Settings"/>; with none set, tutorials count as disabled.
/// The game classes are the seam interfaces of <c>GameContextConditionals.cs</c>.
/// </remarks>
public static class Conditionals
{
    private sealed class NoSettings : IConditionalsSettings
    {
        public bool ShowTutorials => false;
        public bool IsTutorialTaskCompleted(string task) => false;
    }

    private static volatile IConditionalsSettings CurrentSettings = new NoSettings();
    private static readonly AsyncLocal<IConditionalsSettings?> ScopedSettings = new();

    /// <summary>Stands in for <c>UncivGame.Current.settings</c>. A <see cref="UseSettings"/> scope of the running call chain wins over the global value.</summary>
    public static IConditionalsSettings Settings
    {
        get => ScopedSettings.Value ?? CurrentSettings;
        set => CurrentSettings = value;
    }

    private sealed class SettingsScope(IConditionalsSettings? previous) : IDisposable
    {
        public void Dispose() => ScopedSettings.Value = previous;
    }

    /// <summary>
    /// C054: makes <paramref name="settings"/> the settings of the calling flow only (a query evaluating the viewer's tutorial conditionals must not touch the
    /// process-wide value other hosts in the same process read). Dispose restores the previous scope.
    /// </summary>
    public static IDisposable UseSettings(IConditionalsSettings settings)
    {
        var previous = ScopedSettings.Value;
        ScopedSettings.Value = settings;
        return new SettingsScope(previous);
    }

    // Deterministic stand-in for Unique.hashCode() (JVM identity hash in Kotlin).
    private static int UniqueHash(Unique unique) =>
        Randomness.HashOf(JavaHash.HashCode(unique.Text), unique.SourceObjectType == null ? 0 : JavaHash.HashCode(unique.SourceObjectType.Value.ToString()),
            unique.SourceObjectName == null ? 0 : JavaHash.HashCode(unique.SourceObjectName));

    private static float GetStateBasedRandom(GameContext state, Unique? unique)
    {
        int seed = Randomness.HashOf(state.GameInfo?.Turns ?? 0, unique == null ? 0 : UniqueHash(unique), state.GetHashCode());
        return Randomness.Salted(seed, state.GameInfo?.RandomSalt ?? 0).NextFloat();
    }

    private static float ToFloat(string text) =>
        UniqueParameterTypeExtensions.ToFloatOrNull(text) ?? throw new FormatException($"Invalid float: \"{text}\"");

    private static int ToInt(string text) =>
        UniqueParameterTypeExtensions.ToIntOrNull(text) ?? throw new FormatException($"Invalid int: \"{text}\"");

    public static bool ConditionalApplies(Unique? unique, Unique conditional, GameContext state)
    {
        if (conditional.IsOtherModifierType)
            return true; // not a filtering condition, includes e.g. ModifierHiddenFromUsers

        if (TryEvalGameAndEraConditional(unique, conditional, state, out var result)) return result;
        if (TryEvalCivAndReligionConditional(unique, conditional, state, out result)) return result;
        if (TryEvalCityConditional(conditional, state, out result)) return result;
        if (TryEvalUnitAndCombatConditional(conditional, state, out result)) return result;
        if (TryEvalTileAndCountableConditional(conditional, state, out result)) return result;
        return false;
    }

    private static bool CheckOnGameInfo(GameContext state, Func<IGameContextGameInfo, bool> predicate) =>
        state.GameInfo != null && predicate(state.GameInfo);

    private static bool CheckOnCiv(GameContext state, Func<IGameContextCivilization, bool> predicate) =>
        state.RelevantCiv != null && predicate(state.RelevantCiv);

    private static bool CheckOnCity(GameContext state, Func<IGameContextCity, bool> predicate) =>
        state.RelevantCity != null && predicate(state.RelevantCity);

    private static bool CompareEra(GameContext state, string eraParam, Func<int, int, bool> compare)
    {
        if (state.GameInfo == null) return false;
        int? era = state.GameInfo.Ruleset.GetEraNumber(eraParam);
        if (era == null) return false;
        return compare(state.RelevantCiv.NotNull().GetEraNumber(), era.Value);
    }

    private static bool CheckResourceOrStatAmount(
        GameContext state,
        string resourceOrStatName,
        float lowerLimit,
        float upperLimit,
        bool modifyByGameSpeed,
        Func<int, float, float, bool> compare)
    {
        if (state.GameInfo == null) return false;
        float gameSpeedModifier = modifyByGameSpeed ? state.GameInfo.Speed.Modifier : 1f;

        if (state.GameInfo.Ruleset.TileResources.ContainsKey(resourceOrStatName))
            return compare(state.GetResourceAmount(resourceOrStatName), lowerLimit * gameSpeedModifier, upperLimit * gameSpeedModifier);
        var stat = StatExtensions.SafeValueOf(resourceOrStatName);
        if (stat == null) return false;
        int statReserve = state.GetStatAmount(stat.Value);

        gameSpeedModifier = modifyByGameSpeed ? state.GameInfo.Speed.GetStatCostModifier(stat.Value) : 1f;
        return compare(statReserve, lowerLimit * gameSpeedModifier, upperLimit * gameSpeedModifier);
    }

    private static bool CompareCountables2(GameContext state, string first, string second, Func<int, int, bool> compare)
    {
        int? firstNumber = Countables.GetCountableAmount(first, state);
        int? secondNumber = Countables.GetCountableAmount(second, state);
        return firstNumber != null && secondNumber != null && compare(firstNumber.Value, secondNumber.Value);
    }

    private static bool CompareCountables3(GameContext state, string first, string second, string third, Func<int, int, int, bool> compare)
    {
        int? firstNumber = Countables.GetCountableAmount(first, state);
        int? secondNumber = Countables.GetCountableAmount(second, state);
        int? thirdNumber = Countables.GetCountableAmount(third, state);
        return firstNumber != null && secondNumber != null && thirdNumber != null
            && compare(firstNumber.Value, secondNumber.Value, thirdNumber.Value);
    }

    private static bool BuildingBuiltBy(IGameContextCivilization civ, string name) =>
        civ.Cities.Any(it => it.ContainsBuildingOrEquivalent(name));

    private static bool DifficultyCompare(IGameContextGameInfo gameInfo, string difficulty, Func<int, int, bool> compare)
    {
        if (gameInfo.Ruleset.DifficultyNames.Contains(difficulty))
        {
            var difficulties = gameInfo.Ruleset.DifficultyNames;
            return compare(IndexOf(difficulties, gameInfo.GetDifficultyName()), IndexOf(difficulties, difficulty));
        }
        return false;
    }

    private static bool TryEvalGameAndEraConditional(Unique? unique, Unique conditional, GameContext state, out bool result)
    {
        var parameters = conditional.Params;
        string P(int index) => parameters[index];

        result = conditional.Type switch
        {
            UniqueType.ConditionalChance => GetStateBasedRandom(state, unique) < ToFloat(P(0)) / 100f,
            UniqueType.ConditionalEveryTurns => CheckOnGameInfo(state, g => g.Turns % ToInt(P(0)) == 0),
            UniqueType.ConditionalBeforeTurns => CheckOnGameInfo(state, g => g.Turns < ToInt(P(0))),
            UniqueType.ConditionalAfterTurns => CheckOnGameInfo(state, g => g.Turns >= ToInt(P(0))),
            UniqueType.ConditionalTutorialsEnabled => Settings.ShowTutorials,
            UniqueType.ConditionalTutorialCompleted => Settings.IsTutorialTaskCompleted(P(0)),
            UniqueType.ConditionalBeforeEra => CompareEra(state, P(0), (current, param) => current < param),
            UniqueType.ConditionalStartingFromEra => CompareEra(state, P(0), (current, param) => current >= param),
            UniqueType.ConditionalDuringEra => CompareEra(state, P(0), (current, param) => current == param),
            UniqueType.ConditionalIfStartingInEra => CheckOnGameInfo(state, g => g.GameParameters.StartingEra == P(0)),
            UniqueType.ConditionalSpeed => CheckOnGameInfo(state, g => g.GameParameters.Speed == P(0)),
            UniqueType.ConditionalDifficulty => CheckOnGameInfo(state, g => g.GameParameters.Difficulty == P(0)),
            UniqueType.ConditionalDifficultyOrHigher => CheckOnGameInfo(state, g => DifficultyCompare(g, P(0), (a, b) => a >= b)),
            UniqueType.ConditionalDifficultyOrLower => CheckOnGameInfo(state, g => DifficultyCompare(g, P(0), (a, b) => a <= b)),
            UniqueType.ConditionalVictoryEnabled => CheckOnGameInfo(state, g => g.GameParameters.VictoryTypes.Contains(P(0))),
            UniqueType.ConditionalVictoryDisabled => CheckOnGameInfo(state, g => !g.GameParameters.VictoryTypes.Contains(P(0))),
            UniqueType.ConditionalReligionEnabled => CheckOnGameInfo(state, g => g.IsReligionEnabled()),
            UniqueType.ConditionalReligionDisabled => CheckOnGameInfo(state, g => !g.IsReligionEnabled()),
            UniqueType.ConditionalEspionageEnabled => CheckOnGameInfo(state, g => g.IsEspionageEnabled()),
            UniqueType.ConditionalEspionageDisabled => CheckOnGameInfo(state, g => !g.IsEspionageEnabled()),
            UniqueType.ConditionalNuclearWeaponsEnabled => CheckOnGameInfo(state, g => g.GameParameters.NuclearWeaponsEnabled),
            UniqueType.ConditionalNuclearWeaponsDisabled => CheckOnGameInfo(state, g => !g.GameParameters.NuclearWeaponsEnabled),
            UniqueType.ConditionalModEnabled => CheckOnGameInfo(state, g => ModsAndBase(g).Any(it => ModCompatibility.ModNameFilter(it, P(0)))),
            UniqueType.ConditionalModNotEnabled => CheckOnGameInfo(state, g => !ModsAndBase(g).Any(it => ModCompatibility.ModNameFilter(it, P(0)))),
            _ => false,
        };
        return conditional.Type is
            UniqueType.ConditionalChance or UniqueType.ConditionalEveryTurns or UniqueType.ConditionalBeforeTurns or
            UniqueType.ConditionalAfterTurns or UniqueType.ConditionalTutorialsEnabled or UniqueType.ConditionalTutorialCompleted or
            UniqueType.ConditionalBeforeEra or UniqueType.ConditionalStartingFromEra or UniqueType.ConditionalDuringEra or
            UniqueType.ConditionalIfStartingInEra or UniqueType.ConditionalSpeed or UniqueType.ConditionalDifficulty or
            UniqueType.ConditionalDifficultyOrHigher or UniqueType.ConditionalDifficultyOrLower or UniqueType.ConditionalVictoryEnabled or
            UniqueType.ConditionalVictoryDisabled or UniqueType.ConditionalReligionEnabled or UniqueType.ConditionalReligionDisabled or
            UniqueType.ConditionalEspionageEnabled or UniqueType.ConditionalEspionageDisabled or UniqueType.ConditionalNuclearWeaponsEnabled or
            UniqueType.ConditionalNuclearWeaponsDisabled or UniqueType.ConditionalModEnabled or UniqueType.ConditionalModNotEnabled;
    }

    private static bool TryEvalCivAndReligionConditional(Unique? unique, Unique conditional, GameContext state, out bool result)
    {
        var parameters = conditional.Params;
        string P(int index) => parameters[index];

        switch (conditional.Type)
        {
            case UniqueType.ConditionalCivFilter: result = CheckOnCiv(state, c => c.MatchesFilter(P(0), state)); return true;
            case UniqueType.ConditionalWar: result = CheckOnCiv(state, c => c.IsAtWar()); return true;
            case UniqueType.ConditionalNotWar: result = CheckOnCiv(state, c => !c.IsAtWar()); return true;
            case UniqueType.ConditionalWithResource: result = state.GetResourceAmount(P(0)) > 0; return true;
            case UniqueType.ConditionalWithoutResource: result = state.GetResourceAmount(P(0)) <= 0; return true;

            case UniqueType.ConditionalWhenAboveAmountStatResource:
                result = CheckResourceOrStatAmount(state, P(1), ToFloat(P(0)), float.MaxValue, unique?.IsModifiedByGameSpeed() == true,
                    (current, lowerLimit, _) => current > lowerLimit);
                return true;
            case UniqueType.ConditionalWhenBelowAmountStatResource:
                // Kotlin Float.MIN_VALUE is the smallest positive float, i.e. float.Epsilon (not float.MinValue)
                result = CheckResourceOrStatAmount(state, P(1), float.Epsilon, ToFloat(P(0)), unique?.IsModifiedByGameSpeed() == true,
                    (current, _, upperLimit) => current < upperLimit);
                return true;
            case UniqueType.ConditionalWhenBetweenStatResource:
                result = CheckResourceOrStatAmount(state, P(2), ToFloat(P(0)), ToFloat(P(1)), unique?.IsModifiedByGameSpeed() == true,
                    (current, lowerLimit, upperLimit) => current >= lowerLimit && current <= upperLimit);
                return true;

            case UniqueType.ConditionalHappy: result = CheckOnCiv(state, c => c.Happiness >= 0); return true;
            case UniqueType.ConditionalGoldenAge: result = CheckOnCiv(state, c => c.IsGoldenAge()); return true;
            case UniqueType.ConditionalNotGoldenAge: result = CheckOnCiv(state, c => !c.IsGoldenAge()); return true;
            case UniqueType.ConditionalTech:
                result = CheckOnCiv(state, c =>
                    c.GameInfo.NotNull().Ruleset.Technologies.ContainsKey(P(0)) ? c.IsTechResearched(P(0)) : c.AnyResearchedTechnologyMatchesFilter(P(0)));
                return true;
            case UniqueType.ConditionalNoTech:
                result = CheckOnCiv(state, c =>
                    c.GameInfo.NotNull().Ruleset.Technologies.ContainsKey(P(0)) ? !c.IsTechResearched(P(0)) : !c.AnyResearchedTechnologyMatchesFilter(P(0)));
                return true;
            case UniqueType.ConditionalWhileResearching: result = CheckOnCiv(state, c => c.CurrentTechnologyMatchesFilter(P(0))); return true;
            case UniqueType.ConditionalNoCivAdopted:
                result = CheckOnGameInfo(state, g => !g.Civilizations.Any(it =>
                    it.IsMajorCiv() && it.IsAlive() && (it.IsPolicyAdopted(P(0)) || it.Religion?.HasBelief(P(0)) == true)));
                return true;
            case UniqueType.ConditionalAfterPolicyOrBelief:
                result = CheckOnCiv(state, c => c.IsPolicyAdopted(P(0)) || c.Religion?.HasBelief(P(0)) == true); return true;
            case UniqueType.ConditionalBeforePolicyOrBelief:
                result = CheckOnCiv(state, c => !c.IsPolicyAdopted(P(0)) && c.Religion?.HasBelief(P(0)) != true); return true;
            case UniqueType.ConditionalBeforePantheon: result = CheckOnCiv(state, c => c.ReligionState == ReligionState.None); return true;
            case UniqueType.ConditionalAfterPantheon: result = CheckOnCiv(state, c => c.ReligionState != ReligionState.None); return true;
            case UniqueType.ConditionalBeforeReligion: result = CheckOnCiv(state, c => c.ReligionState < ReligionState.Religion); return true;
            case UniqueType.ConditionalAfterReligion: result = CheckOnCiv(state, c => c.ReligionState >= ReligionState.Religion); return true;
            case UniqueType.ConditionalBeforeEnhancingReligion: result = CheckOnCiv(state, c => c.ReligionState < ReligionState.EnhancedReligion); return true;
            case UniqueType.ConditionalAfterEnhancingReligion: result = CheckOnCiv(state, c => c.ReligionState >= ReligionState.EnhancedReligion); return true;
            case UniqueType.ConditionalAfterGeneratingGreatProphet: result = CheckOnCiv(state, c => c.GreatProphetsEarned() > 0); return true;

            case UniqueType.ConditionalBuildingBuilt: result = CheckOnCiv(state, c => BuildingBuiltBy(c, P(0))); return true;
            case UniqueType.ConditionalBuildingNotBuilt: result = CheckOnCiv(state, c => !BuildingBuiltBy(c, P(0))); return true;
            case UniqueType.ConditionalBuildingBuiltAll:
                result = CheckOnCiv(state, c => c.Cities.Where(it => it.MatchesFilter(P(1), null)).All(it => it.ContainsBuildingOrEquivalent(P(0)))); return true;
            case UniqueType.ConditionalBuildingBuiltAmount:
                result = CheckOnCiv(state, c => c.Cities.Count(it => it.ContainsBuildingOrEquivalent(P(0)) && it.MatchesFilter(P(2), null)) >= ToInt(P(1))); return true;
            case UniqueType.ConditionalBuildingBuiltByAnybody:
                result = CheckOnGameInfo(state, g => g.GetCities().Any(it => it.ContainsBuildingOrEquivalent(P(0)))); return true;
            case UniqueType.ConditionalBuildingNotBuiltByAnybody:
                result = !CheckOnGameInfo(state, g => g.GetCities().Any(it => it.ContainsBuildingOrEquivalent(P(0)))); return true;

            case UniqueType.ConditionalFirstCivToResearch:
                result = unique != null
                    && unique.SourceObjectType == UniqueTarget.Tech
                    && CheckOnGameInfo(state, g => !g.Civilizations.Any(it =>
                        !Equals(it, state.RelevantCiv) && it.IsMajorCiv()
                        && it.IsTechResearched(unique.SourceObjectName.NotNull()))); // guarded by the sourceObjectType check
                return true;
            case UniqueType.ConditionalFirstCivToAdopt:
                result = unique != null
                    && unique.SourceObjectType == UniqueTarget.Policy
                    && CheckOnGameInfo(state, g => !g.Civilizations.Any(it =>
                        !Equals(it, state.RelevantCiv) && it.IsMajorCiv()
                        && it.IsPolicyAdopted(unique.SourceObjectName.NotNull()))); // guarded by the sourceObjectType check
                return true;

            default:
                result = false;
                return false;
        }
    }

    private static bool TryEvalCityConditional(Unique conditional, GameContext state, out bool result)
    {
        var parameters = conditional.Params;
        string P(int index) => parameters[index];

        switch (conditional.Type)
        {
            // Filtered via city.getMatchingUniques
            case UniqueType.ConditionalInThisCity: result = state.RelevantCity != null; return true;
            case UniqueType.ConditionalCityFilter: result = CheckOnCity(state, c => c.MatchesFilter(P(0), state.RelevantCiv)); return true;
            case UniqueType.ConditionalCityConnected: result = CheckOnCity(state, c => c.IsConnectedToCapital()); return true;
            case UniqueType.ConditionalCityReligion:
                result = CheckOnCity(state, c => c.GetMajorityReligion()?.MatchesFilter(P(0), state, state.RelevantCiv) == true); return true;
            case UniqueType.ConditionalCityNotReligion:
                result = CheckOnCity(state, c => c.GetMajorityReligion()?.MatchesFilter(P(0), state, state.RelevantCiv) != true); return true;
            case UniqueType.ConditionalCityMajorReligion: result = CheckOnCity(state, c => c.GetMajorityReligion()?.IsMajorReligion() == true); return true;
            case UniqueType.ConditionalCityEnhancedReligion: result = CheckOnCity(state, c => c.GetMajorityReligion()?.IsEnhancedReligion() == true); return true;
            case UniqueType.ConditionalCityThisReligion:
                result = CheckOnCity(state, c => Equals(c.GetMajorityReligion(), state.RelevantCiv?.Religion)); return true;
            case UniqueType.ConditionalWLTKD: result = CheckOnCity(state, c => c.IsWeLoveTheKingDayActive()); return true;
            case UniqueType.ConditionalCityWithBuilding: result = CheckOnCity(state, c => c.ContainsBuildingOrEquivalent(P(0))); return true;
            case UniqueType.ConditionalCityWithoutBuilding: result = CheckOnCity(state, c => !c.ContainsBuildingOrEquivalent(P(0))); return true;
            case UniqueType.ConditionalPopulationFilter: result = CheckOnCity(state, c => c.GetPopulationFilterAmount(P(1)) >= ToInt(P(0))); return true;
            case UniqueType.ConditionalExactPopulationFilter: result = CheckOnCity(state, c => c.GetPopulationFilterAmount(P(1)) == ToInt(P(0))); return true;
            case UniqueType.ConditionalBetweenPopulationFilter:
                result = CheckOnCity(state, c =>
                {
                    int amount = c.GetPopulationFilterAmount(P(2));
                    return amount >= ToInt(P(0)) && amount <= ToInt(P(1));
                });
                return true;
            case UniqueType.ConditionalBelowPopulationFilter: result = CheckOnCity(state, c => c.GetPopulationFilterAmount(P(1)) < ToInt(P(0))); return true;
            case UniqueType.ConditionalWhenGarrisoned: result = CheckOnCity(state, c => c.CenterTileMilitaryUnitCanGarrison()); return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryEvalUnitAndCombatConditional(Unique conditional, GameContext state, out bool result)
    {
        var parameters = conditional.Params;
        string P(int index) => parameters[index];

        switch (conditional.Type)
        {
            case UniqueType.ConditionalVsCity: result = state.TheirCombatant?.MatchesFilter("City", false) == true; return true;
            case UniqueType.ConditionalVsUnits:
            case UniqueType.ConditionalVsCombatant:
                result = state.TheirCombatant?.MatchesFilter(P(0), true) == true; return true;
            case UniqueType.ConditionalOurUnit:
            case UniqueType.ConditionalOurUnitOnUnit:
                result = state.RelevantUnit?.MatchesFilter(P(0)) == true; return true;
            case UniqueType.ConditionalUnitWithPromotion:
                result = state.RelevantUnit != null && state.RelevantUnit.HasPromotionOrStatus(P(0)); return true;
            case UniqueType.ConditionalUnitWithoutPromotion:
                result = state.RelevantUnit != null && !state.RelevantUnit.HasPromotionOrStatus(P(0)); return true;
            case UniqueType.ConditionalAttacking: result = state.CombatAction == CombatAction.Attack; return true;
            case UniqueType.ConditionalDefending: result = state.CombatAction == CombatAction.Defend; return true;
            case UniqueType.ConditionalAboveHP:
                result = (state.RelevantUnit != null && state.RelevantUnit.Health > ToInt(P(0)))
                    || (state.OurCombatant != null && state.OurCombatant.GetHealth() > ToInt(P(0)));
                return true;
            case UniqueType.ConditionalBelowHP:
                result = (state.RelevantUnit != null && state.RelevantUnit.Health < ToInt(P(0)))
                    || (state.OurCombatant != null && state.OurCombatant.GetHealth() < ToInt(P(0)));
                return true;
            case UniqueType.ConditionalAboveMovement:
                result = state.RelevantUnit != null && state.RelevantUnit.CurrentMovement > ToInt(P(0)); return true;
            case UniqueType.ConditionalBelowMovement:
                result = state.RelevantUnit != null && state.RelevantUnit.CurrentMovement < ToInt(P(0)); return true;
            case UniqueType.ConditionalHasNotUsedOtherActions:
                result = state.Unit == null // So we get the action as a valid action in BaseUnit.hasUnique()
                    || state.Unit.AbilityToTimesUsedIsEmpty;
                return true;
            case UniqueType.ConditionalStackedWithUnit:
                result = state.RelevantUnit != null
                    && state.RelevantUnit.GetTile().GetUnits().Any(it => !Equals(it, state.RelevantUnit) && it.MatchesFilter(P(0)));
                return true;
            case UniqueType.ConditionalNotStackedWithUnit:
                result = state.RelevantUnit == null
                    || !state.RelevantUnit.GetTile().GetUnits().Any(it => !Equals(it, state.RelevantUnit) && it.MatchesFilter(P(0)));
                return true;
            case UniqueType.ConditionalWhenCarriedBy:
                // Check if the unit is currently transported and being carried by matching filter
                if (state.RelevantUnit == null || !state.RelevantUnit.IsTransported)
                {
                    result = false;
                    return true;
                }
                var carrier = state.RelevantUnit.GetTile().MilitaryUnit;
                // Only true if: 1) carrier exists, 2) carrier is NOT the unit itself, 3) carrier matches filter
                result = carrier != null && !Equals(carrier, state.RelevantUnit) && carrier.MatchesFilter(P(0));
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryEvalTileAndCountableConditional(Unique conditional, GameContext state, out bool result)
    {
        var parameters = conditional.Params;
        string P(int index) => parameters[index];

        switch (conditional.Type)
        {
            case UniqueType.ConditionalInTiles: result = state.RelevantTile?.MatchesFilter(P(0), state.RelevantCiv) == true; return true;
            case UniqueType.ConditionalInTilesNot: result = state.RelevantTile?.MatchesFilter(P(0), state.RelevantCiv) == false; return true;
            case UniqueType.ConditionalAdjacentTo: result = state.RelevantTile?.IsAdjacentTo(P(0), state.RelevantCiv) == true; return true;
            case UniqueType.ConditionalNotAdjacentTo: result = state.RelevantTile?.IsAdjacentTo(P(0), state.RelevantCiv) == false; return true;
            case UniqueType.ConditionalFightingInTiles: result = state.AttackedTile?.MatchesFilter(P(0), state.RelevantCiv) == true; return true;
            case UniqueType.ConditionalNearTiles:
                result = state.RelevantTile != null
                    && state.RelevantTile.GetTilesInDistance(ToInt(P(0))).Any(it => it.MatchesFilter(P(1), state.RelevantCiv));
                return true;
            case UniqueType.ConditionalVsLargerCiv:
                int yourCities = state.RelevantCiv?.Cities.Count ?? 1;
                int theirCities = state.TheirCombatant?.GetCivInfo().Cities.Count ?? 0;
                result = yourCities < theirCities;
                return true;
            case UniqueType.ConditionalForeignContinent:
                result = CheckOnCiv(state, c =>
                    state.RelevantTile != null && (
                        c.Cities.Count == 0 || c.GetCapital() == null
                        || c.GetCapital().NotNull().GetCenterTileOrNull().NotNull().GetContinent() != state.RelevantTile.GetContinent()));
                return true;
            case UniqueType.ConditionalAdjacentUnit:
                result = state.RelevantCiv != null
                    && state.RelevantUnit != null
                    && state.RelevantTile.NotNull().Neighbors.Any(tile => tile.GetUnits().Any(it =>
                        !Equals(it, state.RelevantUnit) && Equals(it.Civ, state.RelevantCiv) && it.MatchesFilter(P(0))));
                return true;
            case UniqueType.ConditionalNeighborTiles:
                if (state.RelevantTile == null)
                {
                    result = false;
                    return true;
                }
                int count = state.RelevantTile.Neighbors.Count(it => it.MatchesFilter(P(2), state.RelevantCiv));
                result = count >= ToInt(P(0)) && count <= ToInt(P(1));
                return true;
            case UniqueType.ConditionalOnWaterMaps: result = state.Region?.ContinentId == -1; return true;
            case UniqueType.ConditionalInRegionOfType: result = state.Region?.Type == P(0); return true;
            case UniqueType.ConditionalInRegionExceptOfType: result = state.Region?.Type != P(0); return true;
            case UniqueType.ConditionalCountableEqualTo: result = CompareCountables2(state, P(0), P(1), (a, b) => a == b); return true;
            case UniqueType.ConditionalCountableDifferentThan: result = CompareCountables2(state, P(0), P(1), (a, b) => a != b); return true;
            case UniqueType.ConditionalCountableMoreThan: result = CompareCountables2(state, P(0), P(1), (a, b) => a > b); return true;
            case UniqueType.ConditionalCountableLessThan: result = CompareCountables2(state, P(0), P(1), (a, b) => a < b); return true;
            case UniqueType.ConditionalCountableBetween:
                result = CompareCountables3(state, P(0), P(1), P(2), (first, second, third) => first >= second && first <= third); return true;
            default:
                result = false;
                return false;
        }
    }

    private static IEnumerable<string> ModsAndBase(IGameContextGameInfo gameInfo) =>
        gameInfo.GameParameters.Mods.Concat([gameInfo.GameParameters.BaseRuleset]);

    // Kotlin List.indexOf: -1 when absent
    private static int IndexOf(IReadOnlyList<string> list, string item)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == item) return i;
        return -1;
    }
}
