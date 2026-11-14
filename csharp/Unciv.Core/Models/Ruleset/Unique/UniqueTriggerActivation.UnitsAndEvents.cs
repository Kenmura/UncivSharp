using Unciv.Logic.Civilization;
using Unciv.Logic.Civilization.Managers;
using Unciv.Logic.Civilization.Transients;
using Unciv.Logic.Map.MapUnits;
using Unciv.Models.Translations;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;

namespace Unciv.Models.Ruleset.Unique;

public static partial class UniqueTriggerActivation
{
    private static Func<bool>? HandleTriggerEvent(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var unit = ctx.Unit;
        var gameContext = ctx.GameContext;
        var rng = ctx.Rng;
        var ruleset = ctx.Ruleset;
        var eventObj = ruleset.Events.GetValueOrDefault(unique.Params[0]);
        if (eventObj == null) return null;
        var choices = eventObj.GetMatchingChoices(gameContext);
        if (choices == null) return null;
        if (choices.Count == 0) return null;
        if (civInfo.IsAI() || eventObj.Presentation == Event.PresentationType.None)
            return () =>
            {
                var choice = choices.ToList().RandomWeighted(rng, it => ((IHasUniques)it).GetWeightForAiDecision(gameContext));
                return choice.TriggerChoice(civInfo, unit);
            };
        if (eventObj.Presentation is Event.PresentationType.Alert or Event.PresentationType.Floating)
            return () =>
            {
                // See AlertPopup.addEvent for the deserializing of this string to the context
                var eventText = eventObj.Name;
                if (unit != null) eventText += Constants.StringSplitCharacter + "unitId=" + unit.Id;
                civInfo.AddPopupAlert(new PopupAlert(AlertType.Event, eventText));
                return true;
            };
        // Deviation (T171): Kotlin throws NotImplementedError for Floating (todo: park them in a queue in GameInfo).
        // Engine: Floating is queued as an Event PopupAlert like Alert; the client decides how to show it (see EventPresentation).
        // Only Presentation None/Alert/Floating exist, so everything is handled above.
        return null;
    }

    private static Func<bool>? HandleMarkTutorialComplete(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        return () =>
        {
            civInfo.GameInfo.ClientState.AddCompletedTutorialTask(civInfo.CivId, unique.Params[0]);
            return true;
        };
    }

    private static Func<bool>? HandlePlaySound(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        if (civInfo.IsAIOrAutoPlaying()) return null;
        var soundName = unique.Params[0];
        if (soundName.Length == 0) return null;
        var audio = civInfo.GameInfo.ClientState.Audio;
        if (audio == null || !audio.HasSound(soundName)) return null;
        return () =>
        {
            audio.PlaySound(civInfo, soundName);
            return true;
        };
    }

    private static Func<bool>? HandleChooseMusic(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        if (civInfo.IsAIOrAutoPlaying()) return null;
        var prefix = string.Equals(unique.Params[0], "this civ", StringComparison.OrdinalIgnoreCase) ? civInfo.CivId : unique.Params[0];
        var suffixes = unique.Params[1].Split(',').Select(it => it.KotlinTrim()).ToList();
        var flags = unique.Params[1].Split(',')
            .Select(param => MusicTrackChooserFlagName(param.KotlinTrim()))
            .OfType<string>()
            .ToList();
        var audio = civInfo.GameInfo.ClientState.Audio;
        if (audio == null) return null;
        return () =>
        {
            audio.ChooseMusic(prefix, suffixes, flags);
            return true;
        };
    }

    private static Func<bool>? HandleGetLeaderTitle(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        if (!civInfo.IsMajorCiv()) return null;
        return () =>
        {
            civInfo.LeaderTitle = unique.Params[0];
            if (notification != null)
                civInfo.AddNotification(notification, NotificationCategory.Diplomacy, NotificationIcon.Diplomacy);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeFreeUnit(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var unit = ctx.Unit;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var chosenCity = ctx.ChosenCity;
        var ruleset = ctx.Ruleset;
        CityObject? RelevantCity() => ctx.RelevantCity();
        var unitName = unique.Params[0];
        var baseUnit = ruleset.Units.GetValueOrDefault(unitName);
        if (baseUnit == null) return null;
        var civUnit = civInfo.GetEquivalentUnit(baseUnit);
        if (civUnit.IsCityFounder() && civInfo.IsOneCityChallenger())
            return null;

        int? limit = MinOrNull(((IHasUniques)civUnit).GetMatchingUniques(UniqueType.MaxNumberBuildable).Select(it => it.Params[0].ToInt()));
        if (limit != null && limit <= civInfo.Units.GetCivUnits().Count(it => it.Name == civUnit.Name))
            return null;

        bool PlaceUnit()
        {
            MapUnit? placedUnit;
            // Set unit at city if there's an explict city or if there's no tile to set at
            if (RelevantCity() != null || (tile == null && civInfo.Cities.Count > 0))
                placedUnit = (MapUnit?)civInfo.Units.AddUnit(civUnit, chosenCity);
            // Else set the unit at the given tile
            else if (tile != null)
                placedUnit = civInfo.Units.PlaceUnitNearTile(tile.Position, civUnit);
            // Else set unit unit near other units if we have no cities
            else if (civInfo.Units.GetCivUnits().Any())
                placedUnit = civInfo.Units.PlaceUnitNearTile(civInfo.Units.GetCivUnits().First().CurrentTile.Position, civUnit);
            else
                return false;
            if (placedUnit == null) return false;

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"Gained [1] [{civUnit.Name}] unit(s)");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, MapUnitAction.Of(placedUnit), NotificationCategory.Units, placedUnit.Name);
            return true;
        }
        return PlaceUnit;
    }

    private static Func<bool>? HandleOneTimeAmountFreeUnits(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var unit = ctx.Unit;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var chosenCity = ctx.ChosenCity;
        var ruleset = ctx.Ruleset;
        CityObject? RelevantCity() => ctx.RelevantCity();
        var unitName = unique.Params[1];
        var baseUnit = ruleset.Units.GetValueOrDefault(unitName);
        if (baseUnit == null) return null;
        var civUnit = civInfo.GetEquivalentUnit(baseUnit);
        if (civUnit.IsCityFounder() && civInfo.IsOneCityChallenger())
            return null;

        int? limit = MinOrNull(((IHasUniques)civUnit).GetMatchingUniques(UniqueType.MaxNumberBuildable).Select(it => it.Params[0].ToInt()));
        int unitCount = civInfo.Units.GetCivUnits().Count(it => it.Name == civUnit.Name);
        int amountFromTriggerable = unique.Params[0].ToInt();
        int actualAmount =
            limit == null ? amountFromTriggerable
            : unchecked(amountFromTriggerable + unitCount) > limit ? limit.Value - unitCount
            : amountFromTriggerable;

        if (actualAmount <= 0) return null;

        bool PlaceUnits()
        {
            var placedUnits = new List<MapUnit>();
            for (int i = 0; i < actualAmount; i++)
            {
                MapUnit? placedUnit;
                if (RelevantCity() != null || (tile == null && civInfo.Cities.Count > 0))
                    placedUnit = (MapUnit?)civInfo.Units.AddUnit(civUnit, chosenCity);
                else if (tile != null)
                    placedUnit = civInfo.Units.PlaceUnitNearTile(tile.Position, civUnit);
                else if (civInfo.Units.GetCivUnits().Any())
                    placedUnit = civInfo.Units.PlaceUnitNearTile(civInfo.Units.GetCivUnits().First().CurrentTile.Position, civUnit);
                else
                    placedUnit = null;
                if (placedUnit != null) placedUnits.Add(placedUnit);
            }
            if (placedUnits.Count == 0) return false;

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"Gained [{placedUnits.Count}] [{civUnit.Name}] unit(s)");

            if (notificationText != null)
                civInfo.AddNotification(notificationText, MapUnitAction.Of(placedUnits), NotificationCategory.Units, civUnit.Name);
            return true;
        }
        return PlaceUnits;
    }

    private static Func<bool>? HandleOneTimeRebel(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var ruleset = ctx.Ruleset;
        CityObject? RelevantCity() => ctx.RelevantCity();
        var barbarians = civInfo.GameInfo.GetCivilizationOrNull(Constants.Barbarians);
        if (barbarians == null) return null; // Barbs can be deselected as new-game option
        var unitName = unique.Params[0];
        var baseUnit = ruleset.Units.GetValueOrDefault(unitName);
        if (baseUnit == null) return null;
        var civUnit = civInfo.GetEquivalentUnit(baseUnit);
        if (civUnit.IsCityFounder() && civInfo.IsOneCityChallenger())
            return null;

        // Choose a city as the placement anchor: use the relevant city if available, otherwise the first city
        var placementCity = RelevantCity() ?? civInfo.Cities.FirstOrDefault();
        if (placementCity == null) return null;

        bool PlaceUnit()
        {
            var placedUnit = (MapUnit?)civInfo.GameInfo.TileMap.PlaceUnitNearTile(
                placementCity.GetCenterTile().Position, baseUnit, barbarians);
            if (placedUnit == null) return false;

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"[1] [{civUnit.Name}] has rebelled against us!");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, MapUnitAction.Of(placedUnit), NotificationCategory.Units, placedUnit.Name);
            return true;
        }
        return PlaceUnit;
    }

    private static Func<bool>? HandleOneTimeAmountRebels(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var city = ctx.City;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var ruleset = ctx.Ruleset;
        CityObject? RelevantCity() => ctx.RelevantCity();
        var barbarians = civInfo.GameInfo.GetCivilizationOrNull(Constants.Barbarians);
        if (barbarians == null) return null; // Barbs can be deselected as new-game option
        var unitName = unique.Params[1];
        var baseUnit = ruleset.Units.GetValueOrDefault(unitName);
        if (baseUnit == null) return null;
        var civUnit = civInfo.GetEquivalentUnit(baseUnit);
        if (civUnit.IsCityFounder() && civInfo.IsOneCityChallenger())
            return null;

        // Existing limit logic (based on the number of similar units the civ already owns)
        int? limit = MinOrNull(((IHasUniques)civUnit).GetMatchingUniques(UniqueType.MaxNumberBuildable).Select(it => it.Params[0].ToInt()));
        int unitCount = civInfo.Units.GetCivUnits().Count(it => it.Name == civUnit.Name);
        int amountFromTriggerable = unique.Params[0].ToInt();
        int actualAmount =
            limit == null ? amountFromTriggerable
            : unchecked(amountFromTriggerable + unitCount) > limit ? limit.Value - unitCount
            : amountFromTriggerable;

        if (actualAmount <= 0) return null;

        // Choose a city as the placement anchor
        var placementCity = RelevantCity() ?? civInfo.Cities.FirstOrDefault();
        if (placementCity == null) return null;

        bool PlaceUnits()
        {
            var placedUnits = new List<MapUnit>();
            for (int i = 0; i < actualAmount; i++)
            {
                var placedUnit = (MapUnit?)civInfo.GameInfo.TileMap.PlaceUnitNearTile(
                    placementCity.GetCenterTile().Position, baseUnit, barbarians);
                if (placedUnit != null) placedUnits.Add(placedUnit);
            }
            if (placedUnits.Count == 0) return false;

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"[{placedUnits.Count}] [{civUnit.Name}] unit(s) have rebelled against us!");

            if (notificationText != null)
                civInfo.AddNotification(notificationText, MapUnitAction.Of(placedUnits), NotificationCategory.Units, civUnit.Name);
            return true;
        }
        return PlaceUnits;
    }

    private static Func<bool>? HandleOneTimeFreeUnitRuins(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var unit = ctx.Unit;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var ruleset = ctx.Ruleset;
        var unitName = unique.Params[0];
        var baseUnit = ruleset.Units.GetValueOrDefault(unitName);
        if (baseUnit == null) return null;
        var civUnit = civInfo.GetEquivalentUnit(baseUnit);
        if (civUnit.IsCityFounder() && civInfo.IsOneCityChallenger())
        {
            var replacementUnit = ruleset.Units.Values
                .FirstOrDefault(it => ((IHasUniques)it).GetMatchingUniques(UniqueType.BuildImprovements, GameContext.IgnoreConditionalsState)
                    .Any(u => u.Params[0] == "Land"));
            if (replacementUnit == null) return null;
            civUnit = civInfo.GetEquivalentUnit(replacementUnit.Name);
        }

        bool PlaceUnit()
        {
            var placeRng = (unit?.Cache.State ?? civInfo.State).StateBasedRandom("UniqueTriggerActivation.getTriggerFunction");
            var placingTile = tile ?? civInfo.Cities.Random(placeRng).GetCenterTile();
            var placedUnit = civInfo.Units.PlaceUnitNearTile(placingTile.Position, civUnit.Name);
            if (notification != null && placedUnit != null)
            {
                var notificationText = notification.HasPlaceholderParameters()
                    ? notification.FillPlaceholders(unique.Params[0])
                    : notification;
                civInfo.AddNotification(
                    notificationText,
                    new NotificationAction[] { MapUnitAction.Of(placedUnit) }.Concat(LocationAction.Of(tile?.Position)),
                    NotificationCategory.Units,
                    placedUnit.Name);
            }
            return placedUnit != null;
        }

        return PlaceUnit;
    }

    private static Func<bool>? HandleOneTimeFreePolicy(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        // spectators get all techs at start of game, and if (in a mod) a tech gives a free policy, the game gets stuck on the policy picker screen
        if (civInfo.IsSpectator()) return null;

        return () =>
        {
            civInfo.Policies.FreePolicies++;

            var notificationText = GetNotificationText(notification, triggerNotificationText,
                "You may choose a free Policy");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, new PolicyAction(), NotificationCategory.General, NotificationIcon.Culture);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeAmountFreePolicies(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        if (civInfo.IsSpectator()) return null;
        int newFreePolicies = unique.Params[0].ToInt();

        return () =>
        {
            civInfo.Policies.FreePolicies += newFreePolicies;

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                $"You may choose [{newFreePolicies}] free Policies");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, new PolicyAction(), NotificationCategory.General, NotificationIcon.Culture);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeAdoptPolicyOrBelief(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var name = unique.Params[0];
        var policy = civInfo.GameInfo.Ruleset.Policies.GetValueOrDefault(name);
        var belief = civInfo.GameInfo.Ruleset.Beliefs.GetValueOrDefault(name);
        if (policy != null && !civInfo.Policies.IsAdopted(name))
            return () =>
            {
                civInfo.Policies.FreePolicies++;
                civInfo.Policies.Adopt(policy);
                var text = GetNotificationText(notification, triggerNotificationText, $"You gain the [{name}] Policy");
                if (text != null)
                    civInfo.AddNotification(text, new PolicyAction(name), NotificationCategory.General, NotificationIcon.Culture);
                return true;
            };
        if (belief != null && civInfo.ReligionManager.Religion?.HasBelief(name) == false)
            return () =>
            {
                civInfo.ReligionManager.ChooseBeliefs([belief]);
                var text = GetNotificationText(notification, triggerNotificationText, $"You gain the [{name}] Belief");
                if (text != null)
                    civInfo.AddNotification(text, NotificationCategory.Religion, NotificationIcon.Faith);
                return true;
            };
        return null;
    }

    private static Func<bool>? HandleOneTimeRemovePolicy(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var gameContext = ctx.GameContext;
        var policyFilter = unique.Params[0];
        var policiesToRemove = civInfo.Policies
            .GetAdoptedPoliciesMatching(policyFilter, gameContext, forRemoval: true);
        if (!policiesToRemove.Any()) return null;

        return () =>
        {
            foreach (var policy in policiesToRemove)
            {
                civInfo.Policies.RemovePolicy(policy);

                var notificationText = GetNotificationText(
                    notification, triggerNotificationText,
                    $"You lose the [{policy.Name}] Policy");
                if (notificationText != null)
                    civInfo.AddNotification(notificationText, new PolicyAction(policy.Name), NotificationCategory.General, NotificationIcon.Culture);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeRemovePolicyRefund(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        var gameContext = ctx.GameContext;
        var policyFilter = unique.Params[0];
        var policiesToRemove = civInfo.Policies
            .GetAdoptedPoliciesMatching(policyFilter, gameContext, forRemoval: true);
        if (!policiesToRemove.Any()) return null;

        int refundPercentage = unique.Params[1].ToInt();
        var policiesToRemoveMap = civInfo.Policies.GetCultureRefundMap(policiesToRemove, refundPercentage);

        return () =>
        {
            foreach (var policy in policiesToRemoveMap)
            {
                string effectNotificationText;
                if (policy.Value == PolicyManager.FreePolicyMarker)
                {
                    civInfo.Policies.RemovePolicy(policy.Key, assumeWasFree: true);
                    civInfo.Policies.FreePolicies++;
                    effectNotificationText = $"You lose the [{policy.Key.Name}] Policy. A free policy has been refunded";
                }
                else
                {
                    civInfo.Policies.RemovePolicy(policy.Key);
                    civInfo.Policies.AddCulture(policy.Value);
                    effectNotificationText = $"You lose the [{policy.Key.Name}] Policy. [{policy.Value}] Culture has been refunded";
                }
                var notificationText =
                    GetNotificationText(notification, triggerNotificationText, effectNotificationText);
                if (notificationText != null)
                    civInfo.AddNotification(notificationText, new PolicyAction(policy.Key.Name), NotificationCategory.General, NotificationIcon.Culture);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeEnterGoldenAge(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        var triggerNotificationText = ctx.TriggerNotificationText;
        return () =>
        {
            if (unique.Type == UniqueType.OneTimeEnterGoldenAgeTurns) civInfo.GoldenAges.EnterGoldenAge(unique.Params[0].ToInt());
            else civInfo.GoldenAges.EnterGoldenAge();

            var notificationText = GetNotificationText(
                notification, triggerNotificationText,
                "You enter a Golden Age");
            if (notificationText != null)
                civInfo.AddNotification(notificationText, NotificationCategory.General, NotificationIcon.Happiness);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeFreeGreatPerson(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        if (civInfo.IsSpectator()) return null;
        return () =>
        {
            civInfo.GreatPeople.FreeGreatPeople++;
            // Anyone an idea for a good icon?
            if (notification != null)
                civInfo.AddNotification(notification, NotificationCategory.General);

            // NextTurnAutomation.chooseGreatPerson is reached through GameClientState.NextTurnAutomation
            if (civInfo.IsAI() || civInfo.GameInfo.ClientState.IsFullAutoPlayAI)
                civInfo.GameInfo.ClientState.NextTurnAutomation?.ChooseGreatPerson(civInfo);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainPopulation(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        IEnumerable<CityObject> GetApplicableCities(string cityFilter) => ctx.GetApplicableCities(cityFilter);
        var applicableCities = GetApplicableCities(unique.Params[1]);
        if (!applicableCities.Any()) return null;
        return () =>
        {
            foreach (var applicableCity in applicableCities)
                applicableCity.Population.AddPopulation(unique.Params[0].ToInt());
            if (notification != null)
                civInfo.AddNotification(
                    notification,
                    LocationAction.Of(applicableCities.Select(it => it.Location)),
                    NotificationCategory.Cities,
                    NotificationIcon.Population);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGainPopulationRandomCity(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var tileBasedRandom = ctx.TileBasedRandom;
        if (civInfo.Cities.Count == 0) return null;
        return () =>
        {
            var randomCity = civInfo.Cities.Random(tileBasedRandom);
            randomCity.Population.AddPopulation(unique.Params[0].ToInt());
            if (notification != null)
            {
                var notificationText = notification.HasPlaceholderParameters()
                    ? notification.FillPlaceholders(randomCity.Name)
                    : notification;
                civInfo.AddNotification(
                    notificationText,
                    LocationAction.Of(randomCity.Location, tile?.Position),
                    NotificationCategory.Cities,
                    NotificationIcon.Population);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeFreeTech(TriggerContext ctx)
    {
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        if (civInfo.IsSpectator()) return null;
        return () =>
        {
            civInfo.Tech.FreeTechs += 1;
            if (notification != null)
                civInfo.AddNotification(notification, NotificationCategory.General, NotificationIcon.Science);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeAmountFreeTechs(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var notification = ctx.Notification;
        if (civInfo.IsSpectator()) return null;
        return () =>
        {
            civInfo.Tech.FreeTechs += unique.Params[0].ToInt();
            if (notification != null)
                civInfo.AddNotification(notification, NotificationCategory.General, NotificationIcon.Science);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeFreeTechRuins(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        var notification = ctx.Notification;
        var gameContext = ctx.GameContext;
        var tileBasedRandom = ctx.TileBasedRandom;
        var ruleset = ctx.Ruleset;
        var researchableTechsFromThatEra = ruleset.Technologies.Values
            .Where(it => ((IHasUniques)it).Era(ruleset)?.MatchesFilter(unique.Params[1], gameContext) == true
                         && civInfo.Tech.CanBeResearched(it.Name))
            .ToList();
        if (researchableTechsFromThatEra.Count == 0) return null;

        return () =>
        {
            var techsToResearch = researchableTechsFromThatEra.Shuffled(tileBasedRandom)
                .Take(unique.Params[0].ToInt()).ToList();
            foreach (var tech in techsToResearch)
                civInfo.Tech.AddTechnology(tech.Name);

            if (notification != null)
            {
                var notificationText = notification.HasPlaceholderParameters()
                    ? notification.FillPlaceholders(techsToResearch.Select(it => it.Name).ToArray())
                    : notification;
                // Notification click for first tech only, supporting multiple adds little value.
                // Relies on RulesetValidator catching <= 0!
                var notificationActions = LocationAction.Of(tile?.Position).Cast<NotificationAction>()
                    .Append(new TechAction(techsToResearch.First().Name));
                civInfo.AddNotification(
                    notificationText, notificationActions,
                    NotificationCategory.General, NotificationIcon.Science);
            }
            return true;
        };
    }

}
