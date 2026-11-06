using Microsoft.UI.Xaml.Controls;

namespace WindowsCalendar.Views;

/// <summary>
/// The Destructive colour role (ui-colour-theme R3) on a confirmation's primary button: delete, remove, discard, or a
/// change that loses edits. Cancel stays the default button, so Enter never confirms by accident.
/// </summary>
internal static class Destructive
{
    public static ContentDialog Apply(ContentDialog dialog)
    {
        // The accent button style, with its colours replaced for this dialog only (lightweight styling).
        dialog.PrimaryButtonStyle = Theme.Style("AccentButtonStyle");
        foreach (var (state, background) in new[] { ("", "DestructiveBrush"), ("PointerOver", "DestructiveHoverBrush"), ("Pressed", "DestructivePressedBrush") })
        {
            dialog.Resources["AccentButtonBackground" + state] = Theme.Brush(background);
            dialog.Resources["AccentButtonBorderBrush" + state] = Theme.Brush(background);
            dialog.Resources["AccentButtonForeground" + state] = Theme.Brush("TextOnDestructiveBrush");
        }
        return dialog;
    }
}
