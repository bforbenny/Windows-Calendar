using System.Globalization;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

/// <summary>Toolbar sync status and notice bars (ui-states R1, R1a, R2, R3).</summary>
public sealed class SyncHealthTests
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-AU");
    private static readonly DateTime TenFortyTwo = new(2026, 11, 12, 10, 42, 0);

    private static AccountHealth A(string name, SyncOutcome outcome, string? reason = null) => new(name, name, outcome, reason);

    [Fact]
    public void Offline_NoNetwork()
    {
        Assert.True(SyncHealth.IsOffline(false, [A("Work", SyncOutcome.Ok)]));
    }

    [Fact]
    public void Offline_EveryTriedAccountHadNetworkError()
    {
        Assert.True(SyncHealth.IsOffline(true, [A("Work", SyncOutcome.Network), A("Home", SyncOutcome.Network)]));
    }

    [Fact]
    public void NotOffline_WhenOneServerAnswered()
    {
        // A Radicale server on the LAN answers while MS365 cannot be reached.
        Assert.False(SyncHealth.IsOffline(true, [A("Work", SyncOutcome.Network), A("Home", SyncOutcome.Ok)]));
        Assert.False(SyncHealth.IsOffline(true, [A("Work", SyncOutcome.Network), A("Home", SyncOutcome.Failed, "HTTP 500")]));
    }

    [Fact]
    public void Offline_IgnoresAccountsWaitingForSignIn()
    {
        Assert.True(SyncHealth.IsOffline(true, [A("Work", SyncOutcome.SignInNeeded), A("Home", SyncOutcome.Network)]));
        Assert.False(SyncHealth.IsOffline(true, [A("Work", SyncOutcome.SignInNeeded)]));
    }

    [Fact]
    public void NotOffline_BeforeAnySync()
    {
        Assert.False(SyncHealth.IsOffline(true, []));
    }

    [Theory]
    [InlineData(true, true, 3, "Syncing…")]
    [InlineData(false, true, 3, "Offline")]
    [InlineData(false, false, 3, "3 changes waiting")]
    [InlineData(false, false, 1, "1 change waiting")]
    [InlineData(false, false, 0, "Synced 10:42 am")]
    public void StatusText_Precedence(bool syncing, bool offline, int pending, string expected)
    {
        Assert.Equal(expected, SyncHealth.StatusText(syncing, offline, pending, TenFortyTwo, Culture));
    }

    [Fact]
    public void StatusText_EmptyBeforeFirstSync()
    {
        Assert.Equal("", SyncHealth.StatusText(false, false, 0, null, Culture));
    }

    [Fact]
    public void OfflineMessage_WithLastSynced()
    {
        Assert.Equal("Offline. Changes are saved and will sync later. Last synced 10:42 am.", SyncHealth.OfflineMessage(TenFortyTwo, Culture));
        Assert.Equal("Offline. Changes are saved and will sync later.", SyncHealth.OfflineMessage(null, Culture));
    }

    [Fact]
    public void SignInMessage_OneOrMany()
    {
        Assert.Null(SyncHealth.SignInMessage([A("Work", SyncOutcome.Ok)]));
        Assert.Equal("Sign in to Work to resume syncing.", SyncHealth.SignInMessage([A("Work", SyncOutcome.SignInNeeded), A("Home", SyncOutcome.Ok)]));
        Assert.Equal("Sign in to 2 accounts to resume syncing.",
            SyncHealth.SignInMessage([A("Work", SyncOutcome.SignInNeeded), A("Home", SyncOutcome.SignInNeeded)]));
    }

    [Fact]
    public void ErrorMessage_OneOrMany()
    {
        Assert.Null(SyncHealth.ErrorMessage([A("Work", SyncOutcome.Network)], TenFortyTwo, Culture));
        Assert.Equal("Home could not sync: HTTP 500. Last synced 10:42 am.",
            SyncHealth.ErrorMessage([A("Home", SyncOutcome.Failed, "HTTP 500")], TenFortyTwo, Culture));
        Assert.Equal("2 accounts could not sync: Home, Work.",
            SyncHealth.ErrorMessage([A("Home", SyncOutcome.Failed, "HTTP 500"), A("Work", SyncOutcome.Failed, "HTTP 403")], null, Culture));
    }

    [Fact]
    public void FirstSyncText_OneOrMany()
    {
        Assert.Null(SyncHealth.FirstSyncText([]));
        Assert.Equal("Syncing Work for the first time…", SyncHealth.FirstSyncText(["Work"]));
        Assert.Equal("Syncing 2 accounts for the first time…", SyncHealth.FirstSyncText(["Work", "Home"]));
    }
}
