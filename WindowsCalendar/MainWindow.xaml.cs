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
/// naturally instead of paging (calendar-views R1a).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly TimeGridView _week = new();
    private readonly MonthGridView _month = new();
    private readonly AgendaView _agenda = new();
    private readonly EventEditor _editor = new();
    private readonly DispatcherQueueTimer _minuteTimer;
    private readonly Dictionary<string, Collection> _collections = [];
    private ViewKind _view = ViewKind.Week;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly? _pendingDay;
    private bool _quitting;
    private int _conflictsDismissed;
    private readonly DispatcherQueueTimer _undoTimer;
    private (string ItemId, EventFields Before, string PayloadAfter)? _lastMove;
    private bool _renderPending;

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
            view.EventOpened += id => _editor.OpenExisting(id, _collections);
            view.CreateRequested += (start, allDay) => _editor.OpenNew(start, allDay, _collections);
            view.EventDropped += OnEventDropped;
        }

        // Esc cancels a drag; Ctrl+Z undoes the latest move unless a text box is being typed in (undo R3).
        ((UIElement)Content).PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && EventDrag.CancelActive())
            {
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Z && IsControlDown()
                && FocusManager.GetFocusedElement(Content.XamlRoot) is not TextBox && _lastMove is not null)
            {
                e.Handled = true;
                UndoMove();
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
        _editor.OpenChanged += PlaceEditor;
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

        ShowAccounts();
        RenderView();
        ShowConflicts();
        ScrollToToday();
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
        // The open event may have been deleted or moved by the sync.
        if (_editor.ItemId is { } id && Store?.GetItem(id) is null)
        {
            _editor.Hide();
        }
    }

    /// <summary>
    /// The editor pushes the views narrower at 1200 epx and wider, and slides over them below that (calendar-views R6c).
    /// </summary>
    private void PlaceEditor()
    {
        EditorHost.Visibility = _editor.IsOpen ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>A drop saves the new times like an editor change and offers Undo (calendar-views R7, undo interim).</summary>
    private void OnEventDropped(DisplayEvent e, DragTarget target)
    {
        try
        {
            if (Store?.GetItem(e.ItemId) is not { } item)
            {
                return;
            }
            var zone = TimeZoneInfo.Local;
            var before = CalDavEvents.Read(item, zone);
            if (CalDavEvents.Edit(item, before with { AllDay = target.AllDay, Start = target.Start, End = target.End }, zone,
                DateTimeOffset.UtcNow) is not { } edited)
            {
                return;
            }
            Store.UpdateItem(edited);
            _lastMove = (item.Id, before, edited.RawPayload);
            ShowUndo("Event moved", canUndo: true);
            Log.Information("Event {ItemId} moved by drag", item.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Moving event {ItemId} failed", e.ItemId);
        }
        RenderView();
        _editor.Reload(e.ItemId);
    }

    /// <summary>
    /// Puts the previous times back (undo R4). Refused if the server changed the event since (R6): its payload is no
    /// longer the one the move wrote.
    /// </summary>
    private void UndoMove()
    {
        if (_lastMove is not { } move || Store is not { } store)
        {
            return;
        }
        _lastMove = null;
        try
        {
            if (store.GetItem(move.ItemId) is not { } item || item.RawPayload != move.PayloadAfter)
            {
                ShowUndo("This item changed on the server; can't undo", canUndo: false);
                return;
            }
            if (CalDavEvents.Edit(item, move.Before, TimeZoneInfo.Local, DateTimeOffset.UtcNow) is { } restored)
            {
                store.UpdateItem(restored);
            }
            UndoBar.IsOpen = false;
            Log.Information("Move of event {ItemId} undone", move.ItemId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Undoing move of event {ItemId} failed", move.ItemId);
        }
        RenderView();
        _editor.Reload(move.ItemId);
    }

    private void OnUndo(object sender, RoutedEventArgs e) => UndoMove();

    private void ShowUndo(string title, bool canUndo)
    {
        UndoBar.Title = title;
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
        var (first, days) = ViewPeriod.Window(_today, WeekStart);

        var watch = Stopwatch.StartNew();
        var start = first.ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
        var events = (Store?.GetOccurrenceViews(from, from.AddDays(days + 1)) ?? [])
            .Where(o => o.Kind == ItemKind.Event && _collections.ContainsKey(o.CollectionId))
            .Select(o => DisplayEvent.From(o, _collections[o.CollectionId]))
            .ToList();

        var view = (UIElement)CurrentView;
        CurrentView.SetData(first, days, events);
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

    /// <summary>
    /// Sidebar (calendar-views R2): a Calendars and a Task lists section, each grouped by account, with a visibility
    /// checkbox, colour and shape per collection. Hidden collections are remembered (interim until calendar-sets).
    /// Shows the first-launch panel when there are no accounts (ui-states R5).
    /// </summary>
    private void ShowAccounts()
    {
        Theme.Current = ((FrameworkElement)Content).ActualTheme;
        AccountList.Children.Clear();
        _collections.Clear();
        var accounts = Store?.GetAccounts() ?? [];
        var hidden = Store?.GetHiddenCollectionIds() ?? new HashSet<string>();
        var byAccount = accounts.ToDictionary(a => a.Id, a => Store!.GetCollections(a.Id));
        foreach (var collection in byAccount.Values.SelectMany(c => c))
        {
            _collections[collection.Id] = collection;
        }

        // Two sections, each grouped by account (calendar-views R2).
        foreach (var (kind, title) in new[] { (CollectionKind.Calendar, "Calendars"), (CollectionKind.TaskList, "Task lists") })
        {
            if (!byAccount.Values.Any(list => list.Any(c => c.Kind == kind)))
            {
                continue;
            }
            AccountList.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, AccountList.Children.Count == 0 ? 4 : 20, 0, 0),
            });

            foreach (var account in accounts)
            {
                var collections = byAccount[account.Id].Where(c => c.Kind == kind).ToList();
                if (collections.Count == 0)
                {
                    continue;
                }
                AccountList.Children.Add(AccountHeader(account));
                foreach (var collection in collections)
                {
                    AccountList.Children.Add(VisibilityCheckBox(collection, !hidden.Contains(collection.Id)));
                }
            }
        }

        EmptyPanel.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewHost.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AddAccountLink.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AddRadicaleButton.IsEnabled = AddAccountLink.IsEnabled = Store is not null;
    }

    /// <summary>Account name with its options menu (Remove account, account-management R8).</summary>
    private Grid AccountHeader(Account account)
    {
        var more = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove account…", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += async (_, _) => await RemoveAccountAsync(account);
        more.Items.Add(remove);

        var header = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = account.DisplayName,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Theme.Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
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

    /// <summary>Visibility checkbox with the collection's shape marker in its colour (ui-colour-theme R7).</summary>
    private CheckBox VisibilityCheckBox(Collection collection, bool visible)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch));
        label.Children.Add(new TextBlock
        {
            Text = collection.SourceUrl is not null
                ? Core.Sync.FeedSync.FetchUrl(collection.SourceUrl) is null ? $"{collection.Name} (not secure, not synced)" : $"{collection.Name} (subscribed)"
                : collection.ReadOnly ? $"{collection.Name} (read-only)" : collection.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var check = new CheckBox { Content = label, IsChecked = visible, MinWidth = 0 };
        check.Click += (_, _) =>
        {
            Store?.SetCollectionHidden(collection.Id, check.IsChecked != true);
            RenderView();
        };
        return check;
    }

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
            Content = "Its calendars, tasks and any changes that have not synced are deleted from this PC, and its saved "
                + "password is removed. Nothing is deleted on the server.",
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
            CredentialVault.Remove(account.Id);
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
