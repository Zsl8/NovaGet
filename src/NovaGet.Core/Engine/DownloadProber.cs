namespace NovaGet.Core.Engine;

/// <summary>Asks the server about an address before it is added (the Add URL → File Info flow).</summary>
public interface IDownloadProber
{
    Task<ProbeResult> ProbeAsync(RequestContext context, CancellationToken cancellationToken);
}

public sealed class DownloadProber(IEnumerable<ITransferProtocol> protocols) : IDownloadProber
{
    private readonly IReadOnlyList<ITransferProtocol> _protocols = [.. protocols];

    public Task<ProbeResult> ProbeAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var protocol = _protocols.FirstOrDefault(p => p.CanHandle(context.Url))
            ?? throw new DownloadException(DownloadErrorKind.InvalidAddress, $"Unsupported address: {context.Url}");
        return protocol.ProbeAsync(context, cancellationToken);
    }
}
