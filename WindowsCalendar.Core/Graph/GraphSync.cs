using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Two-way sync of an MS365 account's calendars (sync-ms365 R1–R4, R6–R8, R12). The calendar list is re-read first
/// (account-management R11), then queued changes are sent, then each calendar is pulled. Series masters referenced
/// by occurrences are kept; any not returned by delta are fetched by id. Each calendar's -3/+12 month window is covered by fixed 30-day chunks,
/// each pulled with its own calendarView delta. Each chunk remembers the event ids it returned; an event leaves the
/// store only when no chunk of its calendar holds it. A chunk that pages too long, repeats a page or loses its delta
/// state restarts once from a fresh delta (R3). Throws <see cref="GraphException"/> for Unauthorized and Throttled
/// (the caller pauses or backs off the account) and for errors before any calendar was pulled.
/// </summary>
public sealed class GraphSync(LocalStore store, HttpClient http, TimeProvider? time = null)
{
    public const int ChunkDays = 30;
    public const int MaxPages = 200;
    private const string StateKey = "graph-chunks";

    /// <summary>Chunks start on multiples of 30 days from this date, so they stay the same from day to day.</summary>
    private static readonly DateOnly Epoch = new(2020, 1, 1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record ChunkState(string? Delta, HashSet<string> Ids);

    /// <summary>A chunk restarted from a fresh delta (R3): calendar's local id, chunk start, reason. For the log.</summary>
    public event Action<string, string, string>? ChunkRestarted;

    /// <summary>Series masters fetched by id for a calendar (R6): calendar's local id, count. For the log.</summary>
    public event Action<string, int>? MastersFetched;

    /// <summary>A calendar's pull started (true) or ended (false), for the sidebar indicator (ui-states R1). Raised off the UI thread.</summary>
    public event Action<string, bool>? CollectionSyncing;

    /// <summary>
    /// Syncs one account with an access token obtained for this run. Only calendars <paramref name="pull"/> accepts are
    /// pulled (hidden ones are not, local-store-offline R10); queued changes are sent for all.
    /// </summary>
    public async Task<SyncSummary> SyncAccountAsync(Account account, string accessToken, Func<Collection, bool>? pull = null,
        CancellationToken cancellationToken = default)
    {
        var client = new GraphClient(http, accessToken);
        var changes = store.SyncCollections(account.Id, await DiscoverAsync(client, cancellationToken));
        var calendars = store.GetCollections(account.Id).Where(c => c.Kind == CollectionKind.Calendar).ToList();
        var (sent, conflicts, failed) = await PushAsync(client, calendars, cancellationToken);
        calendars = pull is null ? calendars : calendars.Where(pull).ToList();
        var summary = new SyncSummary(calendars.Count, 0, 0, 0, 0, 0, 0, sent, conflicts, failed,
            CalendarListChanges: changes.Added + changes.Updated + changes.Removed);
        // Calendars are pulled in parallel once the queued changes are out (local-store-offline R10).
        var results = new SyncSummary[calendars.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, calendars.Count),
            new ParallelOptions { MaxDegreeOfParallelism = SyncSummary.MaxParallel, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                CollectionSyncing?.Invoke(calendars[i].Id, true);
                try
                {
                    results[i] = await SyncCalendarAsync(client, calendars[i], SyncSummary.Empty, token);
                }
                finally
                {
                    CollectionSyncing?.Invoke(calendars[i].Id, false);
                }
            });
        return results.Aggregate(summary, (a, b) => a + b);
    }

    /// <summary>The account's calendars (R1): <c>canEdit</c> false is read-only, <c>isDefaultCalendar</c> the default.</summary>
    public static async Task<IReadOnlyList<DiscoveredCollection>> DiscoverAsync(GraphClient client, CancellationToken cancellationToken)
    {
        var found = new List<DiscoveredCollection>();
        string? url = "/me/calendars?$select=id,name,canEdit,isDefaultCalendar";
        while (url is not null)
        {
            using var doc = await client.GetAsync(url, cancellationToken);
            foreach (var c in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (GraphClient.Text(c, "id") is { } id)
                {
                    found.Add(new DiscoveredCollection(id, GraphClient.Text(c, "name") ?? "Calendar", CollectionKind.Calendar,
                        ReadOnly: !(c.TryGetProperty("canEdit", out var e) && e.ValueKind == JsonValueKind.True),
                        IsDefault: c.TryGetProperty("isDefaultCalendar", out var d) && d.ValueKind == JsonValueKind.True));
                }
            }
            url = GraphClient.Text(doc.RootElement, "@odata.nextLink");
        }
        return found;
    }

    /// <summary>The chunk starts covering the window around <paramref name="today"/> (R2, R4).</summary>
    internal static IReadOnlyList<DateOnly> Chunks(DateOnly today)
    {
        var from = today.AddMonths(-3);
        var to = today.AddMonths(12);
        var first = Epoch.AddDays((from.DayNumber - Epoch.DayNumber) / ChunkDays * ChunkDays);
        var chunks = new List<DateOnly>();
        for (var c = first; c < to; c = c.AddDays(ChunkDays))
        {
            chunks.Add(c);
        }
        return chunks;
    }

    private async Task<SyncSummary> SyncCalendarAsync(GraphClient client, Collection calendar, SyncSummary summary,
        CancellationToken cancellationToken)
    {
        var state = Load(store.GetSyncState(calendar.Id, StateKey));
        var wanted = Chunks(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime));
        var candidates = new HashSet<string>();

        // R4: chunks that left the window go, with their events unless another chunk holds them.
        foreach (var key in state.Keys.Where(k => !wanted.Any(w => Key(w) == k)).ToList())
        {
            candidates.UnionWith(state[key].Ids);
            state.Remove(key);
        }

        int fetched = 0, restarts = 0, unchanged = 0;
        foreach (var chunk in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(chunk);
            state.TryGetValue(key, out var old);
            // Series masters referenced by this chunk's occurrences.
            var series = new HashSet<string>();
            try
            {
                var (next, applied, gone, restarted) = await PullChunkAsync(client, calendar, chunk, old, series, cancellationToken);
                applied += await KeepMastersAsync(client, calendar, series, next.Ids, gone, cancellationToken);
                state[key] = next;
                candidates.UnionWith(gone);
                fetched += applied;
                restarts += restarted ? 1 : 0;
                unchanged += applied == 0 && gone.Count == 0 ? 1 : 0;
            }
            catch (GraphException ex) when (ex.Error is GraphError.Http or GraphError.SyncStateGone)
            {
                // The chunk starts afresh next time; its ids stay until then.
                state[key] = new ChunkState(null, old?.Ids ?? []);
            }
            store.SetSyncState(calendar.Id, StateKey, Save(state));
        }

        var held = state.Values.SelectMany(s => s.Ids).ToHashSet();
        var removed = 0;
        foreach (var id in candidates.Where(id => !held.Contains(id)))
        {
            if (store.RemoveServerItem([calendar.Id], id) == ServerChangeResult.Applied)
            {
                removed++;
            }
        }
        return summary with
        {
            Fetched = summary.Fetched + fetched,
            Removed = summary.Removed + removed,
            FullResyncs = summary.FullResyncs + restarts,
            Unchanged = summary.Unchanged + unchanged,
        };
    }

    /// <summary>
    /// One delta round of a chunk: from its deltaLink, or from scratch. Returns the new state, the number of events
    /// applied, the ids the chunk no longer holds, and whether it had to restart (R3).
    /// </summary>
    private async Task<(ChunkState State, int Applied, HashSet<string> Gone, bool Restarted)> PullChunkAsync(
        GraphClient client, Collection calendar, DateOnly chunk, ChunkState? old, HashSet<string> seriesIds,
        CancellationToken cancellationToken)
    {
        var restarted = false;
        while (true)
        {
            var fresh = old?.Delta is null;
            var ids = fresh ? new HashSet<string>() : new HashSet<string>(old!.Ids);
            var gone = new HashSet<string>();
            var applied = 0;
            string? url = fresh ? InitialUrl(calendar, chunk) : old!.Delta;
            string? previous = null;
            var pages = 0;
            string? reason = null;
            string? deltaLink = null;
            try
            {
                while (url is not null)
                {
                    if (++pages > MaxPages)
                    {
                        reason = "too many pages";
                        break;
                    }
                    using var doc = await client.GetAsync(url, cancellationToken);
                    var root = doc.RootElement;
                    var value = root.TryGetProperty("value", out var v) ? v : default;
                    var hash = Hash(value.ValueKind == JsonValueKind.Array ? value.GetRawText() : "");
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 && hash == previous)
                    {
                        reason = "repeated page";
                        break;
                    }
                    previous = hash;
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ev in value.EnumerateArray())
                        {
                            if (GraphClient.Text(ev, "id") is not { } id)
                            {
                                continue;
                            }
                            var item = ev.TryGetProperty("@removed", out _) ? null : GraphEventMapper.Map(ev, calendar.Id, _time.LocalTimeZone);
                            if (item is null)
                            {
                                ids.Remove(id);
                                gone.Add(id);
                                continue;
                            }
                            ids.Add(id);
                            gone.Remove(id);
                            if (item.SeriesId is { } series)
                            {
                                seriesIds.Add(series);
                            }
                            // The echo of the app's own write keeps the local version (and its Undo).
                            if (store.FindItemByRemoteId(calendar.Id, id) is { } local && local.Etag == item.Etag
                                && local.RawPayload.Contains(GraphEvents.ChangedKey))
                            {
                                continue;
                            }
                            store.ApplyServerItem([calendar.Id], item);
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

            if (reason is null)
            {
                if (fresh && old is not null)
                {
                    // A fresh round lists everything: what it did not return is gone from this chunk.
                    gone.UnionWith(old.Ids.Where(id => !ids.Contains(id)));
                }
                return (new ChunkState(deltaLink, ids), applied, gone, restarted);
            }
            if (restarted)
            {
                // At most one restart per run (no tight retry loop); the next run starts afresh.
                throw new GraphException(GraphError.SyncStateGone);
            }
            ChunkRestarted?.Invoke(calendar.Id, Key(chunk), reason);
            restarted = true;
            old = new ChunkState(null, old?.Ids ?? []);
        }
    }

    /// <summary>
    /// After a chunk's round: the masters its occurrences reference stay held by the chunk, and any not stored yet are
    /// fetched by id (R6). Returns how many were fetched.
    /// </summary>
    private async Task<int> KeepMastersAsync(GraphClient client, Collection calendar, HashSet<string> seriesIds,
        HashSet<string> ids, HashSet<string> gone, CancellationToken cancellationToken)
    {
        var fetched = 0;
        foreach (var series in seriesIds)
        {
            ids.Add(series);
            gone.Remove(series);
            if (store.FindItemByRemoteId(calendar.Id, series) is not null)
            {
                continue;
            }
            if (await FetchAsync(client, calendar, series, cancellationToken) is { } master)
            {
                store.ApplyServerItem([calendar.Id], master);
                fetched++;
            }
        }
        if (fetched > 0)
        {
            MastersFetched?.Invoke(calendar.Id, fetched);
        }
        return fetched;
    }

    /// <summary>The server's current version of one event, or null if it has none.</summary>
    private async Task<Item?> FetchAsync(GraphClient client, Collection calendar, string remoteId, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await client.GetAsync($"/me/events/{Uri.EscapeDataString(remoteId)}", cancellationToken);
            return GraphEventMapper.Map(doc.RootElement, calendar.Id, _time.LocalTimeZone);
        }
        catch (GraphException ex) when (ex.Error == GraphError.Http && ex.Status is HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the due outbox entries of these calendars in replay order (R7): POST to the calendar, PATCH of the changed
    /// fields or DELETE, with If-Match. 412 or 404: the server version wins (conflict-resolution). Network, 401 and
    /// throttling stop the account (thrown); 5xx and 408 retry later; other 4xx wait for the user.
    /// </summary>
    private async Task<(int Sent, int Conflicts, int Failed)> PushAsync(GraphClient client, IReadOnlyList<Collection> calendars,
        CancellationToken cancellationToken)
    {
        var byId = calendars.ToDictionary(c => c.Id);
        int sent = 0, conflicts = 0, failed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = store.GetDueOutbox()
                .Select(e => (Entry: e, Item: store.GetItem(e.ItemId, includeDeleted: true)))
                .FirstOrDefault(x => x.Item is { } i && byId.ContainsKey(i.CollectionId)
                    && (i.RemoteId is not null || x.Entry.Operation == OutboxOperation.Create));
            if (next.Item is not { } item)
            {
                break;
            }
            var entry = next.Entry;
            var calendar = byId[item.CollectionId];
            store.MarkInFlight(entry.Id);

            HttpStatusCode status;
            string? etag, id;
            try
            {
                var path = $"/me/events/{Uri.EscapeDataString(item.RemoteId ?? "")}";
                (status, etag, id) = entry.Operation switch
                {
                    OutboxOperation.Create => await client.SendAsync(HttpMethod.Post,
                        $"/me/calendars/{Uri.EscapeDataString(calendar.RemoteRef)}/events", GraphEvents.CreateBody(entry.Payload!), null, cancellationToken),
                    OutboxOperation.Update => await client.SendAsync(HttpMethod.Patch, path, GraphEvents.PatchBody(entry.Payload!), entry.BaseEtag, cancellationToken),
                    _ => await client.SendAsync(HttpMethod.Delete, path, null, entry.BaseEtag, cancellationToken),
                };
            }
            catch (GraphException ex)
            {
                store.RecordOutboxFailure(entry.Id, ex.Error.ToString());
                throw;
            }

            if ((int)status is >= 200 and < 300 || entry.Operation == OutboxOperation.Delete && status == HttpStatusCode.NotFound)
            {
                switch (entry.Operation)
                {
                    case OutboxOperation.Delete:
                        store.CompleteOutboxEntry(entry.Id);
                        break;
                    case OutboxOperation.Create:
                        store.CompleteOutboxWrite(entry.Id, etag, id);
                        break;
                    default:
                        store.CompleteOutboxWrite(entry.Id, etag);
                        break;
                }
                sent++;
            }
            else if (status is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound && item.RemoteId is { } remoteId)
            {
                var server = await FetchAsync(client, calendar, remoteId, cancellationToken);
                if (server is null && entry.Operation == OutboxOperation.Delete)
                {
                    store.CompleteOutboxEntry(entry.Id);
                    sent++;
                }
                else
                {
                    store.ResolveConflict(entry.Id, server);
                    conflicts++;
                }
            }
            else
            {
                var transient = (int)status >= 500 || status == HttpStatusCode.RequestTimeout;
                store.RecordOutboxFailure(entry.Id, $"HTTP {(int)status}", permanent: !transient);
                failed++;
            }
        }
        return (sent, conflicts, failed);
    }

    private static string InitialUrl(Collection calendar, DateOnly chunk)
    {
        var start = chunk.ToDateTime(TimeOnly.MinValue);
        var end = chunk.AddDays(ChunkDays).ToDateTime(TimeOnly.MinValue);
        return $"/me/calendars/{Uri.EscapeDataString(calendar.RemoteRef)}/calendarView/delta"
            + $"?startDateTime={start:yyyy-MM-ddTHH:mm:ss}Z&endDateTime={end:yyyy-MM-ddTHH:mm:ss}Z";
    }

    private static string Key(DateOnly chunk) => chunk.ToString("yyyy-MM-dd");

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Dictionary<string, ChunkState> Load(string? json)
    {
        if (json is null)
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ChunkState>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Save(Dictionary<string, ChunkState> state) => JsonSerializer.Serialize(state);
}
