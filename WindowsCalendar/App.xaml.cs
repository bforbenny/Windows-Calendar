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
using Serilog;
using Windows.ApplicationModel;
using Windows.Networking.Connectivity;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
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
    /// Interim: failures are only logged; the on-screen message comes with ui-states.
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
        _window = new MainWindow();
        _trayIcon = CreateTrayIcon();
        StartSyncScheduling();

        // A second launch signals here through Program.Main (app-platform R4).
        if (Program.Activated is { } activated)
        {
            ThreadPool.RegisterWaitForSingleObject(activated, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null,
                Timeout.Infinite, executeOnlyOnce: false);
        }

        ToastActivator.LogScheduledCount();
        ToastActivator.ScheduleTestToastsIfRequested();
        RunModelSpikeIfRequested();

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
            _online = online;
        };
    }

    private static bool IsOnline() =>
        NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;

    private async Task RunSync(string reason, IReadOnlySet<string>? only, CancellationToken cancellationToken)
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

        // All accounts sync at once (local-store-offline R10); each catches its own errors. Hidden collections are
        // not pulled; a run for a just-ticked collection syncs only its account and pulls only it.
        var hidden = store.GetHiddenCollectionIds();
        Func<Collection, bool> pull = c => !hidden.Contains(c.Id) && (only is null || only.Contains(c.Id));
        var accounts = store.GetAccounts()
            .Where(a => only is null || store.GetCollections(a.Id).Any(c => only.Contains(c.Id)));
        var auth = Ms365;
        var results = await Task.WhenAll(accounts.Select(account => account.Type switch
        {
            AccountType.CalDAV => SyncRadicaleAsync(store, account, reason, pull, cancellationToken),
            AccountType.MS365 when auth is not null => SyncMs365Async(store, account, auth, pull, cancellationToken),
            _ => Task.FromResult(false),
        }));
        if (auth is null && store.GetAccounts().Any(a => a.Type == AccountType.MS365))
        {
            Log.Warning("MS365 accounts not synced: ms365.json has no app registration ids");
        }
        changed |= results.Any(c => c);

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

    private void Pause(string accountId)
    {
        lock (_accountGate)
        {
            _pausedAccounts.Add(accountId);
        }
    }

    /// <summary>Syncs one Radicale account. Returns whether the views may have changed.</summary>
    private async Task<bool> SyncRadicaleAsync(LocalStore store, Account account, string reason, Func<Collection, bool> pull,
        CancellationToken cancellationToken)
    {
        if (IsPaused(account.Id))
        {
            return false;
        }

        // sync-radicale R10: the password comes from Credential Manager for this run only, and is never logged.
        var password = CredentialVault.RetrievePassword(account.Id);
        if (password is null)
        {
            Log.Warning("Account {AccountId} has no saved password; paused until restart", account.Id);
            Pause(account.Id);
            return false;
        }
        var radicale = new RadicaleSync(store, CalDavHttp);
        radicale.CollectionSyncing += OnCollectionSyncing;
        try
        {
            // Sync now also refetches subscribed feeds at once (sync-radicale R1a).
            var summary = await radicale.SyncAccountAsync(account, password, forceFeeds: reason == "sync now", pull, cancellationToken);
            Log.Information("Account {AccountId} synced: {@Summary}", account.Id, summary);
            return summary.AnyChange;
        }
        catch (CalDavException ex) when (ex.Error == CalDavError.WrongCredentials)
        {
            // sync-radicale R11 interim: no retry loop; the Sign in bar comes with ui-states.
            Log.Warning("Account {AccountId} rejected the saved password (401); paused until restart", account.Id);
            Pause(account.Id);
        }
        catch (CalDavException ex)
        {
            Log.Warning("Account {AccountId} sync failed: {Error} {Status}", account.Id, ex.Error, (int?)ex.Status);
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
    private async Task<bool> SyncMs365Async(LocalStore store, Account account, Ms365Auth auth, Func<Collection, bool> pull,
        CancellationToken cancellationToken)
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
        graph.ListsUnavailable += accountId =>
            Log.Warning("Account {AccountId}: To Do lists could not be read; task lists kept as they were", accountId);
        graph.CollectionSyncing += OnCollectionSyncing;
        string? token = null;
        try
        {
            token = await auth.GetTokenAsync(account.Username ?? "", cancellationToken);
            var summary = await graph.SyncAccountAsync(account, token, pull, cancellationToken);
            Log.Information("Account {AccountId} synced: {@Summary}", account.Id, summary);
            return summary.AnyChange;
        }
        catch (MsalUiRequiredException ex)
        {
            Log.Warning("Account {AccountId} needs to sign in again ({Code}); paused until restart", account.Id, ex.ErrorCode);
            Pause(account.Id);
        }
        catch (GraphException ex) when (ex.Error == GraphError.Unauthorized)
        {
            Log.Warning("Account {AccountId} was refused by Graph (401); paused until restart", account.Id);
            Pause(account.Id);
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
        }
        catch (MsalException ex)
        {
            Log.Warning("Account {AccountId} token request failed: {Code}", account.Id, ex.ErrorCode);
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
        }
        _scheduler?.Request("signed in again");
    }

    /// <summary>Asks for a sync now, e.g. after an account is added (local-store-offline R10).</summary>
    internal void RequestSync(string reason) => _scheduler?.Request(reason);

    /// <summary>A calendar or task list was ticked: sync just it, since hidden ones are not pulled (local-store-offline R10).</summary>
    internal void SyncCollection(string collectionId) => _scheduler?.RequestCollection("calendar shown", collectionId);

    /// <summary>Tray and toolbar Sync now: retry failed changes immediately (local-store-offline R7, R10).</summary>
    internal void SyncNow()
    {
        Store?.ResetBackoff();
        _scheduler?.Request("sync now");
    }

    /// <summary>
    /// Tray icon and menu (app-platform R5). Left-click opens the main window until the tray flyout exists.
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
            LeftClickCommand = new Command(ShowMainWindow),
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

    internal void ShowMainWindow() => _window?.ShowAndFocus();

    /// <summary>
    /// Spike (on-device-model R12): if LocalState\run-spike exists, deletes it and runs the model spike in the
    /// background; the report goes to LocalState\spike-report.md.
    /// </summary>
    private static void RunModelSpikeIfRequested()
    {
        var folder = global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        var flag = System.IO.Path.Combine(folder, "run-spike");
        if (!System.IO.File.Exists(flag))
        {
            return;
        }
        System.IO.File.Delete(flag);
        _ = Task.Run(() => Ai.ModelSpike.RunAsync(folder, CancellationToken.None));
    }

    private void Quit()
    {
        Log.Information("App quit");
        _scheduler?.Dispose();
        Store?.Dispose();
        Log.CloseAndFlush();
        _trayIcon?.Dispose();
        _window?.CloseForQuit();
        Shutdown();
    }
}
