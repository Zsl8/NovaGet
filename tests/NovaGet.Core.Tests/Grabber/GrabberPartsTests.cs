using NovaGet.Core.Grabber;

namespace NovaGet.Core.Tests.Grabber;

public sealed class GrabberPartsTests
{
    private static readonly Uri Page = new("https://www.example.com/blog/post.html");

    [Fact]
    public void Html_links_are_found_by_kind()
    {
        const string html = """
            <html><head>
              <link rel="stylesheet" href="/css/site.css">
              <link rel="icon" href="favicon.ico">
              <meta http-equiv="refresh" content="5; url=/next.html">
              <style>body { background: url('img/bg.png') } @import "more.css";</style>
            </head><body>
              <a href="other.html#part">Other</a>
              <a href="/files/report.pdf">Report</a>
              <a href="javascript:void(0)">No</a> <a href="mailto:a@b.c">Mail</a> <a href="#top">Top</a>
              <img src="a.jpg" srcset="a-480.jpg 480w, a-800.jpg 800w" alt="">
              <img data-src="lazy.webp">
              <picture><source srcset="pic.avif 1x, pic@2x.avif 2x"></picture>
              <video src="clip.mp4" poster="poster.jpg"><track src="subs.vtt"></video>
              <div style="background-image:url(&quot;div.png&quot;)"></div>
              <iframe src="https://player.example.net/embed/1"></iframe>
              <a href="ftp://files.example.com/pub/x.zip">FTP</a>
            </body></html>
            """;

        var links = LinkScanner.ScanHtml(html, Page);
        string[] Of(LinkKind kind) => [.. links.Where(l => l.Kind == kind).Select(l => l.Url.AbsoluteUri)];

        Assert.Equal(
            ["https://www.example.com/blog/other.html", "https://www.example.com/files/report.pdf", "ftp://files.example.com/pub/x.zip",
             "https://player.example.net/embed/1", "https://www.example.com/next.html"],
            Of(LinkKind.Page));
        Assert.Equal(["https://www.example.com/css/site.css"], Of(LinkKind.Stylesheet));
        var resources = Of(LinkKind.Resource);
        foreach (var name in new[] { "a.jpg", "a-480.jpg", "a-800.jpg", "lazy.webp", "pic.avif", "pic@2x.avif", "clip.mp4", "poster.jpg", "subs.vtt", "div.png", "img/bg.png", "more.css" })
        {
            Assert.Contains("https://www.example.com/blog/" + name, resources);
        }

        Assert.Contains("https://www.example.com/blog/favicon.ico", resources);
    }

    [Fact]
    public void Base_href_changes_where_links_point()
    {
        var links = LinkScanner.ScanHtml("""<base href="https://cdn.example.com/static/"><img src="x.png"><a href="page.html">p</a>""", Page);

        Assert.Equal(["https://cdn.example.com/static/page.html", "https://cdn.example.com/static/x.png"], links.Select(l => l.Url.AbsoluteUri).Order());
    }

    [Fact]
    public void Css_urls_and_imports()
    {
        var urls = LinkScanner.ScanCss("@import 'base.css'; .a { background: url(../img/a.png) } .b { src: url(\"/fonts/f.woff2\") format('woff2') } .c { background: url(data:image/png;base64,AAAA) }",
            new Uri("https://example.com/css/site.css"));

        Assert.Equal(["https://example.com/css/base.css", "https://example.com/fonts/f.woff2", "https://example.com/img/a.png"], urls.Select(u => u.AbsoluteUri).Order());
    }

    [Fact]
    public void Pages_are_rewritten_to_local_links()
    {
        const string html = """
            <html><head><meta charset="windows-1256"><base href="https://example.com/"></head>
            <body><a href="about.html#team">About</a> <a href="https://elsewhere.example/x">Away</a>
            <img src="img/a.png" srcset="img/a.png 1x, img/b.png 2x"><p style="background:url(img/a.png)"></p></body></html>
            """;
        var local = new Dictionary<string, string>
        {
            ["https://example.com/about.html"] = "about.html",
            ["https://example.com/img/a.png"] = "img/a.png",
        };

        var rewritten = LinkScanner.RewriteHtml(html, new Uri("https://example.com/index.html"), u => local.GetValueOrDefault(u.AbsoluteUri));

        Assert.Contains("href=\"about.html#team\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("href=\"https://elsewhere.example/x\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("srcset=\"img/a.png 1x, img/b.png 2x\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("url(&quot;img/a.png&quot;)", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("<base", rewritten, StringComparison.Ordinal);
        Assert.Contains("charset=\"utf-8\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Robots_rules_follow_the_longest_match()
    {
        var robots = RobotsRules.Parse("""
            User-agent: SomeBot
            Disallow: /

            User-agent: *
            Disallow: /private/
            Allow: /private/public/
            Disallow: /*.pdf$
            Crawl-delay: 2
            """);

        Assert.False(robots.IsAllowed(new Uri("https://e.com/private/x.html")));
        Assert.True(robots.IsAllowed(new Uri("https://e.com/private/public/x.html")));
        Assert.False(robots.IsAllowed(new Uri("https://e.com/docs/a.pdf")));
        Assert.True(robots.IsAllowed(new Uri("https://e.com/docs/a.pdf?x=1")));
        Assert.True(robots.IsAllowed(new Uri("https://e.com/index.html")));
        Assert.Equal(TimeSpan.FromSeconds(2), robots.CrawlDelay);
        Assert.False(RobotsRules.Parse("User-agent: NovaGet\nDisallow: /\n\nUser-agent: *\nAllow: /").IsAllowed(new Uri("https://e.com/a")));
        Assert.True(RobotsRules.Parse("").IsAllowed(new Uri("https://e.com/a")));
    }

    [Fact]
    public void Offline_paths_mirror_the_site()
    {
        var root = Path.Combine(Path.GetTempPath(), "site");
        string Local(string url, bool page) => Path.GetRelativePath(root, OfflinePaths.LocalPath(root, new Uri(url), page)).Replace('\\', '/');

        Assert.Equal("example.com/index.html", Local("https://example.com/", true));
        Assert.Equal("example.com/docs/index.html", Local("https://example.com/docs/", true));
        Assert.Equal("example.com/about.html", Local("https://example.com/about", true));
        Assert.Equal("example.com/index.php.html", Local("https://example.com/index.php", true));
        Assert.Matches(@"^example\.com/list_[0-9a-f]{8}\.php\.html$", Local("https://example.com/list.php?page=2", true));
        Assert.Equal("example.com_8080/img/a b.png", Local("http://example.com:8080/img/a%20b.png", false));
        Assert.DoesNotContain("..", Local("https://example.com/a/../../../etc/passwd", false), StringComparison.Ordinal);
        Assert.Equal("../img/a%20b.png", OfflinePaths.RelativeLink(Path.Combine(root, "e", "docs", "x.html"), Path.Combine(root, "e", "img", "a b.png")));
    }

    [Theory]
    [InlineData("*.example.org/*", "https://www.example.com/forum/1", false)]
    [InlineData("*/forum/*", "https://www.example.com/forum/1", true)]
    [InlineData("logout", "https://www.example.com/user/LOGOUT?x", true)]
    [InlineData("*.pdf", "https://e.com/a.PDF", true)]
    [InlineData("*.pdf", "https://e.com/a.pdf?x", false)]
    public void Wildcard_filters(string pattern, string url, bool expected) => Assert.Equal(expected, SiteCrawler.WildcardMatch(pattern, url));

    [Theory]
    [InlineData("https://e.com/files/a.zip", true)]
    [InlineData("https://e.com/page.html", false)]
    [InlineData("https://e.com/index.php?id=3", false)]
    [InlineData("https://e.com/blog/2024.08", false)]
    [InlineData("https://e.com/about", false)]
    [InlineData("https://e.com/v1.2/", false)]
    public void File_links_are_told_from_pages(string url, bool file) => Assert.Equal(file, SiteCrawler.LooksLikeFile(new Uri(url)));

    [Fact]
    public void Type_filter_with_sizes()
    {
        var filter = new FileTypeFilter("jpg png r0*", 10 * 1024, 100 * 1024);

        Assert.True(filter.Matches("jpg", -1));
        Assert.True(filter.Matches("r01", 50 * 1024));
        Assert.False(filter.Matches("jpg", 5 * 1024));
        Assert.False(filter.Matches("png", 200 * 1024));
        Assert.False(filter.Matches("gif", 50 * 1024));
        Assert.True(new FileTypeFilter("*", 0, 0).Matches(string.Empty, 1));
    }

    [Fact]
    public void Settings_round_trip_with_protected_secrets()
    {
        var settings = GrabberSettings.ForTemplate(GrabberTemplate.OfflineSite) with
        {
            StartUrl = "https://example.com/",
            SaveFolder = @"C:\Sites\example",
            UseAuthorization = true,
            UserName = "me",
            Password = "secret",
            Cookies = "sid=1",
            ExcludeFilters = ["*logout*"],
        };

        var stored = settings.Protect(s => s is null ? null : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))).ToJson();
        var loaded = GrabberSettings.FromJson(stored).Unprotect(s => s is null ? null : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s)));

        Assert.DoesNotContain("secret", stored, StringComparison.Ordinal);
        Assert.Equal(settings with { ExcludeFilters = loaded.ExcludeFilters, IncludeFilters = loaded.IncludeFilters }, loaded);
        Assert.Equal(["*logout*"], loaded.ExcludeFilters);
        Assert.True(loaded.ConvertLinks);
        Assert.Equal("*", loaded.FileTypes);
        Assert.Equal(GrabberTemplate.Images, GrabberSettings.FromJson("{broken").Template);
    }
}
