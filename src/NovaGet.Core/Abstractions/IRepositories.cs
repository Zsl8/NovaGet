using NovaGet.Core.Models;

namespace NovaGet.Core.Abstractions;

public interface IDownloadRepository
{
    long Insert(Download download);

    /// <summary>Writes every column (used when nothing else can be changing the row).</summary>
    void Update(Download download);

    /// <summary>
    /// Writes only what the user can edit (address, names, folder, category, queue, description, credentials,
    /// limits, checksum). Never touches engine-owned state, so it is safe while the download runs.
    /// </summary>
    void UpdateDetails(Download download);

    /// <summary>Engine: what the probe learned.</summary>
    void UpdateProbe(long id, string url, string fileName, long size, bool? resumeCapable, string? etag, DateTime? lastModified);

    /// <summary>Engine: the size became known or changed before any data was kept.</summary>
    void UpdateSize(long id, long size);

    /// <summary>Engine: resume support turned out to be missing.</summary>
    void UpdateResumeCapable(long id, bool? resumeCapable);

    /// <summary>Engine: the file is in place.</summary>
    void MarkCompleted(long id, string fileName, long size, DateTime completedAt);

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

/// <summary>Site grabber projects and the files they found.</summary>
public interface IGrabberRepository
{
    IReadOnlyList<GrabberProject> GetProjects();

    GrabberProject? GetProject(long id);

    long InsertProject(GrabberProject project);

    void UpdateProject(GrabberProject project);

    /// <summary>Deletes the project and its results (its downloads stay).</summary>
    void DeleteProject(long id);

    IReadOnlyList<GrabberResult> GetResults(long projectId);

    GrabberResult? FindResult(long projectId, string url);

    /// <summary>Inserts or updates by (project, url); returns the id.</summary>
    long SaveResult(GrabberResult result);

    /// <summary>A grabbed file's download finished: remember it and its validators.</summary>
    void MarkDownloaded(long downloadId, long size, string? etag, DateTime? lastModified, string? localPath);

    /// <summary>Downloads made by a project (for its node in the categories tree).</summary>
    IReadOnlyList<long> GetDownloadIds(long projectId);
}
