using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>What one account sync did; logged by the app (no URLs, names or credentials).</summary>
public sealed record SyncSummary(int Collections, int Unchanged, int FullResyncs, int Fetched, int Removed, int KeptLocal, int Unreadable);

/// <summary>
/// Pull-only Radicale sync (sync-radicale R1-R4, R9; interim: no writes). For each distinct collection URL:
/// quick-check the sync-token, run sync-collection from the stored token (from scratch if the server rejects it),
/// fetch changed resources in batches of 100, and apply them to the store.
/// </summary>
/// <param name="http">A client that does not follow redirects itself (see <see cref="DavTransport"/>).</param>
public sealed class RadicaleSync(LocalStore store, HttpClient http, TimeProvider? time = null)
{
    public const int BatchSize = 100;
    private const string TokenKey = "sync-token";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Syncs one account. Throws <see cref="CalDavException"/> on transport errors (e.g. WrongCredentials);
    /// collections done before the error keep their progress.
    /// </summary>
    public async Task<SyncSummary> SyncAccountAsync(Account account, string password, CancellationToken cancellationToken = default)
    {
        var client = new CalDavClient(http, account.Username ?? "", password);
        var summary = new SyncSummary(0, 0, 0, 0, 0, 0, 0);
        foreach (var group in store.GetCollections(account.Id).GroupBy(c => c.RemoteRef))
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary = Add(summary, await SyncCollectionAsync(client, new Uri(group.Key), group.ToList(), cancellationToken));
        }
        return summary;
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

        int fetched = 0, kept = 0, unreadable = 0, removed = 0;
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
                if (store.ApplyServerItem(ids, mapped with { Id = Guid.NewGuid().ToString("N"), CollectionId = target.Id }))
                {
                    fetched++;
                }
                else
                {
                    kept++;
                }
            }
        }

        // From scratch, anything not listed is gone from the server (R3).
        var gone = full
            ? local.Keys.Except(changes.Changed.Select(c => c.Href)).Concat(changes.Removed).Distinct()
            : changes.Removed;
        foreach (var href in gone)
        {
            if (store.RemoveServerItem(ids, href))
            {
                removed++;
            }
            else
            {
                kept++;
            }
        }

        if (changes.NewToken is not null)
        {
            store.SetSyncState(stateId, TokenKey, changes.NewToken);
        }
        return new SyncSummary(1, 0, full ? 1 : 0, fetched, removed, kept, unreadable);
    }

    private static SyncSummary Add(SyncSummary a, SyncSummary b) => new(
        a.Collections + b.Collections, a.Unchanged + b.Unchanged, a.FullResyncs + b.FullResyncs, a.Fetched + b.Fetched,
        a.Removed + b.Removed, a.KeptLocal + b.KeptLocal, a.Unreadable + b.Unreadable);
}
