using System.Globalization;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>The clash and short-gap check (schedule-gap-warning R2-R5, R7, R10) and its settings in the store.</summary>
public sealed class ScheduleCheckTests
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-AU");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly HashSet<string> NoOwn = [];

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 11, 16, hour, minute, 0, TimeSpan.Zero);

    private static OccurrenceView Event(string id, DateTimeOffset start, DateTimeOffset end, string collection = "work",
        ShowAs showAs = ShowAs.Busy, bool allDay = false, ItemKind kind = ItemKind.Event, string title = "Weekly sync") =>
        new(id, collection, kind, title, null, start, end, allDay, ShowAs: showAs);

    private static ScheduleNote? Check(DateTimeOffset start, DateTimeOffset end, IEnumerable<OccurrenceView> others,
        int gap = 10, IReadOnlySet<string>? members = null, IReadOnlySet<string>? own = null, ShowAs showAs = ShowAs.Busy, bool allDay = false) =>
        ScheduleCheck.Check(start, end, allDay, showAs, others, own ?? NoOwn, members, gap, Utc, Culture);

    [Fact]
    public void Overlap_IsAWarningNamingTheFirstAndTheRest()
    {
        var note = Check(At(10), At(11), [Event("b", At(10, 30), At(11, 30), title: "Review"), Event("a", At(9, 30), At(10, 15))]);

        Assert.NotNull(note);
        Assert.True(note.Overlap);
        Assert.StartsWith("Overlaps Weekly sync", note.Text);
        Assert.EndsWith("+1 more", note.Text);
    }

    [Fact]
    public void ShortGap_BeforeAndAfter()
    {
        var after = Check(At(10, 5), At(11), [Event("a", At(9), At(10))]);
        var before = Check(At(9), At(9, 53), [Event("a", At(10), At(11))]);

        Assert.Equal(new ScheduleNote(false, "5 min after Weekly sync, less than your 10 min gap"), after);
        Assert.Equal(new ScheduleNote(false, "7 min before Weekly sync, less than your 10 min gap"), before);
    }

    [Fact]
    public void GapAtTheMinimum_IsFine()
    {
        Assert.Null(Check(At(10, 10), At(11), [Event("a", At(9), At(10))]));
    }

    [Fact]
    public void BackToBack_IsAShortGap_UnlessOff()
    {
        Assert.Equal(new ScheduleNote(false, "Right after Weekly sync"), Check(At(10), At(11), [Event("a", At(9), At(10))]));
        Assert.Null(Check(At(10), At(11), [Event("a", At(9), At(10))], gap: 0));
    }

    [Fact]
    public void Off_StillWarnsAboutAnOverlap()
    {
        Assert.True(Check(At(10), At(11), [Event("a", At(10, 30), At(12))], gap: 0)?.Overlap);
    }

    [Fact]
    public void FreeAllDayTasksOtherCalendarsAndOwnSeries_DoNotCount()
    {
        OccurrenceView[] others =
        [
            Event("free", At(10), At(11), showAs: ShowAs.Free),
            Event("allday", At(0), At(23, 59), allDay: true),
            Event("task", At(10), At(11), kind: ItemKind.Task),
            Event("home", At(10), At(11), collection: "home"),
            Event("own", At(10), At(11)),
        ];

        Assert.Null(Check(At(10), At(11), others, members: new HashSet<string> { "work" }, own: new HashSet<string> { "own" }));
    }

    [Fact]
    public void TentativeAndOutOfOffice_Count()
    {
        Assert.True(Check(At(10), At(11), [Event("a", At(10), At(11), showAs: ShowAs.Tentative)])?.Overlap);
        Assert.True(Check(At(10), At(11), [Event("a", At(10), At(11), showAs: ShowAs.OutOfOffice)])?.Overlap);
    }

    [Fact]
    public void FreeOrAllDaySavedEvent_IsNotChecked()
    {
        OccurrenceView[] others = [Event("a", At(10), At(11))];

        Assert.Null(Check(At(10), At(11), others, showAs: ShowAs.Free));
        Assert.Null(Check(At(0), At(0).AddDays(1), others, allDay: true));
    }

    [Fact]
    public void AllMembers_WhenNull()
    {
        Assert.True(Check(At(10), At(11), [Event("a", At(10), At(11), collection: "anything")])?.Overlap);
    }

    [Fact]
    public void DeclinedInvitations_DoNotCount()
    {
        Assert.Null(Check(At(10), At(11), [Event("a", At(10), At(11)) with { Declined = true }]));
    }

    [Fact]
    public void CancelledMeetings_DoNotCount()
    {
        Assert.Null(Check(At(10), At(11), [Event("a", At(10), At(11)) with { Cancelled = true }]));
    }

    // ---- View indicators (R11) ----

    private static IReadOnlyList<ScheduleProblem> Problems(int gap, params OccurrenceView[] events) =>
        ScheduleCheck.Problems(events, gap, Utc, Culture);

    [Fact]
    public void Problems_OverlapCoversTheOverlappingMinutes()
    {
        var p = Assert.Single(Problems(10, Event("a", At(9), At(10, 15)), Event("b", At(10), At(11), title: "Review")));

        Assert.True(p.Overlap);
        Assert.Equal((At(10), At(10, 15)), (p.Start, p.End));
        Assert.StartsWith("Weekly sync overlaps Review", p.Text);
        Assert.Equal(("a|", "b|"), (p.First, p.Second));
    }

    [Fact]
    public void Problems_ShortGapAndBackToBack()
    {
        var problems = Problems(10, Event("a", At(9), At(10)), Event("b", At(10, 5), At(11), title: "Review"), Event("c", At(11), At(12), title: "Lunch"));

        Assert.Equal(2, problems.Count);
        Assert.Equal(new ScheduleProblem(At(10), At(10, 5), false, "5 min between Weekly sync and Review, less than your 10 min gap", "a|", "b|"), problems[0]);
        Assert.Equal(new ScheduleProblem(At(11), At(11), false, "Lunch right after Review", "b|", "c|"), problems[1]);
    }

    [Fact]
    public void Problems_GapIsMeasuredFromTheLatestEnd()
    {
        // b sits inside a; c starts 5 min after a ends, not after b.
        var problems = Problems(10, Event("a", At(9), At(11)), Event("b", At(9, 30), At(10)), Event("c", At(11, 5), At(12)));

        Assert.Equal(2, problems.Count);
        Assert.True(problems[0].Overlap);
        Assert.Equal((At(11), At(11, 5)), (problems[1].Start, problems[1].End));
    }

    [Fact]
    public void Problems_OffFindsOverlapsOnly_AndIgnoresWhatTheCheckIgnores()
    {
        Assert.Empty(Problems(0, Event("a", At(9), At(10)), Event("b", At(10), At(11))));
        Assert.Empty(Problems(10,
            Event("a", At(9), At(11)),
            Event("free", At(10), At(11), showAs: ShowAs.Free),
            Event("allday", At(0), At(23), allDay: true),
            Event("declined", At(10), At(11)) with { Declined = true },
            Event("task", At(10), At(11), kind: ItemKind.Task)));
    }

    [Fact]
    public void Summary_CountsBoth()
    {
        Assert.Equal("1 overlap, 2 short gaps", ScheduleCheck.Summary([
            new(At(9), At(10), true, ""), new(At(10), At(10), false, ""), new(At(11), At(11), false, "")]));
        Assert.Equal("2 overlaps", ScheduleCheck.Summary([new(At(9), At(10), true, ""), new(At(9), At(10), true, "")]));
    }

    // ---- Declined (attendees-invitations interim) ----

    [Theory]
    [InlineData("""{"isOrganizer":false,"responseStatus":{"response":"declined"}}""", true)]
    [InlineData("""{"isOrganizer":true,"responseStatus":{"response":"declined"}}""", false)]
    [InlineData("""{"isOrganizer":false,"responseStatus":{"response":"accepted"}}""", false)]
    [InlineData("""{"isOrganizer":false,"responseStatus":null}""", false)]
    public void Declined_Graph(string json, bool expected)
    {
        Assert.Equal(expected, Declined.FromPayload(json, null));
    }

    [Theory]
    [InlineData("ATTENDEE;PARTSTAT=DECLINED:mailto:ben@example.test", true)]
    [InlineData("ATTENDEE;PARTSTAT=DECLINED:mailto:someone@example.test", false)]
    [InlineData("ATTENDEE;PARTSTAT=ACCEPTED:mailto:ben@example.test", false)]
    public void Declined_CalDav_OnlyYourOwnAttendee(string attendee, bool expected)
    {
        Assert.Equal(expected, Declined.FromPayload(ScheduleSettingsTests.Ics(attendee), "Ben@Example.test"));
    }
}

/// <summary>The per-set switch, the active set's members and the minimum gap in the store (calendar-sets R12).</summary>
public sealed class ScheduleSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-gap-").FullName;
    private LocalStore _store;

    public ScheduleSettingsTests()
    {
        _store = LocalStore.Open(DbPath);
        _store.AddDiscoveredAccount(new Account("a1", AccountType.CalDAV, "Radicale", "https://dav.example.test/", "ben", "ben@example.test"),
            [new("https://dav.example.test/ben/home/", "home", CollectionKind.Calendar, false),
             new("https://dav.example.test/ben/work/", "work", CollectionKind.Calendar, false)]);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "store.db");

    private string Id(string name) => _store.GetCollections("a1").Single(c => c.Name == name).Id;

    [Fact]
    public void All_StartsOn_UserSetsStartOff_AndNeitherIsModified()
    {
        _store.SetCollectionHidden(Id("home"), true);
        var work = _store.CreateCalendarSet("Work");

        Assert.True(_store.GetClashCheck(null));
        Assert.False(_store.GetClashCheck(work.Id));

        _store.SetClashCheck(work.Id, true);
        _store.SetClashCheck(null, false);

        Assert.True(_store.GetClashCheck(work.Id));
        Assert.False(_store.GetClashCheck(null));
        Assert.False(_store.IsActiveSetModified());
    }

    [Fact]
    public void ActiveSetMembers_AreTheSavedMembers_EvenWhenUnticked()
    {
        Assert.Null(_store.GetActiveSetMembers());
        _store.SetCollectionHidden(Id("home"), true);
        _store.CreateCalendarSet("Work");
        _store.SetCollectionHidden(Id("work"), true);

        Assert.Equal(new HashSet<string> { Id("work") }, _store.GetActiveSetMembers());
    }

    [Fact]
    public void MinimumGap_DefaultsToTen_AndKeepsAChoice()
    {
        Assert.Equal(10, _store.GetMinimumGap());
        _store.SetMinimumGap(0);
        Assert.Equal(0, _store.GetMinimumGap());
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.SetMinimumGap(7));
    }

    internal static string Ics(string attendee) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:m1\r\nDTSTAMP:20261101T000000Z\r\n"
        + "DTSTART:20261116T100000Z\r\nDTEND:20261116T110000Z\r\nSUMMARY:Meeting\r\n"
        + "ORGANIZER:mailto:boss@example.test\r\n" + attendee + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void DeclinedFlag_IsStoredAndShownOnTheOccurrence()
    {
        var start = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        _store.CreateItem(new Item("m1", Id("work"), ItemKind.Event, "m1.ics", Ics("ATTENDEE;PARTSTAT=DECLINED:mailto:ben@example.test"),
            Summary: "Meeting", StartUtc: start, EndUtc: start.AddHours(1)));
        _store.CreateItem(new Item("m2", Id("work"), ItemKind.Event, "m2.ics", Ics("ATTENDEE;PARTSTAT=ACCEPTED:mailto:ben@example.test"),
            Summary: "Meeting", StartUtc: start, EndUtc: start.AddHours(1)));

        var views = _store.GetOccurrenceViews(start.AddDays(-1), start.AddDays(1));

        Assert.True(views.Single(v => v.ItemId == "m1").Declined);
        Assert.False(views.Single(v => v.ItemId == "m2").Declined);
    }

    [Fact]
    public void Migration_SetsTheDeclinedFlagOfExistingEvents()
    {
        var start = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        _store.CreateItem(new Item("m1", Id("work"), ItemKind.Event, "m1.ics", Ics("ATTENDEE;PARTSTAT=DECLINED:mailto:ben@example.test"),
            Summary: "Meeting", StartUtc: start, EndUtc: start.AddHours(1)));
        _store.Dispose();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE items DROP COLUMN declined; ALTER TABLE items DROP COLUMN cancelled; PRAGMA user_version = 18;";
            cmd.ExecuteNonQuery();
        }

        _store = LocalStore.Open(DbPath);

        Assert.True(Assert.Single(_store.GetOccurrenceViews(start.AddDays(-1), start.AddDays(1))).Declined);
    }

    [Fact]
    public void Migration_AddsTheSwitch_ExistingSetsAreOff()
    {
        _store.SetCollectionHidden(Id("home"), true);
        var work = _store.CreateCalendarSet("Work");
        _store.Dispose();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE calendar_sets DROP COLUMN clash_check; ALTER TABLE items DROP COLUMN declined; ALTER TABLE items DROP COLUMN cancelled; PRAGMA user_version = 17;";
            cmd.ExecuteNonQuery();
        }

        _store = LocalStore.Open(DbPath);

        Assert.False(_store.GetClashCheck(work.Id));
        _store.SetClashCheck(work.Id, true);
        Assert.True(_store.GetClashCheck(work.Id));
    }
}
