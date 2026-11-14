using System.Collections.Frozen;

namespace Unciv.Assets;

/// <summary>
/// T300: the generic pictogram of an icon category (a gem for resources, a hammer for improvements, a house for buildings...), drawn with plain polygons and circles in
/// integer maths (4 sub-samples per pixel) instead of the initials the placeholder icons used to show. A key without art in any pack gets its category's pictogram,
/// so no icon of the client is ever a pair of letters; the name stays available as tooltip or label text next to it.
/// </summary>
public static class Pictograms
{
    public enum Kind
    {
        Generic,
        Gem,
        Hammer,
        House,
        Flask,
        Sword,
        Scroll,
        Chevrons,
        Coin,
        Pennant,
        Grid,
        Banner,
        Arrow,
        Bell,
    }

    /// <summary>A light (the glyph colour) or dark (outline colour, for details) polygon in a 0..100 box, or a circle when <see cref="Radius"/> is above 0 (centre in the first two coordinates).</summary>
    internal readonly record struct Piece(bool Dark, int[] Pts, int Radius = 0);

    internal static Piece L(params int[] pts) => new(false, pts);

    internal static Piece D(params int[] pts) => new(true, pts);

    internal static Piece Disc(bool dark, int x, int y, int r) => new(dark, [x, y], r);

    private static readonly FrozenDictionary<Kind, Piece[]> Shapes = new Dictionary<Kind, Piece[]>()
    {
        [Kind.Generic] = [L(50, 6, 94, 50, 50, 94, 6, 50), Disc(true, 50, 50, 12)],
        [Kind.Gem] = [L(50, 8, 90, 36, 50, 94, 10, 36), D(10, 35, 90, 35, 90, 40, 10, 40), D(30, 38, 36, 38, 54, 88, 48, 88), D(70, 38, 64, 38, 46, 88, 52, 88)],
        [Kind.Hammer] = [L(12, 10, 88, 10, 88, 34, 12, 34), L(42, 34, 58, 34, 58, 94, 42, 94), D(42, 34, 58, 34, 58, 39, 42, 39)],
        [Kind.House] = [L(50, 6, 96, 36, 4, 36), L(10, 40, 90, 40, 90, 48, 10, 48), L(18, 52, 31, 52, 31, 80, 18, 80), L(44, 52, 57, 52, 57, 80, 44, 80), L(69, 52, 82, 52, 82, 80, 69, 80), L(8, 84, 92, 84, 92, 94, 8, 94)],
        [Kind.Flask] = [L(34, 6, 66, 6, 66, 16, 34, 16), L(40, 16, 60, 16, 60, 36, 40, 36), L(40, 34, 60, 34, 92, 92, 8, 92), D(22, 70, 78, 70, 86, 84, 14, 84), Disc(true, 46, 54, 5)],
        [Kind.Sword] = [L(50, 3, 60, 16, 60, 64, 40, 64, 40, 16), L(22, 64, 78, 64, 78, 73, 22, 73), L(45, 73, 55, 73, 55, 88, 45, 88), Disc(false, 50, 93, 6), D(48, 16, 52, 16, 52, 58, 48, 58)],
        [Kind.Scroll] = [L(20, 8, 66, 8, 80, 24, 80, 92, 20, 92), D(66, 8, 66, 24, 80, 24), D(30, 36, 70, 36, 70, 43, 30, 43), D(30, 54, 70, 54, 70, 61, 30, 61), D(30, 72, 60, 72, 60, 79, 30, 79)],
        [Kind.Chevrons] = [L(50, 8, 92, 40, 80, 52, 50, 28, 20, 52, 8, 40), L(50, 50, 92, 82, 80, 94, 50, 70, 20, 94, 8, 82)],
        [Kind.Coin] = [Disc(false, 50, 50, 46), Disc(true, 50, 50, 35), Disc(false, 50, 50, 29), D(46, 30, 54, 30, 54, 70, 46, 70)],
        [Kind.Pennant] = [L(18, 6, 28, 6, 28, 94, 18, 94), L(28, 10, 90, 34, 28, 58)],
        [Kind.Grid] = [L(12, 12, 46, 12, 46, 46, 12, 46), L(54, 12, 88, 12, 88, 46, 54, 46), L(12, 54, 46, 54, 46, 88, 12, 88), L(54, 54, 88, 54, 88, 88, 54, 88)],
        [Kind.Banner] = [L(12, 6, 22, 6, 22, 94, 12, 94), L(22, 10, 90, 10, 90, 62, 22, 62), D(22, 30, 90, 30, 90, 40, 22, 40)],
        [Kind.Arrow] = [L(6, 38, 56, 38, 56, 14, 96, 50, 56, 86, 56, 62, 6, 62)],
        [Kind.Bell] = [Disc(false, 50, 40, 30), L(20, 40, 80, 40, 90, 76, 10, 76), L(6, 76, 94, 76, 94, 85, 6, 85), Disc(false, 50, 91, 7)],
    }.ToFrozenDictionary();

    /// <summary>The pictogram of an icon category (<c>icon/resource</c> and so on); any other category gets the generic diamond with a dot.</summary>
    public static Kind KindFor(string category) => category switch
    {
        RulesetAssetKeys.ResourceIcon => Kind.Gem,
        RulesetAssetKeys.ImprovementIcon => Kind.Hammer,
        RulesetAssetKeys.BuildingIcon => Kind.House,
        RulesetAssetKeys.TechIcon => Kind.Flask,
        RulesetAssetKeys.UnitIcon => Kind.Sword,
        RulesetAssetKeys.PolicyIcon => Kind.Scroll,
        RulesetAssetKeys.PromotionIcon => Kind.Chevrons,
        RulesetAssetKeys.StatIcon => Kind.Coin,
        RulesetAssetKeys.StatusIcon => Kind.Pennant,
        RulesetAssetKeys.UiIcon => Kind.Grid,
        RulesetAssetKeys.NationIcon => Kind.Banner,
        RulesetAssetKeys.ActionIcon => Kind.Arrow,
        RulesetAssetKeys.NotificationIcon => Kind.Bell,
        _ => Kind.Generic,
    };

    /// <summary>
    /// Paints <paramref name="kind"/> into the square RGBA buffer, centred at (<paramref name="centreX"/>, <paramref name="centreY"/>) pixels and <paramref name="box"/> pixels wide:
    /// glyph in <see cref="PlaceholderPalette.IconGlyph"/>, details in <see cref="PlaceholderPalette.IconOutline"/>, anti-aliased over what is already there.
    /// </summary>
    public static void Draw(byte[] rgba, int size, Kind kind, int box, int centreX, int centreY) =>
        DrawPieces(rgba, size, Shapes[kind], box, centreX, centreY);

    internal static void DrawPieces(byte[] rgba, int size, IReadOnlyList<Piece> rawPieces, int box, int centreX, int centreY)
    {
        var pieces = rawPieces.Select(p => ToQuarterPixels(p, box, centreX, centreY)).ToArray();
        var light = PlaceholderPalette.IconGlyph;
        var dark = PlaceholderPalette.IconOutline;
        var from = Math.Max(0, centreY - box / 2 - 1);
        var to = Math.Min(size - 1, centreY + box / 2 + 1);
        var left = Math.Max(0, centreX - box / 2 - 1);
        var right = Math.Min(size - 1, centreX + box / 2 + 1);
        for (var y = from; y <= to; y++)
        {
            for (var x = left; x <= right; x++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (var s = 0; s < 4; s++)
                {
                    var u = 4 * x + 1 + 2 * (s & 1);
                    var v = 4 * y + 1 + 2 * (s >> 1);
                    var hit = -1;
                    for (var i = 0; i < pieces.Length; i++)
                    {
                        if (Covers(pieces[i], u, v)) hit = i;
                    }
                    if (hit < 0) continue;
                    var c = pieces[hit].Dark ? dark : light;
                    r += c.R; g += c.G; b += c.B; n++;
                }
                if (n == 0) continue;
                var o = (y * size + x) * 4;
                if (rgba[o + 3] == 0)
                {
                    rgba[o] = (byte)(r / n); rgba[o + 1] = (byte)(g / n); rgba[o + 2] = (byte)(b / n); rgba[o + 3] = (byte)(n * 255 / 4);
                }
                else
                {
                    rgba[o] = (byte)((rgba[o] * (4 - n) + r) / 4);
                    rgba[o + 1] = (byte)((rgba[o + 1] * (4 - n) + g) / 4);
                    rgba[o + 2] = (byte)((rgba[o + 2] * (4 - n) + b) / 4);
                }
            }
        }
    }

    private static Piece ToQuarterPixels(Piece piece, int box, int centreX, int centreY)
    {
        var originX = 4 * centreX - 2 * box;
        var originY = 4 * centreY - 2 * box;
        var pts = new int[piece.Pts.Length];
        for (var i = 0; i < pts.Length; i += 2)
        {
            pts[i] = originX + piece.Pts[i] * 4 * box / 100;
            pts[i + 1] = originY + piece.Pts[i + 1] * 4 * box / 100;
        }
        return piece with { Pts = pts, Radius = piece.Radius * 4 * box / 100 };
    }

    private static bool Covers(Piece piece, int u, int v)
    {
        var p = piece.Pts;
        if (piece.Radius > 0)
        {
            long dx = u - p[0], dy = v - p[1];
            return dx * dx + dy * dy <= (long)piece.Radius * piece.Radius;
        }
        var inside = false;
        for (int i = 0, j = p.Length - 2; i < p.Length; j = i, i += 2)
        {
            var (xi, yi, xj, yj) = (p[i], p[i + 1], p[j], p[j + 1]);
            if ((yi > v) == (yj > v)) continue;
            // u is left of the edge at height v  <=>  u < xi + (v - yi) * (xj - xi) / (yj - yi), compared without division
            long lhs = (long)(u - xi) * (yj - yi);
            long rhs = (long)(v - yi) * (xj - xi);
            if (yj > yi ? lhs < rhs : lhs > rhs) inside = !inside;
        }
        return inside;
    }
}
