using System.Net;
using System.Net.Sockets;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Network;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Network;

public sealed class ProxyTests
{
    private static readonly ISecretProtector Protector = new DevOnlySecretProtector();

    private static ProxySettings Manual(string host = "proxy.example", int port = 3128) => new()
    {
        Mode = ProxyMode.Manual,
        Http = new ProxyServerSettings { Host = host, Port = port },
        UseSameProxyForAllProtocols = true,
        BypassList = string.Empty,
    };

    [Fact]
    public void No_proxy_turns_the_proxy_off()
    {
        var proxy = ProxyFactory.Create(new ProxySettings { Mode = ProxyMode.None }, Protector, null, out var useProxy);

        Assert.Null(proxy);
        Assert.False(useProxy);
    }

    [Fact]
    public void System_settings_use_the_default_proxy()
    {
        var proxy = ProxyFactory.Create(new ProxySettings { Mode = ProxyMode.System }, Protector, null, out var useProxy);

        Assert.Null(proxy);
        Assert.True(useProxy);
    }

    [Fact]
    public void Pac_without_a_resolver_falls_back_to_system_settings()
    {
        var settings = new ProxySettings { Mode = ProxyMode.AutoConfigScript, PacUrl = "http://wpad.example/proxy.pac" };

        Assert.Null(ProxyFactory.Create(settings, Protector, null, out var useProxy));
        Assert.True(useProxy);
    }

    [Fact]
    public void Pac_uses_the_resolver()
    {
        var settings = new ProxySettings { Mode = ProxyMode.AutoConfigScript, PacUrl = "http://wpad.example/proxy.pac", BypassList = "intranet" };
        var resolver = new FakePac(new Uri("http://pac-chosen.example:8080"));

        var proxy = ProxyFactory.Create(settings, Protector, resolver, out _)!;

        Assert.Equal(new Uri("http://pac-chosen.example:8080"), proxy.GetProxy(new Uri("https://files.example/a.zip")));
        Assert.Equal(new Uri("http://wpad.example/proxy.pac"), resolver.LastScript);
        Assert.True(proxy.IsBypassed(new Uri("http://intranet/")));
    }

    [Fact]
    public void Manual_uses_one_proxy_for_all_protocols_when_asked()
    {
        var proxy = ProxyFactory.Create(Manual(), Protector, null, out var useProxy)!;

        Assert.True(useProxy);
        Assert.Equal(new Uri("http://proxy.example:3128"), proxy.GetProxy(new Uri("http://a.example/")));
        Assert.Equal(new Uri("http://proxy.example:3128"), proxy.GetProxy(new Uri("https://a.example/")));
        Assert.False(proxy.IsBypassed(new Uri("https://a.example/")));
    }

    [Fact]
    public void Manual_picks_the_proxy_by_scheme()
    {
        var settings = Manual();
        settings.UseSameProxyForAllProtocols = false;
        settings.Https = new ProxyServerSettings { Host = "secure.example", Port = 8443 };

        var proxy = ProxyFactory.Create(settings, Protector, null, out _)!;

        Assert.Equal(new Uri("http://proxy.example:3128"), proxy.GetProxy(new Uri("http://a.example/")));
        Assert.Equal(new Uri("http://secure.example:8443"), proxy.GetProxy(new Uri("https://a.example/")));
        // No FTP proxy configured: direct.
        Assert.True(proxy.IsBypassed(new Uri("ftp://a.example/")));
    }

    [Fact]
    public void Manual_credentials_are_offered_to_the_proxy()
    {
        var settings = Manual();
        settings.Http.User = "alice";
        settings.Http.ProtectedPassword = Protector.Protect("s3cret");

        var proxy = ProxyFactory.Create(settings, Protector, null, out _)!;
        var credential = proxy.Credentials!.GetCredential(new Uri("http://proxy.example:3128"), "Basic");

        Assert.Equal("alice", credential!.UserName);
        Assert.Equal("s3cret", credential.Password);
    }

    [Theory]
    [InlineData(SocksVersion.Socks5, true, "socks5")]
    [InlineData(SocksVersion.Socks5, false, "socks5")]
    [InlineData(SocksVersion.Socks4, false, "socks4")]
    [InlineData(SocksVersion.Socks4, true, "socks4a")]
    [InlineData(SocksVersion.Socks4a, false, "socks4a")]
    public void Socks_wins_over_http_proxies(SocksVersion version, bool remoteDns, string scheme)
    {
        var settings = Manual();
        settings.Socks = new SocksSettings { Enabled = true, Host = "socks.example", Port = 1080, Version = version, ResolveDnsThroughSocks = remoteDns };

        var proxy = ProxyFactory.Create(settings, Protector, null, out _)!;

        Assert.Equal(new Uri($"{scheme}://socks.example:1080"), proxy.GetProxy(new Uri("https://a.example/")));
    }

    [Fact]
    public void Ipv6_proxy_hosts_are_bracketed()
    {
        var proxy = ProxyFactory.Create(Manual("::1", 8080), Protector, null, out _)!;

        Assert.Equal(new Uri("http://[::1]:8080"), proxy.GetProxy(new Uri("http://a.example/")));
    }

    [Theory]
    [InlineData("localhost;127.0.0.1;<local>", "http://localhost/", true)]
    [InlineData("localhost;127.0.0.1;<local>", "http://intranet/", true)]
    [InlineData("localhost;127.0.0.1;<local>", "http://www.example.com/", false)]
    [InlineData("*.corp.example", "https://files.corp.example/x", true)]
    [InlineData("*.corp.example", "https://corp.example.evil.com/x", false)]
    [InlineData("10.*, 192.168.?.1", "http://10.1.2.3/", true)]
    [InlineData("10.*, 192.168.?.1", "http://192.168.5.1/", true)]
    [InlineData("10.*, 192.168.?.1", "http://192.168.15.1/", false)]
    [InlineData("http://mirror.example:8080", "http://MIRROR.example/", true)]
    [InlineData("mirror.example:8080", "http://mirror.example/", true)]
    [InlineData("", "http://localhost/", false)]
    public void Bypass_list_patterns(string list, string url, bool bypassed)
    {
        Assert.Equal(bypassed, new BypassList(list).Matches(new Uri(url)));
    }

    [Fact]
    public void Fingerprint_changes_with_the_configuration()
    {
        var a = Manual();
        var b = Manual();
        Assert.Equal(ProxyFactory.Fingerprint(a), ProxyFactory.Fingerprint(b));

        b.Http.Port = 3129;
        Assert.NotEqual(ProxyFactory.Fingerprint(a), ProxyFactory.Fingerprint(b));
        b.Http.Port = 3128;
        b.Socks.Enabled = true;
        Assert.NotEqual(ProxyFactory.Fingerprint(a), ProxyFactory.Fingerprint(b));
    }

    [Fact]
    public async Task Tester_reports_a_working_direct_connection()
    {
        await using var server = await TestHttpServer.StartAsync();
        var file = server.AddFile("/probe.bin", 1000);

        var result = await ProxyTester.TestAsync(new ProxySettings { Mode = ProxyMode.None }, Protector, server.UrlFor(file), TimeSpan.FromSeconds(10));

        Assert.True(result.Success, result.Message);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Tester_reports_an_unreachable_proxy()
    {
        await using var server = await TestHttpServer.StartAsync();
        var file = server.AddFile("/probe.bin", 1000);

        var result = await ProxyTester.TestAsync(Manual("127.0.0.1", ClosedPort()), Protector, server.UrlFor(file), TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Tester_refuses_non_http_addresses()
    {
        var result = await ProxyTester.TestAsync(new ProxySettings(), Protector, new Uri("ftp://example.com/"), TimeSpan.FromSeconds(1));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Engine_clients_follow_proxy_settings_as_they_change()
    {
        await using var server = await TestHttpServer.StartAsync();
        var file = server.AddFile("/data.bin", 4096);
        var settings = Manual("127.0.0.1", ClosedPort());
        using var provider = new HttpClientProvider(() => settings, Protector);
        var context = new RequestContext { Url = server.UrlFor(file) };

        // Through a proxy that isn't listening: fails.
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetClient(context).GetAsync(server.UrlFor(file)));

        // Bypassing it for the test server: works, with a new client.
        settings.BypassList = "127.0.0.1;localhost";
        using var response = await provider.GetClient(context).GetAsync(server.UrlFor(file));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakePac(Uri result) : IPacResolver
    {
        public Uri? LastScript { get; private set; }

        public Uri? Resolve(Uri script, Uri destination)
        {
            LastScript = script;
            return result;
        }
    }
}
