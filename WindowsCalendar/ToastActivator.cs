using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Web;
using System.Windows;
using Serilog;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WindowsCalendar;

/// <summary>One name/value pair the toast sends back, e.g. the snooze selection box.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NOTIFICATION_USER_INPUT_DATA
{
    [MarshalAs(UnmanagedType.LPWStr)] public string Key;
    [MarshalAs(UnmanagedType.LPWStr)] public string Value;
}

/// <summary>COM callback Windows calls when a toast button or the toast body is activated.</summary>
[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INotificationActivationCallback
{
    void Activate(
        [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] NOTIFICATION_USER_INPUT_DATA[] data,
        [In, MarshalAs(UnmanagedType.U4)] uint dataCount);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    void CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
    void LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

/// <summary>
/// COM activator for toast actions (reminders-notifications R2). The CLSID matches ToastActivatorCLSID and the
/// com:Class Id in Package.appxmanifest. Interim prerequisite test: the real reminder scheduler will reuse this.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid(ClsidText)]
internal sealed class ToastActivator : INotificationActivationCallback
{
    internal const string ClsidText = "4d4096fe-bc21-4d6d-8f25-e48f645b4763";
    private const uint ClsctxLocalServer = 4;
    private const uint RegclsMultipleUse = 1;
    private const string Group = "test";

    private static bool _startedByActivation;

    /// <summary>Registers the class object; returns the cookie for Revoke, or 0 if registration failed.</summary>
    internal static uint Register(bool startedByActivation)
    {
        _startedByActivation = startedByActivation;
        try
        {
            var clsid = new Guid(ClsidText);
            var hr = CoRegisterClassObject(ref clsid, new Factory(), ClsctxLocalServer, RegclsMultipleUse, out var cookie);
            if (hr != 0)
            {
                Log.Error("Toast activator registration failed: HRESULT {Hresult:X8}", hr);
                return 0;
            }
            return cookie;
        }
        catch (Exception ex)
        {
            Log.Error("Toast activator registration failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return 0;
        }
    }

    internal static void Revoke(uint cookie)
    {
        if (cookie != 0)
        {
            CoRevokeClassObject(cookie);
        }
    }

    public void Activate(string appUserModelId, string invokedArgs, NOTIFICATION_USER_INPUT_DATA[] data, uint dataCount)
    {
        try
        {
            var args = HttpUtility.ParseQueryString(invokedArgs ?? "");
            var action = args["action"] ?? "open";
            var id = args["id"] ?? "";
            var url = args["url"];
            var snoozeMinutes = 5;
            if (data is not null)
            {
                foreach (var item in data.Take((int)dataCount))
                {
                    if (item.Key == "snoozeMinutes" && int.TryParse(item.Value, out var m) && m is 5 or 10 or 15 or 60)
                    {
                        snoozeMinutes = m;
                    }
                }
            }

            var startedByThis = _startedByActivation;
            _startedByActivation = false;
            Log.Information("Toast activated: action {Action}, id {Id}, started by activation {Started}", action, id, startedByThis);

            _ = Application.Current.Dispatcher.BeginInvoke(() => Handle(action, id, url, snoozeMinutes));
        }
        catch (Exception ex)
        {
            Log.Error("Toast activation failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    private static void Handle(string action, string id, string? url, int snoozeMinutes)
    {
        try
        {
            switch (action)
            {
                case "snooze":
                    Schedule(id, DateTimeOffset.Now.AddMinutes(snoozeMinutes));
                    break;
                case "join":
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    }
                    break;
                case "open":
                    (Application.Current as App)?.ShowMainWindow();
                    break;
                    // dismiss: nothing but the log line written in Activate.
            }
        }
        catch (Exception ex)
        {
            Log.Error("Toast action failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>Logs how many toasts are scheduled; called on every start.</summary>
    internal static void LogScheduledCount()
    {
        try
        {
            Log.Information("Scheduled toasts: {Count}", ToastNotificationManager.CreateToastNotifier().GetScheduledToastNotifications().Count);
        }
        catch (Exception ex)
        {
            Log.Error("Scheduled toast count failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>If LocalState\toast-test exists, deletes it and schedules five test toasts at +1, 3, 5, 7, 9 minutes.</summary>
    internal static void ScheduleTestToastsIfRequested()
    {
        try
        {
            var flag = System.IO.Path.Combine(global::Windows.Storage.ApplicationData.Current.LocalFolder.Path, "toast-test");
            if (!System.IO.File.Exists(flag))
            {
                return;
            }
            System.IO.File.Delete(flag);
            var now = DateTimeOffset.Now;
            for (var n = 1; n <= 5; n++)
            {
                Schedule($"test-{n}", now.AddMinutes(2 * n - 1));
            }
            Log.Information("Scheduled 5 test toasts");
        }
        catch (Exception ex)
        {
            Log.Error("Test toast scheduling failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    private static void Schedule(string id, DateTimeOffset due)
    {
        var number = id.StartsWith("test-", StringComparison.Ordinal) ? id[5..] : "?";
        var xml = $"""
            <toast launch="{SecurityElement.Escape($"action=open&id={id}")}" scenario="reminder">
              <visual><binding template="ToastGeneric">
                <text>Test reminder {SecurityElement.Escape(number)}</text>
                <text>{due:t}</text>
              </binding></visual>
              <actions>
                <input id="snoozeMinutes" type="selection" defaultInput="5">
                  <selection id="5" content="5 minutes" />
                  <selection id="10" content="10 minutes" />
                  <selection id="15" content="15 minutes" />
                  <selection id="60" content="1 hour" />
                </input>
                <action content="Snooze" activationType="background" arguments="{SecurityElement.Escape($"action=snooze&id={id}")}" />
                <action content="Dismiss" activationType="background" arguments="{SecurityElement.Escape($"action=dismiss&id={id}")}" />
                <action content="Join" activationType="foreground" arguments="{SecurityElement.Escape($"action=join&id={id}&url=https%3A%2F%2Fexample.com")}" />
              </actions>
            </toast>
            """;
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var toast = new ScheduledToastNotification(doc, due) { Tag = id, Group = Group };
        ToastNotificationManager.CreateToastNotifier().AddToSchedule(toast);
    }

    private sealed class Factory : IClassFactory
    {
        public void CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(unchecked((int)0x80040110)); // CLASS_E_NOAGGREGATION
            }
            var unknown = Marshal.GetIUnknownForObject(new ToastActivator());
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in riid, out ppvObject));
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public void LockServer(bool fLock)
        {
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);
}
