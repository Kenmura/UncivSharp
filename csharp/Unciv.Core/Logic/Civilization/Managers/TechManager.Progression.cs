using System.Globalization;
using Unciv.Logic.Map.Tiles;
using Unciv.Models.Ruleset.Tech;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.Ruleset.Unit;
using Unciv.Presentation.Components;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;

namespace Unciv.Logic.Civilization.Managers;

public partial class TechManager
{
    public void AddTechnology(string techName, bool showNotification = true)
    {
        var isNewTech = TechsResearched.Add(techName);

        // this is to avoid concurrent modification problems
        var newTech = GetRuleset().Technologies[techName];
        if (!newTech.IsContinuallyResearchable())
            TechsToResearch.Remove(techName);
        else
            RepeatingTechsResearched++;
        TechsInProgress.Remove(techName);
        ResearchedTechnologies = [.. ResearchedTechnologies, newTech];
        AddTechToTransients(newTech);

        MoveToNewEra(showNotification);
        if (isNewTech) Archaeology.ArchaeologyRules.OnTechResearched(CivInfo, newTech); // B010: the first Archaeology creates the sites

        if (!CivInfo.IsSpectator() && showNotification)
            CivInfo.AddNotification($"Research of [{techName}] has completed!", new TechAction(techName),
                NotificationCategory.General,
                NotificationIcon.Science);
        if (isNewTech)
        {
            CivInfo.AddPopupAlert(new PopupAlert(AlertType.TechResearched, techName));
            GameEventHooks.ResearchCompleted(CivInfo, techName);
        }

        var triggerNotificationText = $"due to researching [{techName}]";
        foreach (var unique in newTech.UniqueObjects)
        {
            if (!unique.IsTriggerable || unique.HasTriggerConditional() || !unique.ConditionalsApply(CivInfo.State))
                continue;
            var multiplier = unique.GetUniqueMultiplier(CivInfo.State); // evaluated once, like repeat()
            for (var i = 0; i < multiplier; i++)
                UniqueTriggerActivation.TriggerUnique(unique, CivInfo, triggerNotificationText: triggerNotificationText);
        }

        CivInfo.ForEachTriggeredUnique(UniqueType.TriggerUponResearch, CivInfo.State, it => newTech.MatchesFilter(it.Params[0], CivInfo.State), unique =>
        {
            UniqueTriggerActivation.TriggerUnique(unique, CivInfo, triggerNotificationText: triggerNotificationText);
        });

        var revealedResources = GetRuleset().TileResources.Values.Where(it => techName == it.RevealedBy).ToList();
        if (CivInfo.PlayerType == PlayerType.Human)
        {
            foreach (var revealedResource in revealedResources)
            {
                CivInfo.GameInfo.NotifyExploredResources(CivInfo, revealedResource, 5);
            }
        }

        UpdateTransientBooleans();

        // In the case of a player hurrying research, this civ's resource availability may now be out of date
        // - e.g. when an owned tile by luck already has an appropriate improvement or when a tech provides a resource.
        // That can be seen on WorldScreenTopBar, so better update.
        CivInfo.Cache.UpdateCivResources();

        foreach (var city in CivInfo.Cities)
        {
            city.ReassignPopulationDeferred();
        }

        ObsoleteOldUnits(techName);

        CivInfo.ForEachMatchingUnique(UniqueType.MayanGainGreatPerson, unique =>
        {
            if (unique.Params[1] == techName)
                CivInfo.AddNotification("You have unlocked [The Long Count]!",
                    new MayaLongCountAction(), NotificationCategory.General, MayaCalendar.NotificationIcon);
        });

        UpdateResearchProgress();
    }

    /// <summary>A variant of kotlin's associateBy that omits null values</summary>
    private static LinkedHashMap<K, V> AssociateByNotNull<T, K, V>(IEnumerable<T> source, Func<T, K> keySelector, Func<T, V?> valueTransform)
        where K : notnull where V : class
    {
        var destination = new LinkedHashMap<K, V>();
        foreach (var element in source)
        {
            var value = valueTransform(element);
            if (value == null) continue;
            destination.Put(keySelector(element), value);
        }
        return destination;
    }

    private void ObsoleteOldUnits(string techName)
    {
        // First build a map with obsoleted units to their (nation-specific) upgrade
        BaseUnit? GetEquivalentUpgradeOrNull(BaseUnit unit)
        {
            var unitUpgradesTo = unit.AutomaticallyUpgradedInProductionToUnitByTech(techName);
            if (unitUpgradesTo == null) return null;
            return CivInfo.GetEquivalentUnit(unitUpgradesTo);
        }
        var obsoleteUnits = AssociateByNotNull(GetRuleset().Units, it => it.Key, it => GetEquivalentUpgradeOrNull(it.Value));
        if (obsoleteUnits.Count == 0) return;

        // Apply each to all cities - and remember which cities had which obsoleted unit
        //  in their construction queues in this Map<String, MutableSet<City>>:
        var unitUpgrades = new LinkedHashMap<string, LinkedHashSet<CityObject>>();
        foreach (var key in obsoleteUnits.Keys) unitUpgrades.Put(key, new LinkedHashSet<CityObject>());
        string? TransformConstruction(string old, CityObject city)
        {
            if (!unitUpgrades.TryGetValue(old, out var entry)) return old;  // Entry OK, not obsolete
            entry.Add(city);  // Remember city has updated its queue
            return obsoleteUnits.TryGetValue(old, out var replacement) ? replacement.Name : null;  // Replacement, or pass through null to remove from queue
        }
        foreach (var city in CivInfo.Cities)
        {
            city.CityConstructions.EditQueue(queue =>
            {
                var transformed = queue.Select(it => TransformConstruction(it, city)).OfType<string>().ToList();
                queue.Clear();
                queue.AddRange(transformed);
            });
        }

        // Add notifications for obsolete units/constructions
        foreach (var (unit, cities) in unitUpgrades)
        {
            if (cities.Count == 0) continue;

            //The validation check happens again while processing start and end of turn,
            //but for mid-turn free tech picks like Oxford University, it should happen immediately
            //so the hammers from the obsolete unit are guaranteed to go to the upgraded unit
            //and players don't think they lost all their production mid turn
            foreach (var city in cities)
                city.CityConstructions.ValidateInProgressConstructions();

            var locationAction = LocationAction.Of(cities.Select(it => it.Location));
            var cityText = cities.Count == 1 ? $"[{cities.First().Name}]"
                : $"[{cities.Count.ToString(CultureInfo.InvariantCulture)}] cities";
            var newUnit = obsoleteUnits.TryGetValue(unit, out var upgraded) ? upgraded.Name : null;
            var text = newUnit == null
                ? $"[{unit}] has become obsolete and was removed from the queue in {cityText}!"
                : $"{cityText} changed production from [{unit}] to [{newUnit}]";
            var icons = newUnit == null
                ? new[] { NotificationIcon.Construction }
                : new[] { unit, NotificationIcon.Construction, newUnit };
            CivInfo.AddNotification(text, locationAction, NotificationCategory.Production, icons);
        }
    }

    private void MoveToNewEra(bool showNotification = true)
    {
        var previousEra = CivInfo.GetEra();
        UpdateEra();
        var currentEra = CivInfo.GetEra();
        if (previousEra == currentEra) return;

        if (showNotification)
        {
            if (!CivInfo.IsSpectator())
                CivInfo.AddNotification(
                    $"You have entered the [{currentEra}]!",
                    NotificationCategory.General,
                    NotificationIcon.Science
                );
            if (CivInfo.IsMajorCiv())
            {
                foreach (var knownCiv in CivInfo.GetKnownCivsWithSpectators())
                {
                    knownCiv.AddNotification(
                        $"[{CivInfo.CivName}] has entered the [{currentEra}]!",
                        NotificationCategory.General, CivInfo.CivName, NotificationIcon.Science
                    );
                }
            }

            foreach (var policyBranch in GetRuleset().PolicyBranches.Values.Where(it =>
                         it.Era == currentEra.Name && CivInfo.Policies.IsAdoptable(it)).ToList())
            {
                if (!CivInfo.IsSpectator())
                    CivInfo.AddNotification(
                        $"[{policyBranch.Name}] policy branch unlocked!",
                        new PolicyAction(policyBranch.Name),
                        NotificationCategory.General,
                        NotificationIcon.Culture
                    );
            }
        }

        var erasPassed = GetRuleset().Eras.Values
            .Where(it => it.EraNumber > previousEra.EraNumber && it.EraNumber <= currentEra.EraNumber)
            .OrderBy(it => it.EraNumber)
            .ToList();

        foreach (var era in erasPassed)
        {
            foreach (var unique in era.UniqueObjects)
            {
                if (unique.HasTriggerConditional() || !unique.ConditionalsApply(CivInfo.State)) continue;
                var multiplier = unique.GetUniqueMultiplier(CivInfo.State); // evaluated once, like repeat()
                for (var i = 0; i < multiplier; i++)
                    UniqueTriggerActivation.TriggerUnique(unique, CivInfo, triggerNotificationText: $"due to entering the [{era.Name}]");
            }

            // B004 (bnw-spec 3.2 FreeSocialPoliciesPerEra, Poland): free policies for every era entered, also when several are skipped at once.
            foreach (var unique in CivInfo.GetMatchingUniques(UniqueType.FreePoliciesPerEra, CivInfo.State))
            {
                CivInfo.Policies.FreePolicies += int.Parse(unique.Params[0], System.Globalization.CultureInfo.InvariantCulture);
                CivInfo.Policies.ShouldOpenPolicyPicker = true;
            }

            CivInfo.ForEachTriggeredUnique(UniqueType.TriggerUponEnteringEra, CivInfo.State, it => it.Params[0] == era.Name, unique =>
            {
                UniqueTriggerActivation.TriggerUnique(unique, CivInfo, triggerNotificationText: $"due to entering the [{era.Name}]");
            });
        }

        // The unfiltered version
        CivInfo.ForEachTriggeredUnique(UniqueType.TriggerUponEnteringEraUnfiltered, CivInfo.State, _ => true, false, unique =>
        {
            UniqueTriggerActivation.TriggerUnique(unique, CivInfo, triggerNotificationText: $"due to entering the [{currentEra.Name}]");
        });
    }

    private void UpdateEra()
    {
        var ruleset = CivInfo.GameInfo.Ruleset;
        if (ruleset.Technologies.Count == 0 || ResearchedTechnologies.Count == 0)
            return;

        // maxByOrNull / minByOrNull: the first element with the extreme value wins
        TechColumn? maxColumn = null;
        foreach (var tech in ResearchedTechnologies)
        {
            var column = tech.Column.NotNull();
            if (maxColumn == null || maxColumn.ColumnNumber < column.ColumnNumber) maxColumn = column;
        }
        var maxEraOfResearchedTechs = maxColumn.NotNull().Era;
        var maxEra = ruleset.Eras[maxEraOfResearchedTechs];

        TechColumn? minColumn = null;
        foreach (var tech in ruleset.Technologies.Values)
        {
            if (ResearchedTechnologies.Contains(tech)) continue;
            var column = tech.Column.NotNull();
            if (minColumn == null || minColumn.ColumnNumber > column.ColumnNumber) minColumn = column;
        }
        if (minColumn == null)
        {
            Era = maxEra;
            return;
        }

        var minEra = ruleset.Eras[minColumn.Era];

        Era = minEra.EraNumber <= maxEra.EraNumber ? maxEra : minEra;
    }

    private void AddTechToTransients(Technology tech)
    {
        TechUniques.AddUniques(tech.UniqueObjects);
    }

    public void SetTransients(Civilization civInfo)
    {
        CivInfo = civInfo;
        ResearchedTechnologies.AddRange(TechsResearched.Select(it => GetRuleset().Technologies[it]).ToList());
        ResearchedTechnologies.ForEach(AddTechToTransients);
        UpdateEra();  // before updateTransientBooleans so era-based conditionals can work
        UpdateTransientBooleans();
    }

    /// <summary>
    /// T208: rebuilds everything <see cref="SetTransients"/> derives from <see cref="TechsResearched"/>, in the same order, so a running game
    /// (which appends in research order, and for barbarians swaps <see cref="TechsResearched"/> wholesale) equals a reloaded one.
    /// </summary>
    public void RebuildTransients()
    {
        ResearchedTechnologies = [];
        TechUniques = new UniqueMap();
        ResearchedTechnologies.AddRange(TechsResearched.Select(it => GetRuleset().Technologies[it]).ToList());
        ResearchedTechnologies.ForEach(AddTechToTransients);
        UpdateEra();
        UpdateTransientBooleans();
    }

    private void UpdateTransientBooleans()
    {
        UnitsCanEmbark = CivInfo.HasUnique(UniqueType.LandUnitEmbarkation);
        var enterOceanUniques = CivInfo.GetMatchingUniques(UniqueType.UnitsMayEnterOcean).ToList();
        AllUnitsCanEnterOcean = enterOceanUniques.Any(it => Constants.All.Contains(it.Params[0]));
        EmbarkedUnitsCanEnterOcean = AllUnitsCanEnterOcean ||
                enterOceanUniques.Any(it => it.Params[0] == Constants.Embarked);
        SpecificUnitsCanEnterOcean = enterOceanUniques.Any(it => !Constants.All.Contains(it.Params[0]) && it.Params[0] != Constants.Embarked);

        MovementSpeedOnRoads = CivInfo.HasUnique(UniqueType.RoadMovementSpeed)
            ? RoadStatus.Road.GetMovementImproved() : RoadStatus.Road.GetMovement();
        RoadsConnectAcrossRivers = CivInfo.HasUnique(UniqueType.RoadsConnectAcrossRivers);
        allTechsAreResearched = CivInfo.GameInfo.Ruleset.Technologies.Values
            .All(it => IsResearched(it.Name) || !CanBeResearched(it.Name));
    }

    public RoadStatus GetBestRoadAvailable()
    {
        var railroadImprovement = GetRuleset().RailroadImprovement;  // May not exist in mods
        if (railroadImprovement != null && (railroadImprovement.TechRequired == null || IsResearched(railroadImprovement.TechRequired))
            && !ImprovementFunctions.GetImprovementBuildingProblems(railroadImprovement, CivInfo.State).Any())
            return RoadStatus.Railroad;

        var roadImprovement = GetRuleset().RoadImprovement;
        if (roadImprovement != null && (roadImprovement.TechRequired == null || IsResearched(roadImprovement.TechRequired))
            && !ImprovementFunctions.GetImprovementBuildingProblems(roadImprovement, CivInfo.State).Any())
            return RoadStatus.Road;

        return RoadStatus.None;
    }

}
