using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace WindowsCalendar;

/// <summary>
/// MS365 sign-in through MSAL with the Windows broker (WAM), delegated permissions only (account-management R2, R3,
/// R6). Tokens stay in WAM; access tokens are returned for one sync run or one add-account step and never logged.
/// The client and tenant ids come from ms365.json shipped with the app.
/// </summary>
internal sealed class Ms365Auth
{
    // offline_access is added by MSAL itself.
    // Calendars.Read.Shared: colleague calendars added by email (ms365-colleague-calendars R9; admin consent granted).
    private static readonly string[] Scopes = ["Calendars.ReadWrite", "Calendars.Read.Shared", "Tasks.ReadWrite", "User.Read"];

    private readonly IPublicClientApplication _app;

    private Ms365Auth(string clientId, string tenantId)
    {
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithDefaultRedirectUri()
            // MSAL's own cache is in memory only (no tokens are stored by the app); listing the accounts Windows knows
            // lets a sync after a restart find the signed-in account in WAM.
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { ListOperatingSystemAccounts = true })
            .Build();
    }

    /// <summary>The configured sign-in, or null when ms365.json is missing or still has the placeholder ids.</summary>
    public static Ms365Auth? Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ms365.json")));
            var clientId = doc.RootElement.GetProperty("clientId").GetString();
            var tenantId = doc.RootElement.GetProperty("tenantId").GetString();
            return Guid.TryParse(clientId, out var c) && c != Guid.Empty && Guid.TryParse(tenantId, out var t) && t != Guid.Empty
                ? new Ms365Auth(clientId!, tenantId!)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Interactive sign-in over <paramref name="window"/>: the sign-in name and an access token for this step.</summary>
    public async Task<(string Username, string AccessToken)> SignInAsync(IntPtr window, CancellationToken cancellationToken)
    {
        var result = await _app.AcquireTokenInteractive(Scopes)
            .WithParentActivityOrWindow(window)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);
        return (result.Account.Username, result.AccessToken);
    }

    /// <summary>
    /// An access token for one sync run, without prompting. Throws <see cref="MsalUiRequiredException"/> when the
    /// account needs to sign in again (sync-ms365 R11).
    /// </summary>
    public async Task<string> GetTokenAsync(string username, CancellationToken cancellationToken)
    {
        var account = (await _app.GetAccountsAsync()).FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
        // After a restart MSAL's in-memory list is empty; WAM still knows the account by its sign-in name.
        var request = account is not null ? _app.AcquireTokenSilent(Scopes, account) : _app.AcquireTokenSilent(Scopes, username);
        var result = await request.ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    /// <summary>Signs the account out of this app (account-management R8).</summary>
    public async Task SignOutAsync(string username)
    {
        foreach (var account in (await _app.GetAccountsAsync()).Where(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            await _app.RemoveAsync(account);
        }
    }
}
