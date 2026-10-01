using System.Collections.Concurrent;
using System.Diagnostics;
using NovaGet.Core.Engine;
using NovaGet.Core.Grabber;
using NovaGet.Core.Tests.Engine;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Grabber;

/// <summary>A small site on the test server: pages, images, a style sheet, robots.txt and an "external" host (localhost).</summary>
internal sealed class TestSite
{
    private const string Html = "text/html; charset=utf-8";

    public TestSite(TestHttpServer server)
    {
        Server = server;
        var external = new UriBuilder(server.BaseUri) { Host = "localhost" }.Uri.AbsoluteUri.TrimEnd('/');
        server.AddText("index.html", $"""
            <html><head><title>Home</title><link rel="stylesheet" href="/style.css"></head><body>
            <a href="/a.html">A</a> <a href="/b/">B</a> <a href="/private/p.html">P</a>
            <a href="/files/doc.pdf">Doc</a> <a href="/download">Get</a> <a href="{external}/ext.html">External</a>
            <img src="/img/1.png" srcset="/img/1-2x.png 2x">
            </body></html>
            """, Html);
        server.AddText("a.html", """<html><body><a href="/c.html">C</a> <img src="/img/2.png"> <a href="/index.html">Home</a></body></html>""", Html);
        server.AddText("b/", """<html><body><img src="3.png"></body></html>""", Html);
        server.AddText("c.html", """<html><body><a href="/d.html">D</a> <img src="/img/4.png"></body></html>""", Html);
        server.AddText("d.html", """<html><body><img src="/img/5.png"></body></html>""", Html);
        server.AddText("ext.html", """<html><body><img src="/img/ext.png"></body></html>""", Html);
        server.AddText("private/p.html", """<html><body><img src="/img/secret.png"></body></html>""", Html);
        server.AddText("style.css", "body { background: url(img/bg.png) }", "text/css");
        server.AddText("robots.txt", "User-agent: *\nDisallow: /private/\n", "text/plain");
        var seed = 1;
        foreach (var image in new[] { "img/1.png", "img/1-2x.png", "img/2.png", "b/3.png", "img/4.png", "img/5.png", "img/bg.png", "img/secret.png", "img/ext.png" })
        {
            Images[image] = server.AddFile(image, 2000 + seed, seed++);
            Images[image].ContentType = "image/png";
        }

        server.AddFile("files/doc.pdf", 20_000, 50).ContentType = "application/pdf";
        var zip = server.AddFile("download", 30_000, 60);
        zip.ContentType = "application/zip";
        zip.ContentDisposition = "attachment; filename=\"pack.zip\"";
    }

    public TestHttpServer Server { get; }

    public Dictionary<string, TestFile> Images { get; } = [];

    public Uri Start => Server.UrlFor("index.html");

    public GrabberSettings Settings(GrabberTemplate template, string saveFolder) =>
        GrabberSettings.ForTemplate(template) with { StartUrl = Start.AbsoluteUri, SaveFolder = saveFolder };

    public static IPageFetcher Fetcher(EngineHarness harness) =>
        new HttpPageFetcher([harness.Http], (url, referrer) => new RequestContext { Url = url, Referrer = referrer?.AbsoluteUri, Timeout = TimeSpan.FromSeconds(10) });
}

public sealed class SiteCrawlerTests
{
    private static async Task<(SiteCrawler Crawler, List<GrabbedFile> Files, List<CrawledPage> Pages)> CrawlAsync(EngineHarness harness, GrabberSettings settings, CancellationToken token = default)
    {
        var crawler = new SiteCrawler(settings, TestSite.Fetcher(harness));
        var files = new ConcurrentQueue<GrabbedFile>();
        var pages = new ConcurrentQueue<CrawledPage>();
        crawler.FileFound += (_, f) => files.Enqueue(f);
        crawler.PageExplored += (_, p) => pages.Enqueue(p);
        await crawler.RunAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
        return (crawler, [.. files], [.. pages]);
    }

    private static string[] Paths(IEnumerable<GrabbedFile> files) => [.. files.Select(f => f.Url.AbsolutePath).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Images_are_collected_to_the_depth_on_the_site_obeying_robots()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (crawler, files, pages) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path));

        Assert.Equal(["/b/3.png", "/img/1-2x.png", "/img/1.png", "/img/2.png", "/img/4.png", "/img/bg.png"], Paths(files));
        Assert.All(files, f => Assert.True(f.Matches));
        Assert.Equal("png", files[0].Extension);
        Assert.Equal(site.Start, files.Single(f => f.Url.AbsolutePath == "/img/1.png").PageUrl);
        Assert.Equal(4, crawler.PagesExplored); // index, a, b/, c
        Assert.Contains(pages, p => p.Url.AbsolutePath == "/private/p.html" && p.Error == "Blocked by robots.txt");
        Assert.DoesNotContain(harness.Server.Requests, r => r.Path is "/private/p.html" or "/d.html" or "/ext.html");
    }

    [Fact]
    public async Task Depth_zero_is_the_start_page_only()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (crawler, files, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { Depth = 0 });

        Assert.Equal(["/img/1-2x.png", "/img/1.png", "/img/bg.png"], Paths(files));
        Assert.Equal(1, crawler.PagesExplored);
    }

    [Fact]
    public async Task Links_that_turn_out_to_be_files_are_recognized()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (_, files, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.FileTypes, temp.Path) with { FileTypes = "pdf zip", Depth = 1 });

        Assert.Equal(["/download", "/files/doc.pdf"], Paths(files));
        var zip = files.Single(f => f.Url.AbsolutePath == "/download");
        Assert.Equal(("zip", 30_000L), (zip.Extension, zip.Size));
    }

    [Fact]
    public async Task Listing_every_file_marks_the_matching_ones()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (_, files, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { Depth = 0, OnlyMatching = false });

        Assert.Contains(files, f => f.Url.AbsolutePath == "/files/doc.pdf" && !f.Matches);
        Assert.Contains(files, f => f.Url.AbsolutePath == "/img/1.png" && f.Matches);
    }

    [Fact]
    public async Task Include_and_exclude_filters_narrow_the_exploration()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (_, excluded, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { ExcludeFilters = ["*/b/*", "*/img/2.png"] });
        var (_, included, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { IncludeFilters = ["*/a.html"] });

        Assert.Equal(["/img/1-2x.png", "/img/1.png", "/img/4.png", "/img/bg.png"], Paths(excluded));
        Assert.Equal(["/img/1-2x.png", "/img/1.png", "/img/2.png", "/img/bg.png"], Paths(included));
    }

    [Fact]
    public async Task External_links_are_followed_only_when_asked()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (_, followed, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { Depth = 1, FollowExternalLinks = true, ExternalDepth = 1 });
        var (_, anywhere, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { Depth = 1, StayOnSite = false });

        Assert.Contains("/img/ext.png", Paths(followed));
        Assert.Contains("/img/ext.png", Paths(anywhere));
    }

    [Fact]
    public async Task Robots_can_be_ignored_and_pages_limited()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (_, files, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { Depth = 1, ObeyRobots = false });
        var (limited, _, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { MaxPages = 2 });

        Assert.Contains("/img/secret.png", Paths(files));
        Assert.Equal(2, limited.PagesExplored);
    }

    [Fact]
    public async Task Requests_are_spaced_by_the_delay()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);
        var watch = Stopwatch.StartNew();

        var (crawler, _, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.Images, temp.Path) with { Depth = 1, DelayMs = 150, MaxParallel = 4 });

        // index, a, b/, the style sheet and /download each wait for their turn.
        Assert.True(watch.ElapsedMilliseconds >= 4 * 150 - 50, $"took {watch.ElapsedMilliseconds} ms");
        var pageTimes = harness.Server.Requests.Where(r => r.Path is "/index.html" or "/a.html" or "/b/").Select(r => r.Time).Order().ToList();
        Assert.All(pageTimes.Zip(pageTimes.Skip(1)), pair => Assert.True((pair.Second - pair.First).TotalMilliseconds >= 120));
        Assert.Equal(3, crawler.PagesExplored);
    }

    [Fact]
    public async Task Pause_holds_the_crawl_and_stop_ends_it()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);
        var crawler = new SiteCrawler(site.Settings(GrabberTemplate.Images, temp.Path), TestSite.Fetcher(harness));

        crawler.Pause();
        var run = crawler.RunAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(0, crawler.PagesExplored);
        Assert.True(crawler.IsPaused);
        crawler.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(4, crawler.PagesExplored);

        using var stop = new CancellationTokenSource();
        var stopped = new SiteCrawler(site.Settings(GrabberTemplate.Images, temp.Path) with { DelayMs = 5000 }, TestSite.Fetcher(harness));
        var slow = stopped.RunAsync(stop.Token);
        await Task.Delay(200);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(stopped.PagesExplored <= 1);
    }

    [Fact]
    public async Task Offline_copies_link_to_each_other()
    {
        await using var harness = await EngineHarness.CreateAsync();
        using var temp = new TempDirectory();
        var site = new TestSite(harness.Server);

        var (crawler, files, _) = await CrawlAsync(harness, site.Settings(GrabberTemplate.OfflineSite, temp.Path) with { Depth = 1 });
        crawler.RewriteSavedPages(files.Select(f => f.Url));

        var hostFolder = Path.Combine(temp.Path, $"127.0.0.1_{harness.Server.BaseUri.Port}");
        var index = File.ReadAllText(Path.Combine(hostFolder, "index.html"));
        Assert.Contains("href=\"a.html\"", index, StringComparison.Ordinal);
        Assert.Contains("href=\"b/index.html\"", index, StringComparison.Ordinal);
        Assert.Contains("src=\"img/1.png\"", index, StringComparison.Ordinal);
        Assert.Contains("href=\"style.css\"", index, StringComparison.Ordinal);
        Assert.Contains("href=\"files/doc.pdf\"", index, StringComparison.Ordinal);
        Assert.Contains("/private/p.html\"", index, StringComparison.Ordinal); // blocked by robots: still the web address
        Assert.Contains("http://localhost:", index, StringComparison.Ordinal);
        var b = File.ReadAllText(Path.Combine(hostFolder, "b", "index.html"));
        Assert.Contains("src=\"3.png\"", b, StringComparison.Ordinal);
        Assert.Contains("/style.css", Paths(files));
        Assert.Contains("/img/bg.png", Paths(files));
        Assert.Equal(Path.Combine(hostFolder, "img", "1.png"), OfflinePaths.LocalPath(temp.Path, site.Server.UrlFor("img/1.png"), isPage: false));
    }
}
