using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace NovaGet.Core.Engine.Http;

public interface IHttpClientProvider
{
    /// <summary>A client suitable for the request (shared per proxy/credential profile).</summary>
    HttpClient GetClient(RequestContext context);
}

/// <summary>
/// Owns the <see cref="SocketsHttpHandler"/>s. Redirects, decompression and cookies are off (the engine handles
/// them explicitly), connections are pooled so a finished segment's keep-alive socket serves the next split,
/// and header bytes are decoded as Latin-1 so UTF-8 file names can be recovered later.
/// </summary>
public sealed class HttpClientProvider : IHttpClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<bool, HttpClient> _clients = new();

    public HttpClient GetClient(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _clients.GetOrAdd(context.IgnoreCertificateErrors, CreateClient);
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private static HttpClient CreateClient(bool ignoreCertificateErrors)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxConnectionsPerServer = 256,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ResponseHeaderEncodingSelector = static (_, _) => Encoding.Latin1,
            UseProxy = true,
        };

        if (ignoreCertificateErrors)
        {
            // Only reachable through the per-download "Ignore certificate errors" option, which warns the user (spec §22).
#pragma warning disable CA5359
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }
}
