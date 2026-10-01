using System.Net;
using System.Security.Cryptography;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Ftp;
using NovaGet.Core.Models;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Engine;

public sealed class FtpTests : IAsyncLifetime
{
    private EngineHarness _h = null!;
    private TestFtpServer _ftp = null!;
    private readonly FakeSiteCredentials _logins = new();

    public Task InitializeAsync() => SetUpAsync(FtpTlsMode.None);

    public async Task DisposeAsync()
    {
        await _h.DisposeAsync();
        await _ftp.DisposeAsync();
    }

    private async Task SetUpAsync(FtpTlsMode tls, int maxConnections = 4)
    {
        if (_ftp is not null)
        {
            await _ftp.DisposeAsync();
        }

        _ftp = await TestFtpServer.StartAsync(tls);
        _h ??= await EngineHarness.CreateAsync(o => o with { MaxConnections = maxConnections });
        _h.Options = _h.Options with { MaxConnections = maxConnections };
        _h.ExtraProtocols.Clear();
        _h.ExtraProtocols.Add(new FtpTransferProtocol(_logins));
        _h.RestartEngine();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Probe_reads_size_date_name_and_resume_support()
    {
        var file = _ftp.AddFile("/pub/archive%20one.zip".Replace("%20", " ", StringComparison.Ordinal), 123_456);
        var protocol = new FtpTransferProtocol();

        var probe = await protocol.ProbeAsync(new RequestContext { Url = _ftp.UrlFor(file) }, CancellationToken.None);

        Assert.Equal(123_456, probe.Size);
        Assert.True(probe.ResumeSupported);
        Assert.Equal("archive one.zip", probe.FileName);
        Assert.Equal(file.LastModified, probe.LastModified);
    }

    [Fact]
    public async Task Downloads_with_several_connections()
    {
        var file = _ftp.AddFile("/data/big.bin", 3_000_000, seed: 7);
        file.BytesPerSecond = 2_000_000;
        var id = _h.Add(_ftp.UrlFor(file));

        var result = await _h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        var download = _h.Get(id);
        Assert.Equal("big.bin", download.FileName);
        Assert.Equal(file.Sha256(), Sha256(download.FullPath));
        Assert.True(_ftp.PeakConnections > 1, $"peak {_ftp.PeakConnections}");
        Assert.Contains(_ftp.Commands, c => c.StartsWith("REST ", StringComparison.Ordinal) && c != "REST 0");
    }

    [Fact]
    public async Task Active_mode_is_used_when_passive_mode_is_off()
    {
        var settings = new NovaGet.Core.Settings.ProxySettings { Mode = NovaGet.Core.Settings.ProxyMode.None, FtpPassiveMode = false };
        _h.ExtraProtocols.Clear();
        _h.ExtraProtocols.Add(new FtpTransferProtocol(_logins, () => settings));
        _h.RestartEngine();
        var file = _ftp.AddFile("/data/active.bin", 1_200_000, seed: 11);
        file.BytesPerSecond = 2_000_000;
        var id = _h.Add(_ftp.UrlFor(file));

        var result = await _h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), Sha256(_h.Get(id).FullPath));
        Assert.Contains(_ftp.Commands, c => c.StartsWith("EPRT ", StringComparison.Ordinal) || c.StartsWith("PORT ", StringComparison.Ordinal));
        Assert.DoesNotContain(_ftp.Commands, c => c is "PASV" or "EPSV");
    }

    [Fact]
    public async Task Passive_mode_is_the_default()
    {
        var file = _ftp.AddFile("/data/passive.bin", 200_000, seed: 12);

        var result = await _h.RunAsync(_h.Add(_ftp.UrlFor(file)));

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Contains(_ftp.Commands, c => c is "PASV" or "EPSV");
        Assert.DoesNotContain(_ftp.Commands, c => c.StartsWith("EPRT ", StringComparison.Ordinal) || c.StartsWith("PORT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resumes_after_a_pause()
    {
        var file = _ftp.AddFile("/data/resume.bin", 1_500_000, seed: 3);
        file.BytesPerSecond = 500_000;
        var id = _h.Add(_ftp.UrlFor(file));
        var ended = _h.WaitForEndAsync(id);
        Assert.True(_h.Engine.Start(id));
        await EngineHarness.WaitUntilAsync(() => (_h.Engine.GetProgress(id)?.Downloaded ?? 0) > 300_000, because: "some data");
        await _h.Engine.PauseAsync(id);
        Assert.Equal(DownloadStatus.Paused, (await ended).Status);
        file.BytesPerSecond = 0;

        var result = await _h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), Sha256(_h.Get(id).FullPath));
    }

    [Fact]
    public async Task Logs_in_with_the_address_login_or_a_site_login()
    {
        _ftp.AllowAnonymous = false;
        _ftp.Users["alice"] = "s3cret:@";
        var file = _ftp.AddFile("/private/report.pdf", 40_000);

        var withAddressLogin = _h.Add(_ftp.UrlFor(file.Path, "alice", "s3cret:@"));
        Assert.Equal(DownloadStatus.Completed, (await _h.RunAsync(withAddressLogin)).Status);

        var anonymous = _h.Add(_ftp.UrlFor(file));
        var failed = await _h.RunAsync(anonymous);
        Assert.Equal(DownloadStatus.Error, failed.Status);
        Assert.Equal(DownloadErrorKind.AuthenticationRequired, failed.ErrorKind);

        _logins.Login = new NetworkCredential("alice", "s3cret:@");
        var withSiteLogin = _h.Add(_ftp.UrlFor(file), "report-copy.pdf");
        Assert.Equal(DownloadStatus.Completed, (await _h.RunAsync(withSiteLogin)).Status);
        Assert.Contains("USER alice", _ftp.Commands);
    }

    [Theory]
    [InlineData(FtpTlsMode.Explicit)]
    [InlineData(FtpTlsMode.Implicit)]
    public async Task Ftps_needs_a_trusted_certificate_or_the_override(FtpTlsMode mode)
    {
        await SetUpAsync(mode);
        var file = _ftp.AddFile("/secure/data.bin", 600_000, seed: 11);

        var strict = _h.Add(_ftp.UrlFor(file));
        var rejected = await _h.RunAsync(strict);
        Assert.Equal(DownloadStatus.Error, rejected.Status);

        var trusting = _h.Add(_ftp.UrlFor(file), "trusted.bin", d => d.IgnoreCertificateErrors = true);
        var result = await _h.RunAsync(trusting);
        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), Sha256(_h.Get(trusting).FullPath));
        if (mode == FtpTlsMode.Explicit)
        {
            Assert.Contains(_ftp.Commands, c => c.StartsWith("AUTH TLS", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains("PROT P", _ftp.Commands);
    }

    [Fact]
    public async Task Servers_without_rest_get_one_connection()
    {
        _ftp.RefuseRest = true;
        var file = _ftp.AddFile("/norest.bin", 800_000, seed: 5);

        var result = await _h.RunAsync(_h.Add(_ftp.UrlFor(file)));

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(1, _ftp.PeakConnections);
        Assert.DoesNotContain(_ftp.Commands, c => c.StartsWith("REST ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Too_many_connections_lowers_the_count()
    {
        _ftp.MaxConnections = 2;
        var file = _ftp.AddFile("/limited.bin", 2_000_000, seed: 9);
        file.BytesPerSecond = 1_500_000;

        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await _h.RunAsync(_h.Add(_ftp.UrlFor(file)), TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}"); // a refused connection must not stall its segment
    }

    [Fact]
    public async Task Missing_files_fail_as_expired_links()
    {
        var result = await _h.RunAsync(_h.Add(_ftp.UrlFor("/nothing/here.bin")));

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.LinkExpired, result.ErrorKind);
    }

    [Theory]
    [InlineData("ftp://h/f", FluentFTP.FtpEncryptionMode.None, 21)]
    [InlineData("ftp://h:2121/f", FluentFTP.FtpEncryptionMode.None, 2121)]
    [InlineData("ftps://h/f", FluentFTP.FtpEncryptionMode.Implicit, 990)]
    [InlineData("ftps://h:21/f", FluentFTP.FtpEncryptionMode.Explicit, 21)]
    [InlineData("ftpes://h/f", FluentFTP.FtpEncryptionMode.Explicit, 21)]
    public void Schemes_map_to_encryption_and_port(string url, FluentFTP.FtpEncryptionMode mode, int port)
    {
        Assert.Equal((mode, port), FtpTransferProtocol.Endpoint(new Uri(url)));
    }

    private sealed class FakeSiteCredentials : ISiteCredentials
    {
        public NetworkCredential? Login { get; set; }

        public NetworkCredential? Find(Uri uri) => Login;
    }
}

