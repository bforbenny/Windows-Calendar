using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Server colours snapped to the 24 calendar colours (ui-colour-theme R9 interim).</summary>
public sealed class CalendarColoursTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStore _store;

    public CalendarColoursTests()
    {
        Directory.CreateDirectory(_dir);
        _store = LocalStore.Open(Path.Combine(_dir, "store.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    // Exact light swatch steps (50 / 70 / 90) snap to themselves.
    [InlineData("#437CF1", "Navy/L1")]
    [InlineData("#0E49BF", "Navy/L2")]
    [InlineData("#5E0E0E", "Strawberry Red/L3")]
    // Alpha is ignored; case does not matter.
    [InlineData("#a77900ff", "Amber/L1")]
    // Near misses: a typical Thunderbird green and red.
    [InlineData("#33CC66", "Sea Green/L1")]
    [InlineData("#E01B24", "Strawberry Red/L1")]
    public void Snap_IsTheNearestCalendarColour(string server, string expected) =>
        Assert.Equal(expected, CalendarColours.Snap(server));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blue")]
    [InlineData("#12345")]
    public void Snap_OfNoColour_IsNull(string? server) => Assert.Null(CalendarColours.Snap(server));

    [Fact]
    public void ServerColour_SetsTheColour_FollowsChanges_AndAbsentKeepsIt()
    {
        var account = new Account("a1", AccountType.CalDAV, "Home");
        _store.AddDiscoveredAccount(account, [new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false, ServerColour: "#437CF1")]);
        Assert.Equal("Navy/L1", _store.GetCollections("a1").Single().Colour);

        var changes = _store.SyncCollections("a1", [
            new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false, ServerColour: "#E54B4B"),
            new DiscoveredCollection("u2", "Home", CollectionKind.Calendar, false),
        ]);
        Assert.Equal((1, 1), (changes.Added, changes.Updated));
        Assert.Equal("Strawberry Red/L1", _store.GetCollections("a1").Single(c => c.RemoteRef == "u1").Colour);

        // No server colour: the colour stays, and nothing counts as changed.
        var home = _store.GetCollections("a1").Single(c => c.RemoteRef == "u2").Colour;
        changes = _store.SyncCollections("a1", [
            new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false),
            new DiscoveredCollection("u2", "Home", CollectionKind.Calendar, false),
        ]);
        Assert.False(changes.Any);
        Assert.Equal("Strawberry Red/L1", _store.GetCollections("a1").Single(c => c.RemoteRef == "u1").Colour);
        Assert.Equal(home, _store.GetCollections("a1").Single(c => c.RemoteRef == "u2").Colour);
    }

    [Fact]
    public void Override_SurvivesServerColours_UntilUseServerColour()
    {
        _store.AddDiscoveredAccount(new Account("a1", AccountType.CalDAV, "Home"),
            [new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false, ServerColour: "#437CF1")]);
        var id = _store.GetCollections("a1").Single().Id;

        _store.SetCollectionColour(id, "Teal/L2");
        _store.SyncCollections("a1", [new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false, ServerColour: "#E54B4B")]);
        var c = _store.GetCollections("a1").Single();
        Assert.Equal(("Teal/L2", true, "Strawberry Red/L1"), (c.Colour, c.ColourOverride, c.ServerColour));

        _store.UseServerColour(id);
        c = _store.GetCollections("a1").Single();
        Assert.Equal(("Strawberry Red/L1", false), (c.Colour, c.ColourOverride));
    }

    [Fact]
    public void Shape_CanBeAnIcon_AndSyncLeavesIt()
    {
        _store.AddDiscoveredAccount(new Account("a1", AccountType.CalDAV, "Home"),
            [new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false)]);
        var id = _store.GetCollections("a1").Single().Id;

        _store.SetCollectionShape(id, "Glyph:E80F");
        _store.SyncCollections("a1", [new DiscoveredCollection("u1", "Work", CollectionKind.Calendar, false, ServerColour: "#437CF1")]);

        Assert.Equal("Glyph:E80F", _store.GetCollections("a1").Single().Shape);
    }
}
