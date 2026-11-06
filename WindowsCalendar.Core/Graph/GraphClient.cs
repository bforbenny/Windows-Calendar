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
/// Calls to Microsoft Graph v1.0 for one sync run with an access token that the caller obtained for this run only
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
        using var request = Request(HttpMethod.Get, url);
        request.Headers.Add("Prefer", "odata.maxpagesize=50");
        using var response = await SendRawAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        var status = response.StatusCode;
        if (status == HttpStatusCode.Gone || await ErrorCodeAsync(response, cancellationToken) is "syncStateNotFound" or "resyncRequired")
        {
            throw new GraphException(GraphError.SyncStateGone, status);
        }
        throw new GraphException(GraphError.Http, status);
    }

    /// <summary>
    /// A write (sync-ms365 R7): POST, PATCH or DELETE of <paramref name="path"/> under v1.0 with an optional JSON body
    /// and <c>If-Match</c> etag. Returns the status and, on success, the reply's etag and id. Throws only for
    /// Network, Unauthorized and Throttled; other statuses are for the caller (412, 404, 4xx, 5xx).
    /// </summary>
    public async Task<(HttpStatusCode Status, string? Etag, string? Id)> SendAsync(HttpMethod method, string path, string? json,
        string? ifMatch, CancellationToken cancellationToken)
    {
        using var request = Request(method, BaseUrl + path);
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        using var response = await SendRawAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
        {
            return (response.StatusCode, null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return (response.StatusCode, Text(doc.RootElement, "@odata.etag"), Text(doc.RootElement, "id"));
        }
        catch (JsonException)
        {
            return (response.StatusCode, null, null);
        }
    }

    /// <summary>Times in UTC (R8) and bodies as plain text (sync-ms365 second slice).</summary>
    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        return request;
    }

    /// <summary>Sends; throws for no response, 401 and 429/503 (R10, R11). The caller disposes the response.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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
        var status = response.StatusCode;
        if (status == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new GraphException(GraphError.Unauthorized, status);
        }
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var wait = response.Headers.RetryAfter is { } r
                ? r.Delta ?? (r.Date is { } d ? d - DateTimeOffset.UtcNow : null)
                : null;
            response.Dispose();
            throw new GraphException(GraphError.Throttled, status, wait is { } w && w > TimeSpan.Zero ? w : DefaultRetryAfter);
        }
        return response;
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
