using Unciv.AI.GrandStrategy;

namespace Unciv.Core.Tests.AI;

public class LeaderPersonalityFlavorsTests
{
    [Fact]
    public void AllFlavorsAndGetEffectiveFlavorsWork()
    {
        var flavors = new LeaderPersonalityFlavors();
        flavors.SetFlavor("FLAVOR_SCIENCE", 7);

        Assert.Single(flavors.AllFlavors);
        Assert.Equal(7, flavors.GetEffectiveFlavors(Unciv.AI.GrandStrategy.GrandStrategy.None)["FLAVOR_SCIENCE"]);

        var effective = flavors.GetEffectiveFlavors(Unciv.AI.GrandStrategy.GrandStrategy.Spaceship);
        Assert.True(effective.ContainsKey("FLAVOR_SCIENCE"));
        Assert.True(effective.Count > 1);
    }
}
