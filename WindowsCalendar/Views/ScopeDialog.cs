using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsCalendar.Views;

/// <summary>Which part of a repeating series a change applies to (recurring-events R4–R6).</summary>
internal enum EditScope { Occurrence, Following, Series }

/// <summary>
/// Asks the scope of a change to a repeating event as a radio list, This event selected, with OK and Cancel
/// (calendar-views R6d, R7, R10).
/// </summary>
internal static class ScopeDialog
{
    /// <summary>
    /// The chosen scope, or null when cancelled. <paramref name="allowFollowing"/> false leaves out "This and following"
    /// (meetings you organise, recurring-events R6).
    /// </summary>
    public static async Task<EditScope?> AskAsync(XamlRoot root, string title, bool allowFollowing)
    {
        var options = new RadioButtons();
        options.Items.Add(new RadioButton { Content = "This event", Tag = EditScope.Occurrence });
        if (allowFollowing)
        {
            options.Items.Add(new RadioButton { Content = "This and following events", Tag = EditScope.Following });
        }
        options.Items.Add(new RadioButton { Content = "All events in the series", Tag = EditScope.Series });
        options.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = options,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && options.SelectedItem is RadioButton { Tag: EditScope scope }
            ? scope
            : null;
    }
}
