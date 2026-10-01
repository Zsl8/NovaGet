using System.Windows;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Grabber;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Security;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// The Site Grabber's windows: the wizard for new and edited projects, a results window per running project, and the
/// project commands of the categories tree. Projects are stored with their password and cookies DPAPI-protected.
/// </summary>
internal sealed class GrabberUiService(
    IGrabberRepository repository,
    IDownloadService downloads,
    IDownloadProber prober,
    ICategoryRepository categories,
    IQueueRepository queues,
    IEnumerable<ITransferProtocol> protocols,
    ISettingsService settings,
    ISecretProtector protector,
    IDialogService dialogs,
    BrowserLoginService logins,
    AppPaths paths,
    Lazy<IAppController> controller,
    ILoggerFactory loggers)
{
    private readonly Dictionary<long, GrabberResultsWindow> _windows = [];

    /// <summary>Tasks → Run site grabber.</summary>
    public void ShowNew()
    {
        var wizard = Wizard(new GrabberWizardViewModel());
        if (dialogs.ShowModal(wizard) != true)
        {
            return;
        }

        var project = new GrabberProject { Name = wizard.ViewModel.ProjectName.Trim() };
        Save(project, wizard.ViewModel.Build());
        repository.InsertProject(project);
        RefreshTree();
        Run(project.Id);
    }

    /// <summary>Runs a project again ("only new or changed files"); an open results window is brought to the front.</summary>
    public void Run(long projectId)
    {
        if (_windows.TryGetValue(projectId, out var open))
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            if (open.ViewModel.IsExploring)
            {
                return;
            }

            open.Close();
        }

        if (repository.GetProject(projectId) is not { } project)
        {
            return;
        }

        var projectSettings = Load(project);
        var session = new GrabberSession(project, projectSettings, repository, Fetcher(projectSettings), prober, downloads, categories,
            loggers.CreateLogger("NovaGet.Grabber"));
        var viewModel = new GrabberResultsViewModel(session, project.Name, (items, queueId) => AddToDownloads(session, items, queueId));
        var window = new GrabberResultsWindow(viewModel, downloads, PickQueue);
        if (Application.Current?.MainWindow is { IsVisible: true } main)
        {
            window.Owner = main;
        }

        window.Closed += (_, _) =>
        {
            _windows.Remove(projectId);
            RefreshTree();
        };
        _windows[projectId] = window;
        window.Show();
        _ = viewModel.RunAsync();
    }

    /// <summary>Edit project… (then run it).</summary>
    public void Edit(long projectId)
    {
        if (repository.GetProject(projectId) is not { } project)
        {
            return;
        }

        var wizard = Wizard(new GrabberWizardViewModel(Load(project), project.Name));
        if (dialogs.ShowModal(wizard) != true)
        {
            return;
        }

        project.Name = wizard.ViewModel.ProjectName.Trim();
        Save(project, wizard.ViewModel.Build());
        repository.UpdateProject(project);
        RefreshTree();
        Run(projectId);
    }

    public void Delete(long projectId)
    {
        if (repository.GetProject(projectId) is not { } project || !dialogs.Confirm(Localizer.Format("Grabber_ConfirmDelete", project.Name)))
        {
            return;
        }

        if (_windows.TryGetValue(projectId, out var window))
        {
            window.Close();
        }

        repository.DeleteProject(projectId);
        RefreshTree();
    }

    /// <summary>Download selected (null: the main queue, started now) or Add selected to queue.</summary>
    private IReadOnlyList<long> AddToDownloads(GrabberSession session, IReadOnlyList<GrabberItem> items, long? queueId)
    {
        var ids = session.Download(items, queueId ?? DownloadQueue.MainQueueId);
        if (queueId is null && ids.Count > 0)
        {
            controller.Value.StartQueue(DownloadQueue.MainQueueId);
        }

        RefreshTree();
        return ids;
    }

    private long? PickQueue()
    {
        var dialog = new QueuePickDialog([.. queues.GetAll().Select(q => new ChoiceItem(q.Id, MainViewModel.QueueTitle(q)))]);
        return dialogs.ShowModal(dialog) == true ? dialog.SelectedQueueId : null;
    }

    private GrabberWizardDialog Wizard(GrabberWizardViewModel viewModel) =>
        new(viewModel, folder => dialogs.PickFolder(folder), logins.WaitForLoginAsync, url => ShellService.OpenUrl(url.AbsoluteUri));

    /// <summary>The crawler's requests: the app's user agent and timeout; the project's login and cookies only for its own site.</summary>
    private HttpPageFetcher Fetcher(GrabberSettings project)
    {
        var options = EngineOptions.FromSettings(settings.Current, paths);
        var start = new Uri(project.StartUrl);
        var site = start.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? start.Host[4..] : start.Host;
        return new HttpPageFetcher(protocols, (url, referrer) =>
        {
            var onSite = url.Host.Equals(site, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);
            return new RequestContext
            {
                Url = url,
                Referrer = referrer?.AbsoluteUri,
                UserAgent = options.UserAgent,
                Timeout = options.Timeout,
                Cookies = onSite ? project.Cookies : null,
                UserName = onSite && project.UseAuthorization ? project.UserName : null,
                Password = onSite && project.UseAuthorization ? project.Password : null,
            };
        });
    }

    private GrabberSettings Load(GrabberProject project) => GrabberSettings.FromJson(project.SettingsJson).Unprotect(protector.Unprotect);

    private void Save(GrabberProject project, GrabberSettings grabberSettings) =>
        project.SettingsJson = grabberSettings.Protect(s => string.IsNullOrEmpty(s) ? null : protector.Protect(s)).ToJson();

    private static void RefreshTree()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
        {
            main.BuildTree();
        }
    }
}
