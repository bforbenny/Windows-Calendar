using Microsoft.Data.Sqlite;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Calendar sets in the store (calendar-sets R1, R2, R5, R8, R9).</summary>
public sealed class CalendarSetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-sets-").FullName;
    private LocalStore _store;

    public CalendarSetTests()
    {
        _store = LocalStore.Open(DbPath);
        _store.AddDiscoveredAccount(Radicale("a1"), [Cal("home"), Cal("work")]);
        _store.AddDiscoveredAccount(Radicale("a2"), [Cal("club")]);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "store.db");

    private static Account Radicale(string id) =>
        new(id, AccountType.CalDAV, $"Radicale {id}", "https://dav.example.test/", "ben", "ben@example.test");

    private static DiscoveredCollection Cal(string name) =>
        new($"https://dav.example.test/ben/{name}/", name, CollectionKind.Calendar, false);

    private string Id(string name) =>
        _store.GetAccounts().SelectMany(a => _store.GetCollections(a.Id)).Single(c => c.Name == name).Id;

    private HashSet<string> Hidden() =>
        _store.GetAccounts().SelectMany(a => _store.GetCollections(a.Id)).Where(c => _store.GetHiddenCollectionIds().Contains(c.Id))
            .Select(c => c.Name).ToHashSet();

    /// <summary>A "Work" set of work and club, made the way the sidebar does: untick, then Save as new set.</summary>
    private CalendarSet SaveWork()
    {
        _store.SetCollectionHidden(Id("home"), true);
        return _store.CreateCalendarSet(" Work ");
    }

    [Fact]
    public void SaveAsNewSet_HoldsTheTickedCollections_AndBecomesActive()
    {
        var work = SaveWork();

        Assert.Equal("Work", work.Name);
        Assert.Equal([work], _store.GetCalendarSets());
        Assert.Equal(work.Id, _store.ActiveSetId);
        Assert.False(_store.IsActiveSetModified());
        Assert.Equal(1, _store.CreateCalendarSet("Later").Position);
        Assert.Throws<ArgumentException>(() => _store.CreateCalendarSet("  "));
    }

    [Fact]
    public void Switching_RewritesTheTicks_AllShowsEverything()
    {
        var work = SaveWork();
        _store.SwitchToSet(null);
        Assert.Empty(Hidden());
        Assert.Null(_store.ActiveSetId);
        Assert.False(_store.IsActiveSetModified());

        _store.SwitchToSet(work.Id);
        Assert.Equal(["home"], Hidden());
        Assert.Equal(work.Id, _store.ActiveSetId);
    }

    [Fact]
    public void ManualTick_MarksModified_UntilUpdateOrSwitch()
    {
        var work = SaveWork();
        _store.SetCollectionHidden(Id("home"), false);
        Assert.True(_store.IsActiveSetModified());

        _store.UpdateCalendarSet(work.Id);
        Assert.False(_store.IsActiveSetModified());
        _store.SwitchToSet(work.Id);
        Assert.Empty(Hidden());

        _store.SetCollectionHidden(Id("club"), true);
        Assert.True(_store.IsActiveSetModified());
        _store.SwitchToSet(work.Id);
        Assert.False(_store.IsActiveSetModified());

        // "All" is modified while anything is hidden.
        _store.SwitchToSet(null);
        _store.SetCollectionHidden(Id("club"), true);
        Assert.True(_store.IsActiveSetModified());
    }

    [Fact]
    public void NewCollections_JoinOnlyAll()
    {
        SaveWork();

        _store.SyncCollections("a1", [Cal("home"), Cal("work"), Cal("new")]);
        _store.AddDiscoveredAccount(Radicale("a3"), [Cal("other")]);

        Assert.Equal(["home", "new", "other"], Hidden());
        Assert.False(_store.IsActiveSetModified());

        // With "All" active, a new collection is shown.
        _store.SwitchToSet(null);
        _store.SyncCollections("a2", [Cal("club"), Cal("extra")]);
        Assert.Empty(Hidden());
    }

    [Fact]
    public void RemovingAnAccount_RemovesItsCollectionsFromSets()
    {
        var work = SaveWork();
        _store.RemoveAccount("a2");

        Assert.False(_store.IsActiveSetModified());
        _store.SwitchToSet(null);
        _store.SwitchToSet(work.Id);
        Assert.Equal(["home"], Hidden());
        Assert.Equal(1, Count("SELECT COUNT(*) FROM calendar_set_members;"));
    }

    [Fact]
    public void SetsAndActiveSet_PersistAcrossRestarts()
    {
        var work = SaveWork();
        _store.SetCollectionHidden(Id("home"), false);
        _store.Dispose();
        _store = LocalStore.Open(DbPath);

        Assert.Equal([work], _store.GetCalendarSets());
        Assert.Equal(work.Id, _store.ActiveSetId);
        Assert.True(_store.IsActiveSetModified());
    }

    [Fact]
    public void Migration_AddsSetsToAnExistingStore()
    {
        _store.SetCollectionHidden(Id("home"), true);
        _store.Dispose();
        using (var c = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE calendar_set_members; DROP TABLE calendar_sets; PRAGMA user_version = 14;";
            cmd.ExecuteNonQuery();
        }

        _store = LocalStore.Open(DbPath);

        Assert.Empty(_store.GetCalendarSets());
        Assert.Null(_store.ActiveSetId);
        Assert.Equal(["home"], Hidden());
        Assert.Equal(["home", "work"], _store.GetCollections("a1").Select(c => c.Name));
        Assert.Equal("Rest", _store.CreateCalendarSet("Rest").Name);
    }

    private long Count(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
