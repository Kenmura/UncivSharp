using Unciv.Assets;
using Unciv.GodotClient.Settings;
using Unciv.Logic.Map;
using Unciv.Net.Visibility;

namespace Unciv.Core.Tests.Godot;

public sealed class AccessibilityModelTests
{
    [Fact]
    public void ParseColorblindMode_HandlesValidAndInvalidValues()
    {
        Assert.Equal(ColorblindMode.Off, AccessibilityModel.ParseColorblindMode(null));
        Assert.Equal(ColorblindMode.Off, AccessibilityModel.ParseColorblindMode("not-a-mode"));
        Assert.Equal(ColorblindMode.Deuteranopia, AccessibilityModel.ParseColorblindMode("deuteranopia"));
        Assert.Equal(ColorblindMode.Protanopia, AccessibilityModel.ParseColorblindMode("Protanopia"));
        Assert.Equal(ColorblindMode.Tritanopia, AccessibilityModel.ParseColorblindMode("Tritanopia"));
        Assert.Equal(ColorblindMode.HighContrast, AccessibilityModel.ParseColorblindMode("HighContrast"));
        foreach (var mode in AccessibilityModel.Modes)
            Assert.False(string.IsNullOrWhiteSpace(AccessibilityModel.Label(mode)));
    }

    [Fact]
    public void TextScale_ClampsAndStepsWithinBounds()
    {
        Assert.Equal(1f, AccessibilityModel.ClampTextScale(float.NaN));
        Assert.Equal(AccessibilityModel.MinTextScale, AccessibilityModel.ClampTextScale(0.1f));
        Assert.Equal(AccessibilityModel.MaxTextScale, AccessibilityModel.ClampTextScale(5f));
        Assert.Equal(1.15f, AccessibilityModel.StepTextScale(1f, 1));
        Assert.Equal(0.85f, AccessibilityModel.StepTextScale(1f, -1));
        Assert.Equal(AccessibilityModel.MaxTextScale, AccessibilityModel.StepTextScale(AccessibilityModel.MaxTextScale, 1));
        Assert.Equal(AccessibilityModel.MinTextScale, AccessibilityModel.StepTextScale(AccessibilityModel.MinTextScale, -1));
    }

    [Fact]
    public void RemapCivColour_PreservesWhenOff_AndRemapsDeterministicallyWhenEnabled()
    {
        var outer = new Rgb(10, 20, 30);
        var inner = new Rgb(200, 210, 220);
        Assert.Equal((outer, inner), AccessibilityModel.RemapCivColour(ColorblindMode.Off, "Rome", outer, inner));

        foreach (var mode in AccessibilityModel.Modes.Where(m => m != ColorblindMode.Off))
        {
            var a = AccessibilityModel.RemapCivColour(mode, "Rome", outer, inner);
            var b = AccessibilityModel.RemapCivColour(mode, "Rome", outer, inner);
            Assert.Equal(a, b);
            Assert.NotEqual((outer, inner), a);

            var barb = AccessibilityModel.RemapCivColour(mode, "Barbarians", outer, inner);
            Assert.NotEqual(a, barb);
        }
    }

    [Fact]
    public void OwnerCue_And_UnitStateCue_ReturnNonColourGlyphs()
    {
        Assert.Equal("", AccessibilityModel.OwnerCue(null, "Rome", false, false, false));
        Assert.Equal("★", AccessibilityModel.OwnerCue("Rome", "Rome", false, false, false));
        Assert.Equal("●", AccessibilityModel.OwnerCue("Greece", "Rome", false, true, false));
        Assert.Equal("▲", AccessibilityModel.OwnerCue("Aztec", "Rome", true, false, false));
        Assert.Equal("✕", AccessibilityModel.OwnerCue("Barbarians", "Rome", false, false, true));
        Assert.Equal("◆", AccessibilityModel.OwnerCue("Egypt", "Rome", false, false, false));

        var ready = new UnitDto
        {
            Id = 1,
            Name = "Warrior",
            Owner = "Rome",
            X = 0,
            Y = 0,
            HealthBucket = 4,
            IsEmbarked = false,
            IsFortified = false,
            Own = new OwnUnitDto
            {
                Health = 100,
                CurrentMovement = 2f,
                Xp = 0,
                Promotions = []
            }
        };
        var exhausted = ready with { Own = ready.Own! with { CurrentMovement = 0f } };
        var fortified = ready with { IsFortified = true };
        var sleeping = ready with { Own = ready.Own! with { Action = "Sleep" } };
        var embarked = ready with { IsEmbarked = true };

        Assert.Equal("!", AccessibilityModel.UnitStateCue(ready));
        Assert.Equal("·", AccessibilityModel.UnitStateCue(exhausted));
        Assert.Equal("F", AccessibilityModel.UnitStateCue(fortified));
        Assert.Equal("Z", AccessibilityModel.UnitStateCue(sleeping));
        Assert.Equal("E", AccessibilityModel.UnitStateCue(embarked));
    }

    [Fact]
    public void StepCursor_And_HelpLines_And_SettingsSanitize()
    {
        Assert.Equal(new HexCoord(3, 5), AccessibilityModel.StepCursor(new HexCoord(2, 4), 1, 1));
        Assert.False(string.IsNullOrWhiteSpace(AccessibilityModel.HelpTitle));
        Assert.NotEmpty(AccessibilityModel.HelpLines);

        var dirty = new ClientSettings { TextScale = float.NaN, ColorblindMode = "bogus" }.Sanitized();
        Assert.Equal(1f, dirty.TextScale);
        Assert.Equal(nameof(ColorblindMode.Off), dirty.ColorblindMode);

        var valid = new ClientSettings { TextScale = 1.45f, ColorblindMode = "tritanopia" }.Sanitized();
        Assert.Equal(1.45f, valid.TextScale);
        Assert.Equal(nameof(ColorblindMode.Tritanopia), valid.ColorblindMode);
    }
}
