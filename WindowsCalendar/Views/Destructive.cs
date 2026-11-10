using Wpf.Ui.Controls;

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
        dialog.PrimaryButtonAppearance = ControlAppearance.Danger;
        dialog.CloseButtonAppearance = ControlAppearance.Secondary;
        return dialog;
    }
}
