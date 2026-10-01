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
        Assert.Equal(DownloadStatus.Queued, _service.Find(c.Id)!.Status);
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
        _h.Repository.MarkCompleted(done.Id, "done.zip", 10, DateTime.UtcNow);
        _service.Reload(done.Id);

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

    [Fact]
    public void Save_never_overwrites_engine_state()
    {
        var added = AddUrl("state.zip");
        var stale = _service.Find(added.Id)!;
        _h.Repository.UpdateProgress(added.Id, 5000, DownloadStatus.Receiving, DateTime.UtcNow);

        stale.Description = "edited";
        stale.Status = DownloadStatus.Paused;
        stale.Downloaded = 0;
        _service.Save(stale);

        var stored = _h.Repository.Get(added.Id)!;
        Assert.Equal("edited", stored.Description);
        Assert.Equal(DownloadStatus.Receiving, stored.Status);
        Assert.Equal(5000, stored.Downloaded);
        Assert.Equal("edited", _service.Find(added.Id)!.Description);
    }

    [Fact]
    public async Task Destination_changes_while_downloading_are_used_at_completion()
    {
        var file = _h.Server.AddFile("moving.bin", 2 * 1024 * 1024);
        file.BytesPerSecond = 1024 * 1024;
        var added = AddUrl("moving.bin");
        var completed = new TaskCompletionSource();
        _service.StateChanged += (_, e) =>
        {
            if (e.Status == DownloadStatus.Completed)
            {
                completed.TrySetResult();
            }
        };
        _service.Start(added.Id);
        await EngineHarness.WaitUntilAsync(() => _h.Engine.GetProgress(added.Id)?.Downloaded > 100 * 1024);

        var edit = _service.Find(added.Id)!;
        edit.SavePath = Path.Combine(_h.SaveDirectory, "elsewhere");
        edit.FileName = "renamed.bin";
        _service.Save(edit);
        file.BytesPerSecond = 0;
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var done = _service.Find(added.Id)!;
        Assert.Equal(Path.Combine(_h.SaveDirectory, "elsewhere", "renamed.bin"), done.FullPath);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(done.FullPath));
    }

    [Fact]
    public void Finds_duplicates_by_original_or_current_address()
    {
        var added = _service.Add(new DownloadRequest { Url = "https://CDN.example.com/final/a.zip", OriginalUrl = "https://example.com/get?id=1", FileName = "a.zip" });

        Assert.Equal(added.Id, _service.FindByUrl("https://example.com/get?id=1")!.Id);
        Assert.Equal(added.Id, _service.FindByUrl("https://cdn.example.com/final/a.zip")!.Id);
        Assert.Null(_service.FindByUrl("https://example.com/get?id=2"));
        Assert.Null(_service.FindByUrl("https://cdn.example.com/final/A.zip"));
    }

    [Fact]
    public async Task Move_or_rename_moves_finished_files()
    {
        _h.Server.AddFile("mv.bin", 1000);
        var added = AddUrl("mv.bin");
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
        var before = _service.Find(added.Id)!.FullPath;
        var folder = Path.Combine(_h.SaveDirectory, "moved");

        var error = await _service.MoveOrRenameAsync(added.Id, folder, "new name.bin");

        Assert.Null(error);
        Assert.False(File.Exists(before));
        Assert.True(File.Exists(Path.Combine(folder, "new name.bin")));
        Assert.Equal(Path.Combine(folder, "new name.bin"), _service.Find(added.Id)!.FullPath);
    }

    [Fact]
    public async Task Move_or_rename_of_unfinished_download_only_changes_the_destination()
    {
        var added = AddUrl("later.zip");

        var error = await _service.MoveOrRenameAsync(added.Id, "/somewhere/else", "x?.zip");

        Assert.Null(error);
        var d = _service.Find(added.Id)!;
        Assert.Equal("/somewhere/else", d.SavePath);
        Assert.Equal("x_.zip", d.FileName);
    }
}
