using Unciv.Assets;

namespace Unciv.Core.Tests.Assets;

/// <summary>T300: dark Unciv silhouettes are shown white on the client's dark panels; light ones and empty images are left alone.</summary>
public sealed class IconToneTests
{
    [Fact]
    public void DarkOpaquePixelsAreDarkAndBecomeWhiteKeepingAlpha()
    {
        byte[] rgba = [0, 0, 0, 255, 10, 10, 10, 200, 255, 255, 255, 0];
        Assert.True(IconTone.IsDark(rgba));
        IconTone.MakeWhite(rgba);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 200, 255, 255, 255, 0 }, rgba);
    }

    [Fact]
    public void LightAndTransparentImagesAreNotDark()
    {
        Assert.False(IconTone.IsDark([250, 250, 250, 255, 0, 0, 0, 10]));
        Assert.False(IconTone.IsDark([0, 0, 0, 0]));
        Assert.False(IconTone.IsDark([]));
    }

    [Fact]
    public void OnlyTheGameplayCategoriesAreNormalised()
    {
        Assert.True(IconTone.IsNormalised(RulesetAssetKeys.ResourceIcon));
        Assert.True(IconTone.IsNormalised(RulesetAssetKeys.PromotionIcon));
        Assert.False(IconTone.IsNormalised(RulesetAssetKeys.ActionIcon));
        Assert.False(IconTone.IsNormalised(RulesetAssetKeys.StatIcon));
    }
}
