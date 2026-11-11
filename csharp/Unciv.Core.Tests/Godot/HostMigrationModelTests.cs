using Unciv.GodotClient.Setup;
using Unciv.Net.Standby;

namespace Unciv.Tests.Godot;

// T275: the host-migration UI texts (Godot-free).
public sealed class HostMigrationModelTests
{
    [Fact]
    public void BackupHostStateText_CoversEveryStandbyState()
    {
        Assert.Equal("", HostMigrationModel.BackupHostStateText(StandbyStates.None));
        Assert.Equal("", HostMigrationModel.BackupHostStateText(null));
        Assert.Equal("", HostMigrationModel.BackupHostStateText(""));
        Assert.Contains("not answered", HostMigrationModel.BackupHostStateText(StandbyStates.Offered));
        Assert.Contains("cannot reach", HostMigrationModel.BackupHostStateText(StandbyStates.Unreachable));
        Assert.Contains("ready", HostMigrationModel.BackupHostStateText(StandbyStates.Ready));
        Assert.Contains("catching up", HostMigrationModel.BackupHostStateText(StandbyStates.Syncing));
        Assert.Contains("fallen behind", HostMigrationModel.BackupHostStateText(StandbyStates.Lagging));
        Assert.Contains("stopped updating", HostMigrationModel.BackupHostStateText(StandbyStates.Stopped));
        Assert.Contains("mystery", HostMigrationModel.BackupHostStateText("mystery"));
    }

    [Fact]
    public void RolledBackNotice_SaysHowManyWereLost()
    {
        Assert.Contains("last action", HostMigrationModel.RolledBackNotice(1).Detail);
        Assert.Contains("last 3 actions", HostMigrationModel.RolledBackNotice(3).Detail);
    }

    [Fact]
    public void HostChangedNotice_DistinguishesAnElectionFromAGracefulHandoff()
    {
        Assert.Contains("backup host took over", HostMigrationModel.HostChangedNotice(election: true).Detail);
        Assert.Contains("nothing was lost", HostMigrationModel.HostChangedNotice(election: false).Detail);
    }

    [Fact]
    public void TrustNotice_IsTheNameFreeGenericNotice()
    {
        Assert.Equal(StandbyNotice.Generic, HostMigrationModel.TrustNotice());
    }

    [Fact]
    public void BackupHostOfferText_TellsThePlayerWhatConsentMeans()
    {
        Assert.Contains("complete copy", HostMigrationModel.BackupHostOfferText(), StringComparison.Ordinal);
        Assert.Contains("trusted like the host", HostMigrationModel.BackupHostOfferText(), StringComparison.Ordinal);
    }

    [Fact]
    public void BackupHostRefusal_ExplainsEveryRefusal()
    {
        Assert.Contains("already started", HostMigrationModel.BackupHostRefusal("not-in-lobby"));
        Assert.Contains("host", HostMigrationModel.BackupHostRefusal("own-seat"));
        Assert.Contains("player", HostMigrationModel.BackupHostRefusal("not-a-human-seat"));
        Assert.Contains("could not be changed", HostMigrationModel.BackupHostRefusal("anything-else"));
    }

    [Fact]
    public void QuitWarning_NamesTheMissingBackupHost()
    {
        Assert.Contains("No ready backup host", HostMigrationModel.QuitWarningTitle);
        Assert.Contains("cannot keep playing automatically", HostMigrationModel.QuitWarningDetail, StringComparison.Ordinal);
    }
}
