using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>The public holiday calendar (calendar-shading R3-R5).</summary>
public sealed class HolidayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStore _store;

    public HolidayTests()
    {
        Directory.CreateDirectory(_dir);
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void OnlyOneCalendar_IsTheHolidayCalendar()
    {
        _store.AddDiscoveredAccount(new Account("a1", AccountType.CalDAV, "Home"), [
            new DiscoveredCollection("u1", "SG holidays", CollectionKind.Calendar, false),
            new DiscoveredCollection("u2", "Other holidays", CollectionKind.Calendar, false),
        ]);
        var ids = _store.GetCollections("a1").ToDictionary(c => c.RemoteRef, c => c.Id);
        Assert.Null(_store.GetHolidayCollectionId());

        _store.SetHolidayCollection(ids["u1"]);
        Assert.Equal(ids["u1"], _store.GetHolidayCollectionId());

        // Marking another moves the role.
        _store.SetHolidayCollection(ids["u2"]);
        Assert.Equal(ids["u2"], _store.GetHolidayCollectionId());

        _store.SetHolidayCollection(null);
        Assert.Null(_store.GetHolidayCollectionId());
    }

    [Fact]
    public void RemovedCalendar_IsNoLongerTheHolidayCalendar()
    {
        var work = new DiscoveredCollection("u2", "Work", CollectionKind.Calendar, false);
        _store.AddDiscoveredAccount(new Account("a1", AccountType.CalDAV, "Home"),
            [new DiscoveredCollection("u1", "SG holidays", CollectionKind.Calendar, false), work]);
        _store.SetHolidayCollection(_store.GetCollections("a1").Single(c => c.RemoteRef == "u1").Id);

        // An empty discovery removes nothing, so keep one calendar.
        _store.SyncCollections("a1", [work]);

        Assert.Null(_store.GetHolidayCollectionId());
    }

    [Fact]
    public void ShadingOnly_IsOnByDefault()
    {
        Assert.True(_store.GetHolidayShadingOnly());
        _store.SetHolidayShadingOnly(false);
        Assert.False(_store.GetHolidayShadingOnly());
        _store.SetHolidayShadingOnly(true);
        Assert.True(_store.GetHolidayShadingOnly());
    }

    [Fact]
    public void Days_SpanMultiDayHolidays_AndJoinSameDay()
    {
        var days = HolidayDays.Build([
            (new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 25), "Christmas Day"),
            (new DateOnly(2027, 2, 6), new DateOnly(2027, 2, 7), "Chinese New Year"),
            (new DateOnly(2027, 2, 7), new DateOnly(2027, 2, 7), "Other"),
            (new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 25), "Christmas Day"),
        ]);

        Assert.Equal("Christmas Day", days[new DateOnly(2026, 12, 25)]);
        Assert.Equal("Chinese New Year", days[new DateOnly(2027, 2, 6)]);
        Assert.Equal("Chinese New Year, Other", days[new DateOnly(2027, 2, 7)]);
        Assert.Equal(3, days.Count);
    }
}
