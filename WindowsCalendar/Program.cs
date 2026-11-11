using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace WindowsCalendar;

/// <summary>
/// Custom entry point: keeps the app single-instance (app-platform R4). The first instance owns a named mutex; a
/// second launch signals the first to show its window, then exits.
/// </summary>
public static class Program
{
    private const string InstanceName = @"Local\WindowsCalendar-9f8d7707-4829-4516-a409-911bae828654";
    private const int AnyProcess = -1;

    // Kept for the life of the process: releasing it would let a second instance start.
    private static Mutex? _instance;

    /// <summary>Set by a second launch; the running instance shows its window (see App.OnStartup).</summary>
    public static EventWaitHandle? Activated { get; private set; }

    /// <summary>True when this process was started by a toast action (reminders-notifications R2); it stays in the tray.</summary>
    public static bool ToastLaunch { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        ToastLaunch = args.Contains("-ToastActivated");
        _instance = new Mutex(initiallyOwned: true, InstanceName + "-instance", out var first);
        var activated = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-activate");
        if (!first)
        {
            // Let the running instance bring its window to the foreground, unless a toast action started this launch.
            if (!ToastLaunch)
            {
                AllowSetForegroundWindow(AnyProcess);
                activated.Set();
            }
            activated.Dispose();
            return 0;
        }

        Activated = activated;
        var app = new App();
        app.InitializeComponent();
        var cookie = ToastActivator.Register(ToastLaunch);
        try
        {
            return app.Run();
        }
        finally
        {
            ToastActivator.Revoke(cookie);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
