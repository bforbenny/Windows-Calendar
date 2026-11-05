using System.Net;

namespace WindowsCalendar.Core.CalDav;

/// <summary>Why a CalDAV request failed (account-management R4a, sync-radicale R3, R10, R11).</summary>
public enum CalDavError { NotHttps, Unreachable, CertificateNotTrusted, WrongCredentials, NotCalDav, NoCalendars }

/// <summary>
/// A failed CalDAV request. The message never contains the username, the password or the URL.
/// </summary>
public sealed class CalDavException(CalDavError error, HttpStatusCode? status = null, Exception? inner = null)
    : Exception($"CalDAV request failed: {error}{(status is null ? "" : $" (HTTP {(int)status})")}", inner)
{
    public CalDavError Error { get; } = error;
    public HttpStatusCode? Status { get; } = status;
}
