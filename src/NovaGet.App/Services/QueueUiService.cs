using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;
using NovaGet.Core.Services;
using NovaGet.Core.Services.Queues;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// The UI around queues: sounds and notifications when queues start and stop, "after the queue finishes" actions,
/// and creating and deleting queues (which also keeps their wake tasks in step).
/// </summary>
internal sealed class QueueUiService(
    IQueueManager manager,
    IQueueRepository queues,
    IDownloadService downloads,
    SoundService sounds,
    TrayIconService tray,
    WakeTaskService wakeTasks,
    DownloadUiService downloadUi,
    IDialogService dialogs,
    IDialUpService dialUp,
    ISettingsService settings,
    ILogger<QueueUiService> logger) : IDisposable
{
    private static readonly TimeSpan RedialCheck = TimeSpan.FromSeconds(15);
    private Timer? _redial;
    private int _dialing;

    public void Initialize()
    {
        manager.QueueStarted += OnQueueStarted;
        manager.QueueStopped += OnQueueStopped;
        manager.BeforeStart = ConnectBeforeScheduledStart;
        _redial = new Timer(_ => RedialIfNeeded(), null, RedialCheck, RedialCheck);
    }

    public void Dispose()
    {
        manager.QueueStarted -= OnQueueStarted;
        manager.QueueStopped -= OnQueueStopped;
        manager.BeforeStart = null;
        _redial?.Dispose();
    }

    /// <summary>
    /// "Connect before starting scheduled queues": scheduled and command-line starts arrive off the UI thread and
    /// may wait for the connection; a queue the user starts by hand doesn't dial (and never blocks the window).
    /// </summary>
    private void ConnectBeforeScheduledStart(DownloadQueue queue)
    {
        var dialUpSettings = settings.Current.DialUp;
        if (!dialUpSettings.ConnectBeforeScheduledQueues || string.IsNullOrWhiteSpace(dialUpSettings.ConnectionName)
            || Application.Current?.Dispatcher.CheckAccess() == true)
        {
            return;
        }

        Dial($"before queue {queue.Name}");
    }

    /// <summary>"Redial if disconnected" while queues run.</summary>
    private void RedialIfNeeded()
    {
        var dialUpSettings = settings.Current.DialUp;
        if (!dialUpSettings.RedialIfDisconnected || string.IsNullOrWhiteSpace(dialUpSettings.ConnectionName)
            || manager.RunningQueueIds.Count == 0 || dialUp.IsConnected(dialUpSettings.ConnectionName))
        {
            return;
        }

        Dial("redial");
    }

    private void Dial(string reason)
    {
        if (Interlocked.Exchange(ref _dialing, 1) == 1)
        {
            return;
        }

        try
        {
            if (dialUp.Connect() is { } error)
            {
                logger.LogWarning("Dial-up connection failed ({Reason}): {Error}", reason, error);
                OnUi(() => tray.ShowBalloon(Localizer.Get("Notify_DialFailed"), error, error: true));
            }
        }
        finally
        {
            Volatile.Write(ref _dialing, 0);
        }
    }

    /// <summary>Message for a name that can't be used, or null.</summary>
    public static string? NameError(string name, IQueueRepository queues, long? renamingId = null) =>
        QueueNames.Check(name, queues, renamingId) switch
        {
            QueueNameProblem.None => null,
            QueueNameProblem.Empty => Localizer.Get("Error_NameRequired"),
            QueueNameProblem.Exists => Localizer.Get("Error_QueueExists"),
            QueueNameProblem.TooLong => Localizer.Format("Error_QueueNameLength", QueueNames.MaxLength),
            _ => Localizer.Get("Error_QueueNameChars"),
        };

    /// <summary>Asks for a name and adds the queue. Null when canceled.</summary>
    public DownloadQueue? CreateInteractive()
    {
        var dialog = new InputDialog(Localizer.Get("QueueDialog_Title"), Localizer.Get("QueueDialog_Name"), string.Empty,
            name => NameError(name, queues));
        if (dialogs.ShowModal(dialog) != true)
        {
            return null;
        }

        var queue = new DownloadQueue { Name = dialog.Value };
        queues.Insert(queue);
        RefreshMainWindow();
        return queue;
    }

    /// <summary>Stops the queue, takes its files out of it, deletes it and its wake task.</summary>
    public async Task DeleteAsync(long queueId)
    {
        var queue = queues.Get(queueId);
        if (queue is null || queue.IsBuiltIn)
        {
            return;
        }

        await manager.StopAsync(queueId);
        downloads.SetQueue([.. downloads.GetAll().Where(d => d.QueueId == queueId).Select(d => d.Id)], null);
        queues.Delete(queueId);
        wakeTasks.Remove(queueId);
        RefreshMainWindow();
    }

    /// <summary>Saves a queue's settings and updates its wake task. Returns a wake task error, or null.</summary>
    public string? Save(DownloadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        queues.Update(queue);
        RefreshMainWindow();
        return wakeTasks.Update(queue);
    }

    public static void RefreshMainWindow()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.BuildTree();
            vm.BuildQueueMenus();
        }
    }

    private void OnQueueStarted(object? sender, QueueEventArgs e) => OnUi(() =>
    {
        sounds.Play(SoundEvent.QueueStarted);
        tray.ShowBalloon(Localizer.Get("Notify_QueueStarted"), MainViewModel.QueueTitle(e.Queue));
    });

    private void OnQueueStopped(object? sender, QueueEventArgs e) => OnUi(() =>
    {
        sounds.Play(SoundEvent.QueueStopped);
        if (e.Reason != QueueStopReason.Finished)
        {
            return;
        }

        var title = MainViewModel.QueueTitle(e.Queue);
        tray.ShowBalloon(Localizer.Get("Notify_QueueFinished"), title);
        var schedule = e.Queue.Schedule;
        if (schedule.OpenFileWhenDone && !string.IsNullOrWhiteSpace(schedule.OpenFilePath))
        {
            // A file the user picked in the scheduler, never one that was just downloaded.
            var path = Environment.ExpandEnvironmentVariables(schedule.OpenFilePath);
            if (!File.Exists(path) || !ShellService.OpenFile(path))
            {
                logger.LogWarning("Could not open {File} after queue {Queue}", path, e.Queue.Name);
            }
        }

        downloadUi.RunCompletionActions(new CompletionOptions
        {
            HangUp = schedule.HangUpWhenDone,
            ExitWhenDone = schedule.ExitWhenDone,
            TurnOff = schedule.TurnOffWhenDone,
            PowerAction = schedule.PowerAction == PowerAction.None ? PowerAction.ShutDown : schedule.PowerAction,
            ForceProcesses = schedule.ForceProcessesToTerminate,
        });
    });

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
