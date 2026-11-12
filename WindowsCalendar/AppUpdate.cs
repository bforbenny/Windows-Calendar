using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace WindowsCalendar;

/// <summary>Check for and install package updates from the .appinstaller the app was installed from (app-updates R7).</summary>
internal static class AppUpdate
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// <summary>The .appinstaller URI the package was installed from, or null when there is none (or unpackaged).</summary>
    public static Uri? Source()
    {
        try
        {
            return Package.Current.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "No App Installer source");
            return null;
        }
    }

    /// <summary>Asks Windows whether a newer package is available. Returns (available, text for the user).</summary>
    public static async Task<(bool Available, string Message)> CheckAsync()
    {
        try
        {
            var result = await Package.Current.CheckUpdateAvailabilityAsync();
            switch (result.Availability)
            {
                case PackageUpdateAvailability.Available:
                case PackageUpdateAvailability.Required:
                    return (true, "An update is available");
                case PackageUpdateAvailability.NoUpdates:
                    return (false, "WindowsCalendar is up to date");
                default:
                    Log.Warning(result.ExtendedError, "Update check returned {Availability}", result.Availability);
                    return (false, $"Couldn't check for updates: {Reason(result.ExtendedError)}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            return (false, $"Couldn't check for updates: {Reason(ex)}");
        }
    }

    /// <summary>Registers for restart, then installs from <paramref name="source"/>; Windows closes the app. Returns an error text, or null.</summary>
    public static async Task<string?> InstallAsync(Uri source)
    {
        try
        {
            var hr = RegisterApplicationRestart(null, 0);
            Log.Information("RegisterApplicationRestart returned 0x{Hresult:X8}", hr);
            var manager = new PackageManager();
            var result = await manager.AddPackageByAppInstallerFileAsync(
                source, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, manager.GetDefaultPackageVolume());
            if (result.ExtendedErrorCode is { } error)
            {
                Log.Warning(error, "Update install failed: {Text}", result.ErrorText);
                return string.IsNullOrWhiteSpace(result.ErrorText) ? Reason(error) : result.ErrorText.Trim();
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update install failed");
            return Reason(ex);
        }
    }

    private static string Reason(Exception? ex) => string.IsNullOrWhiteSpace(ex?.Message) ? "unknown error" : ex.Message.Trim();
}
