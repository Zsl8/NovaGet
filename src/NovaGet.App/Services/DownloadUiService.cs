using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Engine.Naming;
using NovaGet.Core.Integration;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// The interactive side of downloading: Add URL → duplicate check → Download File Info, progress dialogs for
/// downloads the user started, the Download complete dialog and "Options on completion".
/// </summary>
internal sealed class DownloadUiService(
    IDownloadService downloads,
    IDownloadEngine engine,
    IDownloadProber prober,
    ICategoryRepository categories,
    IQueueRepository queues,
    ISettingsService settings,
    IDialogService dialogs,
    IDialUpService dialUp,
    AppPaths paths,
    Lazy<IAppController> controller,
    ILogger<DownloadUiService> logger) : IDisposable
{
    private readonly Dictionary<long, ProgressWindow> _windows = [];
    private readonly Dictionary<long, CompletionOptions> _completion = [];
    private readonly HashSet<long> _manual = [];
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        downloads.StateChanged += OnStateChanged;
        downloads.Changed += OnListChanged;
    }

    public void Dispose()
    {
        downloads.StateChanged -= OnStateChanged;
        downloads.Changed -= OnListChanged;
    }

    // ----------------------------------------------------------------- adding downloads

    /// <summary>Tasks → Add new download (pre-filled from <paramref name="url"/> or a URL on the clipboard).</summary>
    public async Task ShowAddUrlAsync(string? url)
    {
        var dialog = new AddUrlDialog(settings.Current.Downloads.AddressHistory, url ?? ClipboardUrl(), UserAgent, prober.ProbeAsync);
        if (dialogs.ShowModal(dialog) != true)
        {
            return;
        }

        RememberAddress(dialog.Url);
        await AddAsync(new DownloadRequest { Url = dialog.Url, AuthUser = dialog.UserName, AuthPassword = dialog.Password }, dialog.Probe, interactive: true);
    }

    /// <summary>
    /// Adds a download: handles duplicates, then shows "Download File Info" (unless disabled or not interactive)
    /// and starts it or puts it in a queue. Returns the new (or existing) download id, or null when cancelled.
    /// </summary>
    public async Task<long?> AddAsync(DownloadRequest request, ProbeResult? probe, bool interactive)
    {
        if (probe is not null)
        {
            request = request with
            {
                OriginalUrl = request.OriginalUrl ?? request.Url,
                Url = probe.FinalUri.AbsoluteUri,
                FileName = string.IsNullOrWhiteSpace(request.FileName) ? probe.FileName : request.FileName,
                Size = probe.Size,
                ResumeCapable = probe.ResumeSupported,
                ETag = probe.ETag,
                LastModified = probe.LastModified?.UtcDateTime,
            };
        }

        if (downloads.FindByUrl(request.OriginalUrl ?? request.Url) is { } existing)
        {
            var action = settings.Current.Downloads.DuplicateAction;
            if (action == DuplicateDownloadAction.ShowDialog)
            {
                if (!interactive)
                {
                    action = DuplicateDownloadAction.AddNumbered;
                }
                else
                {
                    var duplicate = new DuplicateDialog(existing.FileName);
                    if (dialogs.ShowModal(duplicate) != true)
                    {
                        return null;
                    }

                    action = duplicate.Action;
                }
            }

            switch (action)
            {
                case DuplicateDownloadAction.ShowCompleteOrResume:
                    if (existing.Status == DownloadStatus.Completed)
                    {
                        ShowComplete(existing);
                    }
                    else
                    {
                        StartDownload(existing.Id);
                    }

                    return existing.Id;
                case DuplicateDownloadAction.AddAndOverwrite:
                    request = request with { OverwriteExisting = true };
                    break;
            }
        }

        if (!interactive || !settings.Current.Downloads.ShowStartDialog)
        {
            var added = downloads.Add(request);
            if (request.QueueId is null)
            {
                StartDownload(added.Id);
            }

            return added.Id;
        }

        return await ShowFileInfoAsync(request);
    }

    private async Task<long?> ShowFileInfoAsync(DownloadRequest request)
    {
        var categoryList = categories.GetAll();
        var matcher = new CategoryMatcher(categoryList);
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Core.Engine.Naming.FileNameResolver.NameFromUrl(new Uri(request.Url)) ?? "download"
            : request.FileName;
        var categoryId = request.CategoryId ?? matcher.Match(fileName).Id;

        // "Start downloading immediately while displaying the dialog": it runs while the user decides.
        Download? early = null;
        if (settings.Current.Downloads.StartImmediatelyWhileShowingInfoDialog)
        {
            early = downloads.Add(request with { FileName = fileName, CategoryId = categoryId });
            StartDownload(early.Id, showProgress: false);
        }

        var dialog = new FileInfoDialog(new FileInfoRequest
        {
            Url = request.OriginalUrl ?? request.Url,
            FileName = fileName,
            Size = request.Size,
            Categories = [.. categoryList.Select(c => new ChoiceItem(c.Id, c.Id == Category.GeneralId ? Localizer.Get("Category_General") : MainViewModel.CategoryTitle(c)))],
            CategoryId = categoryId,
            FolderForCategory = id => FolderFor(id),
            RecentFolders = settings.Current.SaveTo.RecentFolders,
            Description = request.Description,
            InitialFolder = request.SaveFolder,
            Queues = [.. queues.GetAll().Select(q => new ChoiceItem(q.Id, MainViewModel.QueueTitle(q)))],
            ShowQueuePicker = settings.Current.Downloads.ShowQueueSelectionOnDownloadLater,
            AddCategory = AddCategoryInteractive,
            CreateQueue = CreateQueueInteractive,
        });

        if (dialogs.ShowModal(dialog) != true || dialog.Choice == FileInfoChoice.Cancel)
        {
            if (early is not null)
            {
                await downloads.RemoveAsync([early.Id], deleteFiles: true);
            }

            return null;
        }

        RememberFolder(dialog.Folder);
        if (dialog.RememberFolder && categories.Get(dialog.CategoryId) is { } category)
        {
            category.DefaultSaveDir = dialog.Folder;
            categories.Update(category);
        }

        long id;
        if (early is not null)
        {
            id = early.Id;
            if (downloads.Find(id) is { } current)
            {
                current.Description = string.IsNullOrEmpty(dialog.Description) ? null : dialog.Description;
                current.CategoryId = dialog.CategoryId;
                downloads.Save(current);
            }

            await downloads.MoveOrRenameAsync(id, dialog.Folder, dialog.FileName);
            if (dialog.Choice == FileInfoChoice.Later)
            {
                await downloads.StopAsync(id);
                downloads.SetQueue([id], dialog.QueueId);
            }
            else if (settings.Current.Downloads.ShowProgressDialog)
            {
                ShowProgress(id);
            }
        }
        else
        {
            var added = downloads.Add(request with
            {
                FileName = dialog.FileName,
                SaveFolder = dialog.Folder,
                CategoryId = dialog.CategoryId,
                Description = string.IsNullOrEmpty(dialog.Description) ? null : dialog.Description,
                QueueId = dialog.Choice == FileInfoChoice.Later ? dialog.QueueId : null,
            });
            id = added.Id;
            if (dialog.Choice == FileInfoChoice.Start)
            {
                StartDownload(id);
            }
        }

        return id;
    }

    /// <summary><c>NovaGet.exe /d URL [/p folder] [/f name] [/n] [/a]</c>.</summary>
    public Task<long?> AddFromCommandLineAsync(CommandLineOptions options)
    {
        var request = new DownloadRequest
        {
            Url = options.Url!,
            FileName = options.FileName,
            SaveFolder = options.SaveFolder,
            QueueId = options.AddToQueueOnly ? DownloadQueue.MainQueueId : null,
        };
        return AddAsync(request, probe: null, interactive: !options.Silent && !options.AddToQueueOnly);
    }

    /// <summary>
    /// A captured browser download or "Download with NovaGet": the File Info dialog (or a direct start), with the
    /// browser's referrer, cookies and user agent so the server sees the same client.
    /// </summary>
    public Task<long?> AddFromBrowserAsync(BrowserDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        var request = new DownloadRequest
        {
            Url = (download.FinalUrl ?? download.Url).AbsoluteUri,
            OriginalUrl = download.Url.AbsoluteUri,
            Referrer = download.Request.Referrer,
            Cookies = download.Request.Cookies,
            UserAgent = download.Request.UserAgent,
            FileName = string.IsNullOrWhiteSpace(download.FileName) ? null : FileNameSanitizer.Sanitize(download.FileName),
            Size = download.FileSize,
        };
        return AddAsync(request, probe: null, interactive: true);
    }

    /// <summary>
    /// The "Download all links" selection dialog (also for several dropped or pasted links). "Download" appends the
    /// chosen links to the main queue and starts it, so they run a few at a time; "Download Later" only queues them.
    /// Cookies go only to links on the page's own site.
    /// </summary>
    public void ShowLinks(string title, IReadOnlyList<(Uri Url, string? Description)> links, BrowserRequestInfo? request = null, Uri? pageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (links.Count == 0)
        {
            dialogs.Info(Localizer.Get("Links_NoneFound"));
            return;
        }

        var dialog = new LinksDialog(new LinksRequest
        {
            Title = title,
            Links = links,
            PreferredExtensions = CategoryMatcher.SplitPatterns(settings.Current.FileTypes.AutoCaptureExtensions),
            Categories = [.. categories.GetAll().Select(c => new ChoiceItem(c.Id, c.Id == Category.GeneralId ? Localizer.Get("Category_General") : MainViewModel.CategoryTitle(c)))],
            FolderForCategory = FolderFor,
            Queues = [.. queues.GetAll().Select(q => new ChoiceItem(q.Id, MainViewModel.QueueTitle(q)))],
        });
        if (dialogs.ShowModal(dialog) != true)
        {
            return;
        }

        var vm = dialog.ViewModel;
        var queueId = dialog.QueueId ?? DownloadQueue.MainQueueId;
        foreach (var link in vm.SelectedLinks)
        {
            var sameSite = pageUrl is not null && string.Equals(link.Url.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase);
            downloads.Add(new DownloadRequest
            {
                Url = link.Address,
                Referrer = request?.Referrer ?? pageUrl?.AbsoluteUri,
                Cookies = sameSite ? request?.Cookies : null,
                UserAgent = request?.UserAgent,
                Description = string.IsNullOrWhiteSpace(link.Description) ? null : link.Description,
                CategoryId = vm.IsAutomaticCategory ? null : vm.CategoryId,
                SaveFolder = vm.IsAutomaticCategory ? null : Environment.ExpandEnvironmentVariables(vm.SaveFolder.Trim()),
                QueueId = queueId,
            });
        }

        if (dialog.QueueId is null)
        {
            controller.Value.StartQueue(DownloadQueue.MainQueueId);
        }
    }

    /// <summary>Links dropped on the window or the drop target: one goes to Add URL, several to the selection dialog.</summary>
    public async Task AddDroppedAsync(IReadOnlyList<Uri> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count == 1)
        {
            await ShowAddUrlAsync(urls[0].AbsoluteUri);
        }
        else if (urls.Count > 1)
        {
            ShowLinks(Localizer.Get("Links_DroppedTitle"), [.. urls.Select(u => (u, (string?)null))]);
        }
    }

    // ----------------------------------------------------------------- starting, progress, completion

    /// <summary>Starts a download on the user's behalf: it gets a progress dialog (if enabled) and a complete dialog.</summary>
    public void StartDownload(long id, bool showProgress = true)
    {
        if (!downloads.Start(id) && !engine.IsRunning(id))
        {
            return;
        }

        _manual.Add(id);
        if (showProgress && settings.Current.Downloads.ShowProgressDialog && !settings.Current.Downloads.StartProgressDialogMinimized)
        {
            ShowProgress(id);
        }
    }

    public void ShowProgress(long id)
    {
        if (_windows.TryGetValue(id, out var existing))
        {
            existing.Show();
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        if (downloads.Find(id) is not { } download || download.Status == DownloadStatus.Completed)
        {
            return;
        }

        var window = new ProgressWindow(id, downloads, engine, settings, dialogs, controller.Value, CompletionFor(id));
        window.Closed += (_, _) => _windows.Remove(id);
        _windows[id] = window;
        window.Show();
    }

    public CompletionOptions CompletionFor(long id)
    {
        if (!_completion.TryGetValue(id, out var options))
        {
            options = new CompletionOptions { ShowCompleteDialog = settings.Current.Downloads.ShowCompleteDialog };
            _completion[id] = options;
        }

        return options;
    }

    public void ShowComplete(Download download)
    {
        var dialog = new CompleteDialog(download, settings) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        dialog.Show();
        dialog.Activate();
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnStateChanged(sender, e));
            return;
        }

        _windows.TryGetValue(e.Id, out var window);
        switch (e.Status)
        {
            case DownloadStatus.Completed:
                window?.Close();
                _completion.Remove(e.Id, out var options);
                var manual = _manual.Remove(e.Id);
                if (downloads.Find(e.Id) is { } done && manual
                    && settings.Current.Downloads.ShowCompleteDialog && (options?.ShowCompleteDialog ?? true))
                {
                    ShowComplete(done);
                }

                if (options is not null)
                {
                    RunCompletionActions(options);
                }

                break;

            case DownloadStatus.Error when e.ErrorKind == DownloadErrorKind.ServerFileChanged && (_manual.Contains(e.Id) || window is not null):
                window?.Refresh();
                if (dialogs.Confirm(Localizer.Get("Confirm_RestartChanged")))
                {
                    _ = RestartAsync(e.Id);
                }

                break;

            default:
                window?.Refresh();
                break;
        }
    }

    private async Task RestartAsync(long id)
    {
        try
        {
            _manual.Add(id);
            await downloads.RedownloadAsync(id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Restart of download {Id} failed", id);
        }
    }

    private void OnListChanged(object? sender, DownloadListChangedEventArgs e)
    {
        if (e.Change != DownloadListChange.Removed)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var id in e.Ids)
            {
                if (_windows.TryGetValue(id, out var window))
                {
                    window.Close();
                }

                _manual.Remove(id);
                _completion.Remove(id);
            }
        });
    }

    /// <summary>"Options on completion": hang up, then shut down / sleep / … (after a countdown) or exit.</summary>
    internal void RunCompletionActions(CompletionOptions options)
    {
        if (options.HangUp)
        {
            dialUp.HangUp();
        }

        if (options.TurnOff)
        {
            if (dialogs.ShowModal(new PowerCountdownDialog(options.PowerAction)) == true)
            {
                logger.LogInformation("Running power action {Action} after download completion", options.PowerAction);
                PowerService.Execute(options.PowerAction, options.ForceProcesses);
            }
        }
        else if (options.ExitWhenDone)
        {
            controller.Value.RequestExit();
        }
    }

    // ----------------------------------------------------------------- helpers

    private string UserAgent => EngineOptions.FromSettings(settings.Current, paths).UserAgent;

    private string FolderFor(long categoryId) =>
        SaveLocationResolver.FolderFor(categories.Get(categoryId) ?? new Category { Id = Category.GeneralId, Name = "General" }, settings.Current, paths);

    private ChoiceItem? AddCategoryInteractive()
    {
        var dialog = new CategoryDialog(null, settings.Current, paths);
        if (dialogs.ShowModal(dialog) != true)
        {
            return null;
        }

        var category = new Category { Name = dialog.CategoryName, Extensions = dialog.Extensions, DefaultSaveDir = dialog.Folder, ParentId = Category.GeneralId };
        categories.Insert(category);
        RefreshMainWindowTree();
        return new ChoiceItem(category.Id, category.Name);
    }

    private ChoiceItem? CreateQueueInteractive() =>
        controller.Value.CreateQueue() is { } queue ? new ChoiceItem(queue.Id, queue.Name) : null;

    private static void RefreshMainWindowTree()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.BuildTree();
            vm.BuildQueueMenus();
        }
    }

    private void RememberAddress(string url) => settings.Update(s =>
    {
        s.Downloads.AddressHistory.Remove(url);
        s.Downloads.AddressHistory.Insert(0, url);
    });

    private void RememberFolder(string folder) => settings.Update(s =>
    {
        s.SaveTo.RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        s.SaveTo.RecentFolders.Insert(0, folder);
    });

    private static string? ClipboardUrl()
    {
        try
        {
            return Clipboard.ContainsText() ? CommandLineParser.NormalizeUrlArgument(Clipboard.GetText().Trim()) : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
