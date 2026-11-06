namespace WindowsCalendar.Core.Sync;

/// <summary>
/// Decides when sync runs (local-store-offline R10): every 5 minutes, plus whenever <see cref="Request"/> is
/// called (local change, app focus, network back, Sync now). One run at a time; requests made during a run
/// cause exactly one more run after it. <see cref="RequestCollection"/> asks for a run that pulls only the named
/// collections (a calendar just ticked); a full request pending at the same time takes its place.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Func<string, IReadOnlySet<string>?, CancellationToken, Task> _sync;
    private readonly Action<Exception> _onError;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private bool _running;
    private string? _pendingReason;
    // With a pending run: the collections it is limited to, or null for a full run.
    private HashSet<string>? _pendingOnly;

    /// <param name="sync">The sync run; receives the reason it was requested.</param>
    /// <param name="onError">Called when a run throws; the scheduler keeps going.</param>
    public SyncScheduler(Func<string, CancellationToken, Task> sync, Action<Exception> onError, TimeProvider? time = null)
        : this((reason, _, cancellationToken) => sync(reason, cancellationToken), onError, time)
    {
    }

    /// <param name="sync">The sync run; receives the reason and the collections to pull (null: all).</param>
    /// <param name="onError">Called when a run throws; the scheduler keeps going.</param>
    public SyncScheduler(Func<string, IReadOnlySet<string>?, CancellationToken, Task> sync, Action<Exception> onError, TimeProvider? time = null)
    {
        _sync = sync;
        _onError = onError;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Request("timer"), null, Interval, Interval);
    }

    public void Request(string reason) => Request(reason, null);

    /// <summary>A run that pulls only this collection, merged with other such requests pending.</summary>
    public void RequestCollection(string reason, string collectionId) => Request(reason, collectionId);

    private void Request(string reason, string? collectionId)
    {
        lock (_gate)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            if (_running)
            {
                if (_pendingReason is null || collectionId is null)
                {
                    _pendingReason = reason;
                    _pendingOnly = collectionId is null ? null : [collectionId];
                }
                else if (_pendingOnly is not null)
                {
                    _pendingReason = reason;
                    _pendingOnly.Add(collectionId);
                }
                // Otherwise a full run is pending, which covers this collection.
                return;
            }
            _running = true;
        }
        var only = collectionId is null ? null : new HashSet<string> { collectionId };
        _ = Task.Run(() => RunAsync(reason, only));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts.Cancel();
        }
        _timer.Dispose();
    }

    private async Task RunAsync(string reason, HashSet<string>? only)
    {
        while (true)
        {
            try
            {
                await _sync(reason, only, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _onError(ex);
            }

            lock (_gate)
            {
                if (_pendingReason is null || _cts.IsCancellationRequested)
                {
                    _running = false;
                    return;
                }
                (reason, only) = (_pendingReason, _pendingOnly);
                _pendingReason = null;
                _pendingOnly = null;
            }
        }
    }
}
