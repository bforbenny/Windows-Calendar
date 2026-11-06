using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>The source shown after a calendar's name in the editors (calendar-views interim).</summary>
public sealed class CollectionSourceTests
{
    [Fact]
    public void CalDav_ShowsTheServerHost() =>
        Assert.Equal("dav-1.bforbenny.com",
            CollectionSource.For(new Account("a1", AccountType.CalDAV, "Radicale", ServerUrl: "https://dav-1.bforbenny.com/ben/")));

    [Fact]
    public void Ms365_ShowsTheEmail_ThenTheUsername() =>
        Assert.Equal(["ben@example.com", "ben@corp.example.com"],
            [
                CollectionSource.For(new Account("a1", AccountType.MS365, "Work", Username: "ben@corp.example.com", Email: "ben@example.com")),
                CollectionSource.For(new Account("a2", AccountType.MS365, "Work", Username: "ben@corp.example.com")),
            ]);

    [Fact]
    public void MissingDetails_FallBackToTheAccountName() =>
        Assert.Equal("Radicale", CollectionSource.For(new Account("a1", AccountType.CalDAV, "Radicale")));
}
