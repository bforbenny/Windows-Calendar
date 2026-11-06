using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.AppLifecycle;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Networking.Connectivity;
using Microsoft.Identity.Client;
using WindowsCalendar.Core.CalDav;
using WindowsCalendar.Core.Graph;
using WindowsCalendar.Core.Store;
using WindowsCalendar.Core.Sync;
using AppInstance = Microsoft.Windows.AppLifecycle.AppInstance;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

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

    /// <summary>Accounts skipped after a 401 or a missing password, until restart (sync-radicale R11 interim). Sync runs one at a time.</summary>
    private readonly HashSet<string> _pausedAccounts = [];

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

    /// <summary>MS365 accounts skipped until this time after a 429/503 (sync-ms365 R10).</summary>
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
        InitializeComponent();
        InitializeLogging();
        OpenStore();
    }

    /// <summary>
    /// Opens LocalState\store.db and runs migrations (local-store-offline R1).
    /// Interim: failures are only logged; the on-screen message comes with ui-states.
    /// </summary>
    private void OpenStore()
    {
        var path = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "store.db");
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
        var logPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Logs", "app-.log");
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        UnhandledException += (_, e) => Log.Fatal(e.Exception, "Unhandled UI exception");
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
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _trayIcon = CreateTrayIcon();
        StartSyncScheduling();

        // A second launch is redirected here by Program.Main (app-platform R4).
        AppInstance.GetCurrent().Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(ShowMainWindow);

        // Launched by the Start with Windows task: stay hidden in the tray (app-platform R6).
        if (AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask)
        {
            Log.Information("Started by startup task; staying in tray");
            return;
        }

        _window.Activate();
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

        _window!.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _scheduler.Request("focus");
            }
        };

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

    private async Task RunSync(string reason, CancellationToken cancellationToken)
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

        var radicale = new RadicaleSync(store, CalDavHttp);
        foreach (var account in store.GetAccounts().Where(a => a.Type == AccountType.CalDAV))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pausedAccounts.Contains(account.Id))
            {
                continue;
            }

            // sync-radicale R10: the password comes from Credential Manager for this run only, and is never logged.
            var password = CredentialVault.RetrievePassword(account.Id);
            if (password is null)
            {
                Log.Warning("Account {AccountId} has no saved password; paused until restart", account.Id);
                _pausedAccounts.Add(account.Id);
                continue;
            }
            try
            {
                // Sync now also refetches subscribed feeds at once (sync-radicale R1a).
                var summary = await radicale.SyncAccountAsync(account, password, forceFeeds: reason == "sync now", cancellationToken);
                Log.Information("Account {AccountId} synced: {@Summary}", account.Id, summary);
                changed |= summary.AnyChange;
            }
            catch (CalDavException ex) when (ex.Error == CalDavError.WrongCredentials)
            {
                // sync-radicale R11 interim: no retry loop; the Sign in bar comes with ui-states.
                Log.Warning("Account {AccountId} rejected the saved password (401); paused until restart", account.Id);
                _pausedAccounts.Add(account.Id);
            }
            catch (CalDavException ex)
            {
                Log.Warning("Account {AccountId} sync failed: {Error} {Status}", account.Id, ex.Error, (int?)ex.Status);
            }
            finally
            {
                password = null;
            }
        }

        changed |= await SyncMs365Async(store, cancellationToken);

        if (changed && _window is { } window)
        {
            window.DispatcherQueue.TryEnqueue(window.RefreshView);
        }
    }

    /// <summary>
    /// Syncs each MS365 account: queued changes out, then calendars in (sync-ms365). The access token is fetched silently for this run only and never
    /// logged. Needing sign-in pauses the account until restart (R11 interim); throttling backs it off (R10).
    /// </summary>
    private async Task<bool> SyncMs365Async(LocalStore store, CancellationToken cancellationToken)
    {
        var accounts = store.GetAccounts().Where(a => a.Type == AccountType.MS365).ToList();
        if (accounts.Count == 0)
        {
            return false;
        }
        if (Ms365 is not { } auth)
        {
            Log.Warning("MS365 accounts not synced: ms365.json has no app registration ids");
            return false;
        }
        var graph = new GraphSync(store, GraphHttp);
        graph.ChunkRestarted += (collectionId, chunk, why) =>
            Log.Warning("Calendar {CollectionId} chunk {Chunk} restarted: {Reason}", collectionId, chunk, why);
        graph.MastersFetched += (collectionId, count) =>
            Log.Information("Calendar {CollectionId}: {Count} series masters fetched by id", collectionId, count);
        var changed = false;
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pausedAccounts.Contains(account.Id)
                || (_backoffUntil.TryGetValue(account.Id, out var until) && DateTimeOffset.UtcNow < until))
            {
                continue;
            }
            string? token = null;
            try
            {
                token = await auth.GetTokenAsync(account.Username ?? "", cancellationToken);
                var summary = await graph.SyncAccountAsync(account, token, cancellationToken);
                Log.Information("Account {AccountId} synced: {@Summary}", account.Id, summary);
                changed |= summary.AnyChange;
            }
            catch (MsalUiRequiredException ex)
            {
                Log.Warning("Account {AccountId} needs to sign in again ({Code}); paused until restart", account.Id, ex.ErrorCode);
                _pausedAccounts.Add(account.Id);
            }
            catch (GraphException ex) when (ex.Error == GraphError.Unauthorized)
            {
                Log.Warning("Account {AccountId} was refused by Graph (401); paused until restart", account.Id);
                _pausedAccounts.Add(account.Id);
            }
            catch (GraphException ex) when (ex.Error == GraphError.Throttled)
            {
                var wait = ex.RetryAfter ?? GraphClient.DefaultRetryAfter;
                _backoffUntil[account.Id] = DateTimeOffset.UtcNow + wait;
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
        }
        return changed;
    }

    /// <summary>After the user signed an account in again: it syncs again (sync-ms365 R11 interim).</summary>
    internal void ResumeAccount(string accountId)
    {
        _pausedAccounts.Remove(accountId);
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
    /// Tray icon and menu (app-platform R5). Left-click opens the main window until the tray flyout exists.
    /// </summary>
    private TaskbarIcon CreateTrayIcon()
    {
        var open = new XamlUICommand();
        open.ExecuteRequested += (_, _) => ShowMainWindow();
        var syncNow = new XamlUICommand();
        syncNow.ExecuteRequested += (_, _) => SyncNow();
        var quit = new XamlUICommand();
        quit.ExecuteRequested += (_, _) => Quit();

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open", Command = open });
        menu.Items.Add(new MenuFlyoutItem { Text = "Sync now", Command = syncNow });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = quit });

        var icon = new TaskbarIcon
        {
            ToolTipText = "WindowsCalendar",
            // A .ico via Icon, not IconSource: H.NotifyIcon's BitmapImage conversion crashes on Windows App SDK 2.6.
            Icon = LoadTrayIcon(),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = open,
            NoLeftClickDelay = true,
        };
        icon.ForceCreate(false);

        // Swap the tray icon live when the Windows mode changes (app-platform R10).
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _uiSettings.ColorValuesChanged += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_trayIcon is null) return;
            var old = _trayIcon.Icon;
            _trayIcon.Icon = LoadTrayIcon();
            old?.Dispose();
        });
        return icon;
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

    private void ShowMainWindow() => _window?.ShowAndFocus();

    private void Quit()
    {
        Log.Information("App quit");
        _scheduler?.Dispose();
        Store?.Dispose();
        Log.CloseAndFlush();
        _trayIcon?.Dispose();
        _window?.CloseForQuit();
        Exit();
    }
}
