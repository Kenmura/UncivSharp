namespace Unciv.GodotClient.Setup;

/// <summary>T340: a warning the UI shows while the game is still playable (unlike <see cref="ConnectionView.Final"/>); it can be put aside.</summary>
public sealed record HostMigrationNotice(string Title, string Detail);

/// <summary>
/// T340: the words for the one hole host migration accepts. With exactly two humans the electorate is the successor's own seat (design/HOST-MIGRATION.md section 2), so a partition between the
/// two machines can give each machine a host. The successor must be told that its copy may really be one of two games, and the old host, once it learns the newer epoch, that its copy is out of date
/// and refuses to continue. Both texts name the recovery: agree which copy continues and join that host with an invite, keeping the same key and seat.
/// </summary>
public static class HostMigrationModel
{
    /// <summary>More players than this have a quorum that needs a second machine, so a lone promotion cannot be a two-machine split.</summary>
    public const int TwoHumanPlayers = 2;

    /// <summary>True for the one case T340 warns about: a crash-election promotion in a game of exactly two humans.</summary>
    public static bool MayHaveSplit(bool election, int humans) => election && humans == TwoHumanPlayers;

    /// <summary>The successor's warning: it won the election alone, so the other player may be running a host of their own.</summary>
    public static HostMigrationNotice MayHaveSplitNotice() => new(
        "The game may have split",
        "You lost contact with the host and continued from your copy of the game. There are only two players, so nobody else could agree to that: the other player may still be playing on their own copy, and there may now be two separate games. Agree with them which copy continues. If this one does, send them an invite: they keep their seat and key. If theirs does, leave and join their game with their invite. Actions taken on the copy that is dropped are lost.");

    /// <summary>The fenced old host's warning: a newer host owns the game, so this copy must not be played any further.</summary>
    public const string FencedTitle = "Another host took over";

    /// <summary>The same sentence is used for the old host's own screen and for a player a superseded host refuses to seat.</summary>
    public const string FencedDetail =
        "The other player could not reach this game and continued from their own copy of it, which is now ahead. This copy is out of date: it cannot continue and it refuses players. Leave this game, then join the other player's game with their invite and the same key; anything done here since the split is lost.";

    /// <summary>The name-free trust notice every player sees before joining (the Welcome carries it; the lobby state names the player). It is <see cref="Unciv.Net.Standby.StandbyNotice.Generic"/>.</summary>
    public static string TrustNotice() => Unciv.Net.Standby.StandbyNotice.Generic;

    /// <summary>T275: the consent prompt shown to the player the host chose as backup host (design/HOST-MIGRATION.md section 1). Nothing is stored or shared until they accept.</summary>
    public static string BackupHostOfferText() =>
        "The host chose you as the backup host. You will receive a complete copy of this game, including what you could not see otherwise, and you will be trusted like the host. If the host goes away, your computer takes the game over. Declining leaves the game without a backup host. Your address reaches the host and the other players.";

    /// <summary>T275: why the host's picker could not set the backup host.</summary>
    public static string BackupHostRefusal(string code) => code switch
    {
        "not-in-lobby" => "The game has already started.",
        "not-a-human-seat" => "That seat is not a player, so it cannot be the backup host.",
        "own-seat" => "The host's own seat is the host, not the backup.",
        _ => "The backup host could not be changed.",
    };

    /// <summary>T275: one sentence for the host screen and lobby showing where the backup-host designation stands (the <see cref="Unciv.Net.Standby.StandbyStates"/> value). Empty for "none".</summary>
    public static string BackupHostStateText(string? state) => state switch
    {
        null or "" or Unciv.Net.Standby.StandbyStates.None => "",
        Unciv.Net.Standby.StandbyStates.Offered => "A backup host has been asked and has not answered yet.",
        Unciv.Net.Standby.StandbyStates.Unreachable => "The backup host accepted, but this machine cannot reach it yet; promotion would not reach the others.",
        Unciv.Net.Standby.StandbyStates.Ready => "The backup host is ready and holds an up-to-date copy of the game.",
        Unciv.Net.Standby.StandbyStates.Syncing => "The backup host is catching up with the latest copy of the game.",
        Unciv.Net.Standby.StandbyStates.Lagging => "The backup host has fallen behind; it is being brought up to date.",
        Unciv.Net.Standby.StandbyStates.Stopped => "The backup host's copy stopped updating; it is trying again.",
        _ => "Backup host: " + state,
    };

    /// <summary>T275: the notice shown when a host change rolled this player back (design/HOST-MIGRATION.md section 1): commands the dead host acknowledged but never replicated are lost.</summary>
    public static HostMigrationNotice RolledBackNotice(long lostCommands) => new(
        "A few actions were lost",
        lostCommands == 1
            ? "The host changed and did not have your last action, so it was rolled back. Everything before it is unchanged."
            : $"The host changed and did not have your last {lostCommands} actions, so they were rolled back. Everything before them is unchanged.");

    /// <summary>T275: the notice shown when this client followed the game to a new host. <paramref name="election"/> is true for a crash election (nobody chose the successor), false for a graceful hand-off (the host quit cleanly).</summary>
    public static HostMigrationNotice HostChangedNotice(bool election) => new(
        "The host changed",
        election
            ? "The host went away and the backup host took over automatically. You kept your seat and your key; anything the old host had not copied to the backup was lost."
            : "The host quit and handed the game to the backup host. You kept your seat and your key, and nothing was lost.");

    /// <summary>T275: the title of the quit warning shown when the host is about to stop without a ready backup host.</summary>
    public const string QuitWarningTitle = "No ready backup host";

    /// <summary>T275: what the host is told when quitting (stopping hosting or leaving) without a backup host that is ready, so a quit or crash leaves the game with nobody to continue it.</summary>
    public const string QuitWarningDetail =
        "No backup host is ready right now, so if this game ends, the others cannot keep playing automatically. They can still resume it from a copy of the game folder, but an automatic hand-over needs a ready backup host. Stop anyway?";
}
