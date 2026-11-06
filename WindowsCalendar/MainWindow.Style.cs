using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
    private MenuFlyout StyleMenu(Collection collection, FrameworkElement target)
    {
        var menu = new MenuFlyout();
        var colour = new MenuFlyoutItem { Text = "Colour…", Icon = new FontIcon { Glyph = "" } };
        colour.Click += (_, _) => ShowPicker(target, ColourPicker(collection));
        var icon = new MenuFlyoutItem { Text = "Icon…", Icon = new FontIcon { Glyph = "" } };
        icon.Click += (_, _) => ShowPicker(target, IconPicker(collection));
        menu.Items.Add(colour);
        menu.Items.Add(icon);
        if (collection.ColourOverride && collection.ServerColour is not null)
        {
            var server = new MenuFlyoutItem { Text = "Use server colour" };
            server.Click += (_, _) => ApplyStyle(collection, s => s.UseServerColour(collection.Id), "server colour");
            menu.Items.Add(server);
        }
        if (collection.Kind == CollectionKind.Calendar)
        {
            AddHolidayItems(menu, collection);
        }
        return menu;
    }

    /// <summary>
    /// Public holiday calendar (calendar-shading R3, R5): marking one replaces any other; "Show as shading only"
    /// shows while it is marked. Read on opening, since another calendar's menu may have moved the role.
    /// </summary>
    private void AddHolidayItems(MenuFlyout menu, Collection collection)
    {
        var holiday = new ToggleMenuFlyoutItem { Text = "Public holiday calendar" };
        var shadingOnly = new ToggleMenuFlyoutItem { Text = "Show as shading only" };
        holiday.Click += (_, _) => ApplyStyle(collection,
            s => s.SetHolidayCollection(holiday.IsChecked ? collection.Id : null), $"holiday calendar {holiday.IsChecked}");
        shadingOnly.Click += (_, _) => ApplyStyle(collection,
            s => s.SetHolidayShadingOnly(shadingOnly.IsChecked), $"holiday shading only {shadingOnly.IsChecked}");
        menu.Opening += (_, _) =>
        {
            var marked = Store?.GetHolidayCollectionId() == collection.Id;
            holiday.IsChecked = marked;
            shadingOnly.IsChecked = Store?.GetHolidayShadingOnly() != false;
            shadingOnly.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
        };
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(holiday);
        menu.Items.Add(shadingOnly);
    }

    private static void ShowPicker(FrameworkElement target, UIElement content)
    {
        var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        if (content is FrameworkElement f)
        {
            f.Tag = flyout;
        }
        flyout.ShowAt(target);
    }

    /// <summary>The 24 colours: hues across, levels 1-3 down; the current one has a check mark.</summary>
    private Grid ColourPicker(Collection collection)
    {
        var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
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
                    swatch.Child = new FontIcon { Glyph = "", FontSize = 12, Foreground = Theme.Brush("SurfaceBaseBrush") };
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
        var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
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
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static void Close(FrameworkElement picker) => (picker.Tag as Flyout)?.Hide();

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
