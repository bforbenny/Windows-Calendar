using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Serilog;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>
/// The secondary timezone setting (timezone-handling R8, calendar-sets R11): a toggle and a zone picker, used by
/// Settings &gt; Appearance and the set selector's "Secondary timezone…" dialog. Changes save at once into the current
/// ruler, which belongs to the active calendar set.
/// </summary>
internal static class RulerPicker
{
    /// <summary>The ruler's zone, null when it is off or Windows does not know its zone.</summary>
    public static TimeZoneInfo? Zone(LocalStore? store) =>
        store?.GetRuler() is { On: true, ZoneId: { } id } && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    /// <summary>The controls, showing the current ruler; <paramref name="changed"/> runs after each saved change.</summary>
    public static SpacedStack Build(LocalStore store, Action changed)
    {
        var current = store.GetRuler();
        var toggle = new Wpf.Ui.Controls.ToggleSwitch { Content = "Secondary timezone", IsChecked = current.On };
        // Typing jumps to a display name, e.g. "(UTC+10" (no new package for a filtered search).
        var zones = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsTextSearchEnabled = true, IsEnabled = current.On };
        AutomationProperties.SetName(zones, "Secondary timezone zone");
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var item = new ComboBoxItem { Content = zone.DisplayName, Tag = zone.Id };
            zones.Items.Add(item);
            if (zone.Id == (current.ZoneId ?? "UTC"))
            {
                zones.SelectedItem = item;
            }
        }

        void Save()
        {
            var ruler = new RulerChoice(toggle.IsChecked == true, (zones.SelectedItem as ComboBoxItem)?.Tag as string);
            zones.IsEnabled = ruler.On;
            store.SetRuler(ruler);
            Log.Information("Secondary timezone {State} ({Zone})", ruler.On ? "on" : "off", ruler.ZoneId ?? "none");
            changed();
        }
        toggle.Click += (_, _) => Save();
        zones.SelectionChanged += (_, _) => Save();

        var panel = new SpacedStack { Spacing = 8 };
        panel.Children.Add(toggle);
        panel.Children.Add(zones);
        panel.Children.Add(new TextBlock
        {
            Text = "Saved with the active calendar set.",
            Style = Theme.Style("CaptionTextBlockStyle"),
            Foreground = Theme.Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }
}
