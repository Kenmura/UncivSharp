using System.Text.Json;
using Unciv.Json;
using Unciv.Logic.Civilization;
using Unciv.Logic.Civilization.Diplomacy;
using Unciv.Presentation.Victory;

namespace Unciv.Core.Tests.Logic.Diplomacy;

public class DiplomaticModifiersAndHistoryTests
{
    [Fact]
    public void DiplomaticModifiersTextCoversAllValues()
    {
        foreach (var mod in Enum.GetValues<DiplomaticModifiers>())
        {
            Assert.False(string.IsNullOrEmpty(mod.Text()));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DiplomaticModifiers)999).Text());
    }

    [Fact]
    public void CivRankingHistorySupportsEqualsHashCodeRemoveAndRejectsInvalidJson()
    {
        var a = new CivRankingHistory();
        a[1] = new Dictionary<RankingType, int> { [RankingType.Score] = 10 };
        var b = a.Clone();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.True(b.Remove(1));
        Assert.NotEqual(a, b);
        Assert.False(b.Remove(1));

        Assert.Throws<JsonException>(() => UncivJson.Deserialize<CivRankingHistory>("[1, 2]"));
    }
}
