using System.Globalization;

namespace WindowsCalendar.Core.Views;

/// <summary>
/// The secondary timezone ruler's text (timezone-handling R8): its name, the hour labels and the offset note.
/// <paramref name="Hours"/>[h] labels the display zone's hour h (index 0 is unused, like the primary ruler's).
/// </summary>
public sealed record RulerLabels(string Name, string[] Hours, string? Note);

public static class SecondaryRuler
{
    /// <summary>
    /// One ruler for the visible days: labels use today's offset when today is visible, else the first visible day's.
    /// When the offset between the zones differs on another visible day (a DST change in either zone), the note says
    /// by how much and from which day ("+1 h from Sun"), or until which day when the differing days come before the
    /// reference ("−1 h until Mon"). Offsets are taken at noon, after the night-time DST changes.
    /// </summary>
    public static RulerLabels Build(TimeZoneInfo zone, TimeZoneInfo display, DateOnly first, int days, DateOnly today,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var reference = today >= first && today < first.AddDays(days) ? today : first;
        var shift = Shift(zone, display, reference);
        var hours = new string[24];
        for (var h = 0; h < 24; h++)
        {
            hours[h] = new DateTime(2000, 1, 2).Add(TimeSpan.FromHours(h) + shift).ToString("t", culture);
        }

        string? note = null;
        // A later day that differs first; else an earlier one.
        for (var d = reference.AddDays(1); d < first.AddDays(days) && note is null; d = d.AddDays(1))
        {
            var delta = Shift(zone, display, d) - shift;
            note = delta == TimeSpan.Zero ? null : $"{Amount(delta)} from {Day(d, culture)}";
        }
        for (var d = reference.AddDays(-1); d >= first && note is null; d = d.AddDays(-1))
        {
            var delta = Shift(zone, display, d) - shift;
            note = delta == TimeSpan.Zero ? null : $"{Amount(delta)} until {Day(d, culture)}";
        }
        return new RulerLabels(TimeZoneLabels.ShortName(zone), hours, note);
    }

    /// <summary>The ruler zone's offset less the display zone's, at noon on <paramref name="day"/>.</summary>
    private static TimeSpan Shift(TimeZoneInfo zone, TimeZoneInfo display, DateOnly day)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), display);
        return zone.GetUtcOffset(utc) - display.GetUtcOffset(utc);
    }

    /// <summary>"+1 h", "−30 min", "+1 h 30 min".</summary>
    private static string Amount(TimeSpan delta)
    {
        var sign = delta < TimeSpan.Zero ? "−" : "+";
        var a = delta.Duration();
        var text = a.Minutes == 0 ? $"{(int)a.TotalHours} h" : a.TotalHours < 1 ? $"{a.Minutes} min" : $"{(int)a.TotalHours} h {a.Minutes} min";
        return sign + text;
    }

    private static string Day(DateOnly day, CultureInfo culture) => culture.DateTimeFormat.AbbreviatedDayNames[(int)day.DayOfWeek];
}
