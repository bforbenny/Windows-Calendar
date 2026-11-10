using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

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
    /// (meetings you organise, recurring-events R6); <paramref name="allowOccurrence"/> false leaves out "This event"
    /// (a rule change, R3). A <paramref name="delete"/> asks with a red Delete button instead of OK (ui-colour-theme R3), and
    /// Cancel is the default.
    /// </summary>
    public static async Task<EditScope?> AskAsync(string title, bool allowFollowing, bool allowOccurrence = true, bool delete = false)
    {
        var options = new SpacedStack { Spacing = 4 };
        void Add(string text, EditScope scope) =>
            options.Children.Add(new RadioButton { Content = text, Tag = scope, GroupName = "scope", IsChecked = options.Children.Count == 0 });
        if (allowOccurrence)
        {
            Add("This event", EditScope.Occurrence);
        }
        if (allowFollowing)
        {
            Add("This and following events", EditScope.Following);
        }
        Add("All events in the series", EditScope.Series);

        var dialog = new ContentDialog(ViewParts.DialogHost)
        {
            Title = title,
            Content = options,
            PrimaryButtonText = delete ? "Delete" : "OK",
            CloseButtonText = "Cancel",
            DefaultButton = delete ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (delete)
        {
            Destructive.Apply(dialog);
        }
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            && options.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true) is { Tag: EditScope scope }
            ? scope
            : null;
    }
}
