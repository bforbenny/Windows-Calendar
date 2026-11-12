using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;
using WindowsCalendar.Core.Views;

namespace WindowsCalendar;

/// <summary>
/// Window size, position and maximised state (display-scaling R3): saved with GetWindowPlacement when the window hides
/// to the tray, on Quit and at sign-out; restored with SetWindowPlacement before the first show, in physical pixels so
/// it is right across monitors with different scaling. A saved place on no connected display is dropped and the window
/// opens centred on the primary display. While running, a window left past its monitor's work area (a display removed)
/// is moved and shrunk to fit.
/// </summary>
public sealed partial class MainWindow
{
    private const string PlacementKey = "window-placement";
    // Corners within this many physical pixels of a display count as on it: the invisible resize border of a window
    // snapped to the screen edge sits just outside the display.
    private const int EdgeSlack = 16;

    private void SetupPlacement()
    {
        if (WindowFit.Parse(Store?.GetUiText(PlacementKey)) is { } saved)
        {
            var normal = ToRect32(saved.Normal);
            if (MonitorFromRect(ref normal, MonitorDefaultToNull) != IntPtr.Zero)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                SourceInitialized += (_, _) => Restore(saved.Maximised, normal);
            }
            else
            {
                Log.Information("Saved window place is on no connected display; opening centred");
            }
        }
        // Without a usable saved place the window opens centred on the primary display, shrunk to fit its work area.
        if (WindowStartupLocation == WindowStartupLocation.CenterScreen)
        {
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        }

        // A display added, removed or rescaled (docking, undocking) may leave the window past its work area.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(KeepOnScreen);
    }

    private void Restore(bool maximised, Rect32 normal)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>(), ShowCmd = SwHide, NormalPosition = normal };
        if (!SetWindowPlacement(new WindowInteropHelper(this).Handle, ref placement))
        {
            Log.Warning("Restoring the window place failed");
            return;
        }
        if (maximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Saves the window's place; does nothing before the window was first shown.</summary>
    public void SavePlacement()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (hwnd == IntPtr.Zero || Store is not { } store || !GetWindowPlacement(hwnd, ref placement))
        {
            return;
        }
        var r = placement.NormalPosition;
        // Minimised from maximised restores to maximised.
        var maximised = WindowState == WindowState.Maximized
            || WindowState == WindowState.Minimized && (placement.Flags & WpfRestoreToMaximized) != 0;
        try
        {
            store.SetUiText(PlacementKey, WindowFit.Format(maximised, new PixelRect(r.Left, r.Top, r.Right, r.Bottom)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the window place failed");
        }
    }

    /// <summary>
    /// If a corner of the window is on no display, moves it inside its nearest monitor's work area, shrunk to fit; if
    /// it is on no display at all, centres it on the primary one. Maximised windows are left to Windows.
    /// </summary>
    private void KeepOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Normal || !IsVisible || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
        {
            return;
        }
        var inner = new Rect32 { Left = r.Left + EdgeSlack, Top = r.Top + EdgeSlack, Right = r.Right - EdgeSlack, Bottom = r.Bottom - EdgeSlack };
        if (OnDisplay(inner.Left, inner.Top) && OnDisplay(inner.Right, inner.Top) && OnDisplay(inner.Left, inner.Bottom)
            && OnDisplay(inner.Right, inner.Bottom))
        {
            return;
        }
        var anywhere = MonitorFromRect(ref r, MonitorDefaultToNull) != IntPtr.Zero;
        var monitor = anywhere ? MonitorFromRect(ref r, MonitorDefaultToNearest) : MonitorFromPoint(default, MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }
        var w = info.Work;
        var fit = WindowFit.Fit(new PixelRect(r.Left, r.Top, r.Right, r.Bottom), new PixelRect(w.Left, w.Top, w.Right, w.Bottom), centre: !anywhere);
        SetWindowPos(hwnd, IntPtr.Zero, fit.Left, fit.Top, fit.Width, fit.Height, SwpNoZOrder | SwpNoActivate);
        Log.Information("Window was past the screen; fitted to the work area");
    }

    private static bool OnDisplay(int x, int y) => MonitorFromPoint(new Point32 { X = x, Y = y }, MonitorDefaultToNull) != IntPtr.Zero;

    private static Rect32 ToRect32(PixelRect r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

    private const int SwHide = 0;
    private const int WpfRestoreToMaximized = 0x0002;
    private const int MonitorDefaultToNull = 0;
    private const int MonitorDefaultToPrimary = 1;
    private const int MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point32 MinPosition;
        public Point32 MaxPosition;
        public Rect32 NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect32 rect, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
