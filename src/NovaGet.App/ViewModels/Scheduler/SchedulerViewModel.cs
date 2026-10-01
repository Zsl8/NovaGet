using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Services;
using NovaGet.Core.Services.Queues;

namespace NovaGet.App.ViewModels.Scheduler;

/// <summary>Downloads → Scheduler (section 10): queues on the left, their schedule and files on the right.</summary>
public sealed partial class SchedulerViewModel : ObservableObject, IDisposable
{
    private readonly IQueueRepository _queues;
    private readonly IDownloadService _downloads;
    private readonly IDownloadEngine _engine;
    private readonly IQueueManager _manager;
    private readonly IAppController _controller;
    private readonly IDialogService _dialogs;
    private readonly Func<DownloadQueue, string?> _save;
    private readonly Dispatcher _dispatcher;

    internal SchedulerViewModel(
        IQueueRepository queues,
        IDownloadService downloads,
        IDownloadEngine engine,
        IQueueManager manager,
        IAppController controller,
        IDialogService dialogs,
        QueueUiService queueUi)
        : this(queues, downloads, engine, manager, controller, dialogs, queueUi.Save)
    {
    }

    internal SchedulerViewModel(
        IQueueRepository queues,
        IDownloadService downloads,
        IDownloadEngine engine,
        IQueueManager manager,
        IAppController controller,
        IDialogService dialogs,
        Func<DownloadQueue, string?> save)
    {
        _queues = queues;
        _downloads = downloads;
        _engine = engine;
        _manager = manager;
        _controller = controller;
        _dialogs = dialogs;
        _save = save;
        _dispatcher = Dispatcher.CurrentDispatcher;
        LoadQueues(null);
        _downloads.Changed += OnDownloadsChanged;
        _downloads.StateChanged += OnStateChanged;
        _manager.QueueStarted += OnQueueChanged;
        _manager.QueueStopped += OnQueueChanged;
    }

    public ObservableCollection<QueueEditViewModel> Queues { get; } = [];

    public ObservableCollection<DownloadItemViewModel> Files { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteQueueCommand), nameof(StartNowCommand), nameof(StopCommand))]
    private QueueEditViewModel? _selectedQueue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(DeleteFromQueueCommand))]
    private DownloadItemViewModel? _selectedFile;

    public bool HasChanges => Queues.Any(q => q.IsDirty);

    /// <summary>Selects a queue (when opened from a queue's context menu).</summary>
    public void Select(long queueId) => SelectedQueue = Queues.FirstOrDefault(q => q.Id == queueId) ?? SelectedQueue;

    /// <summary>Live status and time left of the listed files (called every second by the window).</summary>
    public void RefreshProgress()
    {
        foreach (var file in Files)
        {
            if (_engine.GetProgress(file.Id) is { } progress)
            {
                file.UpdateProgress(progress);
            }
        }

        foreach (var queue in Queues)
        {
            queue.IsRunning = _manager.IsRunning(queue.Id);
        }

        StartNowCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Saves every changed queue. False (with the queue selected and a message shown) when one is invalid.</summary>
    public bool ApplyAll()
    {
        foreach (var queue in Queues.Where(q => q.IsDirty).ToList())
        {
            if (!Apply(queue))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Drag and drop: moves a file to a new place in the list.</summary>
    public void MoveFile(DownloadItemViewModel file, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(file);
        var index = Files.IndexOf(file);
        if (index < 0 || newIndex == index)
        {
            return;
        }

        _downloads.MoveInQueue(file.Id, Math.Clamp(newIndex, 0, Files.Count - 1) - index);
        LoadFiles();
        SelectedFile = Files.FirstOrDefault(f => f.Id == file.Id);
    }

    public void Dispose()
    {
        _downloads.Changed -= OnDownloadsChanged;
        _downloads.StateChanged -= OnStateChanged;
        _manager.QueueStarted -= OnQueueChanged;
        _manager.QueueStopped -= OnQueueChanged;
    }

    partial void OnSelectedQueueChanged(QueueEditViewModel? value) => LoadFiles();

    [RelayCommand]
    private void NewQueue()
    {
        if (_controller.CreateQueue() is { } queue)
        {
            var edit = new QueueEditViewModel(queue);
            Queues.Add(edit);
            SelectedQueue = edit;
        }
    }

    private bool CanDeleteQueue() => SelectedQueue is { IsBuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanDeleteQueue))]
    private async Task DeleteQueue()
    {
        if (SelectedQueue is not { IsBuiltIn: false } queue || !_dialogs.Confirm(Localizer.Format("Confirm_DeleteQueue", queue.Title)))
        {
            return;
        }

        await _controller.DeleteQueueAsync(queue.Id);
        var index = Queues.IndexOf(queue);
        Queues.Remove(queue);
        SelectedQueue = Queues.Count == 0 ? null : Queues[Math.Clamp(index - 1, 0, Queues.Count - 1)];
    }

    private bool CanStart() => SelectedQueue is { } queue && !_manager.IsRunning(queue.Id);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartNow()
    {
        if (SelectedQueue is not { } queue || (queue.IsDirty && !Apply(queue)))
        {
            return;
        }

        _manager.Start(queue.Id);
        RefreshProgress();
    }

    private bool CanStop() => SelectedQueue is { } queue && _manager.IsRunning(queue.Id);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        if (SelectedQueue is { } queue)
        {
            await _manager.StopAsync(queue.Id);
            RefreshProgress();
        }
    }

    [RelayCommand]
    private void ApplyChanges() => ApplyAll();

    private bool CanMoveUp() => SelectedFile is { } file && Files.IndexOf(file) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => MoveFile(SelectedFile!, Files.IndexOf(SelectedFile!) - 1);

    private bool CanMoveDown() => SelectedFile is { } file && Files.IndexOf(file) < Files.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => MoveFile(SelectedFile!, Files.IndexOf(SelectedFile!) + 1);

    private bool HasFile() => SelectedFile is not null;

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void DeleteFromQueue()
    {
        if (SelectedFile is { } file)
        {
            _downloads.SetQueue([file.Id], null);
        }
    }

    [RelayCommand]
    private void MoreAtOnce()
    {
        if (SelectedQueue is { SimultaneousCount: < 32 } queue)
        {
            queue.SimultaneousCount++;
        }
    }

    [RelayCommand]
    private void FewerAtOnce()
    {
        if (SelectedQueue is { SimultaneousCount: > 1 } queue)
        {
            queue.SimultaneousCount--;
        }
    }

    private bool Apply(QueueEditViewModel queue)
    {
        var error = queue.Validate();
        if (error is null && queue.CanRename)
        {
            error = QueueUiService.NameError(queue.Name, _queues, queue.Id);
        }

        if (error is not null)
        {
            SelectedQueue = queue;
            _dialogs.Error(error);
            return false;
        }

        var model = queue.ToModel();
        var wakeError = _save(model);
        queue.MarkSaved(model);
        if (wakeError is not null)
        {
            _dialogs.Error(Localizer.Format("Scheduler_ErrorWake", wakeError));
        }

        return true;
    }

    private void LoadQueues(long? select)
    {
        Queues.Clear();
        foreach (var queue in _queues.GetAll())
        {
            Queues.Add(new QueueEditViewModel(queue) { IsRunning = _manager.IsRunning(queue.Id) });
        }

        SelectedQueue = Queues.FirstOrDefault(q => q.Id == select) ?? Queues.FirstOrDefault();
    }

    private void LoadFiles()
    {
        var selectedId = SelectedFile?.Id;
        Files.Clear();
        if (SelectedQueue is { } queue)
        {
            foreach (var download in _downloads.GetAll().Where(d => d.QueueId == queue.Id).OrderBy(d => d.QueuePosition).ThenBy(d => d.Id))
            {
                Files.Add(new DownloadItemViewModel(download));
            }
        }

        SelectedFile = Files.FirstOrDefault(f => f.Id == selectedId);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void OnDownloadsChanged(object? sender, DownloadListChangedEventArgs e) => OnUi(LoadFiles);

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e) => OnUi(() =>
    {
        if (Files.FirstOrDefault(f => f.Id == e.Id) is { } file && _downloads.Find(e.Id) is { } download)
        {
            file.Update(download);
        }
    });

    private void OnQueueChanged(object? sender, QueueEventArgs e) => OnUi(RefreshProgress);

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
