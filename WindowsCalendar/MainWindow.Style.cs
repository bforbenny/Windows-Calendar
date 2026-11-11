using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Serilog;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// Colour and icon overrides from the sidebar (ui-colour-theme interim): right-click a calendar or task list for
/// Colour, Icon and, after a colour override, Use server colour. Stored locally only.
/// </summary>
public sealed partial class MainWindow
{
    private const int PickerColumns = 8;
    private static readonly string[] Hues = ["Navy", "Sea Green", "Strawberry Red", "Amber", "Purple", "Teal", "Orange", "Pink"];

    /// <summary>The right-click menu of a sidebar collection.</summary>
    private ContextMenu StyleMenu(Collection collection, FrameworkElement target)
    {
        var menu = new ContextMenu();
        var colour = new MenuItem { Header = "Colour…", Icon = ViewParts.Icon("", 14) };
        colour.Click += (_, _) => ShowPicker(target, ColourPicker(collection));
        var icon = new MenuItem { Header = "Icon…", Icon = ViewParts.Icon("", 14) };
        icon.Click += (_, _) => ShowPicker(target, IconPicker(collection));
        menu.Items.Add(colour);
        menu.Items.Add(icon);
        if (collection.ColourOverride && collection.ServerColour is not null)
        {
            var server = new MenuItem { Header = "Use server colour" };
            server.Click += (_, _) => ApplyStyle(collection, s => s.UseServerColour(collection.Id), "server colour");
            menu.Items.Add(server);
        }
        if (collection.Kind == CollectionKind.Calendar)
        {
            AddHolidayItems(menu, collection);
        }
        if (Core.Graph.GraphSync.ColleagueEmail(collection) is not null)
        {
            // ms365-colleague-calendars R8.
            var remove = new MenuItem { Header = "Remove…", Icon = ViewParts.Icon("", 14) };
            remove.Click += async (_, _) => await RemoveColleagueAsync(collection);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
        }
        return menu;
    }

    /// <summary>
    /// Public holiday calendar (calendar-shading R3, R5): marking one replaces any other; "Show as shading only"
    /// shows while it is marked. Read on opening, since another calendar's menu may have moved the role.
    /// </summary>
    private void AddHolidayItems(ContextMenu menu, Collection collection)
    {
        var holiday = new MenuItem { Header = "Public holiday calendar", IsCheckable = true };
        var shadingOnly = new MenuItem { Header = "Show as shading only", IsCheckable = true };
        holiday.Click += (_, _) => ApplyStyle(collection,
            s => s.SetHolidayCollection(holiday.IsChecked ? collection.Id : null), $"holiday calendar {holiday.IsChecked}");
        shadingOnly.Click += (_, _) => ApplyStyle(collection,
            s => s.SetHolidayShadingOnly(shadingOnly.IsChecked), $"holiday shading only {shadingOnly.IsChecked}");
        menu.Opened += (_, _) =>
        {
            var marked = Store?.GetHolidayCollectionId() == collection.Id;
            holiday.IsChecked = marked;
            shadingOnly.IsChecked = Store?.GetHolidayShadingOnly() != false;
            shadingOnly.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(holiday);
        menu.Items.Add(shadingOnly);
    }

    private static void ShowPicker(FrameworkElement target, FrameworkElement content)
    {
        var popup = new Popup
        {
            Child = new Border
            {
                Child = content,
                Background = Theme.Brush("SurfaceBaseBrush"),
                BorderBrush = Theme.Brush("DividerBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
            },
            PlacementTarget = target,
            Placement = PlacementMode.Right,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        content.Tag = popup;
        popup.IsOpen = true;
    }

    /// <summary>The 24 colours: hues across, levels 1-3 down; the current one has a check mark.</summary>
    private Grid ColourPicker(Collection collection)
    {
        var grid = new Grid();
        for (var c = 0; c < PickerColumns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (var level = 1; level <= 3; level++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var h = 0; h < Hues.Length; h++)
            {
                var value = $"{Hues[h]}/L{level}";
                var brushes = Theme.Calendar(value);
                var swatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Background = brushes.Swatch };
                if (value == collection.Colour)
                {
                    var check = ViewParts.Icon("", 12);
                    check.Foreground = Theme.Brush("SurfaceBaseBrush");
                    check.HorizontalAlignment = HorizontalAlignment.Center;
                    swatch.Child = check;
                }
                var button = PickerButton(swatch, $"{Hues[h]}, level {level}");
                button.Click += (_, _) =>
                {
                    Close(grid);
                    ApplyStyle(collection, s => s.SetCollectionColour(collection.Id, value), value);
                };
                Grid.SetRow(button, level - 1);
                Grid.SetColumn(button, h);
                grid.Children.Add(button);
            }
        }
        return grid;
    }

    /// <summary>The 5 shapes, then the Fluent icons, in the collection's colour; the current one is highlighted.</summary>
    private Grid IconPicker(Collection collection)
    {
        var swatch = Theme.Calendar(collection.Colour).Swatch;
        var choices = ShapeMarker.Shapes.Select(s => (Value: s.Shape, s.Name))
            .Concat(ShapeMarker.Icons.Select(i => (Value: ShapeMarker.GlyphPrefix + i.Code, i.Name)))
            .ToList();
        var grid = new Grid();
        for (var c = 0; c < PickerColumns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (var i = 0; i < choices.Count; i++)
        {
            if (i % PickerColumns == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var (value, name) = choices[i];
            var cell = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                BorderBrush = swatch,
                BorderThickness = new Thickness(value == collection.Shape ? 2 : 0),
                Child = ShapeMarker.Create(value, swatch, 16),
            };
            ((FrameworkElement)cell.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var button = PickerButton(cell, name);
            button.Click += (_, _) =>
            {
                Close(grid);
                ApplyStyle(collection, s => s.SetCollectionShape(collection.Id, value), value);
            };
            Grid.SetRow(button, i / PickerColumns);
            Grid.SetColumn(button, i % PickerColumns);
            grid.Children.Add(button);
        }
        return grid;
    }

    private static Button PickerButton(UIElement content, string name)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(2),
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = name,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static void Close(FrameworkElement picker)
    {
        if (picker.Tag is Popup popup)
        {
            popup.IsOpen = false;
        }
    }

    /// <summary>Saves a style change and redraws the sidebar and views.</summary>
    private void ApplyStyle(Collection collection, Action<LocalStore> change, string what)
    {
        if (Store is not { } store)
        {
            return;
        }
        try
        {
            change(store);
            Log.Information("Collection {CollectionId} style set: {Style}", collection.Id, what);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Setting the style of collection {CollectionId} failed", collection.Id);
        }
        RefreshView();
    }
}
