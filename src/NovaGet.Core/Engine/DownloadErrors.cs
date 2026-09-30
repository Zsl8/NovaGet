namespace NovaGet.Core.Engine;

public enum DownloadErrorKind
{
    None,

    /// <summary>Connection failed or was reset (retried).</summary>
    Network,

    /// <summary>The server refused the connection (retried; lowers the host's connection count).</summary>
    ConnectionRefused,

    /// <summary>No data within the timeout (retried).</summary>
    Timeout,

    /// <summary>429 / 503: too many connections or temporarily overloaded (retried; lowers the connection count).</summary>
    ServerBusy,

    /// <summary>Other HTTP error; 5xx are retried, 4xx are not.</summary>
    HttpError,

    /// <summary>403 / 404 / 410: the address no longer works. Offer "Refresh download address".</summary>
    LinkExpired,

    /// <summary>401 / 407 without usable credentials.</summary>
    AuthenticationRequired,

    /// <summary>The file on the server changed since the download started. Offer to restart.</summary>
    ServerFileChanged,

    /// <summary>The server ignored a range request although it said it supports them.</summary>
    RangeNotSupported,

    DiskFull,

    FileSystem,

    TooManyRedirects,

    InvalidAddress,

    /// <summary>DRM-protected media; never downloaded.</summary>
    ProtectedContent,

    Unknown,
}

/// <summary>A classified download failure. <see cref="IsTransient"/> failures are retried.</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(DownloadErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public DownloadErrorKind Kind { get; }

    public int? StatusCode { get; }

    /// <summary>Server-reported total size, when the error carries one (e.g. 416 with <c>Content-Range: */N</c>).</summary>
    public long? ReportedSize { get; init; }

    public bool IsTransient => Kind switch
    {
        DownloadErrorKind.Network or DownloadErrorKind.ConnectionRefused or DownloadErrorKind.Timeout or DownloadErrorKind.ServerBusy => true,
        DownloadErrorKind.HttpError => StatusCode is null or >= 500 or 408,
        _ => false,
    };

    /// <summary>Errors that suggest the server limits concurrent connections.</summary>
    public bool SuggestsConnectionLimit => Kind is DownloadErrorKind.ServerBusy or DownloadErrorKind.ConnectionRefused;
}
