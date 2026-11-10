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
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// The main window: sidebar with accounts and calendars, toolbar, and the Week/Month/Agenda views, which scroll
/// naturally instead of paging (calendar-views R1a).
/// WPF port, first slice (app-platform interim): read-only views; the editor, dialogs, search, keyboard, banners,
/// settings and account adding follow in later slices.
/// </summary>
public sealed partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string SidebarHiddenFlag = "sidebar-hidden";
    private const string WeekNumbersFlag = "week-numbers";

    private readonly TimeGridView _week = new();
    private readonly MonthGridView _month = new();
    private readonly AgendaView _agenda = new();
    private readonly DispatcherTimer _minuteTimer = new();
    private readonly Dictionary<string, Collection> _collections = [];
    private ViewKind _view = ViewKind.Week;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly? _pendingDay;
    private bool _quitting;
    private bool _renderPending;
    private IReadOnlyList<DisplayEvent> _shownEvents = [];
    private HashSet<string> _joinShown = [];
    // Sidebar names of the collections being pulled now, for the hue wave (ui-states R1).
    private readonly HashSet<string> _syncing = [];
    private readonly Dictionary<string, TextBlock> _names = [];

    public MainWindow()
    {
        InitializeComponent();
        ViewParts.Root = (UIElement)Content;
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

        // Esc cancels a drag; Ctrl+B hides the sidebar (calendar-views R2b).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && EventDrag.CancelActive())
            {
                e.Handled = true;
            }
            else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ToggleSidebar();
            }
        };
        EventDrag.Ended += () =>
        {
            if (_renderPending)
            {
                RenderView();
            }
        };

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
        ViewParts.ShowWeekNumbers = Store?.GetUiFlag(WeekNumbersFlag) == true;
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
        _joinShown = JoinableNow();
        var view = (UIElement)CurrentView;
        if (ViewHost.Children.Count != 1 || ViewHost.Children[0] != view)
        {
            ViewHost.Children.Clear();
            ViewHost.Children.Add(view);
        }
        CurrentView.SetData(first, days, events, holidays);
        Log.Information("{View} view loaded in {Ms} ms ({Count} occurrences)", _view, watch.ElapsedMilliseconds, events.Count);
    }

    /// <summary>A task with a due date on the calendar (tasks R6 interim); it is never dragged.</summary>
    private static DisplayEvent TaskItem(OccurrenceView o, Collection list)
    {
        var e = DisplayEvent.From(o, list) with { Editable = false, IsTask = true };
        return e.AllDay || e.End > e.Start ? e : e with { End = e.Start.AddMinutes(30) };
    }

    /// <summary>Whether an event entered or left its Join window since the last render (online-meeting-join R4).</summary>
    private bool JoinChanged() => !JoinableNow().SetEquals(_joinShown);

    private HashSet<string> JoinableNow()
    {
        var now = DateTime.Now;
        return _shownEvents.Where(e => e.JoinNow(now)).Select(e => e.Key).ToHashSet();
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
                var header = Collapser($"collapsed:{kind}:{account.Id}", new TextBlock
                {
                    Text = account.DisplayName,
                    Style = Theme.Style("CaptionTextBlockStyle"),
                    Foreground = Theme.Brush("TextSecondaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }, group);
                header.Margin = new Thickness(0, 8, 0, 2);
                section.Children.Add(header);
                section.Children.Add(group);
                foreach (var collection in collections)
                {
                    group.Children.Add(VisibilityCheckBox(collection, !hidden.Contains(collection.Id)));
                }
            }
        }

        EmptyPanel.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewHost.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    public void CloseForQuit()
    {
        _quitting = true;
        _minuteTimer.Stop();
        Close();
    }
}
