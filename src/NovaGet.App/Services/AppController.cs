using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.App.ViewModels.Options;
using NovaGet.App.ViewModels.Scheduler;
using NovaGet.App.Views;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Models;
using NovaGet.Core.Services.Queues;
using NovaGet.Core.Settings;
using NovaGet.Core.Services;
using NovaGet.Core.Paths;

namespace NovaGet.App.Services;

internal sealed class AppController(
    Lazy<MainWindow> mainWindow,
    TrayIconService tray,
    DownloadUiService downloadUi,
    SoundService sounds,
    OptionsService options,
    SettingsPackageService settingsPackage,
    IQueueManager queueManager,
    QueueUiService queueUi,
    QuotaUiService quotaUi,
    IQueueRepository queues,
    ISettingsService settings,
    Func<SchedulerViewModel> schedulerFactory,
    IDownloadService downloads,
    IDialogService dialogs,
    AppPaths paths,
    ILogger<AppController> logger) : IAppController
{
    private SchedulerWindow? _scheduler;

    public bool IsExiting { get; private set; }

    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public void Initialize(bool showMainWindow)
    {
        tray.Initialize(this);
        downloadUi.Initialize();
        sounds.Initialize();
        queueUi.Initialize();
        quotaUi.Initialize();
        if (showMainWindow)
        {
            ShowMainWindow();
        }

        queueManager.OnAppStarted();
    }

    public void ShowMainWindow() => OnUiThread(() =>
    {
        var window = mainWindow.Value;
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        // Toggling Topmost is the reliable way to come to the front.
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    });

    public void HandleCommandLine(CommandLineOptions options) => OnUiThread(() =>
    {
        if (options.Exit)
        {
            RequestExit();
            return;
        }

        if (options.Url is not null)
        {
            _ = downloadUi.AddFromCommandLineAsync(options);
        }

        if (options.StartMainQueue)
        {
            queueManager.Start(DownloadQueue.MainQueueId);
        }

        foreach (var name in options.StartQueues)
        {
            if (!queueManager.Start(name) && !queueManager.RunningQueueIds.Any(id => queues.Get(id)?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true))
            {
                logger.LogWarning("/startqueue: no queue named {Queue}", name);
            }
        }

        foreach (var name in options.StopQueues)
        {
            _ = queueManager.StopAsync(name);
        }

        if (!options.StartInTray && !options.Silent && options.Url is null)
        {
            ShowMainWindow();
        }
    });

    public void RequestExit() => OnUiThread(() =>
    {
        if (IsExiting)
        {
            return;
        }

        IsExiting = true;
        logger.LogInformation("Exit requested");
        if (mainWindow.IsValueCreated)
        {
            mainWindow.Value.Close();
        }

        tray.Dispose();
        Application.Current.Shutdown();
    });

    public void ShowAddUrl(string? url = null) => OnUiThread(() => _ = downloadUi.ShowAddUrlAsync(url));

    public void StartDownload(long downloadId) => OnUiThread(() => downloadUi.StartDownload(downloadId));

    public void ShowAddBatch(bool fromClipboard) => NotAvailable();

    public void ShowOptions(string? page = null) => OnUiThread(() =>
    {
        var start = Enum.TryParse<OptionsPage>(page, ignoreCase: true, out var parsed) ? parsed : OptionsPage.General;
        if (dialogs.ActiveWindow is OptionsDialog open)
        {
            open.SelectPage(start);
            open.Activate();
            return;
        }

        dialogs.ShowModal(new OptionsDialog(options, settingsPackage, sounds, dialogs, paths, ShowHelp, start));

        // Categories may have been added, renamed or removed (also by Import / Reset).
        if (mainWindow.IsValueCreated && mainWindow.Value.DataContext is MainViewModel main)
        {
            main.BuildTree();
        }
    });

    public void ShowScheduler(long? queueId = null) => OnUiThread(() =>
    {
        if (_scheduler is { IsLoaded: true })
        {
            if (queueId is { } id)
            {
                _scheduler.Select(id);
            }

            if (_scheduler.WindowState == WindowState.Minimized)
            {
                _scheduler.WindowState = WindowState.Normal;
            }

            _scheduler.Activate();
            return;
        }

        _scheduler = new SchedulerWindow(schedulerFactory(), settings, queueId);
        if (mainWindow.IsValueCreated && mainWindow.Value.IsVisible)
        {
            _scheduler.Owner = mainWindow.Value;
        }

        _scheduler.Closed += (_, _) => _scheduler = null;
        _scheduler.Show();
    });

    public void ShowGrabber(long? projectId = null) => NotAvailable();

    public void ShowImport(bool ef2) => NotAvailable();

    public void ShowExport(bool ef2, IReadOnlyCollection<long>? ids) => NotAvailable();

    public void ShowProgress(long downloadId) => OnUiThread(() => downloadUi.ShowProgress(downloadId));

    public void ShowProperties(long downloadId) => OnUiThread(() =>
    {
        if (downloads.Find(downloadId) is { } download)
        {
            dialogs.ShowModal(new Views.Dialogs.PropertiesDialog(download, downloads, settings.Current.Advanced.AllowIgnoringCertificateErrors));
        }
    });

    public void ShowMoveRename(long downloadId) => OnUiThread(() =>
    {
        if (downloads.Find(downloadId) is { } download)
        {
            dialogs.ShowModal(new Views.Dialogs.MoveRenameDialog(download, downloads));
        }
    });

    public void RefreshAddress(long downloadId) => NotAvailable();

    public void StartQueue(long queueId) => queueManager.Start(queueId);

    public void StopQueue(long queueId) => _ = queueManager.StopAsync(queueId);

    public async Task StopAllAsync()
    {
        await queueManager.StopAllAsync();
        await downloads.StopAllAsync();
    }

    public DownloadQueue? CreateQueue() => queueUi.CreateInteractive();

    public Task DeleteQueueAsync(long queueId) => queueUi.DeleteAsync(queueId);

    public void ToggleDropTarget() => NotAvailable();

    public void CheckForUpdates() => NotAvailable();

    public void ShowHelp(string? topic = null)
    {
        var page = Path.Combine(paths.ExecutableDir, "docs", (topic ?? "index") + ".html");
        if (File.Exists(page))
        {
            ShellService.OpenUrl(page);
        }
        else
        {
            NotAvailable();
        }
    }

    private void NotAvailable() => OnUiThread(() => dialogs.Info(Localizer.Get("Msg_NotAvailable")));

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }
}
