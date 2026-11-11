using System.Net;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>What one account sync did; logged by the app (no URLs, names or credentials).</summary>
public sealed record SyncSummary(
    int Collections, int Unchanged, int FullResyncs, int Fetched, int Removed, int KeptLocal, int Unreadable,
    int Sent = 0, int Conflicts = 0, int Failed = 0, int CalendarListChanges = 0, int FeedItems = 0, int FeedFailures = 0)
{
    /// <summary>Whether anything the views show may have changed.</summary>
    public bool AnyChange => Fetched + Removed + Sent + Conflicts + CalendarListChanges + FeedItems > 0;

    public static readonly SyncSummary Empty = new(0, 0, 0, 0, 0, 0, 0);

    public static SyncSummary operator +(SyncSummary a, SyncSummary b) => new(
        a.Collections + b.Collections, a.Unchanged + b.Unchanged, a.FullResyncs + b.FullResyncs, a.Fetched + b.Fetched,
        a.Removed + b.Removed, a.KeptLocal + b.KeptLocal, a.Unreadable + b.Unreadable,
        a.Sent + b.Sent, a.Conflicts + b.Conflicts, a.Failed + b.Failed,
        a.CalendarListChanges + b.CalendarListChanges, a.FeedItems + b.FeedItems, a.FeedFailures + b.FeedFailures);

    /// <summary>Collections of one account pulled at the same time (local-store-offline R10; Graph allows 4 per mailbox).</summary>
    public const int MaxParallel = 4;
}

/// <summary>
/// Two-way Radicale sync (sync-radicale R1-R4, R6-R9). First the account's queued local changes are sent
/// (PUT / DELETE with etag preconditions); then, for each distinct collection URL, the sync-token is quick-checked,
/// sync-collection runs from the stored token (from scratch if the server rejects it), changed resources are fetched
/// in batches of 100 and applied to the store. A 412 or a server change to an item with unsent changes makes the
/// server version win, with the local version kept in Conflicts (conflict-resolution R1-R4).
/// </summary>
/// <param name="http">A client that does not follow redirects itself (see <see cref="DavTransport"/>).</param>
public sealed class RadicaleSync(LocalStore store, HttpClient http, TimeProvider? time = null)
{
    public const int BatchSize = 100;
    private const string TokenKey = "sync-token";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>A collection's pull started (true) or ended (false), for the sidebar indicator (ui-states R1). Raised off the UI thread.</summary>
    public event Action<string, bool>? CollectionSyncing;

    /// <summary>
    /// Syncs one account: re-reads its collection list (account-management R11), sends queued changes, pulls each
    /// CalDAV collection, then refreshes subscribed feeds (hourly, or now when <paramref name="forceFeeds"/>).
    /// Throws <see cref="CalDavException"/> on Radicale transport errors (e.g. WrongCredentials); work done before
    /// the error keeps its progress. A failing feed is counted, not thrown. Only collections <paramref name="pull"/>
    /// accepts are pulled (the app pulls all, shown or hidden: local-store-offline R10); queued changes are sent for all.
    /// </summary>
    public async Task<SyncSummary> SyncAccountAsync(Account account, string password, bool forceFeeds = false,
        Func<Collection, bool>? pull = null, CancellationToken cancellationToken = default)
    {
        pull ??= _ => true;
        var listChanges = 0;
        if (account.ServerUrl is { } server)
        {
            IReadOnlyList<DiscoveredCollection> found;
            try
            {
                found = await new CalDavDiscovery(http).DiscoverAsync(new Uri(server), account.Username ?? "", password, cancellationToken);
            }
            catch (CalDavException ex) when (ex.Error == CalDavError.NoCalendars)
            {
                found = []; // Removes nothing (R11).
            }
            var changes = store.SyncCollections(account.Id, found);
            listChanges = changes.Added + changes.Updated + changes.Removed;
        }

        var client = new CalDavClient(http, account.Username ?? "", password);
        var all = store.GetCollections(account.Id);
        var collections = all.Where(c => c.SourceUrl is null).ToList();
        var summary = await PushAsync(client, collections, cancellationToken) with { CalendarListChanges = listChanges };

        // Once the queued changes are out, collection URLs are pulled in parallel (local-store-offline R10).
        var groups = collections.GroupBy(c => c.RemoteRef).Select(g => g.ToList()).Where(g => g.Any(pull)).ToList();
        var results = new SyncSummary[groups.Count];
        await ForEachAsync(groups.Count, cancellationToken, async (i, token) =>
            results[i] = await Syncing(groups[i], () => SyncCollectionAsync(client, new Uri(groups[i][0].RemoteRef), groups[i], token)));
        summary = results.Aggregate(summary, (a, b) => a + b);

        // Feeds (sync-radicale R1a): fetched without the Radicale credentials, also in parallel.
        var feeds = new FeedSync(store, http, _time);
        var feedList = all.Where(c => c.SourceUrl is not null && pull(c)).ToList();
        int feedItems = 0, feedFailures = 0;
        await ForEachAsync(feedList.Count, cancellationToken, async (i, token) =>
        {
            try
            {
                var items = await Syncing([feedList[i]], () => feeds.RefreshAsync(feedList[i], forceFeeds, token));
                Interlocked.Add(ref feedItems, items);
            }
            catch (CalDavException)
            {
                Interlocked.Increment(ref feedFailures);
            }
        });
        return summary with { FeedItems = feedItems, FeedFailures = feedFailures };
    }

    private static Task ForEachAsync(int count, CancellationToken cancellationToken, Func<int, CancellationToken, ValueTask> body) =>
        Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = SyncSummary.MaxParallel, CancellationToken = cancellationToken }, body);

    /// <summary>Runs one pull with its collections marked as syncing.</summary>
    private async Task<T> Syncing<T>(IReadOnlyList<Collection> group, Func<Task<T>> pull)
    {
        foreach (var c in group)
        {
            CollectionSyncing?.Invoke(c.Id, true);
        }
        try
        {
            return await pull();
        }
        finally
        {
            foreach (var c in group)
            {
                CollectionSyncing?.Invoke(c.Id, false);
            }
        }
    }

    /// <summary>
    /// Sends the due outbox entries of this account's collections, in replay order, until none are due (R6).
    /// Each completed entry can make the next one for the same item or group due.
    /// </summary>
    private async Task<SyncSummary> PushAsync(CalDavClient client, IReadOnlyList<Collection> collections, CancellationToken cancellationToken)
    {
        var byId = collections.ToDictionary(c => c.Id);
        int sent = 0, conflicts = 0, failed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = store.GetDueOutbox()
                .Select(e => (Entry: e, Item: store.GetItem(e.ItemId, includeDeleted: true)))
                .FirstOrDefault(x => x.Item is { RemoteId: not null } && byId.ContainsKey(x.Item.CollectionId));
            if (next.Item is not { RemoteId: { } href } item)
            {
                break;
            }
            var entry = next.Entry;
            store.MarkInFlight(entry.Id);

            HttpStatusCode status;
            string? etag = null;
            try
            {
                if (entry.Operation == OutboxOperation.Delete)
                {
                    status = await client.DeleteAsync(new Uri(href), entry.BaseEtag, cancellationToken);
                }
                else
                {
                    (status, etag) = await client.PutAsync(new Uri(href), entry.Payload!, entry.BaseEtag,
                        create: entry.Operation == OutboxOperation.Create, cancellationToken);
                }
            }
            catch (CalDavException ex)
            {
                // Unreachable, untrusted certificate or 401: try again later; the account stops here.
                store.RecordOutboxFailure(entry.Id, ex.Error.ToString());
                throw;
            }

            if ((int)status is >= 200 and < 300 || entry.Operation == OutboxOperation.Delete && status == HttpStatusCode.NotFound)
            {
                if (entry.Operation == OutboxOperation.Delete)
                {
                    store.CompleteOutboxEntry(entry.Id);
                }
                else
                {
                    store.CompleteOutboxWrite(entry.Id, etag);
                }
                sent++;
            }
            else if (status is HttpStatusCode.PreconditionFailed || status is HttpStatusCode.NotFound)
            {
                // The server changed (412) or removed (404) the item since the base version: the server wins,
                // unless both sides deleted it.
                var server = await FetchAsync(client, byId[item.CollectionId], href, cancellationToken);
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
                // 5xx, 408 and 429 are retried with backoff; any other 4xx (incl. 409) waits for the user (local-store-offline R7).
                var transient = (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
                store.RecordOutboxFailure(entry.Id, $"HTTP {(int)status}", permanent: !transient);
                failed++;
            }
        }
        return new SyncSummary(0, 0, 0, 0, 0, 0, 0, sent, conflicts, failed);
    }

    /// <summary>The server's current version of one resource, or null if it has none.</summary>
    private async Task<Item?> FetchAsync(CalDavClient client, Collection collection, string href, CancellationToken cancellationToken)
    {
        var found = await client.MultigetAsync(new Uri(collection.RemoteRef), [href], cancellationToken);
        return found.Count == 1 && CalDavItemMapper.Map(found[0].Href, found[0].Etag, found[0].Data, _time.LocalTimeZone) is { } item
            ? item with { CollectionId = collection.Id }
            : null;
    }

    private async Task<SyncSummary> SyncCollectionAsync(CalDavClient client, Uri url, IReadOnlyList<Collection> group,
        CancellationToken cancellationToken)
    {
        // State lives on the first collection for this URL (R1).
        var stateId = group[0].Id;
        var ids = group.Select(c => c.Id).ToList();
        var token = store.GetSyncState(stateId, TokenKey);

        // R4 quick check.
        if (token is not null && await client.GetSyncTokenAsync(url, cancellationToken) == token)
        {
            return new SyncSummary(1, 1, 0, 0, 0, 0, 0);
        }

        // R2, with the R3 fallback: a rejected token means starting again from no token.
        var full = token is null;
        var changes = await client.SyncCollectionAsync(url, token, cancellationToken);
        if (changes is null)
        {
            full = true;
            changes = await client.SyncCollectionAsync(url, null, cancellationToken)
                ?? throw new CalDavException(CalDavError.NotCalDav);
        }

        var local = store.GetRemoteEtags(ids);
        var toFetch = changes.Changed
            .Where(c => !local.TryGetValue(c.Href, out var etag) || etag != c.Etag)
            .Select(c => c.Href)
            .ToList();

        int fetched = 0, kept = 0, unreadable = 0, removed = 0, conflicts = 0;
        void Count(ServerChangeResult result, ref int applied)
        {
            switch (result)
            {
                case ServerChangeResult.Applied: applied++; break;
                case ServerChangeResult.Conflict: conflicts++; break;
                default: kept++; break;
            }
        }

        var zone = _time.LocalTimeZone;
        foreach (var batch in toFetch.Chunk(BatchSize))
        {
            foreach (var (href, etag, data) in await client.MultigetAsync(url, batch, cancellationToken))
            {
                var mapped = CalDavItemMapper.Map(href, etag, data, zone);
                // Events go to the Calendar collection, tasks to the TaskList one (R1).
                var target = mapped is null ? null : group.FirstOrDefault(c => c.Kind ==
                    (mapped.Kind == ItemKind.Event ? CollectionKind.Calendar : CollectionKind.TaskList));
                if (mapped is null || target is null)
                {
                    unreadable++;
                    continue;
                }
                Count(store.ApplyServerItem(ids, mapped with { Id = Guid.NewGuid().ToString("N"), CollectionId = target.Id }), ref fetched);
            }
        }

        // From scratch, anything not listed is gone from the server (R3).
        var gone = full
            ? local.Keys.Except(changes.Changed.Select(c => c.Href)).Concat(changes.Removed).Distinct()
            : changes.Removed;
        foreach (var href in gone)
        {
            Count(store.RemoveServerItem(ids, href), ref removed);
        }

        if (changes.NewToken is not null)
        {
            store.SetSyncState(stateId, TokenKey, changes.NewToken);
        }
        return new SyncSummary(1, 0, full ? 1 : 0, fetched, removed, kept, unreadable, Conflicts: conflicts);
    }
}
