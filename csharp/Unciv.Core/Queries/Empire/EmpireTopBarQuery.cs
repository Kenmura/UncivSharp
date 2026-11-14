using Unciv.Models.Ruleset.Tile;
using Unciv.Queries.Cities;
using Unciv.View;

namespace Unciv.Queries.Empire;

// Name, Amount: a luxury or strategic resource the civ currently has (bonus resources are not listed), summed over all origins; never zero
public sealed record TopBarResource(string Name, string Type, int Amount);

// GoldenAgeTurnsLeft: 0 when no golden age is running. GoldenAgeStored, GoldenAgeNeeded: happiness stored towards the next golden age and what it costs.
// StatSources: the per-turn yields itemised by source (the same shape as the city screen's stat breakdown), so the top bar can explain each yield on hover.
public sealed record EmpireTopBarResult(int Turn, int Year, int Gold, float GoldPerTurn, float SciencePerTurn, float CulturePerTurn, float FaithPerTurn,
    int Happiness, int GoldenAgeTurnsLeft, int GoldenAgeStored, int GoldenAgeNeeded, IReadOnlyList<TopBarResource> Resources,
    IReadOnlyList<StatSource> StatSources,
    string? CurrentTech = null, int CurrentTechProgress = 0, int CurrentTechCost = 0, int? CurrentTechTurns = null);

// C011: the numbers of the world screen's top bar (the Kotlin TopBar / StatsTable), as the viewer's own civ shows them. Empire.Overview has the
// per-turn stats too, but the bar also needs the year, the golden age and the resource list, and asks for them every time the snapshot changes.
[QueryType("empire.TopBar")]
public sealed record EmpireTopBarQuery : IQuery<EmpireTopBarResult>
{
    public string Type => "empire.TopBar";

    public EmpireTopBarResult Run(GameView view)
    {
        var civView = view.CivView;
        var civ = civView.Unwrap();
        var stats = civ.Stats.StatsForNextTurn;
        var golden = civ.GoldenAges;
        var resources = civ.GetCivResourceSupply()
            .Where(it => it.Resource.ResourceType != ResourceType.Bonus)
            .GroupBy(it => it.Resource.Name, StringComparer.Ordinal)
            .Select(group => (Resource: group.First().Resource, Amount: group.Sum(it => it.Amount)))
            .Where(it => it.Amount != 0)
            .OrderBy(it => (int)it.Resource.ResourceType)
            .ThenBy(it => it.Resource.Name, StringComparer.Ordinal)
            .Select(it => new TopBarResource(it.Resource.Name, it.Resource.ResourceType.ToString(), it.Amount))
            .ToList();
        // T344: where each per-turn yield comes from, as the engine itemised it (the StatMap is insertion-ordered, so the wire order is stable).
        var statSources = civ.Stats.GetStatMapForNextTurn()
            .Select(it => new StatSource(it.Key, CityQueryHelpers.StatList(it.Value)))
            .ToList();
        string? currentTech = civ.Tech.CurrentTechnologyName();
        int techProgress = currentTech == null ? 0 : civ.Tech.ResearchOfTech(currentTech);
        int techCost = currentTech == null ? 0 : civ.Tech.CostOfTech(currentTech);
        int? techTurns = currentTech == null ? null : TechTreeQuery.Turns(civ, currentTech);
        return new EmpireTopBarResult(view.GetTurn(), civ.GameInfo.GetYear(0), civView.Gold, stats.Gold, stats.Science, stats.Culture, stats.Faith,
            civView.GetHappiness(), golden.IsGoldenAge() ? golden.TurnsLeftForCurrentGoldenAge : 0, golden.StoredHappiness,
            golden.HappinessRequiredForNextGoldenAge(), resources, statSources,
            currentTech, techProgress, techCost, techTurns);
    }
}
