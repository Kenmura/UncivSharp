using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.City.Managers;
using Unciv.Logic.Civilization.Managers;
using Unciv.Logic.Map;
using Unciv.Logic.Map.MapUnits;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Models.StatTypes;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using NotificationCategory = Unciv.Logic.Civilization.Notification.NotificationCategory;

namespace Unciv.Logic.Civilization;

public partial class Civilization
{
    #region state-changing functions
    /// <summary>Kotlin <c>@VisibleForTesting setNameForUnitTests</c>.</summary>
    public void SetNameForUnitTests(string name)
    {
        CivName = name;
    }

    /// <summary>
    /// This is separate because the REGULAR setTransients updates the viewable ties,
    /// and updateVisibleTiles tries to meet civs...
    /// And if the civs don't yet know who they are then they don't know if they're barbarians =\
    /// </summary>
    public void SetNationTransient()
    {
        nation = GameInfo.Ruleset.Nations.TryGetValue(CivName, out var found)
            ? found
            : throw new MissingNationException($"Nation {CivName} is not found!", GameInfo.Ruleset.Mods.ToList());
    }

    public void SetTransients()
    {
        GoldenAges.CivInfo = this;
        GreatPeople.CivInfo = this;
        CivConstructions.SetTransients(this);
        Policies.SetTransients(this);
        QuestManager.SetTransients(this);
        ReligionManager.SetTransients(this); // needs to be before tech, since tech setTransients looks at all uniques
        Tech.SetTransients(this);
        RuinsManager.SetTransients(this);
        EspionageManager.SetTransients(this);
        VictoryManager.CivInfo = this;

        foreach (var diplomacyManager in Diplomacy.Values)
        {
            diplomacyManager.CivInfo = this;
            diplomacyManager.UpdateHasOpenBorders();
        }

        foreach (var city in Cities)
        {
            city.SetTransients(this); // must be before the city's setTransients because it depends on the tilemap, that comes from the currentPlayerCivInfo
        }

        // Now that all tile transients have been updated, clean "worked" tiles that are not under the Civ's control
        foreach (var city in Cities)
            foreach (var tile in city.GetWorkedTiles().ToList())
                if (!ReferenceEquals(tile.GetOwner(), this))
                    city.StopWorkingTile(tile);

        PassThroughImpassableUnlocked = PassableImpassables.Count != 0;

        HasLongCountDisplayUnique = HasUnique(UniqueType.MayanCalendarDisplay);

        Cache.SetTransients();
    }

    public void AddFlag(string flag, int count) => FlagsCountdown[flag] = count;
    public void RemoveFlag(string flag) => FlagsCountdown.Remove(flag);
    public bool HasFlag(string flag) => FlagsCountdown.ContainsKey(flag);

    private int? GetFlag(CivFlags flag) => FlagsCountdown.TryGetValue(flag.ToString(), out var value) ? value : null;

    public int GetTurnsBetweenDiplomaticVotes() => KotlinMath.FloatToInt(15 * GameInfo.Speed.Modifier); // Dunno the exact calculation, hidden in Lua files
    public int? GetTurnsTillNextDiplomaticVote() => GetFlag(CivFlags.TurnsTillNextDiplomaticVote);

    public int? GetRecentBullyingCountdown() => GetFlag(CivFlags.RecentlyBullied);
    public int? GetTurnsTillCallForBarbHelp() => GetFlag(CivFlags.TurnsTillCallForBarbHelp);

    public bool MayVoteForDiplomaticVictory() =>
        // Does not need checks for Barbarians or dead civs because the callers already ensure that
        // (NextTurnAutomation.tryVoteForDiplomaticVictory and NextTurnAction.WorldCongressVote)
        !IsSpectator()
        && GetTurnsTillNextDiplomaticVote() == 0
        && !GameInfo.DiplomaticVictoryVotesCast.ContainsKey(CivId)
        // Only vote if there is someone to vote for, may happen in one-more-turn mode
        && GameInfo.Civilizations.Any(it => it.IsMajorCiv() && !it.IsDefeated() && !ReferenceEquals(it, this));

    public void DiplomaticVoteForCiv(string? chosenCivId)
    {
        GameInfo.DiplomaticVictoryVotesCast[CivId] = chosenCivId;
    }

    public bool ShouldShowDiplomaticVotingResults() =>
        GetFlag(CivFlags.ShowDiplomaticVotingResults) == 0
        && GameInfo.Civilizations.Any(it => it.IsMajorCiv() && !it.IsDefeated() && !ReferenceEquals(it, this));

    /// <summary>Modify gold by a given amount making sure it does neither overflow nor underflow.</summary>
    /// <param name="delta">the amount to add (can be negative)</param>
    public void AddGold(int delta)
    {
        // not using Long.coerceIn - this stays in 32 bits
        if (delta > 0 && Gold > int.MaxValue - delta) Gold = int.MaxValue;
        else if (delta < 0 && Gold < int.MinValue - delta) Gold = int.MinValue;
        else Gold = unchecked(Gold + delta);
    }

    public bool HasStatToBuy(Stat stat, int price)
    {
        if (GameInfo.GameParameters.GodMode) return true;
        if (price == 0) return true;
        return GetStatReserve(stat) >= price;
    }

    public void AddStats(Models.StatTypes.Stats stats)
    {
        foreach (var (stat, amount) in stats) AddStat(stat, KotlinMath.FloatToInt(amount));
    }

    public void AddStat(Stat stat, int amount)
    {
        switch (stat)
        {
            case Stat.Culture:
                Policies.AddCulture(amount);
                if (amount > 0) TotalCultureForContests += amount;
                break;
            case Stat.Science: Tech.AddScience(amount); break;
            case Stat.Gold: AddGold(amount); break;
            case Stat.Faith:
                ReligionManager.StoredFaith += amount;
                if (amount > 0) TotalFaithForContests += amount;
                break;
                // Food and Production wouldn't make sense to be added nationwide
                // Happiness cannot be added as it is recalculated again, use a unique instead
        }
    }

    /// <param name="stat">A <see cref="Stat"/>, a <see cref="SubStat"/> or a <see cref="TileResource"/> (Kotlin <c>GameResource</c>).</param>
    /// <param name="amount">The amount to add.</param>
    public void AddGameResource(object stat, int amount)
    {
        if (stat is TileResource { IsStockpiled: true } stockpiled) GainStockpiledResource(stockpiled, amount);
        switch (stat)
        {
            case Stat.Culture:
                Policies.AddCulture(amount);
                if (amount > 0) TotalCultureForContests += amount;
                break;
            case Stat.Science: Tech.AddScience(amount); break;
            case Stat.Gold: AddGold(amount); break;
            case Stat.Faith:
                ReligionManager.StoredFaith += amount;
                if (amount > 0) TotalFaithForContests += amount;
                break;
            case SubStat.GoldenAgePoints: GoldenAges.AddHappiness(amount); break;
                // Food and Production wouldn't make sense to be added nationwide
                // Happiness cannot be added as it is recalculated again, use a unique instead
        }
    }

    /// <param name="gameResource">A <see cref="Stat"/>, a <see cref="SubStat"/> or a <see cref="TileResource"/> (Kotlin <c>GameResource</c>).</param>
    public int GetGameResource(object gameResource) => gameResource switch
    {
        TileResource resource => GetResourceAmount(resource),
        Stat stat => GetStatReserve(stat),
        SubStat.GoldenAgePoints => GoldenAges.StoredHappiness,
        _ => throw new Exception($"Unrecognized gameResource {(gameResource as IGameResource)?.Name ?? gameResource.ToString()}"),
    };

    public void GainStockpiledResource(TileResource resource, int amount)
    {
        if (resource.IsCityWide) return;
        ResourceStockpiles.Add(resource.Name, amount);
    }

    public int GetStatReserve(Stat stat)
    {
        switch (stat)
        {
            case Stat.Culture: return Policies.StoredCulture;
            case Stat.Science:
                var currentTechnology = Tech.CurrentTechnology();
                return currentTechnology == null ? 0 : Tech.ResearchOfTech(currentTechnology.Name);
            case Stat.Gold: return Gold;
            case Stat.Faith: return ReligionManager.StoredFaith;
            case Stat.Happiness: return Stats.Happiness;
            default: return 0;
        }
    }

    #region addNotification
    // Add a Notification to this civ's notifications. Ignored for AI civ's. There are overloads accepting zero, one, a Sequence or
    // Iterable of NotificationActions between text and category; another overload accepts a HexCoord as shorthand for a LocationAction -
    // for several use LocationAction.Of(positions). notificationIcons: Zero or more icons to decorate the notification with - see NotificationIcon.

    /// <summary>No-action version.</summary>
    public void AddNotification(string text, NotificationCategory category, params string[] notificationIcons) =>
        AddNotification(text, (IEnumerable<NotificationAction>?)null, category, notificationIcons);

    /// <summary>Single-location version.</summary>
    public void AddNotification(string text, HexCoord location, NotificationCategory category, params string[] notificationIcons) =>
        AddNotification(text, new LocationAction(location), category, notificationIcons);

    /// <summary>Single-action version.</summary>
    public void AddNotification(string text, NotificationAction action, NotificationCategory category, params string[] notificationIcons) =>
        AddNotification(text, new[] { action }, category, notificationIcons);

    /// <summary>Iterable (and Sequence) version.</summary>
    public void AddNotification(string text, IEnumerable<NotificationAction>? actions, NotificationCategory category, params string[] notificationIcons)
    {
        if (PlayerType == PlayerType.AI) return; // no point in lengthening the saved game info if no one will read it
        Notifications.Add(GameEventHooks.NotificationAdded(this, new Notification(text, notificationIcons, actions, category)));
    }
    #endregion

    /// <summary>
    /// Kotlin <c>addCity(location, unit)</c>. <c>CityFounder</c> reads this civ's <see cref="Unciv.Logic.PlayerSettings"/> for <c>City.resetSpecialistsControl</c>.
    /// </summary>
    public CityObject AddCity(HexCoord location, MapUnit? unit = null)
    {
        var newCity = new CityFounder().FoundCity(this, location, unit);
        newCity.CityConstructions.ChooseNextConstruction();
        return newCity;
    }

    /// <summary>
    /// Destroy what's left of a Civilization
    ///
    /// - function expects cities.isEmpty()
    /// - remaining units are destroyed and diplomacy cleaned up
    /// </summary>
    /// <param name="notificationLocation">if given *and* the civ receiving the notification can see the tile or knows there was a city there, then the notification can show this location on click</param>
    public void Destroy(HexCoord? notificationLocation = null)
    {
        // At the moment, the "last unit down" callers do not pass a location, the city ones do - because the former isn't interesting
        RevealMapWhenDefeated();
        string destructionText = IsMajorCiv() ? $"The civilization of [{CivName}] has been destroyed!"
            : $"The City-State of [{CivName}] has been destroyed!";
        foreach (var civ in GameInfo.Civilizations)
        {
            if (civ.IsDefeated()) continue; // addNotification will ignore barbarians and other AI
            HexCoord? location = null;
            if (notificationLocation != null)
            {
                var tile = GameInfo.TileMap[notificationLocation.Value];
                if (tile.IsVisible(civ) || (tile.IsExplored(civ) && tile.GetShownImprovement(civ) == Constants.CityCenter))
                    location = notificationLocation;
            }
            civ.AddNotification(destructionText, LocationAction.Of(location), NotificationCategory.General, CivName, NotificationIcon.Death);
        }
        foreach (var unit in Units.GetCivUnits().ToList()) unit.Destroy();
        TradeRequests.Clear(); // if we don't do this then there could be resources taken by "pending" trades forever
        foreach (var diplomacyManager in Diplomacy.Values)
        {
            diplomacyManager.Trades.Clear();
            diplomacyManager.OtherCivDiplomacy().Trades.Clear();
            foreach (var tradeRequest in diplomacyManager.OtherCiv.TradeRequests.Where(it => it.RequestingCiv == CivId).ToList())
                diplomacyManager.OtherCiv.TradeRequests.Remove(tradeRequest); // it  would be really weird to get a trade request from a dead civ
        }
        if (GameInfo.IsEspionageEnabled())
            EspionageManager.RemoveAllSpies();
    }

    /// <summary>Reveals the entire map to a human player defeated in a singleplayer game, so they can watch the game play out.</summary>
    public void RevealMapWhenDefeated()
    {
        if (TurnState.IsSimultaneousGame(this) ? !TurnState.DefeatedSeesWholeMap(this) : GameInfo.GameParameters.IsOnlineMultiplayer || !IsCurrentPlayer()) return;
        foreach (var tile in GameInfo.TileMap.Values) tile.SetExplored(this, true);
    }

    public Proximity UpdateProximity(Civilization otherCiv, Proximity? preCalculated = null) => Cache.UpdateProximity(otherCiv, preCalculated);

    /// <summary>Removes current capital then moves capital to argument city if not null</summary>
    public void MoveCapitalTo(CityObject? city, CityObject? oldCapital)
    {
        // Add new capital first so the civ doesn't get stuck in a state where it has cities but no capital
        var newCapitalIndicator = city == null ? null : CapitalCityIndicator(city);
        if (newCapitalIndicator != null)
        {
            // move new capital
            city.NotNull().CityConstructions.AddBuilding(newCapitalIndicator);
            city.IsBeingRazed = false; // stop razing the new capital if it was being razed

            // move the buildings with MovedToNewCapital unique
            if (oldCapital != null)
            {
                // Get the Set of the buildings to move
                var buildingsToMove = oldCapital.CityConstructions.GetBuiltBuildings()
                    .Where(it => ((IHasUniques)it).HasUnique(UniqueType.MovesToNewCapital))
                    .Distinct().ToList();

                foreach (var building in buildingsToMove) oldCapital.CityConstructions.RemoveBuilding(building);

                // Add the buildings to new capital
                foreach (var building in buildingsToMove) city.CityConstructions.AddBuilding(building);
            }
        }

        var oldCapitalIndicator = oldCapital == null ? null : CapitalCityIndicator(oldCapital);
        if (oldCapitalIndicator != null) oldCapital.NotNull().CityConstructions.RemoveBuilding(oldCapitalIndicator);
    }

    /// <param name="oldCapital"><c>null</c> when destroying, otherwise old capital</param>
    public void MoveCapitalToNextLargest(CityObject? oldCapital)
    {
        var availableCities = Cities.Where(it => !it.IsCapital()).ToList();
        if (availableCities.Count == 0)
        {
            MoveCapitalTo(null, oldCapital);
            return;
        }

        // Kotlin maxByOrNull: the first element with the maximum population wins
        static CityObject? LargestOrNull(IEnumerable<CityObject> candidates)
        {
            CityObject? best = null;
            foreach (var candidate in candidates)
                if (best == null || candidate.Population.Population > best.Population.Population) best = candidate;
            return best;
        }

        var newCapital = LargestOrNull(availableCities.Where(it => !it.IsPuppet));

        if (newCapital == null)
        {
            // No non-puppets, take largest puppet and annex
            newCapital = LargestOrNull(availableCities).NotNull();
            newCapital.AnnexCity();
        }

        // Slight "Easter egg": see #11486: In the rare case a City-state loses their last city but it's not their original capital, the notification names the Nation which confuses players.
        // Rename the newly conquered city when the conquering Nation's first-city name is equal to the nation name (meaning Babylon too) and the civ has lost that...
        var currentCapital = GetCapital();
        if (IsCityState && currentCapital != null && currentCapital.IsOriginalCapital && CivName == currentCapital.Name)
            newCapital.Name = $"New [{CivName}]\n(formerly known as [{newCapital.Name}])";

        MoveCapitalTo(newCapital, oldCapital);
    }

    private Civilization? allyCiv;
    /// <summary>
    /// Current ally (assuming this is a city-state)
    /// Setting this also sets its backing serialization field <c>allyCivName</c>
    /// </summary>
    [JsonIgnore]
    public Civilization? AllyCiv
    {
        get
        {
            if (allyCiv == null && allyCivName != null)
                allyCiv = (Civilization)GameInfo.GetCivilization(allyCivName);
            return allyCiv;
        }
        set
        {
            allyCivName = value?.CivId;
            allyCiv = value;
        }
    }

    /// <summary>
    /// Determine if this civ (typically as human player) is allowed to know how many major civs there are
    ///
    /// Can only be <c>true</c> if GameParameters.randomNumberOfPlayers is <c>true</c>, but in that case
    /// we try to see if the player _could_ be certain with a modicum of cleverness...
    /// </summary>
    public bool ShouldHideCivCount()
    {
        if (!GameInfo.GameParameters.RandomNumberOfPlayers) return false;
        int knownCivs = 1 + GetKnownCivs().Count(it => it.IsMajorCiv());
        if (knownCivs >= GameInfo.GameParameters.MaxNumberOfPlayers) return false;
        if (HasUnique(UniqueType.OneTimeRevealEntireMap)) return false;
        // Other ideas? viewableTiles.size == gameInfo.tileMap.tileList.size seems not quite useful...
        return true;
    }

    public CivilizationInfoPreview AsPreview() => new(this);

    public string? GetLastSeenImprovement(HexCoord position)
    {
        if (IsAI() || IsSpectator()) return null;
        return LastSeenImprovement.TryGetValue(position, out var improvement) ? improvement : null;
    }

    public void SetLastSeenImprovement(HexCoord position, string? improvement)
    {
        if (IsAI() || IsSpectator()) return;
        if (improvement == null)
            LastSeenImprovement.Remove(position);
        else
            LastSeenImprovement[position] = improvement;
    }
    #endregion

}
