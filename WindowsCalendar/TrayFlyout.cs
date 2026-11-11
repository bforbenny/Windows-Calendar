using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using WindowsCalendar.Core.QuickAdd;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// The tray flyout (tray-flyout R1-R4, R6): today's date and the next event, the all-day row, events in progress, the
/// rest of today and tomorrow, and tasks due, with Join on meetings. A borderless window created once and hidden rather
/// than closed; clicking outside or Esc hides it. Clicking an item opens it in the main window.
/// </summary>
public sealed partial class TrayFlyout : Window
{
    private const double FlyoutWidth = 360;
    private const double MaxFlyoutHeight = 560;

    private readonly TextBlock _date = new();
    private readonly TextBlock _next = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 4, 16, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly QuickAddBox _quick = new(true);
    private readonly DispatcherTimer _minuteTimer = new();
    private Dictionary<string, Collection> _collections = [];
    private DateTime _hiddenAt = DateTime.MinValue;

    public TrayFlyout()
    {
        Title = "WindowsCalendar";
        Width = FlyoutWidth;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "SurfaceBaseBrush");

        _date.Style = Theme.Style("BodyStrongTextBlockStyle");
        _date.TextTrimming = TextTrimming.CharacterEllipsis;
        _date.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _next.Style = Theme.Style("CaptionTextBlockStyle");
        _next.TextTrimming = TextTrimming.CharacterEllipsis;
        _next.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        header.Children.Add(_date);
        header.Children.Add(_next);

        // Footer (R3): sync status, Sync now, Open calendar.
        var open = new Button { Content = "Open calendar", Margin = new Thickness(8, 0, 0, 0) };
        open.Click += (_, _) =>
        {
            HideFlyout();
            App.ShowMainWindow();
        };
        var sync = ViewParts.IconButton("", "Sync now");
        sync.Click += (_, _) => App.SyncNow();
        _status.Style = Theme.Style("CaptionTextBlockStyle");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var footerRow = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(sync, Dock.Right);
        footerRow.Children.Add(open);
        footerRow.Children.Add(sync);
        footerRow.Children.Add(_status);
        var footer = new Border { Child = footerRow, Padding = new Thickness(16, 8, 12, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");

        // Quick add (R3, R4b): created through the main window, so it goes on its undo stack.
        _quick.Options = () => App.Calendar is { } main ? main.QuickOptions(null, false) with { TaskByDefault = false }
            : new QuickAddOptions(false, []);
        _quick.CollectionOf = id => _collections.GetValueOrDefault(id);
        _quick.Submitted += OnQuickAdd;
        _quick.MoreDetails += result =>
        {
            HideFlyout();
            App.ShowMainWindow();
            if (App.Calendar?.QuickMoreDetails(result) == true)
            {
                _quick.Clear();
            }
        };
        _quick.CloseRequested += HideFlyout;
        var quick = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        quick.Children.Add(_quick);
        _message.Style = Theme.Style("CaptionTextBlockStyle");
        quick.Children.Add(_message);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(quick, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(quick);
        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 0, 8, 8),
        };
        // R8: Up/Down move from row to row (the scroll viewer would otherwise scroll), stopping at the ends.
        scroll.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || FocusedRow() is not { } row)
            {
                return;
            }
            var rows = Rows();
            var i = rows.IndexOf(row) + (e.Key == Key.Down ? 1 : -1);
            if (i >= 0 && i < rows.Count)
            {
                FocusRow(rows[i]);
            }
            e.Handled = true;
        };
        root.Children.Add(scroll);
        Content = root;

        SourceInitialized += (_, _) => RoundCorners();
        // Clicking outside (another window, the desktop, the taskbar) hides the flyout (R2).
        Deactivated += (_, _) => HideFlyout();
        // Bubbling, so the quick-add box closes its suggestions on Esc first.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideFlyout();
            }
        };
        // Moving onto a monitor with another DPI resizes the window; put it back in place at the new scale.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Place(ActualHeight));

        // R4a: after each sync while visible (the system-change hook and resume ask for a sync run, so they land here
        // too), and every minute while visible, which also covers midnight.
        App.SyncStateChanged += ended =>
        {
            ShowStatus();
            if (ended && IsVisible)
            {
                RefreshVisible();
            }
        };
        _minuteTimer.Tick += (_, _) =>
        {
            _minuteTimer.Stop();
            if (IsVisible)
            {
                RefreshVisible();
                StartMinuteTimer();
            }
        };
    }

    private static App App => (App)Application.Current;

    private void StartMinuteTimer()
    {
        var now = DateTime.Now;
        _minuteTimer.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(200);
        _minuteTimer.Start();
    }

    /// <summary>"Syncing…", "Offline" or "Synced 10:42" (R3 footer).</summary>
    private void ShowStatus() =>
        _status.Text = App.SyncRunning ? "Syncing…"
            : !App.Online ? "Offline"
            : App.LastSynced is { } last ? $"Synced {last.ToString("t", CultureInfo.CurrentCulture)}"
            : "";

    /// <summary>A line under the quick-add box: what was just done, or why it could not be (prerequisite).</summary>
    private void ShowMessage(string? text, bool error = false)
    {
        _message.Text = text ?? "";
        _message.SetResourceReference(TextBlock.ForegroundProperty, error ? "DestructiveBrush" : "TextSecondaryBrush");
        _message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnQuickAdd(QuickAddResult result)
    {
        var error = App.Calendar is { } main ? main.QuickCreate(result) : "Can't add this now.";
        if (error is not null)
        {
            ShowMessage(error, error: true);
            FitToContent();
            return;
        }
        _quick.Clear();
        ShowMessage(result.TargetHidden && result.Target is { } target ? $"Added to {target.Name} (hidden)"
            : result.Kind == QuickAddKind.Task ? "Task added" : "Event added");
        RefreshVisible();
    }

    /// <summary>The tray icon the flyout is anchored to.</summary>
    public H.NotifyIcon.Core.TrayIcon? TrayIcon { get; set; }

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>
    /// The tray's left click (R1, R2): shows or hides the flyout. A click within 300 ms of the flyout hiding itself is the
    /// click that hid it (on the tray icon), so it is ignored rather than reopening the flyout.
    /// </summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
        }
        else if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds >= 300)
        {
            ShowFlyout();
        }
    }

    public void HideFlyout()
    {
        if (IsVisible)
        {
            _hiddenAt = DateTime.UtcNow;
            _minuteTimer.Stop();
            Hide();
        }
    }

    private void ShowFlyout()
    {
        var watch = Stopwatch.StartNew();
        ShowMessage(null);
        ShowStatus();
        Refresh();
        FitToContent();
        Show();
        Activate();
        FocusRow(Rows().FirstOrDefault());
        StartMinuteTimer();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            Log.Information("Tray flyout opened in {Ms} ms", watch.ElapsedMilliseconds));
    }

    /// <summary>After a Windows timezone or clock change, without waiting for the sync run (timezone-handling R6, F6).</summary>
    internal void RefreshIfVisible()
    {
        if (IsVisible)
        {
            RefreshVisible();
        }
    }

    /// <summary>
    /// Rebuilds the list while the flyout is open, and resizes it to fit (R4a). A row with keyboard focus keeps it; if
    /// that row is gone, the row now in its place gets it (R8).
    /// </summary>
    private void RefreshVisible()
    {
        var focused = FocusedRow();
        var key = focused?.Tag;
        var index = focused is null ? -1 : Rows().IndexOf(focused);
        Refresh();
        FitToContent();
        if (focused is not null)
        {
            var rows = Rows();
            FocusRow(rows.FirstOrDefault(r => Equals(r.Tag, key)) ?? rows.ElementAtOrDefault(Math.Min(index, rows.Count - 1)));
        }
    }

    /// <summary>The focusable rows of the list (all-day, events, tasks), top to bottom.</summary>
    private List<Button> Rows() => _list.Children.OfType<Button>().ToList();

    private Button? FocusedRow() => _list.Children.OfType<Button>().FirstOrDefault(r => r.IsKeyboardFocusWithin);

    /// <summary>Focuses a row and scrolls it into view; with no row, the quick-add box (R8).</summary>
    private void FocusRow(Button? row)
    {
        if (row is null)
        {
            _quick.FocusBox();
            return;
        }
        row.Focus();
        row.BringIntoView();
    }

    /// <summary>Height fits the content up to 560 epx (R2), then placed next to the tray icon.</summary>
    private void FitToContent()
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(FlyoutWidth, double.PositiveInfinity));
        var height = Math.Min(root.DesiredSize.Height, MaxFlyoutHeight);
        Height = height;
        Place(height);
    }

    /// <summary>Rebuilds the content from the store (R3, R4a: each open).</summary>
    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        var zone = TimeZoneInfo.Local;
        _list.Children.Clear();
        if (Store is not { } store || store.GetAccounts().Count == 0)
        {
            _date.Text = now.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            _next.Text = "";
            _list.Children.Add(Note("No accounts yet"));
            return;
        }

        var collections = _collections = store.GetAccounts().SelectMany(a => store.GetCollections(a.Id)).ToDictionary(c => c.Id);
        var today = now.Date;
        var from = new DateTimeOffset(today, zone.GetUtcOffset(today));
        var to = new DateTimeOffset(today.AddDays(2), zone.GetUtcOffset(today.AddDays(2)));
        var occurrences = store.GetOccurrenceViews(from, to).Where(o => collections.ContainsKey(o.CollectionId));
        var tasks = store.GetTasks().Where(t => collections.ContainsKey(t.CollectionId));
        var content = FlyoutList.Build(occurrences, tasks, store.GetHolidayCollectionId(), now, zone);

        var date = now.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        _date.Text = content.Holiday is { } holiday ? $"{date} · {holiday}" : date;
        _next.Text = FlyoutList.NextText(content, now, zone, CultureInfo.CurrentCulture);

        if (content.AllDay.Count + content.InProgress.Count + content.LaterToday.Count == 0)
        {
            _list.Children.Add(Note("No events today"));
        }
        AddEvents(null, content.AllDay, collections);
        AddEvents("Now", content.InProgress, collections);
        AddEvents(content.InProgress.Count > 0 ? "Later today" : null, content.LaterToday, collections);
        AddEvents("Tomorrow", content.Tomorrow, collections);

        _list.Children.Add(Heading("Tasks due"));
        if (content.Tasks.Count == 0)
        {
            _list.Children.Add(Note("No tasks due"));
        }
        var todayDate = DateOnly.FromDateTime(today);
        foreach (var task in content.Tasks)
        {
            _list.Children.Add(TaskRow(task, collections[task.CollectionId], TaskQuery.DueDay(task, zone) < todayDate));
        }
    }

    private void AddEvents(string? heading, IReadOnlyList<OccurrenceView> events, Dictionary<string, Collection> collections)
    {
        if (events.Count == 0)
        {
            return;
        }
        if (heading is not null)
        {
            _list.Children.Add(Heading(heading));
        }
        foreach (var o in events)
        {
            _list.Children.Add(EventRow(o, collections[o.CollectionId]));
        }
    }

    /// <summary>Time, calendar marker, title and location; Join by the views' rule (online-meeting-join R4).</summary>
    private Button EventRow(OccurrenceView o, Collection collection)
    {
        var e = DisplayEvent.From(o, collection);
        var row = new DockPanel();
        if (e.JoinNow(DateTime.Now) && e.Join is { } link)
        {
            var join = new Button { Content = "Join", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(join, $"Join {e.Title}");
            join.Click += (_, args) =>
            {
                args.Handled = true;
                HideFlyout();
                MeetingJoin.Launch(link);
            };
            DockPanel.SetDock(join, Dock.Right);
            row.Children.Add(join);
        }
        var marker = ShapeMarker.Create(collection.Shape, Theme.Calendar(collection.Colour).Swatch);
        marker.Margin = new Thickness(0, 4, 8, 0);
        marker.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(marker, Dock.Left);
        row.Children.Add(marker);
        var text = new StackPanel();
        text.Children.Add(Line(e.Title, "BodyTextBlockStyle", "TextPrimaryBrush"));
        var details = string.IsNullOrWhiteSpace(e.Location) ? e.TimeText() : $"{e.TimeText()} · {e.Location}";
        text.Children.Add(Line(details, "CaptionTextBlockStyle", "TextSecondaryBrush"));
        row.Children.Add(text);
        return ItemButton(row, e.Description(), $"{o.ItemId}|{o.StartUtc:o}",
            () => ((App)Application.Current).OpenItem(o.ItemId, o.StartUtc, o.RecurrenceUtc));
    }

    /// <summary>
    /// A due task with its checkbox (R3): ticking completes a Microsoft To Do task through the main window, then the
    /// list refreshes; Radicale tasks are view-only, so their checkbox is disabled (tasks).
    /// </summary>
    private Button TaskRow(Item task, Collection collection, bool overdue)
    {
        var row = new DockPanel();
        var check = new CheckBox
        {
            IsEnabled = App.Calendar?.IsWritableTaskList(collection) == true && !collection.ReadOnly,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        AutomationProperties.SetName(check, $"Complete {task.Summary}");
        check.Click += (_, e) =>
        {
            e.Handled = true;
            var error = App.Calendar is { } main ? main.TrySetTaskCompleted(task.Id, true) : "Can't change this task now.";
            ShowMessage(error, error: true);
            RefreshVisible();
        };
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        var title = string.IsNullOrWhiteSpace(task.Summary) ? "(No title)" : task.Summary;
        var text = new StackPanel();
        text.Children.Add(Line(title, "BodyTextBlockStyle", "TextPrimaryBrush"));
        text.Children.Add(Line(overdue ? $"Overdue · {collection.Name}" : collection.Name, "CaptionTextBlockStyle",
            overdue ? "NowLineBrush" : "TextSecondaryBrush"));
        row.Children.Add(text);
        var name = $"Task {title}, {(overdue ? "overdue" : "due today")}, {collection.Name}";
        return ItemButton(row, name, task.Id, () => ((App)Application.Current).OpenItem(task.Id, task.DueUtc, null));
    }

    /// <summary>A list row; <paramref name="key"/> finds the same row again after a refresh (R8).</summary>
    private Button ItemButton(UIElement content, string name, string key, Action open)
    {
        var button = new Button
        {
            Tag = key,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            ToolTip = name,
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            HideFlyout();
            open();
        };
        return button;
    }

    private static TextBlock Line(string text, string style, string brush)
    {
        var line = new TextBlock { Text = text, Style = Theme.Style(style), TextTrimming = TextTrimming.CharacterEllipsis };
        line.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return line;
    }

    private static TextBlock Heading(string text)
    {
        var heading = Line(text, "CaptionTextBlockStyle", "TextSecondaryBrush");
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(8, 10, 8, 2);
        return heading;
    }

    private static TextBlock Note(string text)
    {
        var note = Line(text, "BodyTextBlockStyle", "TextSecondaryBrush");
        note.Margin = new Thickness(8, 6, 8, 6);
        return note;
    }
}
