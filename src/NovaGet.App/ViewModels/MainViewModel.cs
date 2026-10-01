using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Formatting;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.ViewModels;

/// <summary>The main window: categories tree, download list, toolbar, menus and status bar.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly string[] DefaultToolbarOrder =
        ["AddUrl", "Resume", "Stop", "StopAll", "Delete", "DeleteCompleted", "Options", "Scheduler", "StartQueue", "StopQueue", "Grabber", "TellFriend"];

    private readonly IDownloadService _downloads;
    private readonly IDownloadEngine _engine;
    private readonly ICategoryRepository _categories;
    private readonly IQueueRepository _queues;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IAppController _controller;
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<long, DownloadItemViewModel> _byId = [];
    private readonly DispatcherTimer _timer;
    private readonly List<IRelayCommand> _selectionCommands = [];
    private List<DownloadItemViewModel> _selection = [];
    private int _tick;
    private string _findText = string.Empty;
    private bool _findMatchCase;

    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _statusDownloads = string.Empty;

    [ObservableProperty]
    private string _statusActive = string.Empty;

    [ObservableProperty]
    private string _statusSpeed = string.Empty;

    [ObservableProperty]
    private string _statusLimiter = string.Empty;

    public MainViewModel(
        IDownloadService downloads,
        IDownloadEngine engine,
        ICategoryRepository categories,
        IQueueRepository queues,
        ISettingsService settings,
        IDialogService dialogs,
        IAppController controller,
        AppPaths paths)
    {
        _downloads = downloads;
        _engine = engine;
        _categories = categories;
        _queues = queues;
        _settings = settings;
        _dialogs = dialogs;
        _controller = controller;
        _paths = paths;
        _dispatcher = Dispatcher.CurrentDispatcher;

        ItemsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => SelectedNode?.Matches((DownloadItemViewModel)item) ?? true;
        ItemsView.IsLiveFiltering = true;
        foreach (var property in new[] { nameof(DownloadItemViewModel.IsCompleted), nameof(DownloadItemViewModel.CategoryId), nameof(DownloadItemViewModel.QueueId) })
        {
            ItemsView.LiveFilteringProperties.Add(property);
        }

        _selectionCommands.AddRange(
        [
            ResumeCommand, StopCommand, DeleteCommand, DeleteWithFileCommand, OpenCommand, OpenWithCommand, OpenFolderCommand,
            MoveRenameCommand, PropertiesCommand, RedownloadCommand, MoveUpCommand, MoveDownCommand, RefreshAddressCommand,
            CopyAddressCommand, AddToQueueCommand, DeleteFromQueueCommand, TogglePauseCommand, ExportSelectedCommand,
        ]);

        BuildToolbar();
        LoadDownloads();
        BuildTree();
        BuildQueueMenus();
        BuildArrangeMenu();
        ApplySort(settings.Current.Ui.SortColumn, settings.Current.Ui.SortDescending, persist: false);
        UpdateStatusBar();

        _downloads.Changed += OnDownloadsChanged;
        _settings.Changed += OnSettingsChanged;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnTick, _dispatcher);
        _timer.Start();
    }

    /// <summary>The view should select and show this item (Find).</summary>
    public event EventHandler<DownloadItemViewModel>? RevealRequested;

    /// <summary>The sort changed (the view updates the column header arrows).</summary>
    public event EventHandler? SortChanged;

    /// <summary>View → Show/Hide columns (the view owns the columns).</summary>
    public event EventHandler? ColumnsDialogRequested;

    public static string Title => AppInfo.MainWindowTitle;

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    /// <summary>Filtered (selected tree node) and sorted view the list binds to.</summary>
    public ListCollectionView ItemsView { get; }

    public ObservableCollection<TreeNodeViewModel> Tree { get; } = [];

    public ObservableCollection<ToolbarItemViewModel> Toolbar { get; } = [];

    public ObservableCollection<MenuEntryViewModel> StartQueueMenu { get; } = [];

    public ObservableCollection<MenuEntryViewModel> StopQueueMenu { get; } = [];

    public ObservableCollection<MenuEntryViewModel> AddToQueueMenu { get; } = [];

    public ObservableCollection<MenuEntryViewModel> ArrangeMenu { get; } = [];

    public ObservableCollection<MenuEntryViewModel> SortDirectionMenu { get; } = [];

    public ObservableCollection<MenuEntryViewModel> DeleteMenu { get; } = [];

    public IReadOnlyList<DownloadItemViewModel> Selection => _selection;

    public string SortKey { get; private set; } = DownloadItemComparer.OrderOfAddition;

    public bool SortDescending { get; private set; }

    public bool ShowCategories
    {
        get => _settings.Current.Ui.ShowCategories;
        set
        {
            if (value != ShowCategories)
            {
                _settings.Update(s => s.Ui.ShowCategories = value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HideCategories));
            }
        }
    }

    public bool HideCategories
    {
        get => !ShowCategories;
        set => ShowCategories = !value;
    }

    public bool LargeToolbar
    {
        get => _settings.Current.Ui.ToolbarSize == ToolbarButtonSize.Large;
        set
        {
            _settings.Update(s => s.Ui.ToolbarSize = value ? ToolbarButtonSize.Large : ToolbarButtonSize.Small);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SmallToolbar));
        }
    }

    public bool SmallToolbar
    {
        get => !LargeToolbar;
        set => LargeToolbar = !value;
    }

    public bool ShowToolbarLabels
    {
        get => _settings.Current.Ui.ToolbarShowLabels;
        set
        {
            _settings.Update(s => s.Ui.ToolbarShowLabels = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HideToolbarLabels));
        }
    }

    public bool HideToolbarLabels
    {
        get => !ShowToolbarLabels;
        set => ShowToolbarLabels = !value;
    }

    public bool ShowTraySpeedGraph
    {
        get => _settings.Current.General.ShowTraySpeedGraph;
        set
        {
            _settings.Update(s => s.General.ShowTraySpeedGraph = value);
            OnPropertyChanged();
        }
    }

    public bool ShowDropTarget => _settings.Current.General.ShowDropTarget;

    public bool IsLimiterOn => _settings.Current.SpeedLimiter.Enabled;

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void SetSelection(IEnumerable<DownloadItemViewModel> items)
    {
        _selection = [.. items];
        NotifySelectionCommands();
    }

    /// <summary>Double-click on a row: completed → configured action; otherwise the progress dialog.</summary>
    public void Activate(DownloadItemViewModel item)
    {
        if (!item.IsCompleted)
        {
            _controller.ShowProgress(item.Id);
            return;
        }

        switch (_settings.Current.Downloads.CompletedDoubleClick)
        {
            case CompletedDoubleClickAction.OpenFile:
                OpenItem(item);
                break;
            case CompletedDoubleClickAction.OpenFolder:
                ShellService.OpenFolder(item.Model.FullPath);
                break;
            default:
                _controller.ShowProperties(item.Id);
                break;
        }
    }

    /// <summary>Dropping list rows on a tree node moves them to that category or queue.</summary>
    public void MoveToNode(IReadOnlyList<long> ids, TreeNodeViewModel node)
    {
        if (ids.Count == 0)
        {
            return;
        }

        if (node.Kind == TreeNodeKind.Queue && node.QueueId is { } queueId)
        {
            _downloads.SetQueue(ids, queueId);
        }
        else if (node.Kind == TreeNodeKind.Category && node.CategoryId is { } categoryId)
        {
            _downloads.SetCategory(ids, categoryId);
        }
        else if (node.Kind is TreeNodeKind.AllDownloads or TreeNodeKind.Unfinished or TreeNodeKind.Finished)
        {
            _downloads.SetCategory(ids, Category.GeneralId);
        }
    }

    public static bool CanDropOn(TreeNodeViewModel node) =>
        node.Kind is TreeNodeKind.Queue or TreeNodeKind.Category or TreeNodeKind.AllDownloads or TreeNodeKind.Unfinished or TreeNodeKind.Finished;

    /// <summary>Header click: same column toggles the direction, a new column sorts ascending.</summary>
    public void ToggleSort(string key) =>
        ApplySort(key, key == SortKey ? !SortDescending : false, persist: true);

    public void Dispose()
    {
        _timer.Stop();
        _downloads.Changed -= OnDownloadsChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    // ----------------------------------------------------------------- list maintenance

    private void LoadDownloads()
    {
        foreach (var download in _downloads.GetAll())
        {
            var item = new DownloadItemViewModel(download);
            _byId[item.Id] = item;
            Items.Add(item);
        }
    }

    private void OnDownloadsChanged(object? sender, DownloadListChangedEventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnDownloadsChanged(sender, e));
            return;
        }

        switch (e.Change)
        {
            case DownloadListChange.Added:
                foreach (var id in e.Ids)
                {
                    if (!_byId.ContainsKey(id) && _downloads.Find(id) is { } download)
                    {
                        var item = new DownloadItemViewModel(download);
                        _byId[id] = item;
                        Items.Add(item);
                    }
                }

                break;
            case DownloadListChange.Removed:
                foreach (var id in e.Ids)
                {
                    if (_byId.Remove(id, out var item))
                    {
                        Items.Remove(item);
                        _selection.Remove(item);
                    }
                }

                break;
            case DownloadListChange.Updated:
                foreach (var id in e.Ids)
                {
                    if (_byId.TryGetValue(id, out var item) && _downloads.Find(id) is { } download)
                    {
                        item.Update(download);
                    }
                }

                break;
        }

        NotifySelectionCommands();
        UpdateStatusBar();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        foreach (var id in _engine.RunningIds)
        {
            if (_byId.TryGetValue(id, out var item) && _engine.GetProgress(id) is { } progress)
            {
                item.UpdateProgress(progress);
            }
        }

        if (++_tick % 4 == 0)
        {
            UpdateStatusBar();
            NotifySelectionCommands();
        }
    }

    private void UpdateStatusBar()
    {
        var stats = _downloads.GetStatistics();
        StatusDownloads = Localizer.Format("StatusBar_Downloads", stats.Total);
        StatusActive = Localizer.Format("StatusBar_Active", stats.Active);
        StatusSpeed = stats.Active > 0 ? DisplayFormat.Rate(stats.BytesPerSecond, 1, Localizer.Culture) : string.Empty;
        var limiter = _settings.Current.SpeedLimiter;
        StatusLimiter = limiter.Enabled
            ? Localizer.Format("StatusBar_LimiterOn", limiter.MaxKBps)
            : Localizer.Get("StatusBar_LimiterOff");
        StopAllCommand.NotifyCanExecuteChanged();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(IsLimiterOn));
        OnPropertyChanged(nameof(ShowDropTarget));
        UpdateStatusBar();
    }

    private void NotifySelectionCommands()
    {
        foreach (var command in _selectionCommands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) => ItemsView.Refresh();

    // ----------------------------------------------------------------- tree

    /// <summary>Rebuilds the Categories pane (after categories or queues change), keeping the selection.</summary>
    public void BuildTree()
    {
        var previous = SelectedNode;
        var categories = _categories.GetAll();
        var queues = _queues.GetAll();
        Tree.Clear();

        var all = RootCategoryNode(TreeNodeKind.AllDownloads, "Tree_AllDownloads", "all-downloads", ListScope.All, categories);
        all.IsExpanded = true;
        Tree.Add(all);
        Tree.Add(RootCategoryNode(TreeNodeKind.Unfinished, "Tree_Unfinished", "unfinished", ListScope.Unfinished, categories));
        Tree.Add(RootCategoryNode(TreeNodeKind.Finished, "Tree_Finished", "finished", ListScope.Finished, categories));
        Tree.Add(new TreeNodeViewModel(TreeNodeKind.GrabberProjects, Localizer.Get("Tree_GrabberProjects"), "grabber-project"));

        var queueRoot = new TreeNodeViewModel(TreeNodeKind.Queues, Localizer.Get("Tree_Queues"), "queues") { IsExpanded = true };
        foreach (var queue in queues)
        {
            queueRoot.Children.Add(new TreeNodeViewModel(TreeNodeKind.Queue, QueueTitle(queue), queue.IsSyncQueue ? "sync-queue" : "queue")
            {
                QueueId = queue.Id,
                IsBuiltIn = queue.IsBuiltIn,
                Parent = queueRoot,
            });
        }

        Tree.Add(queueRoot);

        var match = previous is null ? null : Flatten(Tree).FirstOrDefault(n =>
            n.Kind == previous.Kind && n.CategoryId == previous.CategoryId && n.QueueId == previous.QueueId && n.Scope == previous.Scope);
        var selected = match ?? all;
        selected.IsSelected = true;
        SelectedNode = selected;
    }

    private TreeNodeViewModel RootCategoryNode(TreeNodeKind kind, string titleKey, string icon, ListScope scope, IReadOnlyList<Category> categories)
    {
        var root = new TreeNodeViewModel(kind, Localizer.Get(titleKey), icon, scope) { CategoryId = Category.GeneralId, IsBuiltIn = true };
        foreach (var c in categories)
        {
            root.CategoryIds.Add(c.Id);
        }

        AddCategoryChildren(root, Category.GeneralId, scope, categories);
        return root;
    }

    private static void AddCategoryChildren(TreeNodeViewModel parent, long parentId, ListScope scope, IReadOnlyList<Category> categories)
    {
        foreach (var category in categories.Where(c => c.Id != Category.GeneralId && (c.ParentId ?? Category.GeneralId) == parentId))
        {
            var node = new TreeNodeViewModel(TreeNodeKind.Category, CategoryTitle(category), CategoryIcon(category), scope)
            {
                CategoryId = category.Id,
                IsBuiltIn = category.IsBuiltIn,
                Parent = parent,
            };
            AddCategoryChildren(node, category.Id, scope, categories);
            node.CategoryIds.Add(category.Id);
            foreach (var child in node.Children)
            {
                node.CategoryIds.UnionWith(child.CategoryIds);
            }

            parent.Children.Add(node);
        }
    }

    public static string CategoryTitle(Category category) =>
        category.IsBuiltIn ? Localizer.Get("Category_" + category.Name) : category.Name;

    public static string QueueTitle(DownloadQueue queue) => queue.Id switch
    {
        DownloadQueue.MainQueueId when queue.IsBuiltIn => Localizer.Get("Queue_Main"),
        DownloadQueue.SyncQueueId when queue.IsBuiltIn => Localizer.Get("Queue_Sync"),
        _ => queue.Name,
    };

    private static string CategoryIcon(Category category) =>
        category.IsBuiltIn && !string.IsNullOrEmpty(category.Icon) ? category.Icon : "category";

    private static IEnumerable<TreeNodeViewModel> Flatten(IEnumerable<TreeNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    public void BuildQueueMenus()
    {
        StartQueueMenu.Clear();
        StopQueueMenu.Clear();
        AddToQueueMenu.Clear();
        foreach (var queue in _queues.GetAll())
        {
            var title = QueueTitle(queue);
            StartQueueMenu.Add(new MenuEntryViewModel(title, StartQueueCommand, queue.Id));
            StopQueueMenu.Add(new MenuEntryViewModel(title, StopQueueCommand, queue.Id));
            AddToQueueMenu.Add(new MenuEntryViewModel(title, AddToQueueCommand, queue.Id));
        }
    }

    // ----------------------------------------------------------------- toolbar

    private void BuildToolbar()
    {
        DeleteMenu.Add(new MenuEntryViewModel(Localizer.Get("Tb_Delete"), DeleteCommand));
        DeleteMenu.Add(new MenuEntryViewModel(Localizer.Get("Tb_DeleteWithFile"), DeleteWithFileCommand));

        var all = new Dictionary<string, ToolbarItemViewModel>
        {
            ["AddUrl"] = new("AddUrl", Localizer.Get("Tb_AddUrl"), "add-url", AddUrlCommand),
            ["Resume"] = new("Resume", Localizer.Get("Tb_Resume"), "resume", ResumeCommand),
            ["Stop"] = new("Stop", Localizer.Get("Tb_Stop"), "stop", StopCommand),
            ["StopAll"] = new("StopAll", Localizer.Get("Tb_StopAll"), "stop-all", StopAllCommand),
            ["Delete"] = new("Delete", Localizer.Get("Tb_Delete"), "delete", DeleteCommand) { DropDown = DeleteMenu },
            ["DeleteCompleted"] = new("DeleteCompleted", Localizer.Get("Tb_DeleteCompleted"), "delete-completed", DeleteCompletedCommand),
            ["Options"] = new("Options", Localizer.Get("Tb_Options"), "options", OptionsCommand),
            ["Scheduler"] = new("Scheduler", Localizer.Get("Tb_Scheduler"), "scheduler", SchedulerCommand),
            ["StartQueue"] = new("StartQueue", Localizer.Get("Tb_StartQueue"), "start-queue", StartMainQueueCommand) { DropDown = StartQueueMenu },
            ["StopQueue"] = new("StopQueue", Localizer.Get("Tb_StopQueue"), "stop-queue", StopMainQueueCommand) { DropDown = StopQueueMenu },
            ["Grabber"] = new("Grabber", Localizer.Get("Tb_Grabber"), "grabber", GrabberCommand),
            ["TellFriend"] = new("TellFriend", Localizer.Get("Tb_TellFriend"), "tell-friend", TellFriendCommand),
        };

        Toolbar.Clear();
        var saved = _settings.Current.Ui.ToolbarButtons;
        var order = saved.Count > 0 ? saved.Select(b => b.Id).Where(all.ContainsKey).ToList() : [.. DefaultToolbarOrder];
        order.AddRange(DefaultToolbarOrder.Where(id => !order.Contains(id)));
        foreach (var id in order)
        {
            var item = all[id];
            item.IsVisible = saved.Find(b => b.Id == id)?.Visible ?? true;
            Toolbar.Add(item);
        }
    }

    // ----------------------------------------------------------------- sorting

    private void BuildArrangeMenu()
    {
        (string Key, string Label)[] entries =
        [
            (DownloadItemComparer.OrderOfAddition, "Arrange_Order"), ("FileName", "Arrange_FileName"), ("Size", "Arrange_Size"),
            ("StatusRank", "Arrange_Status"), ("TimeLeftSeconds", "Arrange_TimeLeft"), ("Rate", "Arrange_TransferRate"),
            ("LastTry", "Arrange_LastTry"), ("Description", "Arrange_Description"), ("SaveTo", "Arrange_SaveTo"), ("Referrer", "Arrange_Referrer"),
        ];
        foreach (var (key, label) in entries)
        {
            ArrangeMenu.Add(new MenuEntryViewModel(Localizer.Get(label), ArrangeByCommand, key) { IsCheckable = true });
        }

        SortDirectionMenu.Add(new MenuEntryViewModel(Localizer.Get("Sort_Ascending"), SortDirectionCommand, false) { IsCheckable = true });
        SortDirectionMenu.Add(new MenuEntryViewModel(Localizer.Get("Sort_Descending"), SortDirectionCommand, true) { IsCheckable = true });
    }

    private void ApplySort(string? key, bool descending, bool persist)
    {
        key = key is not null && DownloadItemComparer.Keys.Contains(key) ? key : DownloadItemComparer.OrderOfAddition;
        SortKey = key;
        SortDescending = descending;
        ItemsView.CustomSort = new DownloadItemComparer(key, descending);
        foreach (var entry in ArrangeMenu)
        {
            entry.IsChecked = Equals(entry.Parameter, key);
        }

        foreach (var entry in SortDirectionMenu)
        {
            entry.IsChecked = Equals(entry.Parameter, descending);
        }

        if (persist)
        {
            _settings.Update(s =>
            {
                s.Ui.SortColumn = key;
                s.Ui.SortDescending = descending;
            });
        }

        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ArrangeBy(string key) => ApplySort(key, SortDescending, persist: true);

    [RelayCommand]
    private void SortDirection(bool descending) => ApplySort(SortKey, descending, persist: true);

    // ----------------------------------------------------------------- toolbar / download commands

    [RelayCommand]
    private void AddUrl() => _controller.ShowAddUrl();

    private bool CanResume() => _selection.Exists(i => i.Status.IsResumable() && !_engine.IsRunning(i.Id));

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        foreach (var item in _selection.Where(i => i.Status.IsResumable()))
        {
            _controller.StartDownload(item.Id);
        }
    }

    private bool CanStop() => _selection.Exists(i => _engine.IsRunning(i.Id));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await Task.WhenAll(_selection.Where(i => _engine.IsRunning(i.Id)).Select(i => _downloads.StopAsync(i.Id)));
    }

    private bool CanStopAll() => _engine.RunningIds.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStopAll))]
    private Task StopAll() => _controller.StopAllAsync();

    [RelayCommand]
    private Task PauseAll() => _controller.StopAllAsync();

    private bool HasSelection() => _selection.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Delete() => DeleteSelectionAsync(alsoFromDisk: false);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteWithFile() => DeleteSelectionAsync(alsoFromDisk: true);

    private async Task DeleteSelectionAsync(bool alsoFromDisk)
    {
        var ids = _selection.Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var (yes, fromDisk) = _dialogs.ConfirmWithCheck(
            Localizer.Format("Confirm_Delete", ids.Count), Localizer.Get("Confirm_DeleteAlsoFromDisk"), alsoFromDisk);
        if (yes)
        {
            await _downloads.RemoveAsync(ids, fromDisk);
        }
    }

    [RelayCommand]
    private void DeleteCompleted()
    {
        if (Items.Any(i => i.IsCompleted) && _dialogs.Confirm(Localizer.Get("Confirm_DeleteCompleted")))
        {
            _downloads.RemoveCompleted();
        }
    }

    [RelayCommand]
    private void Options() => _controller.ShowOptions();

    [RelayCommand]
    private void Scheduler() => _controller.ShowScheduler();

    [RelayCommand]
    private void StartQueue(long queueId) => _controller.StartQueue(queueId);

    [RelayCommand]
    private void StopQueue(long queueId) => _controller.StopQueue(queueId);

    [RelayCommand]
    private void StartMainQueue() => _controller.StartQueue(DownloadQueue.MainQueueId);

    [RelayCommand]
    private void StopMainQueue() => _controller.StopQueue(DownloadQueue.MainQueueId);

    [RelayCommand]
    private void Grabber() => _controller.ShowGrabber();

    [RelayCommand]
    private void TellFriend() => _dialogs.ShowModal(new TellAFriendDialog());

    private bool CanOpen() => _selection.Count == 1 && _selection[0].IsCompleted;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => OpenItem(_selection[0]);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenWith()
    {
        var path = _selection[0].Model.FullPath;
        if (EnsureExists(path))
        {
            ShellService.OpenWith(path);
        }
    }

    private bool SingleSelection() => _selection.Count == 1;

    [RelayCommand(CanExecute = nameof(SingleSelection))]
    private void OpenFolder()
    {
        var model = _selection[0].Model;
        ShellService.OpenFolder(model.Status == DownloadStatus.Completed ? model.FullPath : model.SavePath);
    }

    private void OpenItem(DownloadItemViewModel item)
    {
        var path = item.Model.FullPath;
        if (EnsureExists(path))
        {
            ShellService.OpenFile(path);
        }
    }

    private bool EnsureExists(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        _dialogs.Error(Localizer.Format("Error_FileMissing", path));
        return false;
    }

    [RelayCommand(CanExecute = nameof(SingleSelection))]
    private void MoveRename() => _controller.ShowMoveRename(_selection[0].Id);

    [RelayCommand(CanExecute = nameof(SingleSelection))]
    private void Properties() => _controller.ShowProperties(_selection[0].Id);

    private bool CanRedownload() => _selection.Count > 0 && _selection.TrueForAll(i => !i.IsActive);

    [RelayCommand(CanExecute = nameof(CanRedownload))]
    private async Task RedownloadAsync()
    {
        foreach (var item in _selection.ToList())
        {
            await _downloads.RedownloadAsync(item.Id);
        }
    }

    private bool CanMoveInQueue() => _selection.Count == 1 && _selection[0].QueueId is not null;

    [RelayCommand(CanExecute = nameof(CanMoveInQueue))]
    private void MoveUp() => _downloads.MoveInQueue(_selection[0].Id, -1);

    [RelayCommand(CanExecute = nameof(CanMoveInQueue))]
    private void MoveDown() => _downloads.MoveInQueue(_selection[0].Id, +1);

    private bool CanRefreshAddress() => _selection.Count == 1 && !_selection[0].IsCompleted;

    [RelayCommand(CanExecute = nameof(CanRefreshAddress))]
    private void RefreshAddress() => _controller.RefreshAddress(_selection[0].Id);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyAddress()
    {
        var text = string.Join(Environment.NewLine, _selection.Select(i => i.Model.OriginalUrl.Length > 0 ? i.Model.OriginalUrl : i.Model.Url));
        try
        {
            ClipboardMonitor.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy (another app holds it); ignore like Explorer does.
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddToQueue(long queueId) => _downloads.SetQueue([.. _selection.Select(i => i.Id)], queueId);

    private bool AnyQueued() => _selection.Exists(i => i.QueueId is not null);

    [RelayCommand(CanExecute = nameof(AnyQueued))]
    private void DeleteFromQueue() => _downloads.SetQueue([.. _selection.Where(i => i.QueueId is not null).Select(i => i.Id)], null);

    /// <summary>Space: pause what runs, resume what doesn't.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TogglePauseAsync()
    {
        var running = _selection.Where(i => _engine.IsRunning(i.Id)).ToList();
        if (running.Count > 0)
        {
            await Task.WhenAll(running.Select(i => _downloads.StopAsync(i.Id)));
            return;
        }

        foreach (var item in _selection.Where(i => i.Status.IsResumable()))
        {
            _controller.StartDownload(item.Id);
        }
    }

    // ----------------------------------------------------------------- Tasks / Help menus

    [RelayCommand]
    private void AddBatch() => _controller.ShowAddBatch(fromClipboard: false);

    [RelayCommand]
    private void AddBatchFromClipboard() => _controller.ShowAddBatch(fromClipboard: true);

    [RelayCommand]
    private void Export(bool ef2) => _controller.ShowExport(ef2, null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ExportSelected(bool ef2) => _controller.ShowExport(ef2, [.. _selection.Select(i => i.Id)]);

    [RelayCommand]
    private void Import(bool ef2) => _controller.ShowImport(ef2);

    [RelayCommand]
    private void GrabberProjects() => _controller.ShowGrabber();

    [RelayCommand]
    private void Exit() => _controller.RequestExit();

    [RelayCommand]
    private void HelpContents() => _controller.ShowHelp();

    [RelayCommand]
    private void Faq() => _controller.ShowHelp("faq");

    [RelayCommand]
    private void CommandLineHelp() => _controller.ShowHelp("command-line");

    [RelayCommand]
    private void CheckUpdates() => _controller.CheckForUpdates();

    [RelayCommand]
    private void About() => _dialogs.ShowModal(new AboutDialog());

    // ----------------------------------------------------------------- View / Downloads menus

    [RelayCommand]
    private void ToggleCategories() => ShowCategories = !ShowCategories;

    [RelayCommand]
    private void ShowColumns() => ColumnsDialogRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CustomizeToolbar()
    {
        var dialog = ReorderDialogs.ForToolbar(Toolbar.Select(t => (t.Id, t.Label, t.SmallIcon, t.IsVisible)).ToList(), DefaultToolbarOrder);
        if (_dialogs.ShowModal(dialog) != true)
        {
            return;
        }

        _settings.Update(s => s.Ui.ToolbarButtons = [.. dialog.Result.Select(r => new ToolbarButtonSetting { Id = r.Id, Visible = r.Visible })]);
        BuildToolbar();
    }

    [RelayCommand]
    private void ToggleDropTarget() => _controller.ToggleDropTarget();

    [RelayCommand]
    private void Find()
    {
        var dialog = new FindDialog(_findText, _findMatchCase);
        if (_dialogs.ShowModal(dialog) == true)
        {
            _findText = dialog.SearchText;
            _findMatchCase = dialog.MatchCase;
            FindNext();
        }
    }

    [RelayCommand]
    private void FindNext()
    {
        if (string.IsNullOrEmpty(_findText))
        {
            Find();
            return;
        }

        var visible = ItemsView.Cast<DownloadItemViewModel>().ToList();
        var start = _selection.Count > 0 ? visible.IndexOf(_selection[0]) + 1 : 0;
        var comparison = _findMatchCase ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        for (var n = 0; n < visible.Count; n++)
        {
            var item = visible[(start + n) % visible.Count];
            if (item.FileName.Contains(_findText, comparison))
            {
                RevealRequested?.Invoke(this, item);
                return;
            }
        }

        _dialogs.Info(Localizer.Format("Find_NotFound", _findText), Localizer.Get("Find_Title"));
    }

    [RelayCommand]
    private void LimiterOn() => _settings.Update(s => s.SpeedLimiter.Enabled = true);

    [RelayCommand]
    private void LimiterOff() => _settings.Update(s => s.SpeedLimiter.Enabled = false);

    [RelayCommand]
    private void ToggleLimiter() => _settings.Update(s => s.SpeedLimiter.Enabled = !s.SpeedLimiter.Enabled);

    [RelayCommand]
    private void LimiterSettings()
    {
        var limiter = _settings.Current.SpeedLimiter;
        var dialog = new SpeedLimiterDialog(limiter.MaxKBps, limiter.ApplyToSchedulerQueuesOnly);
        if (_dialogs.ShowModal(dialog) == true)
        {
            _settings.Update(s =>
            {
                s.SpeedLimiter.MaxKBps = dialog.MaxKBps;
                s.SpeedLimiter.ApplyToSchedulerQueuesOnly = dialog.QueueOnly;
            });
        }
    }

    // ----------------------------------------------------------------- tree context menu commands

    [RelayCommand]
    private void AddCategory(TreeNodeViewModel? parent)
    {
        var dialog = new CategoryDialog(null, _settings.Current, _paths);
        if (_dialogs.ShowModal(dialog) != true)
        {
            return;
        }

        _categories.Insert(new Category
        {
            Name = dialog.CategoryName,
            Extensions = dialog.Extensions,
            DefaultSaveDir = dialog.Folder,
            ParentId = parent?.Kind == TreeNodeKind.Category ? parent.CategoryId : Category.GeneralId,
        });
        BuildTree();
    }

    private static bool IsCategory(TreeNodeViewModel? node) => node?.Kind == TreeNodeKind.Category && node.CategoryId is not null;

    [RelayCommand(CanExecute = nameof(IsCategory))]
    private void EditCategory(TreeNodeViewModel? node)
    {
        if (node?.CategoryId is not { } id || _categories.Get(id) is not { } category)
        {
            return;
        }

        var dialog = new CategoryDialog(category, _settings.Current, _paths);
        if (_dialogs.ShowModal(dialog) != true)
        {
            return;
        }

        category.Name = dialog.CategoryName;
        category.Extensions = dialog.Extensions;
        category.DefaultSaveDir = dialog.Folder;
        _categories.Update(category);
        BuildTree();
    }

    private static bool IsUserCategory(TreeNodeViewModel? node) => IsCategory(node) && !node!.IsBuiltIn;

    [RelayCommand(CanExecute = nameof(IsUserCategory))]
    private void DeleteCategory(TreeNodeViewModel? node)
    {
        if (node?.CategoryId is not { } id || node.IsBuiltIn || !_dialogs.Confirm(Localizer.Format("Confirm_DeleteCategory", node.Title)))
        {
            return;
        }

        _downloads.SetCategory([.. Items.Where(i => i.CategoryId == id).Select(i => i.Id)], Category.GeneralId);
        _categories.Delete(id);
        BuildTree();
    }

    [RelayCommand]
    private void OpenCategoryFolder(TreeNodeViewModel? node)
    {
        var category = _categories.Get(node?.CategoryId ?? Category.GeneralId);
        if (category is null)
        {
            return;
        }

        var folder = SaveLocationResolver.FolderFor(category, _settings.Current, _paths);
        Directory.CreateDirectory(folder);
        ShellService.OpenFolder(folder);
    }

    private static bool IsQueue(TreeNodeViewModel? node) => node?.Kind == TreeNodeKind.Queue && node.QueueId is not null;

    [RelayCommand(CanExecute = nameof(IsQueue))]
    private void QueueStart(TreeNodeViewModel? node) => _controller.StartQueue(node!.QueueId!.Value);

    [RelayCommand(CanExecute = nameof(IsQueue))]
    private void QueueStop(TreeNodeViewModel? node) => _controller.StopQueue(node!.QueueId!.Value);

    [RelayCommand(CanExecute = nameof(IsQueue))]
    private void QueueEdit(TreeNodeViewModel? node) => _controller.ShowScheduler(node!.QueueId);

    [RelayCommand]
    private void CreateQueue() => _controller.CreateQueue();

    private static bool IsUserQueue(TreeNodeViewModel? node) => IsQueue(node) && !node!.IsBuiltIn;

    [RelayCommand(CanExecute = nameof(IsUserQueue))]
    private void DeleteQueue(TreeNodeViewModel? node)
    {
        if (node?.QueueId is not { } id || node.IsBuiltIn || !_dialogs.Confirm(Localizer.Format("Confirm_DeleteQueue", node.Title)))
        {
            return;
        }

        _ = _controller.DeleteQueueAsync(id);
    }
}
