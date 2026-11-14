using Godot;
using Unciv.Assets.Packaging;
using Unciv.Models.Metadata;
using Unciv.Models.Ruleset;

namespace Unciv.GodotClient.Assets;

/// <summary>
/// Loads the rulesets: Vanilla, Gods and Kings and Brave New World from the jsons found by <see cref="ContentLocator"/> (bundled next to an exported build (or extracted from the pck, <see cref="ContentRoot"/>), or the checkout's
/// <c>csharp/data/jsons</c>), plus every local mod in the per-user mods folder (<see cref="UserPaths.Mods"/>, C042).
/// </summary>
public static class RulesetLocator
{
    /// <summary>T363: the app's own base ruleset for the menu theme and sounds is the default game ruleset, Brave New World (not a Vanilla pin).</summary>
    public static Ruleset LoadDefault()
    {
        System.IO.Directory.CreateDirectory(UserPaths.Mods);
        RulesetCache.Current.Clear();
        RulesetCache.Current.LoadRulesets(FileSource());
        return RulesetCache.Current.Get(BaseRuleset.Civ_V_BNW.FullName())
            ?? throw new InvalidOperationException("The default ruleset " + BaseRuleset.Civ_V_BNW.FullName() + " did not load");
    }

    /// <summary>T247: where rulesets load from and mods are installed (also how a mod downloaded in the lobby is hot-loaded).</summary>
    public static DirectoryRulesetFileSource FileSource()
    {
        var exeDir = ContentRoot.Directory ?? System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        var dir = ContentLocator.FindJsons(exeDir, ProjectSettings.GlobalizePath("res://"), System.IO.Directory.Exists)
            ?? throw new System.IO.DirectoryNotFoundException($"No ruleset jsons: expected {ContentLocator.BundledJsons(exeDir)} or csharp/data/jsons above the project");
        return new DirectoryRulesetFileSource(dir, UserPaths.Mods);
    }
}

/// <summary>C025: finds the game's translation files (<c>jsons/translations</c>) in the same place <see cref="RulesetLocator"/> finds the jsons: bundled next to an exported build, else the checkout's <c>csharp/data/jsons</c>.</summary>
public static class TranslationLocator
{
    public static string Directory()
    {
        var exeDir = ContentRoot.Directory ?? System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        var jsons = ContentLocator.FindJsons(exeDir, ProjectSettings.GlobalizePath("res://"), System.IO.Directory.Exists)
            ?? throw new System.IO.DirectoryNotFoundException($"No translation files: expected {ContentLocator.BundledJsons(exeDir)}/translations or csharp/data/jsons/translations above the project");
        return System.IO.Path.Combine(jsons, "translations");
    }
}
