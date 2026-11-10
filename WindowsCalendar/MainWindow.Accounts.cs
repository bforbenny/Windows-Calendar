using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Serilog;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

namespace WindowsCalendar;

/// <summary>
/// Adding and removing accounts from the sidebar and the first-launch panel (account-management R2, R4, R8):
/// Microsoft 365 through WAM, Radicale through <see cref="AddRadicaleDialog"/>.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Account name, collapsing its group, with its options menu (Remove account, account-management R8).</summary>
    private Grid AccountHeader(Account account, string flag, UIElement group)
    {
        var header = new Grid { Margin = new Thickness(0, 8, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Collapser(flag, new TextBlock
        {
            Text = account.DisplayName,
            Style = Theme.Style("CaptionTextBlockStyle"),
            Foreground = Theme.Brush("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, group));

        var more = ViewParts.IconButton("", $"{account.DisplayName} options");
        more.Padding = new Thickness(6);
        var remove = new MenuItem { Header = "Remove account…", Icon = ViewParts.Icon("", 14) };
        remove.Click += async (_, _) => await RemoveAccountAsync(account);
        var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
        menu.Items.Add(remove);
        more.Click += (_, _) => menu.IsOpen = true;
        Grid.SetColumn(more, 1);
        header.Children.Add(more);
        return header;
    }

    /// <summary>The sidebar's Add account link: Microsoft 365 or Radicale.</summary>
    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var ms365 = new MenuItem { Header = "Microsoft 365" };
        ms365.Click += OnAddMs365;
        var radicale = new MenuItem { Header = "Radicale" };
        radicale.Click += OnAddRadicale;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        menu.Items.Add(ms365);
        menu.Items.Add(radicale);
        menu.IsOpen = true;
    }

    /// <summary>
    /// MS365 sign-in through WAM (account-management R2), then the user's name and calendars are read and saved. The
    /// access token is used for these two calls only.
    /// </summary>
    private async void OnAddMs365(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        if (App.Ms365 is not { } auth)
        {
            await ShowMessageAsync("Microsoft 365 is not set up",
                "Fill in the app registration's client ID and tenant ID in ms365.json, then rebuild the app.");
            return;
        }
        string? token = null;
        try
        {
            AddMs365Button.IsEnabled = AddAccountLink.IsEnabled = false;
            (var username, token) = await auth.SignInAsync(new WindowInteropHelper(this).Handle, default);
            if (store.GetAccounts().FirstOrDefault(a => a.Type == AccountType.MS365 && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                // Signing in an account that is already here resumes its sync (e.g. after "needs to sign in again").
                ((App)Application.Current).ResumeAccount(existing.Id);
                Log.Information("MS365 account {AccountId} signed in again", existing.Id);
                await ShowMessageAsync("Signed in", "This Microsoft 365 account is already in the app; it is signed in again and will sync now.");
                return;
            }
            var client = new Core.Graph.GraphClient(App.GraphHttp, token);
            var (name, email) = await client.GetMeAsync(default);
            var found = await Core.Graph.GraphSync.DiscoverAsync(client, default);
            var account = new Account(Guid.NewGuid().ToString("N"), AccountType.MS365, name, Username: username, Email: email);
            store.AddDiscoveredAccount(account, found);
            Log.Information("MS365 account {AccountId} added with {Count} calendars", account.Id, found.Count);
            RefreshView();
            ((App)Application.Current).RequestSync("account added");
        }
        catch (Microsoft.Identity.Client.MsalClientException ex) when (ex.ErrorCode == Microsoft.Identity.Client.MsalError.AuthenticationCanceledError)
        {
            // The user closed the sign-in window.
        }
        catch (Microsoft.Identity.Client.MsalException ex)
        {
            Log.Warning("MS365 sign-in failed: {Code}", ex.ErrorCode);
            await ShowMessageAsync("Sign-in failed", $"Microsoft 365 sign-in did not complete ({ex.ErrorCode}).");
        }
        catch (Core.Graph.GraphException ex)
        {
            Log.Warning("MS365 account setup failed: {Error} {Status}", ex.Error, (int?)ex.Status);
            await ShowMessageAsync("Could not read the account", ex.Error == Core.Graph.GraphError.Network
                ? "Microsoft 365 could not be reached. Check your connection and try again."
                : "Microsoft 365 refused the request. The log has the details.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Adding the MS365 account failed");
            await ShowMessageAsync("Could not add the account", "The log has the details.");
        }
        finally
        {
            token = null;
            AddMs365Button.IsEnabled = AddAccountLink.IsEnabled = true;
        }
    }

    private async Task ShowMessageAsync(string title, string message) =>
        await new ContentDialog(DialogHost) { Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();

    private async void OnAddRadicale(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        var dialog = new AddRadicaleDialog(DialogHost, store);
        await dialog.ShowAsync();
        if (dialog.AddedAccount is not null)
        {
            RefreshView();
            ((App)Application.Current).RequestSync("account added");
        }
    }

    /// <summary>Destructive: asks first, then deletes cached data and the saved password (account-management R8).</summary>
    private async Task RemoveAccountAsync(Account account)
    {
        var confirm = Destructive.Apply(new ContentDialog(DialogHost)
        {
            Title = $"Remove {account.DisplayName}?",
            Content = "Its calendars, tasks and any changes that have not synced are deleted from this PC, and "
                + (account.Type == AccountType.MS365 ? "it is signed out of this app." : "its saved password is removed.")
                + " Nothing is deleted on the server.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            Store!.RemoveAccount(account.Id);
            if (account.Type == AccountType.MS365)
            {
                if (App.Ms365 is { } auth && account.Username is { } username)
                {
                    await auth.SignOutAsync(username);
                }
            }
            else
            {
                CredentialVault.Remove(account.Id);
            }
            Log.Information("Account {AccountId} removed", account.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Removing account {AccountId} failed", account.Id);
        }
        RefreshView();
    }
}
