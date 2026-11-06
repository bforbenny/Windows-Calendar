using System;
using System.Linq;
using Serilog;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// The Tasks view, the task editor and due tasks on the calendar (tasks interim). Only To Do tasks in writable lists
/// can be changed; the event and task editors share the editor host, one at a time.
/// </summary>
public sealed partial class MainWindow
{
    private readonly TasksView _tasks = new();
    private readonly TaskEditor _taskEditor = new();

    private void SetupTasks()
    {
        _tasks.TaskOpened += OpenTask;
        _tasks.TaskToggled += SetTaskCompleted;
        _tasks.TaskAdded += AddTask;
        EventCard.TaskChecked += e => SetTaskCompleted(e.ItemId, true);
        _taskEditor.Changed += RenderView;
        _taskEditor.OpenChanged += PlaceEditor;
    }

    /// <summary>A To Do list the app may write to (sync-ms365 third slice); Radicale task writes come later.</summary>
    private bool IsWritableTaskList(Collection list) => IsGraph(list) && !list.ReadOnly;

    /// <summary>Opens the event editor in the host, closing the task editor first.</summary>
    private void ShowEventEditor()
    {
        _taskEditor.Close();
        EditorHost.Child = _editor;
    }

    private void OpenTask(string itemId)
    {
        if (Store?.GetItem(itemId) is not { } item || !_collections.TryGetValue(item.CollectionId, out var list))
        {
            return;
        }
        if (_editor.IsOpen)
        {
            _editor.Close();
        }
        EditorHost.Child = _taskEditor;
        _taskEditor.Open(itemId, _collections, IsWritableTaskList(list));
    }

    /// <summary>The Tasks view: open tasks of the visible task lists, by account and list in sidebar order.</summary>
    private void RenderTasks()
    {
        var accounts = Store?.GetAccounts() ?? [];
        var byList = (Store?.GetTasks() ?? []).ToLookup(t => t.CollectionId);
        var groups = _collections.Values
            .Where(c => c.Kind == CollectionKind.TaskList && byList.Contains(c.Id))
            .Select(c => (Collection: c, Account: accounts.FirstOrDefault(a => a.Id == c.AccountId)))
            .Where(x => x.Account is not null)
            .Select(x => new TaskGroup(x.Account!, x.Collection, byList[x.Collection.Id].ToList(), IsWritableTaskList(x.Collection)))
            .ToList();
        _tasks.SetData(groups, _today);
        if (ViewHost.Children.Count != 1 || ViewHost.Children[0] != _tasks)
        {
            ViewHost.Children.Clear();
            ViewHost.Children.Add(_tasks);
        }
        PeriodTitle.Text = "Tasks";
    }

    /// <summary>A due task as a calendar item: not draggable, a timed one drawn 30 minutes long (tasks R6 interim).</summary>
    private DisplayEvent TaskItem(OccurrenceView o, Collection list)
    {
        var e = DisplayEvent.From(o, list) with { Editable = false, IsTask = true, Checkable = IsWritableTaskList(list) && !o.ReadOnly };
        return e.AllDay || e.End > e.Start ? e : e with { End = e.Start.AddMinutes(30) };
    }

    /// <summary>Completes or reopens a To Do task from its checkbox in the Tasks view or on the calendar.</summary>
    private void SetTaskCompleted(string itemId, bool completed)
    {
        if (Store is not { } store || store.GetItem(itemId) is not { } item
            || !_collections.TryGetValue(item.CollectionId, out var list) || !IsWritableTaskList(list))
        {
            return;
        }
        try
        {
            if (GraphTasks.Edit(item, GraphTaskMapper.Read(item) with { Completed = completed }, TimeZoneInfo.Local) is { } edited)
            {
                store.UpdateItem(edited);
                Log.Information("Task {ItemId} {State}", itemId, completed ? "completed" : "reopened");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Completing task {ItemId} failed", itemId);
        }
        RenderView();
        _taskEditor.Reload(itemId, _collections);
    }

    /// <summary>The "New task" box: a task with that title in the default task list, if it is a To Do list.</summary>
    private void AddTask(string title)
    {
        if (Store is not { } store)
        {
            return;
        }
        var list = store.DefaultTaskListId is { } id && _collections.TryGetValue(id, out var d) && IsWritableTaskList(d) ? d
            : _collections.Values.FirstOrDefault(c => c.Kind == CollectionKind.TaskList && IsWritableTaskList(c));
        if (list is null)
        {
            Log.Information("New task not added: no Microsoft To Do list to add it to");
            return;
        }
        try
        {
            var task = GraphTasks.Create(list, new TaskFields(title, false, null, "normal", null), TimeZoneInfo.Local);
            store.CreateItem(task);
            Log.Information("Task {ItemId} created", task.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Creating a task failed");
        }
        RenderView();
    }
}
