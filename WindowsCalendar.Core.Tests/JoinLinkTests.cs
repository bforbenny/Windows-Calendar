using System.Text.Json;
using Microsoft.Data.Sqlite;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Online-meeting join links (online-meeting-join R1-R3, R6a, R7).</summary>
public sealed class JoinLinkTests
{
    private static readonly TimeZoneInfo Perth = TimeZoneInfo.FindSystemTimeZoneById("Australia/Perth");
    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false,
        "https://dav.example.test/ben/home/");

    private static Item CalDav(string extra) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\nBEGIN:VEVENT\nUID:x1\nDTSTAMP:20260101T000000Z\n"
             + "DTSTART:20261110T010000Z\nDTEND:20261110T020000Z\nSUMMARY:Sync\n" + extra + "END:VEVENT\nEND:VCALENDAR\n")
            .Replace("\n", "\r\n"), Perth)! with { Id = "i1", CollectionId = "c1" };

    private static Item Graph(string extra) => GraphEventMapper.Map(JsonDocument.Parse($$"""
        {"id":"e1","subject":"Sync",{{extra}}
         "start":{"dateTime":"2026-11-10T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T02:00:00","timeZone":"UTC"} }
        """).RootElement, "g1", Perth)!;

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0", "Teams")]
    [InlineData("https://teams.live.com/meet/123", "Teams")]
    [InlineData("https://zoom.us/j/123", "Zoom")]
    [InlineData("https://acme.zoom.us/j/123?pwd=abc", "Zoom")]
    [InlineData("https://meet.google.com/abc-defg-hij", "Google Meet")]
    [InlineData("https://acme.webex.com/meet/ben", "Webex")]
    public void Recognise_KnownProvidersOverHttps(string url, string provider) =>
        Assert.Equal(provider, JoinLink.Recognise(url)?.Provider);

    [Theory]
    [InlineData("http://zoom.us/j/123")]
    [InlineData("https://example.com/zoom.us")]
    [InlineData("https://notzoom.us/j/1")]
    [InlineData("https://zoom.us.evil.test/j/1")]
    [InlineData("ftp://meet.google.com/x")]
    [InlineData("not a url")]
    public void Recognise_RefusesOtherLinks(string url) => Assert.Null(JoinLink.Recognise(url));

    [Fact]
    public void FirstIn_FindsTheFirstRecognisedLink_AndTrimsPunctuation()
    {
        var link = JoinLink.FirstIn("Agenda at https://example.com/a. Join (https://acme.zoom.us/j/9).");

        Assert.Equal("https://acme.zoom.us/j/9", link?.Url);
    }

    [Fact]
    public void FirstIn_ReadsHtmlBodies()
    {
        var link = JoinLink.FirstIn("""<a href="https://meet.google.com/abc-defg-hij?authuser=0&amp;hs=1">Join</a>""");

        Assert.Equal("https://meet.google.com/abc-defg-hij?authuser=0&hs=1", link?.Url);
    }

    [Fact]
    public void Graph_OnlineMeetingComesFirst()
    {
        var item = Graph("""
            "isOnlineMeeting":true,"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/a"},
            "location":{"displayName":"https://zoom.us/j/1"},"body":{"contentType":"text","content":"https://meet.google.com/x"},
            """);

        Assert.Equal("https://teams.microsoft.com/l/meetup-join/a", item.JoinUrl);
    }

    [Fact]
    public void Graph_FallsBackToLocationThenBody()
    {
        Assert.Equal("https://zoom.us/j/1", Graph("""
            "isOnlineMeeting":false,"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/x"},
            "location":{"displayName":"Zoom https://zoom.us/j/1"},"body":{"contentType":"text","content":"https://meet.google.com/x"},
            """).JoinUrl);
        Assert.Equal("https://meet.google.com/x", Graph("""
            "body":{"contentType":"html","content":"<p>Join: https://meet.google.com/x</p>"},
            """).JoinUrl);
        Assert.Null(Graph("""
            "body":{"contentType":"text","content":"http://zoom.us/j/1"},
            """).JoinUrl);
    }

    [Fact]
    public void Graph_DeclinedMeeting_HasNoJoin()
    {
        var item = Graph("""
            "isOrganizer":false,"responseStatus":{"response":"declined"},
            "isOnlineMeeting":true,"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/x"},
            """);

        Assert.Null(item.JoinUrl);
    }

    [Fact]
    public void CalDav_UrlThenConferencePropertiesThenText()
    {
        Assert.Equal("https://zoom.us/j/1", CalDav("URL:https://zoom.us/j/1\nLOCATION:https://meet.google.com/x\n").JoinUrl);
        Assert.Equal("https://meet.google.com/abc", CalDav("URL:https://example.com/agenda\nX-GOOGLE-CONFERENCE:https://meet.google.com/abc\n").JoinUrl);
        Assert.Equal("https://acme.webex.com/m/1", CalDav("LOCATION:Room 1\nDESCRIPTION:Dial in or https://acme.webex.com/m/1\n").JoinUrl);
        Assert.Null(CalDav("URL:https://example.com/agenda\n").JoinUrl);
    }

    [Fact]
    public void CalDav_LocationEdit_UpdatesTheLink()
    {
        var item = CalDav("");
        var now = new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Perth) with { Location = "https://zoom.us/j/77" }, Perth, now)!;

        Assert.Null(item.JoinUrl);
        Assert.Equal("https://zoom.us/j/77", edited.JoinUrl);
    }

    [Fact]
    public void CalDav_Duplicate_DropsJoinLinkProperties_KeepsOtherUrls()
    {
        var now = new DateTimeOffset(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);
        var meeting = CalDav("URL:https://zoom.us/j/1\nX-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/x\n");
        var page = CalDav("URL:https://example.com/agenda\n");

        var copy = CalDavEvents.Duplicate(meeting, null, Home, Perth, now);

        Assert.Null(copy.JoinUrl);
        Assert.DoesNotContain("zoom.us", copy.RawPayload);
        Assert.DoesNotContain("SKYPETEAMS", copy.RawPayload);
        Assert.Contains("https://example.com/agenda", CalDavEvents.Duplicate(page, null, Home, Perth, now).RawPayload);
    }

    [Fact]
    public void Store_KeepsTheLink_AndRemapsOnUpgrade()
    {
        var dir = Directory.CreateTempSubdirectory("wincal-join-").FullName;
        var path = Path.Combine(dir, "store.db");
        try
        {
            using (var store = LocalStore.Open(path))
            {
                store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
                store.AddCollection(Home);
                var item = CalDav("URL:https://zoom.us/j/1\n");
                store.CreateItem(item);
                Assert.Equal("https://zoom.us/j/1", store.GetItem("i1")!.JoinUrl);
                // As stored before v12: no link.
                store.UpdateItem(item with { JoinUrl = null });
            }
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "INSERT INTO meta (key, value) VALUES ('remap-join-url', '1');";
                cmd.ExecuteNonQuery();
            }
            using (var store = LocalStore.Open(path))
            {
                Assert.Equal("https://zoom.us/j/1", store.GetItem("i1")!.JoinUrl);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
