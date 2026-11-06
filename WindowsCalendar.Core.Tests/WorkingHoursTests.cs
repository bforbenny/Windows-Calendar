using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Core.Tests;

/// <summary>Working hours and week numbers (calendar-shading R1, R7).</summary>
public sealed class WorkingHoursTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStore _store;

    public WorkingHoursTests()
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
    public void Default_IsMondayToFriday_NineToFive()
    {
        var hours = _store.GetWorkingHours();
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(17, 0)), (hours.Start, hours.End));
        Assert.True(hours.IsWorkingDay(new DateOnly(2026, 11, 6)));   // Friday
        Assert.False(hours.IsWorkingDay(new DateOnly(2026, 11, 7)));  // Saturday
    }

    [Fact]
    public void Saved_RoundTrips()
    {
        var hours = new WorkingHours(new HashSet<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Saturday }, new TimeOnly(8, 30), new TimeOnly(18, 0));
        _store.SetWorkingHours(hours);

        var read = _store.GetWorkingHours();
        Assert.Equal(hours.Days.Order(), read.Days.Order());
        Assert.Equal((hours.Start, hours.End), (read.Start, read.End));
    }

    [Fact]
    public void EndNotAfterStart_IsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            _store.SetWorkingHours(WorkingHours.Default with { Start = new TimeOnly(17, 0), End = new TimeOnly(9, 0) }));

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("1,2|17:00|09:00")]
    [InlineData("9|09:00|17:00")]
    public void Unreadable_IsTheDefault(string? stored) => Assert.Equal(WorkingHours.Default, WorkingHours.Parse(stored));

    [Fact]
    public void NoWorkingDays_IsAllowed() => Assert.Empty(WorkingHours.Parse("|09:00|17:00").Days);

    [Theory]
    // 5 Nov 2026 is in ISO week 45 (PRD acceptance), whichever day weeks start on.
    [InlineData(2026, 11, 5, DayOfWeek.Monday, 45)]
    [InlineData(2026, 11, 5, DayOfWeek.Sunday, 45)]
    // Sun 1 Nov 2026 is ISO week 44, but a Sunday-start row takes its Monday's number.
    [InlineData(2026, 11, 1, DayOfWeek.Sunday, 45)]
    [InlineData(2026, 11, 1, DayOfWeek.Monday, 44)]
    // Year boundaries: Thu 1 Jan 2026 is week 1; Fri 1 Jan 2027 is week 53 of 2026.
    [InlineData(2026, 1, 1, DayOfWeek.Monday, 1)]
    [InlineData(2027, 1, 1, DayOfWeek.Monday, 53)]
    public void WeekNumber_IsTheIsoWeekOfTheMonday(int y, int m, int d, DayOfWeek weekStart, int expected) =>
        Assert.Equal(expected, ViewPeriod.WeekNumber(new DateOnly(y, m, d), weekStart));
}
