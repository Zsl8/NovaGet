using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Grabber;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Engine;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Grabber;

public sealed class GrabberSessionTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private EngineHarness _h = null!;
    private DownloadService _downloads = null!;
    private GrabberRepository _repository = null!;
    private CategoryRepository _categories = null!;
    private TestSite _site = null!;

    public async Task InitializeAsync()
    {
        _h = await EngineHarness.CreateAsync();
        var paths = AppPaths.ForRoot(_temp.Combine("profile"));
        _categories = new CategoryRepository(_h.Database);
        _downloads = new DownloadService(_h.Repository, _categories, _h.Engine, new SettingsService(paths), paths);
        _repository = new GrabberRepository(_h.Database);
        _site = new TestSite(_h.Server);
    }

    public async Task DisposeAsync()
    {
        await _h.DisposeAsync();
        _temp.Dispose();
    }

    private (GrabberProject Project, GrabberSettings Settings) NewProject(Func<GrabberSettings, GrabberSettings> configure)
    {
        var settings = configure(_site.Settings(GrabberTemplate.Images, _temp.Combine("grabbed")));
        var project = new GrabberProject { Name = "Test site", SettingsJson = settings.ToJson() };
        _repository.InsertProject(project);
        return (project, settings);
    }

    private GrabberSession Session(GrabberProject project, GrabberSettings settings) =>
        new(project, settings, _repository, TestSite.Fetcher(_h), new DownloadProber([_h.Http]), _downloads, _categories);

    private async Task DownloadAllAsync(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            var done = _h.WaitForEndAsync(id);
            Assert.True(_downloads.Start(id));
            Assert.Equal(DownloadStatus.Completed, (await done).Status);
        }
    }

    [Fact]
    public async Task Files_go_to_category_folders_and_are_remembered()
    {
        var (project, settings) = NewProject(s => s with { FileTypes = "png pdf", Depth = 1 });
        using var tracker = new GrabberDownloadTracker(_downloads, _repository);
        var session = Session(project, settings);

        await session.RunAsync(CancellationToken.None);

        var items = session.Items;
        Assert.Equal(6, items.Count); // 1, 1-2x, bg, 2, b/3 and the PDF
        Assert.All(items, i => Assert.Equal(GrabberItemState.New, i.State));
        Assert.All(items, i => Assert.True(i.CheckedByDefault));
        Assert.NotNull(_repository.GetProject(project.Id)!.LastRunAt);

        var ids = session.Download(items, DownloadQueue.MainQueueId);
        Assert.Equal(6, ids.Count);
        var pdf = _downloads.GetAll().Single(d => d.Url.EndsWith("doc.pdf", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(settings.SaveFolder, "Documents"), pdf.SavePath);
        Assert.Equal(_site.Start.AbsoluteUri, pdf.Referrer);
        Assert.Equal(DownloadQueue.MainQueueId, pdf.QueueId);
        Assert.Equal(settings.SaveFolder, _downloads.GetAll().Single(d => d.Url.EndsWith("/img/1.png", StringComparison.Ordinal)).SavePath);
        Assert.All(items, i => Assert.Equal(GrabberItemState.Queued, i.State));
        Assert.Empty(session.Download(items, DownloadQueue.MainQueueId)); // already queued
        Assert.Equal(ids.Order(), _repository.GetDownloadIds(project.Id).Order());

        await DownloadAllAsync(ids);
        var result = _repository.FindResult(project.Id, _site.Server.UrlFor("img/1.png").AbsoluteUri)!;
        Assert.Equal(GrabberResultStatus.Downloaded, result.Status);
        Assert.Equal(_site.Images["img/1.png"].ETag, result.ETag);
        Assert.True(File.Exists(result.LocalPath));
    }

    [Fact]
    public async Task A_second_run_offers_only_new_or_changed_files()
    {
        var (project, settings) = NewProject(s => s with { Depth = 0 });
        using var tracker = new GrabberDownloadTracker(_downloads, _repository);
        var first = Session(project, settings);
        await first.RunAsync(CancellationToken.None);
        await DownloadAllAsync(first.Download(first.Items, DownloadQueue.MainQueueId));

        _site.Images["img/1.png"].ChangeContent(99);
        _h.Server.AddText("index.html", $"""<html><body><img src="/img/1.png"><img src="/img/1-2x.png"><img src="/img/new.png"></body></html>""", "text/html");
        _h.Server.AddFile("img/new.png", 500, 77).ContentType = "image/png";
        var second = Session(project, settings);
        await second.RunAsync(CancellationToken.None);

        var states = second.Items.ToDictionary(i => i.Url.AbsolutePath, i => i.State);
        Assert.Equal(GrabberItemState.Changed, states["/img/1.png"]);
        Assert.Equal(GrabberItemState.Unchanged, states["/img/1-2x.png"]);
        Assert.Equal(GrabberItemState.New, states["/img/new.png"]);
        Assert.Equal(["/img/1.png", "/img/new.png"], second.Items.Where(i => i.CheckedByDefault).Select(i => i.Url.AbsolutePath).Order());

        // The changed file replaces the earlier copy.
        var ids = second.Download(second.Items.Where(i => i.CheckedByDefault), DownloadQueue.MainQueueId);
        Assert.True(_downloads.Find(ids[0])!.OverwriteExisting || _downloads.Find(ids[1])!.OverwriteExisting);
    }

    [Fact]
    public async Task Offline_files_are_saved_where_the_pages_expect_them()
    {
        var (project, settings) = NewProject(s => GrabberSettings.ForTemplate(GrabberTemplate.OfflineSite, s) with { Depth = 0 });
        var session = Session(project, settings);
        await session.RunAsync(CancellationToken.None);

        var image = session.Items.Single(i => i.Url.AbsolutePath == "/img/1.png");
        var id = Assert.Single(session.Download([image], DownloadQueue.MainQueueId));
        var download = _downloads.Find(id)!;

        Assert.Equal(OfflinePaths.LocalPath(settings.SaveFolder, image.Url, isPage: false), download.FullPath);
        Assert.True(download.OverwriteExisting);
        var index = File.ReadAllText(Path.Combine(settings.SaveFolder, $"127.0.0.1_{_h.Server.BaseUri.Port}", "index.html"));
        Assert.Contains("src=\"img/1.png\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logins_and_cookies_go_only_to_the_start_site()
    {
        var (project, settings) = NewProject(s => s with
        {
            Depth = 1,
            FollowExternalLinks = true,
            UseAuthorization = true,
            UserName = "me",
            Password = "pw",
            Cookies = "sid=1",
        });
        var session = Session(project, settings);
        await session.RunAsync(CancellationToken.None);

        session.Download(session.Items, DownloadQueue.MainQueueId);

        var all = _downloads.GetAll();
        var onSite = all.Single(d => d.Url.EndsWith("/img/1.png", StringComparison.Ordinal));
        var external = all.Single(d => d.Url.Contains("localhost", StringComparison.Ordinal));
        Assert.Equal(("me", "pw", "sid=1"), (onSite.AuthUser, onSite.AuthPassword, onSite.Cookies));
        Assert.Equal((null, null, null), (external.AuthUser, external.AuthPassword, external.Cookies));
    }

    [Fact]
    public void Projects_and_results_are_stored()
    {
        var project = new GrabberProject { Name = "P", SettingsJson = "{}" };
        _repository.InsertProject(project);
        _repository.SaveResult(new GrabberResult { ProjectId = project.Id, Url = "https://e.com/a.png", Type = "png", Status = GrabberResultStatus.Found });
        var id = _repository.SaveResult(new GrabberResult { ProjectId = project.Id, Url = "https://e.com/a.png", Type = "png", Status = GrabberResultStatus.Queued, DownloadId = 42 });

        var result = Assert.Single(_repository.GetResults(project.Id));
        Assert.Equal((id, GrabberResultStatus.Queued, 42L), (result.Id, result.Status, result.DownloadId!.Value));
        _repository.MarkDownloaded(42, 1234, "\"e\"", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), @"C:\x\a.png");
        result = _repository.FindResult(project.Id, "https://e.com/a.png")!;
        Assert.Equal((GrabberResultStatus.Downloaded, 1234L, "\"e\""), (result.Status, result.Size, result.ETag));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), result.LastModified);

        project.Name = "Renamed";
        _repository.UpdateProject(project);
        Assert.Equal("Renamed", Assert.Single(_repository.GetProjects()).Name);
        _repository.DeleteProject(project.Id);
        Assert.Empty(_repository.GetProjects());
        Assert.Empty(_repository.GetResults(project.Id));
    }
}
