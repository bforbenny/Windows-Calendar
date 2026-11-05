using Microsoft.Extensions.Time.Testing;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

public sealed class OutboxBackoffTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-backoff-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly LocalStore _store;

    public OutboxBackoffTests()
    {
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"), _time);
        _store.AddAccount(new Account("a1", AccountType.MS365, "Work"));
        _store.AddCollection(new Collection("c1", "a1", CollectionKind.Calendar, "Calendar", "Navy", "Circle", true, false, "AAMk"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private long Create(string id)
    {
        _store.CreateItem(new Item(id, "c1", ItemKind.Event, null, "{}"));
        return _store.GetOutbox().Last().Id;
    }

    private IEnumerable<string> DueItems() => _store.GetDueOutbox().Select(e => e.ItemId);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 900)]
    [InlineData(50, 900)]
    public void RetryDelay_DoublesUpTo15Minutes(int attempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), LocalStore.RetryDelay(attempts));

    [Fact]
    public void FailedEntry_IsDueAgainAfterItsDelay()
    {
        var id = Create("i1");
        _store.RecordOutboxFailure(id, "503");
        _store.RecordOutboxFailure(id, "503");

        Assert.Empty(DueItems());
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(DueItems());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["i1"], DueItems());
    }

    [Fact]
    public void PermanentFailure_WaitsForUserRetry()
    {
        var id = Create("i1");
        _store.RecordOutboxFailure(id, "400 Bad Request", permanent: true);

        _time.Advance(TimeSpan.FromHours(1));
        _store.ResetBackoff();
        Assert.Empty(DueItems());
        Assert.True(_store.GetOutbox()[0].Permanent);

        _store.RetryOutboxEntry(id);
        Assert.Equal(["i1"], DueItems());
    }

    [Fact]
    public void ResetBackoff_MakesTransientFailuresDueNow()
    {
        var id = Create("i1");
        _store.RecordOutboxFailure(id, "503");

        _store.ResetBackoff();
        Assert.Equal(["i1"], DueItems());
    }

    [Fact]
    public void LaterEntry_WaitsForEarlierEntryOfSameItem()
    {
        var first = Create("i1");
        _store.MarkInFlight(first);
        _store.UpdateItem(new Item("i1", "c1", ItemKind.Event, null, "{\"v\":2}"));
        Create("i2");

        Assert.Equal(["i2"], DueItems());
    }

    [Fact]
    public void GroupedEntry_WaitsForEarlierEntryOfSameGroup()
    {
        _store.CompleteOutboxEntry(Create("i1"));
        _store.CompleteOutboxEntry(Create("i2"));
        _store.UpdateItem(new Item("i1", "c1", ItemKind.Event, null, "{\"v\":2}"), groupId: "g1");
        _store.CreateItem(new Item("i3", "c1", ItemKind.Event, null, "{}"), groupId: "g1");
        _store.UpdateItem(new Item("i2", "c1", ItemKind.Event, null, "{\"v\":2}"));
        _store.RecordOutboxFailure(_store.GetOutbox()[0].Id, "503");

        Assert.Equal(["i2"], DueItems());
    }
}
