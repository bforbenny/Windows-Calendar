using System.Text.Json.Nodes;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar.Core.Tests;

/// <summary>Undo of a task tick (GraphTasks.Restore) and the task field differences of a conflict (tasks interim, second slice).</summary>
public sealed class TaskUndoAndConflictTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private static Item Task(string json) =>
        GraphTaskMapper.Rebuild(new Item("t1", "c1", ItemKind.Task, "r1", "{}") with { Etag = "W/\"1\"" }, json, Zone);

    private const string Open = """{"id":"r1","title":"Pay rent","status":"notStarted","importance":"normal","dueDateTime":{"dateTime":"2026-11-13T00:00:00","timeZone":"UTC"}}""";

    [Fact]
    public void Restore_ReopensACompletedTask_AndSendsOnlyTheStatus()
    {
        var before = Task(Open);
        var completed = GraphTasks.Edit(before, GraphTaskMapper.Read(before, Zone) with { Completed = true }, Zone)!;

        var restored = GraphTasks.Restore(completed, before.RawPayload, Zone);

        Assert.False(GraphTaskMapper.Read(restored, Zone).Completed);
        Assert.Null(restored.CompletedUtc);
        Assert.Equal(("t1", "r1", "W/\"1\""), (restored.Id, restored.RemoteId, restored.Etag));
        Assert.Equal("""{"status":"notStarted"}""", GraphEvents.PatchBody(restored.RawPayload));
    }

    [Fact]
    public void Restore_CoalescesWithAnUnsentChange()
    {
        var before = Task(Open);
        var renamed = GraphTasks.Edit(before, GraphTaskMapper.Read(before, Zone) with { Title = "Pay rent now" }, Zone)!;
        var completed = GraphTasks.Edit(renamed, GraphTaskMapper.Read(renamed, Zone) with { Completed = true }, Zone)!;

        var restored = GraphTasks.Restore(completed, renamed.RawPayload, Zone);

        var read = GraphTaskMapper.Read(restored, Zone);
        Assert.Equal(("Pay rent now", false), (read.Title, read.Completed));
        var patch = JsonNode.Parse(GraphEvents.PatchBody(restored.RawPayload))!.AsObject();
        Assert.Equal(["status", "title"], patch.Select(p => p.Key).Order());
    }

    [Fact]
    public void Restore_PutsBackAReopenedTask_ToCompleted()
    {
        var done = Task(Open.Replace("notStarted", "completed"));
        var reopened = GraphTasks.Edit(done, GraphTaskMapper.Read(done, Zone) with { Completed = false }, Zone)!;

        var restored = GraphTasks.Restore(reopened, done.RawPayload, Zone);

        Assert.True(GraphTaskMapper.Read(restored, Zone).Completed);
    }

    private static string Local(Action<JsonObject> edit, params string[] changed)
    {
        var task = JsonNode.Parse(Open)!.AsObject();
        edit(task);
        task[GraphEvents.ChangedKey] = new JsonArray(changed.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        return task.ToJsonString();
    }

    [Fact]
    public void TaskDifferences_ListOnlyTheFieldsTheLocalEditChanged()
    {
        var server = Task(Open.Replace("Pay rent", "Pay the rent").Replace("normal", "high"));
        var local = Local(t =>
        {
            t["title"] = "My title";
            t["status"] = "completed";
            t["dueDateTime"] = new JsonObject { ["dateTime"] = "2026-11-20T00:00:00", ["timeZone"] = "UTC" };
            t["body"] = new JsonObject { ["contentType"] = "text", ["content"] = "Notes" };
            t["importance"] = "low";
        }, "title", "status", "dueDateTime", "body");

        var diffs = ConflictResolver.Differences(local, server, graph: true, Zone);

        Assert.Equal(["Title", "Completed", "Due", "Notes"], diffs.Select(d => d.Field));
        Assert.Equal(("My title", "Pay the rent"), (diffs[0].Yours, diffs[0].Server));
        Assert.Equal(("Yes", "No"), (diffs[1].Yours, diffs[1].Server));
        Assert.Equal(("Notes", "(none)"), (diffs[3].Yours, diffs[3].Server));
    }

    [Fact]
    public void TaskDifferences_ImportanceRepeatAndReminder()
    {
        var server = Task(Open);
        var local = Local(t =>
        {
            t["importance"] = "high";
            t["recurrence"] = JsonNode.Parse("""{"pattern":{"interval":1,"type":"daily"},"range":{"startDate":"2026-11-13","type":"noEnd"}}""");
            t["isReminderOn"] = true;
            t["reminderDateTime"] = new JsonObject { ["dateTime"] = "2026-11-13T09:00:00", ["timeZone"] = "UTC" };
        }, "importance", "recurrence", "isReminderOn", "reminderDateTime");

        var diffs = ConflictResolver.Differences(local, server, graph: true, Zone);

        Assert.Equal(["Importance", "Repeat", "Reminder"], diffs.Select(d => d.Field));
        Assert.Equal(("high", "normal"), (diffs[0].Yours, diffs[0].Server));
        Assert.Equal(("Every day", "Doesn't repeat"), (diffs[1].Yours, diffs[1].Server));
        Assert.Equal("None", diffs[2].Server);
    }

    [Fact]
    public void TaskDifferences_SameValues_AreNotListed() =>
        Assert.Empty(ConflictResolver.Differences(Local(_ => { }, "title", "status"), Task(Open), graph: true, Zone));
}
