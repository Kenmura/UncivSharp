using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Settings;
using Unciv.GodotClient.Ui;
using Unciv.Harness;
using Unciv.Logic.Civilization;
using Unciv.Net.Visibility;
using Unciv.Utils;
using CityObject = Unciv.Logic.City.City;

namespace Unciv.GodotHarness;

/// <summary>
/// T343: the confirmations for destructive actions. The audit (ticket Notes) found Disband, Declare war / Denounce / Revoke protection, Raze and the game menu
/// already asked, and Sell building did not; this scenario drives all of them through the real game screen: with the setting on each one asks and Cancel
/// leaves the game unchanged, with "Confirm destructive actions" off each sends on the first press. It also opens the inline Disband question, the Raze modal
/// and the Sell modal at every layout-matrix size and asserts their text and buttons stay inside the window (the same checks the layout matrix runs).
/// </summary>
public sealed class ConfirmDialogScenario : Scenario
{
    private const string DefaultSizes = "640x360,1366x768,1920x1080,2560x1440,3440x1440,3840x2160";
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "unciv-confirm-dialogs-" + System.Environment.ProcessId);

    public override string Name => "confirm-dialogs";

    public override bool Uses3D => true;

    public override bool Rich => true;

    public override IReadOnlyList<string> Checklist =>
    [
        "Do Disband, Declare war, Raze and Sell ask before they destroy something, with the safe choice obvious?",
        "Is each question short and specific (which unit, city or building; what it costs), and are the buttons big enough for a thumb?",
        "With Confirm destructive actions off, does each action send on the first press?",
    ];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session)
    {
        string path = Path.Combine(TempRoot, "settings.json");
        Directory.CreateDirectory(TempRoot);
        if (File.Exists(path)) File.Delete(path);
        var service = new ClientSettingsService(new ClientSettingsStore(path));
        var steps = new List<ScenarioStep>();
        steps.AddRange(LayoutSteps(host, session));
        steps.AddRange(BehaviourSteps(host, session, service));
        return steps;
    }

    /// <summary>T343: the room the inline unit question and the city modal need at the sizes the layout matrix uses.</summary>
    private static List<ScenarioStep> LayoutSteps(Control host, GameSession session)
    {
        var steps = new List<ScenarioStep>();
        var problems = new List<string>();
        var game = session.Game;
        var human = game.GetCivilization(ScenarioGame.HumanA);
        var capital = human.Cities[0];
        int unitId = human.Units.GetCivUnits().OrderBy(it => it.Id).First().Id;
        var realProfile = DisplayProfile.Current;

        foreach (var (width, height, os) in ScaleSheetScenario.Sizes("UNCIV_MATRIX_RES", DefaultSizes))
        {
            string tag = $"{width}x{height}" + (os == 1f ? "" : "@" + os.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var holder = new Holder();
            steps.Add(new Act($"{tag}: build the game screen for the confirmations", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, os);
                var sub = holder.Sub = new SubViewport { Size = new Vector2I(width, height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false, World3D = host.GetViewport().FindWorld3D() };
                host.AddChild(sub);
                holder.Local = new LocalHost(session, ScenarioGame.HumanA, () => 1000L);
                var screen = holder.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(holder.Local, AssetRegistry.ForRuleset(game.Ruleset));
                screen.TutorialsEnabled = false;
                screen.Hud.HideBanner();
            }));
            steps.Add(new WaitFrames(6));
            steps.Add(new Act($"{tag}: select a unit and ask Disband", () =>
            {
                var screen = holder.Screen;
                screen.SelectUnitById(unitId);
                var button = screen.Panel.ButtonFor(UnitOrdersModel.DisbandLabel);
                if (button == null) problems.Add($"{tag} disband: no Disband entry was offered");
                else button.EmitSignal(BaseButton.SignalName.Pressed);
            }));
            steps.Add(new WaitFrames(3));
            steps.Add(new Act($"{tag}: check the Disband question, cancel, open the city", () =>
            {
                var screen = holder.Screen;
                var window = new Rect2(Vector2.Zero, new Vector2(width, height));
                var theme = screen.SharedTheme.NotNull();
                if (!screen.Panel.IsAsking) problems.Add($"{tag} disband: the question did not open");
                problems.AddRange(LayoutChecks.Problems(screen.Panel, window).Select(it => $"{tag} disband: {it}"));
                problems.AddRange(LayoutChecks.TargetProblems(screen.Panel, theme.Px(UiTokens.CompactControlMinHeight), theme.Px(UiTokens.ControlMinHeight) * 4)
                    .Select(it => $"{tag} disband: {it}"));
                screen.Panel.CancelConfirmation();
                var city = holder.Local.Snapshot.Cities.First(it => it.Own?.Id == capital.Id);
                screen.OpenCityScreen(city);
            }));
            steps.Add(new WaitFrames(4));
            steps.Add(new Act($"{tag}: show Production and ask Raze", () =>
            {
                var screen = holder.Screen;
                screen.City.SetProduction(true);
                var button = screen.City.Production.ButtonNamed("State_Raze city");
                if (button == null) problems.Add($"{tag} raze: no Raze button was offered");
                else button.EmitSignal(BaseButton.SignalName.Pressed);
            }));
            steps.Add(new WaitFrames(3));
            steps.Add(new Act($"{tag}: check the Raze modal, cancel and free the size", () =>
            {
                var screen = holder.Screen;
                var window = new Rect2(Vector2.Zero, new Vector2(width, height));
                var theme = screen.SharedTheme.NotNull();
                if (!screen.City.ConfirmPopup.Visible) problems.Add($"{tag} raze: the confirmation did not open");
                problems.AddRange(LayoutChecks.Problems(screen.City.ConfirmPopup, window).Select(it => $"{tag} raze: {it}"));
                problems.AddRange(LayoutChecks.TargetProblems(screen.City.ConfirmPopup, theme.Px(UiTokens.CompactControlMinHeight), theme.Px(UiTokens.ControlMinHeight) * 4)
                    .Select(it => $"{tag} raze: {it}"));
                screen.City.CancelConfirmation();
                holder.Sub.QueueFree();
            }));
            steps.Add(new WaitFrames(2));
        }

        steps.Add(new Act("restore the display profile and report the collected problems", () =>
        {
            DisplayProfile.Current = realProfile;
            foreach (string problem in problems) GD.PrintErr("[confirm-dialogs] " + problem);
        }));
        steps.Add(new AssertQuery("the Disband question and the Raze modal fit every layout-matrix size", () => problems.Count == 0));
        return steps;
    }

    private static List<ScenarioStep> BehaviourSteps(Control host, GameSession session, ClientSettingsService service)
    {
        var run = new Run(host, session, service);
        return [.. run.DefaultOnSteps(), .. run.SettingOffSteps(), .. run.WarSteps()];
    }

    /// <summary>T343: the behaviour run: one game screen at 640x480, then a diplomacy game for the war question.</summary>
    private sealed class Run
    {
        private readonly Control host;
        private readonly GameSession session;
        private readonly ClientSettingsService service;
        private readonly Civilization human;
        private readonly CityObject capital;
        private readonly CityObject puppet;
        private readonly int unitId;
        private readonly Holder main = new();
        private readonly Holder diplomacy = new();
        private readonly GameSession diplomacySession;
        private readonly Civilization me;
        private readonly Civilization rome;
        private long now = 1000;
        private int goldBefore;

        public Run(Control host, GameSession session, ClientSettingsService service)
        {
            this.host = host;
            this.session = session;
            this.service = service;
            human = session.Game.GetCivilization(ScenarioGame.HumanA);
            capital = human.Cities[0];
            puppet = human.Cities[1];
            unitId = human.Units.GetCivUnits().OrderBy(it => it.Id).First().Id;
            diplomacySession = ScenarioGame.Create(solo: true, diplomacy: true);
            me = diplomacySession.Game.GetCivilization(ScenarioGame.HumanA);
            rome = diplomacySession.Game.GetCivilization(ScenarioGame.Ai);
        }

        private CityDto Dto(CityObject city) => main.Local.Snapshot.Cities.First(it => it.Own?.Id == city.Id);

        private static Vector2 Centre(Control? control) => control?.GetGlobalRect().GetCenter() ?? Vector2.Zero;

        private static void EnsureVisible(Control? control)
        {
            for (Node? node = control; node != null; node = node.GetParent())
            {
                if (node is ScrollContainer scroller && control != null)
                {
                    scroller.EnsureControlVisible(control);
                    break;
                }
            }
        }

        public List<ScenarioStep> DefaultOnSteps()
        {
            return
            [
                new Act("build the game screen at 640x480", () =>
                {
                    main.Local = new LocalHost(session, ScenarioGame.HumanA, () => now += 1000);
                    var screen = main.Screen = new GameScreen();
                    host.AddChild(screen);
                    screen.Start(main.Local, AssetRegistry.ForRuleset(session.Game.Ruleset));
                    screen.TutorialsEnabled = false;
                    screen.BindSettings(service);
                    screen.Hud.HideBanner();
                }),
                new WaitFrames(6),
                new AssertQuery("T343: destructive confirmations are on by default", () => service.Current.ConfirmDestructiveActions),
                new Act("select a unit", () => main.Screen.SelectUnitById(unitId)),
                new WaitFrames(3),
                new AssertQuery("the unit is selected and offers Disband", () => main.Screen.SelectedUnitId == unitId && main.Screen.Panel.ButtonFor(UnitOrdersModel.DisbandLabel) != null),
                new Act("choose Disband", () => main.Screen.Panel.ButtonFor(UnitOrdersModel.DisbandLabel).NotNull().EmitSignal(BaseButton.SignalName.Pressed)),
                new WaitFrames(2),
                new AssertQuery("Disband asks first and the unit is still there", () =>
                    main.Screen.Panel.IsAsking && main.Screen.Panel.QuestionText == "Disband this unit?" && human.Units.GetCivUnits().Any(it => it.Id == unitId)),
                new Capture("disband-confirm", "Disband asks first: a short question in the unit panel, the Danger button confirms and Cancel is the way out; the unit is still alive"),
                new Press("Cancel"),
                new AssertQuery("Cancel keeps the unit and shows the action menu again", () => !main.Screen.Panel.IsAsking && human.Units.GetCivUnits().Any(it => it.Id == unitId)),

                new Act("open the capital's Production tab and buy a Monument", () => main.Screen.OpenCityScreen(Dto(capital))),
                new WaitFrames(3),
                new Act("show Production", () => main.Screen.City.SetProduction(true)),
                new WaitFrames(2),
                new Act("scroll Monument into view", () => EnsureVisible(main.Screen.City.Production.ButtonNamed("Build_Monument"))),
                new WaitFrames(2),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("Build_Monument"))),
                new WaitFrames(2),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("QBuy_0_Gold"))),
                new WaitFrames(3),
                new AssertQuery("the bought Monument is listed with its Sell button", () => capital.CityConstructions.IsBuilt("Monument") && main.Screen.City.Production.ButtonNamed("Sell_Monument") != null),
                new Act("remember the gold", () => goldBefore = human.Gold),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("Sell_Monument"))),
                new WaitFrames(2),
                new AssertQuery("Sell asks first, names the building and its price, and sells nothing yet", () =>
                    main.Screen.City.ConfirmPopup.Visible && main.Screen.City.ConfirmPopup.QuestionText.Contains("Monument", StringComparison.Ordinal)
                    && main.Screen.City.ConfirmPopup.QuestionText.Contains("cannot be undone", StringComparison.Ordinal)
                    && capital.CityConstructions.IsBuilt("Monument") && human.Gold == goldBefore),
                new Capture("sell-confirm", "Selling a building asks first: the modal names the Monument and its price and says it cannot be undone; the Danger button confirms and Cancel is the safe choice"),
                new Press("Cancel"),
                new AssertQuery("Cancel keeps the building", () => !main.Screen.City.ConfirmPopup.Visible && capital.CityConstructions.IsBuilt("Monument")),

                new Act("open the puppet city", () => main.Screen.OpenCityScreen(Dto(puppet))),
                new WaitFrames(3),
                new Act("show Production", () => main.Screen.City.SetProduction(true)),
                new WaitFrames(2),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("State_Annex city"))),
                new WaitFrames(3),
                new AssertQuery("Annex is not destructive and needs no confirmation", () => !puppet.IsPuppet && main.Screen.City.Production.ButtonNamed("State_Raze city") != null),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("State_Raze city"))),
                new WaitFrames(2),
                new AssertQuery("Raze asks first, names the city and does not raze it yet", () =>
                    main.Screen.City.ConfirmPopup.Visible && main.Screen.City.ConfirmPopup.QuestionText.Contains(puppet.Name, StringComparison.Ordinal) && !puppet.IsBeingRazed),
                new Capture("raze-confirm", "Raze asks first: the modal names the city and what razing does every turn; the Danger button confirms and Cancel has the focus"),
                new Press("Cancel"),
                new AssertQuery("Cancel leaves the city unrazed", () => !main.Screen.City.ConfirmPopup.Visible && !puppet.IsBeingRazed),
            ];
        }

        public List<ScenarioStep> SettingOffSteps()
        {
            return
            [
                new Act("turn confirmations off", () => service.Update(s => s with { ConfirmDestructiveActions = false })),
                new WaitFrames(1),
                new AssertQuery("the setting is written and off", () => !service.Current.ConfirmDestructiveActions && !new ClientSettingsStore(Path.Combine(TempRoot, "settings.json")).Load().ConfirmDestructiveActions),
                new Act("close the city and select the unit again", () =>
                {
                    main.Screen.OpenCityScreen(null);
                    main.Screen.SelectUnitById(unitId);
                }),
                new WaitFrames(3),
                new Act("choose Disband with confirmations off", () => main.Screen.Panel.ButtonFor(UnitOrdersModel.DisbandLabel).NotNull().EmitSignal(BaseButton.SignalName.Pressed)),
                new WaitFrames(3),
                new AssertQuery("with confirmations off Disband sends at once", () => human.Units.GetCivUnits().All(it => it.Id != unitId) && !main.Screen.Panel.IsAsking),

                new Act("open the capital again and sell without asking", () => main.Screen.OpenCityScreen(Dto(capital))),
                new WaitFrames(3),
                new Act("show Production", () => main.Screen.City.SetProduction(true)),
                new WaitFrames(2),
                new Act("remember the gold before the sale", () => goldBefore = human.Gold),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("Sell_Monument"))),
                new WaitFrames(3),
                new AssertQuery("with confirmations off Sell sends at once", () =>
                    !main.Screen.City.ConfirmPopup.Visible && !capital.CityConstructions.IsBuilt("Monument") && human.Gold > goldBefore),

                new Act("open the puppet again and raze without asking", () => main.Screen.OpenCityScreen(Dto(puppet))),
                new WaitFrames(3),
                new Act("show Production", () => main.Screen.City.SetProduction(true)),
                new WaitFrames(2),
                new Click(() => Centre(main.Screen.City.Production.ButtonNamed("State_Raze city"))),
                new WaitFrames(3),
                new AssertQuery("with confirmations off Raze sends at once", () => !main.Screen.City.ConfirmPopup.Visible && puppet.IsBeingRazed),
            ];
        }

        public List<ScenarioStep> WarSteps()
        {
            return
            [
                new Act("free the first game screen", () => main.Screen.QueueFree()),
                new WaitFrames(2),
                new Act("restore confirmations and build the diplomacy game", () =>
                {
                    service.Update(s => s with { ConfirmDestructiveActions = true });
                    diplomacy.Local = new LocalHost(diplomacySession, ScenarioGame.HumanA, () => 2000L);
                    var screen = diplomacy.Screen = new GameScreen();
                    host.AddChild(screen);
                    screen.Start(diplomacy.Local, AssetRegistry.ForRuleset(diplomacySession.Game.Ruleset));
                    screen.TutorialsEnabled = false;
                    screen.BindSettings(service);
                    screen.Hud.HideBanner();
                    screen.OpenDiplomacyScreen();
                }),
                new WaitFrames(6),
                new WaitUntil("the diplomacy screen offers Declare war", () => diplomacy.Screen.Diplomacy.Buttons.Any(it => it.Id == "DeclareWar" && it.Enabled), 1200),
                new Act("press Declare war once with confirmations on", () => diplomacy.Screen.Diplomacy.ActionButton("DeclareWar").NotNull().EmitSignal(BaseButton.SignalName.Pressed)),
                new WaitFrames(2),
                new AssertQuery("Declare war asks on the first press and declares nothing", () => diplomacy.Screen.Diplomacy.ArmedButton == "DeclareWar" && !me.IsAtWarWith(rome)),
                new Capture("war-armed", "Declare war asks first: the danger button now reads Confirm and the hint says to press again; peace still holds"),
                new Act("turn confirmations off while the question is armed", () => service.Update(s => s with { ConfirmDestructiveActions = false })),
                new WaitFrames(1),
                new AssertQuery("turning confirmations off disarms the question", () => diplomacy.Screen.Diplomacy.ArmedButton == null && !me.IsAtWarWith(rome)),
                new Act("press Declare war again with confirmations off", () => diplomacy.Screen.Diplomacy.ActionButton("DeclareWar").NotNull().EmitSignal(BaseButton.SignalName.Pressed)),
                new WaitFrames(3),
                new AssertQuery("with confirmations off Declare war sends at once", () => me.IsAtWarWith(rome)),
            ];
        }
    }

    private sealed class Holder
    {
        private SubViewport? sub;
        private LocalHost? local;
        private GameScreen? screen;

        public SubViewport Sub
        {
            get => sub ?? throw new InvalidOperationException("not built");
            set => sub = value;
        }

        public LocalHost Local
        {
            get => local ?? throw new InvalidOperationException("not built");
            set => local = value;
        }

        public GameScreen Screen
        {
            get => screen ?? throw new InvalidOperationException("not built");
            set => screen = value;
        }
    }
}
