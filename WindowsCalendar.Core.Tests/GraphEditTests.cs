using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>MS365 local edits and recurrence (sync-ms365 second slice, recurring-events MS365 interim).</summary>
public sealed class GraphEditTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly Collection Calendar = new("c1", "a1", CollectionKind.Calendar, "Calendar", "blue", "circle", true, false, "cal1");

    private static Item Event(string json) => GraphEventMapper.Map(JsonDocument.Parse(json).RootElement, "c1", Utc)!;

    private static Item Series(string pattern, string range = """{"type":"noEnd","startDate":"2026-11-10"}""") => Event($$"""
        {"id":"m1","@odata.etag":"W/\"1\"","type":"seriesMaster","subject":"Sync","body":{"contentType":"text","content":"Notes"},
         "recurrence":{"pattern":{{pattern}},"range":{{range}} },
         "start":{"dateTime":"2026-11-10T09:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T10:00:00.0000000","timeZone":"UTC"} }
        """);

    private const string Weekly = """{"type":"weekly","interval":1,"daysOfWeek":["tuesday"],"firstDayOfWeek":"sunday"}""";

    private static JsonObject Json(Item item) => JsonNode.Parse(item.RawPayload)!.AsObject();

    private static readonly DateTimeOffset Nov17 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Edit_OfAStubOccurrence_ComparesWithItsMaster_AndMarksOnlyTimes()
    {
        var master = Series(Weekly);
        var stub = Event("""
            {"id":"o1","type":"occurrence","seriesMasterId":"m1",
             "start":{"dateTime":"2026-11-17T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T10:00:00","timeZone":"UTC"}}
            """);
        var fields = GraphEventMapper.Read(stub, master, Utc);

        Assert.Null(GraphEvents.Edit(stub, master, fields, Utc));
        var moved = GraphEvents.Edit(stub, master, fields with { Start = fields.Start.AddHours(1), End = fields.End.AddHours(1) }, Utc)!;

        var patch = JsonNode.Parse(GraphEvents.PatchBody(moved.RawPayload))!.AsObject();
        Assert.Equal(["end", "isAllDay", "start"], patch.Select(p => p.Key).Order());
        Assert.Equal("2026-11-17T10:00:00", patch["start"]!["dateTime"]!.GetValue<string>());
        Assert.Equal(Nov17.AddHours(1), moved.StartUtc);
    }

    [Fact]
    public void Edit_KeepsTheEventsOwnTimezone()
    {
        var item = Event("""
            {"id":"e1","type":"singleInstance","subject":"Call","originalStartTimeZone":"Singapore Standard Time",
             "start":{"dateTime":"2026-11-17T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T02:00:00","timeZone":"UTC"}}
            """);
        var fields = GraphEventMapper.Read(item, null, Utc);

        var edited = GraphEvents.Edit(item, null, fields with { Start = fields.Start.AddHours(1), End = fields.End.AddHours(1) }, Utc)!;

        var start = Json(edited)["start"]!;
        Assert.Equal("Singapore Standard Time", start["timeZone"]!.GetValue<string>());
        Assert.Equal("2026-11-17T10:00:00", start["dateTime"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero), edited.StartUtc);
    }

    [Theory]
    [InlineData("false", "notResponded", true)]
    [InlineData("false", "none", true)]
    [InlineData("false", "tentativelyAccepted", true)]
    [InlineData("false", "accepted", false)]
    [InlineData("true", "organizer", false)]
    public void Invitation_NotAccepted_IsMarked(string isOrganizer, string response, bool unaccepted)
    {
        var item = Event($$"""
            {"id":"e1","type":"singleInstance","subject":"Review","isOrganizer":{{isOrganizer}},"responseStatus":{"response":"{{response}}"},
             "start":{"dateTime":"2026-11-17T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T02:00:00","timeZone":"UTC"} }
            """);

        Assert.Equal(unaccepted, item.Unaccepted);
    }

    [Fact]
    public void Create_IsInTheWindowsTimezone_AllDayAsDates()
    {
        var item = GraphEvents.Create(Calendar, new EventFields("Holiday", true, new DateTime(2026, 12, 24), new DateTime(2026, 12, 26), null, "Off"), Utc);

        var json = Json(item);
        Assert.Null(item.RemoteId);
        Assert.True(json["isAllDay"]!.GetValue<bool>());
        Assert.Equal("2026-12-26T00:00:00", json["end"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("UTC", json["end"]!["timeZone"]!.GetValue<string>());
        Assert.Equal("Off", json["body"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Restore_SendsTheChangedFieldsBack()
    {
        var item = Event("""
            {"id":"e1","type":"singleInstance","subject":"Call",
             "start":{"dateTime":"2026-11-17T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T02:00:00","timeZone":"UTC"}}
            """);
        var fields = GraphEventMapper.Read(item, null, Utc);
        var moved = GraphEvents.Edit(item, null, fields with { Start = fields.Start.AddDays(1), End = fields.End.AddDays(1) }, Utc)!;

        var restored = GraphEvents.Restore(moved, item.RawPayload, Utc);

        Assert.Equal(item.StartUtc, restored.StartUtc);
        Assert.Contains("\"start\"", GraphEvents.PatchBody(restored.RawPayload));
    }

    [Theory]
    [InlineData(Weekly, """{"type":"noEnd","startDate":"2026-11-10"}""", RepeatFrequency.Weekly, null, null, null)]
    [InlineData("""{"type":"relativeMonthly","interval":1,"daysOfWeek":["tuesday"],"index":"second"}""", """{"type":"numbered","numberOfOccurrences":5,"startDate":"2026-11-10"}""", RepeatFrequency.Monthly, 2, 5, null)]
    [InlineData("""{"type":"absoluteYearly","interval":1,"dayOfMonth":10,"month":11}""", """{"type":"endDate","endDate":"2030-11-10","startDate":"2026-11-10"}""", RepeatFrequency.Yearly, null, null, "2030-11-10")]
    [InlineData("""{"type":"absoluteMonthly","interval":2,"dayOfMonth":10}""", """{"type":"noEnd","startDate":"2026-11-10"}""", RepeatFrequency.Monthly, null, null, null)]
    public void ReadRule_MapsThePanelsPatterns(string pattern, string range, RepeatFrequency frequency, int? week, int? count, string? until)
    {
        var rule = GraphRecurrence.ReadRule(Series(pattern, range), Utc)!;

        Assert.Equal(frequency, rule.Frequency);
        Assert.Equal(week, rule.MonthlyWeek);
        Assert.Equal(count, rule.Count);
        Assert.Equal(until is null ? null : DateOnly.Parse(until), rule.Until);
        // And back again.
        Assert.Equal(rule, GraphRecurrence.ReadRule(Series(GraphRecurrence.Recurrence(rule, new DateOnly(2026, 11, 10), Utc)["pattern"]!.ToJsonString(),
            GraphRecurrence.Recurrence(rule, new DateOnly(2026, 11, 10), Utc)["range"]!.ToJsonString()), Utc));
    }

    [Theory]
    [InlineData("""{"type":"relativeYearly","interval":1,"daysOfWeek":["tuesday"],"index":"second","month":11}""")]
    [InlineData("""{"type":"relativeMonthly","interval":1,"daysOfWeek":["tuesday","thursday"],"index":"second"}""")]
    [InlineData("""{"type":"absoluteMonthly","interval":1,"dayOfMonth":3}""")]
    public void ReadRule_OtherPatterns_AreNull(string pattern) => Assert.Null(GraphRecurrence.ReadRule(Series(pattern), Utc));

    [Fact]
    public void Dates_MonthlyOnThe31st_UsesTheLastDay()
    {
        var rule = new RepeatRule(RepeatFrequency.Monthly, 1, [], null, null, null);

        var dates = GraphRecurrence.Dates(rule, new DateOnly(2027, 1, 31)).Take(3).ToList();

        Assert.Equal([new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31)], dates);
    }

    [Fact]
    public void EditSeries_MovedADayLater_ShiftsTheWeekdayAndTheStart()
    {
        var master = Series(Weekly);
        var fields = GraphRecurrence.Occurrence(master, Nov17, Utc);

        var edited = GraphRecurrence.EditSeries(master, Nov17, fields with { Start = fields.Start.AddDays(1), End = fields.End.AddDays(1) }, Utc)!;

        var json = Json(edited);
        Assert.Equal("wednesday", json["recurrence"]!["pattern"]!["daysOfWeek"]![0]!.GetValue<string>());
        Assert.Equal("2026-11-11", json["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero), edited.StartUtc);
        Assert.Contains("\"recurrence\"", GraphEvents.PatchBody(edited.RawPayload));
    }

    [Fact]
    public void EditSeries_TitleOnly_PatchesOnlyTheSubject()
    {
        var master = Series(Weekly);
        var fields = GraphRecurrence.Occurrence(master, Nov17, Utc);

        var edited = GraphRecurrence.EditSeries(master, Nov17, fields with { Title = "Renamed" }, Utc)!;

        Assert.Equal("""{"subject":"Renamed"}""", GraphEvents.PatchBody(edited.RawPayload));
        Assert.Equal(master.StartUtc, edited.StartUtc);
    }

    [Fact]
    public void EditSeries_SwitchToAllDay_IsRefused()
    {
        var master = Series(Weekly);
        var fields = GraphRecurrence.Occurrence(master, Nov17, Utc);

        Assert.Throws<EditRefusedException>(() => GraphRecurrence.EditSeries(master, Nov17,
            fields with { AllDay = true, Start = fields.Start.Date, End = fields.Start.Date.AddDays(1) }, Utc));
    }

    [Fact]
    public void Split_Numbered_EndsTheOriginalAndKeepsTheOccurrencesLeft()
    {
        var master = Series(Weekly, """{"type":"numbered","numberOfOccurrences":10,"startDate":"2026-11-10"}""");
        var at = Nov17.AddDays(7);

        var (original, following) = GraphRecurrence.SplitSeries(master, at, GraphRecurrence.Occurrence(master, at, Utc), Calendar, Utc)!.Value;

        var range = Json(original)["recurrence"]!["range"]!;
        Assert.Equal("endDate", range["type"]!.GetValue<string>());
        Assert.Equal("2026-11-23", range["endDate"]!.GetValue<string>());
        var next = Json(following)["recurrence"]!["range"]!;
        Assert.Equal(8, next["numberOfOccurrences"]!.GetValue<int>());
        Assert.Equal("2026-11-24", next["startDate"]!.GetValue<string>());
        Assert.Null(following.RemoteId);
        Assert.Equal("Sync", following.Summary);
    }

    [Fact]
    public void Split_OnTheFirstOccurrence_IsNull()
    {
        var master = Series(Weekly);
        var first = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

        Assert.Null(GraphRecurrence.SplitSeries(master, first, GraphRecurrence.Occurrence(master, first, Utc), Calendar, Utc));
        Assert.Null(GraphRecurrence.DeleteFollowing(master, first, Utc));
    }

    [Fact]
    public void ChangeRule_DoesntRepeatForAll_ReplacesTheMaster_CountingExceptions()
    {
        var master = Series(Weekly);
        var exception = Event("""
            {"id":"x1","type":"exception","seriesMasterId":"m1","subject":"Moved",
             "start":{"dateTime":"2026-11-18T09:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-18T10:00:00","timeZone":"UTC"}}
            """);

        var change = GraphRecurrence.ChangeRule(master, Nov17, false, RepeatRule.None, Calendar, [exception], Utc)!;

        Assert.True(change.ReplacesMaster);
        Assert.Equal(1, change.Lost);
        Assert.False(Json(change.Created!).ContainsKey("recurrence"));
        Assert.Equal(master.StartUtc, change.Created!.StartUtc);
    }

    [Fact]
    public void ChangeRule_SingleEventToWeekly_MovesTheStartToTheFirstChosenDay()
    {
        var single = Event("""
            {"id":"e1","@odata.etag":"W/\"1\"","type":"singleInstance","subject":"Gym",
             "start":{"dateTime":"2026-11-10T07:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-10T08:00:00","timeZone":"UTC"}}
            """);
        var rule = new RepeatRule(RepeatFrequency.Weekly, 1, [DayOfWeek.Thursday], null, null, null);

        var change = GraphRecurrence.ChangeRule(single, null, false, rule, Calendar, [], Utc)!;

        Assert.Equal(new DateTimeOffset(2026, 11, 12, 7, 0, 0, TimeSpan.Zero), change.Updated!.StartUtc);
        var patch = JsonNode.Parse(GraphEvents.PatchBody(change.Updated.RawPayload))!.AsObject();
        Assert.Equal("thursday", patch["recurrence"]!["pattern"]!["daysOfWeek"]![0]!.GetValue<string>());
        Assert.Equal("2026-11-12", patch["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
    }

    [Fact]
    public void ChangeRule_Following_SplitsWithTheNewRule()
    {
        var master = Series(Weekly);
        var rule = new RepeatRule(RepeatFrequency.Daily, 1, [], null, 3, null);

        var change = GraphRecurrence.ChangeRule(master, Nov17, true, rule, Calendar, [], Utc)!;

        Assert.Equal("2026-11-16", Json(change.Updated!)["recurrence"]!["range"]!["endDate"]!.GetValue<string>());
        var next = Json(change.Created!)["recurrence"]!;
        Assert.Equal("daily", next["pattern"]!["type"]!.GetValue<string>());
        Assert.Equal(3, next["range"]!["numberOfOccurrences"]!.GetValue<int>());
        Assert.Equal(Nov17, change.Created!.StartUtc);
    }

    [Fact]
    public void Edit_InACustomZone_WritesUtcNotTheWindowsZone()
    {
        // timezone-handling F1: Graph takes no custom zone on a write; the instants are kept exactly.
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
        var item = Event("""
            {"id":"e1","type":"singleInstance","subject":"Call","originalStartTimeZone":"tzone://Microsoft/Custom",
             "originalEndTimeZone":"tzone://Microsoft/Custom",
             "start":{"dateTime":"2026-11-17T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T02:00:00","timeZone":"UTC"}}
            """);
        var fields = GraphEventMapper.Read(item, null, sydney);

        var edited = GraphEvents.Edit(item, null, fields with { Start = fields.Start.AddHours(1), End = fields.End.AddHours(1) }, sydney)!;

        var json = Json(edited);
        Assert.Equal("UTC", json["start"]!["timeZone"]!.GetValue<string>());
        Assert.Equal("2026-11-17T02:00:00", json["start"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("UTC", json["end"]!["timeZone"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero), edited.StartUtc);
    }

    [Fact]
    public void Edit_KeepsASeparateEndZone()
    {
        // timezone-handling F2: leaves Sydney at 12:00 (01:00 UTC), lands in London at 12:00 (GMT).
        var item = Event("""
            {"id":"e1","type":"singleInstance","subject":"Flight","originalStartTimeZone":"AUS Eastern Standard Time",
             "originalEndTimeZone":"GMT Standard Time",
             "start":{"dateTime":"2026-11-17T01:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-17T12:00:00","timeZone":"UTC"}}
            """);
        var fields = GraphEventMapper.Read(item, null, Utc);

        var edited = GraphEvents.Edit(item, null, fields with { Start = fields.Start.AddHours(1), End = fields.End.AddHours(1) }, Utc)!;

        var json = Json(edited);
        Assert.Equal(("2026-11-17T13:00:00", "AUS Eastern Standard Time"),
            (json["start"]!["dateTime"]!.GetValue<string>(), json["start"]!["timeZone"]!.GetValue<string>()));
        Assert.Equal(("2026-11-17T13:00:00", "GMT Standard Time"),
            (json["end"]!["dateTime"]!.GetValue<string>(), json["end"]!["timeZone"]!.GetValue<string>()));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 13, 0, 0, TimeSpan.Zero), edited.EndUtc);
    }

    [Fact]
    public void EditSeries_WeeklyInSydney_IsWrittenInSydneyTime()
    {
        // A weekly 09:00 Sydney series from Mon 30 Mar 2026 (AEDT, 22:00 UTC the day before); its 13 Apr occurrence
        // (AEST, 23:00 UTC) moves an hour later. The server expands the series in its zone, so a write in that zone
        // keeps 10:00 local on both sides of the DST change.
        var master = Event("""
            {"id":"m1","type":"seriesMaster","subject":"Standup","originalStartTimeZone":"AUS Eastern Standard Time",
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday"],"firstDayOfWeek":"sunday"},
               "range":{"type":"noEnd","startDate":"2026-03-30","recurrenceTimeZone":"AUS Eastern Standard Time"}},
             "start":{"dateTime":"2026-03-29T22:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-03-29T23:00:00","timeZone":"UTC"}}
            """);
        var occurrence = new DateTimeOffset(2026, 4, 12, 23, 0, 0, TimeSpan.Zero);
        var fields = GraphRecurrence.Occurrence(master, occurrence, Utc);

        var edited = GraphRecurrence.EditSeries(master, occurrence, fields with { Start = fields.Start.AddHours(1), End = fields.End.AddHours(1) }, Utc)!;

        var json = Json(edited);
        Assert.Equal(("2026-03-30T10:00:00", "AUS Eastern Standard Time"),
            (json["start"]!["dateTime"]!.GetValue<string>(), json["start"]!["timeZone"]!.GetValue<string>()));
        Assert.Equal("2026-03-30T11:00:00", json["end"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("AUS Eastern Standard Time", json["recurrence"]!["range"]!["recurrenceTimeZone"]!.GetValue<string>());
    }

    [Fact]
    public void EditSeries_MovedAcrossDifferentDstDates_LandsWhereDropped()
    {
        // timezone-handling F4: Mondays 09:00 New York from 2 Mar 2026 (14:00 UTC); the Windows zone is London. New York
        // starts DST on 8 Mar, London on 29 Mar. The 9 Mar occurrence (13:00 London) is dropped on Mon 30 Mar 13:00
        // London, which is 08:00 in New York.
        var london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var master = Event("""
            {"id":"m1","type":"seriesMaster","subject":"Call","originalStartTimeZone":"Eastern Standard Time",
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday"],"firstDayOfWeek":"sunday"},
               "range":{"type":"noEnd","startDate":"2026-03-02","recurrenceTimeZone":"Eastern Standard Time"}},
             "start":{"dateTime":"2026-03-02T14:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-03-02T15:00:00","timeZone":"UTC"}}
            """);
        var occurrence = new DateTimeOffset(2026, 3, 9, 13, 0, 0, TimeSpan.Zero);
        var fields = GraphRecurrence.Occurrence(master, occurrence, london);
        Assert.Equal(new DateTime(2026, 3, 9, 13, 0, 0), fields.Start);

        var edited = GraphRecurrence.EditSeries(master, occurrence,
            fields with { Start = new DateTime(2026, 3, 30, 13, 0, 0), End = new DateTime(2026, 3, 30, 14, 0, 0) }, london)!;

        var json = Json(edited);
        Assert.Equal(("2026-03-23T08:00:00", "Eastern Standard Time"),
            (json["start"]!["dateTime"]!.GetValue<string>(), json["start"]!["timeZone"]!.GetValue<string>()));
        Assert.Equal("2026-03-23T09:00:00", json["end"]!["dateTime"]!.GetValue<string>());
        Assert.Equal("2026-03-23", json["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
        Assert.Equal("monday", json["recurrence"]!["pattern"]!["daysOfWeek"]![0]!.GetValue<string>());
    }
}
