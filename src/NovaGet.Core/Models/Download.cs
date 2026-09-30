namespace NovaGet.Core.Models;

/// <summary>One entry in the download list (table <c>Download</c>).</summary>
public sealed class Download
{
    public long Id { get; set; }

    /// <summary>Current address (may be replaced by "Refresh download address" or after redirects).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Address as originally added; shown to the user.</summary>
    public string OriginalUrl { get; set; } = string.Empty;

    public string? Referrer { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>Folder the finished file goes to.</summary>
    public string SavePath { get; set; } = string.Empty;

    public long CategoryId { get; set; }

    /// <summary>Total size in bytes, or -1 when unknown.</summary>
    public long Size { get; set; } = -1;

    public long Downloaded { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Paused;

    /// <summary>Null until probed.</summary>
    public bool? ResumeCapable { get; set; }

    public string? Description { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Plain text in memory; DPAPI-encrypted in the database.</summary>
    public string? Cookies { get; set; }

    public string? AuthUser { get; set; }

    /// <summary>Plain text in memory; DPAPI-encrypted in the database.</summary>
    public string? AuthPassword { get; set; }

    /// <summary>Per-download override; null uses the global/server setting.</summary>
    public int? MaxConnections { get; set; }

    /// <summary>Per-download speed limit in KB/s; null or 0 means unlimited.</summary>
    public int? SpeedLimitKBps { get; set; }

    public long? QueueId { get; set; }

    public int QueuePosition { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastTryAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? LastError { get; set; }

    public string? ETag { get; set; }

    public DateTime? LastModified { get; set; }

    public bool IsStream { get; set; }

    public string? StreamManifestJson { get; set; }

    public string? ChecksumAlgo { get; set; }

    public string? ChecksumExpected { get; set; }

    /// <summary>Replace an existing file with the same name when the download completes (duplicate handling).</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>A shallow copy (all members are immutable values).</summary>
    public Download Clone() => (Download)MemberwiseClone();

    /// <summary>Full destination path of the finished file.</summary>
    public string FullPath => Path.Combine(SavePath, FileName);

    /// <summary>Progress in percent (0–100), or null when the size is unknown.</summary>
    public double? Percent => Size > 0 ? Math.Min(100.0, Downloaded * 100.0 / Size) : null;
}
