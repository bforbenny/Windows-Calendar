using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Identity.Client;
using Serilog;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;

namespace WindowsCalendar;

/// <summary>
/// The toolbar sync status, the Offline, Sign-in needed and Sync error bars, the first-sync text (ui-states R1-R3), and
/// the pending changes list with Retry and Discard (local-store-offline R7, R9a).
/// </summary>
public sealed partial class MainWindow
{
    private string? _firstSyncText;
    // 0 none yet, 1 Offline, 2 changes waiting, 3 Synced (screen-reader announcements).
    private int _statusKind;

    private void SetupStatus()
    {
        ((App)Application.Current).SyncStateChanged += _ => ShowStatus();
        if (Store is { } store)
        {
            // A local edit changes the "n changes waiting" count at once.
            store.LocalChanged += () => Dispatcher.BeginInvoke(ShowStatus);
        }
        ShowStatus();
    }

    /// <summary>Shows the status, the bars and the first-sync text from the latest outcomes; on the UI thread.</summary>
    private void ShowStatus()
    {
        var app = (App)Application.Current;
        var culture = CultureInfo.CurrentCulture;
        var accounts = Store?.GetAccounts() ?? [];
        var health = app.Health;
        var offline = accounts.Count > 0 && app.Offline;
        var pending = Store?.GetPendingChanges() ?? [];

        var text = accounts.Count == 0 ? "" : SyncHealth.StatusText(app.SyncRunning, offline, pending.Count, app.LastOk, culture);
        SyncStatusText.Text = text;
        // The status lives in the Sync button's tooltip (ui-states R1); the badge shows the waiting count (R3).
        SyncButton.ToolTip = text.Length == 0 ? "Sync now (F5)" : $"Sync now (F5) · {text}";
        AutomationProperties.SetName(SyncButton, text.Length == 0 ? "Sync now" : $"Sync now, {text}");
        PendingBadge.Content = pending.Count > 99 ? "99+" : pending.Count.ToString(culture);
        PendingBadge.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(PendingBadge, $"{SyncHealth.StatusText(false, false, pending.Count, null, culture)}, show list");
        // Announced when it moves between Offline, changes waiting and Synced; not for "Syncing…" or a new time.
        var kind = app.SyncRunning || text.Length == 0 ? _statusKind : offline ? 1 : pending.Count > 0 ? 2 : 3;
        if (kind != _statusKind)
        {
            _statusKind = kind;
            Announce(SyncStatusText);
        }
        if (PendingPopup.IsOpen)
        {
            ShowPendingList(pending);
        }

        ShowBar(OfflineBar, offline ? SyncHealth.OfflineMessage(app.LastOk, culture) : null);
        ShowBar(SignInBar, SyncHealth.SignInMessage(health));
        // Offline says it all; the per-account errors show once a server can be reached.
        ShowBar(SyncErrorBar, offline ? null : SyncHealth.ErrorMessage(health, app.LastOk, culture));

        // R1a: until an account's first sync completes, empty days and lists say it is syncing; once its first run has
        // failed, the bar says why instead.
        var tried = health.Select(h => h.AccountId).ToHashSet();
        var first = SyncHealth.FirstSyncText([.. accounts.Where(a => !tried.Contains(a.Id) && !Store!.HasSyncedOnce(a.Id))
            .Select(a => a.DisplayName)]);
        if (first != _firstSyncText)
        {
            _firstSyncText = _agenda.EmptyText = _tasks.EmptyText = first;
            RenderView();
        }
    }

    /// <summary>Opens the bar with <paramref name="message"/>, or closes it for null; a bar that opens is announced.</summary>
    private static void ShowBar(Wpf.Ui.Controls.InfoBar bar, string? message)
    {
        var opening = message is not null && !bar.IsOpen;
        if (message is not null)
        {
            bar.Message = message;
        }
        BannerFade.SetOpen(bar, message is not null);
        if (opening)
        {
            Announce(bar);
        }
    }

    /// <summary>Tells screen readers a live region changed (its AutomationProperties.LiveSetting says how urgently).</summary>
    private static void Announce(UIElement element) =>
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    // ---- Sign in (ui-states R2, sync-ms365 R11, sync-radicale R11) ----

    /// <summary>Signs in every account that needs it, in turn; a cancelled sign-in stops the round.</summary>
    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        if (Store is not { } store)
        {
            return;
        }
        SignInButton.IsEnabled = false;
        try
        {
            var accounts = store.GetAccounts();
            foreach (var waiting in ((App)Application.Current).Health.Where(h => h.Outcome == SyncOutcome.SignInNeeded))
            {
                if (accounts.FirstOrDefault(a => a.Id == waiting.AccountId) is { } account && !await SignInAgainAsync(account))
                {
                    break;
                }
            }
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    /// <summary>MS365: the interactive sign-in, which must be the same account. Radicale: the masked password prompt.</summary>
    private async Task<bool> SignInAgainAsync(Account account) =>
        account.Type == AccountType.MS365 ? await SignInMs365Async(account) : await AskPasswordAsync(account);

    private async Task<bool> SignInMs365Async(Account account)
    {
        if (App.Ms365 is not { } auth)
        {
            await ShowMessageAsync("Microsoft 365 is not set up",
                "Fill in the app registration's client ID and tenant ID in ms365.json, then rebuild the app.");
            return false;
        }
        string? token = null;
        try
        {
            (var username, token) = await auth.SignInAsync(new WindowInteropHelper(this).Handle, default);
            if (!string.Equals(username, account.Username, StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessageAsync("A different account",
                    $"That sign-in is for another account. Sign in as {account.Username} to resume {account.DisplayName}.");
                return false;
            }
            Log.Information("MS365 account {AccountId} signed in again", account.Id);
            ((App)Application.Current).ResumeAccount(account.Id);
            return true;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            return false;
        }
        catch (MsalException ex)
        {
            Log.Warning("MS365 sign-in failed: {Code}", ex.ErrorCode);
            await ShowMessageAsync("Sign-in failed", $"Microsoft 365 sign-in did not complete ({ex.ErrorCode}).");
            return false;
        }
        finally
        {
            token = null;
        }
    }

    /// <summary>The masked password prompt, checked with the server before it is saved (RadicaleSignInDialog).</summary>
    private async Task<bool> AskPasswordAsync(Account account)
    {
        var dialog = new RadicaleSignInDialog(DialogHost, account);
        await dialog.ShowAsync();
        if (dialog.SignedIn)
        {
            ((App)Application.Current).ResumeAccount(account.Id);
        }
        return dialog.SignedIn;
    }

    // ---- Pending changes (local-store-offline R9a) ----

    private void OnSyncStatusClicked(object sender, RoutedEventArgs e)
    {
        if (Store?.GetPendingChanges() is { Count: > 0 } pending)
        {
            ShowPendingList(pending);
            PendingPopup.IsOpen = true;
        }
    }

    /// <summary>One row per entry: title, operation and state; Retry and Discard on failed ones not being sent.</summary>
    private void ShowPendingList(IReadOnlyList<PendingChange> pending)
    {
        PendingList.Children.Clear();
        if (pending.Count == 0)
        {
            PendingPopup.IsOpen = false;
            return;
        }
        foreach (var change in pending)
        {
            var row = new DockPanel { Margin = new Thickness(4, 4, 4, 8) };
            if (change.Failed && !change.InFlight)
            {
                var buttons = new SpacedStack { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                var retry = new Wpf.Ui.Controls.Button { Content = "Retry" };
                retry.Click += (_, _) => Retry(change);
                var discard = new Wpf.Ui.Controls.Button { Content = "Discard" };
                discard.Click += async (_, _) => await DiscardAsync(change);
                AutomationProperties.SetName(retry, $"Retry {change.Title}");
                AutomationProperties.SetName(discard, $"Discard {change.Title}");
                buttons.Children.Add(retry);
                buttons.Children.Add(discard);
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
            }
            var text = new StackPanel();
            var title = new TextBlock { Text = change.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            text.Children.Add(title);
            var operation = change.Operation switch
            {
                OutboxOperation.Create => "New",
                OutboxOperation.Delete => "Delete",
                _ => "Edit",
            };
            text.Children.Add(new TextBlock
            {
                Text = change.Failed ? $"{operation} · Failed: {change.LastError}" : $"{operation} · Waiting",
                Style = Theme.Style("CaptionTextBlockStyle"),
                Foreground = Theme.Brush("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(text);
            PendingList.Children.Add(row);
        }
    }

    private void Retry(PendingChange change)
    {
        Store?.RetryOutboxEntry(change.EntryId);
        Log.Information("Outbox entry {EntryId} retried by the user", change.EntryId);
        ((App)Application.Current).SyncNow();
        ShowStatus();
    }

    /// <summary>Asks first (CLAUDE.md rule 5); the local version is kept in Conflicts, so it stays recoverable.</summary>
    private async Task DiscardAsync(PendingChange change)
    {
        PendingPopup.IsOpen = false;
        var dialog = Destructive.Apply(new ContentDialog(DialogHost)
        {
            Title = "Discard this change?",
            Content = change.Operation == OutboxOperation.Create
                ? $"{change.Title} is removed from the {(change.Kind == ItemKind.Task ? "task list" : "calendar")}; your version is kept in Conflicts."
                : $"Your version of {change.Title} is kept in Conflicts, and the server's version is fetched again.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || Store is not { } store)
        {
            return;
        }
        try
        {
            if (store.DiscardOutboxEntry(change.EntryId))
            {
                Log.Information("Outbox entry {EntryId} discarded; local version kept in Conflicts", change.EntryId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Discarding outbox entry {EntryId} failed", change.EntryId);
        }
        RefreshView();
        ShowStatus();
        ((App)Application.Current).RequestSync("change discarded");
    }
}
