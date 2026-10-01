using System.Windows;
using System.Windows.Threading;
using NovaGet.App.Localization;
using NovaGet.Core.Formatting;
using NovaGet.Core.Services;

namespace NovaGet.App.Services;

/// <summary>Download limits in the UI: the 1-second check, the optional warning, and tray notifications.</summary>
internal sealed class QuotaUiService(DownloadQuotaService quota, TrayIconService tray, IDialogService dialogs) : IDisposable
{
    private static readonly TimeSpan RefusalNoticeInterval = TimeSpan.FromSeconds(30);
    private DispatcherTimer? _timer;
    private DateTime _lastRefusalNotice = DateTime.MinValue;

    public void Initialize()
    {
        quota.ConfirmStop = Confirm;
        quota.LimitReached += OnLimitReached;
        quota.StartRefused += OnStartRefused;
        quota.Resumed += OnResumed;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = quota.TickAsync(), Application.Current.Dispatcher);
        _timer.Start();
    }

    public void Dispose()
    {
        _timer?.Stop();
        quota.ConfirmStop = null;
        quota.LimitReached -= OnLimitReached;
        quota.StartRefused -= OnStartRefused;
        quota.Resumed -= OnResumed;
    }

    private static string Time(DateTimeOffset when) => when.ToLocalTime().ToString("t", Localizer.Culture);

    /// <summary>"Show warning before stopping downloads": Yes stops now, No keeps downloading for the rest of this period.</summary>
    private bool Confirm(QuotaEventArgs e) => OnUi(() => dialogs.Confirm(Localizer.Format("Quota_Warning",
        DisplayFormat.Size(e.UsedBytes, 1, Localizer.Culture), Time(e.ResumeAt))));

    private void OnLimitReached(object? sender, QuotaEventArgs e) =>
        OnUi(() => tray.ShowBalloon(Localizer.Get("Quota_ReachedTitle"), Localizer.Format("Quota_ResumeAt", Time(e.ResumeAt))));

    private void OnStartRefused(object? sender, QuotaEventArgs e) => OnUi(() =>
    {
        if (DateTime.UtcNow - _lastRefusalNotice < RefusalNoticeInterval)
        {
            return;
        }

        _lastRefusalNotice = DateTime.UtcNow;
        tray.ShowBalloon(Localizer.Get("Quota_ReachedTitle"), Localizer.Format("Quota_ResumeAt", Time(e.ResumeAt)));
    });

    private void OnResumed(object? sender, EventArgs e) =>
        OnUi(() => tray.ShowBalloon(Localizer.Get("Quota_ResumedTitle"), Localizer.Get("Quota_Resumed")));

    private static void OnUi(Action action) => OnUi(() =>
    {
        action();
        return true;
    });

    private static T OnUi<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return default!;
        }

        return dispatcher.CheckAccess() ? func() : dispatcher.Invoke(func);
    }
}
