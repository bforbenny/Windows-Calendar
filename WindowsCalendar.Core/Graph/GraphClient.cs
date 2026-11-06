using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WindowsCalendar.Core.Graph;

public enum GraphError
{
    /// <summary>401: the token was rejected; the account needs sign-in (sync-ms365 R11).</summary>
    Unauthorized,

    /// <summary>429 or 503; wait <see cref="GraphException.RetryAfter"/> (R10).</summary>
    Throttled,

    /// <summary>410 or syncStateNotFound: the delta state expired; restart from a fresh delta (R3).</summary>
    SyncStateGone,

    /// <summary>Any other HTTP error.</summary>
    Http,

    /// <summary>No response (offline, DNS, TLS, timeout).</summary>
    Network,
}

/// <summary>A failed Graph call. The message holds no URLs, tokens or response bodies.</summary>
public sealed class GraphException(GraphError error, HttpStatusCode? status = null, TimeSpan? retryAfter = null)
    : Exception($"Graph request failed: {error}{(status is { } s ? $" ({(int)s})" : "")}")
{
    public GraphError Error { get; } = error;
    public HttpStatusCode? Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// GETs from Microsoft Graph v1.0 for one sync run with an access token that the caller obtained for this run only
/// (sync-ms365 R8, R10, R11). Reads ask for UTC times and pages of 50. The token is never logged.
/// </summary>
public sealed class GraphClient(HttpClient http, string accessToken)
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0";
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The JSON body of <paramref name="pathOrUrl"/>: a path under v1.0, or a nextLink / deltaLink.</summary>
    public async Task<JsonDocument> GetAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        var url = pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : BaseUrl + pathOrUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        request.Headers.Add("Prefer", "odata.maxpagesize=50");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new GraphException(GraphError.Network);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GraphException(GraphError.Network);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            var status = response.StatusCode;
            if (status == HttpStatusCode.Unauthorized)
            {
                throw new GraphException(GraphError.Unauthorized, status);
            }
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = response.Headers.RetryAfter is { } r
                    ? r.Delta ?? (r.Date is { } d ? d - DateTimeOffset.UtcNow : null)
                    : null;
                throw new GraphException(GraphError.Throttled, status, wait is { } w && w > TimeSpan.Zero ? w : DefaultRetryAfter);
            }
            if (status == HttpStatusCode.Gone || await ErrorCodeAsync(response, cancellationToken) is "syncStateNotFound" or "resyncRequired")
            {
                throw new GraphException(GraphError.SyncStateGone, status);
            }
            throw new GraphException(GraphError.Http, status);
        }
    }

    /// <summary>The signed-in user's display name and email (mail, else userPrincipalName).</summary>
    public async Task<(string DisplayName, string Email)> GetMeAsync(CancellationToken cancellationToken)
    {
        using var doc = await GetAsync("/me?$select=displayName,mail,userPrincipalName", cancellationToken);
        var me = doc.RootElement;
        var upn = Text(me, "userPrincipalName") ?? "";
        var email = Text(me, "mail") ?? upn;
        return (Text(me, "displayName") ?? email, email);
    }

    internal static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("error", out var error) ? Text(error, "code") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
