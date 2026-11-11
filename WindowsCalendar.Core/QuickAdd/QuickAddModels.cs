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
/// <see cref="TaskByDefault"/> makes a phrase with no prefix a task (the Tasks view).
/// </summary>
public sealed record QuickAddOptions(bool DayFirst, IReadOnlyList<QuickAddTarget> Targets, DateTime? SlotStart = null,
    bool SlotAllDay = false, bool TaskByDefault = false);

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
