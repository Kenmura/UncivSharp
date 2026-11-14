using Unciv.Logic.Map;
using Unciv.Models.Metadata;
using Unciv.UI.Screens.MapEditorScreen;
using Unciv.UI.Screens.OverviewScreen;

namespace Unciv.Core.Tests.UI;

public class UiEnumTests
{
    [Fact]
    public void EmpireOverviewCategoriesExtensionsCoverAllValues()
    {
        foreach (var category in Enum.GetValues<EmpireOverviewCategories>())
        {
            Assert.False(string.IsNullOrEmpty(category.IconName()));
            Assert.NotEqual('\0', category.ShortcutChar());
            _ = category.ScrollAlign();
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EmpireOverviewCategories)999).IconName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EmpireOverviewCategories)999).ShortcutChar());
    }

    [Fact]
    public void MapGeneratorStepsExtensionsCoverAllLabels()
    {
        foreach (var step in Enum.GetValues<MapGeneratorSteps>())
        {
            Assert.NotNull(step.Label());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ((MapGeneratorSteps)999).Label());
    }

    [Fact]
    public void ModCategoryDefaultConstructorSetsEmptyFields()
    {
        var category = new ModCategory();
        Assert.Equal("", category.Label);
        Assert.Equal("", category.Topic);
        Assert.False(category.Hidden);
    }

    [Fact]
    public void NeighborDirectionClockPositionsCoverAllValues()
    {
        foreach (var dir in Enum.GetValues<NeighborDirection>())
        {
            var pos = dir.GetClockPosition();
            Assert.Equal(dir, NeighborDirectionExtensions.ByClockPosition[pos]);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ((NeighborDirection)999).GetClockPosition());
    }
}
