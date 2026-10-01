using System.Collections.Concurrent;
using System.Net;
using System.Text;
using NovaGet.Core.Network;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Engine.Http;

public interface IHttpClientProvider
{
    /// <summary>
    /// A client suitable for the request (shared per proxy, certificate and login profile). A <paramref name="credential"/>
    /// answers authentication challenges from the request's host only.
    /// </summary>
    HttpClient GetClient(RequestContext context, NetworkCredential? credential = null);
}

/// <summary>
/// Owns the <see cref="SocketsHttpHandler"/>s. Redirects, decompression and cookies are off (the engine handles
/// them explicitly), connections are pooled so a finished segment's keep-alive socket serves the next split,
/// and header bytes are decoded as Latin-1 so UTF-8 file names can be recovered later. Clients are rebuilt when
/// the proxy settings change; the old ones stay alive for requests still using them until the provider is disposed.
/// </summary>
public sealed class HttpClientProvider : IHttpClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<(bool IgnoreCertificateErrors, string Proxy, string Login), HttpClient> _clients = new();
    private readonly Func<ProxySettings>? _proxySettings;
    private readonly ISecretProtector? _protector;
    private readonly IPacResolver? _pac;

    /// <summary>Uses the system proxy settings.</summary>
    public HttpClientProvider()
    {
    }

    /// <summary>Uses Options → Proxy/Socks, re-read for every request.</summary>
    public HttpClientProvider(Func<ProxySettings> proxySettings, ISecretProtector protector, IPacResolver? pac = null)
    {
        _proxySettings = proxySettings;
        _protector = protector;
        _pac = pac;
    }

    public HttpClient GetClient(RequestContext context, NetworkCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var proxy = _proxySettings?.Invoke();
        var host = context.Url.Host;
        var key = (context.IgnoreCertificateErrors, proxy is null ? string.Empty : ProxyFactory.Fingerprint(proxy), LoginKey(host, credential));
        return _clients.GetOrAdd(key, k => CreateClient(k.IgnoreCertificateErrors, proxy, credential is null ? null : new HostCredentials(host, credential)));
    }

    private static string LoginKey(string host, NetworkCredential? credential)
    {
        if (credential is null)
        {
            return string.Empty;
        }

        if (ReferenceEquals(credential, CredentialCache.DefaultNetworkCredentials))
        {
            return "windows|" + host.ToLowerInvariant();
        }

        var secret = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(credential.Password ?? string.Empty));
        return $"{host.ToLowerInvariant()}|{credential.Domain}|{credential.UserName}|{Convert.ToHexString(secret)}";
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private HttpClient CreateClient(bool ignoreCertificateErrors, ProxySettings? proxy, ICredentials? credentials)
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

        if (proxy is not null && _protector is not null)
        {
            ProxyFactory.Configure(handler, proxy, _protector, _pac);
        }

        if (credentials is not null)
        {
            handler.Credentials = credentials;
            handler.PreAuthenticate = true;
        }

        if (ignoreCertificateErrors)
        {
            // Only reachable through the per-download "Ignore certificate errors" option, which warns the user (spec §22).
#pragma warning disable CA5359
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Hands the login only to the host it belongs to (never to a redirect target elsewhere).</summary>
    private sealed class HostCredentials(string host, NetworkCredential credential) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) ? credential : null;
    }
}
