using NovaGet.Core.Models;

namespace NovaGet.Core.Abstractions;

public interface IDownloadRepository
{
    long Insert(Download download);

    void Update(Download download);

    /// <summary>Hot path: persists progress fields only.</summary>
    void UpdateProgress(long id, long downloaded, DownloadStatus status, DateTime? lastTryAt);

    void UpdateStatus(long id, DownloadStatus status, string? lastError);

    Download? Get(long id);

    IReadOnlyList<Download> GetAll();

    IReadOnlyList<Download> GetByQueue(long queueId);

    void Delete(long id);

    /// <summary>Replaces the saved segment map for a download (atomic).</summary>
    void SaveSegments(long downloadId, IReadOnlyCollection<Segment> segments);

    IReadOnlyList<Segment> GetSegments(long downloadId);
}

public interface ICategoryRepository
{
    IReadOnlyList<Category> GetAll();

    Category? Get(long id);

    long Insert(Category category);

    void Update(Category category);

    /// <summary>Deletes a user category; built-ins are refused. Downloads move to General.</summary>
    bool Delete(long id);
}

public interface IQueueRepository
{
    IReadOnlyList<DownloadQueue> GetAll();

    DownloadQueue? Get(long id);

    DownloadQueue? GetByName(string name);

    long Insert(DownloadQueue queue);

    void Update(DownloadQueue queue);

    /// <summary>Deletes a user queue; built-ins are refused. Its downloads leave the queue.</summary>
    bool Delete(long id);
}

public interface ISiteLoginRepository
{
    IReadOnlyList<SiteLogin> GetAll();

    long Insert(SiteLogin login);

    void Update(SiteLogin login);

    void Delete(long id);
}

public interface IServerExceptionRepository
{
    IReadOnlyList<ServerException> GetAll();

    long Insert(ServerException exception);

    void Update(ServerException exception);

    void Delete(long id);
}
