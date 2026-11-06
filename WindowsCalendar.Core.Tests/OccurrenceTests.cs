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
}
