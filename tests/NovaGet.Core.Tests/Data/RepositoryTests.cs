using Dapper;
using NovaGet.Core.Models;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Data;

public sealed class RepositoryTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static Download NewDownload(string name = "file.zip") => new()
    {
        Url = "https://example.com/" + name,
        OriginalUrl = "https://example.com/" + name,
        Referrer = "https://example.com/page",
        FileName = name,
        SavePath = "/tmp/dl",
        CategoryId = 2,
        Size = 1234,
        Downloaded = 10,
        Status = DownloadStatus.Paused,
        ResumeCapable = true,
        Cookies = "session=abc; theme=dark",
        AuthUser = "ali",
        AuthPassword = "s3cret",
        AddedAt = new DateTime(2026, 9, 1, 12, 30, 15, DateTimeKind.Utc),
        LastModified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        ETag = "\"abc\"",
    };

    [Fact]
    public void Download_round_trips_all_fields()
    {
        var repo = new DownloadRepository(_db.Database, _db.Protector);
        var d = NewDownload();
        d.MaxConnections = 16;
        d.SpeedLimitKBps = 500;
        d.QueueId = DownloadQueue.MainQueueId;
        d.QueuePosition = 3;
        d.IsStream = true;
        d.StreamManifestJson = "{}";
        d.ChecksumAlgo = "SHA-256";
        d.ChecksumExpected = "ff";
        d.Description = "desc";

        var id = repo.Insert(d);
        var loaded = repo.Get(id)!;

        Assert.Equal(d.Url, loaded.Url);
        Assert.Equal(d.Referrer, loaded.Referrer);
        Assert.Equal(d.Size, loaded.Size);
        Assert.Equal(DownloadStatus.Paused, loaded.Status);
        Assert.True(loaded.ResumeCapable);
        Assert.Equal("session=abc; theme=dark", loaded.Cookies);
        Assert.Equal("s3cret", loaded.AuthPassword);
        Assert.Equal(16, loaded.MaxConnections);
        Assert.Equal(500, loaded.SpeedLimitKBps);
        Assert.Equal(DownloadQueue.MainQueueId, loaded.QueueId);
        Assert.Equal(3, loaded.QueuePosition);
        Assert.True(loaded.IsStream);
        Assert.Equal("\"abc\"", loaded.ETag);
        Assert.Equal(d.AddedAt, loaded.AddedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.AddedAt.Kind);
        Assert.Equal(d.LastModified, loaded.LastModified);
        Assert.Null(loaded.CompletedAt);
        Assert.Equal("SHA-256", loaded.ChecksumAlgo);
        Assert.Equal("desc", loaded.Description);
    }

    [Fact]
    public void Secrets_are_not_stored_in_plain_text()
    {
        var repo = new DownloadRepository(_db.Database, _db.Protector);
        var id = repo.Insert(NewDownload());

        using var connection = _db.Database.Open();
        var row = connection.QuerySingle<(string cookies, string authPass)>("SELECT cookies, authPass FROM Download WHERE id = @id", new { id });
        Assert.DoesNotContain("session=abc", row.cookies, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", row.authPass, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_resume_capability_stays_null()
    {
        var repo = new DownloadRepository(_db.Database, _db.Protector);
        var d = NewDownload();
        d.ResumeCapable = null;
        d.Cookies = null;
        d.AuthPassword = null;

        var loaded = repo.Get(repo.Insert(d))!;

        Assert.Null(loaded.ResumeCapable);
        Assert.Null(loaded.Cookies);
        Assert.Null(loaded.AuthPassword);
    }

    [Fact]
    public void Update_progress_status_and_delete()
    {
        var repo = new DownloadRepository(_db.Database, _db.Protector);
        var id = repo.Insert(NewDownload());
        var tried = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        repo.UpdateProgress(id, 999, DownloadStatus.Receiving, tried);
        var afterProgress = repo.Get(id)!;
        Assert.Equal(999, afterProgress.Downloaded);
        Assert.Equal(DownloadStatus.Receiving, afterProgress.Status);
        Assert.Equal(tried, afterProgress.LastTryAt);

        repo.UpdateStatus(id, DownloadStatus.Error, "HTTP 404");
        var afterError = repo.Get(id)!;
        Assert.Equal(DownloadStatus.Error, afterError.Status);
        Assert.Equal("HTTP 404", afterError.LastError);
        Assert.Equal(tried, afterError.LastTryAt);

        afterError.FileName = "renamed.zip";
        afterError.CompletedAt = tried;
        repo.Update(afterError);
        Assert.Equal("renamed.zip", repo.Get(id)!.FileName);
        Assert.Equal(tried, repo.Get(id)!.CompletedAt);

        repo.Delete(id);
        Assert.Null(repo.Get(id));
    }

    [Fact]
    public void Segments_are_replaced_atomically_and_cascade_on_delete()
    {
        var repo = new DownloadRepository(_db.Database, _db.Protector);
        var id = repo.Insert(NewDownload());

        repo.SaveSegments(id, [
            new Segment { StartByte = 0, EndByte = 99, CurrentByte = 50, State = SegmentState.Active },
            new Segment { StartByte = 100, EndByte = 199, CurrentByte = 200, State = SegmentState.Done },
        ]);
        repo.SaveSegments(id, [
            new Segment { StartByte = 0, EndByte = 99, CurrentByte = 60, State = SegmentState.Active },
            new Segment { StartByte = 100, EndByte = 149, CurrentByte = 150, State = SegmentState.Done },
            new Segment { StartByte = 150, EndByte = 199, CurrentByte = 160, State = SegmentState.Pending },
        ]);

        var segments = repo.GetSegments(id);
        Assert.Equal(3, segments.Count);
        Assert.Equal(60, segments[0].CurrentByte);
        Assert.Equal(SegmentState.Done, segments[1].State);
        Assert.True(segments[1].IsComplete);
        Assert.Equal(40, segments[2].Remaining);

        repo.Delete(id);
        Assert.Empty(repo.GetSegments(id));
    }

    [Fact]
    public void Queue_membership_is_ordered()
    {
        var repo = new DownloadRepository(_db.Database, _db.Protector);
        foreach (var (name, pos) in new[] { ("c", 3), ("a", 1), ("b", 2) })
        {
            var d = NewDownload(name);
            d.QueueId = DownloadQueue.MainQueueId;
            d.QueuePosition = pos;
            repo.Insert(d);
        }

        repo.Insert(NewDownload("not-queued"));

        Assert.Equal(["a", "b", "c"], repo.GetByQueue(DownloadQueue.MainQueueId).Select(d => d.FileName));
        Assert.Equal(4, repo.GetAll().Count);
    }

    [Fact]
    public void Built_in_categories_cannot_be_deleted_or_renamed()
    {
        var repo = new CategoryRepository(_db.Database);
        var compressed = repo.Get(2)!;
        compressed.Name = "Archives";
        compressed.Extensions = "zip";
        compressed.DefaultSaveDir = "/data/zips";
        repo.Update(compressed);

        var loaded = repo.Get(2)!;
        Assert.Equal("Compressed", loaded.Name);
        Assert.Equal("zip", loaded.Extensions);
        Assert.Equal("/data/zips", loaded.DefaultSaveDir);
        Assert.True(loaded.IsBuiltIn);
        Assert.False(repo.Delete(2));
    }

    [Fact]
    public void Deleting_user_category_moves_downloads_to_General()
    {
        var categories = new CategoryRepository(_db.Database);
        var downloads = new DownloadRepository(_db.Database, _db.Protector);
        var id = categories.Insert(new Category { Name = "Books", ParentId = Category.GeneralId, Extensions = "epub mobi" });
        var d = NewDownload("x.epub");
        d.CategoryId = id;
        var downloadId = downloads.Insert(d);

        Assert.Equal(7, categories.GetAll().Count);
        Assert.Equal(6, categories.Get(id)!.SortOrder);
        Assert.True(categories.Delete(id));

        Assert.Equal(Category.GeneralId, downloads.Get(downloadId)!.CategoryId);
        Assert.Null(categories.Get(id));
    }

    [Fact]
    public void Queues_round_trip_schedule_and_protect_built_ins()
    {
        var repo = new QueueRepository(_db.Database);
        var downloads = new DownloadRepository(_db.Database, _db.Protector);
        var queue = new DownloadQueue
        {
            Name = "Night",
            SimultaneousCount = 3,
            Schedule = new QueueSchedule
            {
                StartEnabled = true,
                StartTime = new TimeSpan(1, 30, 0),
                Days = [DayOfWeek.Monday, DayOfWeek.Friday],
                TurnOffWhenDone = true,
                PowerAction = PowerAction.Hibernate,
            },
        };
        var id = repo.Insert(queue);

        var loaded = repo.GetByName("night")!;
        Assert.Equal(id, loaded.Id);
        Assert.Equal(3, loaded.SimultaneousCount);
        Assert.True(loaded.Schedule.StartEnabled);
        Assert.Equal(new TimeSpan(1, 30, 0), loaded.Schedule.StartTime);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], loaded.Schedule.Days);
        Assert.Equal(PowerAction.Hibernate, loaded.Schedule.PowerAction);

        var d = NewDownload();
        d.QueueId = id;
        var downloadId = downloads.Insert(d);

        Assert.False(repo.Delete(DownloadQueue.MainQueueId));
        Assert.False(repo.Delete(DownloadQueue.SyncQueueId));
        Assert.True(repo.Delete(id));
        Assert.Null(downloads.Get(downloadId)!.QueueId);

        var main = repo.Get(DownloadQueue.MainQueueId)!;
        main.Name = "Renamed";
        main.SimultaneousCount = 2;
        repo.Update(main);
        Assert.Equal("Main download queue", repo.Get(DownloadQueue.MainQueueId)!.Name);
        Assert.Equal(2, repo.Get(DownloadQueue.MainQueueId)!.SimultaneousCount);
        Assert.True(repo.Get(DownloadQueue.SyncQueueId)!.IsSyncQueue);
    }

    [Fact]
    public void Site_logins_encrypt_passwords()
    {
        var repo = new SiteLoginRepository(_db.Database, _db.Protector);
        var id = repo.Insert(new SiteLogin { UrlPattern = "*.example.com/*", User = "u", Password = "pw" });

        Assert.Equal("pw", repo.GetAll().Single().Password);
        using var connection = _db.Database.Open();
        Assert.NotEqual("pw", connection.ExecuteScalar<string>("SELECT passwordDpapi FROM SiteLogin WHERE id = @id", new { id }));

        repo.Update(new SiteLogin { Id = id, UrlPattern = "x", User = "v", Password = "pw2" });
        Assert.Equal("pw2", repo.GetAll().Single().Password);
        repo.Delete(id);
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Server_exceptions_crud()
    {
        var repo = new ServerExceptionRepository(_db.Database);
        var id = repo.Insert(new ServerException { Host = "mirror.example.com", MaxConnections = 2 });

        Assert.Equal(2, repo.GetAll().Single().MaxConnections);
        repo.Update(new ServerException { Id = id, Host = "mirror.example.com", MaxConnections = 4 });
        Assert.Equal(4, repo.GetAll().Single().MaxConnections);
        repo.Delete(id);
        Assert.Empty(repo.GetAll());
    }
}
