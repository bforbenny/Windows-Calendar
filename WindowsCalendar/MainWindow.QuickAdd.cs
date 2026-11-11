using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.QuickAdd;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Quick add (quick-add R1, R4, R5): the toolbar box (Ctrl+N) and the quick-create popup that an empty slot in Week, Month
/// or Agenda opens. Enter creates the event or task through the same Core calls as the editors; Ctrl+Enter or "More
/// details" opens the full editor pre-filled. A creation can be undone like a duplicate (undo R4).
/// </summary>
public sealed partial class MainWindow
{
    private readonly QuickAddBox _quickCreateBox = new(false);
    private readonly Popup _quickCreate = new()
    {
        Placement = PlacementMode.MousePoint,
        StaysOpen = false,
        AllowsTransparency = true,
    };
    // The slot the quick-create popup was opened on.
    private (DateTime Start, bool AllDay)? _slot;
    // The collection the hidden-calendar banner's Show button ticks.
    private string? _hiddenId;

    private void SetupQuickAdd()
    {
        QuickBox.Options = () => QuickOptions(null, false);
        QuickBox.CollectionOf = id => _collections.GetValueOrDefault(id);
        QuickBox.Submitted += result =>
        {
            if (QuickCreate(result))
            {
                QuickBox.Clear();
            }
        };
        QuickBox.MoreDetails += result =>
        {
            if (QuickMoreDetails(result))
            {
                QuickBox.Clear();
            }
        };
        // Esc with no suggestions open leaves the box.
        QuickBox.CloseRequested += () => Keyboard.ClearFocus();

        _quickCreateBox.Options = () => QuickOptions(_slot?.Start, _slot?.AllDay ?? false);
        _quickCreateBox.CollectionOf = id => _collections.GetValueOrDefault(id);
        _quickCreateBox.Submitted += result =>
        {
            if (QuickCreate(result))
            {
                _quickCreate.IsOpen = false;
            }
        };
        _quickCreateBox.MoreDetails += result =>
        {
            if (QuickMoreDetails(result))
            {
                _quickCreate.IsOpen = false;
            }
        };
        _quickCreateBox.CloseRequested += () => _quickCreate.IsOpen = false;

        var card = new Border
        {
            Child = _quickCreateBox,
            Width = 360,
            Padding = new Thickness(8),
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
        _quickCreate.Child = card;
        _quickCreate.Closed += (_, _) =>
        {
            _slot = null;
            _quickCreateBox.Clear();
        };
        Body.Children.Add(_quickCreate);
    }

    /// <summary>Ctrl+N: focuses the toolbar box with its text selected.</summary>
    private void FocusQuickAdd()
    {
        if (SettingsHost.Visibility == Visibility.Visible)
        {
            return;
        }
        _quickCreate.IsOpen = false;
        QuickBox.FocusBox();
    }

    /// <summary>An empty slot was clicked: the quick-create popup opens at the pointer, set to that slot (quick-add R1).</summary>
    private void OpenQuickCreate(DateTime start, bool allDay)
    {
        _quickCreate.IsOpen = false;
        _slot = (start, allDay);
        _quickCreateBox.Clear();
        _quickCreate.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _quickCreateBox.FocusBox(selectAll: false));
    }

    /// <summary>
    /// The parser's inputs: writable calendars, then writable To Do lists, each in sidebar order; the Windows short-date
    /// order; the slot (popup) or the Tasks view's task default (toolbar).
    /// </summary>
    private QuickAddOptions QuickOptions(DateTime? slot, bool slotAllDay)
    {
        var accounts = Store?.GetAccounts() ?? [];
        var hidden = Store?.GetHiddenCollectionIds() ?? new HashSet<string>();
        var targets = new List<QuickAddTarget>();
        foreach (var kind in new[] { CollectionKind.Calendar, CollectionKind.TaskList })
        {
            var defaultId = kind == CollectionKind.Calendar ? Store?.DefaultCalendarId : Store?.DefaultTaskListId;
            foreach (var account in accounts)
            {
                foreach (var c in _collections.Values.Where(c => c.AccountId == account.Id && c.Kind == kind
                    && (kind == CollectionKind.TaskList ? IsWritableTaskList(c) : !c.ReadOnly && c.SourceUrl is null)))
                {
                    targets.Add(new QuickAddTarget(c.Id, c.Name, account.DisplayName, !hidden.Contains(c.Id), c.Id == defaultId,
                        kind == CollectionKind.TaskList));
                }
            }
        }
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        var dayFirst = pattern.IndexOf('d') < pattern.IndexOf('M');
        return new QuickAddOptions(dayFirst, targets, slot, slotAllDay, TaskByDefault: slot is null && _view == ViewKind.Tasks);
    }

    /// <summary>
    /// Creates what the box understood by the editors' own paths: Graph events like EventEditor.CreateGraph, CalDAV events
    /// like EventEditor.Save, tasks like TaskEditor.Create; then the Undo banner (undo deletes the new item).
    /// </summary>
    private bool QuickCreate(QuickAddResult result)
    {
        if (Store is not { } store || result.Error is not null)
        {
            return false;
        }
        if (result.Target is not { } chosen || !_collections.TryGetValue(chosen.Id, out var target))
        {
            ShowUndo("Can't add this", canUndo: false, "There is no calendar to add it to.");
            return false;
        }
        var zone = TimeZoneInfo.Local;
        try
        {
            Item created;
            if (result.Kind == QuickAddKind.Task)
            {
                created = GraphTasks.Create(target, new TaskFields(result.Title, false, DateOnly.FromDateTime(result.Start), "normal", null), zone);
            }
            else
            {
                var fields = new EventFields(result.Title, result.AllDay, result.Start, result.End, result.Location, null,
                    result.AllDay ? ShowAs.Free : ShowAs.Busy);
                if (IsGraph(target))
                {
                    created = GraphEvents.Create(target, fields, zone);
                    if (GraphRecurrence.ChangeRule(created, null, false, result.Repeat, target, [], zone)?.Updated is { } repeating)
                    {
                        created = repeating;
                    }
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    created = CalDavEvents.Create(target, fields, zone, now);
                    if (CalDavRecurrence.ChangeRule(created, null, false, result.Repeat, target, zone, now) is { } repeating)
                    {
                        created = repeating.Original;
                    }
                }
            }
            store.CreateItem(created);
            Log.Information("{Kind} {ItemId} created by quick add", result.Kind, created.Id);
            _lastChange = (created.Id, null, created.RawPayload, null);
            ShowUndo(result.Kind == QuickAddKind.Task ? "Task added" : "Event added", canUndo: true);
        }
        catch (EditRefusedException ex)
        {
            ShowUndo("Can't add this", canUndo: false, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Quick add failed");
            ShowUndo("Can't add this", canUndo: false, "Something went wrong; your text is kept.");
            return false;
        }

        // A hidden calendar (only via #account/name, or an unticked list) says so, with a way to show it (quick-add R4).
        _hiddenId = result.TargetHidden ? target.Id : null;
        if (result.TargetHidden)
        {
            HiddenBar.Title = $"Added to {target.Name} (hidden)";
            HiddenBar.Message = "";
        }
        BannerFade.SetOpen(HiddenBar, result.TargetHidden);
        RenderView();
        return true;
    }

    /// <summary>The banner's Show button: the same as ticking the calendar in the sidebar.</summary>
    private void OnShowHidden(object sender, RoutedEventArgs e)
    {
        if (_hiddenId is not { } id)
        {
            return;
        }
        Store?.SetCollectionHidden(id, false);
        ShowAccounts();
        RenderView();
        ((App)Application.Current).SyncCollection(id);
        BannerFade.SetOpen(HiddenBar, false);
        _hiddenId = null;
    }

    /// <summary>Ctrl+Enter or "More details": the full editor, pre-filled with what the box understood (quick-add R4).</summary>
    private bool QuickMoreDetails(QuickAddResult result)
    {
        if (result.Kind == QuickAddKind.Task)
        {
            var list = result.Target is { } t && _collections.TryGetValue(t.Id, out var chosen) ? chosen : NewTaskList();
            if (list is null)
            {
                ShowUndo("Can't add this", canUndo: false, "There is no To Do list to add it to.");
                return false;
            }
            if (_editor.IsOpen)
            {
                _editor.Close();
            }
            EditorHost.Child = _taskEditor;
            _taskEditor.OpenNew(list, result.Title, DateOnly.FromDateTime(result.Start));
            return true;
        }
        if (!_collections.Values.Any(c => c.Kind == CollectionKind.Calendar && !c.ReadOnly && c.SourceUrl is null))
        {
            ShowUndo("Can't add this", canUndo: false, "There is no calendar to add it to.");
            return false;
        }
        var fields = new EventFields(result.Title, result.AllDay, result.Start, result.End, result.Location, null,
            result.AllDay ? ShowAs.Free : ShowAs.Busy);
        ShowEventEditor();
        _editor.CanSwitchToTask = NewTaskList() is not null;
        _editor.OpenNew(result.Start, result.AllDay, _collections, result.Title, fields, result.Target?.Id, result.Repeat);
        return true;
    }
}
