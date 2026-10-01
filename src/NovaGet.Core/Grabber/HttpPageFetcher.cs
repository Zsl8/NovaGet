using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Naming;

namespace NovaGet.Core.Grabber;

/// <summary>
/// Fetches through the download engine's protocols (proxy, certificates, Site Logins). Only HTML pages (and text when
/// asked) are read, up to <see cref="SiteCrawler.MaxPageBytes"/>; for anything else the headers are enough.
/// </summary>
public sealed class HttpPageFetcher(IEnumerable<ITransferProtocol> protocols, Func<Uri, Uri?, RequestContext> contextFor) : IPageFetcher
{
    private readonly IReadOnlyList<ITransferProtocol> _protocols = [.. protocols];

    public async Task<FetchedResource> FetchAsync(Uri url, Uri? referrer, bool text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var protocol = _protocols.FirstOrDefault(p => p.CanHandle(url))
            ?? throw new DownloadException(DownloadErrorKind.InvalidAddress, $"Unsupported address: {url.Scheme}:");
        var response = await protocol.OpenAsync(new TransferRequest(contextFor(url, referrer), 0, -1, null, UseRange: false), cancellationToken).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            var final = response.FinalUri ?? url;
            var resource = new FetchedResource
            {
                Url = final,
                ContentType = response.ContentType,
                Size = response.TotalSize ?? -1,
                FileName = ContentDispositionParser.GetFileName(response.ContentDisposition),
            };
            var isText = text && response.ContentType is { } type
                && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("css", StringComparison.OrdinalIgnoreCase));
            if (!resource.IsHtml && !isText)
            {
                return resource;
            }

            if (response.TotalSize > SiteCrawler.MaxPageBytes)
            {
                return resource;
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            try
            {
                while ((read = await response.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > SiteCrawler.MaxPageBytes)
                    {
                        return resource; // too big to be a page worth exploring
                    }

                    buffer.Write(chunk, 0, read);
                }
            }
            catch (IOException ex)
            {
                throw new DownloadException(DownloadErrorKind.Network, "The connection was interrupted.", inner: ex);
            }

            return resource with { Body = buffer.ToArray(), Size = buffer.Length };
        }
    }
}
