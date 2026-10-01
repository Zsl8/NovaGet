using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Network;

/// <summary>
/// Turns Options → Proxy/Socks into what <see cref="SocketsHttpHandler"/> needs.
/// <list type="bullet">
/// <item>No proxy: <c>UseProxy = false</c>.</item>
/// <item>System settings: the default proxy, which on Windows follows the Internet Options (including their
/// automatic configuration script).</item>
/// <item>Manual: a per-scheme HTTP proxy with the bypass list, or SOCKS when "Use SOCKS" is on (SOCKS wins).</item>
/// <item>Automatic configuration script: resolved by the platform when it can (see <see cref="IPacResolver"/>);
/// without a resolver the system settings apply.</item>
/// </list>
/// </summary>
public static class ProxyFactory
{
    /// <summary>Configures <paramref name="handler"/> for the given settings.</summary>
    public static void Configure(SocketsHttpHandler handler, ProxySettings settings, ISecretProtector protector, IPacResolver? pac = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var proxy = Create(settings, protector, pac, out var useProxy);
        handler.UseProxy = useProxy;
        handler.Proxy = proxy;
    }

    /// <summary>The proxy to use; null with <paramref name="useProxy"/> true means "the system default proxy".</summary>
    public static IWebProxy? Create(ProxySettings settings, ISecretProtector protector, IPacResolver? pac, out bool useProxy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        useProxy = true;
        switch (settings.Mode)
        {
            case ProxyMode.None:
                useProxy = false;
                return null;
            case ProxyMode.Manual when settings.Socks.Enabled && !string.IsNullOrWhiteSpace(settings.Socks.Host):
                return CreateSocks(settings, protector);
            case ProxyMode.Manual:
                return new ManualProxy(settings, protector);
            case ProxyMode.AutoConfigScript when pac is not null && Uri.TryCreate(settings.PacUrl, UriKind.Absolute, out var script):
                return new PacProxy(pac, script, new BypassList(settings.BypassList));
            default:
                return null;
        }
    }

    /// <summary>A key that changes whenever the effective proxy configuration changes (for caching clients).</summary>
    public static string Fingerprint(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Join('|',
            settings.Mode, settings.PacUrl, settings.UseSameProxyForAllProtocols, settings.BypassList,
            Server(settings.Http), Server(settings.Https), Server(settings.Ftp),
            settings.Socks.Enabled, settings.Socks.Version, settings.Socks.Host, settings.Socks.Port, settings.Socks.User,
            settings.Socks.ProtectedPassword, settings.Socks.ResolveDnsThroughSocks);

        static string Server(ProxyServerSettings s) =>
            $"{s.Host}:{s.Port}:{s.User}:{s.ProtectedPassword}";
    }

    /// <summary>
    /// SOCKS through the handler's built-in support. SOCKS4 can only send addresses, so "Resolve DNS through
    /// SOCKS" upgrades it to SOCKS4a; SOCKS5 always sends host names (resolved by the proxy).
    /// </summary>
    private static BypassingProxy CreateSocks(ProxySettings settings, ISecretProtector protector)
    {
        var socks = settings.Socks;
        var scheme = socks.Version switch
        {
            SocksVersion.Socks4 when socks.ResolveDnsThroughSocks => "socks4a",
            SocksVersion.Socks4 => "socks4",
            SocksVersion.Socks4a => "socks4a",
            _ => "socks5",
        };
        var proxy = new WebProxy(new Uri($"{scheme}://{HostForUri(socks.Host)}:{socks.Port.ToString(CultureInfo.InvariantCulture)}"))
        {
            BypassProxyOnLocal = false,
        };
        if (!string.IsNullOrEmpty(socks.User))
        {
            proxy.Credentials = new NetworkCredential(socks.User, protector.Unprotect(socks.ProtectedPassword) ?? string.Empty);
        }

        return new BypassingProxy(proxy, new BypassList(settings.BypassList));
    }

    internal static string HostForUri(string host)
    {
        host = host.Trim();
        return IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host.Trim('[', ']')}]"
            : host;
    }

    /// <summary>HTTP proxies per scheme (http, https, ftp-over-http), with optional credentials each.</summary>
    private sealed class ManualProxy : IWebProxy
    {
        private readonly Uri? _http;
        private readonly Uri? _https;
        private readonly Uri? _ftp;
        private readonly BypassList _bypass;

        public ManualProxy(ProxySettings settings, ISecretProtector protector)
        {
            var same = settings.UseSameProxyForAllProtocols;
            _http = ToUri(settings.Http);
            _https = same ? _http : ToUri(settings.Https);
            _ftp = same ? _http : ToUri(settings.Ftp);
            _bypass = new BypassList(settings.BypassList);

            var credentials = new CredentialCache();
            Add(credentials, _http, settings.Http, protector);
            if (!same)
            {
                Add(credentials, _https, settings.Https, protector);
                Add(credentials, _ftp, settings.Ftp, protector);
            }

            Credentials = credentials;
        }

        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => destination.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => _https,
            "ftp" or "ftps" => _ftp,
            _ => _http,
        };

        public bool IsBypassed(Uri host) => GetProxy(host) is null || _bypass.Matches(host);

        private static Uri? ToUri(ProxyServerSettings server) =>
            string.IsNullOrWhiteSpace(server.Host) || server.Port is < 1 or > 65535
                ? null
                : new Uri($"http://{HostForUri(server.Host)}:{server.Port.ToString(CultureInfo.InvariantCulture)}");

        private static void Add(CredentialCache cache, Uri? proxy, ProxyServerSettings server, ISecretProtector protector)
        {
            if (proxy is null || string.IsNullOrEmpty(server.User))
            {
                return;
            }

            var credential = new NetworkCredential(server.User, protector.Unprotect(server.ProtectedPassword) ?? string.Empty);
            foreach (var scheme in new[] { "Basic", "Digest", "NTLM", "Negotiate" })
            {
                if (cache.GetCredential(proxy, scheme) is null)
                {
                    cache.Add(proxy, scheme, credential);
                }
            }
        }
    }

    private sealed class BypassingProxy(IWebProxy inner, BypassList bypass) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => inner.Credentials;
            set => inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) => inner.GetProxy(destination);

        public bool IsBypassed(Uri host) => bypass.Matches(host) || inner.IsBypassed(host);
    }

    private sealed class PacProxy(IPacResolver resolver, Uri script, BypassList bypass) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = CredentialCache.DefaultCredentials;

        public Uri? GetProxy(Uri destination) => resolver.Resolve(script, destination);

        public bool IsBypassed(Uri host) => bypass.Matches(host) || GetProxy(host) is null;
    }
}

/// <summary>Evaluates an automatic proxy configuration script (WinHTTP on Windows).</summary>
public interface IPacResolver
{
    /// <summary>The proxy for <paramref name="destination"/>, or null for a direct connection.</summary>
    Uri? Resolve(Uri script, Uri destination);
}

/// <summary>
/// "Bypass proxy for:" — host patterns separated by semicolons, commas or spaces. <c>*</c> and <c>?</c> are
/// wildcards (<c>*.corp.example</c>, <c>10.*</c>); <c>&lt;local&gt;</c> matches host names without a dot.
/// </summary>
public sealed class BypassList
{
    private readonly List<Regex> _patterns = [];
    private readonly bool _local;

    public BypassList(string? list)
    {
        foreach (var raw in (list ?? string.Empty).Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim();
            if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                _local = true;
                continue;
            }

            // Accept "http://host:port" and "host:port" by keeping only the host.
            if (entry.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(entry, UriKind.Absolute, out var uri))
            {
                entry = uri.Host;
            }
            else if (entry.Count(c => c == ':') == 1)
            {
                entry = entry[..entry.IndexOf(':', StringComparison.Ordinal)];
            }

            var pattern = "^" + Regex.Escape(entry.Trim('[', ']')).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
            _patterns.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
        }
    }

    public bool Matches(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var host = destination.IdnHost.Trim('[', ']');
        if (_local && !host.Contains('.', StringComparison.Ordinal) && !host.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        return _patterns.Exists(p => p.IsMatch(host));
    }
}
