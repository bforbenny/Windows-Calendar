using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
public sealed partial class MainWindow : Window
{
    private readonly TimeGridView _week = new();
    private readonly MonthGridView _month = new();
    private readonly AgendaView _agenda = new();
    private readonly EventEditor _editor = new();
    private readonly DispatcherQueueTimer _minuteTimer;
    private readonly Dictionary<string, Collection> _collections = [];
    // MS365 accounts: their events are edited as Graph JSON (sync-ms365 second slice).
    private readonly HashSet<string> _graphAccounts = [];
    private ViewKind _view = ViewKind.Week;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly? _pendingDay;
    private bool _quitting;
    private int _conflictsDismissed;
    private readonly DispatcherQueueTimer _undoTimer;
    private (string ItemId, string PayloadBefore, string PayloadAfter, Item? Following)? _lastChange;
    private bool _renderPending;
    // Sidebar names of the collections being pulled now, for the hue wave (ui-states R1).
    private readonly HashSet<string> _syncing = [];
    private readonly Dictionary<string, TextBlock> _names = [];

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // WinUI 3 does not use the package logo for the window; Alt-Tab needs this (app-platform R10).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Closing hides to the tray; only Quit really closes (app-platform R5).
        AppWindow.Closing += (sender, args) =>
        {
            if (!_quitting)
            {
                args.Cancel = true;
                sender.Hide();
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
            ViewSwitcher.SelectedItem = ViewSwitcher.Items.First(i => (string)i.Tag == nameof(ViewKind.Agenda));
        };

        // Clicking a card opens the editor; clicking an empty slot starts a new event (calendar-views R5, R6b).
        foreach (var view in new IScrollingView[] { _week, _month, _agenda })
        {
            view.EventOpened += e =>
            {
                if (e.IsTask)
                {
                    OpenTask(e.ItemId);
                    return;
                }
                ShowEventEditor();
                _editor.OpenExisting(e.ItemId, e.Recurrence, _collections);
            };
            view.CreateRequested += (start, allDay) =>
            {
                ShowEventEditor();
                _editor.OpenNew(start, allDay, _collections);
            };
            view.EventDropped += OnEventDropped;
        }

        // Esc cancels a drag; Ctrl+Z undoes the latest move unless a text box is being typed in (undo R3).
        ((UIElement)Content).PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && EventDrag.CancelActive())
            {
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.B && IsControlDown())
            {
                e.Handled = true;
                ToggleSidebar();
            }
            else if (e.Key == Windows.System.VirtualKey.Z && IsControlDown()
                && FocusManager.GetFocusedElement(Content.XamlRoot) is not TextBox && _lastChange is not null)
            {
                e.Handled = true;
                Undo();
            }
        };
        _undoTimer = DispatcherQueue.CreateTimer();
        _undoTimer.Interval = TimeSpan.FromSeconds(8);
        _undoTimer.IsRepeating = false;
        _undoTimer.Tick += (_, _) => UndoBar.IsOpen = false;
        // Lifted so the shadows show (floating-banners R3).
        ConflictBar.Translation = UndoBar.Translation = new System.Numerics.Vector3(0, 0, 32);
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
        ((FrameworkElement)Content).SizeChanged += (_, _) => PlaceEditor();

        // Redraw in the new theme when Windows switches light/dark (app-platform R2a).
        ((FrameworkElement)Content).ActualThemeChanged += (_, _) => RenderView();

        // Move the current-time line every minute; at midnight the window and today's shading move on.
        _minuteTimer = DispatcherQueue.CreateTimer();
        _minuteTimer.Interval = TimeSpan.FromMinutes(1);
        _minuteTimer.Tick += (_, _) =>
        {
            if (DateOnly.FromDateTime(DateTime.Now) != _today || !CurrentView.UpdateNowLine())
            {
                _today = DateOnly.FromDateTime(DateTime.Now);
                RenderView();
            }
        };
        _minuteTimer.Start();

        ShowSidebar(Store?.GetUiFlag(SidebarHiddenFlag) != true);
        ShowAccounts();
        RenderView();
        ShowConflicts();
        ScrollToToday();
    }

    private const string SidebarHiddenFlag = "sidebar-hidden";

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

    /// <summary>
    /// The editor pushes the views narrower at 1200 epx and wider, and slides over them below that (calendar-views R6c).
    /// </summary>
    private void PlaceEditor()
    {
        EditorHost.Visibility = _editor.IsOpen || _taskEditor.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        var push = ((FrameworkElement)Content).ActualWidth >= 1200;
        Grid.SetColumn(EditorHost, push ? 1 : 0);
        Grid.SetColumnSpan(EditorHost, push ? 1 : 2);
        EditorHost.HorizontalAlignment = push ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    /// <summary>
    /// "N conflicts" banner (conflict-resolution R5, interim): closable for the session, shown again when the count grows.
    /// </summary>
    private void ShowConflicts()
    {
        var count = Store?.GetConflictCount() ?? 0;
        ConflictBar.Title = count == 1 ? "1 conflict" : $"{count} conflicts";
        ConflictBar.Message = "Changed both here and on the server: the server version was kept, and your version is saved.";
        ConflictBar.IsOpen = count > _conflictsDismissed;
    }

    private void OnConflictBarClosed(InfoBar sender, InfoBarClosedEventArgs args) =>
        _conflictsDismissed = Store?.GetConflictCount() ?? 0;

    /// <summary>
    /// A drop saves the new times like an editor change and offers Undo (calendar-views R7, undo interim). A repeating
    /// event first asks This event / Entire series (recurring-events interim).
    /// </summary>
    private async void OnEventDropped(DisplayEvent e, DragTarget target)
    {
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
            scope = await ScopeDialog.AskAsync(Content.XamlRoot, "Move a repeating event", allowFollowing);
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
            UndoBar.IsOpen = false;
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
        UndoBar.Severity = canUndo ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        UndoButton.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        UndoBar.IsOpen = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private static bool IsControlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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
        Theme.Current = ((FrameworkElement)Content).ActualTheme;
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

        var view = (UIElement)CurrentView;
        CurrentView.SetData(first, days, events, holidays);
        if (ViewHost.Children.Count != 1 || ViewHost.Children[0] != view)
        {
            ViewHost.Children.Clear();
            ViewHost.Children.Add(view);
        }
        Log.Information("{View} view loaded in {Ms} ms ({Count} occurrences)", _view, watch.ElapsedMilliseconds, events.Count);
    }

    /// <summary>Week opens on the current week at the current time; Month on the current week; Agenda on today.</summary>
    private void ScrollToToday() =>
        CurrentView.ScrollTo(_view == ViewKind.Week ? ViewPeriod.StartOfWeek(_today, WeekStart) : _today, toNow: true);

    private void ShowTitle(ViewKind view, DateOnly day)
    {
        if (view == _view)
        {
            PeriodTitle.Text = day.ToString(CultureInfo.CurrentCulture.DateTimeFormat.YearMonthPattern);
        }
    }

    private void OnToday(object sender, RoutedEventArgs e) => ScrollToToday();

    private void OnViewChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // The initial Week selection fires during InitializeComponent; _view is already Week, so it is skipped.
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<ViewKind>(tag, out var view) && view != _view)
        {
            _view = view;
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

    private void ShowSidebar(bool show)
    {
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(show ? 260 : 0);
    }

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
    /// accent colour instead (calendar-views R2b). The storyboard is kept in Tag so it can be stopped.
    /// </summary>
    private static void ShowSyncing(TextBlock name, bool syncing)
    {
        (name.Tag as Microsoft.UI.Xaml.Media.Animation.Storyboard)?.Stop();
        name.Tag = null;
        if (!syncing)
        {
            name.ClearValue(TextBlock.ForegroundProperty);
            return;
        }
        var accent = ((SolidColorBrush)Theme.Brush("TodayChipBrush")).Color;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            name.Foreground = new SolidColorBrush(accent);
            return;
        }
        var text = ((SolidColorBrush)Theme.Brush("TextPrimaryBrush")).Color;
        var brush = new LinearGradientBrush { StartPoint = new(-1, 0), EndPoint = new(0, 0) };
        brush.GradientStops.Add(new GradientStop { Color = text, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = accent, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = text, Offset = 1 });
        name.Foreground = brush;

        // Moving the gradient from left of the text to right of it; outside it the ends pad with the text colour.
        var wave = new Microsoft.UI.Xaml.Media.Animation.Storyboard { RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever };
        foreach (var (property, from, to) in new[] { ("StartPoint", -1.0, 1.0), ("EndPoint", 0.0, 2.0) })
        {
            var move = new Microsoft.UI.Xaml.Media.Animation.PointAnimation
            {
                From = new(from, 0),
                To = new(to, 0),
                Duration = TimeSpan.FromSeconds(1.6),
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(move, brush);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(move, property);
            wave.Children.Add(move);
        }
        name.Tag = wave;
        wave.Begin();
    }

    /// <summary>
    /// Sidebar (calendar-views R2): a Calendars and a Task lists section, each grouped by account, with a visibility
    /// checkbox, colour and shape per collection. Hidden collections are remembered (interim until calendar-sets).
    /// Shows the first-launch panel when there are no accounts (ui-states R5).
    /// </summary>
    private void ShowAccounts()
    {
        Theme.Current = ((FrameworkElement)Content).ActualTheme;
        foreach (var name in _names.Values)
        {
            ShowSyncing(name, false);
        }
        _names.Clear();
        AccountList.Children.Clear();
        _collections.Clear();
        var accounts = Store?.GetAccounts() ?? [];
        _graphAccounts.Clear();
        _graphAccounts.UnionWith(accounts.Where(a => a.Type == AccountType.MS365).Select(a => a.Id));
        var hidden = Store?.GetHiddenCollectionIds() ?? new HashSet<string>();
        var byAccount = accounts.ToDictionary(a => a.Id, a => Store!.GetCollections(a.Id));
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
            var section = new StackPanel { Spacing = 2 };
            var heading = Collapser($"collapsed:{kind}", new TextBlock
            {
                Text = title,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            }, section);
            heading.Margin = new Thickness(0, AccountList.Children.Count == 0 ? 4 : 20, 0, 0);
            AccountList.Children.Add(heading);
            AccountList.Children.Add(section);

            foreach (var account in accounts)
            {
                var collections = byAccount[account.Id].Where(c => c.Kind == kind).ToList();
                if (collections.Count == 0)
                {
                    continue;
                }
                var group = new StackPanel { Spacing = 2 };
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
    private Button Collapser(string flag, TextBlock title, UIElement body)
    {
        var collapsed = Store?.GetUiFlag(flag) == true;
        var chevron = new FontIcon { FontSize = 10, Foreground = Theme.Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        content.Children.Add(chevron);
        content.Children.Add(title);
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(2, 4, 2, 4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        void Apply()
        {
            chevron.Glyph = collapsed ? "\uE76C" : "\uE70D";
            body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{title.Text}, {(collapsed ? "collapsed" : "expanded")}");
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

    /// <summary>Account name, collapsing its group, with its options menu (Remove account, account-management R8).</summary>
    private Grid AccountHeader(Account account, string flag, UIElement group)
    {
        var more = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove account…", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += async (_, _) => await RemoveAccountAsync(account);
        more.Items.Add(remove);

        var header = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Collapser(flag, new TextBlock
        {
            Text = account.DisplayName,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Theme.Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, group));
        var moreButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
            Flyout = more,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(moreButton, $"{account.DisplayName} options");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(moreButton, $"{account.DisplayName} options");
        Grid.SetColumn(moreButton, 1);
        header.Children.Add(moreButton);
        return header;
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
        var label = new Grid { ColumnSpacing = 8 };
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.Children.Add(ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch));
        var name = new TextBlock { Text = collection.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        label.Children.Add(name);
        _names[collection.Id] = name;
        if (_syncing.Contains(collection.Id))
        {
            ShowSyncing(name, true);
        }
        if (glyph is not null)
        {
            var icon = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = Theme.Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(icon, char.ToUpperInvariant(status![0]) + status[1..]);
            Grid.SetColumn(icon, 2);
            label.Children.Add(icon);
        }
        var check = new CheckBox { Content = label, IsChecked = visible, MinWidth = 0 };
        // Right-click: colour and icon (ui-colour-theme interim).
        check.ContextFlyout = StyleMenu(collection, check);
        if (status is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, $"{collection.Name}, {status}");
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

    /// <summary>The sidebar's Add account link: Microsoft 365 or Radicale.</summary>
    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var ms365 = new MenuFlyoutItem { Text = "Microsoft 365" };
        ms365.Click += OnAddMs365;
        var radicale = new MenuFlyoutItem { Text = "Radicale" };
        radicale.Click += OnAddRadicale;
        menu.Items.Add(ms365);
        menu.Items.Add(radicale);
        menu.ShowAt((FrameworkElement)sender);
    }

    /// <summary>
    /// MS365 sign-in through WAM (account-management R2), then the user's name and calendars are read and saved. The
    /// access token is used for these two calls only.
    /// </summary>
    private async void OnAddMs365(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        if (App.Ms365 is not { } auth)
        {
            await ShowMessageAsync("Microsoft 365 is not set up",
                "Fill in the app registration's client ID and tenant ID in ms365.json, then rebuild the app.");
            return;
        }
        string? token = null;
        try
        {
            AddMs365Button.IsEnabled = AddAccountLink.IsEnabled = false;
            (var username, token) = await auth.SignInAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), default);
            if (store.GetAccounts().FirstOrDefault(a => a.Type == AccountType.MS365 && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                // Signing in an account that is already here resumes its sync (e.g. after "needs to sign in again").
                ((App)Application.Current).ResumeAccount(existing.Id);
                Log.Information("MS365 account {AccountId} signed in again", existing.Id);
                await ShowMessageAsync("Signed in", "This Microsoft 365 account is already in the app; it is signed in again and will sync now.");
                return;
            }
            var client = new Core.Graph.GraphClient(App.GraphHttp, token);
            var (name, email) = await client.GetMeAsync(default);
            var found = await Core.Graph.GraphSync.DiscoverAsync(client, default);
            var account = new Account(Guid.NewGuid().ToString("N"), AccountType.MS365, name, Username: username, Email: email);
            store.AddDiscoveredAccount(account, found);
            Log.Information("MS365 account {AccountId} added with {Count} calendars", account.Id, found.Count);
            RefreshView();
            ((App)Application.Current).RequestSync("account added");
        }
        catch (Microsoft.Identity.Client.MsalClientException ex) when (ex.ErrorCode == Microsoft.Identity.Client.MsalError.AuthenticationCanceledError)
        {
            // The user closed the sign-in window.
        }
        catch (Microsoft.Identity.Client.MsalException ex)
        {
            Log.Warning("MS365 sign-in failed: {Code}", ex.ErrorCode);
            await ShowMessageAsync("Sign-in failed", $"Microsoft 365 sign-in did not complete ({ex.ErrorCode}).");
        }
        catch (Core.Graph.GraphException ex)
        {
            Log.Warning("MS365 account setup failed: {Error} {Status}", ex.Error, (int?)ex.Status);
            await ShowMessageAsync("Could not read the account", ex.Error == Core.Graph.GraphError.Network
                ? "Microsoft 365 could not be reached. Check your connection and try again."
                : "Microsoft 365 refused the request. The log has the details.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Adding the MS365 account failed");
            await ShowMessageAsync("Could not add the account", "The log has the details.");
        }
        finally
        {
            token = null;
            AddMs365Button.IsEnabled = AddAccountLink.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message) =>
        await new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();

    private async void OnAddRadicale(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        var dialog = new AddRadicaleDialog(store) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
        if (dialog.AddedAccount is not null)
        {
            RefreshView();
            ((App)Application.Current).RequestSync("account added");
        }
    }

    /// <summary>Destructive: asks first, then deletes cached data and the saved password (account-management R8).</summary>
    private async System.Threading.Tasks.Task RemoveAccountAsync(Account account)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Remove {account.DisplayName}?",
            Content = "Its calendars, tasks and any changes that have not synced are deleted from this PC, and "
                + (account.Type == AccountType.MS365 ? "it is signed out of this app." : "its saved password is removed.")
                + " Nothing is deleted on the server.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            Store!.RemoveAccount(account.Id);
            if (account.Type == AccountType.MS365)
            {
                if (App.Ms365 is { } auth && account.Username is { } username)
                {
                    await auth.SignOutAsync(username);
                }
            }
            else
            {
                CredentialVault.Remove(account.Id);
            }
            Log.Information("Account {AccountId} removed", account.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Removing account {AccountId} failed", account.Id);
        }
        RefreshView();
    }

    public void ShowAndFocus()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        Activate();
    }

    public void CloseForQuit()
    {
        _quitting = true;
        _minuteTimer.Stop();
        Close();
    }
}
