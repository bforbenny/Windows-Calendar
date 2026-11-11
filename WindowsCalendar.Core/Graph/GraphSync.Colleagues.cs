using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>What the user may read of a colleague's calendar (ms365-colleague-calendars R1).</summary>
public enum ColleagueAccess { NotAvailable, FreeBusyOnly, Events }

/// <summary>
/// Colleague calendars (ms365-colleague-calendars): a colleague's primary calendar, added by email because
/// <c>/me/calendars</c> does not list calendars added in Outlook through "Add from directory". Stored as a read-only
/// calendar whose remote ref is <c>user:&lt;email&gt;</c>, and read from the colleague's mailbox under Calendars.Read.Shared.
/// </summary>
public sealed partial class GraphSync
{
    public const string ColleaguePrefix = "user:";

    /// <summary>A calendar's pull failed with an HTTP error this sync (R7): calendar's local id, status. For the log.</summary>
    public event Action<string, int?>? PullFailed;

    /// <summary>The colleague's email if <paramref name="c"/> is a colleague calendar, else null.</summary>
    public static string? ColleagueEmail(Collection c) =>
        c.Kind == CollectionKind.Calendar && c.RemoteRef.StartsWith(ColleaguePrefix, StringComparison.Ordinal)
            ? c.RemoteRef[ColleaguePrefix.Length..]
            : null;

    /// <summary>
    /// The colleague calendar for <paramref name="email"/> (R1, R3), named after its owner, if its events can be read.
    /// NotAvailable: the address is wrong or the calendar is refused. FreeBusyOnly: the calendar is there but one day of
    /// its events is refused (shared as free/busy, or not at all).
    /// </summary>
    public static async Task<(ColleagueAccess Access, DiscoveredCollection? Calendar)> FindColleagueAsync(GraphClient client, string email,
        CancellationToken cancellationToken)
    {
        email = email.Trim().ToLowerInvariant();
        var mailbox = $"/users/{Uri.EscapeDataString(email)}";
        string? owner;
        try
        {
            using var doc = await client.GetAsync($"{mailbox}/calendar?$select=id,name,owner", cancellationToken);
            owner = doc.RootElement.TryGetProperty("owner", out var o) ? GraphClient.Text(o, "name") : null;
        }
        catch (GraphException ex) when (ex.Error == GraphError.Http)
        {
            return (ColleagueAccess.NotAvailable, null);
        }
        try
        {
            var today = DateTime.UtcNow.Date;
            using var _ = await client.GetAsync(
                $"{mailbox}/calendarView?startDateTime={today:yyyy-MM-ddTHH:mm:ss}Z&endDateTime={today.AddDays(1):yyyy-MM-ddTHH:mm:ss}Z&$top=1&$select=id",
                cancellationToken);
        }
        catch (GraphException ex) when (ex.Error == GraphError.Http)
        {
            return (ColleagueAccess.FreeBusyOnly, null);
        }
        return (ColleagueAccess.Events,
            new DiscoveredCollection(ColleaguePrefix + email, owner is { Length: > 0 } ? owner : email, CollectionKind.Calendar, ReadOnly: true));
    }

    /// <summary>The owner addresses, in lower case, of the calendars <c>/me/calendars</c> lists (R2).</summary>
    public static async Task<HashSet<string>> ListedOwnersAsync(GraphClient client, CancellationToken cancellationToken)
    {
        var owners = new HashSet<string>();
        string? url = "/me/calendars?$top=100&$select=owner";
        while (url is not null)
        {
            using var doc = await client.GetAsync(url, cancellationToken);
            foreach (var c in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (c.TryGetProperty("owner", out var o) && GraphClient.Text(o, "address") is { Length: > 0 } address)
                {
                    owners.Add(address.ToLowerInvariant());
                }
            }
            url = GraphClient.Text(doc.RootElement, "@odata.nextLink");
        }
        return owners;
    }

    /// <summary>Where a calendar's events live: the colleague's mailbox (R5), else the user's own.</summary>
    private static string Mailbox(Collection calendar) =>
        ColleagueEmail(calendar) is { } email ? $"/users/{Uri.EscapeDataString(email)}" : "/me";

    /// <summary>Re-discovery keeps colleague calendars (R4): they are listed as found, unchanged.</summary>
    private IEnumerable<DiscoveredCollection> StoredColleagues(string accountId) =>
        store.GetCollections(accountId).Where(c => ColleagueEmail(c) is not null)
            .Select(c => new DiscoveredCollection(c.RemoteRef, c.Name, c.Kind, ReadOnly: true));
}
