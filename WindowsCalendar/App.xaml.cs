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
        _trayIcon?.Dispose();
        _window?.CloseForQuit();
        Exit();
    }
}
