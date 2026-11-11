using System.Windows;
using WindowsCalendar.Views;

namespace WindowsCalendar;

/// <summary>
/// The Settings page (settings-page R1 interim): opened from the toolbar gear or Ctrl+,, in place of the toolbar and
/// views; Back or Esc returns. Changes redraw the views at once.
/// </summary>
public sealed partial class MainWindow
{
    private SettingsPage? _settings;

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Opens Settings on <paramref name="section"/> (default the first), e.g. "Calendar sets" from the set menu.</summary>
    private void OpenSettings(string? section = null)
    {
        if (_settings is null)
        {
            // Built on first use, once the theme is known.
            _settings = new SettingsPage(WeekStart);
            _settings.Changed += RenderView;
            // A renamed, moved or deleted set (calendar-sets R3): the selector, and the ticks if the active set went.
            _settings.SetsChanged += () =>
            {
                ShowAccounts();
                RenderView();
            };
            _settings.BackRequested += CloseSettings;
            SettingsHost.Child = _settings;
        }
        _settings.Open(section);
        SettingsHost.Visibility = Visibility.Visible;
    }

    private void CloseSettings() => SettingsHost.Visibility = Visibility.Collapsed;
}
