using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        InitializeLogging();
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
    /// Tray icon and menu (app-platform R5). Left-click opens the main window until the tray flyout exists.
    /// </summary>
    private TaskbarIcon CreateTrayIcon()
    {
        var open = new XamlUICommand();
        open.ExecuteRequested += (_, _) => ShowMainWindow();
        var quit = new XamlUICommand();
        quit.ExecuteRequested += (_, _) => Quit();

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open", Command = open });
        menu.Items.Add(new MenuFlyoutItem { Text = "Sync now", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = quit });

        var icon = new TaskbarIcon
        {
            ToolTipText = "WindowsCalendar",
            // A .ico via Icon, not IconSource: H.NotifyIcon's BitmapImage conversion crashes on Windows App SDK 2.6.
            Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = open,
            NoLeftClickDelay = true,
        };
        icon.ForceCreate(false);
        return icon;
    }

    private void ShowMainWindow() => _window?.ShowAndFocus();

    private void Quit()
    {
        Log.Information("App quit");
        Log.CloseAndFlush();
        _trayIcon?.Dispose();
        _window?.CloseForQuit();
        Exit();
    }
}
