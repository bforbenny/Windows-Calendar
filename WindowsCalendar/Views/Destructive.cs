using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsCalendar.Views;

/// <summary>
/// The Destructive colour role (ui-colour-theme R3) on a confirmation's primary button: delete, remove, discard, or a
/// change that loses edits. Cancel stays the default button, so Enter never confirms by accident, but is grey: it
/// neither adds nor destroys.
/// </summary>
internal static class Destructive
{
    public static ContentDialog Apply(ContentDialog dialog)
    {
        dialog.PrimaryButtonStyle = Theme.Style("DestructiveButtonStyle");
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Views/NeutralDefaultButton.xaml"),
        });
        return dialog;
    }
}
