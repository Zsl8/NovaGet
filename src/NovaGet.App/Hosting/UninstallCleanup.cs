namespace NovaGet.App.Hosting;

/// <summary>
/// <c>NovaGet.exe /cleanup</c>, run by the uninstaller: removes registrations the app made at run time
/// (the Run key when set from Options, scheduled wake tasks). Must never show UI.
/// </summary>
internal static class UninstallCleanup
{
    public static int Run()
    {
        // Wake tasks and startup registration are added by later milestones; each registers its cleanup here.
        return 0;
    }
}
