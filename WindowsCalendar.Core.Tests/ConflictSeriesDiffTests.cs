using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

/// <summary>The occurrences "Use mine" would overwrite on a CalDAV repeating series (conflict-resolution R6).</summary>
public sealed class ConflictSeriesDiffTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private const string Master = """
        BEGIN:VEVENT
        UID:w1
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20261102T090000
        DTEND;TZID=Europe/London:20261102T100000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        SUMMARY:Standup

        """;

    private static string Series(string extra = "", string end = "") =>
        ("BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//EN\n" + Master.Replace("SUMMARY:Standup", "SUMMARY:Standup" + extra) + "END:VEVENT\n" + end
            + "END:VCALENDAR\n").Replace("\n", "\r\n");

    private static string Override(string day, string summary) =>
        $"BEGIN:VEVENT\nUID:w1\nDTSTAMP:20260101T000000Z\nRECURRENCE-ID;TZID=Europe/London:{day}T090000\n"
        + $"DTSTART;TZID=Europe/London:{day}T090000\nDTEND;TZID=Europe/London:{day}T100000\nSUMMARY:{summary}\nEND:VEVENT\n";

    private static string Exdate(string day) => $"\nEXDATE;TZID=Europe/London:{day}T090000";

    [Fact]
    public void IdenticalPayloads_GiveNoLines()
    {
        var payload = Series(Exdate("20261116"), Override("20261109", "Early"));
        Assert.Empty(ConflictResolver.SeriesOccurrenceDifferences(payload, payload, London));
    }

    [Fact]
    public void OverrideOnlyLocal_IsYoursChanged()
    {
        var line = Assert.Single(ConflictResolver.SeriesOccurrenceDifferences(Series("", Override("20261109", "Early")), Series(), London));
        Assert.EndsWith(": yours changed \u00b7 server unchanged", line);
        Assert.Contains("09:00", line);
    }

    [Fact]
    public void OverrideOnlyOnServer_IsServerChanged()
    {
        var line = Assert.Single(ConflictResolver.SeriesOccurrenceDifferences(Series(), Series("", Override("20261109", "Early")), London));
        Assert.EndsWith(": yours unchanged \u00b7 server changed", line);
    }

    [Fact]
    public void OverrideChangedOnBothSides_IsBothChanged()
    {
        var line = Assert.Single(ConflictResolver.SeriesOccurrenceDifferences(
            Series("", Override("20261109", "Mine")), Series("", Override("20261109", "Theirs")), London));
        Assert.EndsWith(": yours changed \u00b7 server changed", line);
    }

    [Fact]
    public void ExdateOnlyLocal_IsYoursDeleted()
    {
        var line = Assert.Single(ConflictResolver.SeriesOccurrenceDifferences(Series(Exdate("20261116")), Series(), London));
        Assert.EndsWith(": yours deleted \u00b7 server unchanged", line);
    }

    [Fact]
    public void ExdateOnlyOnServer_IsServerDeleted()
    {
        var line = Assert.Single(ConflictResolver.SeriesOccurrenceDifferences(Series(), Series(Exdate("20261116")), London));
        Assert.EndsWith(": yours unchanged \u00b7 server deleted", line);
    }

    [Fact]
    public void Lines_AreOldestFirst()
    {
        var lines = ConflictResolver.SeriesOccurrenceDifferences(
            Series(Exdate("20261123"), Override("20261109", "Early")), Series(), London);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("yours changed \u00b7 server unchanged", lines[0]);
        Assert.EndsWith("yours deleted \u00b7 server unchanged", lines[1]);
    }
}
