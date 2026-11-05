using System;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar;

/// <summary>
/// Adds a Radicale account (account-management R4, R4a, R6, R7): tests the connection, discovers the collections,
/// then saves the password to Credential Manager and the account to the local store.
/// </summary>
public sealed partial class AddRadicaleDialog : ContentDialog
{
    // Redirects are followed by CalDavDiscovery itself, so the Authorization header stays on the same host.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly LocalStore _store;

    public AddRadicaleDialog(LocalStore store)
    {
        InitializeComponent();
        _store = store;
    }

    /// <summary>The saved account, or null if the dialog was cancelled.</summary>
    public Account? AddedAccount { get; private set; }

    private async void OnConnect(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Stay open; Hide() is called once the account is saved.
        args.Cancel = true;
        ErrorBar.IsOpen = false;

        if (!Uri.TryCreate(ServerUrlBox.Text.Trim(), UriKind.Absolute, out var url))
        {
            ShowError("Enter the full server address, starting with https://.");
            return;
        }
        var username = UsernameBox.Text.Trim();
        if (username.Length == 0 || PasswordInput.Password.Length == 0)
        {
            ShowError("Enter your username and password.");
            return;
        }

        // R7: read the password once and clear the box at once. PasswordBox has no SecureString form.
        string? password = PasswordInput.Password;
        PasswordInput.Password = string.Empty;
        SetBusy(true);
        try
        {
            var found = await new CalDavDiscovery(Http).DiscoverAsync(url, username, password);

            var name = DisplayNameBox.Text.Trim();
            var email = EmailBox.Text.Trim();
            var account = new Account(Guid.NewGuid().ToString("N"), AccountType.CalDAV,
                name.Length > 0 ? name : url.Host, url.AbsoluteUri, username, email.Length > 0 ? email : null);

            CredentialVault.Save(account.Id, username, password);
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
            Hide();
        }
        catch (CalDavDiscoveryException ex)
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

    /// <summary>One message per failure (account-management R4a).</summary>
    private static string Message(DiscoveryError error) => error switch
    {
        DiscoveryError.NotHttps => "The server address must start with https://.",
        DiscoveryError.Unreachable => "The server could not be reached. Check the address and your connection.",
        DiscoveryError.CertificateNotTrusted => "The server's certificate is not trusted. Install the server's certificate in Windows, then retry.",
        DiscoveryError.WrongCredentials => "Wrong username or password.",
        DiscoveryError.NotCalDav => "No CalDAV service was found at this address.",
        DiscoveryError.NoCalendars => "Connected, but no calendars or task lists were found.",
        _ => "The connection test failed.",
    };

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        IsPrimaryButtonEnabled = !busy;
        Progress.IsActive = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
