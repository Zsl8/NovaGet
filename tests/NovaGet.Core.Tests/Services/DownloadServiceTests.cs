using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Data;
using NovaGet.Core.Tests.Engine;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Services;

public sealed class DownloadServiceTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private EngineHarness _h = null!;
    private DownloadService _service = null!;
    private SettingsService _settings = null!;

    public async Task InitializeAsync()
    {
        _h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 4 });
        var paths = AppPaths.ForRoot(_temp.Path);
        _settings = new SettingsService(paths);
        _service = new DownloadService(_h.Repository, new CategoryRepository(_h.Database), _h.Engine, _settings, paths);
    }

    public async Task DisposeAsync()
    {
        await _h.DisposeAsync();
        _temp.Dispose();
    }

    private Download AddUrl(string path, long? queueId = null) => _service.Add(new DownloadRequest
    {
        Url = _h.Server.UrlFor(path).AbsoluteUri,
        FileName = Path.GetFileName(path),
        SaveFolder = _h.SaveDirectory,
        QueueId = queueId,
    });

    [Fact]
    public void Add_picks_category_and_folder_and_raises_added()
    {
        var events = new List<DownloadListChangedEventArgs>();
        _service.Changed += (_, e) => events.Add(e);

        var added = _service.Add(new DownloadRequest { Url = "https://example.com/files/movie.mkv", FileName = "movie.mkv" });

        Assert.Equal(6, added.CategoryId); // Video
        Assert.EndsWith("Video", added.SavePath, StringComparison.Ordinal);
        Assert.Equal(DownloadStatus.Paused, added.Status);
        Assert.Equal(added.Url, added.OriginalUrl);
        var e = Assert.Single(events);
        Assert.Equal(DownloadListChange.Added, e.Change);
        Assert.Equal([added.Id], e.Ids);
        Assert.NotNull(_h.Repository.Get(added.Id));
    }

    [Fact]
    public void Returned_downloads_are_copies()
    {
        var added = AddUrl("a.zip");

        var copy = _service.Find(added.Id)!;
        copy.FileName = "changed";

        Assert.Equal("a.zip", _service.Find(added.Id)!.FileName);
    }

    [Fact]
    public async Task Start_runs_the_engine_and_the_list_follows_its_state()
    {
        var file = _h.Server.AddFile("svc.bin", 1024 * 1024);
        var added = AddUrl("svc.bin");
        var completed = new TaskCompletionSource<Download>();
        _service.StateChanged += (_, e) =>
        {
            if (e.Status == DownloadStatus.Completed)
            {
                completed.TrySetResult(_service.Find(e.Id)!);
            }
        };

        Assert.True(_service.Start(added.Id));
        var done = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Completed, done.Status);
        Assert.Equal(file.Size, done.Downloaded);
        Assert.False(_service.Start(added.Id));
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(done.FullPath));
    }

    [Fact]
    public void Queue_membership_and_ordering()
    {
        var a = AddUrl("a.zip", DownloadQueue.MainQueueId);
        var b = AddUrl("b.zip", DownloadQueue.MainQueueId);
        var c = AddUrl("c.zip");
        Assert.Equal(DownloadStatus.Queued, a.Status);
        Assert.Equal([1, 2], new[] { a, b }.Select(d => _service.Find(d.Id)!.QueuePosition));

        _service.SetQueue([c.Id], DownloadQueue.MainQueueId);
        Assert.Equal(3, _service.Find(c.Id)!.QueuePosition);

        _service.MoveInQueue(c.Id, -1);
        Assert.Equal(["a.zip", "c.zip", "b.zip"], Ordered());

        _service.MoveInQueue(a.Id, -1); // already first
        Assert.Equal(["a.zip", "c.zip", "b.zip"], Ordered());

        _service.SetQueue([a.Id], null);
        Assert.Null(_service.Find(a.Id)!.QueueId);
        Assert.Equal(DownloadStatus.Paused, _service.Find(a.Id)!.Status);
        Assert.Equal(["c.zip", "b.zip"], Ordered());
        Assert.Equal([1, 2], Ordered().Select(n => _service.GetAll().Single(d => d.FileName == n).QueuePosition));

        IEnumerable<string> Ordered() => _service.GetAll()
            .Where(d => d.QueueId == DownloadQueue.MainQueueId)
            .OrderBy(d => d.QueuePosition)
            .Select(d => d.FileName)
            .ToList();
    }

    [Fact]
    public async Task Remove_with_files_deletes_the_finished_file()
    {
        _h.Server.AddFile("del.bin", 1000);
        var added = AddUrl("del.bin");
        var completed = new TaskCompletionSource();
        _service.StateChanged += (_, e) =>
        {
            if (e.Status == DownloadStatus.Completed)
            {
                completed.TrySetResult();
            }
        };
        _service.Start(added.Id);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var path = _service.Find(added.Id)!.FullPath;
        Assert.True(File.Exists(path));

        await _service.RemoveAsync([added.Id], deleteFiles: true);

        Assert.False(File.Exists(path));
        Assert.Null(_service.Find(added.Id));
        Assert.Null(_h.Repository.Get(added.Id));
    }

    [Fact]
    public void Remove_completed_keeps_unfinished_entries()
    {
        var done = AddUrl("done.zip");
        var todo = AddUrl("todo.zip");
        var d = _service.Find(done.Id)!;
        d.Status = DownloadStatus.Completed;
        _service.Save(d);

        Assert.Equal(1, _service.RemoveCompleted());
        Assert.Null(_service.Find(done.Id));
        Assert.NotNull(_service.Find(todo.Id));
    }

    [Fact]
    public void Category_can_be_reassigned()
    {
        var added = AddUrl("x.zip");

        _service.SetCategory([added.Id], 3);

        Assert.Equal(3, _service.Find(added.Id)!.CategoryId);
        Assert.Equal(3, _h.Repository.Get(added.Id)!.CategoryId);
    }

    [Fact]
    public async Task Redownload_fetches_the_file_again()
    {
        var file = _h.Server.AddFile("again.bin", 2000);
        var added = AddUrl("again.bin");
        var completions = 0;
        var second = new TaskCompletionSource();
        _service.StateChanged += (_, e) =>
        {
            if (e.Status == DownloadStatus.Completed && Interlocked.Increment(ref completions) == 2)
            {
                second.TrySetResult();
            }
        };
        var first = new TaskCompletionSource();
        _service.StateChanged += (_, e) =>
        {
            if (e.Status == DownloadStatus.Completed)
            {
                first.TrySetResult();
            }
        };
        _service.Start(added.Id);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        file.ChangeContent(5);

        await _service.RedownloadAsync(added.Id);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var download = _service.Find(added.Id)!;
        Assert.Equal(DownloadStatus.Completed, download.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(download.FullPath));
    }
}
