using Godot;
using Unciv.Commands;
using Unciv.GodotClient.Assets;
using Unciv.GodotClient.Play;
using Unciv.GodotClient.Settings;
using Unciv.Harness;

namespace Unciv.GodotHarness;

/// <summary>
/// T301: the world screen put through the window changes a player makes: maximize, restore, fullscreen and back (through <see cref="DisplayServer.WindowSetMode"/>, which a window
/// manager may refuse or delay, so those steps only assert consistency, never a size), a burst of size changes without a frame between them (an X server takes seconds per size under software rendering, so the list is short), and aspect-ratio extremes
/// (very wide, very tall, tiny, huge). After each step the viewport must equal the window, the camera must be finite, the HUD must lie inside the window (at sizes where it can
/// fit) and frames must keep running. No command is submitted and nothing is captured (a window manager decides the sizes, so there is nothing to compare). On a real display run
/// it with <c>UNCIV_RESIZE_REAL=1</c>, which also writes each size to the log; <c>UNCIV_RESIZE_QUALITY</c> = Low (default), High or Ultra applies that graphics preset first (Ultra needs Forward+).
/// </summary>
public sealed class WindowResizeScenario : Scenario
{
    public override string Name => "window-resize";

    public override bool Uses3D => true;

    public override bool Solo => true;

    public override Vector2I Viewport => new(1280, 720);

    public override IReadOnlyList<string> Checklist => ["Maximize, restore and fullscreen the window by hand: does the map and HUD follow without flicker, and does the game keep running?"];

    public override IReadOnlyList<ScenarioStep> Build(Control host, GameSession session)
    {
        var game = session.Game;
        var localHost = new LocalHost(session, ScenarioGame.HumanA, () => 1000L);
        var screen = new GameScreen();
        host.AddChild(screen);
        screen.Start(localHost, AssetRegistry.ForRuleset(game.Ruleset));
        screen.TutorialsEnabled = false;
        var map = screen.Map;
        if (System.Environment.GetEnvironmentVariable("UNCIV_RESIZE_QUALITY") is { Length: > 0 } quality && Enum.TryParse<GraphicsQuality>(quality, true, out var level)) map.ApplyGraphics(GraphicsModel.Preset(level));
        var window = host.GetWindow();
        bool real = System.Environment.GetEnvironmentVariable("UNCIV_RESIZE_REAL") == "1";
        int sizeSignals = 0;
        host.GetViewport().SizeChanged += () => sizeSignals++;
        ulong lastFrame = 0;

        bool Finite(float v) => float.IsFinite(v);
        bool Sane() => Finite(map.Zoom) && map.Zoom > 0f && Finite(map.Focus.X) && Finite(map.Focus.Y) && Finite(map.Focus.Z)
            && Finite(map.Camera.Position.X) && Finite(map.Camera.Position.Y) && Finite(map.Camera.Position.Z) && Finite(map.Camera.Size);
        bool ViewportIsWindow() => host.GetViewport().GetVisibleRect().Size == (Vector2)window.Size;
        bool FramesRun() { bool ok = Engine.GetProcessFrames() > lastFrame; lastFrame = Engine.GetProcessFrames(); return ok; }

        var steps = new List<ScenarioStep> { new WaitFrames(6), new AssertQuery("the camera starts finite", Sane) };

        void Settle(string what, int frames)
        {
            steps.Add(new WaitFrames(frames));
            steps.Add(new AssertQuery($"after {what}: the viewport equals the window, the camera is finite and frames run", () =>
            {
                if (real) GD.Print($"[window-resize] {what}: window {window.Size} mode {window.Mode} viewport {host.GetViewport().GetVisibleRect().Size} zoom {map.Zoom} focus {map.Focus} signals {sizeSignals}");
                return ViewportIsWindow() && Sane() && FramesRun();
            }));
        }

        void Fits(string what)
        {
            steps.Add(new AssertQuery($"after {what}: the HUD overlaps nothing and leaves nothing outside the window", () =>
            {
                var problems = LayoutChecks.Problems(screen, new Rect2(Vector2.Zero, host.GetViewport().GetVisibleRect().Size));
                foreach (string problem in problems.Take(8)) GD.PrintErr($"[window-resize] {what}: {problem.Replace("\n", "\\n")}");
                return problems.Count == 0;
            }));
        }

        void Size(Vector2I size, bool fits)
        {
            steps.Add(new Act($"set the window to {size.X}x{size.Y}", () => { window.Mode = Window.ModeEnum.Windowed; window.Size = size; }));
            Settle($"{size.X}x{size.Y}", 6);
            if (fits) Fits($"{size.X}x{size.Y}");
        }

        void Mode(Window.ModeEnum mode)
        {
            steps.Add(new Act($"window mode {mode}", () => DisplayServer.WindowSetMode((DisplayServer.WindowMode)(int)mode)));
            Settle($"mode {mode}", 40);
        }

        Settle("start", 2);
        Fits("start");
        Mode(Window.ModeEnum.Maximized);
        Mode(Window.ModeEnum.Windowed);
        Mode(Window.ModeEnum.Maximized);
        // T301 review: the unrelated settings change runs in Fullscreen as well as Maximized: without a window manager (Xvfb in CI) a Maximized request does nothing, so only the
        // Fullscreen run can fail with the old behaviour (Apply forcing the stored "Windowed" and the minimum size back).
        void UnrelatedChange(Window.ModeEnum mode)
        {
            Window.ModeEnum modeBefore = default;
            steps.Add(new Act($"in {mode}: record the window mode and minimum size, then apply a settings change that is not about the window", () =>
            {
                modeBefore = window.Mode;
                window.MinSize = Vector2I.Zero;
                var before = new ClientSettings();
                ClientSettingsApplier.Apply(before with { ShowYields = !before.ShowYields }, window, before);
            }));
            steps.Add(new WaitFrames(10));
            steps.Add(new AssertQuery($"in {mode}: an unrelated settings change leaves the window mode and minimum size alone (it used to force Windowed and 640x480)", () => window.Mode == modeBefore && window.MinSize == Vector2I.Zero));
        }
        UnrelatedChange(Window.ModeEnum.Maximized);
        Mode(Window.ModeEnum.Fullscreen);
        UnrelatedChange(Window.ModeEnum.Fullscreen);
        // the paths that must still apply: a changed WindowMode and a changed UiScale (the options screen), and the start (no previous settings)
        steps.Add(new Act("apply a changed window mode (Borderless) and UI scale (1.5)", () =>
        {
            var before = new ClientSettings();
            window.Mode = Window.ModeEnum.Windowed;
            ClientSettingsApplier.Apply(before with { WindowMode = "Borderless" }, window, before);
        }));
        steps.Add(new WaitFrames(20));
        steps.Add(new AssertQuery("a changed window mode is applied (Borderless is Fullscreen)", () => window.Mode == Window.ModeEnum.Fullscreen));
        steps.Add(new Act("apply a changed UI scale", () =>
        {
            var before = new ClientSettings();
            ClientSettingsApplier.Apply(before with { UiScale = 1.5f }, window, before);
        }));
        steps.Add(new WaitFrames(6));
        steps.Add(new AssertQuery("a changed UI scale is applied", () => window.ContentScaleFactor == 1.5f && window.ContentScaleMode == Window.ContentScaleModeEnum.CanvasItems && window.MinSize == new Vector2I(Unciv.Assets.UiTokens.MinWindowWidth, Unciv.Assets.UiTokens.MinWindowHeight) && Sane()));
        steps.Add(new Act("restore the UI scale and window mode at start (no previous settings)", () => { var s = new ClientSettings(); ClientSettingsApplier.Apply(s, window); }));
        steps.Add(new WaitFrames(20));
        steps.Add(new AssertQuery("start applies Windowed and scale 1", () => window.Mode == Window.ModeEnum.Windowed && window.ContentScaleFactor == 1f && window.ContentScaleMode == Window.ContentScaleModeEnum.Disabled));
        Mode(Window.ModeEnum.Windowed);
        Mode(Window.ModeEnum.ExclusiveFullscreen);
        Mode(Window.ModeEnum.Windowed);
        steps.Add(new Act("four size changes with no frame between them", () =>
        {
            window.Mode = Window.ModeEnum.Windowed;
            for (int i = 0; i < 4; i++) window.Size = new Vector2I(400 + i * 500, 300 + (i % 3) * 250);
        }));
        Settle("the burst", 6);
        Size(new Vector2I(640, 480), true);
        Size(new Vector2I(3840, 2160), true);
        Size(new Vector2I(3840, 400), false);
        Size(new Vector2I(400, 3000), false);
        Size(new Vector2I(1, 1), false);
        Size(new Vector2I(8, 8), false);
        Size(new Vector2I(100, 60), false);
        Size(new Vector2I(1280, 720), true);
        return steps;
    }
}
