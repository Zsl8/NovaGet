using Dapper;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;
using NovaGet.Core.Security;

namespace NovaGet.Data.Repositories;

public sealed class DownloadRepository(SqliteDatabase database, ISecretProtector protector) : IDownloadRepository
{
    private const string SelectColumns = """
        SELECT id, url, originalUrl, referrer, fileName, savePath, categoryId, size, downloaded, status,
               resumeCapable, description, userAgent, cookies, authUser, authPass AS AuthPassword,
               maxConnections, speedLimitKBps, queueId, queuePosition, addedAt, lastTryAt, completedAt,
               lastError, etag, lastModified, isStream, streamManifestJson, checksumAlgo, checksumExpected
        FROM Download
        """;

    public long Insert(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        using var connection = database.Open();
        download.Id = connection.ExecuteScalar<long>(
            """
            INSERT INTO Download (url, originalUrl, referrer, fileName, savePath, categoryId, size, downloaded, status,
                resumeCapable, description, userAgent, cookies, authUser, authPass, maxConnections, speedLimitKBps,
                queueId, queuePosition, addedAt, lastTryAt, completedAt, lastError, etag, lastModified, isStream,
                streamManifestJson, checksumAlgo, checksumExpected)
            VALUES (@Url, @OriginalUrl, @Referrer, @FileName, @SavePath, @CategoryId, @Size, @Downloaded, @Status,
                @ResumeCapable, @Description, @UserAgent, @Cookies, @AuthUser, @AuthPass, @MaxConnections, @SpeedLimitKBps,
                @QueueId, @QueuePosition, @AddedAt, @LastTryAt, @CompletedAt, @LastError, @ETag, @LastModified, @IsStream,
                @StreamManifestJson, @ChecksumAlgo, @ChecksumExpected);
            SELECT last_insert_rowid();
            """,
            ToParameters(download));
        return download.Id;
    }

    public void Update(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Download SET url = @Url, originalUrl = @OriginalUrl, referrer = @Referrer, fileName = @FileName,
                savePath = @SavePath, categoryId = @CategoryId, size = @Size, downloaded = @Downloaded, status = @Status,
                resumeCapable = @ResumeCapable, description = @Description, userAgent = @UserAgent, cookies = @Cookies,
                authUser = @AuthUser, authPass = @AuthPass, maxConnections = @MaxConnections, speedLimitKBps = @SpeedLimitKBps,
                queueId = @QueueId, queuePosition = @QueuePosition, addedAt = @AddedAt, lastTryAt = @LastTryAt,
                completedAt = @CompletedAt, lastError = @LastError, etag = @ETag, lastModified = @LastModified,
                isStream = @IsStream, streamManifestJson = @StreamManifestJson, checksumAlgo = @ChecksumAlgo,
                checksumExpected = @ChecksumExpected
            WHERE id = @Id;
            """,
            ToParameters(download));
    }

    public void UpdateProgress(long id, long downloaded, DownloadStatus status, DateTime? lastTryAt)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE Download SET downloaded = @downloaded, status = @status, lastTryAt = COALESCE(@lastTryAt, lastTryAt) WHERE id = @id;",
            new { id, downloaded, status, lastTryAt });
    }

    public void UpdateStatus(long id, DownloadStatus status, string? lastError)
    {
        using var connection = database.Open();
        connection.Execute(
            "UPDATE Download SET status = @status, lastError = @lastError WHERE id = @id;",
            new { id, status, lastError });
    }

    public Download? Get(long id)
    {
        using var connection = database.Open();
        var download = connection.QuerySingleOrDefault<Download>(SelectColumns + " WHERE id = @id;", new { id });
        return download is null ? null : Decrypt(download);
    }

    public IReadOnlyList<Download> GetAll()
    {
        using var connection = database.Open();
        return [.. connection.Query<Download>(SelectColumns + " ORDER BY id;").Select(Decrypt)];
    }

    public IReadOnlyList<Download> GetByQueue(long queueId)
    {
        using var connection = database.Open();
        return [.. connection.Query<Download>(SelectColumns + " WHERE queueId = @queueId ORDER BY queuePosition, id;", new { queueId }).Select(Decrypt)];
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM Download WHERE id = @id;", new { id });
    }

    public void SaveSegments(long downloadId, IReadOnlyCollection<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("DELETE FROM Segment WHERE downloadId = @downloadId;", new { downloadId }, transaction);
        connection.Execute(
            "INSERT INTO Segment (downloadId, startByte, endByte, currentByte, state) VALUES (@DownloadId, @StartByte, @EndByte, @CurrentByte, @State);",
            segments.Select(s => new { DownloadId = downloadId, s.StartByte, s.EndByte, s.CurrentByte, s.State }),
            transaction);
        transaction.Commit();
    }

    public IReadOnlyList<Segment> GetSegments(long downloadId)
    {
        using var connection = database.Open();
        return [.. connection.Query<Segment>(
            "SELECT id, downloadId, startByte, endByte, currentByte, state FROM Segment WHERE downloadId = @downloadId ORDER BY startByte;",
            new { downloadId })];
    }

    private object ToParameters(Download d) => new
    {
        d.Id,
        d.Url,
        d.OriginalUrl,
        d.Referrer,
        d.FileName,
        d.SavePath,
        d.CategoryId,
        d.Size,
        d.Downloaded,
        d.Status,
        d.ResumeCapable,
        d.Description,
        d.UserAgent,
        Cookies = string.IsNullOrEmpty(d.Cookies) ? null : protector.Protect(d.Cookies),
        d.AuthUser,
        AuthPass = string.IsNullOrEmpty(d.AuthPassword) ? null : protector.Protect(d.AuthPassword),
        d.MaxConnections,
        d.SpeedLimitKBps,
        d.QueueId,
        d.QueuePosition,
        d.AddedAt,
        d.LastTryAt,
        d.CompletedAt,
        d.LastError,
        d.ETag,
        d.LastModified,
        d.IsStream,
        d.StreamManifestJson,
        d.ChecksumAlgo,
        d.ChecksumExpected,
    };

    private Download Decrypt(Download d)
    {
        d.Cookies = string.IsNullOrEmpty(d.Cookies) ? null : protector.Unprotect(d.Cookies);
        d.AuthPassword = string.IsNullOrEmpty(d.AuthPassword) ? null : protector.Unprotect(d.AuthPassword);
        return d;
    }
}
