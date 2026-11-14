using Unciv.GodotClient.Setup;
using Unciv.Net.Identity;

namespace Unciv.Tests.Godot;

// C006: the join screen's texts and address parsing (Godot-free).
public sealed class JoinModelTests
{
    private static readonly string Fingerprint = HostKey.Fingerprint([1, 2, 3, 4]);

    [Theory]
    [InlineData("192.168.1.20:47900", "192.168.1.20", 47900)]
    [InlineData("  example.org  ", "example.org", 47821)]
    [InlineData("example.org:1", "example.org", 1)]
    [InlineData("example.org:65535", "example.org", 65535)]
    [InlineData("[::1]:5000", "::1", 5000)]
    [InlineData("[fe80::1]", "fe80::1", 47821)]
    [InlineData("fe80::1", "fe80::1", 47821)]
    public void Parse_AcceptsAddresses(string text, string host, int port)
    {
        var parsed = JoinModel.Parse(text);
        Assert.Null(parsed.Problem);
        Assert.Equal((host, port), (parsed.Target!.Host, parsed.Target.Port));
        Assert.Null(parsed.Target.Fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":47821")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:-1")]
    [InlineData("host:abc")]
    [InlineData("host:")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("two words:1")]
    [InlineData("host/path")]
    [InlineData("unciv://nonsense")]
    [InlineData("unciv://host:1/not-a-fingerprint")]
    public void Parse_RefusesWithASentence(string? text)
    {
        var parsed = JoinModel.Parse(text);
        Assert.Null(parsed.Target);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Problem));
    }

    [Fact]
    public void Parse_Invite_CarriesFingerprintAndToken()
    {
        var parsed = JoinModel.Parse(InviteCode.Format("203.0.113.7", 47891, Fingerprint, "tok123"));
        Assert.Null(parsed.Problem);
        var target = parsed.Target!;
        Assert.Equal(("203.0.113.7", 47891, "tok123"), (target.Host, target.Port, target.Token));
        Assert.True(HostKey.Matches(Fingerprint, target.Fingerprint));
        Assert.Equal("203.0.113.7:47891", target.Address);
    }

    [Fact]
    public void Address_IsTheKnownHostsKey_LowerCasedAndBracketedForIpv6()
    {
        Assert.Equal("example.org:47821", new JoinTarget("Example.ORG", 47821, null, null).Address);
        Assert.Equal("[::1]:5", new JoinTarget("::1", 5, null, null).Address);
        Assert.Equal(JoinModel.Parse("EXAMPLE.org").Target!.Address, JoinModel.Parse("example.org:47821").Target!.Address);
    }

    [Theory]
    [InlineData("connect-failed", "Could not reach the host")]
    [InlineData("handshake-timeout", "did not answer in time")]
    [InlineData("host-untrusted", "did not trust")]
    [InlineData("host-key-mismatch", "does not match the invite")]
    [InlineData("host-key-changed", "has changed")]
    [InlineData("protocol-mismatch", "same version")]
    [InlineData("bad-credentials", "Wrong password")]
    [InlineData("ruleset-mismatch", "different rules")]
    [InlineData("notice-declined", "backup host")]
    public void RefusalText_SaysWhatToDo(string code, string fragment) => Assert.Contains(fragment, JoinModel.RefusalText(code), StringComparison.Ordinal);

    [Fact]
    public void RefusalText_UnknownCode_NamesIt() => Assert.Contains("weird-code", JoinModel.RefusalText("weird-code"), StringComparison.Ordinal);

    [Fact]
    public void TrustText_ShowsTheFingerprintAndTheAddress()
    {
        var text = JoinModel.TrustText("host:1", Fingerprint);
        Assert.Contains("host:1", text, StringComparison.Ordinal);
        Assert.Contains(HostKey.Normalize(Fingerprint)!, text, StringComparison.Ordinal);
    }

    [Fact]
    public void RelayPromptText_NamesTheRelayAndWhatItSees()
    {
        var text = JoinModel.RelayPromptText("relay.example.net:47830");
        Assert.Contains("relay.example.net:47830", text, StringComparison.Ordinal);
        Assert.Contains("IP address", text, StringComparison.Ordinal);
        Assert.Contains("does not change your relay setting", text, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyChangedText_OffersNoWayToContinue()
    {
        var text = JoinModel.KeyChangedText("host:1");
        Assert.Contains("changed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("continue anyway", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Connection_HiddenWhileConnectedAndWaitingWhileNot()
    {
        Assert.False(JoinModel.Connection(true, false, null).Visible);
        var lost = JoinModel.Connection(false, false, null);
        Assert.True(lost.Visible);
        Assert.False(lost.Final);
        Assert.Equal("Connection lost", lost.Title);
        var host = JoinModel.Connection(false, true, null);
        Assert.Equal("The host went away", host.Title);
        Assert.Equal("The host went away", JoinModel.Connection(true, true, null).Title);
    }

    [Fact]
    public void Connection_ReconnectFailed_SaysTheSeatIsKept()
    {
        var view = JoinModel.Connection(false, false, "reconnect-failed");
        Assert.Contains("seat is kept", view.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", view.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", JoinModel.RefusalText("reconnect-failed"), StringComparison.Ordinal);
    }

    // T346: the reconnect overlay says how far the automatic retry is and whom the game still waits for, and offers Rejoin.
    [Fact]
    public void Connection_RetryingShowsTheAttemptTheNextTryTheGiveUpAndTheWaitingLine()
    {
        var view = JoinModel.Connection(false, false, null, null, new ReconnectView(3, 5, 4, 90), "Waiting for Bob (offline, 1:58 to reconnect)");
        Assert.True(view.Visible);
        Assert.False(view.Final);
        Assert.True(view.Rejoin);
        Assert.Contains("Attempt 3 of 5", view.Detail, StringComparison.Ordinal);
        Assert.Contains("next try in 4 s", view.Detail, StringComparison.Ordinal);
        Assert.Contains("giving up in 1:30", view.Detail, StringComparison.Ordinal);
        Assert.Contains("Waiting for Bob (offline, 1:58 to reconnect)", view.Detail, StringComparison.Ordinal);
        Assert.Equal("Connection lost", view.Title);
    }

    [Fact]
    public void Connection_ReconnectDetailWithoutNumbersIsStillASentence()
    {
        var text = JoinModel.ReconnectDetail(null);
        Assert.Equal("Trying to reconnect to the host with the same key. Your commands are kept and sent once you are back.", text);
        Assert.DoesNotContain("Attempt", JoinModel.ReconnectDetail(new ReconnectView(0, 0)), StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_GaveUp_IsACleanSentenceAndOffersRejoin()
    {
        var view = JoinModel.Connection(true, false, "gave-up");
        Assert.True(view.Final);
        Assert.True(view.Rejoin);
        Assert.Contains("Could not get back", view.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("gave-up", view.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", view.Detail, StringComparison.Ordinal);
        Assert.Contains("ten minutes", JoinModel.RefusalText("gave-up"), StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_RefusalOffersNoRejoin()
    {
        Assert.False(JoinModel.Connection(true, false, "kicked").Rejoin);
        Assert.False(JoinModel.Connection(true, false, "stale-epoch").Rejoin);
        Assert.False(JoinModel.Connection(true, false, null).Rejoin);
    }

    [Theory]
    [InlineData("connection-lost")]
    [InlineData("session-closed")]
    [InlineData("snapshot-rejected")]
    [InlineData("internal-error")]
    public void RefusalText_ForConnectionCodes_IsASentenceNotTheCode(string code)
    {
        var text = JoinModel.RefusalText(code);
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        Assert.EndsWith(".", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refused the connection", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_LeftIsACleanSentence()
    {
        Assert.Equal("You left the game.", JoinModel.RefusalText("left"));
        Assert.Equal("The game was closed.", JoinModel.RefusalText("disposed"));
    }

    [Theory]
    [InlineData("reconnect-failed")]
    [InlineData("connection-lost")]
    [InlineData("kicked")]
    public void Connection_EndedIsFinalWhateverTheConnectionSays(string reason)
    {
        var view = JoinModel.Connection(true, false, reason);
        Assert.True(view.Visible);
        Assert.True(view.Final);
        Assert.Equal("Disconnected", view.Title);
        Assert.False(string.IsNullOrWhiteSpace(view.Detail));
    }

    [Theory]
    [InlineData("mod-sync-declined")]
    [InlineData("mod-sync-bad-manifest")]
    [InlineData("mod-sync-bad-file")]
    [InlineData("mod-sync-failed")]
    public void RefusalText_ForModSyncCodes_IsASentenceNotTheCode(string code)
    {
        var text = JoinModel.RefusalText(code);
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        Assert.EndsWith(".", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ModSyncText_ListsEachModWithItsStatusAndSize()
    {
        var text = JoinModel.ModSyncText([("Alpha", "Missing", 500), ("Beta", "Different", 3 * 1024), ("Gamma", "Missing", 5 * 1024 * 1024)]);
        Assert.Contains("- Alpha: not installed, 500 B", text, StringComparison.Ordinal);
        Assert.Contains("- Beta: installed, but different (your copy is kept as a backup, with any images or sounds it has), 3 KB", text, StringComparison.Ordinal);
        Assert.Contains("- Gamma: not installed, 5 MB", text, StringComparison.Ordinal);
        Assert.Contains("trust this host", text, StringComparison.Ordinal);
    }

    // T340: the two-human split-brain warning and the fenced old host's recovery path.

    [Fact]
    public void Connection_StaleEpoch_SaysAnotherHostTookOverAndHowToRecover()
    {
        var view = JoinModel.Connection(true, false, "stale-epoch");
        Assert.True(view.Visible);
        Assert.True(view.Final);
        Assert.Equal(HostMigrationModel.FencedTitle, view.Title);
        Assert.Contains("out of date", view.Detail, StringComparison.Ordinal);
        Assert.Contains("join the other player's game", view.Detail, StringComparison.Ordinal);
        var text = JoinModel.RefusalText("stale-epoch");
        Assert.DoesNotContain("stale-epoch", text, StringComparison.Ordinal);
        Assert.Contains(HostMigrationModel.FencedTitle, text, StringComparison.Ordinal);
        Assert.Contains("join the other player's game", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_MigrationWarning_IsPlayableAndCanBePutAside()
    {
        var view = JoinModel.Connection(true, false, null, HostMigrationModel.MayHaveSplitNotice());
        Assert.True(view.Visible);
        Assert.False(view.Final);
        Assert.True(view.Dismissible);
        Assert.Equal("The game may have split", view.Title);
        Assert.Contains("two separate games", view.Detail, StringComparison.Ordinal);
        Assert.Contains("invite", view.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_AMigrationWarningNeverHidesAConnectionState()
    {
        var warning = HostMigrationModel.MayHaveSplitNotice();
        Assert.Equal("Connection lost", JoinModel.Connection(false, false, null, warning).Title);
        Assert.Equal("The host went away", JoinModel.Connection(true, true, null, warning).Title);
        Assert.False(JoinModel.Connection(true, false, null).Dismissible);
    }

    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(true, 3, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 0, false)]
    public void MayHaveSplit_OnlyAnElectionWithExactlyTwoHumans(bool election, int humans, bool expected) =>
        Assert.Equal(expected, HostMigrationModel.MayHaveSplit(election, humans));

    [Fact]
    public void Connection_PropagatesReconnectStateWhenDisconnected()
    {
        var reconnect = new ReconnectView(2, 5, 4, 30);
        var view = JoinModel.Connection(false, false, null, null, reconnect);
        Assert.Equal(reconnect, view.Reconnect);
        Assert.Null(JoinModel.Connection(true, false, null).Reconnect);
    }

    [Fact]
    public void ModDownloadProgressTextFormatsFilesAndBytes()
    {
        Assert.Equal("Downloading TestMod...", JoinModel.ModDownloadProgressText(0, 0, 0, 0, "TestMod"));
        Assert.Equal("Downloading TestMod: 2/4 (15 KB / 2 MB)", JoinModel.ModDownloadProgressText(2, 4, 15 * 1024, 2 * 1024 * 1024, "TestMod"));
        Assert.Equal("Lade TestMod...", JoinModel.ModDownloadProgressText(0, 0, 0, 0, "TestMod", static _ => "Lade"));
    }
}
