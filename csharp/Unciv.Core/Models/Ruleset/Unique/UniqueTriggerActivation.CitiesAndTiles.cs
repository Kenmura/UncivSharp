using Unciv.Logic.Civilization;
using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Logic.Civilization.Transients;
using Unciv.Logic.Map.MapGenerator;
using Unciv.Logic.Map.Tiles;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unit;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;

namespace Unciv.Models.Ruleset.Unique;

public static partial class UniqueTriggerActivation
{
    private static Func<bool>? HandleFreeSpecificBuildings(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var ruleset = ctx.Ruleset;
        var building = ruleset.Buildings.GetValueOrDefault(unique.Params[0]);
        if (building == null) return null;
        return () =>
        {
            civInfo.CivConstructions.AddFreeBuildings(building, unique.Params[1].ToInt());
            return true;
        };
    }

    private static Func<bool>? HandleRemoveBuilding(TriggerContext ctx)
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
            {
                var buildingsToRemove = applicableCity.CityConstructions.GetBuiltBuildings()
                    .Where(it => it.MatchesFilter(unique.Params[0], applicableCity.State)).ToList();
                foreach (var building in buildingsToRemove) applicableCity.CityConstructions.RemoveBuilding(building);
            }
            if (notification != null)
                civInfo.AddNotification(
                    notification,
                    LocationAction.Of(applicableCities.Select(it => it.Location)),
                    NotificationCategory.Cities,
                    NotificationIcon.Construction);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeSellBuilding(TriggerContext ctx)
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
            {
                var buildingsToSell = applicableCity.CityConstructions.GetBuiltBuildings()
                    .Where(it => it.MatchesFilter(unique.Params[0], applicableCity.State) && it.IsSellable()).ToList();

                foreach (var building in buildingsToSell) applicableCity.SellBuilding(building);
            }
            if (notification != null)
                civInfo.AddNotification(
                    notification,
                    LocationAction.Of(applicableCities.Select(it => it.Location)),
                    NotificationCategory.Cities,
                    NotificationIcon.Gold);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitHeal(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        if (unit.Health == 100) return null;
        return () =>
        {
            unit.HealBy(unique.Params[1].ToInt());
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, "Heal Instantly");
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitDamage(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        return () =>
        {
            unit.TakeDamage(unique.Params[1].ToInt());
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitGainXP(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        return () =>
        {
            unit.Promotions.XP += unique.Params[1].ToInt();
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, "UnitActionIcons/Promote");
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitGainMovement(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        return () =>
        {
            float movementToUse =
                unique.Type == UniqueType.OneTimeUnitLoseMovement
                    ? unique.Params[1].ToFloat()
                    : -unique.Params[1].ToFloat();
            unit.UseMovementPoints(movementToUse);
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitGainStatus(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        if (!unit.Civ.GameInfo.Ruleset.UnitPromotions.ContainsKey(unique.Params[1])) return null;
        return () =>
        {
            unit.SetStatus(unique.Params[1], unique.Params[2].ToInt());
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, unique.Params[1]);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitLoseStatus(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        if (!unit.HasStatus(unique.Params[1])) return null;
        return () =>
        {
            unit.RemoveStatus(unique.Params[1]);
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, unique.Params[1]);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitDestroyed(TriggerContext ctx)
    {
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        return () =>
        {
            if (notification != null)
                unit.Civ.AddNotification(notification, LocationAction.Of(unit.GetTile().Position), NotificationCategory.Units, unit.Name, "OtherIcons/DisbandUnit");
            unit.Destroy();
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitUpgrade(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        var upgrade = MinByOrNull(
            unit.Upgrade.GetUpgradesOfKind(isSpecial: unique.Type == UniqueType.OneTimeUnitSpecialUpgrade)
                .Where(it => unit.Upgrade.CanPerformUpgrade(it, isFree: true)),
            it => it.UpgradedUnit.Cost);
        if (upgrade == null) return null;
        return () =>
        {
            unit.Upgrade.PerformUpgrade(upgrade, isFree: true);
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, "UnitActionIcons/Upgrade");
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitGainPromotion(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        var promotion = unit.Civ.GameInfo.Ruleset.UnitPromotions.Keys
            .FirstOrDefault(it => it == unique.Params[1]);
        if (promotion == null) return null;
        return () =>
        {
            unit.Promotions.AddPromotion(promotion, true);
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, promotion);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitRemovePromotion(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var unit = ctx.Unit;
        var notification = ctx.Notification;
        if (unit == null) return null;
        var promotion = unit.Civ.GameInfo.Ruleset.UnitPromotions.Keys
            .FirstOrDefault(it => it == unique.Params[1]);
        if (promotion == null) return null;
        return () =>
        {
            unit.Promotions.RemovePromotion(promotion);
            if (notification != null)
                unit.Civ.AddNotification(notification, MapUnitAction.Of(unit), NotificationCategory.Units, unit.Name, promotion);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeRemoveResourcesFromTile(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var tile = ctx.Tile;
        if (tile == null) return null;
        var resource = tile.TileResource;
        if (resource == null) return null;
        var resourceFilter = unique.Params[0];
        if (!resource.MatchesFilter(resourceFilter)) return null;
        return () =>
        {
            tile.TileResource = null;
            tile.ResourceAmount = 0;
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeRemoveImprovementsFromTile(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var tile = ctx.Tile;
        if (tile == null) return null;
        var tileImprovement = tile.TileImprovement;
        if (tileImprovement == null) return null;
        var improvementFilter = unique.Params[0];
        if (!tileImprovement.MatchesFilter(improvementFilter)) return null;
        return () =>
        {
            // Don't remove the improvement if we're just removing the roads
            if (improvementFilter != Constants.AllRoad)
                tile.RemoveImprovement();

            // Remove the roads if desired
            if (improvementFilter == "All" || improvementFilter == Constants.AllRoad)
                tile.RemoveRoad();
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeChangeTerrain(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var tile = ctx.Tile;
        var ruleset = ctx.Ruleset;
        if (tile == null) return null;
        var terrain = ruleset.Terrains.GetValueOrDefault(unique.Params[0]);
        if (terrain == null) return null;
        if (terrain.Name == Constants.River)
            return GetOneTimeChangeRiverTriggerFunction(tile);
        if (terrain.Type.IsBaseTerrain() && tile.BaseTerrain == terrain.Name) return null;
        if (tile.IsCityCenter() && terrain.Type != TerrainType.Land) return null;
        if (tile.TerrainFeatures.Contains(terrain.Name)) return null;
        if (terrain.Type == TerrainType.TerrainFeature && !terrain.OccursOn.Contains(tile.LastTerrain.Name))
            return null;

        return () =>
        {
            switch (terrain.Type)
            {
                case TerrainType.Land:
                case TerrainType.Water:
                    tile.SetBaseTerrain(terrain);
                    break;
                case TerrainType.TerrainFeature:
                    tile.AddTerrainFeature(terrain.Name);
                    break;
                case TerrainType.NaturalWonder:
                    NaturalWonderGenerator.PlaceNaturalWonder(terrain, tile);
                    break;
            }
            TileNormalizer.NormalizeToRuleset(tile, ruleset);
            foreach (var tileUnit in tile.GetUnits().Where(it => !it.Movement.CanPassThrough(tile)).ToList())
                tileUnit.Movement.TeleportToClosestMoveableTile();
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeAddResource(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var tile = ctx.Tile;
        var ruleset = ctx.Ruleset;
        if (tile == null) return null;
        var resourceName = unique.Params[0];
        var resource = ruleset.TileResources.GetValueOrDefault(resourceName);
        if (resource == null) return null;
        if (!resource.TerrainsCanBeFoundOn.Any(it => it == tile.BaseTerrain || tile.TerrainFeatures.Contains(it))) return null;
        return () =>
        {
            // Remove the original resources
            if (tile.TileResource != null)
            {
                tile.TileResource = null;
                tile.ResourceAmount = 0;
            }

            // Same deal as the place resource command
            tile.SetTileResource(resource, majorDeposit: false);
            (tile.GetOwner() as Civilization)?.Cache.UpdateCivResources();
            TileNormalizer.NormalizeToRuleset(tile, ruleset);
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeTakeOverTilesInRadius(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var tile = ctx.Tile;
        if (tile == null) return null;
        if (civInfo.Cities.Count == 0) return null;
        var tileFilter = unique.Params[0];
        int radius = unique.Params[1].ToInt();
        if (radius < 0) return null;
        var tilesToTakeOver = tile.GetTilesInDistance(radius)
            .Where(it => !it.IsCityCenter() && it.MatchesFilter(tileFilter) && !ReferenceEquals(it.GetOwner(), civInfo))
            .ToList();
        if (tilesToTakeOver.Count == 0) return null;

        // Lower is better
        int CityPriority(CityObject c) => c.GetCenterTile().AerialDistanceTo(tile) + (c.IsBeingRazed ? 5 : 0);

        var citiesWithAdjacentTiles = tilesToTakeOver
            .SelectMany(it => it.Neighbors.Append(it))
            .Select(it => it.OwningCity)
            .OfType<CityObject>()
            .Where(it => it.Civ == civInfo)
            .Distinct()
            .ToList();

        var cityToAddTo = MinByOrNull(citiesWithAdjacentTiles, CityPriority)
            ?? MinByOrNull(civInfo.Cities, CityPriority).NotNull();

        return () =>
        {
            var civsToNotify = new List<Civilization>();
            foreach (var tileToTakeOver in tilesToTakeOver)
            {
                var otherCiv = tileToTakeOver.GetOwner() as Civilization;
                if (otherCiv != null)
                {
                    // decrease relations for -10 pt/tile
                    otherCiv.GetDiplomacyManagerOrMeet(civInfo).AddModifier(DiplomaticModifiers.StealingTerritory, -10f);
                    if (!civsToNotify.Contains(otherCiv)) civsToNotify.Add(otherCiv);
                }
                // check if civ has stolen a tile from a citystate
                if (otherCiv != null && otherCiv.IsCityState)
                {
                    // create this varibale diplomacyCityState for more readability
                    var diplomacyCityState = otherCiv.GetDiplomacyManagerOrMeet(civInfo);
                    diplomacyCityState.AddInfluence(-15f);

                    if (!diplomacyCityState.HasFlag(DiplomacyFlags.TilesStolen))
                    {
                        civInfo.AddPopupAlert(new PopupAlert(AlertType.TilesStolen, otherCiv.CivId));
                        diplomacyCityState.SetFlag(DiplomacyFlags.TilesStolen, 1);
                    }
                }
                cityToAddTo.Expansion.TakeOwnership(tileToTakeOver);
            }

            foreach (var otherCiv in civsToNotify)
                otherCiv.AddNotification($"Your territory has been stolen by [{civInfo}]!",
                    tile.Position, NotificationCategory.Cities, civInfo.CivName, NotificationIcon.War);

            return true;
        };
    }

    private static Func<bool>? HandleOneTimeUnitGetsName(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var unit = ctx.Unit;
        var rng = ctx.Rng;
        if (unit == null) return null;
        var unitNameGroup = unique.Params[1];
        // Get both the name and associated unique for the unit name
        var pair = unit.Civ.GameInfo.Ruleset.UnitNameGroups.Values.Where(it =>
                // Get all unit name groups that match the name or a tag
                it.Name == unitNameGroup || ((IHasUniques)it).HasTagUnique(unitNameGroup, unit.Cache.State))
            .Where(it =>
                // Validate that it's available
                !((IHasUniques)it).GetMatchingUniques(UniqueType.OnlyAvailable, GameContext.IgnoreConditionalsState)
                    .Any(u => !u.ConditionalsApply(unit.Cache.State))
                && !((IHasUniques)it).GetMatchingUniques(UniqueType.Unavailable, GameContext.IgnoreConditionalsState)
                    .Any(u => u.ConditionalsApply(unit.Cache.State)))
            .SelectMany(group =>
                // Grab only names that haven't been taken
                group.UnitNames.Where(name => !civInfo.GameInfo.UnitNamesTaken.Contains(name))
                    // Make a pair of the name and the group instance
                    .Select(it => (Name: it, Group: group)))
            // JUSTIFY: T052 Kotlin shuffles with the unseeded Random.Default; the engine has no unseeded RNG, so the state-based rng is used
            .ToList().Shuffled(rng).Select(it => ((string Name, UnitNameGroup Group)?)it).FirstOrDefault();
        if (pair == null) return null;

        return () =>
        {
            unit.InstanceName = pair.Value.Name;
            civInfo.GameInfo.UnitNamesTaken.Add(pair.Value.Name);
            unit.Promotions.AddPromotion(pair.Value.Name, true);
            foreach (var groupUnique in pair.Value.Group.UniqueObjects)
            {
                if (groupUnique.IsTriggerable && !groupUnique.HasTriggerConditional() && groupUnique.ConditionalsApply(unit.Cache.State))
                    TriggerUnique(groupUnique, unit);
            }
            return true;
        };
    }

    private static Func<bool>? HandleOneTimeGlobalAlert(TriggerContext ctx)
    {
        var unique = ctx.Unique;
        var civInfo = ctx.CivInfo;
        var triggerNotificationText = ctx.TriggerNotificationText;
        if (triggerNotificationText == null) return null;
        var alertText = unique.Params[0];
        return () => UniqueTriggerExecutors.TriggerGlobalAlerts(civInfo, alertText, triggerNotificationText);
    }

}
