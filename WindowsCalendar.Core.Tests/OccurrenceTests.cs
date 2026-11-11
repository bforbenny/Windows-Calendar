using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

public sealed class OccurrenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddMonths(-6);
    private static readonly DateTimeOffset To = Now.AddMonths(18);

    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-occ-").FullName;
    private readonly FakeTimeProvider _time = new(Now);
    private readonly LocalStore _store;

    public OccurrenceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"), _time);
        _store.AddAccount(new Account("rad", AccountType.CalDAV, "Radicale"));
        _store.AddCollection(new Collection("cal", "rad", CollectionKind.Calendar, "Home", "Navy", "Circle", true, false, "https://example.test/cal/"));
        _store.AddAccount(new Account("m365", AccountType.MS365, "Work"));
        _store.AddCollection(new Collection("work", "m365", CollectionKind.Calendar, "Calendar", "Navy", "Circle", true, false, "AAMk"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\n"
        + string.Concat(events.Select(e => "BEGIN:VEVENT\r\nDTSTAMP:20260101T000000Z\r\n" + e.Replace("\n", "\r\n") + "END:VEVENT\r\n"))
        + "END:VCALENDAR\r\n";

    private static DateTimeOffset Utc(int month, int day, int hour) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    private IEnumerable<(DateTimeOffset, DateTimeOffset)> Occurrences(string itemId) =>
        _store.GetOccurrences(From, To).Where(o => o.ItemId == itemId).Select(o => (o.StartUtc, o.EndUtc));

    [Fact]
    public void CalDavSeries_AppliesOverridesExdatesAndCancellationsAcrossDst()
    {
        // Weekly from Mon 19 Oct (BST); UK clocks go back on 25 Oct.
        var payload = Ics(
            """
            UID:s1
            DTSTART;TZID=Europe/London:20261019T090000
            DTEND;TZID=Europe/London:20261019T100000
            RRULE:FREQ=WEEKLY;COUNT=5
            EXDATE;TZID=Europe/London:20261102T090000
            SUMMARY:Standup

            """,
            """
            UID:s1
            RECURRENCE-ID;TZID=Europe/London:20261026T090000
            DTSTART;TZID=Europe/London:20261026T140000
            DTEND;TZID=Europe/London:20261026T150000
            SUMMARY:Standup moved

            """,
            """
            UID:s1
            RECURRENCE-ID;TZID=Europe/London:20261116T090000
            DTSTART;TZID=Europe/London:20261116T090000
            DTEND;TZID=Europe/London:20261116T100000
            STATUS:CANCELLED
            SUMMARY:Standup

            """);
        _store.CreateItem(new Item("s1", "cal", ItemKind.Event, null, payload, IsRecurring: true));

        Assert.Equal(
            [(Utc(10, 19, 8), Utc(10, 19, 9)), (Utc(10, 26, 14), Utc(10, 26, 15)), (Utc(11, 9, 9), Utc(11, 9, 10))],
            Occurrences("s1"));
    }

    [Fact]
    public void RecurringOccurrence_KeepsItsOriginalStart()
    {
        // Weekly Tuesdays 09:00 UTC; the 17 Nov occurrence moved to 11:00. One-off events get no recurrence id.
        _store.CreateItem(new Item("r1", "cal", ItemKind.Event, null, Ics(
            "UID:r1\nDTSTART:20261110T090000Z\nDURATION:PT1H\nRRULE:FREQ=WEEKLY;COUNT=3\n",
            "UID:r1\nRECURRENCE-ID:20261117T090000Z\nDTSTART:20261117T110000Z\nDURATION:PT1H\n"), IsRecurring: true));
        _store.CreateItem(new Item("g1", "work", ItemKind.Event, "AAMkG1", "{}", StartUtc: Utc(11, 6, 9), EndUtc: Utc(11, 6, 10)));

        var views = _store.GetOccurrenceViews(From, To);

        Assert.Equal(
            [(Utc(11, 10, 9), Utc(11, 10, 9)), (Utc(11, 17, 11), Utc(11, 17, 9)), (Utc(11, 24, 9), Utc(11, 24, 9))],
            views.Where(v => v.ItemId == "r1").Select(v => (v.StartUtc, v.RecurrenceUtc!.Value)));
        Assert.Null(views.Single(v => v.ItemId == "g1").RecurrenceUtc);
    }

    [Fact]
    public void FloatingTime_FollowsWindowsTimezoneChange()
    {
        _store.CreateItem(new Item("f1", "cal", ItemKind.Event, null, Ics("UID:f1\nDTSTART:20261110T090000\nDURATION:PT1H\n\n")));
        Assert.Equal([(Utc(11, 10, 9), Utc(11, 10, 10))], Occurrences("f1"));

        _store.RefreshOccurrences();
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));

        Assert.True(_store.RefreshOccurrences());
        Assert.Equal([(Utc(11, 10, 0), Utc(11, 10, 1))], Occurrences("f1"));
        Assert.False(_store.RefreshOccurrences());
    }

    [Fact]
    public void AllDayEvent_SpansLocalDay()
    {
        _store.CreateItem(new Item("d1", "cal", ItemKind.Event, null, Ics("UID:d1\nDTSTART;VALUE=DATE:20260820\n\n"), AllDay: true));

        // 20 August in London is BST: local midnight is 23:00 UTC the day before.
        Assert.Equal([(Utc(8, 19, 23), Utc(8, 20, 23))], Occurrences("d1"));
    }

    [Fact]
    public void Ms365Item_UsesItsOwnTimes()
    {
        _store.CreateItem(new Item("g1", "work", ItemKind.Event, "AAMkG1", "{}", StartUtc: Utc(11, 6, 9), EndUtc: Utc(11, 6, 10)));

        Assert.Equal([(Utc(11, 6, 9), Utc(11, 6, 10))], Occurrences("g1"));
    }

    [Fact]
    public void ItemOutsideWindow_IsNotIndexed()
    {
        _store.CreateItem(new Item("old", "work", ItemKind.Event, "AAMkOld", "{}", StartUtc: Utc(1, 5, 9), EndUtc: Utc(1, 5, 10)));

        Assert.Empty(Occurrences("old"));
    }

    [Fact]
    public void UnreadableCalDavPayload_FallsBackToColumns()
    {
        _store.CreateItem(new Item("x1", "cal", ItemKind.Event, null, "not ical", StartUtc: Utc(11, 7, 9), EndUtc: Utc(11, 7, 10)));

        Assert.Equal([(Utc(11, 7, 9), Utc(11, 7, 10))], Occurrences("x1"));
    }

    [Fact]
    public void UpdateAndDelete_KeepIndexInStep()
    {
        _store.CreateItem(new Item("g1", "work", ItemKind.Event, "AAMkG1", "{}", StartUtc: Utc(11, 6, 9), EndUtc: Utc(11, 6, 10)));
        _store.UpdateItem(new Item("g1", "work", ItemKind.Event, "AAMkG1", "{}", StartUtc: Utc(11, 8, 9), EndUtc: Utc(11, 8, 10)));
        Assert.Equal([(Utc(11, 8, 9), Utc(11, 8, 10))], Occurrences("g1"));

        _store.DeleteItem("g1");
        Assert.Empty(Occurrences("g1"));
    }

    [Fact]
    public void NewDay_RebuildsIndex()
    {
        Assert.True(_store.RefreshOccurrences());
        Assert.False(_store.RefreshOccurrences());

        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(_store.RefreshOccurrences());
    }

    // A zone no system knows, with its own rules: +03:00, and +04:00 from the 2nd Sunday of April to the 1st Sunday
    // of October.
    private const string CustomZone = """
        BEGIN:VTIMEZONE
        TZID:My Custom Zone
        BEGIN:STANDARD
        DTSTART:19701004T030000
        TZOFFSETFROM:+0400
        TZOFFSETTO:+0300
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=1SU
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:19700412T020000
        TZOFFSETFROM:+0300
        TZOFFSETTO:+0400
        RRULE:FREQ=YEARLY;BYMONTH=4;BYDAY=2SU
        END:DAYLIGHT
        END:VTIMEZONE

        """;

    private static List<DateTimeOffset> Starts(string body, int year, TimeZoneInfo zone) =>
        OccurrenceExpander.Expand(CalDav.CustomTimeZones.Load(
                ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//test//EN\n" + body + "END:VCALENDAR\n").Replace("\n", "\r\n"))!,
                new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero), zone)
            .Select(o => o.Start).ToList();

    [Fact]
    public void CalDavSeries_WeeklyNineInSydney_StaysNineAcrossBothDstChanges()
    {
        // Sydney leaves DST on 5 Apr 2026 (+11 to +10) and starts it again on 4 Oct 2026; the Windows zone is London.
        var body = """
            BEGIN:VEVENT
            UID:syd
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Australia/Sydney:20260330T090000
            DTEND;TZID=Australia/Sydney:20260330T100000
            RRULE:FREQ=WEEKLY;BYDAY=MO
            END:VEVENT

            """;
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

        var starts = Starts(body, 2026, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));

        Assert.All(starts, s => Assert.Equal(9, TimeZoneInfo.ConvertTime(s, sydney).Hour));
        Assert.Contains(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero), starts);
        Assert.Contains(new DateTimeOffset(2026, 4, 5, 23, 0, 0, TimeSpan.Zero), starts);
        Assert.Contains(new DateTimeOffset(2026, 9, 27, 23, 0, 0, TimeSpan.Zero), starts);
        Assert.Contains(new DateTimeOffset(2026, 10, 4, 22, 0, 0, TimeSpan.Zero), starts);
    }

    [Fact]
    public void CalDavSeries_InCustomVTimeZone_IsExpandedWithItsRules()
    {
        // timezone-handling F5: 09:00 is 06:00 UTC at +03:00, and 05:00 UTC from 12 Apr at +04:00.
        var body = CustomZone + """
            BEGIN:VEVENT
            UID:custom
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=My Custom Zone:20260330T090000
            DTEND;TZID=My Custom Zone:20260330T100000
            RRULE:FREQ=WEEKLY;COUNT=4
            END:VEVENT

            """;

        Assert.Equal(
            [Utc(3, 30, 6), Utc(4, 6, 6), Utc(4, 13, 5), Utc(4, 20, 5)],
            Starts(body, 2026, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));
    }

    [Fact]
    public void ZoneChange_RecomputesGraphAllDayStart()
    {
        // timezone-handling F3: stored as midnight London, then midnight Tokyo after the change.
        var item = Graph.GraphEventMapper.Map(System.Text.Json.JsonDocument.Parse("""
            {"id":"AAMkD1","type":"singleInstance","subject":"Holiday","isAllDay":true,
             "start":{"dateTime":"2026-11-20T00:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-11-21T00:00:00.0000000","timeZone":"UTC"}}
            """).RootElement, "work", TimeZoneInfo.FindSystemTimeZoneById("Europe/London"))! with { Id = "d1" };
        _store.CreateItem(item);
        _store.RefreshOccurrences();
        Assert.Equal(Utc(11, 20, 0), _store.GetItem("d1")!.StartUtc);

        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        Assert.True(_store.RefreshOccurrences());

        var moved = _store.GetItem("d1")!;
        Assert.Equal((Utc(11, 19, 15), Utc(11, 20, 15)), (moved.StartUtc, moved.EndUtc));
        Assert.Equal([(Utc(11, 19, 15), Utc(11, 20, 15))], Occurrences("d1"));
    }
}
