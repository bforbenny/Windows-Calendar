using System.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

/// <summary>Subscribed feeds (sync-radicale R1a) and re-discovery edge cases (account-management R11).</summary>
public sealed class FeedSyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-feed-").FullName;
    private readonly LocalStore _store;

    public FeedSyncTests()
    {
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"));
        _store.AddDiscoveredAccount(new Account("a1", AccountType.CalDAV, "Radicale"),
            [new("https://dav.example.test/ben/home/", "Home", CollectionKind.Calendar, false)]);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("webcal://feeds.example.test/a.ics?t=1", "https://feeds.example.test/a.ics?t=1")]
    [InlineData("https://feeds.example.test/a.ics", "https://feeds.example.test/a.ics")]
    [InlineData("http://feeds.example.test/a.ics", null)]
    [InlineData("not a url", null)]
    public void FetchUrl_IsHttpsOnly(string source, string? expected) =>
        Assert.Equal(expected, FeedSync.FetchUrl(source)?.AbsoluteUri);

    [Fact]
    public void Split_GroupsByUidWithTimezonesAndKeepsText()
    {
        var feed = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//x//EN",
            "BEGIN:VTIMEZONE", "TZID:Europe/London", "BEGIN:STANDARD", "DTSTART:19701025T020000", "TZOFFSETFROM:+0100",
            "TZOFFSETTO:+0000", "END:STANDARD", "END:VTIMEZONE",
            "BEGIN:VEVENT", "UID:s1", "DTSTART;TZID=Europe/London:20261019T090000", "RRULE:FREQ=WEEKLY", "X-ODD:keep me",
            "BEGIN:VALARM", "ACTION:DISPLAY", "TRIGGER:-PT5M", "END:VALARM", "END:VEVENT",
            "BEGIN:VEVENT", "UID:o", "DTSTART;VALUE=DATE:20261225", "SUMMARY:Other", "END:VEVENT",
            "BEGIN:VEVENT", "UID:s1", "RECURRENCE-ID;TZID=Europe/London:20261026T090000",
            "DTSTART;TZID=Europe/London:20261026T100000", "END:VEVENT",
            "END:VCALENDAR", "");

        var parts = FeedSync.Split(feed).ToList();

        Assert.Equal(["s1", "o"], parts.Select(p => p.Uid));
        var series = parts[0].Payload;
        Assert.Contains("TZID:Europe/London", series);
        Assert.Contains("X-ODD:keep me", series);
        Assert.Contains("RECURRENCE-ID", series);
        Assert.DoesNotContain("UID:o", series);
        Assert.StartsWith("BEGIN:VCALENDAR", parts[1].Payload);
        Assert.EndsWith("END:VCALENDAR\r\n", parts[1].Payload);
    }

    [Fact]
    public async Task RedirectToHttp_IsRefusedAndNoItemsChange()
    {
        _store.SyncCollections("a1",
        [
            new("https://dav.example.test/ben/home/", "Home", CollectionKind.Calendar, false),
            new("https://dav.example.test/ben/feed/", "Feed", CollectionKind.Calendar, true, SourceUrl: "https://feeds.example.test/a.ics"),
        ]);
        var feed = _store.GetCollections("a1").Single(c => c.SourceUrl is not null);
        var http = new HttpClient(new Redirecting());

        var ex = await Assert.ThrowsAsync<CalDavException>(() =>
            new FeedSync(_store, http).RefreshAsync(feed, force: true, TestContext.Current.CancellationToken));

        Assert.Equal(CalDavError.NotHttps, ex.Error);
    }

    [Fact]
    public void Rediscovery_RenamesAndAdds_EmptyListingRemovesNothing()
    {
        var home = Assert.Single(_store.GetCollections("a1"));

        var changes = _store.SyncCollections("a1",
        [
            new("https://dav.example.test/ben/home/", "Home (renamed)", CollectionKind.Calendar, false),
            new("https://dav.example.test/ben/sub/", "Sub", CollectionKind.Calendar, true, SourceUrl: "https://feeds.example.test/a.ics"),
        ]);
        var empty = _store.SyncCollections("a1", []);

        Assert.Equal(new CollectionChanges(1, 1, 0), changes);
        Assert.Equal(new CollectionChanges(0, 0, 0), empty);
        var list = _store.GetCollections("a1");
        Assert.Equal(["Home (renamed)", "Sub"], list.Select(c => c.Name));
        Assert.Equal(home.Colour, list[0].Colour);
        Assert.NotEqual((list[0].Colour, list[0].Shape), (list[1].Colour, list[1].Shape));
        Assert.Equal("https://feeds.example.test/a.ics", list[1].SourceUrl);
    }

    private sealed class Redirecting : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://feeds.example.test/a.ics") } });
    }
}
