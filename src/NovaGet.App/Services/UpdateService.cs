using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.Core;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// The optional update check (Options → Advanced → "Check weekly", on by default): at most once a week, a tray notice
/// when a newer release exists; Help → Check for updates asks at once and offers the release page. Nothing is
/// downloaded or installed automatically.
/// </summary>
internal sealed class UpdateService(
    ISettingsService settings,
    IHttpClientProvider clients,
    IDialogService dialogs,
    TrayIconService tray,
    ILogger<UpdateService> logger) : IDisposable
{
    private DispatcherTimer? _timer;

    public Uri Feed { get; init; } = new(AppInfo.UpdateFeed);

    public void Initialize()
    {
        if (_timer is not null || Application.Current is null)
        {
            return;
        }

        // First look a minute after start, then every few hours (the weekly rule decides whether to ask).
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) =>
        {
            _timer!.Interval = TimeSpan.FromHours(6);
            await CheckAutomaticallyAsync();
        }, Application.Current.Dispatcher);
        _timer.Start();
    }

    public async Task CheckAutomaticallyAsync()
    {
        var advanced = settings.Current.Advanced;
        if (!advanced.CheckForUpdatesWeekly || !UpdateChecker.IsDue(advanced.LastUpdateCheck, DateTimeOffset.UtcNow))
        {
            return;
        }

        settings.Update(s => s.Advanced.LastUpdateCheck = DateTimeOffset.UtcNow);
        try
        {
            if (await FetchAsync() is { } update && UpdateChecker.IsNewer(update, AppInfo.Version))
            {
                logger.LogInformation("NovaGet {Version} is available", update.Tag);
                tray.ShowBalloon(Localizer.Format("Update_AvailableTitle", UpdateChecker.Display(update.Version)), Localizer.Get("Update_AvailableHint"));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DownloadException)
        {
            logger.LogInformation("Update check failed: {Error}", ex.Message);
        }
    }

    /// <summary>Help → Check for updates.</summary>
    public async Task CheckNowAsync()
    {
        try
        {
            var update = await FetchAsync();
            settings.Update(s => s.Advanced.LastUpdateCheck = DateTimeOffset.UtcNow);
            if (update is not null && UpdateChecker.IsNewer(update, AppInfo.Version))
            {
                if (dialogs.Confirm(Localizer.Format("Update_Available", UpdateChecker.Display(update.Version), AppInfo.InformationalVersion)))
                {
                    ShellService.OpenUrl(update.PageUrl.AbsoluteUri);
                }
            }
            else
            {
                dialogs.Info(Localizer.Format("Update_UpToDate", AppInfo.InformationalVersion));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DownloadException)
        {
            dialogs.Error(Localizer.Format("Update_Failed", ex.Message));
        }
    }

    public void Dispose() => _timer?.Stop();

    private Task<UpdateInfo?> FetchAsync() =>
        UpdateChecker.FetchAsync(clients.GetClient(new RequestContext { Url = Feed }), Feed, CancellationToken.None);
}
