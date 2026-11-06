using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsCalendar.Views;

/// <summary>
/// Asks before a change that drops changed occurrences (recurring-events R3 interim; MS365: Exchange drops a series'
/// exceptions when its time or pattern changes). True to go on; true at once when nothing is lost.
/// </summary>
internal static class LostDialog
{
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string action, int lost)
    {
        if (lost <= 0)
        {
            return true;
        }
        var dialog = Destructive.Apply(new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = lost == 1 ? "1 changed occurrence will be lost." : $"{lost} changed occurrences will be lost.",
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
