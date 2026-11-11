using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Ui;
using Unciv.Harness;
using Unciv.Queries.Empire;
using Unciv.Utils;

namespace Unciv.GodotHarness;

/// <summary>
/// T300: no icon is a pair of letters. The real HUD top bar shows the strategic and luxury resource chips of the owner's bug report (Iron, Pearls, Silver, Truffles, Wheat, Horses...)
/// with pictorial icons, and a gallery shows pack art next to the category pictogram a mod object without art gets.
/// </summary>
public sealed class IconArtScenario : Scenario
{
    public override string Name => "icon-art";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override Vector2I Viewport => new(1280, 720);

    public override IReadOnlyList<string> Checklist =>
    [
        "Is every resource chip an icon you can tell apart at a glance, with no two-letter badge anywhere?",
        "Is a mod object without art (bottom row) a recognisable generic pictogram of its category?",
    ];

    private static readonly string[] Resources = ["Iron", "Pearls", "Silver", "Truffles", "Wheat", "Horses", "Wine", "Oil", "Marble", "Bison"];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session)
    {
        var game = session.Game;
        var localHost = new LocalHost(session, ScenarioGame.HumanA, () => 1000L);
        var screen = new GameScreen();
        host.AddChild(screen);
        var registry = AssetRegistry.ForRuleset(game.Ruleset);
        screen.Start(localHost, registry);
        var hud = screen.Hud;
        var uiTheme = screen.SharedTheme.NotNull();
        hud.HoldBanner = true;
        var bar = new EmpireTopBarResult(12, -3500, 214, 7f, 11f, 3f, 2f, 9, 0, 40, 200,
            Resources.Select((name, i) => new TopBarResource(name, "Strategic", 3 + i)).ToList(), []);
        PanelContainer? gallery = null;

        void Show()
        {
            hud.Update(bar, hud.Action, canEndTurn: true);
            hud.HideBanner();
        }

        Control Cell(AssetKey key, string label)
        {
            var box = new VBoxContainer { Name = "Cell_" + key.Name.Replace(' ', '_') };
            var holder = new CenterContainer();
            holder.AddChild(new IconLabel(uiTheme, key, ""));
            box.AddChild(holder);
            box.AddChild(new Label { Text = label, HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = UiVariants.LabelSmall });
            return box;
        }

        void BuildGallery()
        {
            gallery = new PanelContainer { Name = "IconGallery", ThemeTypeVariation = UiVariants.PanelCard, Theme = uiTheme.Theme };
            gallery.SetAnchorsPreset(Control.LayoutPreset.Center);
            gallery.GrowHorizontal = Control.GrowDirection.Both;
            gallery.GrowVertical = Control.GrowDirection.Both;
            var grid = new GridContainer { Columns = 9 };
            gallery.AddChild(grid);
            string[] resourceNames = ["Gold Ore", "Cotton", "Fish", "Whales", "Spices", "Uranium"];
            foreach (var name in resourceNames) grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.ResourceIcon, name), name));
            grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.ImprovementIcon, "Farm"), "Farm"));
            grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.ImprovementIcon, "Mine"), "Mine"));
            foreach (var name in new[] { "Barracks", "Library", "Granary", "Temple", "Pyramids", "Great Wall" }) grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.BuildingIcon, name), name));
            foreach (var name in new[] { "Agriculture", "Writing", "Bronze Working" }) grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.TechIcon, name), name));
            foreach (var name in new[] { "Warrior", "Archer", "Settler" }) grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.UnitIcon, name), name));
            grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.PolicyIcon, "Aristocracy"), "Aristocracy"));
            grid.AddChild(Cell(AssetKey.Of(RulesetAssetKeys.PromotionIcon, "Accuracy II"), "Accuracy II"));
            foreach (var (category, label) in new[]
            {
                (RulesetAssetKeys.ResourceIcon, "mod resource"), (RulesetAssetKeys.ImprovementIcon, "mod improvement"), (RulesetAssetKeys.BuildingIcon, "mod building"), (RulesetAssetKeys.TechIcon, "mod tech"),
                (RulesetAssetKeys.UnitIcon, "mod unit"), (RulesetAssetKeys.PolicyIcon, "mod policy"), (RulesetAssetKeys.PromotionIcon, "mod promotion"),
            })
                grid.AddChild(Cell(AssetKey.Of(category, "No such " + label), label));
            host.AddChild(gallery);
        }

        bool AllResourceChipsExist() => Resources.All(name => hud.Entries.TryGetValue("Resource:" + name, out var entry) && entry is IconLabel);
        bool ResourcesUsePackArt() => Resources.Where(name => name != "Bison").All(name => registry.Resolve(AssetKey.Of(RulesetAssetKeys.ResourceIcon, name)).Source == AssetSourceKind.Pack);

        return
        [
            new WaitFrames(4),
            new Act("show the resource chips", Show),
            new WaitFrames(3),
            new AssertQuery("a chip per resource, each an icon with its number", AllResourceChipsExist),
            new AssertQuery("every resource of the bug report has pack art (only Bison, which Unciv ships no icon for, gets the generic gem)", ResourcesUsePackArt),
            new AssertQuery("the bar holds them all", () => hud.Entries.Values.All(it => hud.BarRect.Encloses(it.GetGlobalRect()))),
            new Capture("chips", "The top bar with the Iron, Pearls, Silver, Truffles, Wheat, Horses, Wine, Oil, Marble and Bison chips: pictorial icons, no letter badges (Bison, with no art in Unciv, shows the generic gem)"),
            new Act("open the gallery", BuildGallery),
            new WaitFrames(3),
            new Capture("gallery", "Pack art of each gameplay category, and in the last row the generic pictogram of each category for a mod object without art"),
            new Act("remove the gallery", () => gallery?.QueueFree()),
        ];
    }
}
