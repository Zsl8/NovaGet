using System.Security.Cryptography;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Models;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Engine;

/// <summary>Section 23: downloads through an HTTP proxy (CONNECT for https) and a SOCKS5 proxy, with several connections.</summary>
public sealed class ProxyDownloadTests
{
    private const string ProxyUser = "alice";
    private const string ProxyPassword = "s3cret pass";
    private static readonly DevOnlySecretProtector Protector = new();

    public static TheoryData<TestProxyKind, bool, bool> Cases => new()
    {
        { TestProxyKind.Http, false, false },   // http: absolute-form requests to the proxy
        { TestProxyKind.Http, true, false },    // https: CONNECT tunnels
        { TestProxyKind.Http, true, true },     // https: CONNECT with a proxy login
        { TestProxyKind.Socks5, false, false },
        { TestProxyKind.Socks5, true, true },   // https through SOCKS5 with a user name and password
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Downloads_go_through_the_proxy(TestProxyKind kind, bool https, bool login)
    {
        // The HTTP test proxy checks no login; the setting is still sent and must not break the tunnel.
        await using var proxy = TestProxyServer.Start(kind, login && kind == TestProxyKind.Socks5 ? (ProxyUser, ProxyPassword) : null);
        await using var h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 4 }, https);
        UseProxy(h, Settings(kind, proxy.Port, login));
        var file = h.Server.AddFile("/via-proxy.bin", 3_000_000, seed: 21);
        file.BytesPerSecond = 4_000_000;
        var id = h.Add(file, configure: d => d.IgnoreCertificateErrors = https);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), Sha256(h.Get(id).FullPath));
        var port = h.Server.BaseUri.Port;
        var tunnels = proxy.Targets.Where(t => t.EndsWith($":{port}", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(tunnels);
        Assert.All(tunnels, t => Assert.StartsWith(
            kind == TestProxyKind.Socks5 ? "SOCKS " : https ? "CONNECT " : "", t, StringComparison.Ordinal));
        if (kind == TestProxyKind.Http && !https)
        {
            Assert.DoesNotContain(tunnels, t => t.StartsWith("CONNECT ", StringComparison.Ordinal));
        }

        Assert.True(tunnels.Count > 1, $"one connection only: {string.Join(", ", tunnels)}");
    }

    [Fact]
    public async Task A_wrong_socks_password_fails_instead_of_going_direct()
    {
        await using var proxy = TestProxyServer.Start(TestProxyKind.Socks5, (ProxyUser, "another password"));
        await using var h = await EngineHarness.CreateAsync(o => o with { MaxRetries = 1 });
        UseProxy(h, Settings(TestProxyKind.Socks5, proxy.Port, login: true));
        var file = h.Server.AddFile("/refused.bin", 10_000);

        var result = await h.RunAsync(h.Add(file), TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Empty(h.Server.RequestsFor(file));
    }

    private static ProxySettings Settings(TestProxyKind kind, int port, bool login)
    {
        var settings = new ProxySettings { Mode = ProxyMode.Manual, BypassList = string.Empty, UseSameProxyForAllProtocols = true };
        if (kind == TestProxyKind.Http)
        {
            settings.Http = new ProxyServerSettings
            {
                Host = "127.0.0.1",
                Port = port,
                User = login ? ProxyUser : string.Empty,
                ProtectedPassword = login ? Protector.Protect(ProxyPassword) : string.Empty,
            };
        }
        else
        {
            settings.Socks = new SocksSettings
            {
                Enabled = true,
                Version = SocksVersion.Socks5,
                Host = "127.0.0.1",
                Port = port,
                User = login ? ProxyUser : string.Empty,
                ProtectedPassword = login ? Protector.Protect(ProxyPassword) : string.Empty,
            };
        }

        return settings;
    }

    private static void UseProxy(EngineHarness h, ProxySettings settings)
    {
        h.Clients.Dispose();
        h.Clients = new HttpClientProvider(() => settings, Protector);
        h.RestartEngine();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
