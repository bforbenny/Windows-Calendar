using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WindowsCalendar.Core.Store;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WindowsCalendar;
/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // WinUI 3 does not use the package logo for the window; Alt-Tab needs this (app-platform R10).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Closing hides to the tray; only Quit really closes (app-platform R5).
        AppWindow.Closing += (sender, args) =>
        {
            if (!_quitting)
            {
                args.Cancel = true;
                sender.Hide();
            }
        };

        ShowAccounts();
    }

    private static LocalStore? Store => ((App)Application.Current).Store;

    /// <summary>
    /// Interim sidebar: each account with its calendars and task lists, and the first-launch panel when there
    /// are none (ui-states R5). Calendar colours, shapes and checkboxes come with calendar-views.
    /// </summary>
    private void ShowAccounts()
    {
        AccountList.Children.Clear();
        var accounts = Store?.GetAccounts() ?? [];
        foreach (var account in accounts)
        {
            var more = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = "Remove account…", Icon = new SymbolIcon(Symbol.Delete) };
            remove.Click += async (_, _) => await RemoveAccountAsync(account);
            more.Items.Add(remove);

            var header = new Grid { Margin = new Thickness(0, 12, 0, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = account.DisplayName,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var moreButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
                Flyout = more,
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(moreButton, $"{account.DisplayName} options");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(moreButton, $"{account.DisplayName} options");
            Grid.SetColumn(moreButton, 1);
            header.Children.Add(moreButton);
            AccountList.Children.Add(header);

            foreach (var collection in Store!.GetCollections(account.Id))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(4, 2, 0, 2) };
                // Calendar glyph, or checkbox glyph for a task list.
                row.Children.Add(new FontIcon { Glyph = collection.Kind == CollectionKind.Calendar ? "\uE787" : "\uE73A", FontSize = 14 });
                row.Children.Add(new TextBlock
                {
                    Text = collection.ReadOnly ? $"{collection.Name} (read-only)" : collection.Name,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                AccountList.Children.Add(row);
            }
        }

        EmptyPanel.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddAccountLink.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AddRadicaleButton.IsEnabled = AddAccountLink.IsEnabled = Store is not null;
    }

    private async void OnAddRadicale(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        var dialog = new AddRadicaleDialog(store) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
        if (dialog.AddedAccount is not null)
        {
            ShowAccounts();
            ((App)Application.Current).RequestSync("account added");
        }
    }

    /// <summary>Destructive: asks first, then deletes cached data and the saved password (account-management R8).</summary>
    private async System.Threading.Tasks.Task RemoveAccountAsync(Account account)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Remove {account.DisplayName}?",
            Content = "Its calendars, tasks and any changes that have not synced are deleted from this PC, and its saved "
                + "password is removed. Nothing is deleted on the server.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            Store!.RemoveAccount(account.Id);
            CredentialVault.Remove(account.Id);
            Log.Information("Account {AccountId} removed", account.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Removing account {AccountId} failed", account.Id);
        }
        ShowAccounts();
    }

    public void ShowAndFocus()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
        Activate();
    }

    public void CloseForQuit()
    {
        _quitting = true;
        Close();
    }
}
