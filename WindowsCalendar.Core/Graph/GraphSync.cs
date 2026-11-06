using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Pull-only sync of an MS365 account's calendars (sync-ms365 slice 1: R1–R4, R8, R12). The calendar list is
/// re-read first (account-management R11). Each calendar's -3/+12 month window is covered by fixed 30-day chunks,
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

    /// <summary>Syncs one account with an access token obtained for this run.</summary>
    public async Task<SyncSummary> SyncAccountAsync(Account account, string accessToken, CancellationToken cancellationToken = default)
    {
        var client = new GraphClient(http, accessToken);
        var changes = store.SyncCollections(account.Id, await DiscoverAsync(client, cancellationToken));
        var calendars = store.GetCollections(account.Id).Where(c => c.Kind == CollectionKind.Calendar).ToList();
        var summary = new SyncSummary(calendars.Count, 0, 0, 0, 0, 0, 0, CalendarListChanges: changes.Added + changes.Updated + changes.Removed);
        foreach (var calendar in calendars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary = await SyncCalendarAsync(client, calendar, summary, cancellationToken);
        }
        return summary;
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
            try
            {
                var (next, applied, gone, restarted) = await PullChunkAsync(client, calendar, chunk, old, cancellationToken);
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
        GraphClient client, Collection calendar, DateOnly chunk, ChunkState? old, CancellationToken cancellationToken)
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
