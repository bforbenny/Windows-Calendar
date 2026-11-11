using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Core.Sync;

/// <summary>
/// The account was removed while it was syncing (account-management R8): the sync's writes failed because its rows are
/// gone. Nothing is wrong; the app logs it as information, not as a sync error.
/// </summary>
public sealed class AccountRemovedException(Exception inner) : Exception("Account removed during sync", inner)
{
    /// <summary>Whether <paramref name="ex"/> failed a sync only because the account is gone.</summary>
    internal static bool Caused(Exception ex, LocalStore store, string accountId) =>
        ex is not OperationCanceledException && store.GetAccounts().All(a => a.Id != accountId);
}
