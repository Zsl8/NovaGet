using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Engine;

public sealed class HttpProbeTests : IAsyncLifetime
{
    private readonly HttpClientProvider _clients = new();
    private TestHttpServer _server = null!;

    public async Task InitializeAsync() => _server = await TestHttpServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _clients.Dispose();
    }

    private Task<ProbeResult> ProbeAsync(Uri url) =>
        new HttpTransferProtocol(_clients).ProbeAsync(new RequestContext { Url = url, Timeout = TimeSpan.FromSeconds(10) }, CancellationToken.None);

    [Fact]
    public async Task Range_capable_server()
    {
        var file = _server.AddFile("files/ubuntu.iso", 123_456);

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.Equal(123_456, probe.Size);
        Assert.True(probe.ResumeSupported);
        Assert.Equal("ubuntu.iso", probe.FileName);
        Assert.Equal(file.ETag, probe.ETag);
        Assert.Equal(file.LastModified, probe.LastModified);
        Assert.Equal(0, probe.RedirectCount);
        // Accept-Ranges on HEAD settles it: no GET needed.
        Assert.DoesNotContain(_server.RequestsFor(file), r => r.Method == "GET");
    }

    [Fact]
    public async Task Head_not_allowed_falls_back_to_ranged_get()
    {
        var file = _server.AddFile("nohead.bin", 5000);
        file.AllowHead = false;

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.Equal(5000, probe.Size);
        Assert.True(probe.ResumeSupported);
        Assert.Contains(_server.RequestsFor(file), r => r.Method == "GET" && r.Range == "bytes=0-0" && r.Status == 206);
    }

    [Fact]
    public async Task Unadvertised_ranges_are_confirmed_with_a_ranged_get()
    {
        var file = _server.AddFile("quiet.bin", 5000);
        file.AdvertiseRanges = false;

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.True(probe.ResumeSupported);
        Assert.Equal(5000, probe.Size);
    }

    [Fact]
    public async Task Server_without_ranges()
    {
        var file = _server.AddFile("noranges.bin", 7777);
        file.SupportsRanges = false;

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.False(probe.ResumeSupported);
        Assert.Equal(7777, probe.Size);
    }

    [Fact]
    public async Task Unknown_length()
    {
        var file = _server.AddFile("stream.bin", 7777);
        file.SupportsRanges = false;
        file.SendContentLength = false;

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.False(probe.ResumeSupported);
        Assert.Equal(-1, probe.Size);
    }

    [Fact]
    public async Task Follows_redirect_chains_and_keeps_the_final_url()
    {
        var file = _server.AddFile("target/file.zip", 1000);

        var probe = await ProbeAsync(_server.UrlFor("redirect/5/target/file.zip"));

        Assert.Equal(_server.UrlFor(file), probe.FinalUri);
        Assert.Equal(5, probe.RedirectCount);
        Assert.Equal("file.zip", probe.FileName);
        Assert.Equal(1000, probe.Size);
    }

    [Fact]
    public async Task Twenty_redirects_are_allowed_but_not_twenty_one()
    {
        _server.AddFile("t.bin", 10);

        Assert.Equal(20, (await ProbeAsync(_server.UrlFor("redirect/20/t.bin"))).RedirectCount);
        var ex = await Assert.ThrowsAsync<DownloadException>(() => ProbeAsync(_server.UrlFor("redirect/21/t.bin")));
        Assert.Equal(DownloadErrorKind.TooManyRedirects, ex.Kind);
    }

    [Fact]
    public async Task Content_disposition_rfc5987_arabic_name()
    {
        var file = _server.AddFile("download", 100);
        file.ContentDisposition = "attachment; filename=\"fallback.pdf\"; filename*=UTF-8''" + Uri.EscapeDataString("تقرير.pdf");

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.Equal("تقرير.pdf", probe.FileName);
    }

    [Fact]
    public async Task Content_type_supplies_a_missing_extension()
    {
        var file = _server.AddFile("get", 100);
        file.ContentType = "application/zip";

        var probe = await ProbeAsync(_server.UrlFor(file));

        Assert.Equal("get.zip", probe.FileName);
    }

    [Fact]
    public async Task Missing_file_is_reported_as_expired_link()
    {
        var ex = await Assert.ThrowsAsync<DownloadException>(() => ProbeAsync(_server.UrlFor("nothing-here.zip")));

        Assert.Equal(DownloadErrorKind.LinkExpired, ex.Kind);
        Assert.Equal(404, ex.StatusCode);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Sends_browser_context_headers()
    {
        var file = _server.AddFile("ctx.bin", 10);
        var context = new RequestContext
        {
            Url = _server.UrlFor(file),
            Referrer = "https://example.com/page",
            Cookies = "sid=abc",
            UserAgent = "TestAgent/1.0",
        };

        await new HttpTransferProtocol(_clients).ProbeAsync(context, CancellationToken.None);

        var request = _server.RequestsFor(file)[0];
        Assert.Equal("https://example.com/page", request.Headers["Referer"]);
        Assert.Equal("sid=abc", request.Headers["Cookie"]);
        Assert.Equal("TestAgent/1.0", request.Headers["User-Agent"]);
    }
}
