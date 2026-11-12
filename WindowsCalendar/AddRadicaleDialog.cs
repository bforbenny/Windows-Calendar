using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
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
/// Adds a Radicale account (account-management R4, R4a, R6, R7): tests the connection, discovers the collections,
/// then saves the password to Credential Manager and the account to the local store.
/// </summary>
public sealed class AddRadicaleDialog : ContentDialog
{
    private readonly LocalStore _store;
    private readonly Wpf.Ui.Controls.TextBox _serverUrl = new() { PlaceholderText = "https://calendar.example.com/" };
    private readonly Wpf.Ui.Controls.TextBox _username = new();
    // WPF's PasswordBox: masked, and gives the password as a SecureString (WPF-UI's keeps it as plain text).
    private readonly PasswordBox _password = new();
    private readonly Wpf.Ui.Controls.TextBox _email = new() { PlaceholderText = "Used to tell meetings you organise" };
    private readonly Wpf.Ui.Controls.TextBox _displayName = new() { PlaceholderText = "Defaults to the server name" };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly Wpf.Ui.Controls.ProgressRing _progress = new()
    {
        IsIndeterminate = true,
        Width = 24,
        Height = 24,
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed,
    };
    private bool _busy;

    public AddRadicaleDialog(ContentDialogHost host, LocalStore store) : base(host)
    {
        _store = store;
        Title = "Add Radicale account";
        PrimaryButtonText = "Connect";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // account-management R4, R4a, R7
        var form = new SpacedStack { Spacing = 12, Width = 380 };
        form.Children.Add(ViewParts.Field("Server address", _serverUrl));
        form.Children.Add(ViewParts.Field("Username", _username));
        form.Children.Add(ViewParts.Field("Password", _password));
        form.Children.Add(ViewParts.Field("Email address (optional)", _email));
        form.Children.Add(ViewParts.Field("Account name (optional)", _displayName));
        form.Children.Add(_error);
        form.Children.Add(_progress);
        Content = form;
    }

    /// <summary>The saved account, or null if the dialog was cancelled.</summary>
    public Account? AddedAccount { get; private set; }

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
        // Stay open; the dialog closes once the account is saved.
        if (!_busy)
        {
            Connect();
        }
    }

    private async void Connect()
    {
        _error.IsOpen = false;

        if (!Uri.TryCreate(_serverUrl.Text.Trim(), UriKind.Absolute, out var url))
        {
            ShowError("Enter the full server address, starting with https://.");
            return;
        }
        var username = _username.Text.Trim();
        // R7: take the masked password once and clear the box at once; plain text only for the login and the save.
        using var secure = _password.SecurePassword;
        if (username.Length == 0 || secure.Length == 0)
        {
            ShowError("Enter your username and password.");
            return;
        }
        _password.Clear();
        string? password = null;
        SetBusy(true);
        try
        {
            password = Plain(secure);
            var found = await new CalDavDiscovery(App.CalDavHttp).DiscoverAsync(url, username, password);

            var name = _displayName.Text.Trim();
            var email = _email.Text.Trim();
            var account = new Account(Guid.NewGuid().ToString("N"), AccountType.CalDAV,
                name.Length > 0 ? name : url.Host, url.AbsoluteUri, username, email.Length > 0 ? email : null);

            CredentialVault.Save(account.Id, username, password);
            password = null;
            try
            {
                _store.AddDiscoveredAccount(account, found);
            }
            catch
            {
                CredentialVault.Remove(account.Id);
                throw;
            }

            Log.Information("Radicale account {AccountId} added with {Count} collections", account.Id, found.Count);
            AddedAccount = account;
            Hide(Wpf.Ui.Controls.ContentDialogResult.Primary);
        }
        catch (CalDavException ex)
        {
            Log.Warning("Radicale connection test failed: {Error} {Status}", ex.Error, (int?)ex.Status);
            ShowError(Message(ex.Error));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the Radicale account failed");
            ShowError("The account could not be saved. The log has the details.");
        }
        finally
        {
            password = null;
            SetBusy(false);
        }
    }

    /// <summary>The SecureString as a string, zeroing the unmanaged copy straight away.</summary>
    internal static string Plain(SecureString secure)
    {
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? "";
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }

    /// <summary>One message per failure (account-management R4a).</summary>
    internal static string Message(CalDavError error) => error switch
    {
        CalDavError.NotHttps => "The server address must start with https://.",
        CalDavError.Unreachable => "The server could not be reached. Check the address and your connection.",
        CalDavError.CertificateNotTrusted => "The server's certificate is not trusted. Install the server's certificate in Windows, then retry.",
        CalDavError.WrongCredentials => "Wrong username or password.",
        CalDavError.NotCalDav => "No CalDAV service was found at this address.",
        CalDavError.NoCalendars => "Connected, but no calendars or task lists were found.",
        _ => "The connection test failed.",
    };

    private void ShowError(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IsPrimaryButtonEnabled = !busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
