using System.Globalization;
using Unciv.Models.Ruleset;
using Unciv.Models.StatTypes;
using Unciv.Net.Visibility;

namespace Unciv.Assets;

public enum NotificationTargetKind
{
    /// <summary>Centre the map camera on <see cref="NotificationTarget.X"/>, <see cref="NotificationTarget.Y"/>.</summary>
    Tile,

    /// <summary>Open the tech screen on <see cref="NotificationTarget.Name"/>.</summary>
    Tech,

    /// <summary>C052: select the unit <see cref="NotificationTarget.Id"/> and open its promotion picker.</summary>
    Promotion,

    /// <summary>C052: open the move-spies picker.</summary>
    Spies,
}

/// <summary>Where a click on a notification goes. A tile target only exists for a tile the snapshot builder let through (one the viewer can see).</summary>
public sealed record NotificationTarget(NotificationTargetKind Kind, int X, int Y, string? Name, int Id = 0);

/// <summary>One line of the notification list. <see cref="Index"/> is the position in the snapshot (oldest first); <see cref="Target"/> is null when a click has nowhere to go.</summary>
public sealed record NotificationRow(int Index, string Category, string Text, IReadOnlyList<string> Icons, NotificationTarget? Target);

/// <summary>
/// C013: the notification list and the turn-start summary as plain data, Godot-free so it is unit-tested. The notifications are the viewer's own
/// <see cref="ClientSnapshot.Notifications"/> (already filtered for the viewer: an action that points at a tile the viewer cannot see was dropped by the engine
/// before it reached the client), so nothing here can leak; this class only orders, words and picks a click target.
/// </summary>
public static class NotificationsModel
{
    /// <summary>The categories in the order the summary lists them (the engine's <c>NotificationCategory</c> declaration order).</summary>
    public static readonly IReadOnlyList<string> CategoryOrder =
        ["General", "Trade", "Diplomacy", "Production", "Units", "War", "Religion", "Espionage", "Cities"];

    /// <summary>Newest first (the engine appends; the player wants the latest on top). Ties cannot occur: the order is the position.</summary>
    public static IReadOnlyList<NotificationRow> Rows(IReadOnlyList<NotificationDto> notifications)
    {
        var rows = new List<NotificationRow>(notifications.Count);
        for (int i = notifications.Count - 1; i >= 0; i--)
        {
            var it = notifications[i];
            rows.Add(new NotificationRow(i, it.Category, Plain(it.Text), it.Icons, TargetOf(it.Actions)));
        }
        return rows;
    }

    /// <summary>
    /// The first action that can be followed. A picker (promote a unit, move the spies) wins over a place on the map, which wins over a tech; within a group the engine's order.
    /// Deliberate deviation from Kotlin (C052): the original runs every action of a notification in turn (centre the camera, then open the picker), the client follows one, and a
    /// picker is what the player asked for (the promoted unit is selected, so the map shows it anyway); Kotlin's espionage action opens the espionage screen, here the move-spies picker.
    /// </summary>
    public static NotificationTarget? TargetOf(IReadOnlyList<NotificationActionDto> actions)
    {
        foreach (var action in actions)
        {
            if (action.Kind == "promote" && action.Id is { } id) return new NotificationTarget(NotificationTargetKind.Promotion, 0, 0, null, id);
            if (action.Kind == "espionage") return new NotificationTarget(NotificationTargetKind.Spies, 0, 0, null);
        }
        foreach (var action in actions)
            if (action.Kind is "location" or "city" or "unit" && action.X is { } x && action.Y is { } y)
                return new NotificationTarget(NotificationTargetKind.Tile, x, y, null);
        foreach (var action in actions)
            if (action.Kind == "tech" && !string.IsNullOrEmpty(action.Name))
                return new NotificationTarget(NotificationTargetKind.Tech, 0, 0, action.Name);
        return null;
    }

    /// <summary>The engine writes names in square brackets (<c>[Rome] has declared war</c>) for the translator; without translation they are just noise.</summary>
    public static string Plain(string text) => text.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);

    /// <summary>"Turn 3: 4 notifications (2 Production, 1 War, 1 Cities)", categories in <see cref="CategoryOrder"/>, unknown ones last by name; "Turn 3: nothing new" when empty.</summary>
    public static string Summary(int turn, IReadOnlyList<NotificationRow> rows)
    {
        string head = "Turn " + turn.ToString(CultureInfo.InvariantCulture);
        if (rows.Count == 0) return head + ": nothing new";
        var counts = rows.GroupBy(it => it.Category, StringComparer.Ordinal)
            .OrderBy(it => CategoryRank(it.Key)).ThenBy(it => it.Key, StringComparer.Ordinal)
            .Select(it => $"{it.Count().ToString(CultureInfo.InvariantCulture)} {it.Key}");
        string noun = rows.Count == 1 ? "notification" : "notifications";
        return $"{head}: {rows.Count.ToString(CultureInfo.InvariantCulture)} {noun} ({string.Join(", ", counts)})";
    }

    /// <summary>The header of the list: its toggle shows the count.</summary>
    public static string ToggleLabel(int count, bool open) => (open ? "Hide notifications" : "Notifications") + " (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>Resolves the first recognizable icon from <see cref="NotificationRow.Icons"/> (or falls back by <see cref="NotificationRow.Category"/>) into a pack <see cref="AssetKey"/>.</summary>
    public static AssetKey IconFor(NotificationRow row, Ruleset? ruleset = null)
    {
        foreach (string icon in row.Icons)
        {
            if (ResolveIcon(icon, ruleset) is { } key) return key;
        }
        return CategoryIcon(row.Category);
    }

    private static AssetKey? ResolveIcon(string icon, Ruleset? ruleset)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        int slash = icon.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0 && slash + 1 < icon.Length)
        {
            string prefix = icon[..slash];
            string name = icon[(slash + 1)..];
            return prefix switch
            {
                "StatIcons" => name switch
                {
                    "Population" or "Growth" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Cities"),
                    "Acquire" => AssetKey.Of(RulesetAssetKeys.StatIcon, "Gold"),
                    _ => Enum.TryParse<Stat>(name, out _) ? AssetKey.Of(RulesetAssetKeys.StatIcon, name) : AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
                },
                "OtherIcons" => name switch
                {
                    "Diplomacy" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Diplomacy"),
                    "Pillage" => AssetKey.Of(RulesetAssetKeys.ActionIcon, "Pillage"),
                    "DisbandUnit" => AssetKey.Of(RulesetAssetKeys.ActionIcon, "DisbandUnit"),
                    "Spy" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Spy"),
                    "Quest" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Star"),
                    "CityState" => AssetKey.Of(RulesetAssetKeys.NationIcon, "CityState"),
                    "CrosshairB" => AssetKey.Of(RulesetAssetKeys.ActionIcon, "Guard"),
                    "Question" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Question"),
                    _ => RulesetAssetKeys.UiIconNames.Contains(name, StringComparer.Ordinal) ? AssetKey.Of(RulesetAssetKeys.UiIcon, name)
                        : RulesetAssetKeys.ActionIconNames.Contains(name, StringComparer.Ordinal) ? AssetKey.Of(RulesetAssetKeys.ActionIcon, name)
                        : AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
                },
                "ImprovementIcons" => AssetKey.Of(RulesetAssetKeys.ImprovementIcon, name),
                "UnitIcons" => AssetKey.Of(RulesetAssetKeys.UnitIcon, name),
                "TechIcons" => AssetKey.Of(RulesetAssetKeys.TechIcon, name),
                "BuildingIcons" => AssetKey.Of(RulesetAssetKeys.BuildingIcon, name),
                "ResourceIcons" => AssetKey.Of(RulesetAssetKeys.ResourceIcon, name),
                "PolicyIcons" => AssetKey.Of(RulesetAssetKeys.PolicyIcon, name),
                "PromotionIcons" => AssetKey.Of(RulesetAssetKeys.PromotionIcon, name),
                "NationIcons" => AssetKey.Of(RulesetAssetKeys.NationIcon, name),
                "NotificationIcons" => RulesetAssetKeys.NotificationIconNames.Contains(name, StringComparer.Ordinal)
                    ? AssetKey.Of(RulesetAssetKeys.NotificationIcon, name)
                    : AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
                "UnitActionIcons" => AssetKey.Of(RulesetAssetKeys.ActionIcon, name),
                _ => null,
            };
        }
        if (ruleset != null)
        {
            if (ruleset.Technologies.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.TechIcon, icon);
            if (ruleset.Units.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.UnitIcon, icon);
            if (ruleset.Buildings.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.BuildingIcon, icon);
            if (ruleset.TileResources.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.ResourceIcon, icon);
            if (ruleset.TileImprovements.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.ImprovementIcon, icon);
            if (ruleset.Policies.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.PolicyIcon, icon);
            if (ruleset.Nations.ContainsKey(icon)) return AssetKey.Of(RulesetAssetKeys.NationIcon, icon);
        }
        if (Enum.TryParse<Stat>(icon, out _)) return AssetKey.Of(RulesetAssetKeys.StatIcon, icon);
        if (RulesetAssetKeys.UiIconNames.Contains(icon, StringComparer.Ordinal)) return AssetKey.Of(RulesetAssetKeys.UiIcon, icon);
        if (RulesetAssetKeys.ActionIconNames.Contains(icon, StringComparer.Ordinal)) return AssetKey.Of(RulesetAssetKeys.ActionIcon, icon);
        if (RulesetAssetKeys.NotificationIconNames.Contains(icon, StringComparer.Ordinal)) return AssetKey.Of(RulesetAssetKeys.NotificationIcon, icon);
        return null;
    }

    private static AssetKey CategoryIcon(string category) => category switch
    {
        "Trade" => AssetKey.Of(RulesetAssetKeys.StatIcon, "Gold"),
        "Diplomacy" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Diplomacy"),
        "Production" => AssetKey.Of(RulesetAssetKeys.StatIcon, "Production"),
        "Units" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Shield"),
        "War" => AssetKey.Of(RulesetAssetKeys.ActionIcon, "Pillage"),
        "Religion" => AssetKey.Of(RulesetAssetKeys.StatIcon, "Faith"),
        "Espionage" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Spy"),
        "Cities" => AssetKey.Of(RulesetAssetKeys.UiIcon, "Cities"),
        _ => AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
    };

    private static int CategoryRank(string category)
    {
        for (int i = 0; i < CategoryOrder.Count; i++)
            if (CategoryOrder[i] == category) return i;
        return CategoryOrder.Count;
    }
}
