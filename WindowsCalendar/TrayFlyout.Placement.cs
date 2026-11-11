using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowsCalendar;

/// <summary>
/// Places the flyout next to the tray icon (tray-flyout R2, R2a): the icon's rectangle from Windows and that monitor's
/// work area, in physical pixels, so per-monitor DPI and the taskbar edge are respected. An icon in the hidden-icons
/// overflow gives no usable rectangle; the flyout then opens at the cursor. Corners are rounded by DWM.
/// </summary>
public sealed partial class TrayFlyout
{
    /// <summary>Gap between the flyout and the taskbar or screen edge, in epx.</summary>
    private const double Gap = 12;

    private void RoundCorners()
    {
        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).EnsureHandle(), DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>Moves and sizes the window for content <paramref name="height"/> epx tall, next to the tray icon.</summary>
    private void Place(double height)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var anchor = IconRect() ?? CursorRect();
        var monitor = MonitorFromRect(ref anchor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(height * scale);
        var gap = (int)Math.Round(Gap * scale);
        var work = info.Work;
        var screen = info.Monitor;
        var midX = (anchor.Left + anchor.Right) / 2;
        var midY = (anchor.Top + anchor.Bottom) / 2;

        // The taskbar edge is the side where the work area is smaller than the monitor; bottom if none (auto-hide).
        int x, y;
        if (work.Left > screen.Left)
        {
            (x, y) = (work.Left + gap, midY - h / 2);
        }
        else if (work.Right < screen.Right)
        {
            (x, y) = (work.Right - w - gap, midY - h / 2);
        }
        else if (work.Top > screen.Top)
        {
            (x, y) = (midX - w / 2, work.Top + gap);
        }
        else
        {
            (x, y) = (midX - w / 2, work.Bottom - h - gap);
        }
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - w - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));
        SetWindowPos(hwnd, HwndTopmost, x, y, w, h, SwpNoActivate);
    }

    /// <summary>The tray icon's rectangle, or null when Windows has none (the icon is in the closed overflow).</summary>
    private Rect32? IconRect()
    {
        if (TrayIcon is not { } icon)
        {
            return null;
        }
        var id = new NotifyIconIdentifier
        {
            Size = Marshal.SizeOf<NotifyIconIdentifier>(),
            Hwnd = icon.WindowHandle,
            Guid = icon.Id,
        };
        return Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top ? rect : null;
    }

    private static Rect32 CursorRect()
    {
        GetCursorPos(out var p);
        return new Rect32 { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + 1 };
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

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
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int Size;
        public IntPtr Hwnd;
        public uint Id;
        public Guid Guid;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect32 iconLocation);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect32 rect, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
