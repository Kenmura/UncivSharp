using System.Globalization;
using Unciv.Commands;
using Unciv.Commands.Diplomacy;
using Unciv.Net.Visibility;
using Unciv.Queries.Empire;

namespace Unciv.Assets;

/// <summary>One civ of the diplomacy screen's list: a civ the viewer has met. <see cref="Status"/> is the <c>DiplomaticStatus</c> name from the snapshot.</summary>
public sealed record DiplomacyRow(string CivId, string Name, bool IsCityState, string Status)
{
    public string Label => $"{Name}{(IsCityState ? " (city-state)" : "")} - {Status}";

    /// <summary>C073: the civ's flag.</summary>
    public AssetKey Icon => AssetKey.Of(RulesetAssetKeys.NationIcon, Name);

    /// <summary>C073: the second line of the list row: the status, preceded by "City-state" for a city-state.</summary>
    public string Subtitle => IsCityState ? "City-state - " + Status : Status;

}

public enum DiplomacyActionKind
{
    DeclareWar,
    Denounce,
    OfferPeace,
    NegotiatePeace,
    PledgeProtection,
    WithdrawProtection,
    TributeGold,
    TributeWorker,
    Gift,
}

/// <summary>
/// One button of the diplomacy screen. <see cref="Id"/> is the stable node name. <see cref="Enabled"/> is the engine's verdict (<see cref="DiplomacyActionsResult"/>);
/// <see cref="Hint"/> says why a disabled one is. <see cref="NeedsConfirm"/> means the first press only arms the button (<see cref="ConfirmLabel"/>) and the second sends
/// <see cref="Command"/>, so a destructive step is never one mouse accident.
/// </summary>
public sealed record DiplomacyButton(string Id, DiplomacyActionKind Kind, string Label, bool Enabled, bool NeedsConfirm, GameCommand Command, string Hint)
{
    public string ConfirmLabel => "Confirm: " + Label;

    /// <summary>C073: a step that needs a second press is drawn as a danger button (declare war, denounce, revoke protection).</summary>
    public bool Danger => NeedsConfirm;
}

/// <summary>T413: segmented City-State Influence meter geometry and thresholds.</summary>
public sealed record InfluenceMeterSpec(float Influence, bool IsAlly, int FriendThreshold, int AllyThreshold, float Min, float Max,
    float Fraction, float ZeroFraction, float FriendFraction, float AllyFraction);

public enum DiplomacyChipTone { Positive, Negative, Neutral }

/// <summary>T413: structured opinion modifier or diplomatic flag chip.</summary>
public sealed record DiplomacyChipSpec(string Text, DiplomacyChipTone Tone, string Tooltip);

/// <summary>
/// C021: the diplomacy screen's content as plain data, Godot-free so it is unit-tested. It lists the civs the viewer has met (from the viewer's own snapshot, so a
/// civ it has not met never appears), words the engine's panel, quest and action answers, and decides which command a button sends. It computes no game value and
/// decides no legality: every enabled flag is the engine's (<see cref="DiplomacyActionsQuery"/> runs the same validation the command does).
/// </summary>
public static class DiplomacyScreenModel
{
    /// <summary>Status names the engine reports for a city-state the viewer protects.</summary>
    public const string ProtectorStatus = "Protector";

    public const string WarStatus = "War";

    /// <summary>
    /// The met civs: those the snapshot has a relationship with the viewer for, that are alive and neither barbarians nor the viewer. Major civs come first, then
    /// city-states, each by name (ordinal). A civ that only owns a tile or a unit in view is not listed: the viewer has not met it.
    /// </summary>
    public static IReadOnlyList<DiplomacyRow> Rows(ClientSnapshot snapshot)
    {
        var rows = new Dictionary<string, DiplomacyRow>(StringComparer.Ordinal);
        var civs = new Dictionary<string, CivDto>(StringComparer.Ordinal);
        foreach (var civ in snapshot.Civs) civs.TryAdd(civ.CivId, civ);
        foreach (var relation in snapshot.Diplomacy)
        {
            if ((relation.A == snapshot.You) == (relation.B == snapshot.You)) continue; // neither or both is the viewer
            string other = relation.A == snapshot.You ? relation.B : relation.A;
            if (!civs.TryGetValue(other, out var civ) || civ.IsBarbarian || civ.IsDefeated) continue;
            rows.TryAdd(other, new DiplomacyRow(civ.CivId, civ.Name, civ.IsCityState, relation.Status ?? (relation.AtWar ? WarStatus : "Peace")));
        }
        return rows.Values.OrderBy(it => it.IsCityState).ThenBy(it => it.Name, StringComparer.Ordinal).ToList();
    }

    public static string Title(DiplomacyPanelResult panel) => panel.IsCityState ? panel.CivName + " (city-state)" : panel.CivName;

    /// <summary>Status, attitude (or influence for a city-state), treaty and border lines.</summary>
    public static IReadOnlyList<string> InfoLines(DiplomacyPanelResult panel)
    {
        var lines = new List<string> { "Status: " + panel.Status };
        if (panel.IsCityState)
        {
            lines.Add($"Influence: {Number(panel.Influence ?? 0f)} ({panel.Relationship})");
            if (panel.IsAlly) lines.Add("You are its ally");
        }
        else
        {
            lines.Add("Attitude: " + panel.Relationship);
        }
        if (panel.TurnsToPeaceTreaty > 0) lines.Add($"Peace treaty: war is forbidden for {panel.TurnsToPeaceTreaty.ToString(CultureInfo.InvariantCulture)} more turns");
        if (panel.HasOpenBorders) lines.Add("Your units may cross their borders");
        if (panel.TheyHaveOpenBordersWithUs) lines.Add("Their units may cross your borders");
        if (panel.PendingOffersFromThem > 0) lines.Add($"They sent you {panel.PendingOffersFromThem.ToString(CultureInfo.InvariantCulture)} trade offer(s)");
        if (panel.HasPendingOfferFromUs) lines.Add("Your offer waits for their answer");
        return lines;
    }

    /// <summary>The reasons behind the other civ's opinion, then the active diplomatic flags with their turns left; empty when there are none.</summary>
    public static IReadOnlyList<string> ModifierLines(DiplomacyPanelResult panel)
    {
        var lines = new List<string>();
        foreach (var modifier in panel.Modifiers) lines.Add($"{HumanizeKey(modifier.Name)}: {(modifier.Value >= 0f ? "+" : "")}{Number(modifier.Value)}");
        foreach (var flag in panel.Flags) lines.Add($"{HumanizeKey(flag.Name)}: {Number(flag.Value)} turns");
        return lines;
    }

    /// <summary>T413: City-State Influence meter parameters with Friend (30) and Ally (60) threshold fractions, or null for major civs.</summary>
    public static InfluenceMeterSpec? InfluenceMeter(DiplomacyPanelResult panel)
    {
        if (!panel.IsCityState) return null;
        float influence = panel.Influence is float v && float.IsFinite(v) ? v : 0f;
        const int friend = 30;
        const int ally = 60;
        const float min = -60f;
        float max = Math.Max(90f, MathF.Ceiling(Math.Max(influence, ally) / 30f) * 30f);
        float span = max - min;
        float Pos(float value) => Math.Clamp((value - min) / span, 0f, 1f);
        return new InfluenceMeterSpec(influence, panel.IsAlly, friend, ally, min, max, Pos(influence), Pos(0f), Pos(friend), Pos(ally));
    }

    /// <summary>T413: structured color-coded chips for opinion modifiers and active diplomatic flags.</summary>
    public static IReadOnlyList<DiplomacyChipSpec> ModifierChips(DiplomacyPanelResult panel)
    {
        var chips = new List<DiplomacyChipSpec>();
        foreach (var modifier in panel.Modifiers)
        {
            string text = $"{HumanizeKey(modifier.Name)}: {(modifier.Value >= 0f ? "+" : "")}{Number(modifier.Value)}";
            var tone = modifier.Value > 0f ? DiplomacyChipTone.Positive : modifier.Value < 0f ? DiplomacyChipTone.Negative : DiplomacyChipTone.Neutral;
            chips.Add(new DiplomacyChipSpec(text, tone, text));
        }
        foreach (var flag in panel.Flags)
        {
            string text = $"{HumanizeKey(flag.Name)}: {Number(flag.Value)}t";
            string tooltip = $"{HumanizeKey(flag.Name)}: {Number(flag.Value)} turns";
            chips.Add(new DiplomacyChipSpec(text, FlagTone(flag.Name), tooltip));
        }
        return chips;
    }

    private static DiplomacyChipTone FlagTone(string name) => name switch
    {
        "DeclarationOfFriendship" or "DefensivePact" or "ResearchAgreement" or "OpenBorders" or "RecentlyPledgedProtection" or "AngerFreeIntrusion" => DiplomacyChipTone.Positive,
        "Denunciation" or "WaryOf" or "Bullied" or "RecentlyWithdrewProtection" or "DeclaredWarOnUs" or "AgreedToNotSettleNearUs" or "BorderConflict" => DiplomacyChipTone.Negative,
        _ => DiplomacyChipTone.Neutral,
    };

    /// <summary>Turns serialized diplomatic modifier/flag enum names into readable UI text.</summary>
    public static string HumanizeKey(string key) => key switch
    {
        "WarMongerer" => "Warmongering",
        _ => SplitPascalCase(key),
    };

    private static string SplitPascalCase(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var builder = new System.Text.StringBuilder(key.Length + 6);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    /// <summary>A city-state's quests for the viewer, one or two lines each.</summary>
    public static IReadOnlyList<string> QuestLines(CityStateQuestsResult quests)
    {
        if (quests.Quests.Count == 0) return ["No quests"];
        var lines = new List<string>();
        foreach (var quest in quests.Quests)
        {
            string turns = quest.RemainingTurns is { } left ? $", {left.ToString(CultureInfo.InvariantCulture)} turns left" : "";
            lines.Add($"{quest.Name}: {quest.Description} (+{Number(quest.Influence)} influence{turns}{(quest.IsGlobal ? ", all civs" : "")})");
            if (quest.Standing.Length > 0) lines.Add("  " + quest.Standing);
        }
        return lines;
    }

    /// <summary>
    /// The buttons that apply to the civ now, in screen order, each enabled as the engine says. Major civ: declare war (not while at war), denounce, offer peace (only at war).
    /// City-state: gifts, take gold and worker (not at war), pledge or revoke protection, negotiate peace (only at war), declare war (not while at war).
    /// </summary>
    public static IReadOnlyList<DiplomacyButton> Buttons(DiplomacyRow civ, DiplomacyPanelResult panel, DiplomacyActionsResult actions)
    {
        bool atWar = panel.Status == WarStatus;
        string id = civ.CivId;
        var buttons = new List<DiplomacyButton>();
        void Add(DiplomacyActionKind kind, string label, bool enabled, bool confirm, GameCommand command, string hint, string? name = null) =>
            buttons.Add(new DiplomacyButton(name ?? kind.ToString(), kind, label, enabled, confirm, command, hint));

        if (panel.IsCityState)
        {
            foreach (int amount in GiftGold.Amounts)
                Add(DiplomacyActionKind.Gift, $"Gift {amount.ToString(CultureInfo.InvariantCulture)} gold", actions.Gifts.Contains(amount), false,
                    new GiftGold { CivId = id, Amount = amount }, "Not possible now: at war, or not enough gold", "Gift_" + amount.ToString(CultureInfo.InvariantCulture));
            if (!atWar)
            {
                Add(DiplomacyActionKind.TributeGold, "Bully: take gold", actions.TributeGold, false, new TributeGold { CivId = id }, "It is not willing to pay tribute now");
                Add(DiplomacyActionKind.TributeWorker, "Bully: take a worker", actions.TributeWorker, false, new TributeWorker { CivId = id }, "It is not willing to give a worker now");
            }
            if (panel.Status == ProtectorStatus)
                Add(DiplomacyActionKind.WithdrawProtection, "Revoke protection", actions.WithdrawProtection, true, new WithdrawProtection { CivId = id }, "You cannot revoke it yet");
            else
                Add(DiplomacyActionKind.PledgeProtection, "Pledge to protect", actions.PledgeProtection, false, new PledgeProtection { CivId = id }, "You cannot pledge protection now");
            if (atWar)
                Add(DiplomacyActionKind.NegotiatePeace, "Make peace", actions.NegotiateCityStatePeace, false, new NegotiateCityStatePeace { CivId = id }, "It will not make peace now");
        }
        else if (atWar)
        {
            Add(DiplomacyActionKind.OfferPeace, "Offer peace", actions.OfferPeace, false, DiplomacyActionsQuery.PeaceOffer(id), "Peace cannot be offered now");
        }
        if (!atWar)
        {
            Add(DiplomacyActionKind.DeclareWar, "Declare war", actions.DeclareWar, true, new DeclareWar { CivId = id }, "A peace treaty is still in force");
            if (!panel.IsCityState)
                Add(DiplomacyActionKind.Denounce, "Denounce", actions.Denounce, true, new Denounce { CivId = id }, "You cannot denounce them now");
        }
        return buttons;
    }

    private static string Number(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
