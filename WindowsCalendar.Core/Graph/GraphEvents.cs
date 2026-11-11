using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Graph;

/// <summary>
/// Local edits of MS365 events (sync-ms365 second slice: R7, R8, R12). An edit changes the stored Graph JSON and lists
/// the fields it changed under <see cref="ChangedKey"/>; the PATCH sends only those, so fields the app does not show
/// are kept. A new event's JSON holds only fields the app writes and is POSTed as-is.
/// </summary>
public static class GraphEvents
{
    /// <summary>The fields an edit changed (the app's own key; never sent).</summary>
    public const string ChangedKey = "@windowsCalendar.changed";

    /// <summary>Fields a create may copy from a stored event (a move to another calendar).</summary>
    private static readonly string[] Writable =
    [
        "subject", "body", "start", "end", "isAllDay", "location", "recurrence", "showAs", "sensitivity", "importance",
        "categories", "isReminderOn", "reminderMinutesBeforeStart",
    ];

    /// <summary>A new event in <paramref name="collection"/>, in the Windows timezone (R8).</summary>
    public static Item Create(Collection collection, EventFields fields, TimeZoneInfo zone)
    {
        var ev = new JsonObject();
        SetText(ev, fields);
        SetTimes(ev, fields, zone, zone);
        return GraphEventMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), collection.Id, ItemKind.Event, null, "{}"), ev.ToJsonString(), zone);
    }

    /// <summary>
    /// The event with <paramref name="fields"/>, or null when nothing changed. A stub occurrence compares against its
    /// <paramref name="master"/>'s title, location and description. Times are written in the event's own timezone.
    /// </summary>
    public static Item? Edit(Item item, Item? master, EventFields fields, TimeZoneInfo zone)
    {
        var old = GraphEventMapper.Read(item, master, zone);
        var ev = Parse(item.RawPayload);
        var changed = Changed(ev);
        var any = false;
        if (fields.Title != old.Title)
        {
            ev["subject"] = fields.Title;
            changed.Add("subject");
            any = true;
        }
        if (Normalise(fields.Location) != Normalise(old.Location))
        {
            // Replace the whole location so a structured address or coordinates that no longer match are cleared (R3).
            ev["location"] = new JsonObject
            {
                ["displayName"] = fields.Location ?? "",
                ["address"] = new JsonObject(),
                ["coordinates"] = new JsonObject(),
            };
            changed.Add("location");
            any = true;
        }
        if (Normalise(fields.Description) != Normalise(old.Description))
        {
            ev["body"] = new JsonObject { ["contentType"] = "text", ["content"] = fields.Description ?? "" };
            changed.Add("body");
            any = true;
        }
        if (fields.ShowAs != old.ShowAs)
        {
            ev["showAs"] = ShowAsValues.ToGraph(fields.ShowAs);
            changed.Add("showAs");
            any = true;
        }
        if (CalDavEvents.ReminderChanged(old, fields) && fields.Reminder is { Kept: null } reminder)
        {
            changed.UnionWith(SetReminder(ev, reminder));
            any = true;
        }
        if (fields.AllDay != old.AllDay || fields.Start != old.Start || fields.End != old.End)
        {
            SetTimes(ev, fields, zone, OwnZone(ev, zone));
            changed.UnionWith(["start", "end", "isAllDay"]);
            any = true;
        }
        return any ? WithChanged(item, ev, changed, zone) : null;
    }

    /// <summary>
    /// Only the reminder, on an event the app may not otherwise change: your own reminder on a meeting is yours to set
    /// (reminders-notifications R5a; Graph takes a PATCH of these two fields from an attendee). Null when unchanged.
    /// </summary>
    public static Item? EditReminder(Item item, Item? master, ReminderChoice reminder, TimeZoneInfo zone)
    {
        if (reminder.Kept is not null || GraphEventMapper.Read(item, master, zone).Reminder == reminder)
        {
            return null;
        }
        var ev = Parse(item.RawPayload);
        var changed = Changed(ev);
        changed.UnionWith(SetReminder(ev, reminder));
        return WithChanged(item, ev, changed, zone);
    }

    /// <summary>isReminderOn, and the minutes when on; returns the fields written.</summary>
    private static string[] SetReminder(JsonObject ev, ReminderChoice reminder)
    {
        ev["isReminderOn"] = reminder.MinutesBefore is not null;
        if (reminder.MinutesBefore is { } minutes)
        {
            ev["reminderMinutesBeforeStart"] = minutes;
            return ["isReminderOn", "reminderMinutesBeforeStart"];
        }
        return ["isReminderOn"];
    }

    /// <summary>A copy for another calendar of the same account (a create there; the caller deletes the original).</summary>
    public static Item MoveTo(Item item, Collection target, TimeZoneInfo zone)
    {
        var ev = Parse(item.RawPayload);
        var copy = new JsonObject();
        foreach (var name in Writable)
        {
            if (ev[name] is { } value)
            {
                copy[name] = value.DeepClone();
            }
        }
        return GraphEventMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), target.Id, ItemKind.Event, null, "{}"), copy.ToJsonString(), zone);
    }

    /// <summary>
    /// A new, independent copy of an event in <paramref name="target"/> (duplicate-event R3-R5): only the fields a create
    /// may write, so no attendees or online meeting. An occurrence (<paramref name="master"/> set) becomes a single event
    /// with the occurrence's own fields over its master's. Times are written in the event's own timezone;
    /// <paramref name="at"/> moves the copy (a Ctrl+drag drop).
    /// </summary>
    public static Item Duplicate(Item item, Item? master, Collection target, TimeZoneInfo zone,
        (bool AllDay, DateTime Start, DateTime End)? at = null)
    {
        var ev = Parse(item.RawPayload);
        var copy = new JsonObject();
        foreach (var source in master is null ? [ev] : new[] { Parse(master.RawPayload), ev })
        {
            foreach (var name in Writable)
            {
                if (source[name] is { } value)
                {
                    copy[name] = value.DeepClone();
                }
            }
        }
        if (master is not null)
        {
            copy.Remove("recurrence");
        }
        var fields = GraphEventMapper.Read(item, master, zone);
        if (at is { } t)
        {
            fields = fields with { AllDay = t.AllDay, Start = t.Start, End = t.End };
        }
        SetTimes(copy, fields, zone, OwnZone(master is null ? ev : Parse(master.RawPayload), zone));
        return GraphEventMapper.Rebuild(new Item(Guid.NewGuid().ToString("N"), target.Id, ItemKind.Event, null, "{}"), copy.ToJsonString(), zone);
    }

    /// <summary>
    /// Undo (undo R4): the fields the change wrote are set back from <paramref name="payloadBefore"/> and sent again.
    /// </summary>
    public static Item Restore(Item item, string payloadBefore, TimeZoneInfo zone)
    {
        var ev = Parse(item.RawPayload);
        var before = Parse(payloadBefore);
        var changed = Changed(ev);
        changed.UnionWith(Changed(before));
        foreach (var name in changed)
        {
            ev[name] = before[name]?.DeepClone();
        }
        return WithChanged(item, ev, changed, zone);
    }

    /// <summary>The POST body of a new event: its JSON without the app's own key.</summary>
    public static string CreateBody(string payload)
    {
        var ev = Parse(payload);
        ev.Remove(ChangedKey);
        return ev.ToJsonString();
    }

    /// <summary>The PATCH body: only the changed fields (R12). A cleared location is sent as an empty name.</summary>
    public static string PatchBody(string payload)
    {
        var ev = Parse(payload);
        var patch = new JsonObject();
        foreach (var name in Changed(ev))
        {
            patch[name] = ev[name]?.DeepClone();
        }
        return patch.ToJsonString();
    }

    /// <summary>Whether the app may change the event: not a meeting, nor an occurrence of one (sync-ms365 second slice).</summary>
    public static bool CanEdit(Item item, Item? master) => !item.ReadOnly && master?.ReadOnly != true;

    internal static JsonObject Parse(string payload) => JsonNode.Parse(payload) as JsonObject ?? [];

    internal static HashSet<string> Changed(JsonObject ev) =>
        ev[ChangedKey] is JsonArray list ? list.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet() : [];

    internal static Item WithChanged(Item item, JsonObject ev, HashSet<string> changed, TimeZoneInfo zone)
    {
        ev[ChangedKey] = new JsonArray(changed.Order().Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        return GraphEventMapper.Rebuild(item, ev.ToJsonString(), zone);
    }

    internal static void SetText(JsonObject ev, EventFields fields)
    {
        ev["subject"] = fields.Title;
        ev["body"] = new JsonObject { ["contentType"] = "text", ["content"] = fields.Description ?? "" };
        ev["showAs"] = ShowAsValues.ToGraph(fields.ShowAs);
        if (!string.IsNullOrEmpty(fields.Location))
        {
            ev["location"] = new JsonObject { ["displayName"] = fields.Location };
        }
        // Without one the server's default applies.
        if (fields.Reminder is { Kept: null } reminder)
        {
            SetReminder(ev, reminder);
        }
    }

    /// <summary>
    /// Start, end and all-day from <paramref name="fields"/> (wall times in <paramref name="editor"/>) as wall times in
    /// <paramref name="zone"/>; all-day as midnight-to-midnight dates (R8).
    /// </summary>
    internal static void SetTimes(JsonObject ev, EventFields fields, TimeZoneInfo editor, TimeZoneInfo zone)
    {
        var start = fields.AllDay ? fields.Start.Date : Wall(fields.Start, editor, zone);
        var end = fields.AllDay ? fields.End.Date : Wall(fields.End, editor, zone);
        ev["start"] = DateTimeTimeZone(start, zone);
        ev["end"] = DateTimeTimeZone(end, zone);
        ev["isAllDay"] = fields.AllDay;
    }

    internal static JsonObject DateTimeTimeZone(DateTime wall, TimeZoneInfo zone) =>
        new() { ["dateTime"] = wall.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = zone.Id };

    /// <summary>The event's own timezone when Windows knows it, else the Windows timezone (R8).</summary>
    internal static TimeZoneInfo OwnZone(JsonObject ev, TimeZoneInfo zone) =>
        ev["originalStartTimeZone"] is JsonValue v && v.TryGetValue<string>(out var id) && id is not ("UTC" or "tzone://Microsoft/Utc")
            && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var own) ? own : zone;

    /// <summary>A wall time in the editor's timezone as a wall time in <paramref name="zone"/>.</summary>
    private static DateTime Wall(DateTime wall, TimeZoneInfo from, TimeZoneInfo zone) =>
        from.Id == zone.Id ? wall : TimeZoneInfo.ConvertTime(GraphEventMapper.Local(wall, from), zone).DateTime;

    private static string Normalise(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
}
