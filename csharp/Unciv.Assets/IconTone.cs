namespace Unciv.Assets;

/// <summary>
/// T300: Unciv draws most of its gameplay icons (resources, buildings, improvements, units, promotions, policies) as black silhouettes that its own UI puts on a light backdrop,
/// while this client shows them straight on dark panels and the top bar. A pack icon whose opaque pixels are mostly dark is therefore shown as a white silhouette of the same shape.
/// </summary>
public static class IconTone
{
    /// <summary>Categories whose pack art is normalised to a light glyph.</summary>
    public static bool IsNormalised(string category) => category is RulesetAssetKeys.ResourceIcon or RulesetAssetKeys.ImprovementIcon or RulesetAssetKeys.BuildingIcon or RulesetAssetKeys.TechIcon
        or RulesetAssetKeys.UnitIcon or RulesetAssetKeys.PolicyIcon or RulesetAssetKeys.PromotionIcon;

    /// <summary>True when the average brightness of the pixels that are at least half opaque is below 96 (of 255).</summary>
    public static bool IsDark(byte[] rgba)
    {
        long sum = 0, count = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128) continue;
            sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
            count++;
        }
        return count > 0 && sum / (3 * count) < 96;
    }

    /// <summary>Sets every pixel's colour to white and keeps its alpha.</summary>
    public static void MakeWhite(byte[] rgba)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255;
        }
    }
}
