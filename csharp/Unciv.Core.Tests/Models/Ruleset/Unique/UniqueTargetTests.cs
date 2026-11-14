using Unciv.Models.Ruleset.Unique;

namespace Unciv.Core.Tests.Models.Ruleset.Unique;

public class UniqueTargetTests
{
    [Fact]
    public void DocumentationStringCoversAllTargets()
    {
        foreach (var target in Enum.GetValues<UniqueTarget>())
        {
            Assert.NotNull(target.DocumentationString());
        }
    }
}
