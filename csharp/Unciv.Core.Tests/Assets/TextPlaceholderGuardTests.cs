using Unciv.Assets;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;
using RulesetClass = Unciv.Models.Ruleset.Ruleset;

namespace Unciv.Core.Tests.Assets;

/// <summary>
/// T300: no icon of a base ruleset may be a pair of letters. Every object either resolves to a file of the bundled pack or is on the short, named list of objects whose art
/// is not licensed or not shipped (they show the category pictogram, never initials); the generated icon itself carries no name-dependent glyph.
/// </summary>
[Collection("RulesetCache")]
public sealed class TextPlaceholderGuardTests : IDisposable
{
    private static readonly string ClientFolder = FindClientFolder();

    public TextPlaceholderGuardTests()
    {
        RulesetCache.Clear();
        var jsons = Path.Combine(Path.GetDirectoryName(ClientFolder)!, "..", "android", "assets", "jsons");
        RulesetCache.LoadRulesets(new DirectoryRulesetFileSource(jsons, Path.Combine(jsons, "no mods here")), noMods: true);
    }

    public void Dispose() => RulesetCache.Clear();

    private static string FindClientFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Unciv.Godot");
            if (File.Exists(Path.Combine(candidate, "Packs", "unciv", "manifest.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Unciv.Godot/Packs/unciv not found above " + AppContext.BaseDirectory);
    }

    private sealed class DiskFiles : IAssetFiles
    {
        private static string Full(string relativePath) => Path.Combine(ClientFolder, "Packs", relativePath["assets/packs/".Length..]);

        public bool Exists(string relativePath) => File.Exists(Full(relativePath));

        public string? ReadText(string relativePath) => Exists(relativePath) ? File.ReadAllText(Full(relativePath)) : null;
    }

    private static readonly HashSet<string> IconCategories =
    [
        RulesetAssetKeys.ResourceIcon,
        RulesetAssetKeys.ImprovementIcon,
        RulesetAssetKeys.BuildingIcon,
        RulesetAssetKeys.UnitIcon,
        RulesetAssetKeys.PromotionIcon,
        RulesetAssetKeys.TechIcon,
        RulesetAssetKeys.PolicyIcon,
    ];

    private static IEnumerable<AssetKey> BaseKeys(RulesetClass ruleset) => RulesetAssetKeys.KeysFor(ruleset)
        .Where(k => !k.IsDefault && IconCategories.Contains(k.Category));

    [Theory]
    [InlineData(BaseRuleset.Civ_V_Vanilla)]
    [InlineData(BaseRuleset.Civ_V_GnK)]
    [InlineData(BaseRuleset.Civ_V_BNW)]
    public void EveryBaseRulesetObjectHasPackArtOrPerObjectPictogram(BaseRuleset baseRuleset)
    {
        var ruleset = RulesetCache.Get(baseRuleset.FullName())!;
        var resolver = new AssetResolver(new DiskFiles(), RulesetAssetKeys.FallbacksFor(ruleset), basePack: AssetResolver.BundledPack);
        var unresolved = BaseKeys(ruleset).Where(k => resolver.Resolve(k).Source != AssetSourceKind.Pack).ToList();
        var missing = unresolved.Where(k => !ObjectPictograms.Has(k)).Select(k => k.ToString()).Order().ToList();
        Assert.True(missing.Count == 0, $"Objects without pack art or per-object pictogram: {string.Join(", ", missing)}");
        Assert.Empty(resolver.Diagnostics);
    }

    [Fact]
    public void EveryPerObjectPictogramRendersInsideItsBoxAndDiffersFromCategoryDefault()
    {
        foreach (var key in ObjectPictograms.AllKeys)
        {
            var data = new byte[64 * 64 * 4];
            ObjectPictograms.Draw(data, 64, key, 40, 32, 32);
            var inked = Enumerable.Range(0, 64 * 64).Where(p => data[p * 4 + 3] != 0).ToList();
            Assert.InRange(inked.Count, 120, 40 * 40);
            Assert.All(inked, p => Assert.InRange(p % 64, 11, 52));
            Assert.All(inked, p => Assert.InRange(p / 64, 11, 52));

            var categoryData = new byte[64 * 64 * 4];
            Pictograms.Draw(categoryData, 64, Pictograms.KindFor(key.Category), 40, 32, 32);
            Assert.NotEqual(categoryData, data);
        }
    }

    [Fact]
    public void ResolvedPackFilesAreNotTheCategoryDefault()
    {
        var ruleset = RulesetCache.GetVanillaRuleset();
        var resolver = new AssetResolver(new DiskFiles(), RulesetAssetKeys.FallbacksFor(ruleset), basePack: AssetResolver.BundledPack);
        Assert.Equal("icon/resource/Iron", resolver.Resolve(AssetKey.Of(RulesetAssetKeys.ResourceIcon, "Iron")).Matched.ToString());
        Assert.Equal("icon/promotion/Accuracy", resolver.Resolve(AssetKey.Of(RulesetAssetKeys.PromotionIcon, "Accuracy II")).Matched.ToString());
        Assert.Equal("icon/policy/Tradition", resolver.Resolve(AssetKey.Of(RulesetAssetKeys.PolicyIcon, "Tradition Complete")).Matched.ToString());
    }

    [Fact]
    public void PromotionBaseNameStripsOnlyTheTrailingLevelNumeral()
    {
        Assert.Equal("Accuracy", RulesetAssetKeys.PromotionBaseName("Accuracy III"));
        Assert.Equal("Drill", RulesetAssetKeys.PromotionBaseName("Drill II"));
        Assert.Equal("Medic", RulesetAssetKeys.PromotionBaseName("Medic I"));
        Assert.Equal("Indirect Fire", RulesetAssetKeys.PromotionBaseName("Indirect Fire"));
        Assert.Equal("Mobility", RulesetAssetKeys.PromotionBaseName("Mobility"));
    }

    [Fact]
    public void GeneratedIconsHaveNoNameDependentGlyph()
    {
        var glyph = PlaceholderPalette.IconGlyph;
        foreach (var category in new[] { RulesetAssetKeys.ResourceIcon, RulesetAssetKeys.TechIcon, RulesetAssetKeys.UnitIcon, RulesetAssetKeys.BuildingIcon, RulesetAssetKeys.PolicyIcon, RulesetAssetKeys.UiIcon })
        {
            var byShape = new Dictionary<ProceduralImages.IconShape, List<byte[]>>();
            for (var i = 0; i < 80; i++)
            {
                var key = AssetKey.Of(category, "Name" + i * 7 + (char)('A' + i % 26) + (char)('Z' - i % 13));
                var image = ProceduralImages.Icon(key);
                var mask = new byte[image.Rgba.Length / 4];
                for (var p = 0; p < mask.Length; p++)
                {
                    mask[p] = image.Rgba[p * 4] == glyph.R && image.Rgba[p * 4 + 1] == glyph.G && image.Rgba[p * 4 + 2] == glyph.B && image.Rgba[p * 4 + 3] == 255 ? (byte)1 : (byte)0;
                }
                var shape = ProceduralImages.ShapeFor(key);
                if (!byShape.TryGetValue(shape, out var list)) byShape[shape] = list = [];
                list.Add(mask);
            }
            Assert.True(byShape.Count >= (category == RulesetAssetKeys.UiIcon ? 1 : 3)); // interface icons are always rings
            foreach (var masks in byShape.Values.Where(m => m.Count > 1))
            {
                Assert.Contains((byte)1, masks[0]);
                foreach (var mask in masks.Skip(1)) Assert.Equal(masks[0], mask);
            }
        }
    }

    [Fact]
    public void EveryCategoryPictogramIsDistinctAndDrawsInsideItsBox()
    {
        var kinds = new[]
        {
            RulesetAssetKeys.ResourceIcon, RulesetAssetKeys.ImprovementIcon, RulesetAssetKeys.BuildingIcon, RulesetAssetKeys.TechIcon, RulesetAssetKeys.UnitIcon, RulesetAssetKeys.PolicyIcon,
            RulesetAssetKeys.PromotionIcon, RulesetAssetKeys.StatIcon, RulesetAssetKeys.StatusIcon, RulesetAssetKeys.UiIcon, RulesetAssetKeys.NationIcon, RulesetAssetKeys.ActionIcon, RulesetAssetKeys.NotificationIcon,
        }.Select(Pictograms.KindFor).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
        Assert.Equal(Pictograms.Kind.Generic, Pictograms.KindFor("icon/whatever"));
        foreach (var kind in Enum.GetValues<Pictograms.Kind>())
        {
            var data = new byte[64 * 64 * 4];
            Pictograms.Draw(data, 64, kind, 40, 32, 32);
            var inked = Enumerable.Range(0, 64 * 64).Where(p => data[p * 4 + 3] != 0).ToList();
            Assert.InRange(inked.Count, 200, 40 * 40);
            Assert.All(inked, p => Assert.InRange(p % 64, 11, 52));
            Assert.All(inked, p => Assert.InRange(p / 64, 11, 52));
        }
    }
}
