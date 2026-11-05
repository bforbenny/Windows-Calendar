using System.Runtime.InteropServices;
using Windows.Security.Credentials;

namespace WindowsCalendar;

/// <summary>
/// Radicale passwords in Windows Credential Manager, tied to the Windows login (account-management R6).
/// One entry per account: resource "WindowsCalendar/&lt;accountId&gt;", user name = the server username.
/// Passwords are never logged or kept by this class.
/// </summary>
internal static class CredentialVault
{
    private static string Resource(string accountId) => $"WindowsCalendar/{accountId}";

    public static void Save(string accountId, string username, string password) =>
        new PasswordVault().Add(new PasswordCredential(Resource(accountId), username, password));

    /// <summary>Removes the account's entry; does nothing if there is none.</summary>
    public static void Remove(string accountId)
    {
        var vault = new PasswordVault();
        try
        {
            foreach (var credential in vault.FindAllByResource(Resource(accountId)))
            {
                vault.Remove(credential);
            }
        }
        catch (COMException)
        {
            // FindAllByResource throws when no entry exists.
        }
    }
}
