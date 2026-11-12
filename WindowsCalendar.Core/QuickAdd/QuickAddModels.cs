using WindowsCalendar.Core.CalDav;

namespace WindowsCalendar.Core.QuickAdd;

public enum QuickAddKind { Event, Task }

/// <summary>The parts of a phrase the parser recognised; <see cref="QuickAddResult.Spans"/> is keyed by these (quick-add R7).</summary>
public enum QuickAddField { Kind, Date, Time, Duration, AllDay, Location, Target, Repeat }

/// <summary>A writable calendar or task list the phrase can land in, in sidebar order (quick-add R2, R4).</summary>
public sealed record QuickAddTarget(string Id, string Name, string AccountName, bool Ticked, bool IsDefault, bool IsTaskList);

/// <summary>
/// Everything the parser needs besides the phrase (quick-add R5, R7). <see cref="DayFirst"/> is the Windows short-date
/// order. <see cref="SlotStart"/> and <see cref="SlotAllDay"/> pre-set the date and time from a quick-create slot.
/// <see cref="TaskByDefault"/> makes a phrase with no prefix a task (the Tasks view). <see cref="WeekStart"/> is the first
/// day of the week, for "this week" and "next week" (R2 Ranges).
/// </summary>
public sealed record QuickAddOptions(bool DayFirst, IReadOnlyList<QuickAddTarget> Targets, DateTime? SlotStart = null,
    bool SlotAllDay = false, bool TaskByDefault = false, DayOfWeek WeekStart = DayOfWeek.Monday);

/// <summary>
/// What the parser understood (quick-add R7). Times are local. All-day: Start is the date at 00:00 and End the next day.
/// Task: Start.Date is the due date. <see cref="Spans"/> are character ranges in the phrase, including marker words such
/// as "at" or "for". <see cref="Error"/> is null when the result can be saved.
/// </summary>
public sealed record QuickAddResult(
    QuickAddKind Kind, string Title, bool AllDay,
    DateTime Start, DateTime End,
    string? Location, QuickAddTarget? Target, bool TargetHidden,
    RepeatRule Repeat,
    IReadOnlyDictionary<QuickAddField, Range> Spans,
    string? Error);

/// <summary>What a date phrase is, before anyone decides what it means (search-date-chips R1).</summary>
public enum DatePhraseKind { Day, Weekday, Week, Month, MonthName }

/// <summary>
/// A date phrase and its character span in the text. Day: a resolved date (today, tomorrow, next fri, in 3 days, 5 Dec,
/// 5/12) in <see cref="Date"/>; Weekday: a bare weekday name in <see cref="Weekday"/>; Week and Month: this (0), next (+1)
/// or last (-1) in <see cref="Shift"/>; MonthName: a bare month, 1-12 in <see cref="Month"/> (search only).
/// </summary>
public sealed record DatePhrase(DatePhraseKind Kind, Range Span, DateOnly? Date = null, DayOfWeek? Weekday = null,
    int Shift = 0, int? Month = null);
