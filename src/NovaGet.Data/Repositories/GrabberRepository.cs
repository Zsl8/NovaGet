using Dapper;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;

namespace NovaGet.Data.Repositories;

public sealed class GrabberRepository(SqliteDatabase database) : IGrabberRepository
{
    private const string ResultColumns =
        "id, projectId, url, type, size, status, localPath, pageUrl, downloadId, etag AS ETag, lastModified, foundAt";

    public IReadOnlyList<GrabberProject> GetProjects()
    {
        using var connection = database.Open();
        return [.. connection.Query<GrabberProject>("SELECT id, name, settingsJson, lastRunAt FROM GrabberProject ORDER BY name COLLATE NOCASE, id;")];
    }

    public GrabberProject? GetProject(long id)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<GrabberProject>("SELECT id, name, settingsJson, lastRunAt FROM GrabberProject WHERE id = @id;", new { id });
    }

    public long InsertProject(GrabberProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = database.Open();
        project.Id = connection.ExecuteScalar<long>(
            "INSERT INTO GrabberProject (name, settingsJson, lastRunAt) VALUES (@Name, @SettingsJson, @LastRunAt); SELECT last_insert_rowid();",
            project);
        return project.Id;
    }

    public void UpdateProject(GrabberProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        using var connection = database.Open();
        connection.Execute("UPDATE GrabberProject SET name = @Name, settingsJson = @SettingsJson, lastRunAt = @LastRunAt WHERE id = @Id;", project);
    }

    public void DeleteProject(long id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("DELETE FROM GrabberResult WHERE projectId = @id;", new { id }, transaction);
        connection.Execute("DELETE FROM GrabberProject WHERE id = @id;", new { id }, transaction);
        transaction.Commit();
    }

    public IReadOnlyList<GrabberResult> GetResults(long projectId)
    {
        using var connection = database.Open();
        return [.. connection.Query<GrabberResult>($"SELECT {ResultColumns} FROM GrabberResult WHERE projectId = @projectId ORDER BY id;", new { projectId })];
    }

    public GrabberResult? FindResult(long projectId, string url)
    {
        using var connection = database.Open();
        return connection.QuerySingleOrDefault<GrabberResult>(
            $"SELECT {ResultColumns} FROM GrabberResult WHERE projectId = @projectId AND url = @url;", new { projectId, url });
    }

    public long SaveResult(GrabberResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var connection = database.Open();
        result.Id = connection.ExecuteScalar<long>(
            """
            INSERT INTO GrabberResult (projectId, url, type, size, status, localPath, pageUrl, downloadId, etag, lastModified, foundAt)
            VALUES (@ProjectId, @Url, @Type, @Size, @Status, @LocalPath, @PageUrl, @DownloadId, @ETag, @LastModified, @FoundAt)
            ON CONFLICT (projectId, url) DO UPDATE SET
                type = excluded.type, size = excluded.size, status = excluded.status, localPath = excluded.localPath,
                pageUrl = excluded.pageUrl, downloadId = excluded.downloadId, etag = excluded.etag,
                lastModified = excluded.lastModified, foundAt = excluded.foundAt;
            SELECT id FROM GrabberResult WHERE projectId = @ProjectId AND url = @Url;
            """,
            result);
        return result.Id;
    }

    public void MarkDownloaded(long downloadId, long size, string? etag, DateTime? lastModified, string? localPath)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE GrabberResult SET status = @status, size = @size, etag = @etag, lastModified = @lastModified,
                localPath = COALESCE(@localPath, localPath)
            WHERE downloadId = @downloadId;
            """,
            new { downloadId, size, etag, lastModified, localPath, status = GrabberResultStatus.Downloaded });
    }

    public IReadOnlyList<long> GetDownloadIds(long projectId)
    {
        using var connection = database.Open();
        return [.. connection.Query<long>("SELECT downloadId FROM GrabberResult WHERE projectId = @projectId AND downloadId IS NOT NULL;", new { projectId })];
    }
}
