using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Identity.Client;
using Serilog;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

namespace WindowsCalendar;

/// <summary>
/// Colleague calendars (ms365-colleague-calendars R1, R2, R8): added to an MS365 account by email from its sidebar
/// menu, removed from the calendar's right-click menu. Only ids are logged, never the email address.
/// </summary>
public sealed partial class MainWindow
{
    private async Task AddColleagueAsync(Account account)
    {
        if (Store is not { } store || App.Ms365 is not { } auth || account.Username is not { } username)
        {
            return;
        }
        var input = new Wpf.Ui.Controls.TextBox { PlaceholderText = "name@company.com" };
        var dialog = new ContentDialog(DialogHost)
        {
            Title = "Add colleague's calendar",
            Content = ViewParts.Field("Email address", input),
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = input.Text.Trim() is { Length: > 2 } t && t.Contains('@');
        input.Loaded += (_, _) => input.Focus();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        var email = input.Text.Trim().ToLowerInvariant();

        // R2: not twice, and not a calendar Microsoft 365 already lists.
        if (store.GetCollections(account.Id).Any(c => GraphSync.ColleagueEmail(c) == email))
        {
            await ShowMessageAsync("Already added", "This colleague's calendar is already in the sidebar.");
            return;
        }
        string? token = null;
        try
        {
            token = await auth.GetTokenAsync(username, default);
            var client = new GraphClient(App.GraphHttp, token);
            if ((await GraphSync.ListedOwnersAsync(client, default)).Contains(email))
            {
                await ShowMessageAsync("Already listed", "Microsoft 365 already lists this person's calendar for this account.");
                return;
            }
            var (access, colleague) = await GraphSync.FindColleagueAsync(client, email, default);
            if (access == ColleagueAccess.FreeBusyOnly)
            {
                await ShowMessageAsync("Only free/busy is shared",
                    "This colleague shares only free/busy (or nothing) with you, so their events can't be read. They can share "
                    + "more in Outlook: Share calendar, then \"Can view all details\".");
                return;
            }
            if (colleague is not { } found)
            {
                await ShowMessageAsync("Calendar not available",
                    "Check the address. If it is right, the colleague needs to share their calendar with you in Outlook.");
                return;
            }
            var added = store.AddDiscoveredCollection(account.Id, found);
            Log.Information("Colleague calendar {CollectionId} added to account {AccountId}", added.Id, account.Id);
            RefreshView();
            ((App)Application.Current).RequestSync("colleague calendar added");
        }
        catch (MsalUiRequiredException)
        {
            await ShowMessageAsync("Sign in again", "This account needs to sign in again before a calendar can be added.");
        }
        catch (MsalException ex)
        {
            Log.Warning("Colleague calendar: token request failed: {Code}", ex.ErrorCode);
            await ShowMessageAsync("Could not add the calendar", "Microsoft 365 sign-in did not complete. The log has the details.");
        }
        catch (GraphException ex)
        {
            Log.Warning("Colleague calendar could not be checked: {Error} {Status}", ex.Error, (int?)ex.Status);
            await ShowMessageAsync("Could not add the calendar", ex.Error == GraphError.Network
                ? "Microsoft 365 could not be reached. Check your connection and try again."
                : "Microsoft 365 refused the request. The log has the details.");
        }
        finally
        {
            token = null;
        }
    }

    private async Task RemoveColleagueAsync(Collection collection)
    {
        var confirm = Destructive.Apply(new ContentDialog(DialogHost)
        {
            Title = $"Remove {collection.Name}?",
            Content = "This calendar and its events are removed from this PC. Nothing changes in your colleague's calendar.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await confirm.ShowAsync() != ContentDialogResult.Primary || Store is not { } store)
        {
            return;
        }
        store.RemoveCollection(collection.Id);
        Log.Information("Colleague calendar {CollectionId} removed", collection.Id);
        RefreshView();
    }
}
