namespace WindowsCalendar.Core.Store;

/// <summary>Where a calendar or task list comes from, shown muted after its name in the editors (calendar-views interim).</summary>
public static class CollectionSource
{
    /// <summary>The server host for CalDAV, the account email for MS365; the account name when those are missing.</summary>
    public static string For(Account account) => account.Type switch
    {
        AccountType.CalDAV => Uri.TryCreate(account.ServerUrl, UriKind.Absolute, out var url) ? url.Host : account.DisplayName,
        _ => account.Email ?? account.Username ?? account.DisplayName,
    };
}
