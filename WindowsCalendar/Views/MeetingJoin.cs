using System;
using Serilog;
using Windows.System;
using WindowsCalendar.Core.Store;

namespace WindowsCalendar.Views;

/// <summary>Opens a meeting's join link with the Windows default handler, so Teams or Zoom take over (online-meeting-join R5).</summary>
internal static class MeetingJoin
{
    /// <summary>
    /// Only a link that passes R2 again (HTTPS on a recognised host) is opened. The URL is not logged: it may carry a
    /// meeting passcode.
    /// </summary>
    public static async void Launch(JoinLink link)
    {
        if (JoinLink.Recognise(link.Url) is not { } safe)
        {
            return;
        }
        try
        {
            if (await Launcher.LaunchUriAsync(new Uri(safe.Url)))
            {
                Log.Information("Opened a {Provider} meeting", safe.Provider);
            }
            else
            {
                Log.Warning("Windows did not open the {Provider} meeting", safe.Provider);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening the {Provider} meeting failed", safe.Provider);
        }
    }
}
