using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Ui;
using Unciv.Harness;
using Unciv.Models;
using Unciv.Queries.Empire;
using Unciv.Utils;

namespace Unciv.GodotHarness;

/// <summary>
/// C050: the overview's espionage and global politics tabs through the real game screen and the real engine, in the espionage game (espionage on; Babylon has a hidden spy and a
/// set-up spy in the city-state; Rome and the city-state are met, Greece is not). The Move buttons are exactly the destinations the engine offers and send MoveSpy; the coup
/// button shows the engine's odds, stages the coup and calls it off (SetSpyCoup). The politics tab has one row per met civ with the engine's diplomacy panel and never lists
/// the unmet civ; the diplomatic vote line says no vote is open.
/// </summary>
public sealed class OverviewEspionageScenario : Scenario
{
    public override string Name => "overview-espionage";

    public override bool Uses3D => true;

    public override bool Espionage => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Can you tell which spy is where and what it is doing, and which buttons move it?",
        "Does the coup button say what it does and its chance, and does it change when the coup is staged?",
        "Does the politics tab make each met civ's relation to you clear, and is the vote line understandable?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session)
    {
        var game = session.Game;
        var human = game.GetCivilization(ScenarioGame.HumanA);
        var rome = game.GetCivilization(ScenarioGame.Ai);
        var recording = new CityAScenario.RecordingSession(new LocalHost(session, ScenarioGame.HumanA, () => 1000L));
        var screen = new GameScreen();
        host.AddChild(screen);
        screen.Start(recording, AssetRegistry.ForRuleset(game.Ruleset));
        var hidden = human.EspionageManager.SpyList[0];
        var resident = human.EspionageManager.SpyList[1];
        var romeCity = rome.Cities[0];
        int logged = 0;

        Vector2 Centre(Button? button) => button?.GetGlobalRect().GetCenter() ?? Vector2.Zero;
        T Ask<T>(Unciv.Queries.IQuery<T> query) where T : class => recording.QueryAsync(query).GetAwaiter().GetResult().NotNull();
        SpyEntry Entry(Spy spy) => Ask(new SpyDestinationsQuery()).Spies.Single(it => it.Name == spy.Name);

        return
        [
            new WaitFrames(3),
            new Act("dismiss the start banner", () => screen.Hud.HideBanner()),
            new Act("open the overview on the espionage tab", () => screen.OpenOverviewScreen(OverviewModel.Espionage)),
            new WaitFrames(3),
            new AssertQuery("the inline board starts collapsed without destination chips until Move is clicked", () =>
            {
                var spies = Ask(new SpyDestinationsQuery()).Spies;
                var board = screen.Overview.Spies.NotNull();
                return screen.Overview.SelectedTab == OverviewModel.Espionage && screen.Overview.Notice(OverviewModel.Espionage) == null && spies.Count == 2
                    && board.SelectedSpy == null && board.HereButton(romeCity.Id) == null && board.MoveButton(hidden.Name)?.Disabled == false
                    && !Entry(hidden).CanMoveToHideout && Entry(resident).CanMoveToHideout;
            }),
            new AssertQuery("only the spy in the city-state can stage a coup, and its button shows the engine's odds", () =>
            {
                var entry = Entry(resident);
                string percent = KotlinMath.RoundToInt(entry.CoupChance.NotNull() * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var button = screen.Overview.SpyButton(resident.Name, -1);
                return screen.Overview.SpyButton(hidden.Name, -1) == null && button?.Text == $"Coup {percent}%" && button.TooltipText == $"Stage a coup ({percent}% chance)"
                    && button.ThemeTypeVariation == UiVariants.ButtonDanger;
            }),
            new AssertQuery("the spies are a typed table (rank right-aligned) that fits the screen, one row per spy, the coup button inside its row", () =>
            {
                var data = screen.Overview.DataTables(OverviewModel.Espionage)[0];
                var coup = screen.Overview.SpyButton(resident.Name, -1).NotNull().GetGlobalRect();
                return data.RowCount == 2 && data.Cell(0, 1).HorizontalAlignment == HorizontalAlignment.Right && data.GetGlobalRect().End.X <= screen.GetViewportRect().Size.X
                    && coup.Position.X >= data.GetGlobalRect().Position.X && coup.End.X <= data.GetGlobalRect().End.X && coup.Size.X > 0;
            }),
            new Capture("espionage", "Espionage tab: the unified table of the two spies with rank, location, action, Move button and red coup button"),

            new Act("remember the command log", () => logged = session.Log.Count),
            new Click(() => Centre(screen.Overview.Spies.NotNull().MoveButton(hidden.Name))),
            new WaitFrames(2),
            new AssertQuery("clicking Move expands the exact destinations offered by the engine without sending a command", () =>
            {
                var destinations = Entry(hidden).Destinations.Select(it => it.CityId).Order(StringComparer.Ordinal).ToList();
                var board = screen.Overview.Spies.NotNull();
                var spec = board.Spec.NotNull();
                var shown = spec.Locations.Where(it => it.CityId != null && board.HereButton(it.CityId) != null).Select(it => it.CityId.NotNull()).Order(StringComparer.Ordinal).ToList();
                return session.Log.Count == logged && board.SelectedSpy == hidden.Name && shown.SequenceEqual(destinations) && board.HereButton("") == null;
            }),
            new Click(() => Centre(screen.Overview.Spies.NotNull().HereButton(romeCity.Id))),
            new WaitFrames(3),
            new AssertQuery("'Move to' Rome's city sends one MoveSpy and the engine's spy is on its way there", () =>
                session.Log.Count == logged + 1 && session.Log[^1].Type == "move-spy" && session.Log[^1].Payload.GetProperty("CityId").GetString() == romeCity.Id
                && hidden.GetCityOrNull() == romeCity && hidden.Action == SpyAction.Moving && Entry(hidden).Location == romeCity.Name
                && screen.Overview.MessageText == ""),
            new Capture("moved", "The first spy is on its way to Rome's capital; the page shows its new location"),

            new Click(() => Centre(screen.Overview.SpyButton(resident.Name, -1))),
            new WaitFrames(3),
            new AssertQuery("the coup button stages the coup (SetSpyCoup) and then offers to call it off", () =>
                session.Log.Count == logged + 2 && session.Log[^1].Type == "set-spy-coup" && session.Log[^1].Payload.GetProperty("Stage").GetBoolean() && resident.Action == SpyAction.Coup
                && screen.Overview.SpyButton(resident.Name, -1)?.Text == "Call off" && screen.Overview.SpyButton(resident.Name, -1)?.TooltipText == "Call off the coup"),
            new Capture("coup", "The resident spy is staging a coup; the button now calls it off"),
            new Click(() => Centre(screen.Overview.SpyButton(resident.Name, -1))),
            new WaitFrames(3),
            new AssertQuery("calling it off sends SetSpyCoup with Stage false and the stage button returns", () =>
                session.Log.Count == logged + 3 && session.Log[^1].Type == "set-spy-coup" && !session.Log[^1].Payload.GetProperty("Stage").GetBoolean() && resident.Action == SpyAction.CounterIntelligence
                && screen.Overview.SpyButton(resident.Name, -1)?.TooltipText.StartsWith("Stage a coup", StringComparison.Ordinal) == true),

            new Click(() => Centre(screen.Overview.TabButton(OverviewModel.Politics))),
            new WaitFrames(3),
            new AssertQuery("the politics tab has one row per met civ (never the unmet one) with the engine's status and attitude; no vote is open", () =>
            {
                var overview = Ask(new EmpireOverviewQuery());
                var table = screen.Overview.Tables(OverviewModel.Politics)[0];
                bool rowsMatch = table.Rows.All(row =>
                {
                    var panel = Ask(new DiplomacyPanelQuery(row.Key));
                    return row.Cells[2] == panel.Status && row.Cells[3] == panel.Relationship && row.Cells[1] == (panel.IsCityState ? "City-state" : "Major");
                });
                return screen.Overview.SelectedTab == OverviewModel.Politics && table.Rows.Select(it => it.Key).SequenceEqual(overview.KnownCivs.Select(it => it.CivName))
                    && table.Count == 2 && table.Rows.All(it => it.Key != ScenarioGame.HumanB) && rowsMatch && !Ask(new DiplomaticVoteQuery()).CanVote;
            }),
            new Capture("politics", "Politics tab: a table with the relation with each civ you have met (status, attitude, influence, borders, peace treaty), and the diplomatic vote line"),
            new Press("Close overview"),
            new AssertQuery("the close button closes the screen; only the three spy commands were sent", () => !screen.IsOverviewVisible && session.Log.Count == logged + 3),
        ];
    }
}
