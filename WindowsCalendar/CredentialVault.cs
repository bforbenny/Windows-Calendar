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

    /// <summary>
    /// The saved password, or null if there is none. The caller uses it for one sync and then drops it
    /// (CLAUDE.md security rule 3).
    /// </summary>
    public static string? RetrievePassword(string accountId)
    {
        try
        {
            var credential = new PasswordVault().FindAllByResource(Resource(accountId))[0];
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (COMException)
        {
            // FindAllByResource throws when no entry exists.
            return null;
        }
    }

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
