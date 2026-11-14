using System.Collections.Frozen;
using System.Globalization;
using Unciv.Logic.Civilization;
using Unciv.Logic.Civilization.Transients;
using Unciv.Logic.Map.MapGenerator;
using Unciv.Logic.Map.MapUnits;
using Unciv.Models.StatTypes;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using TileClass = Unciv.Logic.Map.Tiles.Tile;

namespace Unciv.Models.Ruleset.Unique;

// core/src/com/unciv/models/ruleset/unique/UniqueTriggerActivation.kt
// Stripped: the purity annotations. Client calls (sound, music, settings) go through GameClientState (IClientAudio, AddCompletedTutorialTask).
// Buildings, techs, policies, ancient ruins and promotions can have 'triggered' effects
public static partial class UniqueTriggerActivation
{
    public static bool TriggerUnique(Unique unique, CityObject city, string? notification = null, string? triggerNotificationText = null) =>
        TriggerUnique(unique, city.Civ, city, tile: city.GetCenterTile(),
            notification: notification, triggerNotificationText: triggerNotificationText);

    public static bool TriggerUnique(Unique unique, MapUnit unit, string? notification = null, string? triggerNotificationText = null) =>
        TriggerUnique(unique, unit.Civ, unit: unit, tile: unit.CurrentTile,
            notification: notification, triggerNotificationText: triggerNotificationText);

    /// <returns>whether an action was successfully performed. Assumes that conditional check has already been performed</returns>
    public static bool TriggerUnique(Unique unique, Civilization civInfo, CityObject? city = null, MapUnit? unit = null, TileClass? tile = null,
        string? notification = null, string? triggerNotificationText = null)
    {
        var function = GetTriggerFunction(unique, civInfo, city, unit, tile, notification, triggerNotificationText);
        return function != null && function();
    }

    /// <returns>The action to be performed if possible, else null
    /// This is so the unit actions can be displayed as "disabled" if they won't actually do anything
    /// Even if the action itself is performable, there are still cases where it can fail -
    ///   for example unit placement - which is why the action itself needs to return Boolean to indicate success</returns>
    public static Func<bool>? GetTriggerFunction(Unique unique, Civilization civInfo, CityObject? city = null, MapUnit? unit = null, TileClass? tile = null,
        string? notification = null, string? triggerNotificationText = null)
    {
        // Kotlin default parameter: tile = city?.getCenterTile() ?: unit?.currentTile (C# cannot tell an explicit null from "not given")
        tile ??= city?.GetCenterTile() ?? unit?.CurrentTile;

        var timingConditional = unique.GetModifiers(UniqueType.ConditionalTimedUnique).FirstOrDefault();
        if (timingConditional != null)
        {
            return () =>
            {
                civInfo.TemporaryUniques.Add(new TemporaryUnique(unique, timingConditional.Params[0].ToInt()));
                if (unique.Type is UniqueType.ProvidesResources or UniqueType.ConsumesResources or UniqueType.StatPercentFromObjectToResource)
                    civInfo.Cache.UpdateCivResources();
                return true;
            };
        }

        if (unique.Type is not { } uniqueType || !Handlers.TryGetValue(uniqueType, out var handler)) return null;
        var ctx = new TriggerContext(unique, civInfo, city, unit, tile, notification, triggerNotificationText);
        return handler(ctx);
    }

    private sealed class TriggerContext
    {
        private readonly KotlinLazy<CityObject?> relevantCityLazy;

        public TriggerContext(Unique unique, Civilization civInfo, CityObject? city, MapUnit? unit, TileClass? tile,
            string? notification, string? triggerNotificationText)
        {
            Unique = unique;
            CivInfo = civInfo;
            City = city;
            Unit = unit;
            Tile = tile;
            Notification = notification;
            TriggerNotificationText = triggerNotificationText;
            relevantCityLazy = new KotlinLazy<CityObject?>(() => city ?? tile?.GetCity());
            GameContext = new GameContext(civInfo, city, unit, tile);
            Rng = GameContext.StateBasedRandom("UniqueTriggerActivation.getTriggerFunction", JavaHash.HashCode(unique.Text));
            ChosenCity = RelevantCity() ?? civInfo.Cities.FirstOrDefault(it => it.IsCapital());
            TileBasedRandom = tile != null
                ? civInfo.GameInfo.SeededRandom(tile.Position.GetHashCode())
                : civInfo.GameInfo.SeededRandom(-550); // Very random indeed
            Ruleset = civInfo.GameInfo.Ruleset;
        }

        public Unique Unique { get; }
        public Civilization CivInfo { get; }
        public CityObject? City { get; }
        public MapUnit? Unit { get; }
        public TileClass? Tile { get; }
        public string? Notification { get; }
        public string? TriggerNotificationText { get; }
        public GameContext GameContext { get; }
        public GameRandom Rng { get; }
        public CityObject? ChosenCity { get; }
        public GameRandom TileBasedRandom { get; }
        public Ruleset Ruleset { get; }

        public CityObject? RelevantCity() => relevantCityLazy.Value;

        public IEnumerable<CityObject> GetApplicableCities(string cityFilter) =>
            cityFilter == "in this city"
                ? new[] { RelevantCity() }.OfType<CityObject>()
                : CivInfo.Cities.Where(it => it.MatchesFilter(cityFilter));
    }

    private static readonly FrozenDictionary<UniqueType, Func<TriggerContext, Func<bool>?>> Handlers =
        new Dictionary<UniqueType, Func<TriggerContext, Func<bool>?>>
        {
            [UniqueType.TriggerEvent] = HandleTriggerEvent,
            [UniqueType.MarkTutorialComplete] = HandleMarkTutorialComplete,
            [UniqueType.PlaySound] = HandlePlaySound,
            [UniqueType.ChooseMusic] = HandleChooseMusic,
            [UniqueType.GetLeaderTitle] = HandleGetLeaderTitle,
            [UniqueType.OneTimeFreeUnit] = HandleOneTimeFreeUnit,
            [UniqueType.OneTimeAmountFreeUnits] = HandleOneTimeAmountFreeUnits,
            [UniqueType.OneTimeRebel] = HandleOneTimeRebel,
            [UniqueType.OneTimeAmountRebels] = HandleOneTimeAmountRebels,
            [UniqueType.OneTimeFreeUnitRuins] = HandleOneTimeFreeUnitRuins,
            [UniqueType.OneTimeFreePolicy] = HandleOneTimeFreePolicy,
            [UniqueType.OneTimeAmountFreePolicies] = HandleOneTimeAmountFreePolicies,
            [UniqueType.OneTimeAdoptPolicyOrBelief] = HandleOneTimeAdoptPolicyOrBelief,
            [UniqueType.OneTimeRemovePolicy] = HandleOneTimeRemovePolicy,
            [UniqueType.OneTimeRemovePolicyRefund] = HandleOneTimeRemovePolicyRefund,
            [UniqueType.OneTimeEnterGoldenAge] = HandleOneTimeEnterGoldenAge,
            [UniqueType.OneTimeEnterGoldenAgeTurns] = HandleOneTimeEnterGoldenAge,
            [UniqueType.OneTimeFreeGreatPerson] = HandleOneTimeFreeGreatPerson,
            [UniqueType.OneTimeGainPopulation] = HandleOneTimeGainPopulation,
            [UniqueType.OneTimeGainPopulationRandomCity] = HandleOneTimeGainPopulationRandomCity,
            [UniqueType.OneTimeFreeTech] = HandleOneTimeFreeTech,
            [UniqueType.OneTimeAmountFreeTechs] = HandleOneTimeAmountFreeTechs,
            [UniqueType.OneTimeFreeTechRuins] = HandleOneTimeFreeTechRuins,
            [UniqueType.OneTimeDiscoverTech] = HandleOneTimeDiscoverTech,
            [UniqueType.OneTimeProvideResources] = HandleOneTimeProvideResources,
            [UniqueType.OneTimeConsumeResources] = HandleOneTimeConsumeResources,
            [UniqueType.OneTimeGainResource] = HandleOneTimeGainResource,
            [UniqueType.OneTimeSetStockpile] = HandleOneTimeSetStockpile,
            [UniqueType.UnitsGainPromotion] = HandleUnitsGainPromotion,
            [UniqueType.CityStateCanGiftGreatPeople] = HandleCityStateCanGiftGreatPeople,
            [UniqueType.OneTimeGainStat] = HandleOneTimeGainStat,
            [UniqueType.OneTimeGainStatRange] = HandleOneTimeGainStatRange,
            [UniqueType.OneTimeGainPantheon] = HandleOneTimeGainPantheon,
            [UniqueType.OneTimeGainProphet] = HandleOneTimeGainProphet,
            [UniqueType.OneTimeGainTechPercent] = HandleOneTimeGainTechPercent,
            [UniqueType.OneTimeFreeBelief] = HandleOneTimeFreeBelief,
            [UniqueType.OneTimeRevealEntireMap] = HandleOneTimeRevealEntireMap,
            [UniqueType.OneTimeRevealSpecificMapTiles] = HandleOneTimeRevealSpecificMapTiles,
            [UniqueType.OneTimeRevealCrudeMap] = HandleOneTimeRevealCrudeMap,
            [UniqueType.OneTimeTriggerVoting] = HandleOneTimeTriggerVoting,
            [UniqueType.OneTimeGlobalSpiesWhenEnteringEra] = HandleOneTimeGlobalSpiesWhenEnteringEra,
            [UniqueType.OneTimeSpiesLevelUp] = HandleOneTimeSpiesLevelUp,
            [UniqueType.OneTimeGainSpy] = HandleOneTimeGainSpy,
            [UniqueType.OneTimeTakeOverTilesInCity] = HandleOneTimeTakeOverTilesInCity,
            [UniqueType.GainFreeBuildings] = HandleGainFreeBuildings,
            [UniqueType.FreeStatBuildings] = HandleFreeStatBuildings,
            [UniqueType.FreeSpecificBuildings] = HandleFreeSpecificBuildings,
            [UniqueType.RemoveBuilding] = HandleRemoveBuilding,
            [UniqueType.OneTimeSellBuilding] = HandleOneTimeSellBuilding,
            [UniqueType.OneTimeUnitHeal] = HandleOneTimeUnitHeal,
            [UniqueType.OneTimeUnitDamage] = HandleOneTimeUnitDamage,
            [UniqueType.OneTimeUnitGainXP] = HandleOneTimeUnitGainXP,
            [UniqueType.OneTimeUnitGainMovement] = HandleOneTimeUnitGainMovement,
            [UniqueType.OneTimeUnitLoseMovement] = HandleOneTimeUnitGainMovement,
            [UniqueType.OneTimeUnitGainStatus] = HandleOneTimeUnitGainStatus,
            [UniqueType.OneTimeUnitLoseStatus] = HandleOneTimeUnitLoseStatus,
            [UniqueType.OneTimeUnitDestroyed] = HandleOneTimeUnitDestroyed,
            [UniqueType.OneTimeUnitUpgrade] = HandleOneTimeUnitUpgrade,
            [UniqueType.OneTimeUnitSpecialUpgrade] = HandleOneTimeUnitUpgrade,
            [UniqueType.OneTimeUnitGainPromotion] = HandleOneTimeUnitGainPromotion,
            [UniqueType.OneTimeUnitRemovePromotion] = HandleOneTimeUnitRemovePromotion,
            [UniqueType.OneTimeRemoveResourcesFromTile] = HandleOneTimeRemoveResourcesFromTile,
            [UniqueType.OneTimeRemoveImprovementsFromTile] = HandleOneTimeRemoveImprovementsFromTile,
            [UniqueType.OneTimeChangeTerrain] = HandleOneTimeChangeTerrain,
            [UniqueType.OneTimeAddResource] = HandleOneTimeAddResource,
            [UniqueType.OneTimeTakeOverTilesInRadius] = HandleOneTimeTakeOverTilesInRadius,
            [UniqueType.OneTimeUnitGetsName] = HandleOneTimeUnitGetsName,
            [UniqueType.OneTimeGlobalAlert] = HandleOneTimeGlobalAlert,
        }.ToFrozenDictionary();


    private static string NotificationIconOf(Stat stat) => stat switch
    {
        Stat.Production => NotificationIcon.Production,
        Stat.Food => NotificationIcon.Food,
        Stat.Gold => NotificationIcon.Gold,
        Stat.Science => NotificationIcon.Science,
        Stat.Culture => NotificationIcon.Culture,
        Stat.Happiness => NotificationIcon.Happiness,
        Stat.Faith => NotificationIcon.Faith,
        _ => throw new ArgumentOutOfRangeException(nameof(stat)),
    };

    private static IEnumerable<T> DeferredShuffleTake<T>(IEnumerable<T> source, GameRandom random, int count)
    {
        foreach (var item in source.Shuffled(random).Take(count)) yield return item;
    }

    /// <summary>The <c>MusicTrackChooserFlags</c> entry whose name equals <paramref name="name"/> ignoring case, or null.</summary>
    private static string? MusicTrackChooserFlagName(string name)
    {
        foreach (var flag in (string[])["PrefixMustMatch", "SuffixMustMatch", "SlowFade", "PlaySingle", "PlayDefaultFile"])
            if (string.Equals(flag, name, StringComparison.OrdinalIgnoreCase)) return flag;
        return null;
    }

    /// <summary>Kotlin <c>minOrNull()</c> on an Int sequence.</summary>
    private static int? MinOrNull(IEnumerable<int> values)
    {
        int? result = null;
        foreach (var value in values)
            if (result == null || value < result) result = value;
        return result;
    }

    /// <summary>Kotlin <c>minByOrNull</c>: the first element with the smallest selector value.</summary>
    private static T? MinByOrNull<T>(IEnumerable<T> source, Func<T, int> selector) where T : class
    {
        T? best = null;
        int bestValue = 0;
        foreach (var item in source)
        {
            int value = selector(item);
            if (best == null || value < bestValue)
            {
                best = item;
                bestValue = value;
            }
        }
        return best;
    }

    private static string? GetNotificationText(string? notification, string? triggerNotificationText, string effectNotificationText)
    {
        if (!string.IsNullOrEmpty(notification)) return notification;
        if (triggerNotificationText == null) return null;
        var translations = Translations.Translations.Current;
        return translations.TriggerNotificationEffectBeforeCause(translations.CurrentLanguage)
            ? $"{{{effectNotificationText}}}{{ }}{{{triggerNotificationText}}}"
            : $"{{{triggerNotificationText}}}{{ }}{{{effectNotificationText}}}";
    }

    private static Func<bool>? GetOneTimeChangeRiverTriggerFunction(TileClass tile)
    {
        if (!tile.Neighbors.Any(it => it.IsLand && !tile.IsConnectedByRiver(it)))
            return null; // no place for another river
        return () => RiverGenerator.ContinueRiverOn(tile);
    }
}

/// <summary>Kotlin <c>String.toInt()/toIntOrNull()/toFloat()/toFloatOrNull()</c> for the trigger parameters.</summary>
internal static class UniqueTriggerParsing
{
    public static int? ToIntOrNull(this string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
    public static float? ToFloatOrNull(this string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
