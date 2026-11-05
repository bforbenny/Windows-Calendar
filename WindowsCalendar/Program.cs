using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WindowsCalendar;

/// <summary>
/// Custom entry point: keeps the app single-instance (app-platform R4).
/// A second launch hands its activation to the running instance and exits.
/// </summary>
public static class Program
{
    private const string InstanceKey = "main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!keyInstance.IsCurrent)
        {
            // Let the running instance bring its window to the foreground.
            AllowSetForegroundWindow(keyInstance.ProcessId);
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => keyInstance.RedirectActivationToAsync(activationArgs).AsTask()).Wait();
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
