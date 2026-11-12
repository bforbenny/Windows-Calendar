using System.Text.Json;
using Ical.Net;
using Ical.Net.CalendarComponents;

namespace WindowsCalendar.Core.Store;

/// <summary>
/// Whether a meeting was cancelled (attendees-invitations R5): Graph <c>isCancelled</c>; CalDAV <c>STATUS:CANCELLED</c>
/// on an event that does not repeat (a cancelled occurrence inside a series stays hidden). Stored as a flag so the
/// views fade it and reminders, Join and the clash check skip it.
/// </summary>
public static class Cancelled
{
    /// <summary>From a stored payload (Graph JSON or iCalendar).</summary>
    public static bool FromPayload(string payload)
    {
        // Most events hold no "cancel" at all; skip parsing them.
        if (payload.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }
        try
        {
            if (payload.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(payload);
                return FromGraph(doc.RootElement);
            }
            return Ical.Net.Calendar.Load(payload) is { Events.Count: > 0 } calendar
                && IsCancelledSingle(CalDav.CalDavEvents.Master(calendar));
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool FromGraph(JsonElement ev) =>
        ev.TryGetProperty("isCancelled", out var c) && c.ValueKind == JsonValueKind.True;

    /// <summary>A cancelled CalDAV event that does not repeat and is not one occurrence of a series.</summary>
    internal static bool IsCancelledSingle(CalendarEvent e) =>
        string.Equals(e.Status, EventStatus.Cancelled, StringComparison.OrdinalIgnoreCase)
        && e.RecurrenceIdentifier is null && e.RecurrenceRule is null && !e.RecurrenceDates.GetAllDates().Any();
}
