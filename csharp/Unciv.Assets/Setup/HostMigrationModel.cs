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
}
