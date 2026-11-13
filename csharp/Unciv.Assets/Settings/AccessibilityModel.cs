using Unciv.Logic.Map;
using Unciv.Net.Visibility;

namespace Unciv.GodotClient.Settings;

/// <summary>
/// T352: Accessibility modes and helpers (Godot-free): colourblind-safe civ palettes, non-colour owner/unit state cues,
/// text-scale bounds, keyboard map-cursor stepping, and the Options screen accessibility help text.
/// </summary>
public enum ColorblindMode
{
    Off = 0,
    Deuteranopia = 1,
    Protanopia = 2,
    Tritanopia = 3,
    HighContrast = 4,
}

public static class AccessibilityModel
{
    public const float MinTextScale = 0.85f;
    public const float MaxTextScale = 1.75f;
    public const float TextScaleStep = 0.15f;

    public static readonly IReadOnlyList<ColorblindMode> Modes =
    [
        ColorblindMode.Off,
        ColorblindMode.Deuteranopia,
        ColorblindMode.Protanopia,
        ColorblindMode.Tritanopia,
        ColorblindMode.HighContrast,
    ];

    public static string Label(ColorblindMode mode) => mode switch
    {
        ColorblindMode.Deuteranopia => "Deuteranopia (red-green)",
        ColorblindMode.Protanopia => "Protanopia (red-green)",
        ColorblindMode.Tritanopia => "Tritanopia (blue-yellow)",
        ColorblindMode.HighContrast => "High contrast",
        _ => "Off",
    };

    public static ColorblindMode ParseColorblindMode(string? value) =>
        Enum.TryParse<ColorblindMode>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : ColorblindMode.Off;

    public static float ClampTextScale(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, MinTextScale, MaxTextScale) : 1f;

    public static float StepTextScale(float current, int direction) =>
        Math.Clamp(MathF.Round((ClampTextScale(current) + direction * TextScaleStep) * 100f, MidpointRounding.AwayFromZero) / 100f, MinTextScale, MaxTextScale);

    // Okabe-Ito / Tol-inspired colourblind-safe (Outer, Inner) pairs with strong luminance separation.
    private static readonly (Unciv.Assets.Rgb Outer, Unciv.Assets.Rgb Inner)[] RedGreenSafe =
    [
        (new(0x00, 0x72, 0xB2), new(0xFF, 0xFF, 0xFF)), // blue / white
        (new(0xE6, 0x9F, 0x00), new(0x11, 0x11, 0x11)), // orange / near-black
        (new(0x56, 0xB4, 0xE9), new(0x0B, 0x22, 0x39)), // sky blue / navy
        (new(0xF0, 0xE4, 0x42), new(0x1A, 0x1A, 0x1A)), // yellow / charcoal
        (new(0xCC, 0x79, 0xA7), new(0x18, 0x0D, 0x16)), // reddish-purple / dark
        (new(0x00, 0x9E, 0x73), new(0xFF, 0xFF, 0xFF)), // bluish-green / white
        (new(0xD5, 0x5E, 0x00), new(0xFF, 0xFF, 0xFF)), // vermilion / white
        (new(0x33, 0x22, 0x88), new(0xF4, 0xF1, 0xDE)), // indigo / cream
    ];

    private static readonly (Unciv.Assets.Rgb Outer, Unciv.Assets.Rgb Inner)[] BlueYellowSafe =
    [
        (new(0xCC, 0x33, 0x11), new(0xFF, 0xFF, 0xFF)), // crimson / white
        (new(0x00, 0x99, 0x88), new(0xFF, 0xFF, 0xFF)), // teal / white
        (new(0xEE, 0x33, 0x77), new(0xFF, 0xFF, 0xFF)), // magenta / white
        (new(0xEE, 0x77, 0x33), new(0x11, 0x11, 0x11)), // coral / near-black
        (new(0x88, 0x22, 0x55), new(0xF8, 0xF8, 0xF8)), // wine / off-white
        (new(0x11, 0x77, 0x33), new(0xFF, 0xFF, 0xFF)), // forest / white
        (new(0xAA, 0x44, 0x99), new(0xFF, 0xFF, 0xFF)), // purple / white
        (new(0x66, 0x11, 0x00), new(0xFF, 0xE6, 0xCC)), // maroon / peach
    ];

    private static readonly (Unciv.Assets.Rgb Outer, Unciv.Assets.Rgb Inner)[] HighContrastPalette =
    [
        (new(0x00, 0x5A, 0xB5), new(0xFF, 0xFF, 0xFF)),
        (new(0xDC, 0x32, 0x20), new(0xFF, 0xFF, 0xFF)),
        (new(0xFF, 0xC2, 0x0A), new(0x00, 0x00, 0x00)),
        (new(0x00, 0xB4, 0xA0), new(0x00, 0x00, 0x00)),
        (new(0x99, 0x4F, 0x00), new(0xFF, 0xFF, 0xFF)),
        (new(0xE1, 0xBE, 0x6A), new(0x00, 0x00, 0x00)),
        (new(0x40, 0xB0, 0xA6), new(0x00, 0x00, 0x00)),
        (new(0xD4, 0x11, 0x59), new(0xFF, 0xFF, 0xFF)),
    ];

    private static readonly (Unciv.Assets.Rgb Outer, Unciv.Assets.Rgb Inner) BarbarianSafe =
        (new(0x1E, 0x1E, 0x1E), new(0xFF, 0xD1, 0x66));

    /// <summary>
    /// Remaps a civilization's outer and inner colours when a colourblind mode is active; returns the original pair when <see cref="ColorblindMode.Off"/>.
    /// </summary>
    public static (Unciv.Assets.Rgb Outer, Unciv.Assets.Rgb Inner) RemapCivColour(
        ColorblindMode mode, string civId, Unciv.Assets.Rgb outer, Unciv.Assets.Rgb inner)
    {
        if (mode == ColorblindMode.Off) return (outer, inner);
        if (string.Equals(civId, "Barbarians", StringComparison.Ordinal)) return BarbarianSafe;
        var palette = mode switch
        {
            ColorblindMode.Tritanopia => BlueYellowSafe,
            ColorblindMode.HighContrast => HighContrastPalette,
            _ => RedGreenSafe,
        };
        uint hash = 2166136261u;
        foreach (char ch in civId)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return palette[hash % (uint)palette.Length];
    }

    /// <summary>Non-colour ownership/diplomacy cue for units and cities so relation is never conveyed by colour alone.</summary>
    public static string OwnerCue(string? owner, string? viewerCivId, bool isAtWar, bool isAlliedOrPact, bool isBarbarian)
    {
        if (string.IsNullOrEmpty(owner)) return "";
        if (isBarbarian || string.Equals(owner, "Barbarians", StringComparison.Ordinal)) return "✕";
        if (viewerCivId != null && string.Equals(owner, viewerCivId, StringComparison.Ordinal)) return "★";
        if (isAtWar) return "▲";
        if (isAlliedOrPact) return "●";
        return "◆";
    }

    /// <summary>Non-colour unit readiness/state cue.</summary>
    public static string UnitStateCue(UnitDto unit)
    {
        if (unit.IsEmbarked) return "E";
        if (unit.IsFortified) return "F";
        if (unit.Own is not { } own) return string.Empty;
        if (own.Action is { } action)
        {
            if (action.Contains("fortif", StringComparison.OrdinalIgnoreCase)) return "F";
            if (action.Contains("sleep", StringComparison.OrdinalIgnoreCase)) return "Z";
        }
        return own.CurrentMovement > 0f ? "!" : "·";
    }

    /// <summary>Steps a keyboard map cursor by one hex in cardinal screen direction (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static HexCoord StepCursor(HexCoord from, int dx, int dy) =>
        new(from.X + dx, from.Y + dy);

    public const string HelpTitle = "Accessibility";

    public static readonly IReadOnlyList<string> HelpLines =
    [
        "• Colourblind mode: remaps civ borders, unit/city colours, and minimap to a high-contrast palette, and adds non-colour owner (★ own, ● ally, ▲ war, ◆ neutral, ✕ barbarian) and state (! ready, F fortified, Z sleeping, E embarked) cues.",
        "• Text scale: enlarges all UI text (85%–175%) across every screen without shrinking icons or controls.",
        "• Keyboard-only play: Tab / Shift+Tab moves visible focus rings through menus and panels; Enter / Space activates; Esc closes modals. On the map, Shift+Arrows moves the tile cursor, Enter selects or orders, and hotkeys (Space/Enter End turn, N Next unit) run turn actions.",
    ];
}
