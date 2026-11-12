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

/// <summary>A user-defined calendar set (calendar-sets R2); "All" is built in and not stored.</summary>
public sealed record CalendarSet(string Id, string Name, int Position);

/// <summary>
/// The secondary timezone ruler (timezone-handling R8, calendar-sets R11): on or off, and its zone (a Windows id; kept
/// while off). Two rulers are the same when both are off, or both on in the same zone.
/// </summary>
public sealed record RulerChoice(bool On, string? ZoneId)
{
    public static readonly RulerChoice Off = new(false, null);

    public bool SameAs(RulerChoice other) => On == other.On && (!On || ZoneId == other.ZoneId);
}

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
    bool Unaccepted = false,
    string? JoinUrl = null);

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
/// A row of the pending changes list (local-store-offline R9a): the entry, its item's title, and its state. Failed
/// means a send failed (<paramref name="LastError"/> is set); <paramref name="Permanent"/> ones wait for Retry.
/// </summary>
public sealed record PendingChange(long EntryId, string ItemId, string Title, OutboxOperation Operation, string? LastError,
    bool Permanent, bool InFlight, ItemKind Kind = ItemKind.Event)
{
    public bool Failed => LastError is not null;
}

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

/// <summary>
/// An occurrence with what the views need to draw it (calendar-views). Completed is for tasks. TimeZone is the event's
/// own start zone id as stored (timezone-handling R9).
/// </summary>
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
    ShowAs ShowAs = ShowAs.Busy,
    string? JoinUrl = null,
    string? TimeZone = null,
    bool Declined = false,
    bool Cancelled = false)
{
    /// <summary>Declined or cancelled: drawn faded, no reminder, no Join, not in the clash check (attendees-invitations R4, R5).</summary>
    public bool Inactive => Declined || Cancelled;
}

/// <summary>Which items a search lists (search-filters R2).</summary>
public enum SearchKind { All, Events, Tasks }

/// <summary>
/// What narrows a search besides its typed words (search-filters R2). Null or empty members set no filter.
/// <see cref="CollectionIds"/> is the calendars and task lists to search (an empty list matches nothing);
/// <see cref="FromUtc"/> and <see cref="ToUtc"/> are a half-open range [From, To) in UTC; <see cref="Attendee"/> is one
/// name or address as returned by <c>LocalStore.People</c>; <see cref="Location"/> is free text.
/// </summary>
public sealed record SearchFilter(
    SearchKind Kind = SearchKind.All,
    IReadOnlyList<string>? CollectionIds = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Attendee = null,
    string? Location = null,
    string? TitleWords = null)
{
    /// <summary>No filter is set (search-filters R2).</summary>
    public bool IsEmpty =>
        Kind == SearchKind.All && CollectionIds is null && FromUtc is null && ToUtc is null
        && string.IsNullOrWhiteSpace(Attendee) && string.IsNullOrWhiteSpace(Location) && string.IsNullOrWhiteSpace(TitleWords);
}

/// <summary>
/// A search result (search R3, R5, R7): the item and occurrence to open, its date (null for an undated task), and its
/// title and snippet with matches between <see cref="MarkStart"/> and <see cref="MarkEnd"/>. Snippet is null when it
/// would only repeat the title.
/// </summary>
public sealed record SearchResult(
    string ItemId,
    string CollectionId,
    ItemKind Kind,
    string Title,
    string? Snippet,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    bool AllDay,
    DateTimeOffset? RecurrenceUtc,
    bool IsSeries,
    bool Completed)
{
    public const char MarkStart = '\u0002';
    public const char MarkEnd = '\u0003';
}

/// <summary>
/// A kept conflict copy for the conflicts list (conflict-resolution R5 interim). Title, start and all-day come from the
/// item when it is on the calendar, else the title from the saved local version and no date. LocalDeleted: the local
/// change was a delete; OnCalendar: the item can be opened.
/// </summary>
public sealed record ConflictInfo(
    long Id,
    string ItemId,
    string CollectionId,
    ItemKind Kind,
    string Title,
    DateTimeOffset? StartUtc,
    bool AllDay,
    bool LocalDeleted,
    bool OnCalendar,
    DateTimeOffset DetectedAt);

/// <summary>
/// A kept conflict copy as stored, for resolving it (conflict-resolution R6): LocalPayload is null when the local change
/// was a delete.
/// </summary>
public sealed record ConflictRecord(long Id, string ItemId, string CollectionId, ItemKind Kind, string? LocalPayload);

/// <summary>One expanded instance of an item in the occurrence index (local-store-offline R3).</summary>
public sealed record ItemOccurrence(string ItemId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
