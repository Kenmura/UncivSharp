namespace Unciv.Net.Lobby;

/// <summary>
/// T348: the bounds a lobby enforces on chat and on the host's kick/ban reasons. The same values the in-game <c>HostLimits</c> uses
/// for chat (<c>MaxChatChars</c> 500, <c>MaxChatPerWindow</c> 5, <c>ChatWindowMillis</c> 10 000), so a player's typing and the host's
/// enforcement agree whether the game has started or not. A test swaps the instance to exercise the rate limit without sleeping.
/// </summary>
public sealed class LobbyLimits
{
    public static LobbyLimits Default { get; } = new();

    /// <summary>Longest chat line broadcast (UTF-16 units); longer text is cut, blank lines are dropped.</summary>
    public int MaxChatChars { get; init; } = 500;

    /// <summary>Chat lines one session may send per <see cref="ChatWindowMillis"/>; the rest are dropped silently.</summary>
    public int MaxChatPerWindow { get; init; } = 5;

    /// <summary>The sliding window the chat rate limit counts over.</summary>
    public long ChatWindowMillis { get; init; } = 10_000;

    /// <summary>Longest kick/ban reason the host relays (UTF-16 units); a longer reason is cut and control characters are stripped.</summary>
    public int MaxReasonChars { get; init; } = 200;
}
