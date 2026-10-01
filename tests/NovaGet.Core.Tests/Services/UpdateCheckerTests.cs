using NovaGet.Core.Engine.Http;
using NovaGet.Core.Services;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Services;

public sealed class UpdateCheckerTests
{
    private const string Release = """
        {"tag_name":"v1.4.2","html_url":"https://github.com/Zsl8/NovaGet/releases/tag/v1.4.2","draft":false,"prerelease":false,"name":"NovaGet 1.4.2"}
        """;

    [Fact]
    public void Releases_are_read_from_the_feed()
    {
        var update = UpdateChecker.Parse(Release)!;

        Assert.Equal(new Version(1, 4, 2, 0), update.Version);
        Assert.Equal("https://github.com/Zsl8/NovaGet/releases/tag/v1.4.2", update.PageUrl.AbsoluteUri);
        Assert.True(UpdateChecker.IsNewer(update, new Version(1, 4, 1)));
        Assert.False(UpdateChecker.IsNewer(update, new Version(1, 4, 2)));
        Assert.False(UpdateChecker.IsNewer(update, new Version(2, 0)));
        Assert.Equal("1.4.2", UpdateChecker.Display(update.Version));
    }

    [Theory]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"https://x/r","draft":true}""")]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"https://x/r","prerelease":true}""")]
    [InlineData("""{"tag_name":"nightly","html_url":"https://x/r"}""")]
    [InlineData("""{"tag_name":"v2.0.0","html_url":"http://x/r"}""")]
    [InlineData("""{"tag_name":"v2.0.0"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Unusable_releases_are_ignored(string json) => Assert.Null(UpdateChecker.Parse(json));

    [Fact]
    public void Checks_are_weekly()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(UpdateChecker.IsDue(null, now));
        Assert.False(UpdateChecker.IsDue(now.AddDays(-6), now));
        Assert.True(UpdateChecker.IsDue(now.AddDays(-7), now));
        Assert.True(UpdateChecker.IsDue(now.AddDays(3), now)); // a clock that went backwards
    }

    [Fact]
    public async Task The_feed_is_fetched_over_http()
    {
        await using var server = await TestHttpServer.StartAsync();
        server.AddText("releases/latest", Release, "application/json");
        using var clients = new HttpClientProvider();
        var feed = server.UrlFor("releases/latest");
        var client = clients.GetClient(new NovaGet.Core.Engine.RequestContext { Url = feed });

        var update = await UpdateChecker.FetchAsync(client, feed, CancellationToken.None);
        var none = await UpdateChecker.FetchAsync(client, server.UrlFor("missing"), CancellationToken.None);

        Assert.Equal("v1.4.2", update!.Tag);
        Assert.Null(none);
        var request = Assert.Single(server.Requests, r => r.Path == "/releases/latest");
        Assert.StartsWith("NovaGet/", request.Headers["User-Agent"], StringComparison.Ordinal);
    }
}
