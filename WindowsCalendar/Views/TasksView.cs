using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar.Views;

/// <summary>One task list's tasks as the Tasks view shows them; Writable is false for view-only lists (tasks interim).</summary>
internal sealed record TaskGroup(Account Account, Collection List, IReadOnlyList<Item> Tasks, bool Writable);

/// <summary>
/// The Tasks view (tasks R1 interim): filters All (open) / Today / Due in 7 days / Overdue / Completed, a "New task"
/// box, and one list of the tasks of all visible task lists, sorted by due date. Rows show the completed checkbox,
/// title, the task list (shape marker and name), due date (overdue in red) and "!" for high importance.
/// </summary>
internal sealed class TasksView : UserControl
{
    private readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private TaskFilter _filter = TaskFilter.All;
    private readonly TextBox _newTask = new Wpf.Ui.Controls.TextBox { PlaceholderText = "New task", MinWidth = 240 };
    private readonly SpacedStack _list = new() { Spacing = 4, Margin = new Thickness(16, 8, 16, 24) };
    private IReadOnlyList<TaskGroup> _groups = [];
    private DateOnly _today;

    public TasksView()
    {
        foreach (var filter in Enum.GetValues<TaskFilter>())
        {
            var text = filter == TaskFilter.DueIn7Days ? "Due in 7 days" : filter.ToString();
            var button = new ToggleButton { Content = text, Tag = filter, IsChecked = filter == TaskFilter.All, Margin = new Thickness(0, 0, 4, 0) };
            button.Click += (_, _) =>
            {
                _filter = filter;
                foreach (var b in _filters.Children.OfType<ToggleButton>())
                {
                    b.IsChecked = (TaskFilter)b.Tag == filter;
                }
                Build();
            };
            _filters.Children.Add(button);
        }
        _newTask.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _newTask.Text.Trim() is { Length: > 0 } title)
            {
                e.Handled = true;
                _newTask.Text = "";
                TaskAdded?.Invoke(title);
            }
        };
        AutomationProperties.SetName(_newTask, "New task");

        var bar = new Grid { Margin = new Thickness(16, 8, 16, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.Children.Add(_filters);
        Grid.SetColumn(_newTask, 1);
        _newTask.VerticalAlignment = VerticalAlignment.Center;
        _newTask.Margin = new Thickness(16, 0, 0, 0);
        bar.Children.Add(_newTask);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(bar);
        root.Children.Add(scroll);
        Content = root;
    }

    /// <summary>Raised with the task's item id when a row is clicked.</summary>
    public event Action<string>? TaskOpened;

    /// <summary>Raised with the task's item id and its new completed state when its checkbox is ticked or cleared.</summary>
    public event Action<string, bool>? TaskToggled;

    /// <summary>Raised with the title typed in the "New task" box.</summary>
    public event Action<string>? TaskAdded;

    private TaskFilter Filter => _filter;

    /// <summary>Replaces the tasks, keeping the filter.</summary>
    public void SetData(IReadOnlyList<TaskGroup> groups, DateOnly today)
    {
        (_groups, _today) = (groups, today);
        Build();
    }

    private void Build()
    {
        _list.Children.Clear();
        var zone = TimeZoneInfo.Local;
        var groupOf = _groups.ToDictionary(g => g.List.Id);
        var tasks = TaskQuery.Apply(_groups.SelectMany(g => g.Tasks), Filter, _today, zone);
        foreach (var task in tasks)
        {
            _list.Children.Add(Row(task, groupOf[task.CollectionId], zone));
        }
        if (tasks.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = Filter == TaskFilter.Completed ? "No completed tasks" : "No tasks",
                Foreground = Theme.Brush("TextSecondaryBrush"),
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    private Border Row(Item task, TaskGroup group, TimeZoneInfo zone)
    {
        var completed = TaskQuery.IsCompleted(task);
        var title = string.IsNullOrWhiteSpace(task.Summary) ? "(No title)" : task.Summary;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox { IsChecked = completed, IsEnabled = group.Writable, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(check, $"Completed: {title}");
        check.Click += (_, _) => TaskToggled?.Invoke(task.Id, check.IsChecked == true);
        // The checkbox only completes; it does not open the task (its click is handled before the row sees it).
        row.Children.Add(check);

        var text = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = completed ? TextDecorations.Strikethrough : null,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = Theme.Brush(completed ? "TextSecondaryBrush" : "TextPrimaryBrush"),
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // The task list inline, before the due date; the account in its tooltip.
        var list = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        list.Children.Add(ShapeMarker.Create(group.List.Shape, Theme.Calendar(group.List.Colour).Swatch));
        list.Children.Add(new TextBlock { Text = group.List.Name, Foreground = Theme.Brush("TextSecondaryBrush") });
        list.ToolTip = $"{group.List.Name} ({group.Account.DisplayName})";
        Grid.SetColumn(list, 2);
        row.Children.Add(list);

        if (TaskQuery.DueDay(task, zone) is { } due)
        {
            var overdue = !completed && due < _today;
            var dueText = new TextBlock
            {
                Text = due == _today ? "Today" : due.ToString("d"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(overdue ? "NowLineBrush" : "TextSecondaryBrush"),
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(dueText, 3);
            row.Children.Add(dueText);
        }
        if (TaskQuery.IsHighImportance(task))
        {
            var important = new TextBlock
            {
                Text = "!",
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "High importance",
            };
            Grid.SetColumn(important, 4);
            row.Children.Add(important);
        }

        var card = new Border
        {
            Child = row,
            Padding = new Thickness(4, 2, 4, 2),
            Background = ViewParts.Clickable,
            CornerRadius = new CornerRadius(4),
        };
        card.MouseLeftButtonUp += (_, _) => TaskOpened?.Invoke(task.Id);
        AutomationProperties.SetName(card, $"{title}, {group.List.Name}");
        return card;
    }
}
