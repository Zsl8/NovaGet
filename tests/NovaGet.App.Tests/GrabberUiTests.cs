using System.Text;
using System.Text.Json;
using System.Windows;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Grabber;
using NovaGet.Core.Ipc;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class GrabberUiTests(WpfFixture wpf)
{
    [Fact]
    public void Wizard_templates_fill_the_later_steps()
    {
        var vm = new GrabberWizardViewModel();

        Assert.Equal(GrabberTemplate.Images, vm.Template);
        Assert.True(vm.FileTypes.Single(f => f.Key == "Images").IsChecked);
        vm.StartUrl = "https://www.example.com/gallery/";
        Assert.Equal("example.com", vm.ProjectName);
        Assert.EndsWith(Path.Combine("Downloads", "example.com"), vm.SaveFolder, StringComparison.Ordinal);

        vm.TemplateId = (long)GrabberTemplate.OfflineSite;
        Assert.True(vm.ConvertLinks);
        Assert.True(vm.FileTypes.Single(f => f.Key == "All").IsChecked);
        Assert.False(vm.FileTypes.Single(f => f.Key == "Images").IsChecked);
        Assert.Equal("https://www.example.com/gallery/", vm.StartUrl); // the start page stays

        vm.TemplateId = (long)GrabberTemplate.Video;
        vm.CustomExtensions = "m3u8";
        var settings = vm.Build();
        Assert.Contains("mp4", settings.FileTypes, StringComparison.Ordinal);
        Assert.EndsWith("m3u8", settings.FileTypes, StringComparison.Ordinal);
        Assert.False(settings.ConvertLinks);
        Assert.Null(vm.ValidateAll());
    }

    [Fact]
    public void Wizard_steps_are_validated()
    {
        var vm = new GrabberWizardViewModel { StartUrl = "ftp://example.com/" };
        Assert.NotNull(vm.Validate(0));
        vm.StartUrl = "https://example.com/";
        Assert.Null(vm.Validate(0));

        vm.SaveFolder = "relative";
        Assert.Equal(1, vm.ValidateAll()!.Value.Step);
        vm.SaveFolder = Path.GetTempPath();
        vm.MaxParallel = 0;
        Assert.Equal(2, vm.ValidateAll()!.Value.Step);
        vm.MaxParallel = 4;
        foreach (var type in vm.FileTypes)
        {
            type.IsChecked = false;
        }

        Assert.Equal(3, vm.ValidateAll()!.Value.Step);
        vm.CustomExtensions = "zip";
        vm.MinSizeKB = 100;
        vm.MaxSizeKB = 10;
        Assert.Equal(3, vm.ValidateAll()!.Value.Step);
        vm.MaxSizeKB = 0;
        Assert.Null(vm.ValidateAll());
    }

    [Fact]
    public void Wizard_loads_a_saved_project()
    {
        var saved = GrabberSettings.ForTemplate(GrabberTemplate.FileTypes) with
        {
            StartUrl = "https://example.com/",
            SaveFolder = @"C:\Grabbed",
            FileTypes = "pdf doc docx ppt pptx xls xlsx txt rtf odt ods epub iso",
            UseAuthorization = true,
            UserName = "me",
            Password = "pw",
            ExcludeFilters = ["*logout*", "*/print/*"],
        };

        var vm = new GrabberWizardViewModel(saved, "Docs");

        Assert.Equal("Docs", vm.ProjectName);
        Assert.Equal(@"C:\Grabbed", vm.SaveFolder);
        Assert.True(vm.FileTypes.Single(f => f.Key == "Documents").IsChecked);
        Assert.Equal("iso", vm.CustomExtensions);
        Assert.Equal("pw", vm.Password);
        Assert.Equal(["*logout*", "*/print/*"], vm.Build().ExcludeFilters);
        vm.StartUrl = "https://other.example.org/";
        Assert.Equal("Docs", vm.ProjectName); // an existing project keeps its name
    }

    [Fact]
    public void Wizard_and_results_windows_load()
    {
        using var app = new AppHost();
        var project = new GrabberProject { Name = "Example" };
        app.Get<IGrabberRepository>().InsertProject(project);
        var settings = GrabberSettings.ForTemplate(GrabberTemplate.Images) with { StartUrl = "https://example.com/", SaveFolder = Path.GetTempPath(), Depth = 0 };
        var session = new GrabberSession(project, settings, app.Get<IGrabberRepository>(), new FakeSite(), app.Get<IDownloadProber>(),
            app.Get<IDownloadService>(), app.Get<ICategoryRepository>());

        wpf.Run(() =>
        {
            var wizard = new GrabberWizardDialog(new GrabberWizardViewModel(), _ => null) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000 };
            wizard.Show();
            wizard.UpdateLayout();
            Assert.Contains("1", wizard.ViewModel.StepTitle, StringComparison.Ordinal);
            wizard.Close();

            var results = new GrabberResultsViewModel(session, project.Name, (items, queue) => session.Download(items, queue ?? DownloadQueue.MainQueueId));
            var window = new GrabberResultsWindow(results, app.Get<IDownloadService>(), () => DownloadQueue.MainQueueId)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
            };
            window.Show();
            window.UpdateLayout();
            window.Close();
        });
    }

    [Fact]
    public async Task Results_fill_live_and_selected_files_are_queued()
    {
        using var app = new AppHost();
        var repository = app.Get<IGrabberRepository>();
        var project = new GrabberProject { Name = "Example" };
        repository.InsertProject(project);
        var settings = GrabberSettings.ForTemplate(GrabberTemplate.Images) with { StartUrl = "https://example.com/", SaveFolder = Path.GetTempPath(), Depth = 0 };
        var session = new GrabberSession(project, settings, repository, new FakeSite(), app.Get<IDownloadProber>(), app.Get<IDownloadService>(), app.Get<ICategoryRepository>());
        var vm = new GrabberResultsViewModel(session, project.Name, (items, queue) => session.Download(items, queue ?? DownloadQueue.MainQueueId));

        await vm.RunAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.All(vm.Items, i => Assert.True(i.IsChecked));
        Assert.Contains("2", vm.Counters, StringComparison.Ordinal);
        Assert.False(vm.IsExploring);
        Assert.Equal(2, vm.AddSelected(DownloadQueue.MainQueueId));
        Assert.All(vm.Items, i => Assert.Equal(GrabberItemState.Queued, i.Item.State));
        Assert.Equal(2, repository.GetDownloadIds(project.Id).Count);
        var queued = app.Get<IDownloadService>().GetAll();
        Assert.All(queued, d => Assert.Equal(DownloadQueue.MainQueueId, d.QueueId));
    }

    [Fact]
    public void Projects_show_their_downloads_in_the_tree()
    {
        var node = new TreeNodeViewModel(TreeNodeKind.GrabberProject, "P", "grabber-project") { GrabberProjectId = 1 };
        node.DownloadIds.Add(5);

        Assert.True(node.Matches(new DownloadItemViewModel(new Download { Id = 5 })));
        Assert.False(node.Matches(new DownloadItemViewModel(new Download { Id = 6 })));
    }

    [Fact]
    public async Task Browser_logins_are_accepted_only_when_asked_for()
    {
        using var app = new AppHost();
        var logins = app.Get<BrowserLoginService>();
        var service = app.Get<BrowserIntegrationService>();
        static IpcRequest Native(string json) => new() { Type = IpcRequestTypes.Native, Payload = JsonDocument.Parse(json).RootElement.Clone(), Args = ["chrome"] };

        Assert.False(service.Handle(Native("""{"type":"cookies","url":"https://shop.example.com/","cookies":"sid=1"}""")).Ok);

        using var cancel = new CancellationTokenSource();
        var waiting = logins.WaitForLoginAsync(new Uri("https://www.shop.example.com/"), cancel.Token);
        var hello = service.Handle(Native("""{"type":"hello"}"""));
        Assert.Equal(["shop.example.com"], hello.Payload!.Value.GetProperty("loginRequests").EnumerateArray().Select(e => e.GetString()));

        Assert.True(service.Handle(Native("""{"type":"cookies","url":"https://account.shop.example.com/me","cookies":"sid=1"}""")).Ok);
        Assert.Equal("sid=1", await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(logins.PendingHosts);

        var abandoned = logins.WaitForLoginAsync(new Uri("https://other.example/"), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Empty(logins.PendingHosts);
    }

    /// <summary>One page with two images.</summary>
    private sealed class FakeSite : IPageFetcher
    {
        public Task<FetchedResource> FetchAsync(Uri url, Uri? referrer, bool text, CancellationToken cancellationToken) =>
            Task.FromResult(url.AbsolutePath switch
            {
                "/" => new FetchedResource
                {
                    Url = url,
                    ContentType = "text/html",
                    Body = Encoding.UTF8.GetBytes("""<img src="/a.png"><img src="/b.jpg"><a href="/doc.pdf">doc</a>"""),
                },
                "/robots.txt" => throw new DownloadException(DownloadErrorKind.LinkExpired, "404", 404),
                _ => new FetchedResource { Url = url, ContentType = "image/png", Size = 1000 },
            });
    }
}
