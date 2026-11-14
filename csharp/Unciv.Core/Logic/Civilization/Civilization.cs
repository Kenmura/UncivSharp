using System.Text.Json.Serialization;
using Unciv.Json;
using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Logic.Civilization.Managers;
using Unciv.Logic.Civilization.Transients;
using Unciv.Logic.Map;
using Unciv.Logic.Trade;
using Unciv.Models;
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

public partial class Civilization : IsPartOfGameInfoSerialization
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

    /// <summary>This is for performance since every movement calculation depends on this, see MapUnit comment</summary>
    [JsonIgnore]
    public bool HasActiveEnemyMovementPenalty { get; set; }

    /// <summary>Same as above variable</summary>
    [JsonIgnore]
    public IEnumerable<UniqueObject>? EnemyMovementPenaltyUniques { get; set; }

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
        otherCiv.GetCivTerritory().Any(it => GameInfo.TileMap[it].IsExplored((IGameContextCivilization)this));


    public PersonalityObject GetPersonality()
    {
        if (!IsAIOrAutoPlaying()) return PersonalityObject.NeutralPersonality;
        return Nation.Personality != null && GameInfo.Ruleset.Personalities.TryGetValue(Nation.Personality, out var personality)
            ? personality
            : PersonalityObject.NeutralPersonality;
    }

    #endregion
}
