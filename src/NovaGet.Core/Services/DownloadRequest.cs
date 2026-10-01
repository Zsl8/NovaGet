namespace NovaGet.Core.Services;

/// <summary>Everything known about a new download when it is added (from a dialog, the browser or the command line).</summary>
public sealed record DownloadRequest
{
    public required string Url { get; init; }

    /// <summary>Address before redirects; defaults to <see cref="Url"/>.</summary>
    public string? OriginalUrl { get; init; }

    public string? Referrer { get; init; }

    /// <summary>Empty = decided by the probe.</summary>
    public string? FileName { get; init; }

    /// <summary>Empty = the category's folder.</summary>
    public string? SaveFolder { get; init; }

    /// <summary>Empty = matched from the file name.</summary>
    public long? CategoryId { get; init; }

    public string? Description { get; init; }

    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    public string? AuthUser { get; init; }

    public string? AuthPassword { get; init; }

    /// <summary>Queue to append the download to (Download Later).</summary>
    public long? QueueId { get; init; }

    public long Size { get; init; } = -1;

    public bool? ResumeCapable { get; init; }

    public string? ETag { get; init; }

    public DateTime? LastModified { get; init; }

    public int? MaxConnections { get; init; }

    public int? SpeedLimitKBps { get; init; }

    /// <summary>Replace an existing file with the same name when finished ("Add duplicate and overwrite").</summary>
    public bool OverwriteExisting { get; init; }

    public bool IsStream { get; init; }

    public string? StreamManifestJson { get; init; }
}
