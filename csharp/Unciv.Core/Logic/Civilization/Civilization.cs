using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Logic.Civilization.Managers;
using Unciv.Logic.Civilization.Transients;
using Unciv.Logic.Map;
using Unciv.Logic.Map.MapGenerator.Mapregions;
using Unciv.Logic.Map.Tiles;
using Unciv.Logic.Trade;
using Unciv.Models;
using Unciv.Models.Ruleset;
using Unciv.Models.Ruleset.Tile;
using Unciv.Models.Ruleset.Unique;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;
using CityStateTypeObject = Unciv.Models.Ruleset.Nation.CityStateType;
using DifficultyObject = Unciv.Models.Ruleset.Nation.Difficulty;
using NationObject = Unciv.Models.Ruleset.Nation.Nation;
using PersonalityObject = Unciv.Models.Ruleset.Nation.Personality;
using ProximityLevel = Unciv.Logic.Civilization.Proximity;
using UniqueObject = Unciv.Models.Ruleset.Unique.Unique;

namespace Unciv.Logic.Civilization;

// Ported from core/src/com/unciv/logic/civilization/Civilization.kt (T055).
//
// Civilization is ported BEFORE its managers, City, MapUnit and GameInfo (seam rule, T053 notes): every field of those types is typed by
// its contract in CivilizationContracts.cs, and the manager fields are lateinit-style properties (reading before assignment throws) because
// the Kotlin initializer (`var tech = TechManager()`) has nothing to construct yet. The ticket porting a manager replaces its property
// by `= new XManager()` (and wires the constructor), then retypes the field to the concrete class.
//
// Stripped / deferred:
// - timeThis profiling wrappers and the purity annotations.
// - addCity (CityFounder): its two client-settings inputs of resetSpecialistsControl are explicit parameters (T061).
// - isAIOrAutoPlaying: the UncivGame.Current.worldScreen auto-play check is UI state; a headless engine has none, so it is `false`.
// - Kotlin HashMap/HashSet fields keep java.util iteration order (JavaHashMap/JavaHashSet), except neutralRoads, whose seam
//   (IGameContextCivilization.NeutralRoads, T053) is an ISet<HexCoord>; its only Kotlin iteration (CivInfoStatsForNextTurn, T058) sums.
// - GameResource (an interface implemented by the enums Stat and SubStat and the class TileResource) is passed as `object`.
// - The `Civilization.NotificationsLog` / `HistoricalAttackMemory` nested classes keep their Kotlin shape.
// Kotlin Civilization has no equals/hashCode: this class keeps reference identity.

public enum Proximity
{
    None, // ie no cities
    Neighbors,
    Close,
    Far,
    Distant,
}

public partial class Civilization : IsPartOfGameInfoSerialization, IConstructionCivilization, IGameProgressCivilization, ILocalUniqueCacheCivilization, IMapRegionsCivilization
{
    private static T Lateinit<T>(T? value, string name) where T : class =>
        value ?? throw new InvalidOperationException($"lateinit property {name} has not been initialized");

    #region Transient fields
    private IGameInfoState? gameInfo;
    /// <summary>Kotlin <c>lateinit var</c>: reading before assignment throws.</summary>
    [JsonIgnore]
    public IGameInfoState GameInfo
    {
        get => Lateinit(gameInfo, "gameInfo");
        set => gameInfo = value;
    }

    private bool IsGameInfoInitialized => gameInfo != null;

    /// <summary>
    /// Context for <see cref="UniqueType.StartBias"/> during map gen / start placement.
    /// Passes <see cref="IGameInfoState"/> only - never this <see cref="Civilization"/>, which may be only partially initialized.
    /// Conditionals that need tiles/cities/units are therefore unsupported here.
    /// </summary>
    public GameContext GetGameContextForStartBias() =>
        IsGameInfoInitialized ? new GameContext(gameInfo: gameInfo) : GameContext.IgnoreConditionalsState;

    // IMapRegionsCivilization (T075 seam, implemented by T041): what MapRegions / MinorCivPlacer read from a Civilization
    List<string> IMapRegionsCivilization.GetStartBias(Ruleset ruleset) =>
        ruleset.Nations[CivName].GetStartBias(ruleset, GetGameContextForStartBias()).ToList();

    GameRandom IMapRegionsCivilization.StateBasedRandom(string caller) => State.StateBasedRandom(caller);

    private NationObject? nation;
    /// <summary>Kotlin <c>lateinit var</c>: reading before assignment throws.</summary>
    [JsonIgnore]
    public NationObject Nation
    {
        get => Lateinit(nation, "nation");
        set => nation = value;
    }

    [JsonIgnore]
    public GameContext State { get; set; } = GameContext.EmptyState;

    /// <summary>Transient. Kotlin <c>val units = UnitManager(this)</c> (assigned in the constructor).</summary>
    [JsonIgnore]
    public UnitManager Units { get; set; }

    /// <summary>Transient. Kotlin <c>var diplomacyFunctions = DiplomacyFunctions(this)</c> (assigned in the constructor).</summary>
    [JsonIgnore]
    public DiplomacyFunctions DiplomacyFunctions { get; set; }

    [JsonIgnore]
    public IReadOnlySet<Tile> ViewableTiles { get; set; } = new HashSet<Tile>();

    /// <summary>
    /// For each tile some detector unit of ours can see, the unit filters (<see cref="UniqueType.CanSeeInvisibleUnits"/>)
    /// it could detect there - independent of what (if anything) currently occupies the tile.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<Tile, IReadOnlySet<string>> ViewableInvisibleUnitsTiles { get; set; } =
        new Dictionary<Tile, IReadOnlySet<string>>();

    /// <summary>This is for performance since every movement calculation depends on this, see MapUnit comment</summary>
    [JsonIgnore]
    public bool HasActiveEnemyMovementPenalty { get; set; }

    /// <summary>Same as above variable</summary>
    [JsonIgnore]
    public IEnumerable<UniqueObject>? EnemyMovementPenaltyUniques { get; set; }

    [JsonIgnore]
    public ResourceSupplyList DetailedCivResources { get; set; } = new();

    [JsonIgnore]
    public ResourceSupplyList SummarizedCivResourceSupply { get; set; } = new();

    [JsonIgnore]
    public UniqueMap CivResourcesUniqueMap { get; set; } = new();

    private ICityStateFunctions? cityStateFunctions;
    /// <summary>
    /// Transient. Kotlin <c>val cityStateFunctions = CityStateFunctions(this)</c>: the real <see cref="Diplomacy.CityStateFunctions"/> unless a test
    /// (or a later seam owner) assigns another <see cref="ICityStateFunctions"/>.
    /// </summary>
    [JsonIgnore]
    public ICityStateFunctions CityStateFunctions
    {
        get => cityStateFunctions ??= new Diplomacy.CityStateFunctions(this);
        set => cityStateFunctions = value;
    }

    /// <summary>Cached Boolean equal to passableImpassables.isNotEmpty()</summary>
    [JsonIgnore]
    public bool PassThroughImpassableUnlocked { get; set; }

    [JsonIgnore]
    public HashSet<HexCoord> NeutralRoads { get; set; } = [];

    [JsonIgnore]
    public ModConstants ModConstants => GameInfo.Ruleset.ModOptions.Constants;

    [JsonIgnore]
    public bool HasLongCountDisplayUnique { get; set; }

    /// <summary>Transient. Kotlin <c>val stats = CivInfoStatsForNextTurn(this)</c> (assigned in the constructor).</summary>
    [JsonIgnore]
    public CivInfoStatsForNextTurn Stats { get; }

    /// <summary>Transient. Kotlin <c>val cache = CivInfoTransientCache(this)</c> (assigned in the constructor).</summary>
    [JsonIgnore]
    public CivInfoTransientCache Cache { get; }

    private int cachedMilitaryMight = -1;
    #endregion

    #region Serialized fields
    [JsonPropertyName("playerType")]
    public PlayerType PlayerType { get; set; } = PlayerType.AI;

    /// <summary>Used in online multiplayer for human players</summary>
    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = "";
    /// <summary>Used in online multiplayer, if a player exceed this time to complete their turn, others can force them to resign</summary>
    [JsonPropertyName("playerMinutesBeforeForceResign")]
    public int PlayerMinutesBeforeForceResign { get; set; } = 3 * 24 * 60;
    /// <summary>Total number of minutes spent on their turn. Recorded at the end of each turn.</summary>
    [JsonPropertyName("totalTurnTimeSeconds")]
    public int TotalTurnTimeSeconds { get; set; }
    /// <summary>To calculate average turn time, we only count turns when controlled by a human. Additionally, offers backward compatibility.</summary>
    [JsonPropertyName("turnsPlayedAsHuman")]
    public int TurnsPlayedAsHuman { get; set; }
    [JsonPropertyName("lastTurnProcessedWithVersion")]
    public Version? LastTurnProcessedWithVersion { get; set; }

    /// <summary>The Civ's gold reserves. Public get, private set - please use <see cref="AddGold"/> method to modify.</summary>
    [JsonPropertyName("gold")]
    [JsonInclude]
    public int Gold { get; private set; }

    /// <summary>
    /// The Civ's name
    /// - must always be equal to Nation.name (except in the unit test code, where only local consistency is needed)
    /// - Displayed and translated as-is
    /// </summary>
    [JsonPropertyName("civName")]
    [JsonInclude]
    public string CivName { get; private set; } = "";

    /// <summary>
    /// The Civ's json key
    /// - is currently defaulted to Nation.name (except in the unit test code, where only local consistency is needed)
    /// - used as uniquely identifying key, so no two players can have the same id
    /// Kotlin <c>civID</c>.
    /// </summary>
    [JsonPropertyName("civID")]
    public string CivId { get; set; } = "";

    [JsonPropertyName("tech")]
    public TechManager Tech { get; set; } = new();

    [JsonPropertyName("policies")]
    public PolicyManager Policies { get; set; } = new();

    /// <summary>Kotlin <c>var civConstructions = CivConstructions()</c>.</summary>
    [JsonPropertyName("civConstructions")]
    public CivConstructions CivConstructions { get; set; } = new();

    /// <summary>Kotlin <c>var questManager = QuestManager()</c>.</summary>
    [JsonPropertyName("questManager")]
    public QuestManager QuestManager { get; set; } = new();

    [JsonPropertyName("religionManager")]
    public ReligionManager ReligionManager { get; set; } = new();

    [JsonPropertyName("goldenAges")]
    public GoldenAgeManager GoldenAges { get; set; } = new();

    [JsonPropertyName("greatPeople")]
    public GreatPersonManager GreatPeople { get; set; } = new();

    [JsonPropertyName("espionageManager")]
    public EspionageManager EspionageManager { get; set; } = new();

    [JsonPropertyName("victoryManager")]
    public VictoryManager VictoryManager { get; set; } = new();

    [JsonPropertyName("ruinsManager")]
    public RuinsManager RuinsManager { get; set; } = new();

    /// <summary>Kotlin <c>HashMap&lt;String, DiplomacyManager&gt;</c>: java.util.HashMap iteration order.</summary>
    [JsonPropertyName("diplomacy")]
    public JavaHashMap<string, DiplomacyManager> Diplomacy { get; set; } = new();
    [JsonPropertyName("proximity")]
    public JavaHashMap<string, Proximity> Proximity { get; set; } = new();
    [JsonPropertyName("popupAlerts")]
    [JsonInclude]
    public List<PopupAlert> PopupAlerts { get; private set; } = [];
    /// <summary>Serialization field for <see cref="AllyCiv"/>. Is equivalent to <c>allyCiv.civName</c></summary>
    [JsonPropertyName("allyCivName")]
    [JsonInclude]
    private string? allyCivName;
    [JsonPropertyName("naturalWonders")]
    public List<string> NaturalWonders { get; set; } = [];

    [JsonPropertyName("notifications")]
    public List<Notification> Notifications { get; set; } = [];
    /// <summary>Used by NotificationsOverviewTable to highlight new entries</summary>
    [JsonPropertyName("notificationCountAtStartTurn")]
    public int? NotificationCountAtStartTurn { get; set; }

    [JsonPropertyName("notificationsLog")]
    public List<NotificationsLog> NotificationsLogs { get; set; } = [];

    public class NotificationsLog(int turn = 0) : IsPartOfGameInfoSerialization
    {
        [JsonPropertyName("turn")]
        public int Turn { get; } = turn;
        [JsonPropertyName("notifications")]
        public List<Notification> Notifications { get; set; } = [];
    }

    /// <summary>for trades here, ourOffers is the current civ's offers, and theirOffers is what the requesting civ offers.</summary>
    [JsonPropertyName("tradeRequests")]
    [JsonInclude]
    public List<TradeRequest> TradeRequests { get; private set; } = [];

    /// <summary>See DiplomacyManager.flagsCountdown for why this does not map Enums to ints</summary>
    [JsonPropertyName("flagsCountdown")]
    public JavaHashMap<string, int> FlagsCountdown { get; set; } = new();

    [JsonPropertyName("resourceStockpiles")]
    public Counter<string> ResourceStockpiles { get; set; } = new();

    /// <summary>
    /// List instead of HashMap as the same unique might appear multiple times.
    /// This can also contain NON-temporary uniques but I can't be bothered to do the deprecation dance with this one
    /// </summary>
    [JsonPropertyName("temporaryUniques")]
    [JsonInclude]
    public List<TemporaryUnique> TemporaryUniques { get; private set; } = [];

    /// <summary>
    /// if we only use lists, and change the list each time the cities are changed,
    /// we won't get concurrent modification exceptions.
    /// This is basically a way to ensure our lists are immutable.
    /// </summary>
    [JsonPropertyName("cities")]
    public IReadOnlyList<CityObject> Cities { get; set; } = [];
    [JsonPropertyName("citiesCreated")]
    public int CitiesCreated { get; set; }

    /// <summary>
    /// Constructions (e.g. buildings) that will be disabled when a new city is founded
    /// They are moved to the "Disabled" section in the build menu, and will not be built during automation.
    /// </summary>
    [JsonPropertyName("disabledCityConstructions")]
    [JsonInclude]
    public JavaHashSet<string> DisabledCityConstructions { get; private set; } = new();

    /// <summary>Limit camera within explored region</summary>
    [JsonPropertyName("exploredRegion")]
    public ExploredRegion ExploredRegion { get; set; } = new();

    public bool HasExplored(Tile tile) => tile.IsExplored(this);

    [JsonPropertyName("lastSeenImprovement")]
    [JsonInclude]
    public LastSeenImprovement LastSeenImprovement { get; private set; } = new();

    /// <summary>To correctly determine "game over" condition as clarified in #4707</summary>
    [JsonPropertyName("hasEverOwnedOriginalCapital")]
    public bool HasEverOwnedOriginalCapital { get; set; }

    /// <summary>For Carthage-like uniques</summary>
    [JsonPropertyName("passableImpassables")]
    [JsonInclude]
    public JavaHashSet<string> PassableImpassables { get; private set; } = new();

    /// <summary>For Aggressor, Warmonger status</summary>
    [JsonPropertyName("numMinorCivsAttacked")]
    [JsonInclude]
    internal int NumMinorCivsAttacked { get; set; }

    [JsonPropertyName("totalCultureForContests")]
    public int TotalCultureForContests { get; set; }
    [JsonPropertyName("totalFaithForContests")]
    public int TotalFaithForContests { get; set; }

    /// <summary>
    /// The title for the Civilization's leader.
    /// When empty, will display the nation's display name, otherwise will parse it with leaderName. For example: <c>"King [leaderName]"</c>
    /// </summary>
    [JsonPropertyName("leaderTitle")]
    public string LeaderTitle { get; set; } = "";

    /// <summary>Container class to represent a historical attack recently performed by this civilization.</summary>
    public class HistoricalAttackMemory() : IsPartOfGameInfoSerialization
    {
        public HistoricalAttackMemory(string? attackingUnit, HexCoord source, HexCoord target) : this()
        {
            AttackingUnit = attackingUnit;
            Source = source;
            Target = target;
        }

        /// <summary>Name key of BaseUnit type that performed the attack, or null (E.G. for city bombardments).</summary>
        [JsonPropertyName("attackingUnit")]
        public string? AttackingUnit { get; set; }
        /// <summary>Position of the tile from which the attack was made. Kotlin <c>lateinit var</c>.</summary>
        [JsonPropertyName("source")]
        public HexCoord Source { get; set; }
        /// <summary>Position of the tile targeted by the attack. Kotlin <c>lateinit var</c>.</summary>
        [JsonPropertyName("target")]
        public HexCoord Target { get; set; }

        public HistoricalAttackMemory Clone() => new(AttackingUnit, Source, Target);
    }

    /// <summary>
    /// List of attacks that this civilization has performed since the start of its most recent turn. Does not include attacks already
    /// tracked in MapUnit.attacksSinceTurnStart of living units. Used in movement arrow overlay.
    /// </summary>
    [JsonPropertyName("attacksSinceTurnStart")]
    public List<HistoricalAttackMemory> AttacksSinceTurnStart { get; set; } = [];

    [JsonPropertyName("hasMovedAutomatedUnits")]
    public bool HasMovedAutomatedUnits { get; set; }

    [JsonPropertyName("statsHistory")]
    public CivRankingHistory StatsHistory { get; set; } = new();

    [JsonPropertyName("cityStatePersonality")]
    public CityStatePersonality CityStatePersonality { get; set; } = CityStatePersonality.Neutral;
    /// <summary>Kotlin <c>cityStateResource</c></summary>
    [JsonPropertyName("cityStateResource")]
    public string? CityStateResource { get; set; }
    /// <summary>Unique unit for militaristic city state. Might still be null if there are no appropriate units</summary>
    [JsonPropertyName("cityStateUniqueUnit")]
    public string? CityStateUniqueUnit { get; set; }
    #endregion

    private readonly KotlinLazy<CityStateTypeObject> cityStateType;
    [JsonIgnore]
    public CityStateTypeObject CityStateType => cityStateType.Value;

    public Civilization()
    {
        Stats = new CivInfoStatsForNextTurn(this);
        Cache = new CivInfoTransientCache(this);
        DiplomacyFunctions = new DiplomacyFunctions(this);
        Units = new UnitManager(this);
        // PublicationOnly: Kotlin `by lazy` does not cache a failed initializer (e.g. gameInfo not yet set), unlike ExecutionAndPublication
        cityStateType = new KotlinLazy<CityStateTypeObject>(() => GameInfo.Ruleset.CityStateTypes[Nation.CityStateType.NotNull()]);
    }

    public Civilization(string civName) : this()
    {
        CivName = civName;
        CivId = civName;
    }

    public Civilization(NationObject nation) : this()
    {
        this.nation = nation;
        CivName = nation.Name;
        CivId = nation.Name;
    }

    public Civilization(string civName, string civId) : this()
    {
        CivName = civName;
        CivId = civId;
    }

    public Civilization(NationObject nation, string civId) : this()
    {
        this.nation = nation;
        CivName = nation.Name;
        CivId = civId;
    }

    public Civilization Clone()
    {
        var toReturn = new Civilization
        {
            Gold = Gold,
            PlayerType = PlayerType,
            PlayerId = PlayerId,
            PlayerMinutesBeforeForceResign = PlayerMinutesBeforeForceResign,
            TotalTurnTimeSeconds = TotalTurnTimeSeconds,
            TurnsPlayedAsHuman = TurnsPlayedAsHuman,
            LastTurnProcessedWithVersion = LastTurnProcessedWithVersion,
            CivName = CivName,
            CivId = CivId,
            Tech = Tech.Clone(),
            Policies = Policies.Clone(),
        };
        toReturn.CivConstructions = CivConstructions.Clone();
        toReturn.CivConstructions.SetTransients(toReturn);
        toReturn.ReligionManager = ReligionManager.Clone();
        toReturn.QuestManager = QuestManager.Clone();
        toReturn.GoldenAges = GoldenAges.Clone();
        toReturn.GreatPeople = GreatPeople.Clone();
        toReturn.RuinsManager = RuinsManager.Clone();
        toReturn.EspionageManager = EspionageManager.Clone();
        toReturn.VictoryManager = VictoryManager.Clone();
        toReturn.allyCivName = allyCivName;
        foreach (var diplomacyManager in Diplomacy.Values.Select(it => it.Clone()).ToList())
            toReturn.Diplomacy[diplomacyManager.OtherCivName] = diplomacyManager;
        toReturn.Proximity.PutAll(Proximity);
        toReturn.Cities = Cities.Select(it => it.Clone()).ToList();
        toReturn.NeutralRoads = [.. NeutralRoads]; // T219: copies, not the source's own collections
        foreach (var construction in DisabledCityConstructions) toReturn.DisabledCityConstructions.Add(construction);
        toReturn.ExploredRegion = ExploredRegion.Clone();
        CopyLastSeenInto(toReturn);
        toReturn.LeaderTitle = LeaderTitle;
        toReturn.Notifications.AddRange(Notifications.Select(it => it.Clone()));
        foreach (var log in NotificationsLogs)
            toReturn.NotificationsLogs.Add(new NotificationsLog(log.Turn) { Notifications = [.. log.Notifications.Select(it => it.Clone())] });
        toReturn.NotificationCountAtStartTurn = NotificationCountAtStartTurn;
        toReturn.CitiesCreated = CitiesCreated;
        toReturn.PopupAlerts.AddRange(PopupAlerts.Select(it => new PopupAlert(it.Type, it.Value)));
        toReturn.TradeRequests.AddRange(TradeRequests.Select(it => new TradeRequest(it.RequestingCiv, it.Trade.Clone())));
        toReturn.NaturalWonders.AddRange(NaturalWonders);
        toReturn.CityStatePersonality = CityStatePersonality;
        toReturn.CityStateResource = CityStateResource;
        toReturn.CityStateUniqueUnit = CityStateUniqueUnit;
        toReturn.FlagsCountdown.PutAll(FlagsCountdown);
        toReturn.TemporaryUniques.AddRange(TemporaryUniques.Select(it => it.Clone()));
        toReturn.HasEverOwnedOriginalCapital = HasEverOwnedOriginalCapital;
        foreach (var impassable in PassableImpassables) toReturn.PassableImpassables.Add(impassable);
        toReturn.NumMinorCivsAttacked = NumMinorCivsAttacked;
        toReturn.TotalCultureForContests = TotalCultureForContests;
        toReturn.TotalFaithForContests = TotalFaithForContests;
        toReturn.AttacksSinceTurnStart = AttacksSinceTurnStart.Select(it => it.Clone()).ToList();
        toReturn.HasMovedAutomatedUnits = HasMovedAutomatedUnits;
        toReturn.StatsHistory = StatsHistory.Clone();
        toReturn.ResourceStockpiles = ResourceStockpiles.Clone();
        return CopyCultureTo(CopyCiv5AiTo(toReturn));
    }

    #region pure functions
    public DifficultyObject GetDifficulty()
    {
        if (IsHuman()) return GameInfo.GetDifficulty();
        var aiDifficultyLevel = GameInfo.GetDifficulty().AiDifficultyLevel;
        if (aiDifficultyLevel != null && GameInfo.Ruleset.Difficulties.TryGetValue(aiDifficultyLevel, out var aiDifficulty))
            return aiDifficulty;
        if (GameInfo.Ruleset.Difficulties.TryGetValue("Chieftain", out var chieftainDifficulty)) return chieftainDifficulty;
        return GameInfo.Ruleset.Difficulties.Values.First();
    }

    /// <summary>Makes this civilization meet <paramref name="civInfo"/> and returns the DiplomacyManager</summary>
    public DiplomacyManager GetDiplomacyManagerOrMeet(Civilization civInfo)
    {
        if (!Knows(civInfo)) DiplomacyFunctions.MakeCivilizationsMeet(civInfo);
        return GetDiplomacyManager(civInfo.CivId).NotNull();
    }
    public DiplomacyManager? GetDiplomacyManager(Civilization civInfo) => GetDiplomacyManager(civInfo.CivId);
    public DiplomacyManager? GetDiplomacyManager(string civId) => Diplomacy.TryGetValue(civId, out var manager) ? manager : null;

    public Proximity GetProximity(Civilization civInfo) => GetProximity(civInfo.CivId);
    public Proximity GetProximity(string civId) => Proximity.TryGetValue(civId, out var proximity) ? proximity : ProximityLevel.None;

    /// <summary>
    /// Returns only undefeated civs, aka the ones we care about
    ///
    /// Note: Currently the implementation of <c>updateAllyCivForCityState</c> will cause the diplomacy map of
    /// city-states to contain the barbarians. Therefore, <see cref="GetKnownCivs"/> will **not** list the barbarians
    /// for major civs, but **will** do so for city-states after some gameplay.
    ///
    /// <c>ForEachKnownCiv</c> is faster, for cases that require high perf
    /// </summary>
    public IEnumerable<Civilization> GetKnownCivs() => Diplomacy.Values.Select(it => it.OtherCiv)
        .Where(it => !it.IsDefeated() && !it.IsSpectator());

    /// <summary><c>ForEachKnownCiv</c> is faster, for cases that require high perf</summary>
    public IEnumerable<Civilization> GetKnownCivsWithSpectators() => Diplomacy.Values.Select(it => it.OtherCiv)
        .Where(it => !it.IsDefeated());

    public void ForEachKnownCiv(bool withSpectators, Action<Civilization> op)
    {
        foreach (var manager in Diplomacy.Values)
        {
            if (!manager.OtherCiv.IsDefeated() && (withSpectators || !manager.OtherCiv.IsSpectator()))
                op(manager.OtherCiv);
        }
    }

    public void ForEachKnownCiv(Action<Civilization> op) => ForEachKnownCiv(false, op);

    public bool Knows(string otherCivName) => Diplomacy.ContainsKey(otherCivName);
    public bool Knows(Civilization otherCiv) => Knows(otherCiv.CivId);
    public CityObject? GetCapital(bool firstCityIfNoCapital = true) =>
        Cities.FirstOrDefault(it => it.IsCapital()) ?? (firstCityIfNoCapital ? Cities.FirstOrDefault() : null);

    public bool IsHuman() => PlayerType == PlayerType.Human;
    public bool IsAI() => PlayerType == PlayerType.AI;
    public bool IsAIOrAutoPlaying()
    {
        if (PlayerType == PlayerType.AI) return true;
        if (GameInfo.IsSimulation()) return true;
        // Kotlin: worldScreen.selectedGameView.civView.getCiv() == this && worldScreen.autoPlay.isAutoPlaying(); the auto-played civ is the one the command named (T228).
        return GameInfo.ClientState.AutoPlay?.IsAutoPlayingFor(CivId) == true;
    }

    public bool IsOneCityChallenger() => PlayerType == PlayerType.Human && GameInfo.GameParameters.OneCityChallenge;
    /// <summary>Always false for AI since currentPlayerCiv is only set for human players</summary>
    public bool IsCurrentPlayer() => ReferenceEquals(GameInfo.CurrentPlayerCiv, this);
    public bool IsMajorCiv() => Nation.IsMajorCiv;
    public bool IsMinorCiv() => Nation.IsCityState || Nation.IsBarbarian;

    [JsonIgnore]
    public bool IsCityState => Nation.IsCityState;
    [JsonIgnore]
    public bool IsBarbarian => Nation.IsBarbarian;

    public bool IsSpectator() => Nation.IsSpectator;
    public bool IsAlive() => !IsDefeated();

    /// <summary>
    /// A human player who lost in singleplayer keeps playing as a de-facto spectator, and should get the same map visibility.
    /// GameInfo.turns &gt; 0 guards against the setup phase, where every civ is briefly "defeated" (zero units) before starting units are placed.
    /// </summary>
    public bool HasSpectatorVision() => IsSpectator() || TurnState.DefeatedSeesWholeMap(this);

    public bool HasMetCivTerritory(Civilization otherCiv) =>
        otherCiv.GetCivTerritory().Any(it => GameInfo.TileMap[it].IsExplored(this));
    public int GetCompletedPolicyBranchesCount() => Policies.AdoptedPolicies.Count(Policy.IsBranchCompleteByName);
    private IEnumerable<HexCoord> GetCivTerritory() => Cities.SelectMany(it => it.Tiles);

    public List<string> GetPreferredVictoryTypes()
    {
        // A victory this civilization cannot achieve is not worth working towards
        var victoryTypes = VictoryManager.GetAvailableVictories().Select(it => it.Name).ToList();
        if (victoryTypes.Count == 1)
            return [victoryTypes[0]]; // That is the most relevant one
        var victoryType = new List<string> { Nation.PreferredVictoryType, GetPersonality().PreferredVictoryType }
            .Where(it => victoryTypes.Contains(it)).ToList();
        return victoryType.Count != 0 ? victoryType : [Constants.NeutralVictoryType];
    }

    public PersonalityObject GetPersonality()
    {
        if (!IsAIOrAutoPlaying()) return PersonalityObject.NeutralPersonality;
        return Nation.Personality != null && GameInfo.Ruleset.Personalities.TryGetValue(Nation.Personality, out var personality)
            ? personality
            : PersonalityObject.NeutralPersonality;
    }

    public void UpdateStatsForNextTurn()
    {
        int previousHappiness = Stats.Happiness;
        // Kotlin Iterable<Float>.sum() accumulates in Float
        float happinessSum = 0f;
        foreach (var (_, value) in Stats.GetHappinessBreakdown()) happinessSum += value;
        Stats.Happiness = KotlinMath.RoundToInt(happinessSum);
        if (Stats.Happiness != previousHappiness && GameInfo.Ruleset.AllHappinessLevelsThatAffectUniques.Any(it =>
                Stats.Happiness < it != previousHappiness < it)) // If move from being below them to not, or vice versa
            foreach (var city in Cities) city.CityStats.Update(updateCivStats: false);

        var newStats = new Models.StatTypes.Stats();
        foreach (var statsForTurn in Stats.GetStatMapForNextTurn().Values) newStats.Add(statsForTurn);
        Stats.StatsForNextTurn = Policies.InAnarchy ? Policies.WithoutAnarchyStats(newStats) : newStats; // B014: anarchy stops culture and science
    }

    public int GetHappiness() => Stats.Happiness;

    public bool CanSeeResource(TileResource? resource)
    {
        if (resource == null) return false;
        return Tech.IsRevealed(resource);
    }

    /// <summary>Note that for stockpiled resources, this gives by how much it grows per turn, not current amount</summary>
    public ResourceSupplyList GetCivResourceSupply() => SummarizedCivResourceSupply;

    /// <summary>
    /// Preserves some origins for resources so we can separate them for trades
    /// Stockpiled uniques cannot be traded currently
    /// </summary>
    public ResourceSupplyList GetPerTurnResourcesWithOriginsForTrade()
    {
        var newResourceSupplyList = new ResourceSupplyList(keepZeroAmounts: true);

        foreach (var resourceSupply in DetailedCivResources)
        {
            if (resourceSupply.Resource.IsStockpiled) continue;
            if (((IHasUniques)resourceSupply.Resource).HasUnique(UniqueType.CannotBeTraded, State)) continue;
            // If we got it from another trade or from a CS, preserve the origin
            if (resourceSupply.IsCityStateOrTradeOrigin())
            {
                newResourceSupplyList.Add(new ResourceSupplyList.ResourceSupply(resourceSupply.Resource, resourceSupply.Origin, resourceSupply.Amount));
                newResourceSupplyList.Add(resourceSupply.Resource, Constants.Tradable, 0); // Still add an empty "tradable" entry so it shows up in the list
            }
            else
                newResourceSupplyList.Add(resourceSupply.Resource, Constants.Tradable, resourceSupply.Amount);
        }
        return newResourceSupplyList;
    }

    public ResourceSupplyList GetStockpiledResourcesForTrade()
    {
        var newResourceSupplyList = new ResourceSupplyList(keepZeroAmounts: false);

        foreach (var resourceSupply in DetailedCivResources)
        {
            var resource = resourceSupply.Resource;
            if (!resource.IsStockpiled) continue;
            var resourceUniques = (IHasUniques)resource;
            if (resourceUniques.HasUnique(UniqueType.CannotBeTraded, State)) continue;
            if (!resourceUniques.HasUnique(UniqueType.AiWillSellAt, State) && !resourceUniques.HasUnique(UniqueType.AiWillBuyAt, State)) continue;

            newResourceSupplyList.Add(resource, Constants.Tradable, resourceSupply.Amount);
        }
        return newResourceSupplyList;
    }

    public bool IsCapitalConnectedToCity(CityObject city) => Cache.CitiesConnectedToCapitalToMediums.ContainsKey(city);

    /// <summary>
    /// Returns a dictionary of ALL resource names, and the amount that the civ has of each
    /// Stockpiled resources return the stockpiled amount
    /// </summary>
    public JavaHashMap<string, int> GetCivResourcesByName()
    {
        var hashMap = new JavaHashMap<string, int>(GameInfo.Ruleset.TileResources.Count);
        foreach (var resource in GameInfo.Ruleset.TileResources.Keys) hashMap[resource] = 0;
        foreach (var entry in GetCivResourceSupply())
            if (!entry.Resource.IsStockpiled)
                hashMap[entry.Resource.Name] = entry.Amount;
        foreach (var (key, value) in ResourceStockpiles)
            hashMap[key] = value;
        return hashMap;
    }

    /// <summary>
    /// Gets the number of resources available to this city
    /// Does not include city-wide resources
    /// Returns 0 for undefined resources
    /// </summary>
    public int GetResourceAmount(string resourceName)
    {
        int stockpileValue = ResourceStockpiles[resourceName];
        if (stockpileValue != 0) return stockpileValue;
        return GetCivResourceSupply().FirstOrDefault(it => !it.Resource.IsStockpiled && it.Resource.Name == resourceName)?.Amount ?? 0;
    }

    /// <summary>
    /// Gets the number of resources available to this city
    /// Does not include city-wide resources
    /// Returns 0 for undefined resources
    /// </summary>
    public int GetResourceAmount(TileResource resource)
    {
        if (resource.IsStockpiled) return ResourceStockpiles[resource.Name];
        return GetCivResourceSupply().FirstOrDefault(it => it.Resource == resource)?.Amount ?? 0;
    }

    /// <summary>
    /// Returns the resource production modifier as a multiplier.
    /// For example: 1.0f means no change, 2.0f results in double production.
    /// </summary>
    public float GetResourceModifier(TileResource resource)
    {
        float finalModifier = 1f;

        ForEachMatchingUnique(UniqueType.PercentResourceProduction, unique =>
        {
            if (resource.MatchesFilter(unique.Params[1]))
                finalModifier += ParseFloat(unique.Params[0]) / 100f;
        });

        return finalModifier;
    }

    private static float ParseFloat(string text) =>
        UniqueParameterTypeExtensions.ToFloatOrNull(text) ?? throw new FormatException($"Invalid float: \"{text}\"");

    public bool HasResource(string resourceName) => GetResourceAmount(resourceName) > 0;
    public bool HasResource(TileResource resource) => GetResourceAmount(resource) > 0;
    #endregion
}
