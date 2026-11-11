using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Tests;

/// <summary>Repeating To Do tasks (tasks R7b): the rule as a Graph <c>recurrence</c>, in create, edit and read.</summary>
public sealed class GraphTaskRecurrenceTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly Collection List = new("c1", "a1", CollectionKind.TaskList, "Tasks", "#000000", "", false, false, "l1");

    private static RepeatRule Weekly(params DayOfWeek[] days) => new(RepeatFrequency.Weekly, 1, days, null, null, null);

    private static Item Created(DateOnly? due, RepeatRule rule) =>
        GraphTasks.Create(List, new TaskFields("Water plants", false, due, "normal", null, rule), Zone);

    public static TheoryData<RepeatRule, string> Rules => new()
    {
        { new RepeatRule(RepeatFrequency.Daily, 3, [], null, null, null), "2026-11-13" },
        { Weekly(DayOfWeek.Friday), "2026-11-13" },
        { new RepeatRule(RepeatFrequency.Weekly, 2, [DayOfWeek.Friday, DayOfWeek.Monday], null, null, null), "2026-11-13" },
        { new RepeatRule(RepeatFrequency.Monthly, 1, [], null, null, null), "2026-11-13" },
        { new RepeatRule(RepeatFrequency.Monthly, 2, [], 2, null, null), "2026-11-13" },
        { new RepeatRule(RepeatFrequency.Monthly, 1, [], -1, null, null), "2026-11-27" },
        { new RepeatRule(RepeatFrequency.Yearly, 1, [], null, null, null), "2026-11-13" },
        { new RepeatRule(RepeatFrequency.Daily, 1, [], null, 5, null), "2026-11-13" },
        { new RepeatRule(RepeatFrequency.Weekly, 1, [DayOfWeek.Friday], null, null, new DateOnly(2027, 3, 1)), "2026-11-13" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Rule_RoundTripsThroughCreateAndRead(RepeatRule rule, string due)
    {
        var item = Created(DateOnly.Parse(due), rule);

        var read = GraphTaskMapper.Read(item);

        Assert.Equal(rule, read.Repeat);
        Assert.Equal(DateOnly.Parse(due), read.Due);
        Assert.True(item.IsRecurring);
    }

    [Fact]
    public void Create_WeeklyOnFridayNoEnd_SendsPatternAndRange()
    {
        var item = Created(new DateOnly(2026, 11, 13), Weekly(DayOfWeek.Friday));

        var body = JsonNode.Parse(GraphEvents.CreateBody(item.RawPayload))!;

        Assert.Equal("""{"pattern":{"interval":1,"type":"weekly","daysOfWeek":["friday"],"firstDayOfWeek":"sunday"},"range":{"startDate":"2026-11-13","recurrenceTimeZone":"UTC","type":"noEnd"}}""",
            body["recurrence"]!.ToJsonString());
        Assert.Equal("2026-11-13T00:00:00", body["dueDateTime"]!["dateTime"]!.GetValue<string>());
    }

    [Fact]
    public void Create_MonthlyOnDay13EndingAfter5_SendsNumberedRange()
    {
        var item = Created(new DateOnly(2026, 11, 13), new RepeatRule(RepeatFrequency.Monthly, 1, [], null, 5, null));

        var recurrence = JsonNode.Parse(item.RawPayload)!["recurrence"]!;

        Assert.Equal("""{"pattern":{"interval":1,"type":"absoluteMonthly","dayOfMonth":13},"range":{"startDate":"2026-11-13","recurrenceTimeZone":"UTC","type":"numbered","numberOfOccurrences":5}}""",
            recurrence.ToJsonString());
    }

    [Fact]
    public void Create_WithoutDue_UsesToday_AndWeeklyMovesToTheFirstChosenWeekday()
    {
        var item = Created(null, new RepeatRule(RepeatFrequency.Daily, 1, [], null, null, null));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), GraphTaskMapper.Read(item).Due);

        var moved = Created(new DateOnly(2026, 11, 11), Weekly(DayOfWeek.Friday));
        Assert.Equal(new DateOnly(2026, 11, 13), GraphTaskMapper.Read(moved).Due);
        Assert.Equal("2026-11-13", JsonNode.Parse(moved.RawPayload)!["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
    }

    [Fact]
    public void Edit_RemovingTheRepeat_PatchesRecurrenceNull()
    {
        var item = Created(new DateOnly(2026, 11, 13), Weekly(DayOfWeek.Friday));

        var edited = GraphTasks.Edit(item, GraphTaskMapper.Read(item) with { Repeat = RepeatRule.None }, Zone)!;

        Assert.Equal("""{"recurrence":null}""", GraphEvents.PatchBody(edited.RawPayload));
        Assert.False(edited.IsRecurring);
        Assert.Null(GraphTaskMapper.Read(edited).Repeat);
    }

    [Fact]
    public void Edit_SettingARepeatOnATaskWithoutDue_PatchesDueAndRecurrence()
    {
        var item = GraphTasks.Create(List, new TaskFields("Water plants", false, null, "normal", null), Zone);

        var edited = GraphTasks.Edit(item, GraphTaskMapper.Read(item) with { Repeat = Weekly(DayOfWeek.Friday) }, Zone)!;

        var patch = JsonNode.Parse(GraphEvents.PatchBody(edited.RawPayload))!.AsObject();
        Assert.Equal(["dueDateTime", "recurrence"], patch.Select(p => p.Key).Order());
        Assert.Equal(DayOfWeek.Friday, GraphTaskMapper.Read(edited).Due!.Value.DayOfWeek);
    }

    [Fact]
    public void Edit_MovingTheDueDate_RestartsThePattern_AndUnchangedIsNull()
    {
        var item = Created(new DateOnly(2026, 11, 13), Weekly(DayOfWeek.Friday));
        Assert.Null(GraphTasks.Edit(item, GraphTaskMapper.Read(item), Zone));

        var edited = GraphTasks.Edit(item, GraphTaskMapper.Read(item) with { Due = new DateOnly(2026, 11, 16) }, Zone)!;

        var read = GraphTaskMapper.Read(edited);
        Assert.Equal(new DateOnly(2026, 11, 20), read.Due);
        Assert.Equal(Weekly(DayOfWeek.Friday), read.Repeat);
        Assert.Equal("2026-11-20", JsonNode.Parse(edited.RawPayload)!["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
    }
}
