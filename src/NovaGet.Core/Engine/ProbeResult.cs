namespace NovaGet.Core.Engine;

/// <summary>What the server told us about a file before downloading it.</summary>
public sealed record ProbeResult
{
    /// <summary>Address after following redirects.</summary>
    public required Uri FinalUri { get; init; }

    /// <summary>Size in bytes, or -1 when unknown.</summary>
    public long Size { get; init; } = -1;

    /// <summary>The server honors byte ranges (Accept-Ranges: bytes or a 206 reply).</summary>
    public bool ResumeSupported { get; init; }

    /// <summary>Sanitized name from Content-Disposition, the final URL, or index.html.</summary>
    public required string FileName { get; init; }

    public string? ContentType { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public int RedirectCount { get; init; }
}
