namespace NovaGet.Core.Engine;

/// <summary>A protocol the engine can download over (HTTP/HTTPS now, FTP/FTPS later).</summary>
public interface ITransferProtocol
{
    bool CanHandle(Uri uri);

    Task<ProbeResult> ProbeAsync(RequestContext context, CancellationToken cancellationToken);

    /// <summary>Opens the body starting at <see cref="TransferRequest.Start"/>. Throws <see cref="DownloadException"/> on failure.</summary>
    Task<TransferResponse> OpenAsync(TransferRequest request, CancellationToken cancellationToken);
}

/// <summary>Validators sent with If-Range so a changed file is detected instead of silently mixed.</summary>
public sealed record ResourceValidator(string? ETag, DateTimeOffset? LastModified)
{
    public bool IsEmpty => string.IsNullOrEmpty(ETag) && LastModified is null;
}

/// <param name="Start">First byte wanted.</param>
/// <param name="End">Last byte wanted (inclusive), or -1 for "to the end".</param>
/// <param name="UseRange">Send a Range header (false for servers without range support, from byte 0).</param>
public sealed record TransferRequest(RequestContext Context, long Start, long End, ResourceValidator? Validator, bool UseRange = true);

/// <summary>An open response body. Dispose it to release the connection.</summary>
public sealed class TransferResponse : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public TransferResponse(Stream body, IDisposable? owner)
    {
        Body = body;
        _owner = owner;
    }

    public Stream Body { get; }

    /// <summary>The server honored the range (HTTP 206 / FTP REST).</summary>
    public bool IsPartial { get; init; }

    /// <summary>First byte of the body.</summary>
    public long Start { get; init; }

    /// <summary>Total resource size if the server reported it.</summary>
    public long? TotalSize { get; init; }

    public Uri? FinalUri { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>HTTP Content-Type, when the protocol has one.</summary>
    public string? ContentType { get; init; }

    /// <summary>HTTP Content-Disposition, when sent.</summary>
    public string? ContentDisposition { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
    }
}
