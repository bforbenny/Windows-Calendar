using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.Identity.Client;
using Microsoft.Win32;
using Serilog;
using Windows.ApplicationModel;
using Windows.Networking.Connectivity;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Reminders;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;

namespace WindowsCalendar;
/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private TaskbarIcon? _trayIcon;
    private TrayFlyout? _flyout;
    private bool _storeTooNew;

    /// <summary>Whether a sync run is going, and when the last one ended (tray-flyout R3 footer; in memory only).</summary>
    internal bool SyncRunning { get; private set; }
    internal DateTime? LastSynced { get; private set; }
    internal bool Online => _online;

    /// <summary>Raised on the UI thread when a sync run starts or ends, or the network comes or goes; true when a run ended.</summary>
    internal event Action<bool>? SyncStateChanged;

    /// <summary>The main window, which always exists while the app runs (only hidden); the flyout acts through it.</summary>
    internal MainWindow? Calendar => _window;
    private SyncScheduler? _scheduler;
    private bool _online;

    /// <summary>Accounts skipped after a 401 or a missing password, until restart (sync-radicale R11 interim). Guarded by _accountGate: accounts sync in parallel.</summary>
    private readonly HashSet<string> _pausedAccounts = [];
    private readonly Lock _accountGate = new();

    /// <summary>
    /// Shared client for CalDAV. Redirects are followed by DavTransport itself, so the Authorization header stays
    /// on the same host; certificate validation stays on (account-management R5).
    /// </summary>
    internal static readonly HttpClient CalDavHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Shared client for Microsoft Graph (sync-ms365).</summary>
    internal static readonly HttpClient GraphHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>MS365 sign-in; null until ms365.json holds the app registration's ids (account-management R3).</summary>
    internal static Ms365Auth? Ms365 { get; } = Ms365Auth.Load();

    /// <summary>MS365 accounts skipped until this time after a 429/503 (sync-ms365 R10). Guarded by _accountGate.</summary>
    private readonly Dictionary<string, DateTimeOffset> _backoffUntil = [];

    /// <summary>Each account's latest sync outcome, for the status and bars (ui-states R2). Guarded by _accountGate.</summary>
    private readonly Dictionary<string, AccountHealth> _health = [];
    private DateTime? _lastOk;

    /// <summary>Accounts whose sign-in toast was shown this session (ui-states R2); UI thread only.</summary>
    private readonly HashSet<string> _signInToasted = [];

    /// <summary>When a server last answered a sync (ui-states R1, R2 "Last synced"); set at the end of a run.</summary>
    internal DateTime? LastOk { get; private set; }

    /// <summary>The latest outcome of every account still in the store.</summary>
    internal IReadOnlyList<AccountHealth> Health
    {
        get
        {
            var ids = Store?.GetAccounts().Select(a => a.Id).ToHashSet() ?? [];
            lock (_accountGate)
            {
                return [.. _health.Values.Where(h => ids.Contains(h.AccountId))];
            }
        }
    }

    /// <summary>No network at all, or no server answered the last attempts (ui-states R2).</summary>
    internal bool Offline => SyncHealth.IsOffline(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(), Health);

    /// <summary>Kept alive for ColorValuesChanged (tray icon theme swap).</summary>
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    /// <summary>The local store; null if it could not be opened (local-store-offline R1).</summary>
    internal LocalStore? Store { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeLogging();
        OpenStore();
    }

    /// <summary>
    /// Opens LocalState\store.db and runs migrations (local-store-offline R1).
    /// A store newer than this build is not opened; OnStartup shows the app-updates R4 message and exits.
    /// </summary>
    private void OpenStore()
    {
        var path = System.IO.Path.Combine(global::Windows.Storage.ApplicationData.Current.LocalFolder.Path, "store.db");
        try
        {
            Store = LocalStore.Open(path);
            Log.Information("Local store opened");
        }
        catch (StoreTooNewException ex)
        {
            Log.Error("Local store not opened: schema {StoreVersion} is newer than supported {SupportedVersion}", ex.StoreVersion, ex.SupportedVersion);
            _storeTooNew = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Local store could not be opened");
        }
    }

    /// <summary>
    /// Rolling daily log under LocalState\Logs, last 14 files kept (app-platform R8).
    /// Never log credentials, tokens or Authorization headers.
    /// </summary>
    private void InitializeLogging()
    {
        var logPath = System.IO.Path.Combine(global::Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Logs", "app-.log");
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += (_, e) => Log.Fatal(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            Log.CloseAndFlush();
        };

        var v = Package.Current.Id.Version;
        Log.Information("App started, version {Version}", $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A store newer than this build is left untouched (app-updates R4).
        if (_storeTooNew)
        {
            MessageBox.Show("This data was created by a newer version. Install the latest version.", "WindowsCalendar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Information("App quit: store is newer than this build");
            Shutdown();
            return;
        }
        Views.TextScale.Start();
        _window = new MainWindow();
        // Signing out or shutting down skips Quit; the window place is saved here too (display-scaling R3).
        SessionEnding += (_, _) => _window?.SavePlacement();
        _trayIcon = CreateTrayIcon();
        StartSyncScheduling();

        // A second launch signals here through Program.Main (app-platform R4).
        if (Program.Activated is { } activated)
        {
            ThreadPool.RegisterWaitForSingleObject(activated, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null,
                Timeout.Infinite, executeOnlyOnce: false);
        }

        StartReminders();

        // Started by a toast action: stay hidden in the tray; the activator handles the action (reminders-notifications R2).
        if (Program.ToastLaunch)
        {
            Log.Information("Started by toast action; staying in tray");
            return;
        }

        // Launched by the Start with Windows task: stay hidden in the tray (app-platform R6).
        if (global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask)
        {
            Log.Information("Started by startup task; staying in tray");
            return;
        }

        _window.Show();
    }

    /// <summary>
    /// Sync triggers (local-store-offline R6, R10): every 5 minutes, local changes, window focus, network back
    /// and Sync now. Interim: a run only refreshes the occurrence index and logs, until the sync engines exist.
    /// </summary>
    private void StartSyncScheduling()
    {
        _scheduler = new SyncScheduler(RunSync, ex => Log.Error(ex, "Sync run failed"));
        CalDavEvents.ZoneNotKept += tzId => Log.Warning("Event timezone {TzId} not resolvable; times written in UTC", tzId);

        if (Store is not null)
        {
            Store.LocalChanged += () => _scheduler.Request("local change");
        }

        _window!.Activated += (_, _) => _scheduler.Request("focus");

        _online = IsOnline();
        NetworkInformation.NetworkStatusChanged += _ =>
        {
            var online = IsOnline();
            if (online && !_online)
            {
                _scheduler.Request("network back");
            }
            else if (!online && _online)
            {
                // The run's results tell whether the servers still answer, so Offline shows within seconds (ui-states R2).
                _scheduler.Request("network lost");
            }
            if (online != _online)
            {
                Dispatcher.BeginInvoke(() => SyncStateChanged?.Invoke(false));
            }
            _online = online;
        };
    }

    /// <summary>
    /// Reminders (reminders-notifications R2, R4, R6): prunes old state, schedules and shows the missed-reminder
    /// summary on start. The shared system-change hook asks for a sync run when the Windows timezone or clock changes
    /// or the PC resumes; the run rebuilds the occurrence index, reschedules reminders and refreshes the views
    /// (timezone-handling R6), and after a resume shows the missed-reminder summary. A timezone or clock change is
    /// also applied locally at once (<see cref="OnTimeChanged"/>).
    /// </summary>
    private void StartReminders()
    {
        if (Store is not { } store)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            store.PruneReminderState(DateTimeOffset.UtcNow.AddDays(-7));
            ReminderToasts.ExpectMissed();
            ReminderToasts.Reschedule(store, ReminderPlanner.RunningWindow);
            ReminderToasts.ShowMissed(store);
        });
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// A Windows timezone or clock change is applied locally at once, not behind a running sync (timezone-handling R6,
    /// F6): the index is rebuilt and reminders rescheduled off the UI thread (the store and reminders lock their own
    /// state), then the views, the open flyout and the open editor's times are refreshed. The sync run still follows.
    /// </summary>
    private void OnTimeChanged(object? sender, EventArgs e)
    {
        if (Store is { } store)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    TimeZoneInfo.ClearCachedData();
                    if (store.RefreshOccurrences())
                    {
                        Log.Information("Occurrence index rebuilt for {Zone}", TimeZoneInfo.Local.Id);
                    }
                    ReminderToasts.Reschedule(store, ReminderPlanner.RunningWindow);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        _window?.RefreshAfterTimeChange();
                        _flyout?.RefreshIfVisible();
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Time change not applied");
                }
            });
        }
        _scheduler?.Request("time changed");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && Store is { } store)
        {
            ReminderToasts.Suspending(store);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            ReminderToasts.ExpectMissed();
            _scheduler?.Request("resume");
        }
    }

    /// <summary>The missed-reminder summary's click (reminders-notifications R3, R6): the Agenda view on today.</summary>
    internal void OpenAgendaToday()
    {
        ShowMainWindow();
        _window?.OpenAgendaToday();
    }

    /// <summary>
    /// A reminder toast's Open or body click (reminders-notifications R3) and a tray flyout item (tray-flyout R4): the
    /// main window on that item's day, with its editor open.
    /// </summary>
    internal void OpenItem(string itemId, DateTimeOffset? start, DateTimeOffset? recurrence)
    {
        _flyout?.HideFlyout();
        ShowMainWindow();
        _window?.OpenFromReminder(itemId, start, recurrence);
    }

    private static bool IsOnline() =>
        NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;

    /// <summary>One sync run, with its start and end shown in the tray flyout's footer (tray-flyout R3, R4a).</summary>
    private async Task RunSync(string reason, CancellationToken cancellationToken)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            SyncRunning = true;
            SyncStateChanged?.Invoke(false);
        });
        try
        {
            await RunSyncCore(reason, cancellationToken);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                SyncRunning = false;
                LastSynced = DateTime.Now;
                lock (_accountGate)
                {
                    LastOk = _lastOk;
                }
                SyncStateChanged?.Invoke(true);
            });
        }
    }

    private async Task RunSyncCore(string reason, CancellationToken cancellationToken)
    {
        Log.Information("Sync requested ({Reason})", reason);
        if (Store is not { } store)
        {
            return;
        }

        // Pick up a Windows timezone change, and move the window daily (local-store-offline R3, timezone-handling R6).
        TimeZoneInfo.ClearCachedData();
        var changed = store.RefreshOccurrences();
        if (changed)
        {
            Log.Information("Occurrence index rebuilt for {Zone}", TimeZoneInfo.Local.Id);
        }

        // Every run tops up the reminder window, before the network work so local changes reschedule at once
        // (reminders-notifications R2); runs come at least every 5 minutes.
        ReminderToasts.Reschedule(store, ReminderPlanner.RunningWindow);

        // All accounts sync at once (local-store-offline R10); each catches its own errors. Every collection is pulled,
        // shown or hidden (calendar-sets R7).
        var accounts = store.GetAccounts();
        var auth = Ms365;
        var conflictsBefore = store.GetConflictCount();
        var results = await Task.WhenAll(accounts.Select(account => account.Type switch
        {
            AccountType.CalDAV => SyncRadicaleAsync(store, account, reason, cancellationToken),
            AccountType.MS365 when auth is not null => SyncMs365Async(store, account, auth, cancellationToken),
            _ => Task.FromResult(false),
        }));
        if (auth is null && store.GetAccounts().Any(a => a.Type == AccountType.MS365))
        {
            Log.Warning("MS365 accounts not synced: ms365.json has no app registration ids");
        }
        if (results.Any(c => c))
        {
            // Server changes may move, add or remove reminders.
            ReminderToasts.Reschedule(store, ReminderPlanner.RunningWindow);
            changed = true;
        }

        // One toast per run when conflicts were kept during it (conflict-resolution R5).
        if (store.GetConflictCount() - conflictsBefore is > 0 and var added)
        {
            ReminderToasts.ShowNewConflicts(added);
        }

        // After a resume (R6); by now Windows has shown any toasts it shows late on wake.
        ReminderToasts.ShowMissed(store);

        if (changed && _window is { } window)
        {
            _ = window.Dispatcher.BeginInvoke(window.RefreshView);
        }
    }

    /// <summary>Shows a collection's pull in the sidebar (ui-states R1); raised by the engines off the UI thread.</summary>
    private void OnCollectionSyncing(string collectionId, bool active)
    {
        if (_window is { } window)
        {
            _ = window.Dispatcher.BeginInvoke(() => window.SetCollectionSyncing(collectionId, active));
        }
    }

    private bool IsPaused(string accountId)
    {
        lock (_accountGate)
        {
            return _pausedAccounts.Contains(accountId);
        }
    }

    private void Pause(Account account)
    {
        lock (_accountGate)
        {
            _pausedAccounts.Add(account.Id);
        }
        Record(account, SyncOutcome.SignInNeeded);
    }

    /// <summary>
    /// Records an account's sync outcome (ui-states R2). A successful run marks it synced once (R1a). The first time an
    /// account needs sign-in while the window is hidden, one toast says so (sync-ms365 R11, sync-radicale R11).
    /// </summary>
    private void Record(Account account, SyncOutcome outcome, string? reason = null)
    {
        lock (_accountGate)
        {
            _health[account.Id] = new AccountHealth(account.Id, account.DisplayName, outcome, reason);
            if (outcome == SyncOutcome.Ok)
            {
                _lastOk = DateTime.Now;
            }
        }
        if (outcome == SyncOutcome.Ok)
        {
            Store?.MarkSyncedOnce(account.Id);
        }
        else if (outcome == SyncOutcome.SignInNeeded)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_window?.IsVisible != true && _signInToasted.Add(account.Id))
                {
                    ReminderToasts.ShowSignIn(account.DisplayName);
                }
            });
        }
    }

    private const string UnexpectedReason = "unexpected error, see the log";

    /// <summary>The short reason in the Sync error bar: the error kind or HTTP status, never server text (ui-states R2).</summary>
    private static string Reason(CalDavException ex) => ex.Status is { } status ? $"HTTP {(int)status}" : ex.Error switch
    {
        CalDavError.CertificateNotTrusted => "the server certificate is not trusted",
        CalDavError.NotHttps => "the server does not use HTTPS",
        CalDavError.NotCalDav => "the server did not answer as a CalDAV server",
        CalDavError.NoCalendars => "no calendars were found",
        _ => ex.Error.ToString(),
    };

    /// <summary>Syncs one Radicale account. Returns whether the views may have changed.</summary>
    private async Task<bool> SyncRadicaleAsync(LocalStore store, Account account, string reason, CancellationToken cancellationToken)
    {
        if (IsPaused(account.Id))
        {
            return false;
        }

        // sync-radicale R10: the password comes from Credential Manager for this run only, and is never logged.
        var password = CredentialVault.RetrievePassword(account.Id);
        if (password is null)
        {
            Log.Warning("Account {AccountId} has no saved password; paused until signed in", account.Id);
            Pause(account);
            return false;
        }
        var radicale = new RadicaleSync(store, CalDavHttp);
        radicale.CollectionSyncing += OnCollectionSyncing;
        try
        {
            // Sync now also refetches subscribed feeds at once (sync-radicale R1a).
            var summary = await radicale.SyncAccountAsync(account, password, forceFeeds: reason == "sync now", cancellationToken: cancellationToken);
            Log.Information("Account {AccountId} synced: {@Summary}", account.Id, summary);
            Record(account, SyncOutcome.Ok);
            return summary.AnyChange;
        }
        catch (AccountRemovedException)
        {
            Log.Information("Account {AccountId} removed during sync", account.Id);
        }
        catch (CalDavException ex) when (ex.Error == CalDavError.WrongCredentials)
        {
            // sync-radicale R11: no retry loop; the Sign in bar asks for the password (ui-states R2).
            Log.Warning("Account {AccountId} rejected the saved password (401); paused until signed in", account.Id);
            Pause(account);
        }
        catch (CalDavException ex)
        {
            Log.Warning("Account {AccountId} sync failed: {Error} {Status}", account.Id, ex.Error, (int?)ex.Status);
            Record(account, ex.Error == CalDavError.Unreachable ? SyncOutcome.Network : SyncOutcome.Failed, Reason(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No failure is silent (ui-states R2): the bar points to the log.
            Log.Error(ex, "Account {AccountId} sync failed unexpectedly", account.Id);
            Record(account, SyncOutcome.Failed, UnexpectedReason);
        }
        finally
        {
            password = null;
        }
        return false;
    }

    /// <summary>
    /// Syncs one MS365 account: queued changes out, then calendars in (sync-ms365). The access token is fetched silently for this run only and never
    /// logged. Needing sign-in pauses the account until restart (R11 interim); throttling backs it off (R10).
    /// </summary>
    private async Task<bool> SyncMs365Async(LocalStore store, Account account, Ms365Auth auth, CancellationToken cancellationToken)
    {
        lock (_accountGate)
        {
            if (_pausedAccounts.Contains(account.Id)
                || (_backoffUntil.TryGetValue(account.Id, out var until) && DateTimeOffset.UtcNow < until))
            {
                return false;
            }
        }
        var graph = new GraphSync(store, GraphHttp);
        graph.ChunkRestarted += (collectionId, chunk, why) =>
            Log.Warning("Calendar {CollectionId} chunk {Chunk} restarted: {Reason}", collectionId, chunk, why);
        graph.MastersFetched += (collectionId, count) =>
            Log.Information("Calendar {CollectionId}: {Count} series masters fetched by id", collectionId, count);
        graph.PullFailed += (collectionId, status) =>
            Log.Warning("Calendar {CollectionId} could not be read ({Status}); its events are kept and it is retried next sync", collectionId, status);
        graph.ListsUnavailable += accountId =>
            Log.Warning("Account {AccountId}: To Do lists could not be read; task lists kept as they were", accountId);
        graph.CollectionSyncing += OnCollectionSyncing;
        string? token = null;
        try
        {
            token = await auth.GetTokenAsync(account.Username ?? "", cancellationToken);
            var summary = await graph.SyncAccountAsync(account, token, cancellationToken: cancellationToken);
            Log.Information("Account {AccountId} synced: {@Summary}", account.Id, summary);
            Record(account, SyncOutcome.Ok);
            return summary.AnyChange;
        }
        catch (AccountRemovedException)
        {
            Log.Information("Account {AccountId} removed during sync", account.Id);
        }
        catch (MsalUiRequiredException ex)
        {
            Log.Warning("Account {AccountId} needs to sign in again ({Code}); paused until signed in", account.Id, ex.ErrorCode);
            Pause(account);
        }
        catch (GraphException ex) when (ex.Error == GraphError.Unauthorized)
        {
            Log.Warning("Account {AccountId} was refused by Graph (401); paused until signed in", account.Id);
            Pause(account);
        }
        catch (GraphException ex) when (ex.Error == GraphError.Throttled)
        {
            var wait = ex.RetryAfter ?? GraphClient.DefaultRetryAfter;
            lock (_accountGate)
            {
                _backoffUntil[account.Id] = DateTimeOffset.UtcNow + wait;
            }
            Log.Warning("Account {AccountId} throttled ({Status}); retrying after {Seconds} s", account.Id, (int?)ex.Status, (int)wait.TotalSeconds);
        }
        catch (GraphException ex)
        {
            Log.Warning("Account {AccountId} sync failed: {Error} {Status}", account.Id, ex.Error, (int?)ex.Status);
            Record(account, ex.Error == GraphError.Network ? SyncOutcome.Network : SyncOutcome.Failed,
                ex.Status is { } status ? $"HTTP {(int)status}" : ex.Error.ToString());
        }
        catch (MsalException ex)
        {
            Log.Warning("Account {AccountId} token request failed: {Code}", account.Id, ex.ErrorCode);
            // With no internet a token refresh fails before Graph is asked: that is Offline, not a sync error.
            Record(account, IsOnline() ? SyncOutcome.Failed : SyncOutcome.Network, "the sign-in token could not be obtained");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Account {AccountId} sync failed unexpectedly", account.Id);
            Record(account, SyncOutcome.Failed, UnexpectedReason);
        }
        finally
        {
            token = null;
        }
        return false;
    }

    /// <summary>After the user signed an account in again: it syncs again (sync-ms365 R11 interim).</summary>
    internal void ResumeAccount(string accountId)
    {
        lock (_accountGate)
        {
            _pausedAccounts.Remove(accountId);
            // The Sign in bar clears at once; the sync records the new outcome.
            _health.Remove(accountId);
        }
        _signInToasted.Remove(accountId);
        SyncStateChanged?.Invoke(false);
        _scheduler?.Request("signed in again");
    }

    /// <summary>Asks for a sync now, e.g. after an account is added (local-store-offline R10).</summary>
    internal void RequestSync(string reason) => _scheduler?.Request(reason);

    /// <summary>Tray and toolbar Sync now: retry failed changes immediately (local-store-offline R7, R10).</summary>
    internal void SyncNow()
    {
        Store?.ResetBackoff();
        _scheduler?.Request("sync now");
    }

    /// <summary>
    /// Tray icon and menu (app-platform R5). Left-click toggles the tray flyout at once; a double-click closes it and
    /// opens the main window (tray-flyout R1).
    /// </summary>
    private TaskbarIcon CreateTrayIcon()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open", ShowMainWindow));
        menu.Items.Add(MenuItem("Sync now", SyncNow));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Quit", Quit));

        var icon = new TaskbarIcon
        {
            ToolTipText = "WindowsCalendar",
            Icon = LoadTrayIcon(),
            ContextMenu = menu,
            LeftClickCommand = new Command(ToggleFlyout),
            DoubleClickCommand = new Command(() =>
            {
                _flyout?.HideFlyout();
                ShowMainWindow();
            }),
            NoLeftClickDelay = true,
        };
        icon.ForceCreate(false);

        // Swap the tray icon live when the Windows mode changes (app-platform R10).
        _uiSettings.ColorValuesChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_trayIcon is null) return;
            var old = _trayIcon.Icon;
            _trayIcon.Icon = LoadTrayIcon();
            old?.Dispose();
        });
        return icon;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>An ICommand for the tray's left click.</summary>
    private sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }

    /// <summary>
    /// Dark glyph on a light taskbar, white glyph on a dark one (app-platform R10).
    /// The taskbar follows the Windows mode (SystemUsesLightTheme), not the app mode.
    /// </summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var lightTaskbar = key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        var file = lightTaskbar ? "TrayIconLight.ico" : "TrayIconDark.ico";
        return new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", file));
    }

    /// <summary>The flyout is created on first open and only hidden afterwards (tray-flyout R6).</summary>
    private void ToggleFlyout()
    {
        _flyout ??= new TrayFlyout { TrayIcon = _trayIcon?.TrayIcon };
        _flyout.Toggle();
    }

    internal void ShowMainWindow() => _window?.ShowAndFocus();

    private void Quit()
    {
        Log.Information("App quit");
        _window?.SavePlacement();
        _scheduler?.Dispose();
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (Store is { } store)
        {
            // Reminders keep firing for up to 48 hours after Quit (reminders-notifications R2).
            ReminderToasts.RescheduleForQuit(store);
        }
        Store?.Dispose();
        Log.CloseAndFlush();
        _trayIcon?.Dispose();
        _window?.CloseForQuit();
        Shutdown();
    }
}
