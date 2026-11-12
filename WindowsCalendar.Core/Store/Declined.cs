using System.Text.Json;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Whether you declined an invitation (attendees-invitations R4): Graph <c>responseStatus.response = declined</c>
/// when you are not the organiser; CalDAV <c>PARTSTAT=DECLINED</c> on the attendee with your address. Stored as a
/// flag so the clash and gap check can ignore it (schedule-gap-warning R2).
/// </summary>
public static class Declined
{
    /// <summary>From a stored payload (Graph JSON or iCalendar); <paramref name="email"/> is the account's address.</summary>
    public static bool FromPayload(string payload, string? email)
    {
        // Most events hold no "declined" at all; skip parsing them.
        if (payload.IndexOf("declined", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        try
        {
            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                var ev = doc.RootElement;
                return ev.TryGetProperty("isOrganizer", out var o) && o.ValueKind == JsonValueKind.False
                    && ev.TryGetProperty("responseStatus", out var r) && r.ValueKind == JsonValueKind.Object
                    && r.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String
                    && string.Equals(response.GetString(), "declined", StringComparison.OrdinalIgnoreCase);
            }
            if (string.IsNullOrWhiteSpace(email) || Ical.Net.Calendar.Load(payload) is not { Events.Count: > 0 } calendar)
            {
                return false;
            }
            var mine = "mailto:" + email.Trim();
            return CalDav.CalDavEvents.Master(calendar).Attendees.Any(a =>
                string.Equals(a.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Value?.OriginalString, mine, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
