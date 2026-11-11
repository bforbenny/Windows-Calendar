using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web;
using System.Windows;
using Serilog;

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
/// com:Class Id in Package.appxmanifest. The actions themselves are handled by <see cref="ReminderToasts"/>.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid(ClsidText)]
internal sealed class ToastActivator : INotificationActivationCallback
{
    internal const string ClsidText = "4d4096fe-bc21-4d6d-8f25-e48f645b4763";
    private const uint ClsctxLocalServer = 4;
    private const uint RegclsMultipleUse = 1;

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
            var action = HttpUtility.ParseQueryString(invokedArgs ?? "")["action"];
            Log.Information("Toast activated: action {Action}, started by activation {Started}", action, startedByThis);

            var args = invokedArgs ?? "";
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ReminderToasts.Handle(args, snoozeMinutes);
                }
                catch (Exception ex)
                {
                    Log.Error("Toast action failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Toast activation failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
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
