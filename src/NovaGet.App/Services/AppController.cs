using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NovaGet.App.Views;
using NovaGet.Core.CommandLine;

namespace NovaGet.App.Services;

internal sealed class AppController(
    Lazy<MainWindow> mainWindow,
    TrayIconService tray,
    ILogger<AppController> logger) : IAppController
{
    public bool IsExiting { get; private set; }

    private static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public void Initialize(bool showMainWindow)
    {
        tray.Initialize(this);
        if (showMainWindow)
        {
            ShowMainWindow();
        }
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

        // Toggling Topmost is the reliable way to come to the front without stealing focus rules.
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

        if (options.Url is not null || options.StartMainQueue || options.StartQueues.Count > 0 || options.StopQueues.Count > 0)
        {
            // Download and queue switches are executed once the engine and queues exist (milestones 2–11).
            logger.LogInformation("Command line actions received: url={Url} startQueues={Start} stopQueues={Stop}",
                options.Url, options.StartQueues, options.StopQueues);
        }

        if (!options.StartInTray && !options.Silent)
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
