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
               lastError, etag, lastModified, isStream, streamManifestJson, checksumAlgo, checksumExpected, overwriteExisting,
               ignoreCertificateErrors
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
                streamManifestJson, checksumAlgo, checksumExpected, overwriteExisting, ignoreCertificateErrors)
            VALUES (@Url, @OriginalUrl, @Referrer, @FileName, @SavePath, @CategoryId, @Size, @Downloaded, @Status,
                @ResumeCapable, @Description, @UserAgent, @Cookies, @AuthUser, @AuthPass, @MaxConnections, @SpeedLimitKBps,
                @QueueId, @QueuePosition, @AddedAt, @LastTryAt, @CompletedAt, @LastError, @ETag, @LastModified, @IsStream,
                @StreamManifestJson, @ChecksumAlgo, @ChecksumExpected, @OverwriteExisting, @IgnoreCertificateErrors);
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
                checksumExpected = @ChecksumExpected, overwriteExisting = @OverwriteExisting,
                ignoreCertificateErrors = @IgnoreCertificateErrors
            WHERE id = @Id;
            """,
            ToParameters(download));
    }

    public void UpdateDetails(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Download SET url = @Url, originalUrl = @OriginalUrl, referrer = @Referrer, fileName = @FileName,
                savePath = @SavePath, categoryId = @CategoryId, description = @Description, userAgent = @UserAgent,
                cookies = @Cookies, authUser = @AuthUser, authPass = @AuthPass, maxConnections = @MaxConnections,
                speedLimitKBps = @SpeedLimitKBps, queueId = @QueueId, queuePosition = @QueuePosition,
                checksumAlgo = @ChecksumAlgo, checksumExpected = @ChecksumExpected, overwriteExisting = @OverwriteExisting,
                ignoreCertificateErrors = @IgnoreCertificateErrors,
                status = CASE WHEN @Status IN (@Paused, @Queued) AND status IN (@Paused, @Queued) THEN @Status ELSE status END
            WHERE id = @Id;
            """,
            new
            {
                download.Id,
                download.Url,
                download.OriginalUrl,
                download.Referrer,
                download.FileName,
                download.SavePath,
                download.CategoryId,
                download.Description,
                download.UserAgent,
                Cookies = string.IsNullOrEmpty(download.Cookies) ? null : protector.Protect(download.Cookies),
                download.AuthUser,
                AuthPass = string.IsNullOrEmpty(download.AuthPassword) ? null : protector.Protect(download.AuthPassword),
                download.MaxConnections,
                download.SpeedLimitKBps,
                download.QueueId,
                download.QueuePosition,
                download.ChecksumAlgo,
                download.ChecksumExpected,
                download.OverwriteExisting,
                download.IgnoreCertificateErrors,
                download.Status,
                Paused = DownloadStatus.Paused,
                Queued = DownloadStatus.Queued,
            });
    }

    public void UpdateProbe(long id, string url, string fileName, long size, bool? resumeCapable, string? etag, DateTime? lastModified)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Download SET url = @url, fileName = CASE WHEN fileName = '' THEN @fileName ELSE fileName END,
                size = @size, resumeCapable = @resumeCapable, etag = @etag, lastModified = @lastModified
            WHERE id = @id;
            """,
            new { id, url, fileName, size, resumeCapable, etag, lastModified });
    }

    public void UpdateSize(long id, long size)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Download SET size = @size WHERE id = @id;", new { id, size });
    }

    public void UpdateResumeCapable(long id, bool? resumeCapable)
    {
        using var connection = database.Open();
        connection.Execute("UPDATE Download SET resumeCapable = @resumeCapable WHERE id = @id;", new { id, resumeCapable });
    }

    public void MarkCompleted(long id, string fileName, long size, DateTime completedAt)
    {
        using var connection = database.Open();
        connection.Execute(
            """
            UPDATE Download SET fileName = @fileName, size = @size, downloaded = @size, status = @status,
                completedAt = @completedAt, lastError = NULL
            WHERE id = @id;
            """,
            new { id, fileName, size, completedAt, status = DownloadStatus.Completed });
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
        d.OverwriteExisting,
        d.IgnoreCertificateErrors,
    };

    private Download Decrypt(Download d)
    {
        d.Cookies = string.IsNullOrEmpty(d.Cookies) ? null : protector.Unprotect(d.Cookies);
        d.AuthPassword = string.IsNullOrEmpty(d.AuthPassword) ? null : protector.Unprotect(d.AuthPassword);
        return d;
    }
}
