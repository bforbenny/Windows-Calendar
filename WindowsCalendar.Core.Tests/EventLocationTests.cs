using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Location text, its mapping and the preservation of server location data (event-location R3-R5).</summary>
public sealed class EventLocationTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 1, 0, 0, TimeSpan.Zero);

    private static Item CalDav(string body) =>
        CalDavItemMapper.Map("https://dav.example.test/ben/home/x.ics", "\"e1\"",
            ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Apple Inc.//macOS//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"), Utc)!
        with { Id = "i1", CollectionId = "c1" };

    private const string Located = """
        BEGIN:VEVENT
        UID:x1
        DTSTAMP:20260101T000000Z
        DTSTART:20261110T090000Z
        DTEND:20261110T100000Z
        SUMMARY:Planning
        LOCATION;ALTREP="https://example.test/room1":Room 1
        GEO:51.5;-0.12
        X-APPLE-STRUCTURED-LOCATION;VALUE=URI;X-TITLE=Room 1:geo:51.5,-0.12
        END:VEVENT

        """;

    private static Item Graph(string location) => GraphEventMapper.Map(JsonDocument.Parse($$"""
        {"id":"e1","type":"singleInstance","subject":"Visit","location":{{location}},
         "start":{"dateTime":"2026-11-17T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T02:00:00","timeZone":"UTC"} }
        """).RootElement, "c1", Utc)!;

    private static JsonObject Patch(Item item) => JsonNode.Parse(GraphEvents.PatchBody(item.RawPayload))!.AsObject();

    [Fact]
    public void CalDav_TitleEdit_KeepsLocationParametersAndStructuredLocation()
    {
        var item = CalDav(Located);

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Utc) with { Title = "Planning v2" }, Utc, Now)!;

        Assert.Contains("ALTREP=", edited.RawPayload);
        Assert.Contains("GEO:", edited.RawPayload);
        Assert.Contains("X-TITLE=Room 1:geo:51.5,-0.12\r\n", edited.RawPayload);
    }

    [Fact]
    public void CalDav_UriValues_AreWrittenBackUnchanged()
    {
        var item = CalDav(Located.Replace("SUMMARY:Planning", "SUMMARY:Planning\nX-EXAMPLE-LINK;VALUE=URI:https://example.test/a%20b,c;d"));

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Utc) with { Title = "Planning v2" }, Utc, Now)!;

        Assert.Contains("X-EXAMPLE-LINK;VALUE=URI:https://example.test/a%20b,c;d\r\n", edited.RawPayload);
    }

    [Fact]
    public void CalDav_LocationEdit_DropsGeoAndStructuredLocation()
    {
        var item = CalDav(Located);

        var edited = CalDavEvents.Edit(item, CalDavEvents.Read(item, Utc) with { Location = "Room 2" }, Utc, Now)!;

        Assert.Contains("LOCATION:Room 2", edited.RawPayload);
        Assert.DoesNotContain("GEO:", edited.RawPayload);
        Assert.DoesNotContain("X-APPLE-STRUCTURED-LOCATION", edited.RawPayload);
        Assert.Equal("Room 2", edited.Location);
    }

    [Fact]
    public void CalDav_SeriesLocationEdit_DropsGeoFromMaster()
    {
        var item = CalDav(Located.Replace("SUMMARY:Planning", "SUMMARY:Planning\nRRULE:FREQ=WEEKLY"));
        var recurrence = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        var fields = CalDavRecurrence.ReadOccurrence(item, recurrence, Utc);

        var edited = CalDavRecurrence.EditSeries(item, recurrence, fields with { Location = "Room 2" }, Utc, Now)!;

        Assert.Contains("LOCATION:Room 2", edited.RawPayload);
        Assert.DoesNotContain("GEO:", edited.RawPayload);
        Assert.DoesNotContain("X-APPLE-STRUCTURED-LOCATION", edited.RawPayload);
    }

    [Fact]
    public void Graph_AddressWithoutName_IsShownAsFormattedAddress()
    {
        var item = Graph("""
            {"displayName":"","address":{"street":"12 Main St","city":"Sydney","state":"NSW","postalCode":"2000","countryOrRegion":"Australia"}}
            """);

        Assert.Equal("12 Main St, Sydney NSW 2000, Australia", item.Location);
        Assert.Equal("12 Main St, Sydney NSW 2000, Australia", GraphEventMapper.Read(item, null, Utc).Location);
    }

    [Fact]
    public void Graph_EmptyAddress_GivesNoLocation()
    {
        Assert.Null(Graph("""{"displayName":"","address":{"street":"","city":""},"coordinates":{}}""").Location);
    }

    [Fact]
    public void Graph_TitleEdit_LeavesLocationAlone()
    {
        var item = Graph("""
            {"displayName":"","address":{"street":"12 Main St","city":"Sydney"},"coordinates":{"latitude":-33.87,"longitude":151.21}}
            """);

        var edited = GraphEvents.Edit(item, null, GraphEventMapper.Read(item, null, Utc) with { Title = "Visit v2" }, Utc)!;

        Assert.Equal(["subject"], Patch(edited).Select(p => p.Key));
        Assert.Equal(-33.87, JsonNode.Parse(edited.RawPayload)!["location"]!["coordinates"]!["latitude"]!.GetValue<double>());
    }

    [Fact]
    public void Graph_LocationEdit_ClearsAddressAndCoordinates()
    {
        var item = Graph("""
            {"displayName":"Office","address":{"street":"12 Main St"},"coordinates":{"latitude":-33.87,"longitude":151.21}}
            """);

        var edited = GraphEvents.Edit(item, null, GraphEventMapper.Read(item, null, Utc) with { Location = "Cafe Rio" }, Utc)!;

        var location = Patch(edited)["location"]!.AsObject();
        Assert.Equal("Cafe Rio", location["displayName"]!.GetValue<string>());
        Assert.Empty(location["address"]!.AsObject());
        Assert.Empty(location["coordinates"]!.AsObject());
        Assert.Equal("Cafe Rio", edited.Location);
    }
}
