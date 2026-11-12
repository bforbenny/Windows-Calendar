using System.Globalization;

namespace WindowsCalendar.Core.Sync;

/// <summary>How an account's latest sync attempt ended (ui-states R2).</summary>
public enum SyncOutcome
{
    Ok,

    /// <summary>No server answer: no network, DNS, TLS or a timeout.</summary>
    Network,

    /// <summary>Paused until the user signs in again (401, or MS365 needing interaction).</summary>
    SignInNeeded,

    /// <summary>Any other failure; <see cref="AccountHealth.Reason"/> says what, without server text.</summary>
    Failed,
}

/// <summary>An account's latest sync outcome, with its name for the bars.</summary>
public sealed record AccountHealth(string AccountId, string Name, SyncOutcome Outcome, string? Reason = null);

/// <summary>
/// The toolbar sync status and the Offline, Sign-in needed and Sync error bars (ui-states R1, R2, R3), worked out from
/// each account's latest outcome. One bar per condition covers every account in it.
/// </summary>
public static class SyncHealth
{
    /// <summary>
    /// Offline: no network at all, or every account that tried to sync failed with a network error. An account waiting
    /// for sign-in did not try. A server answering on the LAN while Windows reports no internet is not offline.
    /// </summary>
    public static bool IsOffline(bool networkAvailable, IReadOnlyCollection<AccountHealth> accounts)
    {
        if (!networkAvailable)
        {
            return true;
        }
        var tried = accounts.Where(a => a.Outcome != SyncOutcome.SignInNeeded).ToList();
        return tried.Count > 0 && tried.All(a => a.Outcome == SyncOutcome.Network);
    }

    /// <summary>"Syncing…", then "Offline", then "n changes waiting", then "Synced 10:42"; empty before any sync.</summary>
    public static string StatusText(bool syncing, bool offline, int pending, DateTime? lastSynced, CultureInfo culture)
    {
        if (syncing)
        {
            return "Syncing…";
        }
        if (offline)
        {
            return "Offline";
        }
        if (pending > 0)
        {
            return pending == 1 ? "1 change waiting" : $"{pending} changes waiting";
        }
        return lastSynced is { } last ? $"Synced {last.ToString("t", culture)}" : "";
    }

    public static string OfflineMessage(DateTime? lastSynced, CultureInfo culture) =>
        "Offline. Changes are saved and will sync later." + LastSynced(lastSynced, culture);

    /// <summary>The Sign-in needed bar's text, or null when no account needs it.</summary>
    public static string? SignInMessage(IReadOnlyCollection<AccountHealth> accounts)
    {
        var waiting = accounts.Where(a => a.Outcome == SyncOutcome.SignInNeeded).ToList();
        return waiting.Count switch
        {
            0 => null,
            1 => $"Sign in to {waiting[0].Name} to resume syncing.",
            _ => $"Sign in to {waiting.Count} accounts to resume syncing.",
        };
    }

    /// <summary>The Sync error bar's text, or null when no account failed.</summary>
    public static string? ErrorMessage(IReadOnlyCollection<AccountHealth> accounts, DateTime? lastSynced, CultureInfo culture)
    {
        var failed = accounts.Where(a => a.Outcome == SyncOutcome.Failed).ToList();
        return failed.Count switch
        {
            0 => null,
            1 => $"{failed[0].Name} could not sync: {failed[0].Reason ?? "unknown error"}." + LastSynced(lastSynced, culture),
            _ => $"{failed.Count} accounts could not sync: {string.Join(", ", failed.Select(a => a.Name))}." + LastSynced(lastSynced, culture),
        };
    }

    /// <summary>"Syncing Work for the first time…" while accounts have not finished a first sync; null otherwise (R1a).</summary>
    public static string? FirstSyncText(IReadOnlyList<string> unsyncedNames) => unsyncedNames.Count switch
    {
        0 => null,
        1 => $"Syncing {unsyncedNames[0]} for the first time…",
        _ => $"Syncing {unsyncedNames.Count} accounts for the first time…",
    };

    private static string LastSynced(DateTime? lastSynced, CultureInfo culture) =>
        lastSynced is { } last ? $" Last synced {last.ToString("t", culture)}." : "";
}
