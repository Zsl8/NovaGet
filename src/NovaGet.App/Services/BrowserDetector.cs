using Microsoft.Win32;
using NovaGet.Core.Integration;

namespace NovaGet.App.Services;

/// <summary>Finds installed browsers from the registered web clients and the App Paths list.</summary>
internal static class BrowserDetector
{
    public static IReadOnlySet<string> InstalledBrowserIds()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var clients = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                foreach (var name in clients?.GetSubKeyNames() ?? [])
                {
                    using var command = clients!.OpenSubKey($@"{name}\shell\open\command");
                    if (BrowserCatalog.FromExecutable(command?.GetValue(null) as string) is { } browser)
                    {
                        found.Add(browser.Id);
                    }
                }

                foreach (var browser in BrowserCatalog.All)
                {
                    foreach (var exe in browser.ExecutableNames.Where(e => !e.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        using var appPath = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                        if (appPath?.GetValue(null) is string { Length: > 0 })
                        {
                            found.Add(browser.Id);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
            }
        }

        return found;
    }
}
