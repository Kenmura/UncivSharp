using Unciv.Assets;
using Unciv.Net.Visibility;

namespace Unciv.Core.Tests.Assets;

public class NotificationsModelTests
{
    [Fact]
    public void APickerActionBeatsATileAndATechAndCarriesItsUnitId()
    {
        var promote = new NotificationActionDto { Kind = "promote", Id = 42 };
        var espionage = new NotificationActionDto { Kind = "espionage" };
        var tech = new NotificationActionDto { Kind = "tech", Name = "Mining" };
        Assert.Equal(new NotificationTarget(NotificationTargetKind.Promotion, 0, 0, null, 42), NotificationsModel.TargetOf([At("unit", 1, 2), promote]));
        Assert.Equal(new NotificationTarget(NotificationTargetKind.Spies, 0, 0, null), NotificationsModel.TargetOf([At("location", 1, 2), tech, espionage]));
        Assert.Equal(NotificationTargetKind.Spies, NotificationsModel.TargetOf([espionage, promote])!.Kind); // the engine's order within the pickers
        Assert.Equal(NotificationTargetKind.Promotion, NotificationsModel.TargetOf([promote, espionage])!.Kind);
    }

    [Fact]
    public void APromoteActionWithoutAUnitIsIgnored()
    {
        Assert.Null(NotificationsModel.TargetOf([new NotificationActionDto { Kind = "promote" }]));
        Assert.Equal(NotificationTargetKind.Tile, NotificationsModel.TargetOf([new NotificationActionDto { Kind = "promote" }, At("unit", 1, 2)])!.Kind);
    }

    private static NotificationDto Note(string text, string category = "General", params NotificationActionDto[] actions) =>
        new() { Category = category, Text = text, Icons = [], Actions = [.. actions] };

    private static NotificationActionDto At(string kind, int x, int y) => new() { Kind = kind, X = x, Y = y };

    [Fact]
    public void RowsAreNewestFirstKeepSnapshotIndexAndDropBrackets()
    {
        var rows = NotificationsModel.Rows([Note("[Rome] met you"), Note("second")]);
        Assert.Equal([1, 0], rows.Select(it => it.Index));
        Assert.Equal("Rome met you", rows[1].Text);
        Assert.Empty(NotificationsModel.Rows([]));
    }

    [Theory]
    [InlineData("location")]
    [InlineData("city")]
    [InlineData("unit")]
    public void PlaceActionsTargetTheTile(string kind)
    {
        var target = NotificationsModel.TargetOf([At(kind, 3, 4)]);
        Assert.Equal(new NotificationTarget(NotificationTargetKind.Tile, 3, 4, null), target);
    }

    [Fact]
    public void TheOriginTileIsAValidTarget()
    {
        Assert.Equal(new NotificationTarget(NotificationTargetKind.Tile, 0, 0, null), NotificationsModel.TargetOf([At("location", 0, 0)]));
    }

    [Fact]
    public void ATileBeatsATechAndTheFirstTileWins()
    {
        var tech = new NotificationActionDto { Kind = "tech", Name = "Mining" };
        Assert.Equal(NotificationTargetKind.Tile, NotificationsModel.TargetOf([tech, At("city", 1, 2), At("location", 5, 6)])!.Kind);
        Assert.Equal(1, NotificationsModel.TargetOf([tech, At("city", 1, 2), At("location", 5, 6)])!.X);
        Assert.Equal("Mining", NotificationsModel.TargetOf([tech])!.Name);
    }

    [Fact]
    public void ActionsWithoutAPlaceOrNameHaveNoTarget()
    {
        Assert.Null(NotificationsModel.TargetOf([]));
        Assert.Null(NotificationsModel.TargetOf([new NotificationActionDto { Kind = "location" }])); // coordinates stripped by the engine for an unseen tile
        Assert.Null(NotificationsModel.TargetOf([new NotificationActionDto { Kind = "tech", Name = "" }]));
        Assert.Null(NotificationsModel.TargetOf([new NotificationActionDto { Kind = "other", X = 1, Y = 1 }]));
    }

    [Fact]
    public void SummaryCountsByCategoryInEngineOrder()
    {
        var rows = NotificationsModel.Rows([Note("a", "War"), Note("b", "Cities"), Note("c", "War"), Note("d", "Zzz"), Note("e", "Production")]);
        Assert.Equal("Turn 3: 5 notifications (1 Production, 2 War, 1 Cities, 1 Zzz)", NotificationsModel.Summary(3, rows));
        Assert.Equal("Turn 4: 2 notifications (1 Aaa, 1 Zzz)", NotificationsModel.Summary(4, NotificationsModel.Rows([Note("a", "Zzz"), Note("b", "Aaa")])));
        Assert.Equal("Turn 3: 1 notification (1 War)", NotificationsModel.Summary(3, NotificationsModel.Rows([Note("a", "War")])));
        Assert.Equal("Turn 12: nothing new", NotificationsModel.Summary(12, []));
    }

    [Fact]
    public void ToggleLabelShowsCountAndState()
    {
        Assert.Equal("Notifications (4)", NotificationsModel.ToggleLabel(4, open: false));
        Assert.Equal("Hide notifications (0)", NotificationsModel.ToggleLabel(0, open: true));
    }

    [Fact]
    public void IconForResolvesPathsRulesetNamesAndCategoryFallbacks()
    {
        var ruleset = new Unciv.Models.Ruleset.Ruleset();
        ruleset.Technologies["Writing"] = new Unciv.Models.Ruleset.Tech.Technology { Name = "Writing" };
        ruleset.Units["Archer"] = new Unciv.Models.Ruleset.Unit.BaseUnit { Name = "Archer" };
        ruleset.Buildings["Monument"] = new Unciv.Models.Ruleset.Building { Name = "Monument" };
        ruleset.TileResources["Iron"] = new Unciv.Models.Ruleset.Tile.TileResource { Name = "Iron" };
        ruleset.TileImprovements["Farm"] = new Unciv.Models.Ruleset.Tile.TileImprovement { Name = "Farm" };
        ruleset.Policies["Tradition"] = new Unciv.Models.Ruleset.Policy { Name = "Tradition" };
        ruleset.Nations["Greece"] = new Unciv.Models.Ruleset.Nation.Nation { Name = "Greece" };

        string firstNotificationIcon = RulesetAssetKeys.NotificationIconNames.First();
        string firstUiIcon = RulesetAssetKeys.UiIconNames.First();
        string firstActionIcon = RulesetAssetKeys.ActionIconNames.First();

        Assert.Equal(AssetKey.Of(RulesetAssetKeys.StatIcon, "Science"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["StatIcons/Science"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.StatIcon, "Gold"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["StatIcons/Acquire"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["StatIcons/Unknown"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Cities"),
            NotificationsModel.IconFor(new NotificationRow(0, "Cities", "t", ["StatIcons/Population"], null)));

        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Diplomacy"),
            NotificationsModel.IconFor(new NotificationRow(0, "Diplomacy", "t", ["OtherIcons/Diplomacy"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, "Pillage"),
            NotificationsModel.IconFor(new NotificationRow(0, "War", "t", ["OtherIcons/Pillage"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, "DisbandUnit"),
            NotificationsModel.IconFor(new NotificationRow(0, "Units", "t", ["OtherIcons/DisbandUnit"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Spy"),
            NotificationsModel.IconFor(new NotificationRow(0, "Espionage", "t", ["OtherIcons/Spy"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Star"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/Quest"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.NationIcon, "CityState"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/CityState"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, "Guard"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/CrosshairB"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Question"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/Question"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, firstUiIcon),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/" + firstUiIcon], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, firstActionIcon),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/" + firstActionIcon], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["OtherIcons/NoSuchOtherIcon"], null)));

        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ImprovementIcon, "Farm"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["ImprovementIcons/Farm"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UnitIcon, "Archer"),
            NotificationsModel.IconFor(new NotificationRow(0, "Units", "t", ["UnitIcons/Archer"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.TechIcon, "Writing"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["TechIcons/Writing"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.BuildingIcon, "Monument"),
            NotificationsModel.IconFor(new NotificationRow(0, "Production", "t", ["BuildingIcons/Monument"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ResourceIcon, "Iron"),
            NotificationsModel.IconFor(new NotificationRow(0, "Trade", "t", ["ResourceIcons/Iron"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.PolicyIcon, "Tradition"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["PolicyIcons/Tradition"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.PromotionIcon, "Shock"),
            NotificationsModel.IconFor(new NotificationRow(0, "Units", "t", ["PromotionIcons/Shock"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.NationIcon, "Greece"),
            NotificationsModel.IconFor(new NotificationRow(0, "Diplomacy", "t", ["NationIcons/Greece"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.NotificationIcon, firstNotificationIcon),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["NotificationIcons/" + firstNotificationIcon], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["NotificationIcons/MissingNote"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, "Fortify"),
            NotificationsModel.IconFor(new NotificationRow(0, "Units", "t", ["UnitActionIcons/Fortify"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["UnknownPrefix/Foo"], null)));

        Assert.Equal(AssetKey.Of(RulesetAssetKeys.TechIcon, "Writing"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["Writing"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UnitIcon, "Archer"),
            NotificationsModel.IconFor(new NotificationRow(0, "Units", "t", ["Archer"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.BuildingIcon, "Monument"),
            NotificationsModel.IconFor(new NotificationRow(0, "Production", "t", ["Monument"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ResourceIcon, "Iron"),
            NotificationsModel.IconFor(new NotificationRow(0, "Trade", "t", ["Iron"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ImprovementIcon, "Farm"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["Farm"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.PolicyIcon, "Tradition"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["Tradition"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.NationIcon, "Greece"),
            NotificationsModel.IconFor(new NotificationRow(0, "Diplomacy", "t", ["Greece"], null), ruleset));

        Assert.Equal(AssetKey.Of(RulesetAssetKeys.StatIcon, "Food"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["Food"], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, firstUiIcon),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", [firstUiIcon], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, firstActionIcon),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", [firstActionIcon], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.NotificationIcon, firstNotificationIcon),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", [firstNotificationIcon], null)));

        Assert.Equal(AssetKey.Of(RulesetAssetKeys.StatIcon, "Gold"),
            NotificationsModel.IconFor(new NotificationRow(0, "Trade", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Diplomacy"),
            NotificationsModel.IconFor(new NotificationRow(0, "Diplomacy", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.StatIcon, "Production"),
            NotificationsModel.IconFor(new NotificationRow(0, "Production", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Shield"),
            NotificationsModel.IconFor(new NotificationRow(0, "Units", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.ActionIcon, "Pillage"),
            NotificationsModel.IconFor(new NotificationRow(0, "War", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.StatIcon, "Faith"),
            NotificationsModel.IconFor(new NotificationRow(0, "Religion", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Spy"),
            NotificationsModel.IconFor(new NotificationRow(0, "Espionage", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Cities"),
            NotificationsModel.IconFor(new NotificationRow(0, "Cities", "t", [], null)));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["UnknownThing"], null), ruleset));
        Assert.Equal(AssetKey.Of(RulesetAssetKeys.UiIcon, "Notifications"),
            NotificationsModel.IconFor(new NotificationRow(0, "General", "t", ["UnknownThing"], null)));
    }
}
