using Unciv.Commands;
using Unciv.Commands.City;
using Unciv.Commands.Empire;
using Unciv.Commands.Turn;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Setup;
using Unciv.Harness;
using Unciv.Models.Ruleset;
using Unciv.Net.Visibility;
using Unciv.Queries;
using Unciv.Queries.Empire;
using Unciv.View;

namespace Unciv.Core.Tests.Assets;

[Collection("RulesetCache")]
public class LocalGameSessionFactoryTests
{
    private sealed class MinimalSession(Ruleset ruleset, ClientSnapshot snapshot) : IGameSession
    {
        public string ViewerCivId => "Rome";
        public Ruleset Ruleset => ruleset;
        public ClientSnapshot Snapshot => snapshot;
        public event Action<ClientSnapshot>? SnapshotChanged { add { } remove { } }
        public event Action<EventBatch>? EventsReceived { add { } remove { } }
        public event Action<string>? Desynchronized { add { } remove { } }
        public Task ResyncAsync() => Task.CompletedTask;
        public Task<TResult?> QueryAsync<TResult>(IQuery<TResult> query) where TResult : class => Task.FromResult<TResult?>(null);
        public Task<SubmitResult> SubmitAsync(GameCommand command) => Task.FromResult(new SubmitResult(false, "", []));
        public Task<TurnOutcome> EndTurnAsync(IProgress<TurnProgress>? progress = null, CancellationToken cancellation = default) =>
            Task.FromResult(new TurnOutcome(false, "", 0, false, []));
        public Task<TurnOutcome> AutoPlayTurnAsync(IProgress<TurnProgress>? progress = null, CancellationToken cancellation = default) =>
            Task.FromResult(new TurnOutcome(false, "", 0, false, []));
        public Task<string?> ExportSaveAsync() => Task.FromResult<string?>(null);
    }

    private sealed class FailingQuery : IQuery<EmpireOverviewResult>
    {
        public string Type => "failing";
        public EmpireOverviewResult Run(GameView view) => throw new QueryException("bad", "bad query");
    }

    [Fact]
    public void IGameSession_DefaultProperties_ReturnNull()
    {
        IGameSession minimal = new MinimalSession(null!, null!);
        Assert.Null(minimal.GameId);
        Assert.Null(minimal.OwnerMemoryKey);
    }

    [Fact]
    public void Start_ReturnsValidationErrorsForInvalidForm()
    {
        ScenarioGame.LoadVanilla();
        var form = NewGameForm.FromRulesetCache();
        form.SeedText = "not-a-number";
        var outcome = LocalGameSessionFactory.Instance.Start(form, () => 1000L);
        Assert.Null(outcome.Host);
        Assert.NotEmpty(outcome.Errors);
    }

    [Fact]
    public void FromSave_ThrowsInvalidDataExceptionForDamagedSave()
    {
        Assert.Throws<InvalidDataException>(() => LocalGameSessionFactory.Instance.FromSave("not valid json", () => 1000L));
    }

    [Fact]
    public async Task LocalHost_StartExportResyncQueryAndEndTurnWork()
    {
        ScenarioGame.LoadVanilla();
        var form = NewGameForm.FromRulesetCache();
        form.SeedText = "42";
        var outcome = LocalGameSessionFactory.Instance.Start(form, () => 1000L);
        Assert.Empty(outcome.Errors);
        var host = outcome.Host!;
        Assert.NotNull(host.GameId);
        Assert.NotNull(host.Ruleset);
        Assert.NotNull(host.Session);
        Assert.False(host.IsProcessingTurn);
        Assert.False(host.IsRetired);
        Assert.True(host.CanExportSave);

        Action<string> onDesync = _ => { };
        host.Desynchronized += onDesync;
        host.Desynchronized -= onDesync;

        var snapshots = 0;
        host.SnapshotChanged += _ => snapshots++;
        await host.ResyncAsync();
        Assert.Equal(1, snapshots);

        var overview = await host.QueryAsync(new EmpireOverviewQuery());
        Assert.NotNull(overview);
        Assert.Null(await host.QueryAsync(new FailingQuery()));

        var save = await host.ExportSaveAsync();
        Assert.NotNull(save);
        var reloaded = LocalGameSessionFactory.Instance.FromSave(save!, () => 1000L);
        Assert.Equal(host.ViewerCivId, reloaded.ViewerCivId);
    }

    [Fact]
    public async Task LocalHost_HotseatTakeOverRetiresPreviousViewerAndRejectsSubsequentCalls()
    {
        var session = ScenarioGame.Create();
        var host = new LocalHost(session, ScenarioGame.HumanA, () => 1000L);
        Assert.False(host.TurnNeedsBegin);

        var city = session.Game.GetCivilization(ScenarioGame.HumanA).Cities[0];
        Assert.True((await host.SubmitAsync(new QueueConstruction { CityId = city.Id, Name = "Warrior" })).Accepted);
        Assert.True((await host.SubmitAsync(new SetResearchQueue { Techs = ["Pottery"] })).Accepted);

        var end = await host.EndTurnAsync();
        Assert.True(end.Accepted, end.Message);
        Assert.Equal(ScenarioGame.HumanB, end.HandOverTo);
        Assert.False(host.CanExportSave);
        Assert.Null(await host.ExportSaveAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.TakeOverAsync("Nobody"));
        var next = await host.TakeOverAsync(ScenarioGame.HumanB);
        Assert.Equal(ScenarioGame.HumanB, next.ViewerCivId);
        Assert.True(host.IsRetired);

        await host.ResyncAsync();
        Assert.Null(await host.QueryAsync(new EmpireOverviewQuery()));
        Assert.False((await host.SubmitAsync(new EndTurn())).Accepted);
        Assert.False((await host.EndTurnAsync()).Accepted);
        Assert.False((await host.AutoPlayTurnAsync()).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.TakeOverAsync(ScenarioGame.HumanB));
    }
}
