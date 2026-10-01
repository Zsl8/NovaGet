using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.App.Services;

namespace NovaGet.App.Hosting;

/// <summary><c>NovaGet.exe /cleanup</c>, run by the uninstaller: removes what NovaGet registered outside its folders.</summary>
internal static class UninstallCleanup
{
    public static int Run()
    {
        // Scheduled queue wake tasks (\NovaGet\Queue n).
        new WakeTaskService(NullLogger<WakeTaskService>.Instance).RemoveAll();

        // "Launch on startup" set from Options (the installer removes its own Run value).
        StartupRegistration.Remove();

        // Toast notification registration (AUMID and activator).
        Toasts.Uninstall();
        return 0;
    }
}
