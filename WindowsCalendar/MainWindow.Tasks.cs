using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.QuickAdd;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

namespace WindowsCalendar;

/// <summary>
/// The Tasks view, the task editor and due tasks on the calendar (tasks interim). Only tasks in writable To Do or
/// Radicale lists can be changed; the event and task editors share the editor host, one at a time.
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
        _tasks.ListChosen += id => Store?.SetNewTaskListId(id);
        _taskEditor.ListChoices = WritableTaskLists;
        _tasks.DeleteOldCompleted += async items => await DeleteOldCompletedAsync(items);
        EventCard.TaskChecked += e => SetTaskCompleted(e.ItemId, true);
        _taskEditor.Changed += RenderView;
        _taskEditor.UndoOffered += title => ShowUndo(title, canUndo: true);
        _taskEditor.OpenChanged += PlaceEditor;
        SetupNewSwitch();
    }

    /// <summary>
    /// A To Do list or a Radicale task list the app may write to (sync-ms365 third slice, tasks third slice); a
    /// subscribed feed is never writable.
    /// </summary>
    internal bool IsWritableTaskList(Collection list) => !list.ReadOnly && (IsGraph(list) || list.SourceUrl is null);

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
        _tasks.CanAdd = NewTaskList() is not null;
        _tasks.SetLists(WritableTaskLists().Select(c => (c, accounts.First(a => a.Id == c.AccountId))).ToList(), ChosenTaskList()?.Id);
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
    private void SetTaskCompleted(string itemId, bool completed) => TrySetTaskCompleted(itemId, completed);

    /// <summary>
    /// <see cref="SetTaskCompleted"/> that returns null when done, else why not, so the tray flyout can show it
    /// (tray-flyout prerequisite).
    /// </summary>
    internal string? TrySetTaskCompleted(string itemId, bool completed)
    {
        if (Store is not { } store || store.GetItem(itemId) is not { } item
            || !_collections.TryGetValue(item.CollectionId, out var list) || !IsWritableTaskList(list))
        {
            return "This task can't be changed here.";
        }
        string? error = null;
        try
        {
            var zone = TimeZoneInfo.Local;
            Item? edited;
            DateOnly? advancedTo = null;
            if (IsGraph(list))
            {
                edited = GraphTasks.Edit(item, GraphTaskMapper.Read(item) with { Completed = completed }, zone);
            }
            else if (completed)
            {
                var result = CalDavTasks.Complete(item, zone, DateTimeOffset.UtcNow);
                edited = result.Item.RawPayload == item.RawPayload ? null : result.Item;
                advancedTo = result.AdvancedTo;
            }
            else
            {
                edited = CalDavTasks.Reopen(item, zone, DateTimeOffset.UtcNow) is { } reopened && reopened.RawPayload != item.RawPayload ? reopened : null;
            }
            if (edited is not null)
            {
                using (store.BeginUndo("Task ticked"))
                {
                    store.UpdateItem(edited);
                }
                Log.Information("Task {ItemId} {State}", itemId, advancedTo is not null ? "advanced" : completed ? "completed" : "reopened");
                ShowUndo(advancedTo is { } next ? $"Task advanced to {next.ToString("d", System.Globalization.CultureInfo.CurrentCulture)}"
                    : completed ? "Task completed" : "Task reopened", canUndo: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Completing task {ItemId} failed", itemId);
            error = "Something went wrong; the task was not changed.";
        }
        RenderView();
        _taskEditor.Reload(itemId, _collections);
        return error;
    }

    /// <summary>
    /// Destructive: asks first (count per task list, account in brackets), then deletes the old completed tasks through
    /// the store, so each queues a server delete (tasks R10). No undo; only counts are logged.
    /// </summary>
    private async System.Threading.Tasks.Task DeleteOldCompletedAsync(IReadOnlyList<Item> items)
    {
        if (Store is not { } store || items.Count == 0)
        {
            return;
        }
        var accounts = store.GetAccounts();
        var lines = items.GroupBy(i => i.CollectionId)
            .Where(g => _collections.ContainsKey(g.Key))
            .Select(g =>
            {
                var list = _collections[g.Key];
                var account = accounts.FirstOrDefault(a => a.Id == list.AccountId)?.DisplayName;
                return $"{list.Name} ({account}): {g.Count()}";
            })
            .OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase);
        var dialog = Destructive.Apply(new ContentDialog(DialogHost)
        {
            Title = "Delete old completed tasks?",
            Content = $"{items.Count} completed {(items.Count == 1 ? "task" : "tasks")} older than 1 year will be deleted, also on the server. This can't be undone.\n\n"
                + string.Join("\n", lines),
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        var deleted = 0;
        foreach (var item in items)
        {
            // A sync while the dialog was open may have reopened or changed the task; only a still-eligible one goes.
            if (store.GetItem(item.Id) is not { } current
                || Core.Views.TaskQuery.OldCompleted([current], DateTimeOffset.Now, new HashSet<string> { item.CollectionId }).Count == 0)
            {
                continue;
            }
            try
            {
                if (_taskEditor.ItemId == item.Id)
                {
                    _taskEditor.Hide();
                }
                store.DeleteItem(item.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Deleting an old completed task failed");
            }
        }
        Log.Information("Deleted {Deleted} of {Count} old completed tasks", deleted, items.Count);
        RenderView();
    }

    /// <summary>Where new tasks go: the default task list if it is writable, else the first writable one.</summary>
    private Collection? NewTaskList() =>
        Store?.DefaultTaskListId is { } id && _collections.TryGetValue(id, out var d) && IsWritableTaskList(d) ? d
        : _collections.Values.FirstOrDefault(c => c.Kind == CollectionKind.TaskList && IsWritableTaskList(c));

    /// <summary>Every writable task list, by account then sidebar order (tasks R11, R12).</summary>
    private List<Collection> WritableTaskLists() =>
        (Store?.GetAccounts() ?? []).SelectMany(a => _collections.Values
            .Where(c => c.AccountId == a.Id && c.Kind == CollectionKind.TaskList && IsWritableTaskList(c))).ToList();

    /// <summary>The list the "New task" box adds to: the remembered one, else the default task list (tasks R12).</summary>
    private Collection? ChosenTaskList() =>
        Core.Views.TaskQuery.PickNewTaskList(Store?.NewTaskListId, Store?.DefaultTaskListId, WritableTaskLists().Select(c => c.Id).ToList()) is { } id
            ? _collections[id] : null;

    /// <summary>
    /// The "New task" box: the text goes through the quick-add parser as a task and the quick-add create path, so it
    /// gets "Task added", Undo and the same date and list words. True when created.
    /// </summary>
    private bool AddTask(string text) =>
        QuickCreate(QuickAddParser.Parse(text, DateTime.Now, QuickOptions(null, false, ChosenTaskList()?.Id) with { TaskByDefault = true })) is null;
}
