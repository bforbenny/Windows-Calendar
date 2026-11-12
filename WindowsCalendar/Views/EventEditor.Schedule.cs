using System;
using System.Windows;
using WindowsCalendar.Core.CalDav;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace WindowsCalendar.Views;

/// <summary>
/// The clash and short-gap line under the times (schedule-gap-warning R1, R7): a warning for an overlap, a note for a
/// short gap. It runs only once the times, all-day or show-as differ from those the event was opened with, updates on
/// each change and clears when resolved.
/// </summary>
internal sealed partial class EventEditor
{
    private readonly InfoBar _scheduleNote = new() { IsClosable = false, Margin = new Thickness(0, -4, 0, 0) };
    private string? _openTimes;

    private static string TimesKey(EventFields f)
    {
        var (start, end) = Utc(f);
        return $"{f.AllDay}|{start.UtcTicks}|{end.UtcTicks}|{f.ShowAs}";
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Utc(EventFields f)
    {
        var zone = f.StartZone ?? TimeZoneInfo.Local;
        var endZone = f.EndZone ?? zone;
        DateTimeOffset At(DateTime wall, TimeZoneInfo z) => z.IsInvalidTime(wall) ? default
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), z));
        return (At(f.Start, zone), At(f.End, endZone));
    }

    /// <summary>
    /// The event as opened (or as moved by a drag, whose banner says it): no line until it changes. A new event is
    /// checked from its first save, since creating it counts (R1).
    /// </summary>
    private void ResetScheduleNote(EventFields fields)
    {
        _openTimes = _itemId is null ? null : TimesKey(fields);
        _scheduleNote.IsOpen = false;
    }

    private void UpdateScheduleNote(EventFields fields)
    {
        var note = Store is { } store && TimesKey(fields) != _openTimes
            ? ScheduleWarning.For(store, _occurrenceId ?? _itemId, fields.Start, fields.End, fields.StartZone ?? TimeZoneInfo.Local,
                fields.EndZone ?? fields.StartZone ?? TimeZoneInfo.Local, fields.AllDay, fields.ShowAs)
            : null;
        _scheduleNote.Message = note?.Text ?? "";
        _scheduleNote.Severity = note?.Overlap == true ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        _scheduleNote.IsOpen = note is not null;
    }
}
