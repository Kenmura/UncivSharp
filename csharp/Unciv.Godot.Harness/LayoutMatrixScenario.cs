using System.Globalization;
using Godot;
using Unciv.Assets;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Saves;
using Unciv.GodotClient.Settings;
using Unciv.GodotClient.Setup;
using Unciv.GodotClient.Ui;
using Unciv.Harness;
using Unciv.Client.Net.Hosting;
using Unciv.Client.Net.Session;
using Unciv.Logic;
using Unciv.Logic.Files;
using Unciv.Logic.Map;
using Unciv.Models;
using Unciv.Net;
using Unciv.Net.Identity;
using Unciv.Net.Lobby;
using Unciv.Server;
using Unciv.Utils;

namespace Unciv.GodotHarness;

/// <summary>
/// T312: the multi-resolution layout matrix. Every main screen (HUD, game menu, city incl. its Production tab, tech, policies, diplomacy, trade, overview, civilopedia, espionage, the popups:
/// a pending alert, a tutorial and a decision picker, the end screens: victory, defeat, the World Congress result and the hotseat cover, and the menu screens: main menu, new game, host, join,
/// options, a modal) is opened in an off-screen viewport of each desktop size (640x360, the minimum window, then 1366x768, 1920x1080, 2560x1440, 3440x1440 ultrawide, 3840x2160) and checked
/// from the real controls, not from pixels: no visible text leaves the window, none is cut off or overlaps another (<see cref="LayoutChecks.Problems"/>), no visible button is under the minimum
/// target height or over the maximum (<see cref="LayoutChecks.TargetProblems"/>), and no full-screen screen wastes the window by leaving empty side bands (<see cref="LayoutChecks.WastedSpaceProblems"/>).
/// All problems of the run are printed and the run fails at the end when there is any. When <c>UNCIV_MATRIX_OUT</c> names a folder each screen is also written there as <c>layout-WxH-screen.png</c>
/// (a real GPU draws it; never compared to goldens). <c>UNCIV_MATRIX_RES</c> (comma separated WxH or WxH@S) replaces the size list. <c>tools/layout-matrix.sh</c> runs it on the host GPU.
/// </summary>
public sealed class LayoutMatrixScenario : Scenario
{
    private static readonly string Fingerprint = HostKey.Fingerprint([1, 2, 3, 4]);
    private const string DefaultSizes = "640x360,1366x768,1920x1080,2560x1440,3440x1440,3840x2160";

    public override string Name => "layout-matrix";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override bool Trade => true;

    public override bool Espionage => true;

    public override IReadOnlyList<string> Checklist => ["Open layout-*.png: does every screen use the window well at every size (no clipping, no tiny or huge widgets, no empty ultrawide margins, HUD in its corners)?"];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session)
    {
        var run = new Run(host, session, GpuBenchScenario.Bench("UNCIV_MATRIX_OUT"));
        if (run.OutDir != null) Directory.CreateDirectory(run.OutDir);
        var realProfile = DisplayProfile.Current;
        var steps = new List<ScenarioStep>();
        foreach (var (width, height, os) in ScaleSheetScenario.Sizes("UNCIV_MATRIX_RES", DefaultSizes))
        {
            var at = new At(run, width, height, os);
            steps.AddRange(GameSteps(at));
            steps.AddRange(PopupSteps(at));
            steps.AddRange(EndScreenSteps(at));
            steps.AddRange(MenuSteps(at, (a, n, r, cap) => Check(a, n, r, contentCap: cap)));
        }

        // T329: the game screens (the HUD, the city and the tech screen) are opened once and resized (and rescaled) live, like the menus
        steps.AddRange(GameResizeSteps(new At(run, 1366, 768, 1f)));
        // T316: the same menu screens, opened once and then resized (and rescaled) live
        steps.AddRange(MenuSteps(new At(run, 1366, 768, 1f), (a, n, r, cap) => ResizeCycle(a, n, r, contentCap: cap)));
        // T321: the dialogs over a game screen (a pending alert, a tutorial, a decision picker) through the same live cycle
        steps.AddRange(PopupResizeSteps(new At(run, 1366, 768, 1f)));
        // T405: the in-game save controller shares the game UI's bound theme, so at a large window with a user scale its breakpoint and pixel sizes are the game UI's
        steps.AddRange(InGameSaveSteps(new At(run, 1600, 900, 1f)));
        // T321: a controller given a theme nothing binds still takes the Compact breakpoint from the width (the defensive fallback)
        steps.AddRange(UnboundSaveSteps(new At(run, 1366, 768, 1f)));

        steps.Add(new Act("report", () =>
        {
            DisplayProfile.Current = realProfile;
            foreach (string it in run.Problems) GD.PrintErr($"[layout-matrix] {it}");
            GD.Print($"[layout-matrix] {run.Problems.Count} problem(s)");
        }));
        steps.Add(new AssertQuery("no screen overflows, clips, overlaps or has an undersized or oversized button at any size", () => run.Problems.Count == 0));
        return steps;
    }

    /// <summary>One size: wait for the layout, run the checks, write the picture. The checks only collect: the run reports every size and screen at the end.</summary>
    /// <param name="at">The window size and the screens built in it.</param>
    /// <param name="name">The screen's name in the report and output file.</param>
    /// <param name="root">The screen under test.</param>
    /// <param name="exempt">Buttons that are not target controls (in addition to the city tiles and check boxes).</param>
    /// <param name="contentCap">T317: the logical content width cap of a full-screen screen, so the wasted-space check can run; null for dialogs and the HUD, whose content is not a full-width column.</param>
    private static ScenarioStep[] Check(At at, string name, Func<Node> root, Func<BaseButton, bool>? exempt = null, int? contentCap = null) =>
    [
        new WaitFrames(8),
        // T321: the alert is the screen under test in its own resize phase, so it is not dismissed there
        new Act($"{at.Size} {name}: the scenario's pending alert is not part of the screen", () => { if (name != "alert") at.Screen?.Alerts.Close(); }),
        new WaitFrames(2),
        new Act($"{at.Size} {name}", () =>
        {
            var t = at.Theme.NotNull();
            var view = at.Sub.NotNull();
            var node = root();
            var window = new Rect2(Vector2.Zero, view.Size);
            foreach (string it in LayoutChecks.Problems(node, window)) at.Run.Problems.Add($"{at.Size} {name}: {it.Replace("\n", "\\n")}");
            foreach (string it in LayoutChecks.TargetProblems(node, t.Px(UiTokens.CompactControlMinHeight), t.Px(UiTokens.ControlMinHeight) * 4, exempt == null ? Exempt : it => Exempt(it) || exempt(it))) at.Run.Problems.Add($"{at.Size} {name}: {it}");
            if (contentCap is { } cap)
            {
                int capPx = Math.Min(t.Px(cap), UiTokens.MaxContentPixels); // T333: the same physical ceiling ScreenFrame applies
                // T333: the saves dialog is deliberately a share of the window, not a full-width screen, so only its frame content is capped.
                if (name != "saves")
                    foreach (string it in LayoutChecks.WastedSpaceProblems(node, window, capPx)) at.Run.Problems.Add($"{at.Size} {name}: {it}");
                foreach (string it in LayoutChecks.ContentWidthProblems(node, capPx)) at.Run.Problems.Add($"{at.Size} {name}: {it}");
            }
            if (name == "new-game")
            {
                // T333: the Human/AI toggle of every player row stays beside the nation name at every size.
                foreach (string it in NewGameGapProblems(node, t)) at.Run.Problems.Add($"{at.Size} {name}: {it}");
            }
            if (at.Run.OutDir != null) Save(view, Path.Combine(at.Run.OutDir, $"layout-{at.Size}-{name}.png"));
        }),
    ];

    /// <summary>T333: the gap between a New game player row's nation button and its Human/AI toggle, in screen pixels (they share one row, so only their separation should part them).</summary>
    private static IEnumerable<string> NewGameGapProblems(Node root, UiTheme theme)
    {
        if (root is not NewGameScreen screen) return [];
        var pairs = new List<(Control Left, Control Right, string What)>();
        for (int i = 0; i < screen.Form.Slots.Count; i++)
        {
            if (screen.SlotNationButton(i) is { } nation && screen.SlotHumanButton(i) is { } human)
                pairs.Add((nation, human, $"player row {i + 1} nation name to its Human/AI toggle"));
        }
        return LayoutChecks.GapProblems(pairs, theme.Px(UiTokens.S2));
    }

    private static List<ScenarioStep> GameSteps(At at)
    {
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build the game screen", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, at.Os);
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false, World3D = at.Run.Host.GetViewport().FindWorld3D() };
                at.Run.Host.AddChild(sub);
                at.Local = new LocalHost(at.Run.Session, ScenarioGame.HumanA, () => at.Now += 1000);
                var screen = at.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(at.Local, at.Run.Registry);
                at.Theme = screen.SharedTheme;
            }),
            new WaitFrames(6),
            new Act($"{at.Size}: dismiss the banner and the pending alert", () =>
            {
                at.Screen.NotNull().Hud.HideBanner();
                at.Screen.NotNull().Alerts.Close();
            }),
        };
        GameScreen S() => at.Screen.NotNull();
        steps.AddRange(Check(at, "hud", S));
        steps.Add(new Act("open the menu", () => S().OpenGameMenu()));
        steps.AddRange(Check(at, "menu", () => S().GameMenu));
        steps.Add(new Act("close the menu", () => S().GameMenu.Close()));
        steps.Add(new Act("open the city", () => S().OpenCityScreen(at.Local.NotNull().Snapshot.Cities.First(it => it.Own != null))));
        steps.AddRange(Check(at, "city", () => S().City, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("show the city's production", () => S().City.NotNull().SetProduction(true)));
        steps.AddRange(Check(at, "city-production", () => S().City, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close the city", () => S().OpenCityScreen(null)));
        steps.Add(new Act("open tech", () => S().OpenTechScreen()));
        steps.AddRange(Check(at, "tech", () => S().Tech, it => it.GetType().Name.Contains("Card", StringComparison.Ordinal), UiTokens.MaxContentWidth));
        steps.Add(new Act("close tech", () => S().Tech.Close()));
        steps.Add(new Act("open policies", () => S().OpenPolicyScreen()));
        steps.AddRange(Check(at, "policies", () => S().Policies, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close policies", () => S().Policies.Close()));
        steps.Add(new Act("open diplomacy", () => S().OpenDiplomacyScreen()));
        steps.AddRange(Check(at, "diplomacy", () => S().Diplomacy, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close diplomacy", () => S().Diplomacy.Close()));
        steps.Add(new Act("open trade", () => S().OpenTradeScreen()));
        steps.AddRange(Check(at, "trade", () => S().Trade, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close trade", () => S().Trade.Close()));
        foreach (string tab in new[] { OverviewModel.Stats, OverviewModel.Cities, OverviewModel.Units })
        {
            steps.Add(new Act("open overview " + tab, () => S().OpenOverviewScreen(tab)));
            steps.AddRange(Check(at, "overview-" + tab.ToLowerInvariant(), () => S().Overview, contentCap: UiTokens.MaxContentWidth));
        }
        steps.Add(new Act("close overview", () => S().Overview.Close()));
        steps.Add(new Act("open espionage", () => S().OpenEspionageScreen()));
        steps.AddRange(Check(at, "espionage", () => S().Espionage, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close espionage", () => S().Espionage.Close()));
        steps.Add(new Act("open the pedia", () => S().OpenPediaScreen("Unit/Warrior")));
        steps.AddRange(Check(at, "pedia", () => S().Pedia, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close the pedia", () => S().Pedia.Close()));
        steps.Add(new Act($"{at.Size}: free the game screen", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Screen = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>
    /// T329: one game screen is built once and the HUD, the city (with its production tab) and the tech screen are each run through the live resize and scale cycle of
    /// <see cref="ResizeCycle"/>: 1366x768 -> 3840x2160 -> 1366x768 -> scale 1.5 -> scale 1, with the overflow and target checks after every change.
    /// </summary>
    private static List<ScenarioStep> GameResizeSteps(At at)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build the game screen for the live resize", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, at.Os);
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false, World3D = run.Host.GetViewport().FindWorld3D() };
                run.Host.AddChild(sub);
                at.Local = new LocalHost(run.Session, ScenarioGame.HumanA, () => at.Now += 1000);
                var screen = at.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(at.Local, run.Registry);
                at.Theme = screen.SharedTheme;
            }),
            new WaitFrames(6),
            new Act($"{at.Size}: dismiss the banner and the pending alert", () =>
            {
                at.Screen.NotNull().Hud.HideBanner();
                at.Screen.NotNull().Alerts.Close();
            }),
        };
        GameScreen S() => at.Screen.NotNull();
        // T329: WorldHud is the real HUD root; HudView is emptied by WorldHud.Mount, which re-parents its Bar, Toast and Dock, so checking HudView would check nothing.
        steps.AddRange(ResizeCycle(at, "hud", () => S().World));
        steps.Add(new Act("open the city", () => S().OpenCityScreen(at.Local.NotNull().Snapshot.Cities.First(it => it.Own != null))));
        steps.AddRange(ResizeCycle(at, "city", () => S().City, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("show the city's production", () => S().City.NotNull().SetProduction(true)));
        steps.AddRange(ResizeCycle(at, "city-production", () => S().City, contentCap: UiTokens.MaxContentWidth));
        steps.Add(new Act("close the city", () => S().OpenCityScreen(null)));
        steps.Add(new Act("open tech", () => S().OpenTechScreen()));
        steps.Add(new WaitUntil("the tech tree loaded", () => S().Tech.Tree != null && S().Tech.Layout.Nodes.Count > 0, 600));
        // T329: the default selection can be a researched tech, for which Fill builds no StatBar; pick an unresearched tech with a real cost so the
        // detail panel's bar and the tree's node cards exist in every resize phase (the shared-widget check is otherwise vacuous).
        steps.Add(new Act("select an unresearched tech with a cost", () =>
        {
            var tech = S().Tech;
            var node = tech.Layout.Nodes.FirstOrDefault(it => it.State == TechScreenModel.Researchable && it.Cost > 0)
                ?? tech.Layout.Nodes.First(it => it.State != TechScreenModel.Researched && it.Cost > 0);
            tech.Select(node.Name);
        }));
        steps.AddRange(ResizeCycle(at, "tech", () => S().Tech, it => it.GetType().Name.Contains("Card", StringComparison.Ordinal), UiTokens.MaxContentWidth));
        steps.Add(new Act("close tech", () => S().Tech.Close()));
        steps.Add(new Act($"{at.Size}: free the live-resize game screen", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Screen = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>
    /// T317: the alert, tutorial and decision-picker popups over the game screen, at this size. The alert is a real pending alert from the alerts game; the tutorial and the picker are
    /// opened by the scenario, so every size sees the same three shapes of popup without submitting a command.
    /// </summary>
    private static List<ScenarioStep> PopupSteps(At at)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build the game screen for the popups", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, at.Os);
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false, World3D = run.Host.GetViewport().FindWorld3D() };
                run.Host.AddChild(sub);
                at.Local = new LocalHost(run.ExtraSession("alerts"), ScenarioGame.HumanA, () => at.Now += 1000);
                var screen = at.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(at.Local, run.Registry);
                at.Theme = screen.SharedTheme;
            }),
            new WaitFrames(6),
            new WaitUntil("the pending alert is open", () => at.Screen.NotNull().Alerts.Visible, 600),
        };
        GameScreen S() => at.Screen.NotNull();
        steps.AddRange(Check(at, "alert", () => S().Alerts));
        steps.Add(new Act("close the alert", () => S().Alerts.Close()));
        steps.Add(new Act("show a tutorial", () => S().Tutorials.Open(new TutorialPage(TutorialTrigger.Introduction, "Introduction", "Found your first city to begin your story.", 0, 1))));
        steps.AddRange(Check(at, "tutorial", () => S().Tutorials));
        steps.Add(new Act("close the tutorial", () => S().Tutorials.Close()));
        steps.Add(new Act("show a decision picker", () => S().Picker.Open(ResizePicker())));
        steps.AddRange(Check(at, "picker", () => S().Picker));
        steps.Add(new Act("close the decision picker", () => S().Picker.Close()));
        steps.Add(new Act($"{at.Size}: free the popup game screen", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Screen = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>
    /// T317: the end screens at this size: the victory screen (a real one-turn domination through the victory game), the defeat screen, the World Congress result and the hotseat cover.
    /// Victory, defeat and the vote result run over a real game screen; the cover is the real <see cref="HandOverView"/>, which is full-screen by design.
    /// </summary>
    private static List<ScenarioStep> EndScreenSteps(At at)
    {
        var steps = new List<ScenarioStep>();
        steps.AddRange(EndGameSteps(at, "victory", "victory", () => at.Screen.NotNull().GameOver));
        steps.AddRange(EndGameSteps(at, "defeat", "defeat", () => at.Screen.NotNull().GameOver));
        steps.AddRange(EndGameSteps(at, "vote-result", "vote-result", () => at.Screen.NotNull().VoteResult));
        steps.AddRange(HotseatSteps(at));
        return steps;
    }

    /// <summary>A modal end screen over a real game screen of its own mode, checked at this size.</summary>
    private static List<ScenarioStep> EndGameSteps(At at, string mode, string name, Func<Node> root)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build the {name} game", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, at.Os);
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false, World3D = run.Host.GetViewport().FindWorld3D() };
                run.Host.AddChild(sub);
                var session = run.ExtraSession(mode);
                at.Local = new LocalHost(session, ScenarioGame.HumanA, () => at.Now += 1000);
                var screen = at.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(at.Local, run.Registry);
                at.Theme = screen.SharedTheme;
                // T317: the end turn runs on the main thread's loop; blocking it here would deadlock the awaited turn
                if (mode == "victory" && session.Game.VictoryData == null) at.Turn = at.Local.EndTurnAsync();
            }),
            new WaitFrames(6),
        };
        if (mode == "victory")
        {
            steps.Add(new WaitUntil("the victory turn finished", () => at.Turn == null || at.Turn.IsCompleted, 1200));
            steps.Add(new AssertQuery("the engine recorded the victory", () => at.Run.ExtraSession("victory").Game.VictoryData != null));
        }
        steps.Add(new WaitUntil($"the {name} screen is open", () => root() is Control control && control.IsVisibleInTree(), 600));
        steps.AddRange(Check(at, name, root));
        steps.Add(new Act("forget the end turn", () => at.Turn = null));
        steps.Add(new Act($"{at.Size}: free the {name} game", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Screen = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>T317: the hotseat pass-the-device cover at this size; it names the next player and offers Ready over an opaque screen.</summary>
    private static List<ScenarioStep> HotseatSteps(At at)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: show the hotseat cover", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, at.Os);
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
                run.Host.AddChild(sub);
                at.Theme = new UiTheme(run.Registry);
                var cover = at.Cover = new HandOverView();
                sub.AddChild(cover);
                cover.Init(at.Theme);
                cover.Show(HotseatModel.HandOver(ScenarioGame.HumanB, id => id));
            }),
        };
        steps.AddRange(Check(at, "hotseat", () => at.Cover.NotNull()));
        steps.Add(new Act($"{at.Size}: free the hotseat cover", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Cover = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>The menu screens, through a screen manager over the same size.</summary>
    private static List<ScenarioStep> MenuSteps(At at, Func<At, string, Func<Node>, int?, ScenarioStep[]> check)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build the menus", () =>
            {
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
                run.Host.AddChild(sub);
                sub.AddChild(new ColorRect { Color = new Color(0.12f, 0.14f, 0.2f), AnchorRight = 1, AnchorBottom = 1 });
                var theme = at.Theme = new UiTheme(run.Registry);
                at.Manager = new ScreenManager(theme);
                sub.AddChild(at.Manager);
                at.Manager.Push(new MainMenu(theme));
            }),
        };
        ScreenManager M() => at.Manager.NotNull();
        UiTheme T() => at.Theme.NotNull();
        steps.AddRange(check(at, "main-menu", () => M().Top.NotNull(), null));
        steps.Add(new Act("open new game", () => M().Push(new NewGameScreen(T(), NewGameForm.FromRulesetCache(), _ => throw new InvalidOperationException("not started")))));
        // T333: the players page is a full-width screen whose rows must stay inside the frame's content cap
        steps.AddRange(check(at, "new-game", () => M().Top.NotNull(), UiTokens.MaxContentWidth));
        steps.Add(new Act("back", () => M().Pop()));
        steps.Add(new Act("open host", () => M().Push(new HostScreen(T(), new HostSettingsForm(NewGameForm.FromRulesetCache()), new HostScreenEnvironment
        {
            Identity = () => run.Identity,
            Certificate = () => throw new InvalidOperationException("not hosting"),
            HostedGamesDirectory = run.Temp,
            DisplayName = "Host",
            Clock = () => 5000,
            SetClipboard = _ => { },
            Addresses = () => [new LocalAddress("192.168.1.20", "Wi-Fi", AddressScope.Lan)],
        }))));
        // T333: Host's tab pages were never inspected at 4K; its frame content and the width cap are checked now
        steps.AddRange(check(at, "host", () => M().Top.NotNull(), UiTokens.MaxContentWidth));
        steps.Add(new Act("back", () => M().Pop()));
        steps.Add(new Act("open the lobby", () =>
        {
            var lobbySettings = new LobbySettings { GameName = "Friday night", Ruleset = "Civ V - Vanilla", Difficulty = "King", MaxMajorCivs = 4, CityStateCount = 0, AiFill = true, Map = new MapParameters { Type = MapType.Pangaea, Shape = MapShape.Rectangular, Seed = 3, MapSize = MapSize.Tiny } };
            var lobby = at.Lobby = new ServerLobby(lobbySettings, hostFingerprint: Fingerprint, hostName: "Host");
            var (client, hostEnd) = InMemoryTransport.CreatePair();
            _ = lobby.AttachConnectionAsync(hostEnd);
            var clientSession = at.LobbySession = new ClientSession(_ => Task.FromResult<ITransport>(client), new ClientSessionOptions { Identity = run.Identity, DisplayName = "Host", RulesetHash = lobbySettings.ComputeRulesetHash(), ExpectedFingerprint = Fingerprint, MaxReconnectAttempts = 0 });
            at.Joined = clientSession.JoinAsync();
        }));
        steps.Add(new WaitUntil("the host joined its lobby", () => at.Joined.NotNull().IsCompleted && at.Joined.NotNull().Result.Joined, 3000));
        steps.Add(new Act("show the lobby", () => M().Push(new LobbyScreen(T(), NewGameForm.FromRulesetCache(), at.LobbySession.NotNull(), run.Identity.PlayerKey, at.Lobby.NotNull()))));
        steps.AddRange(check(at, "lobby", () => M().Top.NotNull(), UiTokens.MaxContentWidth));
        steps.Add(new Act("back", () => M().Pop()));
        steps.Add(new Act("open join", () => M().Push(new JoinScreen(T(), new JoinScreenEnvironment
        {
            Identity = run.Identity,
            DisplayName = "Friend",
            RulesetHash = () => "0",
            KnownHosts = new KnownHosts(),
            PersistKnownHosts = _ => { },
            Connect = (_, _) => throw new InvalidOperationException("not connecting"),
            RelayNeedsPrompt = _ => true,
        }))));
        steps.AddRange(check(at, "join", () => M().Top.NotNull(), UiTokens.MaxContentWidth));
        // T371: the relay trust prompt (the invite names a relay that is not the configured one) at every size
        steps.Add(new Act("type a relay invite and join", () =>
        {
            var screen = (JoinScreen)M().Top.NotNull();
            screen.AddressField.Text = InviteCode.FormatRelay("relay.other.example.net", 47830, "K7M2-9XQ4AB", HostKey.Fingerprint([1, 2, 3, 4]));
            screen.Join();
        }));
        steps.Add(new WaitUntil("the relay prompt is open", () => M().Top is JoinScreen { RelayPrompt.Length: > 0 }, 600));
        steps.AddRange(check(at, "join relay prompt", () => M().Top.NotNull(), null));
        steps.Add(new Act("decline the relay", () => ((JoinScreen)M().Top.NotNull()).RelayNoButton.EmitSignal(BaseButton.SignalName.Pressed)));
        steps.Add(new Act("back", () => M().Pop()));
        // T339: the browse screen with a list of games, the longest names the rules allow, and its join dialog
        steps.Add(new Act("open browse", () => M().Push(new BrowseScreen(T(), new BrowseScreenEnvironment
        {
            Relay = () => new Unciv.Net.Relay.RelayEndpoint("relay.example.org", 47830),
            Fetch = (_, _, _) => Task.FromResult(new DirectoryResult(
                new RelayInfo("Community relay with a rather long name", "admin@example.org", "open", 7, 3),
                [
                    BrowseScenario.Game("K7M2-9XQ4AB", BrowseScenario.StrangerKey, new string('W', 40), new string('H', 32), 1, 4, mods: ["Fun Mod"], password: true),
                    BrowseScenario.Game("22AA-33BBCC", BrowseScenario.FriendKey, "Weekly league", "Bob", 2, 6),
                    BrowseScenario.Game("66GG-77HHJJ", BrowseScenario.FarKey, "From the future", "Dave", 1, 4, proto: Unciv.Net.ProtocolVersion.Current + 1),
                ], 0, false, 42)),
            Context = () => new BrowseContext(Unciv.Net.ProtocolVersion.Current, [BrowseScenario.Vanilla], [], (_, _) => "hash", _ => false),
        }))));
        steps.Add(new WaitUntil("the list is shown", () => M().Top is BrowseScreen { Status: BrowseStatus.Ready }, 600));
        steps.Add(new Act("pick the first game", () => ((BrowseScreen)M().Top.NotNull()).RowButton(0)?.EmitSignal(BaseButton.SignalName.Pressed)));
        steps.AddRange(check(at, "browse", () => M().Top.NotNull(), null));
        steps.Add(new Act("open the join dialog", () => ((BrowseScreen)M().Top.NotNull()).OpenJoinDialog()));
        steps.AddRange(check(at, "browse join dialog", () => ((BrowseScreen)M().Top.NotNull()).Dialog.NotNull(), null));
        steps.Add(new Act("back", () => M().Pop()));
        steps.Add(new Act("open options", () => M().Push(new OptionsScreen(run.Settings, T(), null, ["Retro", "Neon"], null, new RelaySettingsStore(() => new RelaySettings { Url = "unciv-relay://relay.example.org:47831?fp=" + BrowseScenario.StrangerKey, AcceptedRelays = ["a.very.long.relay.host.name.example.net:47830", "friend.example.net:47830"] }, _ => true)))));
        steps.AddRange(check(at, "options", () => M().Top.NotNull(), null));
        // T322: on every page the rows keep within the form width cap (a label next to its control, not across a 4K window)
        for (int tab = 0; tab < 8; tab++) // 8 tabs with Language; it is absent without a localization and SelectTab then ignores the index
        {
            int index = tab;
            steps.Add(new Act($"{at.Size} options page {index}: select", () => ((OptionsScreen)M().Top.NotNull()).Panel.SelectTab(index)));
            steps.Add(new WaitFrames(4));
            steps.Add(new Act($"{at.Size} options page {index}: rows within the cap", () =>
            {
                var panel = ((OptionsScreen)M().Top.NotNull()).Panel;
                if (panel.SelectedTab != index) return;
                var (rows, max) = panel.CurrentRows;
                foreach (string it in LayoutChecks.WidthProblems(rows, max)) at.Run.Problems.Add($"{at.Size} options page {index}: {it}");
            }));
        }
        steps.Add(new Act("back", () => M().Pop()));
        steps.Add(new Act("show the shared widgets", () => M().Push(at.Gallery = new WidgetGallery(T()))));
        steps.AddRange(check(at, "widgets", () => M().Top.NotNull(), null));
        steps.Add(new Act("back", () => M().Pop()));
        steps.AddRange(SavesSteps(at, check));
        steps.Add(new Act("show a modal", () => M().Popups.Show(new PopupSpec("Leave the game?", "Progress since the last save is lost.", [new PopupChoice("Cancel"), new PopupChoice("Leave", null, ChoiceRole.Primary)]))));
        steps.AddRange(check(at, "modal", () => M().Popups, null));
        steps.Add(new Act($"{at.Size}: free the menus", () => at.Sub.NotNull().QueueFree()));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>T321: the saves dialog, with a readable save in its list (the compact breakpoint drops the ruleset and date columns).</summary>
    private static ScenarioStep[] SavesSteps(At at, Func<At, string, Func<Node>, int?, ScenarioStep[]> check)
    {
        var run = at.Run;
        return
        [
            new Act($"{at.Size}: show the saves dialog", () =>
            {
                var store = new SaveStore(Path.Combine(run.Temp, "saves"));
                if (store.List().Count == 0) store.Write("Resize save", UncivFiles.GameInfoToString(new GameInfoPreview(run.Session.Game)));
                at.Saves = new SaveController(store, run.Settings, at.Theme.NotNull(), _ => throw new InvalidOperationException("not loading"), _ => { });
                at.Sub.NotNull().AddChild(at.Saves);
                at.Saves.OpenDialog();
            }),
            .. check(at, "saves", () => at.Saves.NotNull(), UiTokens.MaxContentWidth),
            new Act("close the saves dialog", () => at.Saves.NotNull().CloseDialog()),
        ];
    }

    /// <summary>
    /// T316: a screen already open is checked at its size, then the viewport is resized to 4K and back to 1366x768 and the UI scale is changed and restored, checking after each: the screen has to follow the live
    /// window (a width taken from the theme at construction would stay what it was and overflow or waste the window).
    /// </summary>
    private static ScenarioStep[] ResizeCycle(At at, string name, Func<Node> root, Func<BaseButton, bool>? exempt = null, int? contentCap = null)
    {
        var steps = new List<ScenarioStep>();
        // T326: the tip is shown once, in the first phase, and stays visible across the viewport resize and the two scale changes (showing it again would measure it anew,
        // which is exactly what the check must not let pass); the width at scale 1 is the reference the later phases are measured against
        float? tipWidthAtScale1 = null;
        bool tipShown = false;
        void Phase(string label, Action change)
        {
            steps.Add(new Act($"{name}: {label}", () =>
            {
                at.Size = "resize-" + label;
                change();
            }));
            steps.AddRange(Check(at, name, root, exempt, contentCap));
            steps.Add(new AssertQuery($"{name} {label}: the main menu column follows the live scale", () => name != "main-menu" || MenuColumnWidth(root()) == at.Theme.NotNull().Px(260)));
            // T329: the shared widgets used by the game screens (the HUD's stat chips, the city's stat chips, bars and production rows, the tech cards' bars) follow the
            // live scale too, not only the gallery in the menu cycle
            steps.Add(new AssertQuery($"{name} {label}: the game screen's shared widgets follow the live scale", () =>
            {
                if (name is not ("hud" or "city" or "city-production" or "tech")) return true;
                var wrong = LayoutChecks.SharedWidgetProblems(root(), at.Theme.NotNull());
                foreach (string it in wrong) GD.PrintErr($"[layout-matrix] {at.Size} {name}: {it}");
                return wrong.Count == 0;
            }));
            steps.Add(WidgetsPresent(at, name, label, root));
            // T323: the join column and the shared widgets (list row, list panel, icon label, stat chip, stat bar, tooltip) and the modal are at the live scale, not the one they were built at
            steps.Add(new AssertQuery($"{name} {label}: the join column follows the live scale", () => name != "join" || ColumnWidth(root(), "JoinColumn") == at.Theme.NotNull().Px(420)));
            steps.Add(new AssertQuery($"{name} {label}: the shared widgets follow the live scale", () =>
            {
                if (name != "widgets") return true;
                var wrong = at.Gallery.NotNull().Wrong(at.Theme.NotNull());
                foreach (string it in wrong) GD.PrintErr($"[layout-matrix] {at.Size} widgets: {it}");
                return wrong.Count == 0;
            }));
            if (name == "widgets" && !tipShown)
            {
                tipShown = true;
                steps.Add(new Act($"{name} {label}: show a tooltip", () => at.Gallery.NotNull().ShowTip()));
                steps.Add(new WaitFrames(2));
            }
            // T326: a tip SHOWN across a live resize/scale change is re-measured too, not only a hidden one (the hidden check above resets it to the minimum)
            if (name == "widgets")
            {
                steps.Add(new AssertQuery($"{name} {label}: the visible tooltip text follows the live scale", () =>
                {
                    var gallery = at.Gallery.NotNull();
                    var ui = at.Theme.NotNull();
                    if (!gallery.TipShowing || gallery.ShownTipText != WidgetGallery.TipText) return false;
                    if (Math.Abs(ui.Scale - 1f) < 0.001f) tipWidthAtScale1 ??= gallery.ShownTipWidth;
                    float expected = (tipWidthAtScale1 ?? gallery.ShownTipWidth) * ui.Scale;
                    float tolerance = Math.Max(4f, expected * 0.05f); // the font rounds its size to whole pixels, so the width is only proportional
                    bool ok = Math.Abs(gallery.ShownTipWidth - expected) <= tolerance;
                    if (!ok) GD.PrintErr($"[layout-matrix] {at.Size} widgets: the visible tooltip text is {gallery.ShownTipWidth} wide, the live scale {ui.Scale} gives {expected}");
                    return ok;
                }));
            }
            steps.Add(new AssertQuery($"{name} {label}: the modal panel is at the live width", () =>
            {
                if (name != "modal") return true;
                var ui = at.Theme.NotNull();
                var modal = root().FindChildren("*", "", true, false).OfType<Modal>().FirstOrDefault();
                return modal != null && Math.Abs(modal.Frame.CustomMinimumSize.X - Math.Min(ui.Px(Modal.MinWidth), at.Sub.NotNull().Size.X * Modal.ViewportShare)) < 0.5f;
            }));
            // T328: the game menu's confirm text is the wrap control Fit() sized, so it has to be re-applied at the live scale (the panel width alone would hide a stale wrap width)
            steps.Add(new AssertQuery($"{name} {label}: the game menu's confirm text follows the live scale", () =>
            {
                if (name != "game-menu") return true;
                var ui = at.Theme.NotNull();
                float panel = Math.Min(ui.Px(Modal.MinWidth), at.Sub.NotNull().Size.X * Modal.ViewportShare);
                float body = Math.Max(ui.Px(120), panel - 2 * ui.Px(UiTokens.S4) - 2 * ui.Px(UiTokens.BorderWidth));
                if (root().FindChild("ConfirmText", true, false) is not Label text) return false;
                bool ok = Math.Abs(text.CustomMinimumSize.X - body) <= 0.5f;
                if (!ok) GD.PrintErr($"[layout-matrix] {at.Size} game-menu: the confirm text wraps at {text.CustomMinimumSize.X}, the live scale gives {body}");
                return ok;
            }));
            // T321: crossing the Compact breakpoint swaps the save list between five and three columns without rebuilding the dialog (same width rule as SaveController.IsCompact)
            steps.Add(new AssertQuery($"{name} {label}: the saves table follows the live breakpoint", () =>
            {
                if (name != "saves") return true;
                if (root().FindChild("SaveTable", true, false) is not DataTable table) return false;
                bool compact = UiTokens.BreakpointFor(at.Sub.NotNull().Size.X / at.Theme.NotNull().Scale) == UiBreakpoint.Compact;
                return table.ColumnCount == (compact ? 3 : 5);
            }));
            steps.Add(new AssertQuery($"{name} {label}: the popup stays open through the resize", () =>
                name is not ("alert" or "tutorial" or "picker") || root() is Modal { Visible: true }));
        }
        Phase("1366x768", () => { });
        Phase("3840x2160", () => at.Sub.NotNull().Size = new Vector2I(3840, 2160));
        Phase("1366x768-again", () => at.Sub.NotNull().Size = new Vector2I(1366, 768));
        Phase("scale-1.5", () => at.Theme.NotNull().SetScale(1.5f));
        Phase("scale-1", () => at.Theme.NotNull().SetScale(1f));
        if (name == "saves")
        {
            // T321: 960x640 is below the 1000 px Compact breakpoint, then back to Regular
            Phase("960x640", () => at.Sub.NotNull().Size = new Vector2I(960, 640));
            Phase("1366x768-after-compact", () => at.Sub.NotNull().Size = new Vector2I(1366, 768));
        }
        if (tipShown)
        {
            steps.Add(new Act($"{name}: hide the tooltip", () => at.Gallery.NotNull().HideTip()));
            steps.Add(new WaitFrames(2));
        }
        return [.. steps];
    }

    /// <summary>
    /// T329: asserts the root checked in a live-resize phase really contains the visible widgets <see cref="LayoutChecks.SharedWidgetProblems"/> looks at. Without it the HUD
    /// (an emptied <c>HudView</c>: its Bar/Toast/Dock are re-parented into WorldHud) and a tech screen opened on a researched tech pass the shared-widget check with zero widgets.
    /// </summary>
    private static ScenarioStep WidgetsPresent(At at, string name, string label, Func<Node> root) => new AssertQuery($"{name} {label}: the checked screen contains its widgets", () =>
    {
        if (name is not ("hud" or "city" or "city-production" or "tech")) return true;
        var (chips, bars, icons, rows, cards) = LayoutChecks.VisibleWidgets(root());
        bool ok = name switch
        {
            "hud" => chips >= 3,
            "city" => chips >= 1 && bars >= 1,
            "city-production" => rows >= 1,
            "tech" => cards >= 30 && bars >= 1,
            _ => true,
        };
        if (!ok) GD.PrintErr($"[layout-matrix] {at.Size} {name}: expected widgets, found chips={chips} bars={bars} icons={icons} rows={rows} cards={cards}");
        return ok;
    });

    /// <summary>
    /// T321: the dialogs over a game screen (the pending alert, a tutorial, the decision picker) are opened once and run through the same live resize and scale cycle as the menu screens.
    /// </summary>
    private static List<ScenarioStep> PopupResizeSteps(At at)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build the game screen for the popups", () =>
            {
                DisplayProfile.Current = new DisplayProfile(false, 0f, at.Os);
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false, World3D = run.Host.GetViewport().FindWorld3D() };
                run.Host.AddChild(sub);
                at.Local = new LocalHost(run.Session, ScenarioGame.HumanA, () => at.Now += 1000);
                var screen = at.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(at.Local, run.Registry);
                at.Theme = screen.SharedTheme;
            }),
            new WaitFrames(6),
            new Act("dismiss the banner, keep the pending alert", () => at.Screen.NotNull().Hud.HideBanner()),
        };
        GameScreen S() => at.Screen.NotNull();
        steps.AddRange(ResizeCycle(at, "alert", () => S().Alerts));
        steps.Add(new Act("close the alert", () => S().Alerts.Close()));
        steps.Add(new Act("show a tutorial", () => S().Tutorials.Open(new TutorialPage(TutorialTrigger.Introduction, "Introduction", "Found your first city to begin your story.", 0, 1))));
        steps.AddRange(ResizeCycle(at, "tutorial", () => S().Tutorials));
        steps.Add(new Act("close the tutorial", () => S().Tutorials.Close()));
        steps.Add(new Act("show a decision picker", () => S().Picker.Open(ResizePicker())));
        steps.AddRange(ResizeCycle(at, "picker", () => S().Picker));
        steps.Add(new Act("close the decision picker", () => S().Picker.Close()));
        // T328: the game menu (its Resign entry asks first) is opened once and resized live, so its confirm text's wrap width must follow the window and the scale
        steps.Add(new Act("open the game menu", () => S().GameMenu.Open(GameMenuModel.Build(Unciv.Queries.Victory.GameOverQuery.Playing, false))));
        steps.Add(new Act("ask the game menu's confirm", () => S().GameMenu.ButtonById("Resign")?.EmitSignal(BaseButton.SignalName.Pressed)));
        steps.AddRange(ResizeCycle(at, "game-menu", () => S().GameMenu));
        steps.Add(new Act("close the game menu", () => S().GameMenu.Close()));
        steps.Add(new Act($"{at.Size}: free the popup game screen", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Screen = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    /// <summary>
    /// T405: the in-game SaveController follows the game screen's bound theme (as App.ShowGame wires it), so at 1600x900 with the user scale at 1.5 it must take the game UI's
    /// breakpoint (1600 / (1.5 * 1.25) = 853, Compact -> three columns and the 0.94 dialog share). The unbound theme T321 had to work around would stay Regular (five columns, 0.8).
    /// </summary>
    private static List<ScenarioStep> InGameSaveSteps(At at)
    {
        var run = at.Run;
        return
        [
            new Act($"{at.Size}: build the game screen and the in-game save dialog", () =>
            {
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
                run.Host.AddChild(sub);
                var local = at.Local = new LocalHost(run.Session, ScenarioGame.HumanA, () => at.Now += 1000);
                var screen = at.Screen = new GameScreen();
                sub.AddChild(screen);
                screen.Start(local, run.Registry);
                at.Theme = screen.SharedTheme.NotNull();
                at.Theme.SetScale(1.5f); // the user's UI scale
                at.Theme.Bind(sub); // a real user-scale change also refits the breakpoint through the viewport's resize, which SetScale alone does not
                var store = new SaveStore(Path.Combine(run.Temp, "saves-in-game"));
                if (store.List().Count == 0) store.Write("In-game save", UncivFiles.GameInfoToString(new GameInfoPreview(run.Session.Game)));
                // App.ShowGame's wiring: the controller starts with a theme of its own and follows every theme the screen builds (start, load, hand-over)
                var saves = at.Saves = new SaveController(store, run.Settings, new UiTheme(run.Registry), _ => throw new InvalidOperationException("not loading"), _ => { });
                screen.SharedThemeChanged += saves.UseTheme;
                saves.UseTheme(at.Theme);
                sub.AddChild(saves);
                saves.OpenDialog();
            }),
            new WaitFrames(8),
            .. Check(at, "saves-in-game", () => at.Saves.NotNull()),
            new AssertQuery("at 1600x900 with user scale 1.5 the game UI is Compact at scale 1.875", () =>
            {
                var t = at.Theme.NotNull();
                bool ok = t.Breakpoint == UiBreakpoint.Compact && Math.Abs(t.Scale - 1.875f) < 0.001f;
                if (!ok) GD.PrintErr($"[layout-matrix] in-game saves: breakpoint {t.Breakpoint}, scale {t.Scale}, size {at.Sub.NotNull().Size}");
                return ok;
            }),
            new AssertQuery("the in-game saves table takes the game UI's breakpoint (three columns, not the unbound five)", () => Columns(at) == 3),
            new AssertQuery("the in-game saves panel takes the game UI's Compact share", () =>
            {
                if (at.Saves.NotNull().FindChild("SaveFrame", true, false) is not PanelContainer panel) return false;
                var size = at.Sub.NotNull().Size;
                return panel.Size == (new Vector2(size.X, size.Y) * 0.94f).Floor();
            }),
            new Act($"{at.Size}: free the in-game save dialog", () =>
            {
                at.Sub.NotNull().QueueFree();
                at.Screen = null;
                at.Saves = null;
            }),
            new WaitFrames(2),
        ];
    }

    /// <summary>
    /// T321: a controller given a theme nothing binds (the defensive fallback; App.ShowGame now shares the game theme, see <see cref="InGameSaveSteps"/>) takes the Compact
    /// breakpoint from the width, so a live 1366 -> 960 -> 1366 resize still swaps five and three columns.
    /// </summary>
    private static List<ScenarioStep> UnboundSaveSteps(At at)
    {
        var run = at.Run;
        var steps = new List<ScenarioStep>
        {
            new Act($"{at.Size}: build an unbound save dialog", () =>
            {
                var sub = at.Sub = new SubViewport { Size = new Vector2I(at.Width, at.Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
                run.Host.AddChild(sub);
                at.Theme = new UiTheme(run.Registry);
                var store = new SaveStore(Path.Combine(run.Temp, "saves-unbound"));
                if (store.List().Count == 0) store.Write("Unbound save", UncivFiles.GameInfoToString(new GameInfoPreview(run.Session.Game)));
                at.Saves = new SaveController(store, run.Settings, at.Theme, _ => throw new InvalidOperationException("not loading"), _ => { });
                sub.AddChild(at.Saves);
                at.Saves.OpenDialog();
            }),
            new WaitFrames(8),
        };
        steps.AddRange(Check(at, "saves-unbound", () => at.Saves.NotNull()));
        steps.Add(new AssertQuery("at 1366x768 the unbound saves table has five columns", () => Columns(at) == 5));
        steps.Add(new Act("resize the unbound save dialog to 960x640", () =>
        {
            at.Size = "unbound-960x640";
            at.Sub.NotNull().Size = new Vector2I(960, 640);
        }));
        steps.AddRange(Check(at, "saves-unbound", () => at.Saves.NotNull()));
        steps.Add(new AssertQuery("at 960x640 the unbound saves table is compact (three columns)", () => Columns(at) == 3));
        steps.Add(new Act("resize the unbound save dialog back to 1366x768", () =>
        {
            at.Size = "unbound-1366x768";
            at.Sub.NotNull().Size = new Vector2I(1366, 768);
        }));
        steps.AddRange(Check(at, "saves-unbound", () => at.Saves.NotNull()));
        steps.Add(new AssertQuery("back at 1366x768 the unbound saves table has five columns again", () => Columns(at) == 5));
        steps.Add(new Act($"{at.Size}: free the unbound save dialog", () =>
        {
            at.Sub.NotNull().QueueFree();
            at.Saves = null;
        }));
        steps.Add(new WaitFrames(2));
        return steps;
    }

    private static int Columns(At at) => at.Saves.NotNull().FindChild("SaveTable", true, false) is DataTable table ? table.ColumnCount : -1;

    private static PickerSpec ResizePicker() => new(
        PickerKind.FreeGreatPerson,
        "Choose a free great person",
        "Choose a great person to join your civilization. This prompt is long enough to wrap inside the dialog at every window size.",
        [new PickerSlot("greatPerson", "Great person",
        [
            new PickerOption("Scientist", "Great Scientist", "A Great Scientist can build an academy or discover a technology."),
            new PickerOption("Engineer", "Great Engineer", "A Great Engineer can hurry production in a city."),
        ])],
        "Choose",
        _ => []);

    private static float ColumnWidth(Node screen, string name) => screen.FindChild(name, true, false) is Control column ? column.CustomMinimumSize.X : -1f;

    private static float MenuColumnWidth(Node menu) => menu.FindChild("MenuColumn", true, false) is Control column ? column.CustomMinimumSize.X : -1f;

    /// <summary>What the whole run shares: the game, the host control, where pictures go and the problems found.</summary>
    private sealed class Run(Control host, GameSession session, string? outDir)
    {
        public Control Host { get; } = host;

        public GameSession Session { get; } = session;

        public string? OutDir { get; } = outDir;

        public List<string> Problems { get; } = [];

        public AssetRegistry Registry { get; } = AssetRegistry.ForRuleset(session.Game.Ruleset);

        public string Temp { get; } = Directory.CreateTempSubdirectory("unciv-layout-matrix-").FullName;

        public ClientSettingsService Settings => settings ??= new ClientSettingsService(new ClientSettingsStore(Path.Combine(Temp, "settings.json")));

        public ClientIdentity Identity { get; } = ClientIdentity.Generate();

        private ClientSettingsService? settings;

        private readonly Dictionary<string, GameSession> extraSessions = [];

        /// <summary>T317: one extra game per end screen, built once and reused at every size (a victory game has to be played to the end only once).</summary>
        public GameSession ExtraSession(string mode)
        {
            if (extraSessions.TryGetValue(mode, out var cached)) return cached;
            var session = ScenarioGame.Create(alerts: mode == "alerts", victory: mode == "victory", defeat: mode == "defeat", voteResult: mode == "vote-result");
            extraSessions[mode] = session;
            return session;
        }
    }

    /// <summary>The state of one window size: its viewport and the screens built in it.</summary>
    private sealed class At(Run run, int width, int height, float os)
    {
        public Run Run { get; } = run;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public float Os { get; } = os;

        public string Size { get; set; } = $"{width}x{height}" + (os > 1f ? "@" + os.ToString(CultureInfo.InvariantCulture) : "");

        public SubViewport? Sub { get; set; }

        public GameScreen? Screen { get; set; }

        public UiTheme? Theme { get; set; }

        public ScreenManager? Manager { get; set; }

        public LocalHost? Local { get; set; }

        public long Now { get; set; } = 1000;

        public Task<JoinResult>? Joined { get; set; }

        public ClientSession? LobbySession { get; set; }

        public ServerLobby? Lobby { get; set; }

        public WidgetGallery? Gallery { get; set; }

        public SaveController? Saves { get; set; }

        public HandOverView? Cover { get; set; }

        /// <summary>T317: the one end turn a victory screen needs; a step must not block the main thread waiting for it (its continuation runs on the main thread).</summary>
        public Task? Turn { get; set; }
    }

    /// <summary>Not judged by the target-height check: the city grid's tile buttons (map cells, not controls to hit) and check boxes (a label row whose height is its text line).</summary>
    private static bool Exempt(BaseButton button) => button is CityTileButton or CheckBox;

    private static void Save(SubViewport viewport, string path)
    {
        using var image = viewport.GetTexture().GetImage() ?? throw new IOException($"no image for {path}");
        var error = image.SavePng(path);
        if (error != Godot.Error.Ok) throw new IOException($"Could not write {path}: {error}");
    }
}
