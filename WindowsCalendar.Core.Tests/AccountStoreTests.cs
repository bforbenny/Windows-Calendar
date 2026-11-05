using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

public sealed class AccountStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wincal-acct-").FullName;
    private readonly LocalStore _store;

    public AccountStoreTests() => _store = LocalStore.Open(Path.Combine(_dir, "store.db"));

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static Account Radicale(string id) =>
        new(id, AccountType.CalDAV, $"Radicale {id}", "https://dav.example.test/", "ben", "ben@example.test");

    private static DiscoveredCollection Cal(string name, bool readOnly = false) =>
        new($"https://dav.example.test/ben/{name}/", name, CollectionKind.Calendar, readOnly);

    private static DiscoveredCollection Tasks(string name) =>
        new($"https://dav.example.test/ben/{name}/", name, CollectionKind.TaskList, false);

    [Fact]
    public void AddDiscoveredAccount_SavesAccountAndCollectionsInOrder()
    {
        _store.AddDiscoveredAccount(Radicale("a1"), [Cal("personal"), Tasks("personal"), Cal("holidays", readOnly: true)]);

        Assert.Equal([Radicale("a1")], _store.GetAccounts());
        var collections = _store.GetCollections("a1");
        Assert.Equal(["personal", "personal", "holidays"], collections.Select(c => c.Name));
        Assert.Equal([false, false, true], collections.Select(c => c.ReadOnly));
        Assert.All(collections, c => Assert.True(c.RemindersOn));
    }

    [Fact]
    public void NewCollections_GetAutomaticColourAndShape()
    {
        _store.AddDiscoveredAccount(Radicale("a1"), [Cal("one"), Cal("two")]);
        _store.AddDiscoveredAccount(Radicale("a2"), [Cal("three")]);

        var styles = _store.GetCollections("a1").Concat(_store.GetCollections("a2")).Select(c => (c.Colour, c.Shape));
        Assert.Equal([("Navy/L1", "Circle"), ("Orange/L2", "Square"), ("Teal/L3", "UpTriangle")], styles);
    }

    [Fact]
    public void Styles_SkipCombinationsInUse()
    {
        var used = new HashSet<(string, string)> { CollectionStyle.For(0) };
        Assert.Equal(CollectionStyle.For(1), CollectionStyle.Next(0, used));
    }

    [Fact]
    public void FirstAccount_SetsDefaultsToFirstWritable_LaterAccountsDoNotChangeThem()
    {
        var first = _store.AddDiscoveredAccount(Radicale("a1"), [Cal("holidays", readOnly: true), Cal("personal"), Tasks("todo")]);
        _store.AddDiscoveredAccount(Radicale("a2"), [Cal("work"), Tasks("work")]);

        Assert.Equal(first[1].Id, _store.DefaultCalendarId);
        Assert.Equal(first[2].Id, _store.DefaultTaskListId);
    }

    [Fact]
    public void ServerMainCalendar_IsPreferredAsDefault()
    {
        var added = _store.AddDiscoveredAccount(new Account("m", AccountType.MS365, "Work"),
            [new("AAA", "Birthdays", CollectionKind.Calendar, false), new("BBB", "Calendar", CollectionKind.Calendar, false, IsDefault: true)]);

        Assert.Equal(added[1].Id, _store.DefaultCalendarId);
    }

    [Fact]
    public void RemoveAccount_DeletesItsDataAndMovesDefaults()
    {
        var a1 = _store.AddDiscoveredAccount(Radicale("a1"), [Cal("personal"), Tasks("todo")]);
        var a2 = _store.AddDiscoveredAccount(Radicale("a2"), [Cal("readonly", readOnly: true), Cal("work")]);
        _store.CreateItem(new Item("i1", a1[0].Id, ItemKind.Event, null, "x"));
        _store.CreateItem(new Item("i2", a2[1].Id, ItemKind.Event, null, "y"));

        _store.RemoveAccount("a1");

        Assert.Equal(["a2"], _store.GetAccounts().Select(a => a.Id));
        Assert.Null(_store.GetItem("i1"));
        Assert.Equal(["i2"], _store.GetOutbox().Select(e => e.ItemId));
        Assert.Equal(a2[1].Id, _store.DefaultCalendarId);
        Assert.Null(_store.DefaultTaskListId);
    }

    [Fact]
    public void RemoveAccount_KeepsDefaultsInOtherAccounts()
    {
        var a1 = _store.AddDiscoveredAccount(Radicale("a1"), [Cal("personal")]);
        _store.AddDiscoveredAccount(Radicale("a2"), [Cal("work")]);

        _store.RemoveAccount("a2");

        Assert.Equal(a1[0].Id, _store.DefaultCalendarId);
    }
}
