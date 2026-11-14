using Unciv.Logic.Civilization;
using Unciv.Logic.Map;
using Unciv.Models;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.Ruleset.Unit;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using DifficultyObject = Unciv.Models.Ruleset.Nation.Difficulty;
using TileClass = Unciv.Logic.Map.Tiles.Tile;
using TileResourceObject = Unciv.Models.Ruleset.Tile.TileResource;

namespace Unciv.Logic;

public partial class GameInfo
{
    public bool NotifyExploredResources(Civilization.Civilization civInfo, string resourceName, int maxDistance = int.MaxValue, Func<TileClass, bool>? filter = null)
    {
        var notification = GetExploredResourcesNotification(civInfo, resourceName, maxDistance, filter);
        if (notification == null) return false;
        civInfo.Notifications.Add(notification);
        return true;
    }

    /// <inheritdoc cref="NotifyExploredResources(Civilization.Civilization, string, int, Func{TileClass, bool})"/>
    public bool NotifyExploredResources(Civilization.Civilization civInfo, TileResourceObject resource, int maxDistance = int.MaxValue, Func<TileClass, bool>? filter = null)
    {
        var notification = GetExploredResourcesNotification(civInfo, resource, maxDistance, filter);
        if (notification == null) return false;
        civInfo.Notifications.Add(notification);
        return true;
    }

    /// <summary>Generate a notification pointing out resources. Only researched Resources are considered.
    /// maxDistance: from next City, default removes distance limitation; filter: optional tile filter predicate, e.g. to exclude foreign territory.
    /// </summary>
    /// <returns><c>null</c> if no resources were found, otherwise a Notification instance.</returns>
    public Notification? GetExploredResourcesNotification(Civilization.Civilization civ, string resourceName, int maxDistance = int.MaxValue, Func<TileClass, bool>? filter = null) =>
        GetExploredResourcesNotification(civ, Ruleset.TileResources.GetValueOrDefault(resourceName), maxDistance, filter);

    /// <inheritdoc cref="GetExploredResourcesNotification(Civilization.Civilization, string, int, Func{TileClass, bool})"/>
    public Notification? GetExploredResourcesNotification(Civilization.Civilization civ, TileResourceObject? resource, int maxDistance = int.MaxValue, Func<TileClass, bool>? filter = null)
    {
        if (!civ.CanSeeResource(resource)) return null;
        resource = resource.NotNull();
        filter ??= _ => true;

        // Include your city-state allies' cities with your own for the purpose of showing the closest city
        var relevantCities = civ.Cities.Concat(
            civ.GetKnownCivs()
                .Where(it => it.IsCityState && it.AllyCiv == civ)
                .SelectMany(it => it.Cities)).ToList();

        // All sources of the resource on the map, using a city-state's capital center tile for the CityStateOnlyResource types
        IEnumerable<TileClass> exploredRevealTiles =
            ((IHasUniques)resource).HasUnique(UniqueType.CityStateOnlyResource)
                // Look for matching mercantile CS centers
                ? GetAliveCityStates()
                    .Where(it => it.CityStateResource == resource.Name)
                    .Select(it => it.GetCapital().NotNull().GetCenterTile())
                : TileMap.Values.Where(it => it.TileResource == resource);

        // Apply all filters to the above collection and sort them by distance to closest city
        var exploredRevealInfo = exploredRevealTiles
            .Where(civ.HasExplored)
            .SelectMany(tile => relevantCities
                // build a full cross join all revealed tiles * civ's cities (should rarely surpass a few hundred)
                // cache distance for each pair as sort will call it ~ 2n log n times
                // should still be cheaper than looking up 'the' closest city per reveal tile before sorting
                .Select(city => new CityTileAndDistance(city, tile, tile.AerialDistanceTo(city.GetCenterTile()))))
            .Where(it => it.Distance <= maxDistance && filter(it.Tile))
            // We still want to report tiles on our own territory before those of our cs-allies
            .OrderBy(it => it.City.Civ != civ).ThenBy(it => it.Distance)
            .DistinctBy(it => it.Tile)
            .ToList();

        var chosenCity = exploredRevealInfo.FirstOrDefault()?.City;
        if (chosenCity == null) return null;
        var positions = exploredRevealInfo
            // re-sort to a more pleasant display order
            .OrderBy(it => it.Tile.AerialDistanceTo(chosenCity.GetCenterTile()))
            .Select(it => it.Tile.Position)
            .ToList();

        var positionsCount = positions.Count;
        var text = positionsCount == 1
            ? $"[{resource.Name}] revealed near [{chosenCity.Name}]"
            : $"[{positionsCount}] sources of [{resource.Name}] revealed, e.g. near [{chosenCity.Name}]";

        return new Notification(text, [$"ResourceIcons/{resource.Name}"], LocationAction.Of(positions), Notification.NotificationCategory.General);
    }

    private sealed record CityTileAndDistance(CityObject City, TileClass Tile, int Distance);

    public GameInfoPreview AsPreview() => new(this);

    #region State changing functions

    // T200: which civs' turns have begun. Simultaneous rounds must not call SetTransients mid-round, it would drop the other civs' started flags.
    private void RestoreTurnStartedCivs()
    {
        if (TurnState is { Mode: TurnMode.Simultaneous } turnState) // the saved list says whose turn has begun
        {
            turnStartedCivs.Clear();
            turnStartedCivs.UnionWith(turnState.Started);
        }
        else if (pendingPlayerIndex == null) // a saved or new game always waits for its current player's input
        {
            turnStartedCivs.Clear();
            turnStartedCivs.Add(CurrentPlayer);
        }
    }

    /// <summary>
    /// All cross-game data which needs to be altered (e.g. when removing or changing a name of a building/tech)
    /// will be done here, and not in Civilization.setTransients or City
    /// </summary>
    public void SetTransients()
    {
        TileMap.GameInfo = this;

        // [TEMPORARY] Convert old saves to newer ones by moving base rulesets from the mod list to the base ruleset field
        ConvertOldSavesToNewSaves();

        // Cater for the mad modder using trailing '-' in their repo name - convert the mods list so
        // it requires our new, Windows-safe local name (no trailing blanks)
        foreach (var (oldName, newName) in GameParameters.Mods.Select(it => (it, RepoNameToFolderName(it, onlyOuterBlanks: true))).ToList())
        {
            if (newName == oldName) continue;
            GameParameters.Mods.Remove(oldName);
            GameParameters.Mods.Add(newName);
        }

        // Try to fix mods that are displayed in save with - but when downloaded it's replaced with a space
        if (!RulesetCache.Current.ContainsKey(GameParameters.BaseRuleset) && RulesetCache.Current.ContainsKey(GameParameters.BaseRuleset.Replace("-", " ")))
            GameParameters.BaseRuleset = GameParameters.BaseRuleset.Replace("-", " ");

        foreach (var mod in GameParameters.Mods.ToList())
        {
            if (!RulesetCache.Current.ContainsKey(mod) && RulesetCache.Current.ContainsKey(mod.Replace("-", " ")))
            {
                GameParameters.Mods.Remove(mod);
                GameParameters.Mods.Add(mod.Replace("-", " "));
            }
        }

        Ruleset = RulesetCache.Current.GetComplexRuleset(GameParameters);

        // any mod the saved game lists that is currently not installed causes null pointer
        // exceptions in this routine unless it contained no new objects or was very simple.
        // Player's fault, so better complain early:
        var missingMods = new[] { GameParameters.BaseRuleset }.Concat(GameParameters.Mods)
            .Where(it => !Ruleset.Mods.Contains(it))
            .ToList();
        if (missingMods.Count != 0)
            throw new MissingModsException(missingMods);

        BackwardCompatibility.RemoveMissingModReferences(this);
        if (VictoryData != null) VictoryData.WinningCivObject = GetCivilization(VictoryData.WinningCiv);

        foreach (var baseUnit in Ruleset.Units.Values)
            baseUnit.SetRuleset(Ruleset);

        foreach (var building in Ruleset.Buildings.Values)
            building.Ruleset = Ruleset;

        // This needs to go before tileMap.setTransients, as units need to access
        // the nation of their civilization when setting transients
        foreach (var civInfo in Civilizations) civInfo.GameInfo = this;
        foreach (var civInfo in Civilizations)
        {
            civInfo.SetNationTransient();
            civInfo.Cache.UpdateState();
        }
        // must be done before updating tileMap, since unit uniques depend on civ uniques depend on allied city-state uniques depend on diplomacy
        foreach (var civInfo in Civilizations)
        {
            foreach (var diplomacyManager in civInfo.Diplomacy.Values)
            {
                diplomacyManager.CivInfo = civInfo;
                diplomacyManager.UpdateHasOpenBorders();
            }
        }

        // combinedGlobalUniques needs to be set before tileMap.setTransients!
        SetGlobalTransients();

        TileMap.SetTransients(Ruleset);
        BackwardCompatibility.EnsureUnitIds(this); // T236: before the sort, so units that get an id here are ordered like a running game orders them
        // T231: a load rebuilds unit lists tile by tile; use creation order instead (Kotlin keeps that order in a live game), see UnitManager.SortById.
        foreach (var civInfo in Civilizations) civInfo.Units.SortById();

        // Temporary - All games saved in 4.12.15 turned into 'hexagonal non world wrapped'
        // Here we attempt to fix that

        // How do we recognize a rectangle? By having many tiles at the lowest edge
        var tilesWithLowestRow = TileMap.TileList.GroupBy(it => it.GetRow()).MinBy(it => it.Key).NotNull().ToList();
        if (tilesWithLowestRow.Count > 2) TileMap.MapParameters.Shape = MapShape.Rectangular;

        if (CurrentPlayer == "")
        {
            CurrentPlayerCiv = GameParameters.IsOnlineMultiplayer
                ? Civilizations.First(it => it.IsHuman() && !it.IsSpectator()) // For MP, spectator doesn't get a 'turn'
                : Civilizations.First(it => it.IsHuman()); // for non-MP games, you can be a spectator of an AI-only match, and you *do* get a turn, sort of
            CurrentPlayer = CurrentPlayerCiv.CivId;
        }
        else CurrentPlayerCiv = GetCivilization(CurrentPlayer);
        // T200: simultaneous rounds must not call SetTransients mid-round, it would drop the other civs' started flags.
        RestoreTurnStartedCivs();

        foreach (var religion in Religions.Values) religion.SetTransients(this);

        foreach (var civInfo in Civilizations) civInfo.SetTransients();
        TileMap.SetNeutralTransients(); // has to happen after civInfo.setTransients() sets owningCity

        BackwardCompatibility.ConvertFortify(this);

        UpdateCivilizationState();

        SpaceResources.Clear();
        SpaceResources.UnionWith(Ruleset.Buildings.Values.Where(it => ((IHasUniques)it).HasUnique(UniqueType.SpaceshipPart))
            .SelectMany(it => it.GetResourceRequirementsPerTurn().Keys));
        // Spaceship parts are UNITS in the base ruleset (SS Booster etc., each requiring Aluminum).
        SpaceResources.UnionWith(Ruleset.Units.Values.Where(it => ((IHasUniques)it).HasUnique(UniqueType.SpaceshipPart))
            .SelectMany(it => it.GetResourceRequirementsPerTurn().Keys));
        SpaceResources.UnionWith(Ruleset.Victories.Values.SelectMany(it => it.RequiredSpaceshipParts));

        Barbarians.SetTransients(this);

        BackwardCompatibility.MigrateToTileHistory(this);
        BackwardCompatibility.MigrateGreatGeneralPools(this);
    }

    public void SetGlobalTransients()
    {
        static float EstimateDifficulty(DifficultyObject d) =>
            d.BuildingCostModifier + d.PolicyCostModifier + d.ResearchCostModifier + d.UnhappinessModifier
            - d.AiBuildingCostModifier - d.AiUnitCostModifier - d.AiUnhappinessModifier;

        difficultyObject = Ruleset.Difficulties.GetValueOrDefault(Difficulty)
            ?? MaxByOrFirst(Ruleset.Difficulties.Values, EstimateDifficulty)
            ?? throw new UncivShowableException("Invalid game: Specifies a ruleset with no entries for [Difficulty]");
        speed = Ruleset.Speeds.GetValueOrDefault(GameParameters.Speed)
            ?? MaxByOrFirst(Ruleset.Speeds.Values, it => it.Modifier)
            ?? throw new UncivShowableException("Invalid game: Specifies a ruleset with no entries for [Speed]");

        combinedGlobalUniques = GlobalUniques.Combine(Ruleset.GlobalUniques, speed, difficultyObject);
    }

    /// <summary>Kotlin <c>maxByOrNull</c>: the first element with the maximal selector value, <c>null</c> when empty.</summary>
    private static T? MaxByOrFirst<T>(IEnumerable<T> source, Func<T, float> selector) where T : class
    {
        T? best = null;
        var bestValue = 0f;
        foreach (var item in source)
        {
            var value = selector(item);
            if (best == null || value > bestValue)
            {
                best = item;
                bestValue = value;
            }
        }
        return best;
    }

    private void UpdateCivilizationState()
    {
        foreach (var civInfo in Civilizations
            // update city-state resource first since the happiness of major civ depends on it.
            // See issue: https://github.com/yairm210/Unciv/issues/7781
            .OrderByDescending(it => it.IsCityState).ToList())
        {
            using (Utils.TurnProbe.Measure(TurnProbe, "UCS.UnitVisibleTiles"))
                foreach (var unit in civInfo.Units.GetCivUnits().ToList())
                    unit.UpdateVisibleTiles(false); // this needs to be done after all the units are assigned to their civs and all other transients are set
            if (civInfo.PlayerType == PlayerType.Human)
                civInfo.ExploredRegion.SetMapParameters(TileMap.MapParameters); // Required for the correct calculation of the explored region on world wrap maps

            using (Utils.TurnProbe.Measure(TurnProbe, "UCS.UpdateOurTiles")) civInfo.Cache.UpdateOurTiles();
            using (Utils.TurnProbe.Measure(TurnProbe, "UCS.UpdateSightAndResources")) civInfo.Cache.UpdateSightAndResources(); // only run ONCE and not for each unit - this is a huge performance saver!

            // Since this depends on the cities of ALL civilizations,
            // we need to wait until we've set the transients of all the cities before we can run this.
            // Hence why it's not in CivInfo.setTransients().
            using (Utils.TurnProbe.Measure(TurnProbe, "UCS.CitiesConnectedToCapital")) civInfo.Cache.UpdateCitiesConnectedToCapital(true);

            // We need to determine the GLOBAL happiness state in order to determine the city stats
            using (Utils.TurnProbe.Measure(TurnProbe, "UCS.TileStatsAndHappiness"))
                foreach (var city in civInfo.Cities)
                {
                    city.CityStats.UpdateTileStats(); // Some nat wonders can give happiness!
                    city.CityStats.UpdateCityHappiness(city.CityConstructions.GetStats());
                }

            using (Utils.TurnProbe.Measure(TurnProbe, "UCS.CityStatsUpdate"))
                foreach (var city in civInfo.Cities)
                {
                    // We remove constructions from the queue that aren't defined in the ruleset.
                    // This can lead to situations where the city is puppeted and had its construction removed, and there's no way to user-set it
                    // So for cities like those, we'll auto-set the construction
                    // Also set construction for human players who have automate production turned on
                    if (city.CityConstructions.ConstructionQueue.Count == 0)
                        city.CityConstructions.ChooseNextConstruction();

                    // We also remove resources that the city may be demanding but are no longer in the ruleset
                    if (!Ruleset.TileResources.ContainsKey(city.DemandedResource))
                        city.DemandedResource = "";

                    // No uniques have changed since the cache was created, so we can still use it
                    // T262: the tile stats of the loop above are still what Update would compute again unless the civ's happiness moved since.
                    city.CityStats.Update(updateTileStats: !city.CityStats.TileStatsCurrent);
                }
        }
    }

    private void ConvertOldSavesToNewSaves()
    {
        var baseRulesetInMods = GameParameters.Mods.FirstOrDefault(it => RulesetCache.Current.Get(it)?.ModOptions.IsBaseRuleset == true);
        if (baseRulesetInMods != null)
        {
            GameParameters.BaseRuleset = baseRulesetInMods;
            GameParameters.Mods = new LinkedHashSet<string>(GameParameters.Mods.Where(it => it != baseRulesetInMods));
        }
    }

    /// <summary>Github.repoNameToFolderName: outer blanks (a trailing or leading '-') become '=' so the folder name is Windows-safe.</summary>
    private static string RepoNameToFolderName(string name, bool onlyOuterBlanks = false)
    {
        const char outerBlankReplacement = '=';
        var result = onlyOuterBlanks ? name : name.Replace('-', ' ');
        if (result.EndsWith(' ')) result = result.TrimEnd() + outerBlankReplacement;
        if (result.StartsWith(' ')) result = outerBlankReplacement + result.TrimStart();
        return result;
    }

    #endregion
    #region Seams

    // IGameContextGameInfo
    IGameContextRuleset IGameContextGameInfo.Ruleset => Ruleset;
    IGameContextSpeed IGameContextGameInfo.Speed => Speed;
    string? IGameContextGameInfo.GameId => GameId;
    long? IGameContextGameInfo.MapSeed => TileMap.MapParameters.Seed;
    string IGameContextGameInfo.GetDifficultyName() => GetDifficulty().Name;
    IReadOnlyList<IGameContextCivilization> IGameContextGameInfo.Civilizations => Civilizations;
    IEnumerable<IGameContextCity> IGameContextGameInfo.GetCities() => GetCities();
    IEnumerable<IGameContextTile>? IGameContextGameInfo.TileList => TileMap.TileList;
    IGameContextCivilization IGameContextGameInfo.GetCivilization(string civId) => GetCivilization(civId);
    BaseUnit IGameContextGameInfo.GetBaseUnit(string unitName) => Ruleset.Units[unitName].NotNull();
    ModConstants IGameContextGameInfo.GetModConstants() => Ruleset.ModOptions.Constants;
    void IGameContextGameInfo.HandleObsoleteGlobalQuestsOfAliveCityStates()
    {
        foreach (var civ in GetAliveCityStates()) civ.QuestManager.HandleObsoleteGlobalQuests();
    }

    // IGameInfoState
    Civilization.Civilization? IGameInfoState.CurrentPlayerCiv => currentPlayerCiv;
    void IGameInfoState.NotifyExploredResources(Civilization.Civilization civInfo, TileResourceObject resource, int maxDistance) =>
        NotifyExploredResources(civInfo, resource, maxDistance);
    #endregion
}
