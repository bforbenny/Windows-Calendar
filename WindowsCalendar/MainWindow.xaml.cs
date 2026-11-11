using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using Windows.System.UserProfile;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// The main window: sidebar with accounts and calendars, toolbar, and the Week/Month/Agenda views, which scroll
/// naturally instead of paging (calendar-views R1a), and the Tasks view (MainWindow.Tasks.cs).
/// </summary>
public sealed partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string SidebarHiddenFlag = "sidebar-hidden";

    private readonly TimeGridView _week = new();
    private readonly MonthGridView _month = new();
    private readonly AgendaView _agenda = new();
    private readonly EventEditor _editor = new();
    private readonly DispatcherTimer _minuteTimer = new();
    private readonly Dictionary<string, Collection> _collections = [];
    // MS365 accounts: their events are edited as Graph JSON (sync-ms365 second slice).
    private readonly HashSet<string> _graphAccounts = [];
    private ViewKind _view = ViewKind.Week;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly? _pendingDay;
    private bool _quitting;
    private bool _renderPending;
    private int _conflictsDismissed;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    // PayloadBefore is null for a created copy, which undo deletes.
    private (string ItemId, string? PayloadBefore, string PayloadAfter, Item? Following)? _lastChange;
    // Sidebar names of the collections being pulled now, for the hue wave (ui-states R1).
    private readonly HashSet<string> _syncing = [];
    private readonly Dictionary<string, TextBlock> _names = [];

    public MainWindow()
    {
        InitializeComponent();
        ViewParts.Root = (UIElement)Content;
        ViewParts.DialogHost = DialogHost;
        Theme.Apply(Theme.SystemTheme());
        Icon = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")));

        // Closing hides to the tray; only Quit really closes (app-platform R5).
        Closing += (_, e) =>
        {
            if (!_quitting)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // Every show puts the window back on the screens if any part of it is off them (app-platform R5).
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                KeepOnScreen();
            }
        };

        // The period title follows the scroll position (R1a).
        _week.VisibleDayChanged += day => ShowTitle(ViewKind.Week, day);
        _month.VisibleDayChanged += day => ShowTitle(ViewKind.Month, day);
        _agenda.VisibleDayChanged += day => ShowTitle(ViewKind.Agenda, day);

        // Month "+n more" opens Agenda with that day first (R1a).
        _month.DayRequested += date =>
        {
            _pendingDay = date;
            SwitchView(ViewKind.Agenda);
        };

        // Clicking a card opens the editor; clicking an empty slot starts a new event (calendar-views R5, R6b).
        foreach (var view in new IScrollingView[] { _week, _month, _agenda })
        {
            view.EventOpened += e =>
            {
                // A click selects the card as well as opening it (keyboard-navigation interim).
                Select(e, e.FirstDay, reveal: false);
                OpenItem(e);
            };
            view.CreateRequested += (start, allDay) =>
            {
                ShowEventEditor();
                _editor.CanSwitchToTask = NewTaskList() is not null;
                _editor.OpenNew(start, allDay, _collections);
            };
            view.EventDropped += OnEventDropped;
        }
        EventDrag.DuplicateRequested += e => Duplicate(e, null);

        // Esc cancels a drag; Ctrl+Z undoes the latest move unless a text box is being typed in (undo R3). Alt+key
        // arrives as a system key.
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && EventDrag.CancelActive())
            {
                e.Handled = true;
            }
            else if (DialogHost.Content is not null)
            {
                // A content dialog is open: its own keys only.
            }
            else if (key == Key.Escape && SettingsHost.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseSettings();
            }
            else if (key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+N: the New button, until quick add takes the key (keyboard-navigation interim).
                e.Handled = true;
                NewDefault();
            }
            else if (HandleShortcut(key))
            {
                e.Handled = true;
            }
            else if (key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+, opens Settings (settings-page R1).
                e.Handled = true;
                OpenSettings();
            }
            else if (key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ToggleSidebar();
            }
            else if (key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control
                && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase && _lastChange is not null)
            {
                e.Handled = true;
                Undo();
            }
        };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            BannerFade.SetOpen(UndoBar, false);
        };
        // Closing the conflicts banner hides it for the session, until the count grows (conflict-resolution R5).
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Wpf.Ui.Controls.InfoBar.IsOpenProperty, typeof(Wpf.Ui.Controls.InfoBar))
            .AddValueChanged(ConflictBar, (_, _) =>
            {
                if (!ConflictBar.IsOpen)
                {
                    _conflictsDismissed = Store?.GetConflictCount() ?? 0;
                }
            });
        EventDrag.Ended += () =>
        {
            if (_renderPending)
            {
                RenderView();
            }
        };
        EditorHost.Child = _editor;
        _editor.Changed += RenderView;
        _editor.OccurrenceDeleted += (title, id, before, after) =>
        {
            _lastChange = (id, before, after, null);
            ShowUndo(title, canUndo: true);
        };
        _editor.OpenChanged += PlaceEditor;
        SetupTasks();
        SetupKeyHints();

        // Redraw in the new theme when Windows switches light/dark or high contrast (app-platform R2a).
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Accessibility
                or Microsoft.Win32.UserPreferenceCategory.Color)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Theme.Apply(Theme.SystemTheme());
                    ShowAccounts();
                    RenderView();
                });
            }
        };

        // Move the current-time line every minute; at midnight the window and today's shading move on. Each tick is
        // just after the clock minute, so Join appears at the 10-minute mark (online-meeting-join R4).
        _minuteTimer.Tick += (_, _) =>
        {
            _minuteTimer.Stop();
            if (DateOnly.FromDateTime(DateTime.Now) != _today || JoinChanged() || !CurrentView.UpdateNowLine())
            {
                _today = DateOnly.FromDateTime(DateTime.Now);
                RenderView();
            }
            StartMinuteTimer();
        };
        StartMinuteTimer();

        ShowSidebar(Store?.GetUiFlag(SidebarHiddenFlag) != true);
        ShowAccounts();
        RenderView();
        ShowConflicts();
        ScrollToToday();
    }

    private void StartMinuteTimer()
    {
        var now = DateTime.Now;
        _minuteTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(200);
        _minuteTimer.Start();
    }

    private static LocalStore? Store => ((App)Application.Current).Store;

    private static DayOfWeek WeekStart => (DayOfWeek)(int)GlobalizationPreferences.WeekStartsOn;

    private IScrollingView CurrentView => _view switch
    {
        ViewKind.Month => _month,
        ViewKind.Agenda => _agenda,
        _ => _week,
    };

    /// <summary>Redraws the current view from the store, keeping the scroll position (e.g. after a sync).</summary>
    public void RefreshView()
    {
        ShowAccounts();
        RenderView();
        ShowConflicts();
        // The open event or task may have been deleted or moved by the sync.
        if (_editor.ItemId is { } id && Store?.GetItem(id) is null)
        {
            _editor.Hide();
        }
        if (_taskEditor.ItemId is { } taskId)
        {
            if (Store?.GetItem(taskId) is null)
            {
                _taskEditor.Hide();
            }
            else
            {
                _taskEditor.Reload(taskId, _collections);
            }
        }
    }

    /// <summary>The editor overlays the views on the right at every width, so they never resize (calendar-views R6c).</summary>
    private void PlaceEditor() =>
        EditorHost.Visibility = _editor.IsOpen || _taskEditor.IsOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// "N conflicts" banner (conflict-resolution R5, interim): closable for the session, shown again when the count grows.
    /// </summary>
    private void ShowConflicts()
    {
        var count = Store?.GetConflictCount() ?? 0;
        _conflictsDismissed = Math.Min(_conflictsDismissed, count);
        ConflictBar.Title = count == 1 ? "1 conflict" : $"{count} conflicts";
        ConflictBar.Message = "Changed both here and on the server: the server version was kept, and your version is saved.";
        BannerFade.SetOpen(ConflictBar, count > _conflictsDismissed);
    }

    /// <summary>
    /// A drop saves the new times like an editor change and offers Undo (calendar-views R7, undo interim). A repeating
    /// event first asks This event / Entire series (recurring-events interim).
    /// </summary>
    private async void OnEventDropped(DisplayEvent e, DragTarget target)
    {
        if (target.Copy)
        {
            Duplicate(e, target);
            return;
        }
        if (IsGraph(e.Collection))
        {
            await DropGraphAsync(e, target);
            return;
        }
        var scope = (EditScope?)null;
        if (e.Recurrence is not null)
        {
            var allowFollowing = Store?.GetItem(e.ItemId) is { } series && !CalDavRecurrence.IsOrganisedMeeting(series,
                Store.GetAccounts().FirstOrDefault(a => a.Id == e.Collection.AccountId)?.Email);
            scope = await ScopeDialog.AskAsync("Move a repeating event", allowFollowing);
            if (scope is null)
            {
                return;
            }
        }
        var to = e.Recurrence;
        try
        {
            if (Store?.GetItem(e.ItemId) is not { } item)
            {
                return;
            }
            var zone = TimeZoneInfo.Local;
            var now = DateTimeOffset.UtcNow;
            Item? edited;
            Item? following = null;
            if (e.Recurrence is { } recurrence)
            {
                var fields = CalDavRecurrence.ReadOccurrence(item, recurrence, zone) with { AllDay = target.AllDay, Start = target.Start, End = target.End };
                if (scope == EditScope.Following)
                {
                    var split = CalDavRecurrence.SplitSeries(item, recurrence, fields, e.Collection, zone, now);
                    (edited, following, to) = (split?.Original, split?.Following, split?.Recurrence ?? to);
                }
                else if (scope == EditScope.Series)
                {
                    to = CalDavRecurrence.MovedRecurrence(item, recurrence, fields, zone);
                    edited = CalDavRecurrence.EditSeries(item, recurrence, fields, zone, now);
                }
                else
                {
                    edited = CalDavRecurrence.EditOccurrence(item, recurrence, fields, zone, now);
                }
            }
            else
            {
                var before = CalDavEvents.Read(item, zone);
                edited = CalDavEvents.Edit(item, before with { AllDay = target.AllDay, Start = target.Start, End = target.End }, zone, now);
            }
            if (edited is null)
            {
                return;
            }
            if (following is not null)
            {
                Store.SplitItem(edited, following);
            }
            else
            {
                Store.UpdateItem(edited);
            }
            _lastChange = (item.Id, item.RawPayload, edited.RawPayload, following);
            ShowUndo(scope switch
            {
                EditScope.Series => "Series moved",
                EditScope.Following => "Following events moved",
                _ => "Event moved",
            }, canUndo: true);
            Log.Information("Event {ItemId} moved by drag ({Scope})", item.Id, scope?.ToString() ?? "single");
        }
        catch (EditRefusedException ex)
        {
            ShowUndo("Can't move this event", canUndo: false, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Moving event {ItemId} failed", e.ItemId);
        }
        RenderView();
        _editor.Reload(e.ItemId, e.Recurrence, to);
    }

    /// <summary>
    /// Puts the previous version of the event back (undo R4). Refused if the server changed the event since (R6): its
    /// payload is no longer the one the action wrote.
    /// </summary>
    private void Undo()
    {
        if (_lastChange is not { } change || Store is not { } store)
        {
            return;
        }
        _lastChange = null;
        if (change.PayloadBefore is null)
        {
            UndoDuplicate(store, change.ItemId);
            return;
        }
        try
        {
            if (store.GetItem(change.ItemId) is not { } item || item.RawPayload != change.PayloadAfter
                || (change.Following is { } split && store.GetItem(split.Id)?.RawPayload != split.RawPayload))
            {
                ShowUndo("This item changed on the server; can't undo", canUndo: false);
                return;
            }
            var restored = _collections.TryGetValue(item.CollectionId, out var collection) && IsGraph(collection)
                ? Core.Graph.GraphEvents.Restore(item, change.PayloadBefore, TimeZoneInfo.Local)
                : CalDavEvents.Restore(item, change.PayloadBefore, TimeZoneInfo.Local);
            if (change.Following is { } following)
            {
                // Undo of a split (undo R5): the original back and the new series deleted, one linked group.
                store.UndoSplit(restored, following.Id);
            }
            else
            {
                store.UpdateItem(restored);
            }
            BannerFade.SetOpen(UndoBar, false);
            Log.Information("Change to event {ItemId} undone", change.ItemId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Undoing change to event {ItemId} failed", change.ItemId);
        }
        RenderView();
        _editor.Reload(change.ItemId);
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void ShowUndo(string title, bool canUndo, string message = "")
    {
        UndoBar.Title = title;
        UndoBar.Message = message;
        UndoBar.Severity = canUndo ? Wpf.Ui.Controls.InfoBarSeverity.Informational : Wpf.Ui.Controls.InfoBarSeverity.Warning;
        UndoButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        BannerFade.SetOpen(UndoBar, true);
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    /// <summary>
    /// Loads the whole occurrence window into the current view; the view keeps its scroll position and builds only
    /// what is near the viewport (R1a). The load time is logged (R12).
    /// </summary>
    private void RenderView()
    {
        // Rebuilding would drop the card being dragged; redraw once it is dropped.
        if (EventDrag.Active is not null)
        {
            _renderPending = true;
            return;
        }
        _renderPending = false;
        ViewParts.Working = Store?.GetWorkingHours() ?? WorkingHours.Default;
        ViewParts.ShowWeekNumbers = SettingsPage.WeekNumbersOn(Store);
        if (_view == ViewKind.Tasks)
        {
            RenderTasks();
            return;
        }
        var (first, days) = ViewPeriod.Window(_today, WeekStart);

        var watch = Stopwatch.StartNew();
        var start = first.ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
        var events = (Store?.GetOccurrenceViews(from, from.AddDays(days + 1)) ?? [])
            .Where(o => (o.Kind == ItemKind.Event || !o.Completed) && _collections.ContainsKey(o.CollectionId))
            .Select(o => o.Kind == ItemKind.Task ? TaskItem(o, _collections[o.CollectionId]) : DisplayEvent.From(o, _collections[o.CollectionId]))
            .ToList();

        // The holiday calendar's all-day events shade their days and name them; with "shading only" they are not
        // drawn as cards (calendar-shading R4, R5). A hidden holiday calendar has no occurrences here (R6).
        var holidayId = Store?.GetHolidayCollectionId();
        var isHoliday = (DisplayEvent e) => e.Collection.Id == holidayId && e.AllDay && !e.IsTask;
        var holidays = HolidayDays.Build(events.Where(isHoliday).Select(e => (e.FirstDay, e.LastDay, e.Title)));
        if (Store?.GetHolidayShadingOnly() != false)
        {
            events.RemoveAll(e => isHoliday(e));
        }

        _shownEvents = events;
        RememberJoinShown();
        var view = (UIElement)CurrentView;
        if (ViewHost.Children.Count != 1 || ViewHost.Children[0] != view)
        {
            ViewHost.Children.Clear();
            ViewHost.Children.Add(view);
        }
        CurrentView.SetData(first, days, events, holidays);
        Log.Information("{View} view loaded in {Ms} ms ({Count} occurrences)", _view, watch.ElapsedMilliseconds, events.Count);
    }

    /// <summary>Week opens with today in the middle column at the current time; Month on the current week; Agenda on today.</summary>
    private void ScrollToToday() =>
        CurrentView.ScrollTo(_view == ViewKind.Week ? _today.AddDays(-3) : _today, toNow: true);

    private void ShowTitle(ViewKind view, DateOnly day)
    {
        if (view == _view)
        {
            _visibleDay = day;
            PeriodTitle.Text = day.ToString(CultureInfo.CurrentCulture.DateTimeFormat.YearMonthPattern);
        }
    }

    private void OnToday(object sender, RoutedEventArgs e) => ScrollToToday();

    private void OnViewClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<ViewKind>(tag, out var view))
        {
            SwitchView(view);
        }
    }

    private void SwitchView(ViewKind view)
    {
        foreach (var button in ViewSwitcher.Children.OfType<ToggleButton>())
        {
            button.IsChecked = (string)button.Tag == view.ToString();
        }
        if (view == _view)
        {
            return;
        }
        _view = view;
        NewButtonText.Text = view == ViewKind.Tasks ? "New task" : "New event";
        RenderView();
        if (view == ViewKind.Tasks)
        {
            return;
        }
        if (_pendingDay is { } day)
        {
            CurrentView.ScrollTo(day, toNow: false);
            _pendingDay = null;
        }
        else
        {
            ScrollToToday();
        }
    }

    private void OnSyncNow(object sender, RoutedEventArgs e) => ((App)Application.Current).SyncNow();

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => ToggleSidebar();

    /// <summary>The hamburger and Ctrl+B hide the sidebar fully; the state is kept across restarts (calendar-views R2b).</summary>
    private void ToggleSidebar()
    {
        var show = Sidebar.Visibility != Visibility.Visible;
        ShowSidebar(show);
        Store?.SetUiFlag(SidebarHiddenFlag, !show);
    }

    private void ShowSidebar(bool show) => Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A collection's pull started or ended (ui-states R1): its sidebar name shows the hue wave meanwhile.</summary>
    public void SetCollectionSyncing(string collectionId, bool active)
    {
        if (active ? !_syncing.Add(collectionId) : !_syncing.Remove(collectionId))
        {
            return;
        }
        if (_names.TryGetValue(collectionId, out var name))
        {
            ShowSyncing(name, active);
        }
    }

    /// <summary>
    /// A band of the accent colour sweeps the name left to right; with Windows animations off the name is in the
    /// accent colour instead (calendar-views R2b).
    /// </summary>
    private static void ShowSyncing(TextBlock name, bool syncing)
    {
        if (!syncing)
        {
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            return;
        }
        var accent = ((SolidColorBrush)Theme.Brush("TodayChipBrush")).Color;
        if (!SystemParameters.ClientAreaAnimation)
        {
            name.Foreground = new SolidColorBrush(accent);
            return;
        }
        var text = ((SolidColorBrush)Theme.Brush("TextPrimaryBrush")).Color;
        var brush = new LinearGradientBrush { StartPoint = new Point(-1, 0), EndPoint = new Point(0, 0) };
        brush.GradientStops.Add(new GradientStop(text, 0));
        brush.GradientStops.Add(new GradientStop(accent, 0.5));
        brush.GradientStops.Add(new GradientStop(text, 1));
        name.Foreground = brush;

        // Moving the gradient from left of the text to right of it; outside it the ends pad with the text colour.
        var duration = TimeSpan.FromSeconds(1.6);
        brush.BeginAnimation(LinearGradientBrush.StartPointProperty,
            new PointAnimation(new Point(-1, 0), new Point(1, 0), duration) { RepeatBehavior = RepeatBehavior.Forever });
        brush.BeginAnimation(LinearGradientBrush.EndPointProperty,
            new PointAnimation(new Point(0, 0), new Point(2, 0), duration) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>
    /// Sidebar (calendar-views R2): a Calendars and a Task lists section, each grouped by account, with a visibility
    /// checkbox, colour and shape per collection. Hidden collections are remembered (interim until calendar-sets).
    /// Shows the first-launch text when there are no accounts (ui-states R5).
    /// </summary>
    private void ShowAccounts()
    {
        _names.Clear();
        AccountList.Children.Clear();
        _collections.Clear();
        var accounts = Store?.GetAccounts() ?? [];
        var hidden = Store?.GetHiddenCollectionIds() ?? new HashSet<string>();
        var byAccount = accounts.ToDictionary(a => a.Id, a => Store!.GetCollections(a.Id));
        _graphAccounts.Clear();
        _graphAccounts.UnionWith(accounts.Where(a => a.Type == AccountType.MS365).Select(a => a.Id));
        foreach (var collection in byAccount.Values.SelectMany(c => c))
        {
            _collections[collection.Id] = collection;
        }

        // Two sections, each grouped by account (calendar-views R2); both levels collapse (R2b).
        foreach (var (kind, title) in new[] { (CollectionKind.Calendar, "Calendars"), (CollectionKind.TaskList, "Task lists") })
        {
            if (!byAccount.Values.Any(list => list.Any(c => c.Kind == kind)))
            {
                continue;
            }
            var section = new StackPanel();
            var heading = Collapser($"collapsed:{kind}", new TextBlock
            {
                Text = title,
                Style = Theme.Style("BodyStrongTextBlockStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            }, section);
            heading.Margin = new Thickness(0, AccountList.Children.Count == 0 ? 4 : 20, 0, 2);
            AccountList.Children.Add(heading);
            AccountList.Children.Add(section);

            foreach (var account in accounts)
            {
                var collections = byAccount[account.Id].Where(c => c.Kind == kind).ToList();
                if (collections.Count == 0)
                {
                    continue;
                }
                var group = new StackPanel();
                section.Children.Add(AccountHeader(account, $"collapsed:{kind}:{account.Id}", group));
                section.Children.Add(group);
                foreach (var collection in collections)
                {
                    group.Children.Add(VisibilityCheckBox(collection, !hidden.Contains(collection.Id)));
                }
            }
        }

        EmptyPanel.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewHost.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AddAccountLink.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AddRadicaleButton.IsEnabled = AddMs365Button.IsEnabled = AddAccountLink.IsEnabled = Store is not null;
    }

    /// <summary>
    /// A heading with a chevron that collapses and expands <paramref name="body"/>; the state is kept across restarts
    /// (calendar-views R2b).
    /// </summary>
    private static Button Collapser(string flag, TextBlock title, UIElement body)
    {
        var collapsed = Store?.GetUiFlag(flag) == true;
        var chevron = ViewParts.Icon("", 10);
        chevron.Foreground = Theme.Brush("TextSecondaryBrush");
        chevron.Margin = new Thickness(0, 0, 6, 0);
        var content = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Left);
        content.Children.Add(chevron);
        title.VerticalAlignment = VerticalAlignment.Center;
        if (title.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue)
        {
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        }
        content.Children.Add(title);
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(2, 4, 2, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        void Apply()
        {
            chevron.Text = collapsed ? "\uE76C" : "\uE70D";
            body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(button, $"{title.Text}, {(collapsed ? "collapsed" : "expanded")}");
        }
        button.Click += (_, _) =>
        {
            collapsed = !collapsed;
            Store?.SetUiFlag(flag, collapsed);
            Apply();
        };
        Apply();
        return button;
    }

    /// <summary>
    /// Visibility checkbox with the collection's shape marker in its colour (ui-colour-theme R7), and its status as an
    /// icon after the name: subscribed, read-only, or not secure (calendar-views R2b).
    /// </summary>
    private CheckBox VisibilityCheckBox(Collection collection, bool visible)
    {
        var (glyph, status) = collection.SourceUrl is not null
            ? Core.Sync.FeedSync.FetchUrl(collection.SourceUrl) is null ? ("\uE7BA", "not secure, not synced") : ("\uE71B", "subscribed")
            : collection.ReadOnly ? ("\uE72E", "read-only") : ((string?)null, (string?)null);
        var label = new DockPanel();
        var marker = ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch);
        marker.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(marker, Dock.Left);
        label.Children.Add(marker);
        if (glyph is not null)
        {
            var icon = ViewParts.Icon(glyph, 12);
            icon.Foreground = Theme.Brush("TextSecondaryBrush");
            icon.ToolTip = char.ToUpperInvariant(status![0]) + status[1..];
            icon.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(icon, Dock.Right);
            label.Children.Add(icon);
        }
        var name = new TextBlock { Text = collection.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        label.Children.Add(name);
        _names[collection.Id] = name;
        if (_syncing.Contains(collection.Id))
        {
            ShowSyncing(name, true);
        }
        var check = new CheckBox { Content = label, IsChecked = visible, MinWidth = 0, Margin = new Thickness(0, 1, 0, 1) };
        // Right-click: colour and icon (ui-colour-theme interim).
        check.ContextMenu = StyleMenu(collection, check);
        if (status is not null)
        {
            AutomationProperties.SetName(check, $"{collection.Name}, {status}");
        }
        check.Click += (_, _) =>
        {
            Store?.SetCollectionHidden(collection.Id, check.IsChecked != true);
            RenderView();
            if (check.IsChecked == true)
            {
                ((App)Application.Current).SyncCollection(collection.Id);
            }
        };
        return check;
    }

    public void ShowAndFocus()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    /// <summary>
    /// If any part of the window is outside the screens (a monitor unplugged, scaling changed), centres it on the
    /// primary screen's work area, shrunk to fit if needed (app-platform R5). Maximised windows are left to Windows.
    /// </summary>
    private void KeepOnScreen()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (screens.Contains(new Rect(Left, Top, ActualWidth, ActualHeight)))
        {
            return;
        }
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        Log.Information("Window was off-screen; moved onto the primary screen");
    }

    public void CloseForQuit()
    {
        _quitting = true;
        _minuteTimer.Stop();
        Close();
    }
}
