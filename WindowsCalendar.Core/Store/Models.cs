namespace WindowsCalendar.Core.Store;

public enum AccountType { MS365, CalDAV }

public enum CollectionKind { Calendar, TaskList }

public enum ItemKind { Event, Task }

public enum OutboxOperation { Create, Update, Delete }

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

/// <summary>A calendar or task list (account-management R1). RemoteRef is the CalDAV URL or Graph id.</summary>
public sealed record Collection(
    string Id,
    string AccountId,
    CollectionKind Kind,
    string Name,
    string Colour,
    string Shape,
    bool RemindersOn,
    bool ReadOnly,
    string RemoteRef);

/// <summary>
/// A cached event or task (local-store-offline R2): the raw server payload plus normalised columns.
/// RemoteId is the CalDAV UID or Graph id; null until the server knows the item.
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
    DateTimeOffset? LastModifiedUtc = null);

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
/// IsDefault marks the server's main calendar (Graph isDefaultCalendar); CalDAV has none.
/// </summary>
public sealed record DiscoveredCollection(string RemoteRef, string Name, CollectionKind Kind, bool ReadOnly, bool IsDefault = false);

/// <summary>An occurrence with what the views need to draw it (calendar-views).</summary>
public sealed record OccurrenceView(
    string ItemId,
    string CollectionId,
    ItemKind Kind,
    string? Summary,
    string? Location,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay);

/// <summary>One expanded instance of an item in the occurrence index (local-store-offline R3).</summary>
public sealed record ItemOccurrence(string ItemId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
