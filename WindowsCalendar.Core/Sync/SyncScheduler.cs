namespace WindowsCalendar.Core.Sync;

/// <summary>
/// Decides when sync runs (local-store-offline R10): every 5 minutes, plus whenever <see cref="Request"/> is
/// called (local change, app focus, network back, Sync now). One run at a time; requests made during a run
/// cause exactly one more run after it.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Func<string, CancellationToken, Task> _sync;
    private readonly Action<Exception> _onError;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private bool _running;
    private string? _pendingReason;

    /// <param name="sync">The sync run; receives the reason it was requested.</param>
    /// <param name="onError">Called when a run throws; the scheduler keeps going.</param>
    public SyncScheduler(Func<string, CancellationToken, Task> sync, Action<Exception> onError, TimeProvider? time = null)
    {
        _sync = sync;
        _onError = onError;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Request("timer"), null, Interval, Interval);
    }

    public void Request(string reason)
    {
        lock (_gate)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            if (_running)
            {
                _pendingReason = reason;
                return;
            }
            _running = true;
        }
        _ = Task.Run(() => RunAsync(reason));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts.Cancel();
        }
        _timer.Dispose();
    }

    private async Task RunAsync(string reason)
    {
        while (true)
        {
            try
            {
                await _sync(reason, _cts.Token);
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
                reason = _pendingReason;
                _pendingReason = null;
            }
        }
    }
}
