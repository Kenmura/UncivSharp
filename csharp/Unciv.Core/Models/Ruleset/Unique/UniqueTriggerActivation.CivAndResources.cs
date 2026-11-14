using Unciv.Logic.Civilization;
using Unciv.Logic.Civilization.Managers;
using Unciv.Logic.Civilization.Transients;
using Unciv.Logic.Map;
using Unciv.Logic.Map.MapUnits;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.StatTypes;
using Unciv.Models.Translations;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;
using TileClass = Unciv.Logic.Map.Tiles.Tile;

namespace Unciv.Models.Ruleset.Unique;

public static partial class UniqueTriggerActivation
{
    private static Func<bool>? HandleOneTimeDiscoverTech(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var techName = unique.Params[0];
        if (civInfo.Tech.IsResearched(techName)) return null;

        return () =>
        {
            civInfo.Tech.AddTechnology(techName);
            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"You have discovered the secrets of [{techName}]");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, new TechAction(techName), NotificationCategory.General, NotificationIcon.Science);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeProvideResources(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var ruleset = ctx.Ruleset;
        var resourceName = unique.Params[1];
        var resource = ruleset.TileResources.GetValueOrDefault(resourceName);
        if (resource == null) return null;
        if (!resource.IsStockpiled) return null;

        return () =>
        {
            int amount = unique.Params[0].ToInt();
            if (city != null) city.GainStockpiledResource(resource, amount);
            else civInfo.GainStockpiledResource(resource, amount);

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"You have gained [{amount}] [{resourceName}]");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, NotificationCategory.General, resourceName);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeConsumeResources(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var ruleset = ctx.Ruleset;
        var resourceName = unique.Params[1];
        var resource = ruleset.TileResources.GetValueOrDefault(resourceName);
        if (resource == null) return null;
        if (!resource.IsStockpiled) return null;

        return () =>
        {
            int amount = unique.Params[0].ToInt();
            if (city != null) city.GainStockpiledResource(resource, -amount);
            else civInfo.GainStockpiledResource(resource, -amount);

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"You have lost [{amount}] [{resourceName}]");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, NotificationCategory.General, resourceName);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainResource(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var ruleset = ctx.Ruleset;
        var resourceName = unique.Params[1];
        var resource = ruleset.GetGameResource(resourceName);
        if (resource == null) return null;
        if (resource is TileResource { IsStockpiled: false }) return null;

        return () =>
        {
            int amount = unique.Params[0].ToInt();
            if (unique.IsModifiedByGameSpeed())
            {
                amount = resource is Stat stat
                    ? KotlinMath.RoundToInt(amount * civInfo.GameInfo.Speed.StatCostModifiers[stat])
                    : KotlinMath.RoundToInt(amount * civInfo.GameInfo.Speed.Modifier);
            }
            if (city != null) city.AddGameResource(resource, amount);
            else civInfo.AddGameResource(resource, amount);

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"You have gained [{amount}] [{resourceName}]");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, NotificationCategory.General, resourceName);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeSetStockpile(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var ruleset = ctx.Ruleset;
        var resourceName = unique.Params[0];
        var resource = ruleset.GetGameResource(resourceName);
        if (resource == null) return null;
        if (resource is TileResource { IsStockpiled: false }) return null;

        var countableContext = new GameContext(civInfo, city);
        int? countableResult = Countables.GetCountableAmount(unique.Params[1], countableContext);
        if (countableResult == null) return null;

        return () =>
        {
            int amountRequired = countableResult.Value;
            if (unique.IsModifiedByGameSpeed())
            {
                amountRequired = resource is Stat stat
                    ? KotlinMath.RoundToInt(amountRequired * civInfo.GameInfo.Speed.StatCostModifiers[stat])
                    : KotlinMath.RoundToInt(amountRequired * civInfo.GameInfo.Speed.Modifier);
            }
            if (city != null)
            {
                int currentAmount = city.GetGameResource(resource);
                int missingAmount = amountRequired - currentAmount;
                city.AddGameResource(resource, missingAmount);
            }
            else
            {
                int currentAmount = civInfo.GetGameResource(resource);
                int missingAmount = amountRequired - currentAmount;
                civInfo.AddGameResource(resource, missingAmount);
            }

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"[{resourceName}] has been set to [{amountRequired}]");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, NotificationCategory.General, resourceName);
            return true;
        };
    }

    private static Func<bool>? HandleUnitsGainPromotion(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var notification = ctx.Notification;
        var ruleset = ctx.Ruleset;
        {
            var filter = unique.Params[0];
            var promotionName = unique.Params[1];
            var promotion = ruleset.UnitPromotions.GetValueOrDefault(promotionName);
            if (promotion == null) return null;

            var unitsToPromote = civInfo.Units.GetCivUnits()
                .Where(it => it.MatchesFilter(filter) && (promotion.UnitTypes.Contains(it.Type.Name) || promotion.UnitTypes.Count == 0))
                .ToList();
            if (unitsToPromote.Count == 0) return null;

            return () =>
            {
                var promotedUnits = new List<MapUnit>();
                foreach (var civUnit in unitsToPromote)
                {
                    if (civUnit.Promotions.Promotions.Contains(promotionName)) continue;
                    civUnit.Promotions.AddPromotion(promotionName, isFree: true);
                    promotedUnits.Add(civUnit);
                }

                if (notification != null)
                {
                    civInfo.AddNotification(
                        notification,
                        MapUnitAction.Of(promotedUnits),
                        NotificationCategory.Units,
                        promotionName);
                }
                return true;
            };
        }

        // The mechanics for granting great people are wonky, but basically the following happens:
        // Based on the game speed, a timer with some amount of turns is set, 40 on regular speed
        // Every turn, 1 is subtracted from this timer, as long as you have at least 1 city state ally
        // So no, the number of city-state allies does not matter for this. You have a global timer for all of them combined.
        // If the timer reaches the amount of city-state allies you have (or 10, whichever is lower), it is reset.
        // You will then receive a random great person from a random city-state you are allied to
        // The very first time after acquiring this policy, the timer is set to half of its normal value
        // Note that the way this is implemented now, this unique does NOT stack
    }

    private static Func<bool>? HandleCityStateCanGiftGreatPeople(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        return () =>
        {
            civInfo.AddFlag(
                CivFlags.CityStateGreatPersonGift.ToString(),
                civInfo.CityStateFunctions.TurnsForGreatPersonFromCityState() / 2);
            if (notification != null)
                civInfo.AddNotification(notification, NotificationCategory.Diplomacy, NotificationIcon.CityState);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainStat(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var stat = StatExtensions.SafeValueOf(unique.Params[1]);
        if (stat == null) return null;

        if (!StatExtensions.StatsWithCivWideField.Contains(stat.Value)
            || unique.Params[0].ToIntOrNull() == null)
            return null;
        var statValue = stat.Value;

        return () =>
        {
            int statAmount = unique.Params[0].ToInt();
            if (unique.IsModifiedByGameSpeed()) statAmount = KotlinMath.RoundToInt(statAmount * civInfo.GameInfo.Speed.StatCostModifiers[statValue]);

            var stats = new Stats().Add(statValue, statAmount);
            civInfo.AddStats(stats);

            var filledNotification = notification != null && notification.HasPlaceholderParameters()
                ? notification.FillPlaceholders(statAmount.Tr())
                : notification;

            var notificationText = GetNotificationText(
                filledNotification, triggerNotificationText,
                $"Gained [{stats.ToStringForNotifications()}]");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, LocationAction.Of(tile?.Position), NotificationCategory.General, NotificationIconOf(statValue));
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainStatRange(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var tileBasedRandom = ctx.TileBasedRandom;
        var stat = StatExtensions.SafeValueOf(unique.Params[2]);
        if (stat == null) return null;

        if (!StatExtensions.StatsWithCivWideField.Contains(stat.Value)
            || unique.Params[0].ToIntOrNull() == null
            || unique.Params[1].ToIntOrNull() == null)
            return null;
        var statValue = stat.Value;

        int firstValue = unique.Params[0].ToInt();
        int secondValue = unique.Params[1].ToInt();
        int randomValue = new IntRange(Math.Min(firstValue, secondValue), Math.Max(firstValue, secondValue)).Random(tileBasedRandom);
        int finalStatAmount = unique.IsModifiedByGameSpeed()
            ? KotlinMath.RoundToInt(randomValue * civInfo.GameInfo.Speed.StatCostModifiers[statValue])
            : randomValue;

        return () =>
        {
            var stats = new Stats().Add(statValue, finalStatAmount);
            civInfo.AddStats(stats);

            var filledNotification = notification != null && notification.HasPlaceholderParameters()
                ? notification.FillPlaceholders(finalStatAmount.Tr())
                : notification;

            var notificationText = GetNotificationText(
                filledNotification, triggerNotificationText,
                $"Gained [{stats.ToStringForNotifications()}]");

            if (notificationText != null)
                civInfo.AddNotification(notificationText, LocationAction.Of(tile?.Position), NotificationCategory.General, NotificationIconOf(statValue));
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainPantheon(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        if (civInfo.ReligionManager.ReligionState != ReligionState.None) return null;
        int gainedFaith = civInfo.ReligionManager.FaithForPantheon(2);
        if (gainedFaith == 0) return null;

        return () =>
        {
            civInfo.AddStat(Stat.Faith, gainedFaith);

            if (notification != null)
            {
                var notificationText = notification.HasPlaceholderParameters()
                    ? notification.FillPlaceholders(gainedFaith.Tr())
                    : notification;
                civInfo.AddNotification(notificationText, LocationAction.Of(tile?.Position), NotificationCategory.Religion, NotificationIcon.Faith);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainProphet(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        if (civInfo.ReligionManager.GetGreatProphetEquivalent() == null) return null;
        int gainedFaith =
            (int)(civInfo.ReligionManager.FaithForNextGreatProphet() * (unique.Params[0].ToFloat() / 100f));
        if (gainedFaith == 0) return null;

        return () =>
        {
            civInfo.AddStat(Stat.Faith, gainedFaith);

            if (notification != null)
            {
                var notificationText = notification.HasPlaceholderParameters()
                    ? notification.FillPlaceholders(gainedFaith.Tr())
                    : notification;
                civInfo.AddNotification(notificationText, LocationAction.Of(tile?.Position), NotificationCategory.Religion, NotificationIcon.Faith);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainTechPercent(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var tech = unique.Params[1];
        float? amount = unique.Params[0].ToFloatOrNull();
        if (amount == null || !civInfo.GameInfo.Ruleset.Technologies.ContainsKey(tech) || civInfo.Tech.IsResearched(tech)) return null;
        int scienceGain = KotlinMath.RoundToInt(civInfo.Tech.CostOfTech(tech) * amount.Value / 100);
        if (scienceGain == 0) return null;

        return () =>
        {
            if (!civInfo.Tech.TechsInProgress.TryGetValue(tech, out int inProgress))
                civInfo.Tech.TechsInProgress[tech] = scienceGain;
            else civInfo.Tech.TechsInProgress[tech] = unchecked(inProgress + scienceGain);

            var filledNotification = notification != null && notification.HasPlaceholderParameters()
                ? notification.FillPlaceholders(scienceGain.Tr())
                : notification;

            var notificationText = GetNotificationText(
                filledNotification, triggerNotificationText,
                $"You have gained [{scienceGain}] Science towards researching [{tech}]");

            civInfo.AddNotification(notificationText.NotNull(), LocationAction.Of(tile?.Position), NotificationCategory.General, NotificationIcon.Science);

            return true;
        };
    }

    private static Func<bool>? HandleOneTimeFreeBelief(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        if (!civInfo.IsMajorCiv()) return null;
        var beliefType = Enum.Parse<BeliefType>(unique.Params[0]);
        var religionManager = civInfo.ReligionManager;
        if (beliefType != BeliefType.Pantheon && beliefType != BeliefType.Any
            && religionManager.ReligionState <= ReligionState.Pantheon)
            return null; // situation where we're trying to add a formal religion belief to a civ that hasn't founded a religion
        if (religionManager.NumberOfBeliefsAvailable(beliefType) == 0)
            return null; // no more available beliefs of this type

        return () =>
        {
            var religionIcon = beliefType.ToString();
            if (beliefType == BeliefType.Any && religionManager.ReligionState <= ReligionState.Pantheon)
            {
                religionManager.FreeBeliefs.Add(BeliefType.Pantheon.ToString(), 1); // add pantheon instead of any type
                religionIcon = BeliefType.Pantheon.ToString();
            }
            else
                religionManager.FreeBeliefs.Add(beliefType.ToString(), 1);

            if (notification != null)
            {
                civInfo.AddNotification(
                    notification,
                    NotificationCategory.Religion,
                    NotificationIcon.Faith,
                    $"ReligionIcons/{religionIcon}");
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeRevealEntireMap(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        return () =>
        {
            if (notification != null)
                civInfo.AddNotification(notification, LocationAction.Of(tile?.Position), NotificationCategory.General, NotificationIcon.Scout);
            var tiles = civInfo.GameInfo.TileMap.Values;
            foreach (var t in tiles) { t.SetExplored(civInfo, true); civInfo.SetLastSeenOwner(t.Position, t.GetOwner()?.CivId); } // T303: a revealed map comes with its borders
            if (!civInfo.IsBarbarian)
                civInfo.Cache.DiscoverNaturalWonders(tiles);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeRevealSpecificMapTiles(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var tileBasedRandom = ctx.TileBasedRandom;
        if (tile == null) return null;

        // "Reveal up to [amount/'all'] [tileFilter] within a [amount] tile radius"
        var amount = unique.Params[0];
        var filter = unique.Params[1];
        int radius = unique.Params[2].ToInt();

        bool isAll = Constants.All.Contains(amount);
        var positions = new List<HexCoord>();

        IEnumerable<TileClass> explorableTiles = tile.GetTilesInDistance(radius)
            .Where(it => !it.IsExplored(civInfo) && it.MatchesFilter(filter));

        if (!explorableTiles.Any())
            return null;

        if (!isAll)
        {
            // Kotlin Sequence.shuffled(...).take(n) is lazy: the draw and the explored filter run when the function executes
            var candidates = explorableTiles;
            int takeCount = amount.ToInt();
            explorableTiles = DeferredShuffleTake(candidates, tileBasedRandom, takeCount);
        }

        return () =>
        {
            foreach (var explorableTile in explorableTiles)
            {
                explorableTile.SetExplored(civInfo, true);
                civInfo.SetLastSeenImprovement(explorableTile.Position, explorableTile.Improvement); civInfo.SetLastSeenOwner(explorableTile.Position, explorableTile.GetOwner()?.CivId); // T303: revealed ground comes with its border
                positions.Add(explorableTile.Position);
            }

            if (notification != null)
            {
                civInfo.AddNotification(
                    notification,
                    LocationAction.Of(positions),
                    NotificationCategory.War,
                    unique.Params[1] == Constants.BarbarianEncampment
                        ? NotificationIcon.Barbarians : NotificationIcon.Scout);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeRevealCrudeMap(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var tileBasedRandom = ctx.TileBasedRandom;
        if (tile == null) return null;

        // "From a randomly chosen tile [amount] tiles away from the ruins,
        // reveal tiles up to [amount] tiles away with [amount]% chance"
        int distance = unique.Params[0].ToInt();
        int radius = unique.Params[1].ToInt();
        float chance = unique.Params[2].ToFloat() / 100f;

        var revealCenter = tile.GetTilesAtDistance(distance)
            .Where(it => !it.IsExplored(civInfo))
            .ToList()
            .RandomOrNull(tileBasedRandom);
        if (revealCenter == null) return null;

        return () =>
        {
            revealCenter.ForEachTileInDistance(radius, _ => tileBasedRandom.NextFloat() < chance, it => it.SetExplored(civInfo, true));
            civInfo.Cache.UpdateViewableTiles();
            if (notification != null)
                civInfo.AddNotification(
                    notification,
                    tile.Position,
                    NotificationCategory.General,
                    NotificationIcon.Ruins);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeTriggerVoting(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        return () =>
        {
            foreach (var civ in civInfo.GameInfo.Civilizations)
                if (!civ.IsBarbarian && !civ.IsSpectator())
                    civ.AddFlag(
                        CivFlags.TurnsTillNextDiplomaticVote.ToString(),
                        civInfo.GetTurnsBetweenDiplomaticVotes());
            if (notification != null)
                civInfo.AddNotification(notification, NotificationCategory.General, NotificationIcon.Diplomacy);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGlobalSpiesWhenEnteringEra(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        if (!civInfo.IsMajorCiv()) return null;
        if (!civInfo.GameInfo.IsEspionageEnabled()) return null;

        return () =>
        {
            var currentEra = civInfo.GetEra().Name;
            foreach (var otherCiv in civInfo.GameInfo.GetAliveMajorCivs())
            {
                if (!otherCiv.EspionageManager.ErasSpyEarnedFor.Contains(currentEra))
                {
                    var spy = otherCiv.EspionageManager.AddSpy();
                    otherCiv.EspionageManager.ErasSpyEarnedFor.Add(currentEra);
                    if (otherCiv == civInfo || otherCiv.Knows(civInfo))
                        // We don't tell which civilization entered the new era, as that is done in the notification directly above this one
                        spy.AddNotification($"We have recruited [{spy.Name}] as a spy!");
                    else
                        spy.AddNotification($"After [an unknown civilization] entered the [{currentEra}], we have recruited [{spy.Name}] as a spy!");
                }
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeSpiesLevelUp(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        if (!civInfo.IsMajorCiv()) return null;
        if (!civInfo.GameInfo.IsEspionageEnabled()) return null;

        return () =>
        {
            foreach (var spy in civInfo.EspionageManager.SpyList.ToList())
                spy.LevelUpSpy(unique.Params[0].ToInt());
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainSpy(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        if (!civInfo.IsMajorCiv()) return null;
        if (!civInfo.GameInfo.IsEspionageEnabled()) return null;

        return () =>
        {
            var spy = civInfo.EspionageManager.AddSpy();
            spy.AddNotification($"We have recruited [{spy.Name}] as a spy!");
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeTakeOverTilesInCity(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        IEnumerable<CityObject> GetApplicableCities(string cityFilter) => ctx.GetApplicableCities(cityFilter);
        var applicableCities = GetApplicableCities(unique.Params[1]);
        if (!applicableCities.Any()) return null;
        if (!applicableCities.Any(it => it.Expansion.ChooseNewTileToOwn() != null)) return null;

        return () =>
        {
            int positiveAmount = unique.Params[0].ToInt();
            foreach (var applicableCity in applicableCities)
            {
                for (int i = 1; i <= positiveAmount; i++)
                {
                    var tileToOwn = applicableCity.Expansion.ChooseNewTileToOwn();
                    if (tileToOwn == null) break;
                    applicableCity.Expansion.TakeOwnership(tileToOwn);
                }
            }
            if (notification != null)
                civInfo.AddNotification(notification, LocationAction.Of(applicableCities.Select(it => it.Location)), NotificationCategory.Cities, NotificationIcon.City);
            return true;
        };
    }

    private static Func<bool>? HandleGainFreeBuildings(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        IEnumerable<CityObject> GetApplicableCities(string cityFilter) => ctx.GetApplicableCities(cityFilter);
        var freeBuilding = civInfo.GetEquivalentBuilding(unique.Params[0]);
        var applicableCities = GetApplicableCities(unique.Params[1]);
        if (!applicableCities.Any()) return null;

        return () =>
        {
            foreach (var applicableCity in applicableCities)
            {
                applicableCity.CityConstructions.FreeBuildingsProvidedFromThisCity.AddToMapOfSets(applicableCity.Id, freeBuilding.Name);

                if (applicableCity.CityConstructions.ContainsBuildingOrEquivalent(freeBuilding.Name)) continue;
                applicableCity.CityConstructions.CompleteConstruction(freeBuilding);
            }
            return true;
        };
    }

    private static Func<bool>? HandleFreeStatBuildings(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var stat = StatExtensions.SafeValueOf(unique.Params[0]);
        if (stat == null) return null;
        return () =>
        {
            civInfo.CivConstructions.AddFreeStatBuildings(stat.Value, unique.Params[1].ToInt());
            return true;
        };
    }

}
