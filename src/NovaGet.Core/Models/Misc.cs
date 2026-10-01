namespace NovaGet.Core.Models;

/// <summary>Options → Site Logins entry (table <c>SiteLogin</c>).</summary>
public sealed class SiteLogin
{
    public long Id { get; set; }

    /// <summary>Wildcard pattern, e.g. <c>*.example.com/private/*</c>.</summary>
    public string UrlPattern { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    /// <summary>Plain text in memory; DPAPI-encrypted in the database.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>Options → Connection exception (table <c>ServerException</c>).</summary>
public sealed class ServerException
{
    public long Id { get; set; }

    public string Host { get; set; } = string.Empty;

    public int MaxConnections { get; set; }
}

/// <summary>A saved site grabber project (table <c>GrabberProject</c>).</summary>
public sealed class GrabberProject
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SettingsJson { get; set; } = "{}";

    public DateTime? LastRunAt { get; set; }
}

public enum GrabberResultStatus
{
    Found = 0,
    Queued = 1,
    Downloaded = 2,
    Skipped = 3,
    Error = 4,
}

/// <summary>A file found by the grabber (table <c>GrabberResult</c>).</summary>
public sealed class GrabberResult
{
    public long Id { get; set; }

    public long ProjectId { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? Type { get; set; }

    public long Size { get; set; } = -1;

    public GrabberResultStatus Status { get; set; }

    public string? LocalPath { get; set; }

    /// <summary>The page the file was found on.</summary>
    public string? PageUrl { get; set; }

    /// <summary>The download made from it, if any.</summary>
    public long? DownloadId { get; set; }

    /// <summary>Validators of the downloaded copy, for "only new or changed files" on the next run.</summary>
    public string? ETag { get; set; }

    public DateTime? LastModified { get; set; }

    public DateTime? FoundAt { get; set; }
}
