using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Views;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogHost = Wpf.Ui.Controls.ContentDialogHost;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace WindowsCalendar;

/// <summary>
/// Signs a Radicale account in again after its password was refused (ui-states R2, sync-radicale R11): a masked prompt
/// whose password is checked with the server before it is saved to Credential Manager. The plain text exists only for
/// the check and the save (CLAUDE.md security rules 3, 4). Stays open with the reason when the check fails.
/// </summary>
public sealed class RadicaleSignInDialog : ContentDialog
{
    private readonly Account _account;
    private readonly PasswordBox _password = new();
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private bool _busy;

    public RadicaleSignInDialog(ContentDialogHost host, Account account) : base(host)
    {
        _account = account;
        Title = "Sign in";
        PrimaryButtonText = "Sign in";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetName(_password, "Password");
        var form = new SpacedStack { Spacing = 12, Width = 380 };
        form.Children.Add(new TextBlock
        {
            Text = $"{account.DisplayName} did not accept the saved password. Enter the password for {account.Username}.",
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(ViewParts.Field("Password", _password));
        form.Children.Add(_error);
        Content = form;
        Loaded += (_, _) => _password.Focus();
    }

    /// <summary>Whether a checked password was saved.</summary>
    public bool SignedIn { get; private set; }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button != ContentDialogButton.Primary)
        {
            if (!_busy)
            {
                _password.Clear();
                base.OnButtonClick(button);
            }
            return;
        }
        if (!_busy)
        {
            Check();
        }
    }

    private async void Check()
    {
        _error.IsOpen = false;
        using var secure = _password.SecurePassword;
        if (secure.Length == 0 || !Uri.TryCreate(_account.ServerUrl, UriKind.Absolute, out var url))
        {
            ShowError("Enter your password.");
            return;
        }
        _password.Clear();
        string? password = null;
        SetBusy(true);
        try
        {
            password = AddRadicaleDialog.Plain(secure);
            // Discovery is the same check as when the account was added; its result is not used here.
            await new CalDavDiscovery(App.CalDavHttp).DiscoverAsync(url, _account.Username ?? "", password);
            CredentialVault.Save(_account.Id, _account.Username ?? "", password);
            Log.Information("Radicale account {AccountId} password entered again", _account.Id);
            SignedIn = true;
            Hide(Wpf.Ui.Controls.ContentDialogResult.Primary);
        }
        catch (CalDavException ex)
        {
            Log.Warning("Radicale sign-in check failed: {Error} {Status}", ex.Error, (int?)ex.Status);
            ShowError(AddRadicaleDialog.Message(ex.Error));
        }
        catch (Exception ex)
        {
            Log.Error("Saving the password for account {AccountId} failed: {Type}", _account.Id, ex.GetType().Name);
            ShowError("The password could not be saved. The log has the details.");
        }
        finally
        {
            password = null;
            SetBusy(false);
        }
    }

    private void ShowError(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IsPrimaryButtonEnabled = !busy;
    }
}
