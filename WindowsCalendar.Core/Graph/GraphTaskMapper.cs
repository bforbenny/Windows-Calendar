using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// What the task editor shows and writes (tasks R2 interim). Due is a date only; Importance is low, normal or high.
/// Repeat (tasks R7b) is null or <see cref="RepeatRule.None"/> when the task does not repeat; as read, null also covers
/// a pattern the panel can't show.
/// </summary>
public sealed record TaskFields(string Title, bool Completed, DateOnly? Due, string Importance, string? Notes, RepeatRule? Repeat = null);

/// <summary>
/// Microsoft To Do tasks to store items (sync-ms365 third slice, tasks R2a, R3). The JSON is kept as-is; the columns
/// are derived from it. The due date is the date part of <c>dueDateTime</c>, a date-only due date in the Windows timezone.
/// </summary>
public static class GraphTaskMapper
{
    /// <summary>The item for a task from delta or a write reply, or null without an id.</summary>
    public static Item? Map(JsonElement task, string collectionId, TimeZoneInfo zone) =>
        GraphClient.Text(task, "id") is { } id
            ? Build(Guid.NewGuid().ToString("N"), collectionId, id, task, zone) with { Etag = GraphClient.Text(task, "@odata.etag") }
            : null;

    /// <summary>The item's columns re-derived from a changed payload (local edits); id, server id and etag are kept.</summary>
    internal static Item Rebuild(Item item, string payload, TimeZoneInfo zone)
    {
        using var doc = JsonDocument.Parse(payload);
        return Build(item.Id, item.CollectionId, item.RemoteId, doc.RootElement, zone) with { Etag = item.Etag };
    }

    private static Item Build(string itemId, string collectionId, string? remoteId, JsonElement task, TimeZoneInfo zone)
    {
        var due = DueDate(task);
        var status = GraphClient.Text(task, "status");
        return new Item(
            itemId,
            collectionId,
            ItemKind.Task,
            remoteId,
            task.GetRawText(),
            Summary: GraphClient.Text(task, "title"),
            AllDay: due is not null,
            Status: status,
            DueUtc: due is { } d ? GraphEventMapper.Local(d.ToDateTime(TimeOnly.MinValue), zone) : null,
            CompletedUtc: status == "completed" ? Time(task, "completedDateTime", zone) ?? Modified(task) : null,
            IsRecurring: task.TryGetProperty("recurrence", out var r) && r.ValueKind == JsonValueKind.Object,
            LastModifiedUtc: Modified(task));
    }

    /// <summary>The editor's fields for a stored task.</summary>
    public static TaskFields Read(Item item)
    {
        using var doc = JsonDocument.Parse(item.RawPayload);
        var task = doc.RootElement;
        var importance = GraphClient.Text(task, "importance") is { } i && i is "low" or "high" ? i : "normal";
        return new TaskFields(GraphClient.Text(task, "title") ?? "", GraphClient.Text(task, "status") == "completed",
            DueDate(task), importance, GraphEventMapper.Description(task), ReadRepeat(item.RawPayload));
    }

    /// <summary>
    /// The task's repeat for the panel (tasks R7b): null when it does not repeat or the pattern can't be shown. The
    /// range's start date is the due date the pattern was built on.
    /// </summary>
    private static RepeatRule? ReadRepeat(string payload) =>
        JsonNode.Parse(payload)?["recurrence"] is JsonObject recurrence
        && recurrence["range"]?["startDate"]?.GetValue<string>() is { } text
        && DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var start)
            ? GraphRecurrence.ReadRule(recurrence, start)
            : null;

    /// <summary>The due date from a stored payload, for the occurrence index; null when there is none.</summary>
    internal static DateOnly? DueDate(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return DueDate(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The date part of <c>dueDateTime</c>, whatever its zone (tasks R2a).</summary>
    internal static DateOnly? DueDate(JsonElement task) =>
        task.TryGetProperty("dueDateTime", out var d) && GraphClient.Text(d, "dateTime") is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            ? DateOnly.FromDateTime(wall)
            : null;

    private static DateTimeOffset? Time(JsonElement task, string name, TimeZoneInfo zone)
    {
        if (!task.TryGetProperty(name, out var t) || GraphClient.Text(t, "dateTime") is not { } text
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return null;
        }
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        var tz = GraphClient.Text(t, "timeZone");
        return tz is null or "UTC" or "Etc/UTC"
            ? new DateTimeOffset(wall, TimeSpan.Zero)
            : GraphEventMapper.Local(wall, TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var own) ? own : zone);
    }

    private static DateTimeOffset? Modified(JsonElement task) =>
        GraphClient.Text(task, "lastModifiedDateTime") is { } m
        && DateTimeOffset.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm) ? lm : null;
}
