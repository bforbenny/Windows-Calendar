namespace WindowsCalendar.Core.Store;

public enum AccountType { MS365, CalDAV }

public enum CollectionKind { Calendar, TaskList }

public enum ItemKind { Event, Task }

public enum OutboxOperation { Create, Update, Delete }

/// <summary>What happened to a server change (conflict-resolution R1-R4).</summary>
public enum ServerChangeResult
{
    /// <summary>The server version was applied (or its delete agreed with a local delete).</summary>
    Applied,

    /// <summary>The server version won; the local version is kept in Conflicts.</summary>
    Conflict,

    /// <summary>Not applied yet: the item is being sent, or was never sent.</summary>
    KeptLocal,
}

/// <summary>
/// An account (account-management R1). Credentials are never stored here (R6). ServerUrl, Username and Email
/// are set for CalDAV accounts (R4, R4a).
/// </summary>
public sealed record Account(
    string Id,
    AccountType Type,
    string DisplayName,
    string? ServerUrl = null,
    string? Username = null,
    string? Email = null);

/// <summary>
/// A calendar or task list (account-management R1). RemoteRef is the CalDAV URL or Graph id. SourceUrl is set for a
/// subscribed calendar: the feed the app downloads (sync-radicale R1a). Shape is a shape name or "Glyph:XXXX" for a
/// Fluent icon (ui-colour-theme R7). ServerColour is the server's colour snapped to the 24; ColourOverride marks a
/// colour the user chose, which sync no longer changes.
/// </summary>
public sealed record Collection(
    string Id,
    string AccountId,
    CollectionKind Kind,
    string Name,
    string Colour,
    string Shape,
    bool RemindersOn,
    bool ReadOnly,
    string RemoteRef,
    string? SourceUrl = null,
    bool ColourOverride = false,
    string? ServerColour = null);

/// <summary>
/// A cached event or task (local-store-offline R2): the raw server payload plus normalised columns.
/// RemoteId is the CalDAV UID or Graph id; null until the server knows the item. SeriesId is the Graph id of an
/// occurrence's series master. ReadOnly marks an event the app must not change (an MS365 meeting). Unaccepted marks
/// an invitation not yet accepted (not responded or tentative).
/// </summary>
public sealed record Item(
    string Id,
    string CollectionId,
    ItemKind Kind,
    string? RemoteId,
    string RawPayload,
    string? Summary = null,
    DateTimeOffset? StartUtc = null,
    DateTimeOffset? EndUtc = null,
    bool AllDay = false,
    string? TimeZone = null,
    string? Location = null,
    string? Status = null,
    DateTimeOffset? DueUtc = null,
    DateTimeOffset? CompletedUtc = null,
    bool IsRecurring = false,
    string? Etag = null,
    DateTimeOffset? LastModifiedUtc = null,
    string? SeriesId = null,
    bool ReadOnly = false,
    bool Unaccepted = false);

/// <summary>A queued local change (local-store-offline R4). Id order is replay order.</summary>
public sealed record OutboxEntry(
    long Id,
    string ItemId,
    OutboxOperation Operation,
    string? Payload,
    string? BaseEtag,
    DateTimeOffset CreatedAt,
    int Attempts,
    string? LastError,
    string? GroupId,
    bool InFlight,
    DateTimeOffset NextAttemptAt,
    bool Permanent);

/// <summary>
/// A calendar or task list found on the server, before it gets a local id, colour and shape (account-management R4, R9).
/// IsDefault marks the server's main calendar (Graph isDefaultCalendar); CalDAV has none. ServerColour is the server's
/// colour as #RRGGBB(AA), when it has one (ui-colour-theme R9).
/// </summary>
public sealed record DiscoveredCollection(string RemoteRef, string Name, CollectionKind Kind, bool ReadOnly, bool IsDefault = false,
    string? SourceUrl = null, string? ServerColour = null);

/// <summary>What a re-discovery changed (account-management R11).</summary>
public sealed record CollectionChanges(int Added, int Updated, int Removed)
{
    public bool Any => Added + Updated + Removed > 0;
}

/// <summary>An occurrence with what the views need to draw it (calendar-views). Completed is for tasks.</summary>
public sealed record OccurrenceView(
    string ItemId,
    string CollectionId,
    ItemKind Kind,
    string? Summary,
    string? Location,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    bool IsRecurring = false,
    DateTimeOffset? RecurrenceUtc = null,
    bool ReadOnly = false,
    bool Unaccepted = false,
    bool Completed = false,
    ShowAs ShowAs = ShowAs.Busy);

/// <summary>One expanded instance of an item in the occurrence index (local-store-offline R3).</summary>
public sealed record ItemOccurrence(string ItemId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
