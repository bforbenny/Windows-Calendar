using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Show-as on events, mapped to Graph and CalDAV (event-show-as R1, R3, R6, R7).</summary>
public sealed class ShowAsTests
{
    private static readonly TimeZoneInfo Perth = TimeZoneInfo.FindSystemTimeZoneById("Australia/Perth");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 1, 0, 0, TimeSpan.Zero);
    private static readonly Collection Home = new("c1", "a1", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false,
        "https://dav.example.test/ben/home/");
    private static readonly Collection Ms365 = new("g1", "a2", CollectionKind.Calendar, "Calendar", "red", "circle", true, false, "cal1");

    private static Item CalDav(string extra, string rule = "") =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\nBEGIN:VEVENT\nUID:x1\nDTSTAMP:20260101T000000Z\n"
             + "DTSTART:20261110T010000Z\nDTEND:20261110T020000Z\nSUMMARY:Gym\n" + rule + extra + "END:VEVENT\nEND:VCALENDAR\n")
            .Replace("\n", "\r\n"), Perth)! with { Id = "i1", CollectionId = "c1" };

    private static Item Graph(string showAs) => GraphEventMapper.Map(JsonDocument.Parse($$"""
        {"id":"e1","@odata.etag":"W/\"1\"","type":"singleInstance","subject":"Gym","isOrganizer":true,"showAs":"{{showAs}}",
         "start":{"dateTime":"2026-11-10T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T02:00:00","timeZone":"UTC"} }
        """).RootElement, "g1", Perth)!;

    private static JsonObject Json(Item item) => JsonNode.Parse(item.RawPayload)!.AsObject();

    [Theory]
    [InlineData("", ShowAs.Busy, "busy")]
    [InlineData("TRANSP:OPAQUE\n", ShowAs.Busy, "busy")]
    [InlineData("TRANSP:TRANSPARENT\n", ShowAs.Free, "free")]
    [InlineData("TRANSP:OPAQUE\nX-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE\n", ShowAs.Tentative, "tentative")]
    [InlineData("TRANSP:OPAQUE\nX-MICROSOFT-CDO-BUSYSTATUS:OOF\n", ShowAs.OutOfOffice, "oof")]
    public void CalDav_Read_UsesBusyStatusThenTransp(string extra, ShowAs expected, string column)
    {
        var item = CalDav(extra);

        Assert.Equal(expected, CalDavEvents.Read(item, Perth).ShowAs);
        Assert.Equal(column, item.Status);
    }

    [Fact]
    public void CalDav_SetFree_WritesTranspOnly()
    {
        var item = CalDav("");

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Perth) with { ShowAs = ShowAs.Free }, Perth, Now)!;

        Assert.Contains("TRANSP:TRANSPARENT", edited.RawPayload);
        Assert.DoesNotContain("BUSYSTATUS", edited.RawPayload);
        Assert.Equal("free", edited.Status);
    }

    [Fact]
    public void CalDav_ChangeFromTentative_UpdatesBusyStatusToMatch()
    {
        var item = CalDav("TRANSP:OPAQUE\nX-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE\n");

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Perth) with { ShowAs = ShowAs.Free }, Perth, Now)!;

        Assert.Contains("TRANSP:TRANSPARENT", edited.RawPayload);
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:FREE", edited.RawPayload);
        Assert.DoesNotContain("TENTATIVE", edited.RawPayload);
    }

    [Fact]
    public void CalDav_OtherEdits_KeepShowAsUnchanged()
    {
        var item = CalDav("TRANSP:OPAQUE\nX-MICROSOFT-CDO-BUSYSTATUS:OOF\n");
        var fields = CalDavEvents.Read(item, Perth);

        Assert.Null(CalDavEvents.Edit(item, fields, Perth, Now));
        var edited = CalDavEvents.Edit(item, fields with { Title = "Leave" }, Perth, Now)!;
        Assert.Contains("X-MICROSOFT-CDO-BUSYSTATUS:OOF", edited.RawPayload);
        Assert.Equal(ShowAs.OutOfOffice, CalDavEvents.Read(edited, Perth).ShowAs);
    }

    [Fact]
    public void CalDav_Create_WritesShowAs()
    {
        var fields = new EventFields("Holiday", true, new DateTime(2026, 11, 10), new DateTime(2026, 11, 11), null, null, ShowAs.Free);

        var created = CalDavEvents.Create(Home, fields, Perth, Now);

        Assert.Contains("TRANSP:TRANSPARENT", created.RawPayload);
        Assert.Equal(ShowAs.Free, CalDavEvents.Read(created, Perth).ShowAs);
    }

    [Fact]
    public void CalDav_ShowAsOnlyChange_OfAnOccurrence_IsAChange()
    {
        var item = CalDav("", "RRULE:FREQ=DAILY;COUNT=3\n");
        var occurrence = new DateTimeOffset(2026, 11, 11, 1, 0, 0, TimeSpan.Zero);
        var fields = CalDavRecurrence.ReadOccurrence(item, occurrence, Perth);

        Assert.True(CalDavRecurrence.Changed(fields, fields with { ShowAs = ShowAs.Free }));
        var edited = CalDavRecurrence.EditOccurrence(item, occurrence, fields with { ShowAs = ShowAs.Free }, Perth, Now)!;
        Assert.Equal(ShowAs.Free, CalDavRecurrence.ReadOccurrence(edited, occurrence, Perth).ShowAs);
        Assert.Equal(ShowAs.Busy, CalDavRecurrence.ReadOccurrence(edited, occurrence.AddDays(1), Perth).ShowAs);
    }

    [Fact]
    public void Graph_Edit_SendsOnlyShowAs()
    {
        var item = Graph("busy");

        var edited = GraphEvents.Edit(item, null, GraphEventMapper.Read(item, null, Perth) with { ShowAs = ShowAs.OutOfOffice }, Perth)!;

        Assert.Equal("oof", edited.Status);
        Assert.Equal("""{"showAs":"oof"}""", GraphEvents.PatchBody(edited.RawPayload));
    }

    [Fact]
    public void Graph_Unknown_ReadsAsBusy_AndIsKeptUnlessChanged()
    {
        var item = Graph("unknown");
        var fields = GraphEventMapper.Read(item, null, Perth);

        Assert.Equal(ShowAs.Busy, fields.ShowAs);
        Assert.Null(GraphEvents.Edit(item, null, fields, Perth));
        var edited = GraphEvents.Edit(item, null, fields with { Title = "Swim" }, Perth)!;
        Assert.Equal("unknown", Json(edited)["showAs"]!.GetValue<string>());
    }

    [Fact]
    public void Graph_Create_WritesShowAs()
    {
        var fields = new EventFields("Holiday", true, new DateTime(2026, 11, 10), new DateTime(2026, 11, 11), null, null, ShowAs.Free);

        var created = GraphEvents.Create(Ms365, fields, Perth);

        Assert.Equal("free", Json(created)["showAs"]!.GetValue<string>());
    }

    [Fact]
    public void Store_OnUpgrade_RemapsCalDavStatusToShowAs()
    {
        var dir = Directory.CreateTempSubdirectory("wincal-showas-").FullName;
        var path = Path.Combine(dir, "store.db");
        try
        {
            using (var store = LocalStore.Open(path))
            {
                store.AddAccount(new Account("a1", AccountType.CalDAV, "Radicale"));
                store.AddCollection(Home);
                // As stored before v11: the iCalendar STATUS in the status column.
                store.CreateItem(CalDav("TRANSP:TRANSPARENT\nSTATUS:CONFIRMED\n") with { Status = "CONFIRMED" });
            }
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "INSERT INTO meta (key, value) VALUES ('remap-show-as', '1');";
                cmd.ExecuteNonQuery();
            }
            using (var store = LocalStore.Open(path))
            {
                Assert.Equal("free", store.GetItem("i1")!.Status);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
