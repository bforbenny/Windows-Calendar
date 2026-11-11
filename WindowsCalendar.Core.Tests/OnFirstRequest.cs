namespace WindowsCalendar.Core.Tests;

/// <summary>Runs <paramref name="act"/> once, before the first request that <paramref name="when"/> accepts is sent on.</summary>
internal sealed class OnFirstRequest(HttpMessageHandler inner, Func<HttpRequestMessage, bool> when, Action act) : DelegatingHandler(inner)
{
    private int _done;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (when(request) && Interlocked.Exchange(ref _done, 1) == 0)
        {
            act();
        }
        return base.SendAsync(request, cancellationToken);
    }
}
