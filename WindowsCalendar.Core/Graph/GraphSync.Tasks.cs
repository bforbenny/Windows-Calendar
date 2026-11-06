using System.Net;
using System.Text.Json;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Microsoft To Do (sync-ms365 R9, third slice): task lists are discovered with the calendars, and each list is pulled
/// with one tasks delta whose deltaLink and returned ids are kept in the list's sync state. The loop guard of R3 applies.
/// To Do calls do not ask for UTC times, so due dates keep the zone they were written in.
/// </summary>
public sealed partial class GraphSync
{
    private const string ListStateKey = "todo-delta";

    private sealed record ListState(string? Delta, HashSet<string> Ids);

    /// <summary>The task lists could not be read (e.g. an account without To Do); the existing ones are kept. Account id, for the log.</summary>
    public event Action<string>? ListsUnavailable;

    /// <summary>
    /// The account's To Do lists: Flagged emails is read-only. On an HTTP error the account's existing task lists are
    /// returned unchanged, so a failure removes nothing and the calendars still sync.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredCollection>> DiscoverListsAsync(GraphClient client, string accountId,
        CancellationToken cancellationToken)
    {
        var found = new List<DiscoveredCollection>();
        string? url = "/me/todo/lists";
        try
        {
            while (url is not null)
            {
                using var doc = await client.GetAsync(url, cancellationToken, utcTimes: false);
                foreach (var l in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (GraphClient.Text(l, "id") is { } id)
                    {
                        found.Add(new DiscoveredCollection(id, GraphClient.Text(l, "displayName") ?? "Tasks", CollectionKind.TaskList,
                            ReadOnly: GraphClient.Text(l, "wellknownListName") == "flaggedEmails"));
                    }
                }
                url = GraphClient.Text(doc.RootElement, "@odata.nextLink");
            }
            return found;
        }
        catch (GraphException ex) when (ex.Error == GraphError.Http)
        {
            ListsUnavailable?.Invoke(accountId);
            return store.GetCollections(accountId).Where(c => c.Kind == CollectionKind.TaskList)
                .Select(c => new DiscoveredCollection(c.RemoteRef, c.Name, c.Kind, c.ReadOnly)).ToList();
        }
    }

    /// <summary>
    /// One delta round of a list: from its deltaLink, or from scratch. A fresh round removes the tasks it did not
    /// return. Too many pages, a repeated page or an expired delta restarts it once (R3); a second failure, or an HTTP
    /// error, leaves it to start afresh next run.
    /// </summary>
    private async Task<SyncSummary> SyncListAsync(GraphClient client, Collection list, CancellationToken cancellationToken)
    {
        var old = LoadList(store.GetSyncState(list.Id, ListStateKey));
        var restarted = false;
        while (true)
        {
            var fresh = old.Delta is null;
            var ids = fresh ? new HashSet<string>() : new HashSet<string>(old.Ids);
            var gone = new HashSet<string>();
            int applied = 0, pages = 0;
            string? url = fresh ? $"/me/todo/lists/{Uri.EscapeDataString(list.RemoteRef)}/tasks/delta" : old.Delta;
            string? previous = null, reason = null, deltaLink = null;
            try
            {
                while (url is not null)
                {
                    if (++pages > MaxPages)
                    {
                        reason = "too many pages";
                        break;
                    }
                    using var doc = await client.GetAsync(url, cancellationToken, utcTimes: false);
                    var root = doc.RootElement;
                    var value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v : default;
                    var hash = Hash(value.ValueKind == JsonValueKind.Array ? value.GetRawText() : "");
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 && hash == previous)
                    {
                        reason = "repeated page";
                        break;
                    }
                    previous = hash;
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in value.EnumerateArray())
                        {
                            if (GraphClient.Text(t, "id") is not { } id)
                            {
                                continue;
                            }
                            if (t.TryGetProperty("@removed", out _))
                            {
                                ids.Remove(id);
                                gone.Add(id);
                                continue;
                            }
                            ids.Add(id);
                            gone.Remove(id);
                            var item = GraphTaskMapper.Map(t, list.Id, _time.LocalTimeZone)!;
                            // The echo of the app's own write keeps the local version.
                            if (store.FindItemByRemoteId(list.Id, id) is { } local && local.Etag == item.Etag
                                && local.RawPayload.Contains(GraphEvents.ChangedKey))
                            {
                                continue;
                            }
                            store.ApplyServerItem([list.Id], item);
                            applied++;
                        }
                    }
                    url = GraphClient.Text(root, "@odata.nextLink");
                    deltaLink = GraphClient.Text(root, "@odata.deltaLink");
                    if (url is null && deltaLink is null)
                    {
                        reason = "no delta link";
                    }
                }
            }
            catch (GraphException ex) when (ex.Error == GraphError.SyncStateGone && !restarted)
            {
                reason = "delta state expired";
            }
            catch (GraphException ex) when (ex.Error is GraphError.Http or GraphError.SyncStateGone)
            {
                reason = "failed";
                restarted = true;
            }

            if (reason is null)
            {
                if (fresh)
                {
                    gone.UnionWith(old.Ids.Where(id => !ids.Contains(id)));
                }
                store.SetSyncState(list.Id, ListStateKey, JsonSerializer.Serialize(new ListState(deltaLink, ids)));
                var removed = gone.Count(id => store.RemoveServerItem([list.Id], id) == ServerChangeResult.Applied);
                return SyncSummary.Empty with
                {
                    Fetched = applied,
                    Removed = removed,
                    FullResyncs = restarted ? 1 : 0,
                    Unchanged = applied == 0 && gone.Count == 0 ? 1 : 0,
                };
            }
            if (restarted)
            {
                // At most one restart per run (no tight retry loop); the list starts afresh next run, keeping its ids.
                store.SetSyncState(list.Id, ListStateKey, JsonSerializer.Serialize(new ListState(null, old.Ids)));
                return SyncSummary.Empty;
            }
            ChunkRestarted?.Invoke(list.Id, "tasks", reason);
            restarted = true;
            old = old with { Delta = null };
        }
    }

    /// <summary>The server's current version of one task, or null if it has none.</summary>
    private async Task<Item?> FetchTaskAsync(GraphClient client, Collection list, string remoteId, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await client.GetAsync(TaskPath(list, remoteId), cancellationToken, utcTimes: false);
            return GraphTaskMapper.Map(doc.RootElement, list.Id, _time.LocalTimeZone);
        }
        catch (GraphException ex) when (ex.Error == GraphError.Http && ex.Status is HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string TaskPath(Collection list, string remoteId) =>
        $"/me/todo/lists/{Uri.EscapeDataString(list.RemoteRef)}/tasks/{Uri.EscapeDataString(remoteId)}";

    private static ListState LoadList(string? json)
    {
        try
        {
            return json is null ? new ListState(null, []) : JsonSerializer.Deserialize<ListState>(json) ?? new ListState(null, []);
        }
        catch (JsonException)
        {
            return new ListState(null, []);
        }
    }
}
