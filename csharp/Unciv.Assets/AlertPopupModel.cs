using System.Collections.Frozen;
using System.Globalization;
using Unciv.Commands;
using Unciv.Commands.Diplomacy;
using Unciv.Commands.Turn;
using Unciv.Logic;
using Unciv.Logic.Civilization;
using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Net.Visibility;
using Unciv.Queries.Empire;
using Unciv.Utils;

namespace Unciv.Assets;

/// <summary>One button of an alert popup. <see cref="Command"/> is what pressing it submits.</summary>
public sealed record AlertButton(string Id, string Label, GameCommand Command);

/// <summary>C066: how an alert reaches the player. A <see cref="Blocking"/> alert asks for a decision (or the engine will not end the turn until it is answered) and is a modal popup; an <see cref="Ambient"/> one only informs and waits to be reviewed.</summary>
public enum AlertKind
{
    Blocking,
    Ambient,
}

/// <summary>
/// An alert ready to draw. <see cref="Buttons"/> are the engine's answers (a choice only appears when the engine's query said the command would accept it).
/// <see cref="CanDefer"/> means the popup may be closed without answering (the alert stays pending and the HUD offers to review it); a blocking alert cannot.
/// </summary>
public sealed record AlertPopup(int Index, AlertType Type, string Value, string Title, string Body, IReadOnlyList<AlertButton> Buttons, bool Blocking, bool CanDefer, AssetKey? Icon = null)
{
    /// <summary>C066: whether the alert interrupts the player (a modal popup) or only tells something (<see cref="AlertKind.Ambient"/>, a toast in the notification column).</summary>
    public AlertKind Kind => AlertPopupModel.KindOf(this);

    /// <summary>Identifies this alert instance for "decide later" bookkeeping.</summary>
    public string Key => $"{Index.ToString(CultureInfo.InvariantCulture)}:{Type}:{Value}";
}

/// <summary>
/// C013: the popup alerts as plain data, Godot-free so it is unit-tested. <see cref="Table"/> has an entry for every <see cref="AlertType"/>: its title and body
/// wording and the command that answers it (<see cref="PendingDecisions.ResolverOf"/>: ack-alert, respond-to-alert, respond-demand, respond-friendship).
/// Pending alerts come from the engine's <see cref="PendingDecisionsResult"/>; the choices are the ones its query validated. The model decides no legality.
/// </summary>
public static class AlertPopupModel
{
    private sealed record Wording(string Title, string Body);

    // {civ}: the civ named by the alert value; {city}: the city (value is its id); {value}: the raw value; {minor}: the second part of "civ@minor".
    private static readonly FrozenDictionary<AlertType, Wording> Words = new Dictionary<AlertType, Wording>()
    {
        [AlertType.Defeated] = new("Civilization destroyed", "{civ} has been destroyed by your armies."),
        [AlertType.WonderBuilt] = new("Wonder built", "{value} has been built in a faraway land."),
        [AlertType.TechResearched] = new("Technology discovered", "You have discovered the secrets of {value}."),
        [AlertType.WarDeclaration] = new("War declared", "{civ} has declared war."),
        [AlertType.FirstContact] = new("First contact", "You have met {civ}."),
        [AlertType.CityConquered] = new("City conquered", "Your units have taken {city}. What shall become of it?"),
        [AlertType.CityTraded] = new("City received", "{city} has been handed to you in a trade."),
        [AlertType.BorderConflict] = new("Border conflict", "Your borders overlap with the borders of {civ}."),
        [AlertType.TilesStolen] = new("Tiles taken", "{civ} has taken tiles from your cities."),
        [AlertType.DemandToStopSettlingCitiesNear] = new("A demand from {civ}", ""),
        [AlertType.CitySettledNearOtherCivDespiteOurPromise] = new("Broken promise by {civ}", ""),
        [AlertType.DemandToStopSpreadingReligion] = new("A demand from {civ}", ""),
        [AlertType.ReligionSpreadDespiteOurPromise] = new("Broken promise by {civ}", ""),
        [AlertType.DemandToStopSpyingOnUs] = new("A demand from {civ}", ""),
        [AlertType.SpyingOnUsDespiteOurPromise] = new("Broken promise by {civ}", ""),
        [AlertType.DemandToNotAttackUs] = new("A demand from {civ}", ""),
        [AlertType.AttackedUsDespitePromise] = new("Broken promise by {civ}", ""),
        [AlertType.AcceptingDemand] = new("Demand accepted", "{civ} has accepted your demand."),
        [AlertType.RejectingDemand] = new("Demand refused", "{civ} has refused your demand."),
        [AlertType.GoldenAge] = new("Golden Age", "Your empire enters a Golden Age."),
        [AlertType.DeclarationOfFriendship] = new("Declaration of Friendship", "{civ} offers you a Declaration of Friendship."),
        [AlertType.StartIntro] = new("Welcome", "Found your first city to begin your story."),
        [AlertType.DiplomaticMarriage] = new("Diplomatic marriage", "{city} has joined your empire. What shall become of it?"),
        [AlertType.BulliedProtectedMinor] = new("City-state bullied", "{civ} demanded tribute from {minor}, a city-state you protect. How do you respond?"),
        [AlertType.AttackedProtectedMinor] = new("City-state attacked", "{civ} attacked {minor}, a city-state you protect. How do you respond?"),
        [AlertType.AttackedAllyMinor] = new("Ally attacked", "{civ} attacked {minor}, a city-state allied with you. How do you respond?"),
        [AlertType.RecapturedCivilian] = new("Civilian recaptured", "Your units recaptured a civilian unit at {value} that belongs to another civilization. Return it?"),
        [AlertType.GameHasBeenWon] = new("Game won", "Another civilization has won the game."),
        [AlertType.Event] = new("Event", ""),
        [AlertType.Denounced] = new("Denounced", "{civ} has denounced you. Will you declare war?"),
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, string> ChoiceLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["annex"] = "Annex",
        ["puppet"] = "Make it a puppet",
        ["raze"] = "Raze",
        ["liberate"] = "Liberate",
        ["destroy"] = "Destroy",
        ["return"] = "Return it",
        ["keep"] = "Keep it",
        ["war"] = "Declare war",
        ["side"] = "Side with the city-state",
        ["abandon"] = "Abandon the city-state",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every alert type with its title and body text (before names are filled in).</summary>
    public static IReadOnlyDictionary<AlertType, (string Title, string Body)> Table =>
        Words.ToDictionary(it => it.Key, it => (it.Value.Title, it.Value.Body));

    /// <summary>
    /// The first alert to open as a modal popup, or null. Only alerts that ask for a decision interrupt the player (<see cref="IsInformational"/> ones such as first
    /// contact, a built wonder or the welcome only tell something, and in a game where they also arrive during other civs' turns a modal popup for each would be a
    /// nuisance; they wait under "Review alerts"). With <paramref name="reviewing"/> every pending alert counts, in the engine's order. Alerts that were put off
    /// (<paramref name="deferred"/>, by <see cref="AlertPopup.Key"/>) are skipped.
    /// </summary>
    public static AlertPopup? Next(PendingDecisionsResult? pending, ClientSnapshot snapshot, IReadOnlySet<string> deferred, bool reviewing = false)
    {
        if (pending == null) return null;
        foreach (var entry in pending.Decisions)
            if (Describe(entry, snapshot) is { } popup && !deferred.Contains(popup.Key) && (reviewing || KindOf(popup) == AlertKind.Blocking)) return popup;
        return null;
    }

    /// <summary><see cref="AlertKind.Ambient"/> when the alert is only informational and the engine does not require an answer; every alert that asks for a choice, that blocks the turn or that is a declaration of war (C067: act on it) is <see cref="AlertKind.Blocking"/>.</summary>
    public static AlertKind KindOf(AlertPopup popup) => popup.Blocking || !IsInformational(popup) || popup.Type == AlertType.WarDeclaration ? AlertKind.Blocking : AlertKind.Ambient;

    /// <summary>An alert that is only acknowledged: its single answer is OK.</summary>
    public static bool IsInformational(AlertPopup popup) => popup.Buttons is [{ Command: AckAlert }];

    /// <summary>The popup for one pending decision, or null when it is not an alert or carries no detail.</summary>
    public static AlertPopup? Describe(PendingDecisionEntry entry, ClientSnapshot snapshot)
    {
        if (entry.Alert is not { } alert || !Enum.TryParse<AlertType>(alert.Type, out var type) || !Words.TryGetValue(type, out var words)) return null;
        string civ = CivName(snapshot, alert.Value.Split('@')[0]);
        string minor = alert.Value.Contains('@', StringComparison.Ordinal) ? CivName(snapshot, alert.Value.Split('@')[1]) : "";
        string Fill(string text) => text
            .Replace("{civ}", civ, StringComparison.Ordinal)
            .Replace("{minor}", minor, StringComparison.Ordinal)
            .Replace("{city}", CityName(snapshot, alert.Value), StringComparison.Ordinal)
            .Replace("{value}", alert.Value, StringComparison.Ordinal);

        string body = Fill(words.Body);
        var demand = Demand.Values.FirstOrDefault(it => it.DemandAlert == type);
        var violated = Demand.Values.FirstOrDefault(it => it.ViolationDiscoveredAlert == type);
        if (demand != null) body = demand.DemandText;
        else if (violated != null) body = violated.ViolationNoticedText;
        else if (alert.EventText != null) body = NotificationsModel.Plain(alert.EventText);

        var buttons = new List<AlertButton>();
        string resolver = PendingDecisions.ResolverOf(type);
        if (alert.AckOnly) resolver = "ack-alert";
        switch (resolver)
        {
            case "respond-to-alert":
                foreach (var choice in alert.Choices)
                {
                    string label = type == AlertType.Event ? NotificationsModel.Plain(choice.Label) : ChoiceLabels.GetValueOrDefault(choice.Choice, choice.Label);
                    buttons.Add(new AlertButton("Choice_" + choice.Choice, label,
                        new RespondToAlert { Index = entry.Index, AlertType = type, Value = alert.Value, Choice = choice.Choice }));
                }
                if (buttons.Count == 0) buttons.Add(Ok(entry.Index, type, alert.Value)); // nothing the engine would accept: the alert can only be closed
                break;
            case "respond-demand":
                var demandName = demand.NotNull().Name;
                buttons.Add(new AlertButton("Accept", "Accept", new RespondToDemand { CivId = alert.Value, Demand = demandName, Accept = true }));
                buttons.Add(new AlertButton("Refuse", "Refuse", new RespondToDemand { CivId = alert.Value, Demand = demandName, Accept = false }));
                break;
            case "respond-friendship":
                buttons.Add(new AlertButton("Accept", "Accept", new RespondToFriendship { CivId = alert.Value, Accept = true }));
                buttons.Add(new AlertButton("Decline", "Decline", new RespondToFriendship { CivId = alert.Value, Accept = false }));
                break;
            default:
                buttons.Add(Ok(entry.Index, type, alert.Value));
                break;
        }
        bool canDefer = !entry.Blocking && resolver != "ack-alert";
        return new AlertPopup(entry.Index, type, alert.Value, Fill(words.Title), body, buttons, entry.Blocking, canDefer, IconFor(type, alert.Value, snapshot));
    }

    /// <summary>Contextual icon for an alert popup header: nation emblem for civ alerts, war emblem for war, city/tech/building icons where applicable.</summary>
    public static AssetKey IconFor(AlertType type, string value, ClientSnapshot snapshot)
    {
        if (type == AlertType.WarDeclaration) return AssetKey.Of(RulesetAssetKeys.ActionIcon, "Pillage");
        if (type is AlertType.FirstContact or AlertType.DeclarationOfFriendship or AlertType.Denounced
            or AlertType.BorderConflict or AlertType.TilesStolen or AlertType.Defeated
            or AlertType.AcceptingDemand or AlertType.RejectingDemand
            or AlertType.DemandToStopSettlingCitiesNear or AlertType.CitySettledNearOtherCivDespiteOurPromise
            or AlertType.DemandToStopSpreadingReligion or AlertType.ReligionSpreadDespiteOurPromise
            or AlertType.DemandToStopSpyingOnUs or AlertType.SpyingOnUsDespiteOurPromise
            or AlertType.DemandToNotAttackUs or AlertType.AttackedUsDespitePromise
            or AlertType.BulliedProtectedMinor or AlertType.AttackedProtectedMinor or AlertType.AttackedAllyMinor)
        {
            string civId = value.Split('@')[0];
            var civ = snapshot.Civs.FirstOrDefault(it => it.CivId == civId);
            return civ != null
                ? AssetKey.Of(RulesetAssetKeys.NationIcon, civ.IsCityState ? "CityState" : civ.Name)
                : AssetKey.Of(RulesetAssetKeys.UiIcon, "Diplomacy");
        }
        return type switch
        {
            AlertType.CityConquered or AlertType.CityTraded or AlertType.DiplomaticMarriage => AssetKey.Of(RulesetAssetKeys.UiIcon, "Cities"),
            AlertType.TechResearched => string.IsNullOrEmpty(value) ? AssetKey.Of(RulesetAssetKeys.StatIcon, "Science") : AssetKey.Of(RulesetAssetKeys.TechIcon, value),
            AlertType.WonderBuilt => string.IsNullOrEmpty(value) ? AssetKey.Of(RulesetAssetKeys.UiIcon, "Wonders") : AssetKey.Of(RulesetAssetKeys.BuildingIcon, value),
            AlertType.GoldenAge => AssetKey.Of(RulesetAssetKeys.ActionIcon, "StartGoldenAge"),
            AlertType.StartIntro => AssetKey.Of(RulesetAssetKeys.ActionIcon, "FoundCity"),
            AlertType.RecapturedCivilian => AssetKey.Of(RulesetAssetKeys.ActionIcon, "Escort"),
            AlertType.GameHasBeenWon => AssetKey.Of(RulesetAssetKeys.UiIcon, "Score"),
            _ => AssetKey.Of(RulesetAssetKeys.UiIcon, "ExclamationMark"),
        };
    }

    /// <summary>The button that brings back the alerts that were put off.</summary>
    public static string ReviewLabel(int count) => "Review alerts (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// How many pending alerts wait for the player to review them: the informational ones and the ones that were put off. The welcome alert is not counted: every new
    /// game starts with it, and a standing "Review alerts (1)" would sit on every screen; it is still opened by a review.
    /// </summary>
    public static int WaitingCount(PendingDecisionsResult? pending, ClientSnapshot snapshot, IReadOnlySet<string> deferred) =>
        pending == null ? 0 : pending.Decisions.Count(it => Describe(it, snapshot) is { } popup && (deferred.Contains(popup.Key) || (KindOf(popup) == AlertKind.Ambient && popup.Type != AlertType.StartIntro)));

    private static AlertButton Ok(int index, AlertType type, string value) => new("Ok", "OK", new AckAlert { Index = index, AlertType = type, Value = value });

    private static string CivName(ClientSnapshot snapshot, string id) => snapshot.Civs.FirstOrDefault(it => it.CivId == id)?.Name ?? id;

    private static string CityName(ClientSnapshot snapshot, string id) => snapshot.Cities.FirstOrDefault(it => it.Own?.Id == id)?.Name ?? "a city";
}
