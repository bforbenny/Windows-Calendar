using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsCalendar.Views;

/// <summary>Which part of a repeating series a change applies to (recurring-events interim: no split yet).</summary>
internal enum EditScope { Occurrence, Series }

/// <summary>Asks This event / Entire series / Cancel for a change to a repeating event (calendar-views R6d, R7, R10).</summary>
internal static class ScopeDialog
{
    /// <summary>The chosen scope, or null when cancelled.</summary>
    public static async Task<EditScope?> AskAsync(XamlRoot root, string title)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = "This is a repeating event.",
            PrimaryButtonText = "This event",
            SecondaryButtonText = "Entire series",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => EditScope.Occurrence,
            ContentDialogResult.Secondary => EditScope.Series,
            _ => null,
        };
    }
}
